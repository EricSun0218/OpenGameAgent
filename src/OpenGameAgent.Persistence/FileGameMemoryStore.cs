using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameMemoryMigrationResult
{
    internal FileGameMemoryMigrationResult(int migratedEntries, int partitionedEntries)
    {
        MigratedEntries = migratedEntries;
        PartitionedEntries = partitionedEntries;
    }

    public int MigratedEntries { get; }

    public int PartitionedEntries { get; }

    public bool PerformedWork => MigratedEntries > 0;
}

/// <summary>
/// Stores authoritative memories in hashed session/owner partitions. Existing
/// flat v1 files are migrated automatically and crash-safely on first access.
/// </summary>
public sealed class FileGameMemoryStore :
    IGameMemoryStore,
    IGameMemorySnapshotSource,
    IGameMemoryPartitionSnapshotSource,
    IGameMemorySearchSnapshotSource
{
    private const string Suffix = ".memory.json";
    private const string LayoutDirectoryName = ".memory-v2";
    private const string LayoutFileName = "layout.json";
    private const string PendingFileName = "pending-add.json";
    private const int LayoutVersion = 2;
    private readonly FileStore _files;
    private readonly int _maximumEntries;
    private readonly string _layoutDirectory;
    private readonly string _layoutPath;
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private int _layoutReady;

    public FileGameMemoryStore(
        string directory,
        int maximumEntries = 100_000,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumEntries <= 0 || maximumEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _maximumEntries = maximumEntries;
        _layoutDirectory = Path.Combine(_files.DirectoryPath, LayoutDirectoryName);
        _layoutPath = Path.Combine(_layoutDirectory, LayoutFileName);
        _pendingPath = Path.Combine(_layoutDirectory, PendingFileName);
        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
    }

    public async ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken)
    {
        if (memory is null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        await EnsurePartitionedLayoutAsync(cancellationToken).ConfigureAwait(false);
        var storageKey = StorageKey(memory.SessionId, memory.OwnerId, memory.MemoryId);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRegularFileOrMissing(_files.PathFor(storageKey + Suffix, ".lock"));
            using var processLease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken)
                .ConfigureAwait(false);
            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var capacityLease = await _files.AcquireProcessLeaseAsync(
                        "memory-layout-v2-capacity",
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureLayoutPathsAreSafe(createDirectories: true);
                await RecoverPendingAddAsync(cancellationToken).ConfigureAwait(false);
                var path = PartitionPath(memory.SessionId, memory.OwnerId, memory.MemoryId);
                EnsurePartitionDirectories(memory.SessionId, memory.OwnerId);
                var existing = await ReadPartitionedAsync(path, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    EnsureEquivalent(existing, memory);
                    return;
                }

                var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new PersistenceException("The partitioned memory layout metadata is missing.");
                if (layout.EntryCount >= _maximumEntries)
                {
                    throw new GameRuntimeLimitException(nameof(_maximumEntries), "The file memory store reached its capacity.");
                }

                var pending = new PendingAddDocument
                {
                    FormatVersion = 1,
                    ExpectedEntryCount = layout.EntryCount,
                    SessionHash = Hash(memory.SessionId),
                    OwnerHash = Hash(memory.OwnerId),
                    MemoryHash = Hash(memory.MemoryId),
                };
                await _files.WriteAtomicAsync(_pendingPath, pending, cancellationToken).ConfigureAwait(false);
                EnsureRegularFile(_pendingPath);
                await _files.WriteAtomicAsync(path, Encode(memory), cancellationToken).ConfigureAwait(false);
                EnsureRegularFile(path);
                await _files.WriteAtomicAsync(
                        _layoutPath,
                        new LayoutDocument { FormatVersion = LayoutVersion, EntryCount = checked(layout.EntryCount + 1) },
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureRegularFile(_layoutPath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_pendingPath);
            }
            finally
            {
                _capacityGate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GameMemory>> SearchAsync(
        GameMemoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (query.Limit == 0)
        {
            return Array.Empty<GameMemory>();
        }

        var snapshot = await SearchSnapshotAsync(query, _maximumEntries, cancellationToken).ConfigureAwait(false);
        return snapshot.Memories;
    }

    public async ValueTask<GameMemorySearchSnapshot> SearchSnapshotAsync(
        GameMemoryQuery query,
        int maximumSnapshotEntries,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (maximumSnapshotEntries < 1 || maximumSnapshotEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotEntries));
        }

        maximumSnapshotEntries = Math.Min(maximumSnapshotEntries, _maximumEntries);

        var migrationStartedAt = Stopwatch.GetTimestamp();
        var migration = await EnsurePartitionedLayoutAsync(cancellationToken).ConfigureAwait(false);
        var migrationDuration = Elapsed(migrationStartedAt);
        var snapshotStartedAt = Stopwatch.GetTimestamp();
        var authoritative = await LoadPartitionAsync(
                query.SessionId,
                query.OwnerId,
                maximumSnapshotEntries,
                cancellationToken)
            .ConfigureAwait(false);
        var snapshotDuration = Elapsed(snapshotStartedAt);

        var lexicalStartedAt = Stopwatch.GetTimestamp();
        var inMemory = new InMemoryGameMemoryStore(Math.Max(1, maximumSnapshotEntries));
        foreach (var memory in authoritative)
        {
            await inMemory.AppendAsync(memory, cancellationToken).ConfigureAwait(false);
        }

        var memories = await inMemory.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        var stages = new[]
        {
            new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.StorageMigration,
                migrationDuration,
                migration.MigratedEntries,
                migration.PartitionedEntries),
            new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.AuthoritativeSnapshot,
                snapshotDuration,
                authoritative.Count,
                authoritative.Count),
            new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.LexicalSearch,
                Elapsed(lexicalStartedAt),
                authoritative.Count,
                memories.Count),
        };
        return new GameMemorySearchSnapshot(memories, authoritative, stages);
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        await EnsurePartitionedLayoutAsync(cancellationToken).ConfigureAwait(false);
        var memories = await LoadPartitionAsync(sessionId, ownerId: null, _maximumEntries, cancellationToken)
            .ConfigureAwait(false);
        foreach (var memory in memories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return memory;
        }
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        string ownerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ownerId = GameJson.RequireId(ownerId, nameof(ownerId));
        await EnsurePartitionedLayoutAsync(cancellationToken).ConfigureAwait(false);
        var memories = await LoadPartitionAsync(sessionId, ownerId, _maximumEntries, cancellationToken)
            .ConfigureAwait(false);
        foreach (var memory in memories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return memory;
        }
    }

    /// <summary>
    /// Explicitly completes the same crash-safe migration that normal store
    /// access performs lazily. The operation is idempotent across processes.
    /// </summary>
    public ValueTask<FileGameMemoryMigrationResult> MigrateLegacyLayoutAsync(
        CancellationToken cancellationToken = default) =>
        EnsurePartitionedLayoutAsync(cancellationToken, forceCheck: true);

    private async ValueTask<FileGameMemoryMigrationResult> EnsurePartitionedLayoutAsync(
        CancellationToken cancellationToken,
        bool forceCheck = false)
    {
        if (!forceCheck && Volatile.Read(ref _layoutReady) != 0)
        {
            var ready = await ReadStableLayoutAsync(cancellationToken).ConfigureAwait(false);
            return new FileGameMemoryMigrationResult(0, ready.EntryCount);
        }

        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceCheck && Volatile.Read(ref _layoutReady) != 0)
            {
                var ready = await ReadStableLayoutAsync(cancellationToken).ConfigureAwait(false);
                return new FileGameMemoryMigrationResult(0, ready.EntryCount);
            }

            EnsureLayoutPathsAreSafe(createDirectories: true);
            using var migrationLease = await _files.AcquireProcessLeaseAsync(
                    "memory-layout-v2-migration",
                    cancellationToken)
                .ConfigureAwait(false);
            using var capacityLease = await _files.AcquireProcessLeaseAsync(
                    "memory-layout-v2-capacity",
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureLayoutPathsAreSafe(createDirectories: true);
            await RecoverPendingAddAsync(cancellationToken).ConfigureAwait(false);
            var legacyPaths = Directory.EnumerateFiles(
                    _files.DirectoryPath,
                    "*" + Suffix,
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(_maximumEntries + 1)
                .ToArray();
            if (legacyPaths.Length > _maximumEntries)
            {
                throw new GameRuntimeLimitException(nameof(_maximumEntries), "The legacy memory store exceeded its capacity.");
            }

            var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (legacyPaths.Length == 0 && layout is not null)
            {
                Volatile.Write(ref _layoutReady, 1);
                return new FileGameMemoryMigrationResult(0, layout.EntryCount);
            }

            var entryCount = CountPartitionedEntries();
            var migrated = 0;
            foreach (var legacyPath in legacyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRegularFile(legacyPath);
                var document = await _files.ReadAsync<MemoryDocument>(legacyPath, cancellationToken).ConfigureAwait(false)
                    ?? throw new PersistenceException("A legacy memory file disappeared during migration.");
                var memory = Decode(document);
                _files.EnsurePathFor(
                    legacyPath,
                    StorageKey(memory.SessionId, memory.OwnerId, memory.MemoryId),
                    Suffix,
                    "legacy memory");
                EnsurePartitionDirectories(memory.SessionId, memory.OwnerId);
                var target = PartitionPath(memory.SessionId, memory.OwnerId, memory.MemoryId);
                var existing = await ReadPartitionedAsync(target, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    if (entryCount >= _maximumEntries)
                    {
                        throw new GameRuntimeLimitException(nameof(_maximumEntries), "The file memory store reached its capacity during migration.");
                    }

                    // Both paths are inside the same configured store. Renaming
                    // keeps each entry present at exactly one path across a
                    // crash and avoids rewriting/fsyncing immutable legacy data.
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(legacyPath, target);
                    EnsureRegularFile(target);
                    entryCount++;
                    migrated++;
                    continue;
                }
                else
                {
                    EnsureEquivalent(existing, memory);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(legacyPath);
                migrated++;
            }

            await _files.WriteAtomicAsync(
                    _layoutPath,
                    new LayoutDocument { FormatVersion = LayoutVersion, EntryCount = entryCount },
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureRegularFile(_layoutPath);
            Volatile.Write(ref _layoutReady, 1);
            return new FileGameMemoryMigrationResult(migrated, entryCount);
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    private async ValueTask RecoverPendingAddAsync(CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(_pendingPath);
        if (!File.Exists(_pendingPath))
        {
            return;
        }

        var pending = await _files.ReadAsync<PendingAddDocument>(_pendingPath, cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("The pending memory mutation disappeared during recovery.");
        if (pending.FormatVersion != 1
            || pending.ExpectedEntryCount < 0
            || pending.ExpectedEntryCount >= _maximumEntries
            || !IsHash(pending.SessionHash)
            || !IsHash(pending.OwnerHash)
            || !IsHash(pending.MemoryHash))
        {
            throw new PersistenceException("The pending memory mutation is corrupt.");
        }

        var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("The pending memory mutation has no layout metadata.");
        var target = PartitionPath(pending.SessionHash!, pending.OwnerHash!, pending.MemoryHash!, alreadyHashed: true);
        EnsureRegularFileOrMissing(target);
        var targetExists = File.Exists(target);
        if (layout.EntryCount == pending.ExpectedEntryCount)
        {
            if (targetExists)
            {
                await _files.WriteAtomicAsync(
                        _layoutPath,
                        new LayoutDocument
                        {
                            FormatVersion = LayoutVersion,
                            EntryCount = checked(pending.ExpectedEntryCount + 1),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (layout.EntryCount != checked(pending.ExpectedEntryCount + 1) || !targetExists)
        {
            throw new PersistenceException("The pending memory mutation cannot be reconciled safely.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_pendingPath);
    }

    private async ValueTask<LayoutDocument?> ReadLayoutAsync(
        CancellationToken cancellationToken,
        bool atomicSnapshot = false)
    {
        EnsureLayoutPathsAreSafe(createDirectories: false);
        var document = atomicSnapshot
            ? await _files.ReadAtomicSnapshotAsync<LayoutDocument>(_layoutPath, cancellationToken).ConfigureAwait(false)
            : await _files.ReadAsync<LayoutDocument>(_layoutPath, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (document.FormatVersion != LayoutVersion
            || document.EntryCount < 0
            || document.EntryCount > _maximumEntries)
        {
            throw new PersistenceException("The partitioned memory layout metadata is corrupt.");
        }

        return document;
    }

    private async ValueTask<LayoutDocument> ReadStableLayoutAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await ReadLayoutAsync(cancellationToken, atomicSnapshot: true).ConfigureAwait(false);
                if (document is not null)
                {
                    return document;
                }
            }
            catch (IOException exception)
            {
                if (attempt >= 99)
                {
                    throw new PersistenceException(
                        "The partitioned memory layout metadata remained unavailable.",
                        exception);
                }
            }

            if (attempt < 99)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new PersistenceException("The partitioned memory layout metadata is missing or unavailable.");
    }

    private async ValueTask<IReadOnlyList<GameMemory>> LoadPartitionAsync(
        string sessionId,
        string? ownerId,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var memories = new List<GameMemory>();
        foreach (var path in EnumeratePartitionPaths(sessionId, ownerId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (memories.Count >= maximumEntries)
            {
                throw new GameRuntimeLimitException(
                    nameof(maximumEntries),
                    "The authoritative memory partition exceeded the requested snapshot bound.");
            }

            var memory = await ReadPartitionedAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new PersistenceException("A partitioned memory file disappeared during enumeration.");
            if (!string.Equals(memory.SessionId, sessionId, StringComparison.Ordinal)
                || (ownerId is not null && !string.Equals(memory.OwnerId, ownerId, StringComparison.Ordinal)))
            {
                throw new PersistenceException("A memory file escaped its requested identity partition.");
            }

            memories.Add(memory);
        }

        return memories
            .OrderBy(memory => memory.OwnerId, StringComparer.Ordinal)
            .ThenBy(memory => memory.MemoryId, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<string> EnumeratePartitionPaths(string sessionId, string? ownerId)
    {
        var sessionDirectory = SessionDirectory(sessionId);
        EnsureDirectoryOrMissing(sessionDirectory);
        if (!Directory.Exists(sessionDirectory))
        {
            return Array.Empty<string>();
        }

        if (ownerId is not null)
        {
            var ownerDirectory = OwnerDirectory(sessionId, ownerId);
            EnsureDirectoryOrMissing(ownerDirectory);
            return !Directory.Exists(ownerDirectory)
                ? Array.Empty<string>()
                : EnumerateOwnerPaths(ownerDirectory).ToArray();
        }

        var paths = new List<string>();
        foreach (var ownerDirectory in Directory.EnumerateDirectories(sessionDirectory, "o-*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            EnsureDirectory(ownerDirectory);
            paths.AddRange(EnumerateOwnerPaths(ownerDirectory));
            if (paths.Count > _maximumEntries)
            {
                throw new GameRuntimeLimitException(nameof(_maximumEntries), "The session memory partition exceeded its capacity.");
            }
        }

        return paths;
    }

    private IEnumerable<string> EnumerateOwnerPaths(string ownerDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(ownerDirectory, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            EnsureRegularFile(path);
            yield return path;
        }
    }

    private async ValueTask<GameMemory?> ReadPartitionedAsync(string path, CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(path);
        var document = await _files.ReadAsync<MemoryDocument>(path, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var memory = Decode(document);
        var expected = PartitionPath(memory.SessionId, memory.OwnerId, memory.MemoryId);
        if (!PathEquals(path, expected))
        {
            throw new PersistenceException("The partitioned memory identity does not match its storage path.");
        }

        return memory;
    }

    private int CountPartitionedEntries()
    {
        if (!Directory.Exists(_layoutDirectory))
        {
            return 0;
        }

        var count = 0;
        foreach (var sessionDirectory in Directory.EnumerateDirectories(_layoutDirectory, "s-*", SearchOption.TopDirectoryOnly))
        {
            EnsureDirectory(sessionDirectory);
            foreach (var ownerDirectory in Directory.EnumerateDirectories(sessionDirectory, "o-*", SearchOption.TopDirectoryOnly))
            {
                EnsureDirectory(ownerDirectory);
                foreach (var path in EnumerateOwnerPaths(ownerDirectory))
                {
                    _ = path;
                    if (++count > _maximumEntries)
                    {
                        throw new GameRuntimeLimitException(nameof(_maximumEntries), "The partitioned memory store exceeded its capacity.");
                    }
                }
            }
        }

        return count;
    }

    private void EnsureLayoutPathsAreSafe(bool createDirectories)
    {
        EnsureDirectoryChainIsSafe(_files.DirectoryPath);
        if (createDirectories)
        {
            Directory.CreateDirectory(_layoutDirectory);
        }

        EnsureDirectoryOrMissing(_layoutDirectory);
        EnsureRegularFileOrMissing(_layoutPath);
        EnsureRegularFileOrMissing(_pendingPath);
    }

    private void EnsurePartitionDirectories(string sessionId, string ownerId)
    {
        EnsureLayoutPathsAreSafe(createDirectories: true);
        var sessionDirectory = SessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDirectory);
        EnsureDirectory(sessionDirectory);
        var ownerDirectory = OwnerDirectory(sessionId, ownerId);
        Directory.CreateDirectory(ownerDirectory);
        EnsureDirectory(ownerDirectory);
    }

    private string SessionDirectory(string sessionId) =>
        Path.Combine(_layoutDirectory, "s-" + Hash(sessionId));

    private string OwnerDirectory(string sessionId, string ownerId) =>
        Path.Combine(SessionDirectory(sessionId), "o-" + Hash(ownerId));

    private string PartitionPath(string sessionId, string ownerId, string memoryId) =>
        Path.Combine(OwnerDirectory(sessionId, ownerId), Hash(memoryId) + Suffix);

    private string PartitionPath(string sessionHash, string ownerHash, string memoryHash, bool alreadyHashed)
    {
        _ = alreadyHashed;
        return Path.Combine(
            _layoutDirectory,
            "s-" + sessionHash,
            "o-" + ownerHash,
            memoryHash + Suffix);
    }

    private static MemoryDocument Encode(GameMemory memory) => new()
    {
        FormatVersion = 1,
        MemoryId = memory.MemoryId,
        SessionId = memory.SessionId,
        OwnerId = memory.OwnerId,
        Scope = memory.Scope,
        Kind = memory.Kind.ToString(),
        PayloadJson = memory.PayloadJson,
        Moment = MomentDocument.Encode(memory.Moment),
        Importance = memory.Importance,
        SearchableText = memory.SearchableText,
        Tags = memory.Tags.ToList(),
        SourceInputId = memory.SourceInputId,
        ExpiresAt = memory.ExpiresAt is null ? null : MomentDocument.Encode(memory.ExpiresAt.Value),
        Metadata = new Dictionary<string, string>(memory.Metadata, StringComparer.Ordinal),
    };

    private static string StorageKey(string sessionId, string ownerId, string memoryId) =>
        sessionId + "\n" + ownerId + "\n" + memoryId;

    private static string Hash(string value)
    {
        using var algorithm = SHA256.Create();
        return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2")));
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GameMemory Decode(MemoryDocument document)
    {
        if (document.FormatVersion != 1
            || !Enum.TryParse<GameMemoryKind>(document.Kind, out var kind)
            || !Enum.IsDefined(typeof(GameMemoryKind), kind))
        {
            throw new PersistenceException("The memory document has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "memory document",
            () => new GameMemory(
                document.MemoryId,
                document.SessionId,
                document.OwnerId,
                document.Scope,
                kind,
                document.PayloadJson,
                document.Moment?.Decode() ?? throw new PersistenceException("The memory game moment is missing."),
                document.Importance,
                document.SearchableText,
                document.Tags,
                document.SourceInputId,
                document.ExpiresAt?.Decode(),
                document.Metadata));
    }

    private static void EnsureEquivalent(GameMemory left, GameMemory right)
    {
        if (!string.Equals(left.MemoryId, right.MemoryId, StringComparison.Ordinal)
            || !string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || !string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
            || !string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
            || left.Kind != right.Kind
            || !string.Equals(left.PayloadJson, right.PayloadJson, StringComparison.Ordinal)
            || left.Moment != right.Moment
            || !left.Importance.Equals(right.Importance)
            || !string.Equals(left.SearchableText, right.SearchableText, StringComparison.Ordinal)
            || !left.Tags.SequenceEqual(right.Tags)
            || !string.Equals(left.SourceInputId, right.SourceInputId, StringComparison.Ordinal)
            || left.ExpiresAt != right.ExpiresAt
            || !left.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(right.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("A memory ID cannot be reused for different content.");
        }
    }

    private static void EnsureDirectoryChainIsSafe(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Memory storage cannot use symbolic links or reparse points.");
            }

            current = current.Parent;
        }
    }

    private static void EnsureDirectoryOrMissing(string path)
    {
        if (Directory.Exists(path))
        {
            EnsureDirectory(path);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PersistenceException("Memory storage expected a regular directory.");
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Memory storage expected a regular file or a missing path.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PersistenceException("Memory storage expected a regular file.");
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new PersistenceException("A memory storage file disappeared during an operation.", exception);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static TimeSpan Elapsed(long startedAt)
    {
        var ticks = checked(Stopwatch.GetTimestamp() - startedAt);
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    private sealed class LayoutDocument
    {
        public int FormatVersion { get; set; }

        public int EntryCount { get; set; }
    }

    private sealed class PendingAddDocument
    {
        public int FormatVersion { get; set; }

        public int ExpectedEntryCount { get; set; }

        public string? SessionHash { get; set; }

        public string? OwnerHash { get; set; }

        public string? MemoryHash { get; set; }
    }

    private sealed class MemoryDocument
    {
        public int FormatVersion { get; set; }

        public string MemoryId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string OwnerId { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string PayloadJson { get; set; } = "{}";

        public MomentDocument? Moment { get; set; }

        public double Importance { get; set; }

        public string? SearchableText { get; set; }

        public List<string>? Tags { get; set; }

        public string? SourceInputId { get; set; }

        public MomentDocument? ExpiresAt { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }
    }
}
