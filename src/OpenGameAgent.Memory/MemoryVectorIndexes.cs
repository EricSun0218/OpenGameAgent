using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Memory;

public sealed class InMemoryVectorMemoryIndex : IVectorMemoryIndex, IVectorMemoryPartitionIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SessionId, string OwnerId, string MemoryId), VectorMemoryIndexEntry> _entries = new();
    private readonly int _capacity;

    public InMemoryVectorMemoryIndex(int capacity = 100_000)
    {
        if (capacity < 1 || capacity > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask UpsertAsync(VectorMemoryIndexEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var key = (entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                MemoryVectorIndexCodec.EnsureSameMemory(existing.Memory, entry.Memory);
                _entries[key] = entry;
                return default;
            }

            if (_entries.Count >= _capacity)
            {
                throw new InvalidOperationException("The vector memory index reached its configured capacity.");
            }

            _entries.Add(key, entry);
        }

        return default;
    }

    public ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        ValidateMaximumEntries(maximumEntries);
        VectorMemoryIndexEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values
                .Where(entry => string.Equals(entry.Memory.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(entry => entry.Memory.OwnerId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Memory.MemoryId, StringComparer.Ordinal)
                .Take(maximumEntries + 1)
                .ToArray();
        }

        if (snapshot.Length > maximumEntries)
        {
            throw new InvalidOperationException("The vector memory index exceeded the requested snapshot bound.");
        }

        return new ValueTask<IReadOnlyList<VectorMemoryIndexEntry>>(Array.AsReadOnly(snapshot));
    }

    public ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        string ownerId,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        ownerId = MemoryVectorGuard.Id(ownerId, nameof(ownerId), 1_024);
        ValidateMaximumEntries(maximumEntries);
        VectorMemoryIndexEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values
                .Where(entry => string.Equals(entry.Memory.SessionId, sessionId, StringComparison.Ordinal)
                                && string.Equals(entry.Memory.OwnerId, ownerId, StringComparison.Ordinal))
                .OrderBy(entry => entry.Memory.MemoryId, StringComparer.Ordinal)
                .Take(maximumEntries + 1)
                .ToArray();
        }

        if (snapshot.Length > maximumEntries)
        {
            throw new InvalidOperationException("The vector memory index exceeded the requested snapshot bound.");
        }

        return new ValueTask<IReadOnlyList<VectorMemoryIndexEntry>>(Array.AsReadOnly(snapshot));
    }

    public ValueTask DeleteAsync(
        string sessionId,
        string ownerId,
        string memoryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (
            MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024),
            MemoryVectorGuard.Id(ownerId, nameof(ownerId), 1_024),
            MemoryVectorGuard.Id(memoryId, nameof(memoryId), 1_024));
        lock (_gate)
        {
            _entries.Remove(key);
        }

        return default;
    }

    private static void ValidateMaximumEntries(int maximumEntries)
    {
        if (maximumEntries < 1 || maximumEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
    }
}

/// <summary>
/// Stores one derived vector record per memory. The files contain no provider
/// credentials and remain separated from the game's authoritative save data.
/// A missing or stale file is recoverable through an explicit rebuild.
/// </summary>
public sealed class FileVectorMemoryIndex : IVectorMemoryIndex, IVectorMemoryPartitionIndex
{
    private const string Suffix = ".vector-memory.json";
    private const string PartitionDirectoryName = ".vector-partitions-v2";
    private const string MarkerSuffix = ".entry";
    private readonly string _directory;
    private readonly string _partitionDirectory;
    private readonly string _layoutPath;
    private readonly string _pendingPath;
    private readonly int _capacity;
    private readonly long _maximumFileBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _capacityGate = new(1, 1);
    private readonly SemaphoreSlim _layoutGate = new(1, 1);
    private int _layoutReady;

    public FileVectorMemoryIndex(
        string directory,
        int capacity = 100_000,
        long maximumFileBytes = 4_000_000)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A vector index directory is required.", nameof(directory));
        }

        if (capacity < 1 || capacity > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumFileBytes < 1_024 || maximumFileBytes > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        EnsureDirectoryChainIsSafe(_directory);
        _partitionDirectory = Path.Combine(_directory, PartitionDirectoryName);
        _layoutPath = Path.Combine(_partitionDirectory, "layout.json");
        _pendingPath = Path.Combine(_partitionDirectory, "pending.json");
        _capacity = capacity;
        _maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask UpsertAsync(VectorMemoryIndexEntry entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        await EnsurePartitionIndexAsync(cancellationToken).ConfigureAwait(false);
        var key = StorageKey(entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId);
        var path = Path.Combine(_directory, key + Suffix);
        var marker = MarkerPath(entry.Memory.SessionId, entry.Memory.OwnerId, key);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
                await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
                var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The vector partition metadata is missing.");
                if (File.Exists(path))
                {
                    EnsureRegularFile(path);
                    var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    MemoryVectorIndexCodec.EnsureSameMemory(existing.Memory, entry.Memory);
                    EnsureRegularFileOrMissing(marker);
                    if (File.Exists(marker))
                    {
                        await WriteAtomicAsync(path, entry, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    throw new InvalidDataException("The vector memory index and its partition marker disagree.");
                }

                if (layout.EntryCount >= _capacity)
                {
                    throw new InvalidOperationException("The vector memory index reached its configured capacity.");
                }

                EnsureMarkerDirectories(entry.Memory.SessionId, entry.Memory.OwnerId);
                await WriteDocumentAtomicAsync(
                        _pendingPath,
                        new PendingDocument
                        {
                            FormatVersion = 1,
                            Action = "add",
                            ExpectedEntryCount = layout.EntryCount,
                            SessionHash = Hash(entry.Memory.SessionId),
                            OwnerHash = Hash(entry.Memory.OwnerId),
                            EntryHash = key,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteAtomicAsync(path, entry, cancellationToken).ConfigureAwait(false);
                CreateMarker(marker);
                await WriteDocumentAtomicAsync(
                        _layoutPath,
                        new PartitionLayoutDocument
                        {
                            FormatVersion = 2,
                            EntryCount = checked(layout.EntryCount + 1),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
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

    public async ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        int maximumEntries,
        CancellationToken cancellationToken)
        => await ListPartitionAsync(sessionId, ownerId: null, maximumEntries, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListAsync(
        string sessionId,
        string ownerId,
        int maximumEntries,
        CancellationToken cancellationToken)
        => await ListPartitionAsync(sessionId, ownerId, maximumEntries, cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<VectorMemoryIndexEntry>> ListPartitionAsync(
        string sessionId,
        string? ownerId,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        ownerId = ownerId is null ? null : MemoryVectorGuard.Id(ownerId, nameof(ownerId), 1_024);
        if (maximumEntries < 1 || maximumEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        await EnsurePartitionIndexAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<VectorMemoryIndexEntry>();
        var scanned = 0;
        foreach (var marker in EnumerateMarkers(sessionId, ownerId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > _capacity)
            {
                throw new InvalidOperationException("The vector memory index exceeded its configured capacity.");
            }

            EnsureRegularFile(marker);
            var name = Path.GetFileNameWithoutExtension(marker);
            if (!IsHash(name))
            {
                throw new InvalidDataException("A vector partition marker is malformed.");
            }

            var path = Path.Combine(_directory, name + Suffix);
            EnsureRegularFile(path);
            var entry = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var expectedKey = StorageKey(entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId);
            if (!string.Equals(name, expectedKey, StringComparison.Ordinal)
                || !string.Equals(entry.Memory.SessionId, sessionId, StringComparison.Ordinal)
                || (ownerId is not null && !string.Equals(entry.Memory.OwnerId, ownerId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("A vector partition marker has an invalid identity path.");
            }

            if (entries.Count >= maximumEntries)
            {
                throw new InvalidOperationException("The vector memory index exceeded the requested snapshot bound.");
            }

            entries.Add(entry);
        }

        return new ReadOnlyCollection<VectorMemoryIndexEntry>(entries);
    }

    public async ValueTask DeleteAsync(
        string sessionId,
        string ownerId,
        string memoryId,
        CancellationToken cancellationToken)
    {
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        ownerId = MemoryVectorGuard.Id(ownerId, nameof(ownerId), 1_024);
        memoryId = MemoryVectorGuard.Id(memoryId, nameof(memoryId), 1_024);
        await EnsurePartitionIndexAsync(cancellationToken).ConfigureAwait(false);
        var key = StorageKey(sessionId, ownerId, memoryId);
        var path = Path.Combine(_directory, key + Suffix);
        var marker = MarkerPath(sessionId, ownerId, key);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
            var pathExists = File.Exists(path);
            var markerExists = File.Exists(marker);
            if (!pathExists && !markerExists)
            {
                return;
            }

            if (!pathExists || !markerExists)
            {
                throw new InvalidDataException("The vector memory index and its partition marker disagree.");
            }

            EnsureRegularFile(path);
            EnsureRegularFile(marker);
            var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.Memory.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(existing.Memory.OwnerId, ownerId, StringComparison.Ordinal)
                || !string.Equals(existing.Memory.MemoryId, memoryId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A vector memory index file has an invalid identity path.");
            }

            var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The vector partition metadata is missing.");
            if (layout.EntryCount <= 0)
            {
                throw new InvalidDataException("The vector partition count is inconsistent.");
            }

            await WriteDocumentAtomicAsync(
                    _pendingPath,
                    new PendingDocument
                    {
                        FormatVersion = 1,
                        Action = "delete",
                        ExpectedEntryCount = layout.EntryCount,
                        SessionHash = Hash(sessionId),
                        OwnerHash = Hash(ownerId),
                        EntryHash = key,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            File.Delete(path);
            File.Delete(marker);
            await WriteDocumentAtomicAsync(
                    _layoutPath,
                    new PartitionLayoutDocument
                    {
                        FormatVersion = 2,
                        EntryCount = checked(layout.EntryCount - 1),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(_pendingPath);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Explicitly builds the bounded partition markers used by ListAsync. The
    /// same idempotent migration runs automatically on first access.
    /// </summary>
    public ValueTask<int> MigrateLegacyIndexAsync(CancellationToken cancellationToken = default) =>
        EnsurePartitionIndexAsync(cancellationToken, forceCheck: true);

    private async ValueTask<int> EnsurePartitionIndexAsync(
        CancellationToken cancellationToken,
        bool forceCheck = false)
    {
        if (!forceCheck && Volatile.Read(ref _layoutReady) != 0)
        {
            var ready = await ReadStableLayoutAsync(cancellationToken).ConfigureAwait(false);
            return ready.EntryCount;
        }

        await _layoutGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceCheck && Volatile.Read(ref _layoutReady) != 0)
            {
                var ready = await ReadStableLayoutAsync(cancellationToken).ConfigureAwait(false);
                return ready.EntryCount;
            }

            EnsurePartitionPaths(createDirectory: true);
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
            var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (layout is not null && !forceCheck)
            {
                Volatile.Write(ref _layoutReady, 1);
                return layout.EntryCount;
            }

            var paths = Directory.EnumerateFiles(_directory, "*" + Suffix, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(_capacity + 1)
                .ToArray();
            if (paths.Length > _capacity)
            {
                throw new InvalidOperationException("The vector memory index exceeded its configured capacity.");
            }

            var count = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRegularFile(path);
                var entry = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                var key = StorageKey(entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId);
                var expected = Path.Combine(_directory, key + Suffix);
                if (!PathEquals(path, expected))
                {
                    throw new InvalidDataException("A vector memory index file has an invalid identity path.");
                }

                EnsureMarkerDirectories(entry.Memory.SessionId, entry.Memory.OwnerId);
                var marker = MarkerPath(entry.Memory.SessionId, entry.Memory.OwnerId, key);
                if (!File.Exists(marker))
                {
                    CreateMarker(marker);
                }
                else
                {
                    EnsureRegularFile(marker);
                }

                count++;
            }

            ValidateAllMarkers(paths
                .Select(path => Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - Suffix.Length))
                .ToHashSet(StringComparer.Ordinal));

            await WriteDocumentAtomicAsync(
                    _layoutPath,
                    new PartitionLayoutDocument { FormatVersion = 2, EntryCount = count },
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _layoutReady, 1);
            return count;
        }
        finally
        {
            _layoutGate.Release();
        }
    }

    private async ValueTask RecoverPendingAsync(CancellationToken cancellationToken)
    {
        EnsureRegularFileOrMissing(_pendingPath);
        if (!File.Exists(_pendingPath))
        {
            return;
        }

        var pending = await ReadDocumentAsync<PendingDocument>(_pendingPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending vector mutation disappeared during recovery.");
        if (pending.FormatVersion != 1
            || pending.ExpectedEntryCount < 0
            || pending.ExpectedEntryCount > _capacity
            || pending.Action is not ("add" or "delete")
            || !IsHash(pending.SessionHash)
            || !IsHash(pending.OwnerHash)
            || !IsHash(pending.EntryHash))
        {
            throw new InvalidDataException("The pending vector mutation is corrupt.");
        }

        var layout = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending vector mutation has no partition metadata.");
        var path = Path.Combine(_directory, pending.EntryHash + Suffix);
        var marker = MarkerPath(pending.SessionHash!, pending.OwnerHash!, pending.EntryHash!, alreadyHashed: true);
        EnsureRegularFileOrMissing(path);
        EnsureRegularFileOrMissing(marker);
        if (pending.Action == "add")
        {
            var committed = File.Exists(path);
            if (!committed && File.Exists(marker))
            {
                throw new InvalidDataException("The pending vector add has a marker without an entry.");
            }

            if (committed && !File.Exists(marker))
            {
                EnsureMarkerDirectories(pending.SessionHash!, pending.OwnerHash!, alreadyHashed: true);
                CreateMarker(marker);
            }

            if (layout.EntryCount == pending.ExpectedEntryCount && committed)
            {
                await WriteDocumentAtomicAsync(
                        _layoutPath,
                        new PartitionLayoutDocument
                        {
                            FormatVersion = 2,
                            EntryCount = checked(pending.ExpectedEntryCount + 1),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (layout.EntryCount != pending.ExpectedEntryCount
                     && (layout.EntryCount != checked(pending.ExpectedEntryCount + 1) || !committed))
            {
                throw new InvalidDataException("The pending vector add cannot be reconciled safely.");
            }
        }
        else
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            if (layout.EntryCount == pending.ExpectedEntryCount)
            {
                if (pending.ExpectedEntryCount == 0)
                {
                    throw new InvalidDataException("The pending vector delete has an invalid count.");
                }

                await WriteDocumentAtomicAsync(
                        _layoutPath,
                        new PartitionLayoutDocument
                        {
                            FormatVersion = 2,
                            EntryCount = checked(pending.ExpectedEntryCount - 1),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (layout.EntryCount != checked(pending.ExpectedEntryCount - 1))
            {
                throw new InvalidDataException("The pending vector delete cannot be reconciled safely.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_pendingPath);
    }

    private async ValueTask<PartitionLayoutDocument?> ReadLayoutAsync(CancellationToken cancellationToken)
    {
        EnsurePartitionPaths(createDirectory: false);
        var layout = await ReadDocumentAsync<PartitionLayoutDocument>(_layoutPath, cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            return null;
        }

        if (layout.FormatVersion != 2 || layout.EntryCount < 0 || layout.EntryCount > _capacity)
        {
            throw new InvalidDataException("The vector partition metadata is corrupt.");
        }

        return layout;
    }

    private async ValueTask<PartitionLayoutDocument> ReadStableLayoutAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
                if (document is not null)
                {
                    return document;
                }
            }
            catch (IOException exception)
            {
                if (attempt >= 99)
                {
                    throw new InvalidDataException(
                        "The vector partition metadata remained unavailable.",
                        exception);
                }
            }

            if (attempt < 99)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException("The vector partition metadata is missing or unavailable.");
    }

    private IEnumerable<string> EnumerateMarkers(string sessionId, string? ownerId)
    {
        var sessionDirectory = SessionMarkerDirectory(sessionId);
        EnsureDirectoryOrMissing(sessionDirectory);
        if (!Directory.Exists(sessionDirectory))
        {
            return Array.Empty<string>();
        }

        if (ownerId is not null)
        {
            var ownerDirectory = OwnerMarkerDirectory(sessionId, ownerId);
            EnsureDirectoryOrMissing(ownerDirectory);
            return !Directory.Exists(ownerDirectory)
                ? Array.Empty<string>()
                : Directory.EnumerateFiles(ownerDirectory, "*" + MarkerSuffix, SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
        }

        var paths = new List<string>();
        foreach (var ownerDirectory in Directory.EnumerateDirectories(sessionDirectory, "o-*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            EnsureDirectory(ownerDirectory);
            paths.AddRange(Directory.EnumerateFiles(ownerDirectory, "*" + MarkerSuffix, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal));
            if (paths.Count > _capacity)
            {
                throw new InvalidOperationException("The vector partition exceeded its configured capacity.");
            }
        }

        return paths;
    }

    private void EnsurePartitionPaths(bool createDirectory)
    {
        EnsureDirectoryChainIsSafe(_directory);
        if (createDirectory)
        {
            Directory.CreateDirectory(_partitionDirectory);
        }

        EnsureDirectoryOrMissing(_partitionDirectory);
        EnsureRegularFileOrMissing(_layoutPath);
        EnsureRegularFileOrMissing(_pendingPath);
    }

    private void EnsureMarkerDirectories(string sessionId, string ownerId) =>
        EnsureMarkerDirectories(Hash(sessionId), Hash(ownerId), alreadyHashed: true);

    private void EnsureMarkerDirectories(string sessionHash, string ownerHash, bool alreadyHashed)
    {
        _ = alreadyHashed;
        EnsurePartitionPaths(createDirectory: true);
        var sessionDirectory = Path.Combine(_partitionDirectory, "s-" + sessionHash);
        Directory.CreateDirectory(sessionDirectory);
        EnsureDirectory(sessionDirectory);
        var ownerDirectory = Path.Combine(sessionDirectory, "o-" + ownerHash);
        Directory.CreateDirectory(ownerDirectory);
        EnsureDirectory(ownerDirectory);
    }

    private string SessionMarkerDirectory(string sessionId) =>
        Path.Combine(_partitionDirectory, "s-" + Hash(sessionId));

    private string OwnerMarkerDirectory(string sessionId, string ownerId) =>
        Path.Combine(SessionMarkerDirectory(sessionId), "o-" + Hash(ownerId));

    private string MarkerPath(string sessionId, string ownerId, string key) =>
        Path.Combine(OwnerMarkerDirectory(sessionId, ownerId), key + MarkerSuffix);

    private string MarkerPath(string sessionHash, string ownerHash, string key, bool alreadyHashed)
    {
        _ = alreadyHashed;
        return Path.Combine(
            _partitionDirectory,
            "s-" + sessionHash,
            "o-" + ownerHash,
            key + MarkerSuffix);
    }

    private static void CreateMarker(string path)
    {
        EnsureRegularFileOrMissing(path);
        if (File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private async ValueTask<T?> ReadDocumentAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        EnsureRegularFileOrMissing(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length < 2 || info.Length > _maximumFileBytes)
        {
            throw new InvalidDataException("A vector partition document has an invalid size.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            useAsync: true);
        try
        {
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions { MaxDepth = 128 },
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureUnambiguous(document.RootElement);
            return document.RootElement.Deserialize<T>(MemoryVectorIndexCodec.JsonOptions)
                ?? throw new InvalidDataException("A vector partition document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A vector partition document contains invalid JSON.", exception);
        }
    }

    private async ValueTask WriteDocumentAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, MemoryVectorIndexCodec.JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (stream.Length > _maximumFileBytes)
                {
                    throw new InvalidOperationException("A vector partition document exceeded its configured size.");
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async ValueTask<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_directory, ".vector-memory.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, useAsync: true);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<VectorMemoryIndexEntry> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 2 || info.Length > _maximumFileBytes)
        {
            throw new InvalidDataException("A vector memory index file has an invalid size.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        try
        {
            using var json = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions { MaxDepth = 128 },
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureUnambiguous(json.RootElement);
            var document = json.RootElement.Deserialize<MemoryVectorIndexCodec.MemoryVectorIndexDocument>(
                MemoryVectorIndexCodec.JsonOptions);
            return MemoryVectorIndexCodec.Decode(document);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A vector memory index file contains invalid JSON.", exception);
        }
    }

    private void ValidateAllMarkers(HashSet<string> expectedKeys)
    {
        if (!Directory.Exists(_partitionDirectory))
        {
            return;
        }

        var count = 0;
        foreach (var marker in Directory.EnumerateFiles(
                     _partitionDirectory,
                     "*" + MarkerSuffix,
                     SearchOption.AllDirectories))
        {
            EnsureRegularFile(marker);
            var key = Path.GetFileNameWithoutExtension(marker);
            if (!IsHash(key) || !expectedKeys.Contains(key) || ++count > _capacity)
            {
                throw new InvalidDataException("The vector partition contains an orphan or malformed marker.");
            }
        }

        if (count != expectedKeys.Count)
        {
            throw new InvalidDataException("The vector partition marker count is inconsistent.");
        }
    }

    private static void EnsureUnambiguous(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("A vector memory document contains duplicate JSON properties.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private async ValueTask WriteAtomicAsync(
        string path,
        VectorMemoryIndexEntry entry,
        CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        MemoryVectorIndexCodec.Encode(entry),
                        MemoryVectorIndexCodec.JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > _maximumFileBytes)
                {
                    throw new InvalidOperationException("A vector memory index entry exceeded its configured file bound.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Hash(string value)
    {
        using var algorithm = SHA256.Create();
        return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2")));
    }

    private static string StorageKey(string sessionId, string ownerId, string memoryId) =>
        Hash(sessionId + "\0" + ownerId + "\0" + memoryId);

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureDirectoryChainIsSafe(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Vector memory storage cannot use symbolic links or reparse points.");
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
            throw new InvalidDataException("Vector memory storage expected a regular directory.");
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Vector memory storage expected a regular file or a missing path.");
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
                throw new InvalidDataException("Vector memory storage expected a regular file.");
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException("A vector memory storage file disappeared during an operation.", exception);
        }
    }

    private sealed class PartitionLayoutDocument
    {
        public int FormatVersion { get; set; }

        public int EntryCount { get; set; }
    }

    private sealed class PendingDocument
    {
        public int FormatVersion { get; set; }

        public string? Action { get; set; }

        public int ExpectedEntryCount { get; set; }

        public string? SessionHash { get; set; }

        public string? OwnerHash { get; set; }

        public string? EntryHash { get; set; }
    }
}

internal static class MemoryVectorIndexCodec
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 128,
    };

    public static MemoryVectorIndexDocument Encode(VectorMemoryIndexEntry entry) => new()
    {
        FormatVersion = 1,
        Memory = MemoryDocument.Encode(entry.Memory),
        Identity = entry.Identity is null
            ? null
            : new IdentityDocument
            {
                ProviderId = entry.Identity.ProviderId,
                ModelId = entry.Identity.ModelId,
                Version = entry.Identity.Version,
                Dimensions = entry.Identity.Dimensions,
            },
        Vector = entry.Vector?.ToArray(),
        DiagnosticCode = entry.DiagnosticCode,
    };

    public static VectorMemoryIndexEntry Decode(MemoryVectorIndexDocument? document)
    {
        if (document is null || document.FormatVersion != 1 || document.Memory is null)
        {
            throw new InvalidDataException("A vector memory index document is malformed.");
        }

        var identity = document.Identity is null
            ? null
            : new MemoryEmbeddingIdentity(
                document.Identity.ProviderId ?? string.Empty,
                document.Identity.ModelId ?? string.Empty,
                document.Identity.Version ?? string.Empty,
                document.Identity.Dimensions);
        try
        {
            return new VectorMemoryIndexEntry(
                document.Memory.Decode(),
                identity,
                document.Vector,
                document.DiagnosticCode);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A vector memory index document contains invalid data.", exception);
        }
    }

    public static void EnsureSameMemory(GameMemory left, GameMemory right)
    {
        if (!string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || !string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
            || !string.Equals(left.MemoryId, right.MemoryId, StringComparison.Ordinal)
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
            throw new InvalidOperationException("A memory identity cannot be reused for different content.");
        }
    }

    internal sealed class MemoryVectorIndexDocument
    {
        public int FormatVersion { get; set; }

        public MemoryDocument? Memory { get; set; }

        public IdentityDocument? Identity { get; set; }

        public float[]? Vector { get; set; }

        public string? DiagnosticCode { get; set; }
    }

    internal sealed class IdentityDocument
    {
        public string? ProviderId { get; set; }

        public string? ModelId { get; set; }

        public string? Version { get; set; }

        public int Dimensions { get; set; }
    }

    internal sealed class MemoryDocument
    {
        public string? MemoryId { get; set; }

        public string? SessionId { get; set; }

        public string? OwnerId { get; set; }

        public string? Scope { get; set; }

        public int Kind { get; set; }

        public string? PayloadJson { get; set; }

        public string? TimelineId { get; set; }

        public long Tick { get; set; }

        public string? CalendarJson { get; set; }

        public double Importance { get; set; }

        public string? SearchableText { get; set; }

        public string[]? Tags { get; set; }

        public string? SourceInputId { get; set; }

        public string? ExpiresTimelineId { get; set; }

        public long? ExpiresTick { get; set; }

        public string? ExpiresCalendarJson { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }

        public static MemoryDocument Encode(GameMemory memory) => new()
        {
            MemoryId = memory.MemoryId,
            SessionId = memory.SessionId,
            OwnerId = memory.OwnerId,
            Scope = memory.Scope,
            Kind = (int)memory.Kind,
            PayloadJson = memory.PayloadJson,
            TimelineId = memory.Moment.TimelineId,
            Tick = memory.Moment.Tick,
            CalendarJson = memory.Moment.CalendarJson,
            Importance = memory.Importance,
            SearchableText = memory.SearchableText,
            Tags = memory.Tags.ToArray(),
            SourceInputId = memory.SourceInputId,
            ExpiresTimelineId = memory.ExpiresAt?.TimelineId,
            ExpiresTick = memory.ExpiresAt?.Tick,
            ExpiresCalendarJson = memory.ExpiresAt?.CalendarJson,
            Metadata = new Dictionary<string, string>(memory.Metadata, StringComparer.Ordinal),
        };

        public GameMemory Decode()
        {
            if (!Enum.IsDefined(typeof(GameMemoryKind), Kind))
            {
                throw new InvalidDataException("A vector memory kind is invalid.");
            }

            var moment = new GameMoment(TimelineId ?? string.Empty, Tick, CalendarJson);
            var expires = ExpiresTick.HasValue
                ? new GameMoment(ExpiresTimelineId ?? string.Empty, ExpiresTick.Value, ExpiresCalendarJson)
                : (GameMoment?)null;
            return new GameMemory(
                MemoryId ?? string.Empty,
                SessionId ?? string.Empty,
                OwnerId ?? string.Empty,
                Scope ?? string.Empty,
                (GameMemoryKind)Kind,
                PayloadJson ?? string.Empty,
                moment,
                Importance,
                SearchableText,
                Tags,
                SourceInputId,
                expires,
                Metadata);
        }
    }
}
