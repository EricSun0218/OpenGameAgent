using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public sealed class MemoryRecord
{
    public MemoryRecord(
        string memoryId,
        string scope,
        JsonElement content,
        IEnumerable<string>? tags,
        int importance,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? expiresAt = null)
    {
        MemoryId = RuntimeGuard.RequiredUtf8(memoryId, 128, nameof(memoryId));
        Scope = RuntimeGuard.RequiredUtf8(scope, 256, nameof(scope));
        JsonValueInspector.ValidateAndMeasure(
            content,
            new JsonValueLimits(maxUtf8Bytes: 131_072),
            nameof(content));
        if (importance is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(importance));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "Memory updatedAt cannot precede createdAt.",
                nameof(updatedAt));
        }

        Content = content.Clone();
        Tags = RuntimeGuard.CopyStrings(
            tags ?? Array.Empty<string>(),
            64,
            128,
            nameof(tags),
            sort: true,
            requireUnique: true);
        Importance = importance;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ExpiresAt = expiresAt;
    }

    public string MemoryId { get; }

    public string Scope { get; }

    public JsonElement Content { get; }

    public IReadOnlyList<string> Tags { get; }

    public int Importance { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }
}

public sealed class MemoryQuery
{
    public MemoryQuery(
        string scope,
        JsonElement query,
        IEnumerable<string>? requiredTags = null,
        int maxResults = 8,
        int maxUtf8Bytes = 32_768,
        DateTimeOffset? now = null)
    {
        Scope = RuntimeGuard.RequiredUtf8(scope, 256, nameof(scope));
        JsonValueInspector.ValidateAndMeasure(
            query,
            new JsonValueLimits(maxUtf8Bytes: 32_768),
            nameof(query));
        if (maxResults is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (maxUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        Query = query.Clone();
        RequiredTags = RuntimeGuard.CopyStrings(
            requiredTags ?? Array.Empty<string>(),
            64,
            128,
            nameof(requiredTags),
            sort: true,
            requireUnique: true);
        MaxResults = maxResults;
        MaxUtf8Bytes = maxUtf8Bytes;
        Now = now ?? DateTimeOffset.UtcNow;
    }

    public string Scope { get; }

    public JsonElement Query { get; }

    public IReadOnlyList<string> RequiredTags { get; }

    public int MaxResults { get; }

    public int MaxUtf8Bytes { get; }

    public DateTimeOffset Now { get; }
}

public sealed class MemorySearchResult
{
    /// <summary>
    /// Creates a result returned by an <see cref="IMemoryProvider"/>.
    /// Higher scores rank before lower scores when a consumer combines
    /// provider results.
    /// </summary>
    public MemorySearchResult(MemoryRecord record, int score)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Score = score;
    }

    public MemoryRecord Record { get; }

    public int Score { get; }
}

public interface IMemoryProvider
{
    string ProviderId { get; }

    ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken);
}

public interface IMemoryStore : IMemoryProvider
{
    ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A bounded, deterministic baseline memory implementation. It intentionally
/// requires no embedding model: both natural-language and structured JSON
/// queries are reduced to invariant lexical terms. Production games can replace
/// it with a vector, graph, full-text, or service-backed provider through
/// IMemoryProvider.
/// </summary>
public sealed class DeterministicMemoryStore : IMemoryStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IndexedRecord> _records =
        new(StringComparer.Ordinal);
    private readonly int _capacity;

    public DeterministicMemoryStore(
        string providerId = "deterministic-local",
        int capacity = 10_000)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public string ProviderId { get; }

    public ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var indexed = new IndexedRecord(record, Tokenize(record.Content));
        lock (_sync)
        {
            if (!_records.ContainsKey(record.MemoryId)
                && _records.Count >= _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(record),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            _records[record.MemoryId] = indexed;
        }

        return default;
    }

    public ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("Memory id is required.", nameof(memoryId));
        }

        lock (_sync)
        {
            return new ValueTask<bool>(_records.Remove(memoryId));
        }
    }

    public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var queryTerms = Tokenize(query.Query);
        IndexedRecord[] snapshot;
        lock (_sync)
        {
            snapshot = _records.Values.ToArray();
        }

        var ranked = new List<MemorySearchResult>();
        foreach (var indexed in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = indexed.Record;
            if (!string.Equals(record.Scope, query.Scope, StringComparison.Ordinal)
                || record.ExpiresAt <= query.Now
                || query.RequiredTags.Any(
                    tag => !record.Tags.Contains(tag, StringComparer.Ordinal)))
            {
                continue;
            }

            var overlap = queryTerms.Count == 0
                ? 0
                : queryTerms.Count(indexed.Terms.Contains);
            var exactTagMatches = record.Tags.Count(queryTerms.Contains);
            var score = checked(
                overlap * 1_000
                + exactTagMatches * 2_000
                + record.Importance * 10);
            if (queryTerms.Count > 0 && overlap == 0 && exactTagMatches == 0)
            {
                continue;
            }

            ranked.Add(new MemorySearchResult(record, score));
        }

        ranked.Sort(
            (left, right) =>
            {
                var score = right.Score.CompareTo(left.Score);
                if (score != 0)
                {
                    return score;
                }

                var updated = right.Record.UpdatedAt.CompareTo(
                    left.Record.UpdatedAt);
                return updated != 0
                    ? updated
                    : StringComparer.Ordinal.Compare(
                        left.Record.MemoryId,
                        right.Record.MemoryId);
            });

        var selected = new List<MemorySearchResult>();
        var bytes = 0;
        foreach (var result in ranked)
        {
            if (selected.Count >= query.MaxResults)
            {
                break;
            }

            var itemBytes = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (checked(bytes + itemBytes) > query.MaxUtf8Bytes)
            {
                continue;
            }

            selected.Add(result);
            bytes += itemBytes;
        }

        return new ValueTask<IReadOnlyList<MemorySearchResult>>(
            new ReadOnlyCollection<MemorySearchResult>(selected));
    }

    private static HashSet<string> Tokenize(JsonElement value)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        Visit(value, terms, depth: 0);
        return terms;
    }

    private static void Visit(
        JsonElement value,
        ISet<string> terms,
        int depth)
    {
        if (depth > 64)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AddText(property.Name, terms);
                    Visit(property.Value, terms, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item, terms, depth + 1);
                }

                break;
            case JsonValueKind.String:
                AddText(value.GetString() ?? string.Empty, terms);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                terms.Add(value.GetRawText().ToLowerInvariant());
                break;
        }
    }

    private static void AddText(string value, ISet<string> terms)
    {
        var token = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character)
                || character >= '\u3400' && character <= '\u9fff')
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            FlushToken(token, terms);
        }

        FlushToken(token, terms);
    }

    private static void FlushToken(StringBuilder token, ISet<string> terms)
    {
        if (token.Length == 0)
        {
            return;
        }

        terms.Add(token.ToString());
        token.Clear();
    }

    private sealed class IndexedRecord
    {
        public IndexedRecord(MemoryRecord record, HashSet<string> terms)
        {
            Record = record;
            Terms = terms;
        }

        public MemoryRecord Record { get; }

        public HashSet<string> Terms { get; }
    }
}
