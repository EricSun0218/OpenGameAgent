using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Memory;

public sealed class InMemoryVectorMemoryIndex : IVectorMemoryIndex
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
public sealed class FileVectorMemoryIndex : IVectorMemoryIndex
{
    private const string Suffix = ".vector-memory.json";
    private readonly string _directory;
    private readonly int _capacity;
    private readonly long _maximumFileBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

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
        _capacity = capacity;
        _maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask UpsertAsync(VectorMemoryIndexEntry entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var key = StorageKey(entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId);
        var path = Path.Combine(_directory, key + Suffix);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
                if (File.Exists(path))
                {
                    var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    MemoryVectorIndexCodec.EnsureSameMemory(existing.Memory, entry.Memory);
                }
                else if (Directory.EnumerateFiles(_directory, "*" + Suffix, SearchOption.TopDirectoryOnly)
                             .Take(_capacity)
                             .Count() >= _capacity)
                {
                    throw new InvalidOperationException("The vector memory index reached its configured capacity.");
                }

                await WriteAtomicAsync(path, entry, cancellationToken).ConfigureAwait(false);
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
    {
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        if (maximumEntries < 1 || maximumEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var entries = new List<VectorMemoryIndexEntry>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > _capacity)
            {
                throw new InvalidOperationException("The vector memory index exceeded its configured capacity.");
            }

            var entry = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var expectedPath = Path.Combine(
                _directory,
                StorageKey(entry.Memory.SessionId, entry.Memory.OwnerId, entry.Memory.MemoryId) + Suffix);
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A vector memory index file has an invalid identity path.");
            }

            if (!string.Equals(entry.Memory.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
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
        var key = StorageKey(sessionId, ownerId, memoryId);
        var path = Path.Combine(_directory, key + Suffix);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(path))
            {
                return;
            }

            var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.Memory.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(existing.Memory.OwnerId, ownerId, StringComparison.Ordinal)
                || !string.Equals(existing.Memory.MemoryId, memoryId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A vector memory index file has an invalid identity path.");
            }

            File.Delete(path);
        }
        finally
        {
            gate.Release();
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

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var document = await JsonSerializer.DeserializeAsync<MemoryVectorIndexCodec.MemoryVectorIndexDocument>(
                stream,
                MemoryVectorIndexCodec.JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return MemoryVectorIndexCodec.Decode(document);
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

    private static string StorageKey(string sessionId, string ownerId, string memoryId)
    {
        using var algorithm = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(sessionId + "\0" + ownerId + "\0" + memoryId);
        return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("x2")));
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
