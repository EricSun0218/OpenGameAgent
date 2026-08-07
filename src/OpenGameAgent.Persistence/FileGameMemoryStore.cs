using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameMemoryStore : IGameMemoryStore
{
    private const string Suffix = ".memory.json";
    private readonly FileStore _files;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public FileGameMemoryStore(
        string directory,
        int maximumEntries = 100_000,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _maximumEntries = maximumEntries;
    }

    public async ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken)
    {
        if (memory is null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        var gate = _files.GateFor(memory.MemoryId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _files.PathFor(memory.MemoryId, Suffix);
            var existing = await _files.ReadAsync<MemoryDocument>(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureEquivalent(Decode(existing), memory);
                return;
            }

            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = await _files.ReadAsync<MemoryDocument>(path, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    EnsureEquivalent(Decode(existing), memory);
                    return;
                }

                if (Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                        .Take(_maximumEntries)
                        .Count() >= _maximumEntries)
                {
                    throw new GameRuntimeLimitException(nameof(_maximumEntries), "The file memory store reached its capacity.");
                }

                await _files.WriteAtomicAsync(path, Encode(memory), cancellationToken).ConfigureAwait(false);
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

        var inMemory = new InMemoryGameMemoryStore(_maximumEntries);
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal)
                     .Take(_maximumEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _files.ReadAsync<MemoryDocument>(path, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                var memory = Decode(document);
                _files.EnsurePathFor(path, memory.MemoryId, Suffix, "memory");
                await inMemory.AppendAsync(memory, cancellationToken).ConfigureAwait(false);
            }
        }

        return await inMemory.SearchAsync(query, cancellationToken).ConfigureAwait(false);
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
