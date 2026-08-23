using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

public enum GameMemoryKind
{
    Event,
    Fact,
    Relationship,
    Goal,
    Reflection,
    Procedure,
}

public sealed class GameMemory
{
    public GameMemory(
        string memoryId,
        string sessionId,
        string ownerId,
        string scope,
        GameMemoryKind kind,
        string payloadJson,
        GameMoment moment,
        double importance = 0.5,
        string? searchableText = null,
        IReadOnlyCollection<string>? tags = null,
        string? sourceInputId = null,
        GameMoment? expiresAt = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (double.IsNaN(importance) || double.IsInfinity(importance) || importance < 0 || importance > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(importance), "Importance must be between 0 and 1.");
        }

        MemoryId = RequireBoundedId(memoryId, nameof(memoryId));
        SessionId = RequireBoundedId(sessionId, nameof(sessionId));
        OwnerId = RequireBoundedId(ownerId, nameof(ownerId));
        Scope = RequireBoundedId(scope, nameof(scope));
        if (!Enum.IsDefined(typeof(GameMemoryKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        if (payloadJson is null || payloadJson.Length > 10_000_000)
        {
            throw new ArgumentException("A memory payload cannot exceed 10000000 characters.", nameof(payloadJson));
        }

        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Moment = moment.EnsureValid(nameof(moment));
        Importance = importance;
        if (searchableText?.Length > 1_000_000)
        {
            throw new ArgumentException("Memory searchable text cannot exceed 1000000 characters.", nameof(searchableText));
        }

        SearchableText = searchableText;
        var copiedTags = (tags ?? Array.Empty<string>())
                .Select(tag => RequireBoundedId(tag, nameof(tags)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToArray();
        if (copiedTags.Length > 256)
        {
            throw new ArgumentException("A memory can contain at most 256 tags.", nameof(tags));
        }

        Tags = Array.AsReadOnly(copiedTags);
        SourceInputId = sourceInputId is null ? null : RequireBoundedId(sourceInputId, nameof(sourceInputId));
        if (expiresAt is { } expiry
            && (expiry.EnsureValid(nameof(expiresAt)).TimelineId != moment.TimelineId
                || expiry.Tick < moment.Tick))
        {
            throw new ArgumentException("Memory expiry must be on the same timeline and not precede its creation moment.", nameof(expiresAt));
        }

        ExpiresAt = expiresAt;
        var copiedMetadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (copiedMetadata.Count > 256
            || copiedMetadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                                          || pair.Key.Length > 256
                                          || pair.Value is null
                                          || pair.Value.Length > 65_536))
        {
            throw new ArgumentException("Memory metadata requires non-empty keys and non-null values.", nameof(metadata));
        }

        Metadata = new ReadOnlyDictionary<string, string>(copiedMetadata);
    }

    private static string RequireBoundedId(string value, string name)
    {
        var required = GameJson.RequireId(value, name);
        if (required.Length > 1_024)
        {
            throw new ArgumentException("A memory identifier cannot exceed 1024 characters.", name);
        }

        return required;
    }

    public string MemoryId { get; }

    public string SessionId { get; }

    public string OwnerId { get; }

    public string Scope { get; }

    public GameMemoryKind Kind { get; }

    public string PayloadJson { get; }

    public GameMoment Moment { get; }

    public double Importance { get; }

    public string? SearchableText { get; }

    public IReadOnlyCollection<string> Tags { get; }

    public string? SourceInputId { get; }

    public GameMoment? ExpiresAt { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}

public sealed class GameMemoryQuery
{
    public GameMemoryQuery(
        string sessionId,
        int limit,
        string? ownerId = null,
        IReadOnlyCollection<string>? scopes = null,
        IReadOnlyCollection<GameMemoryKind>? kinds = null,
        IReadOnlyCollection<string>? tags = null,
        string? text = null,
        GameMoment? atOrBefore = null,
        double minimumImportance = 0)
    {
        if (limit < 0 || limit > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (double.IsNaN(minimumImportance)
            || double.IsInfinity(minimumImportance)
            || minimumImportance < 0
            || minimumImportance > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumImportance));
        }

        SessionId = RequireBoundedId(sessionId, nameof(sessionId));
        Limit = limit;
        OwnerId = ownerId is null ? null : RequireBoundedId(ownerId, nameof(ownerId));
        Scopes = CopyIds(scopes, nameof(scopes));
        var copiedKinds = (kinds ?? Array.Empty<GameMemoryKind>()).Distinct().ToArray();
        if (copiedKinds.Any(kind => !Enum.IsDefined(typeof(GameMemoryKind), kind)))
        {
            throw new ArgumentOutOfRangeException(nameof(kinds));
        }

        Kinds = Array.AsReadOnly(copiedKinds);
        Tags = CopyIds(tags, nameof(tags));
        if (text?.Length > 1_000_000)
        {
            throw new ArgumentException("A memory query cannot exceed 1000000 text characters.", nameof(text));
        }

        Text = text;
        AtOrBefore = atOrBefore?.EnsureValid(nameof(atOrBefore));
        MinimumImportance = minimumImportance;
    }

    public string SessionId { get; }

    public int Limit { get; }

    public string? OwnerId { get; }

    public IReadOnlyCollection<string> Scopes { get; }

    public IReadOnlyCollection<GameMemoryKind> Kinds { get; }

    public IReadOnlyCollection<string> Tags { get; }

    public string? Text { get; }

    public GameMoment? AtOrBefore { get; }

    public double MinimumImportance { get; }

    private static IReadOnlyCollection<string> CopyIds(IReadOnlyCollection<string>? values, string parameterName)
    {
        var copied = (values ?? Array.Empty<string>())
            .Select(value => RequireBoundedId(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > 256)
        {
            throw new ArgumentException("A memory query filter can contain at most 256 values.", parameterName);
        }

        return Array.AsReadOnly(copied);
    }

    private static string RequireBoundedId(string value, string name)
    {
        var required = GameJson.RequireId(value, name);
        if (required.Length > 1_024)
        {
            throw new ArgumentException("A memory query identifier cannot exceed 1024 characters.", name);
        }

        return required;
    }
}

public interface IGameMemoryStore
{
    ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameMemory>> SearchAsync(GameMemoryQuery query, CancellationToken cancellationToken);
}

public enum GameMemorySearchStageKind
{
    StorageMigration,
    AuthoritativeSnapshot,
    LexicalSearch,
    VectorIndexRead,
    Embedding,
    VectorScoring,
    Rerank,
}

/// <summary>
/// Bounded operational metadata for one memory-search stage. It intentionally
/// contains no query text, memory content, identifiers, or provider secrets.
/// </summary>
public sealed class GameMemorySearchStageMetric
{
    public GameMemorySearchStageMetric(
        GameMemorySearchStageKind stage,
        TimeSpan duration,
        int scannedCount = 0,
        int candidateCount = 0,
        bool reused = false)
    {
        if (!Enum.IsDefined(typeof(GameMemorySearchStageKind), stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (scannedCount < 0 || scannedCount > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(scannedCount));
        }

        if (candidateCount < 0 || candidateCount > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        }

        Stage = stage;
        Duration = duration;
        ScannedCount = scannedCount;
        CandidateCount = candidateCount;
        Reused = reused;
    }

    public GameMemorySearchStageKind Stage { get; }

    public TimeSpan Duration { get; }

    public int ScannedCount { get; }

    public int CandidateCount { get; }

    public bool Reused { get; }
}

/// <summary>
/// A query result coupled to the exact authoritative identity partition used
/// to produce it. Wrappers such as vector recall can validate derived records
/// against this snapshot without reading the authoritative store a second time.
/// </summary>
public sealed class GameMemorySearchSnapshot
{
    public GameMemorySearchSnapshot(
        IReadOnlyList<GameMemory> memories,
        IReadOnlyList<GameMemory> authoritativeMemories,
        IReadOnlyList<GameMemorySearchStageMetric>? stages = null)
    {
        Memories = CopyMemories(memories, nameof(memories));
        AuthoritativeMemories = CopyMemories(authoritativeMemories, nameof(authoritativeMemories));
        var copiedStages = (stages ?? Array.Empty<GameMemorySearchStageMetric>()).ToArray();
        if (copiedStages.Length > 64 || copiedStages.Any(value => value is null))
        {
            throw new ArgumentException("A memory search snapshot contains invalid stage metrics.", nameof(stages));
        }

        Stages = Array.AsReadOnly(copiedStages);
    }

    public IReadOnlyList<GameMemory> Memories { get; }

    public IReadOnlyList<GameMemory> AuthoritativeMemories { get; }

    public IReadOnlyList<GameMemorySearchStageMetric> Stages { get; }

    private static IReadOnlyList<GameMemory> CopyMemories(IReadOnlyList<GameMemory> values, string name)
    {
        var copy = (values ?? throw new ArgumentNullException(name)).ToArray();
        if (copy.Length > 1_000_000 || copy.Any(value => value is null))
        {
            throw new ArgumentException("A memory search snapshot contains invalid memories.", name);
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// Optional store capability for returning search results and the exact
/// authoritative session/owner partition in one bounded read.
/// </summary>
public interface IGameMemorySearchSnapshotSource
{
    ValueTask<GameMemorySearchSnapshot> SearchSnapshotAsync(
        GameMemoryQuery query,
        int maximumSnapshotEntries,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional owner-partition snapshot capability used when callers already
/// possess an owner-scoped authorization decision.
/// </summary>
public interface IGameMemoryPartitionSnapshotSource
{
    IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        string ownerId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provides a deterministic, authoritative memory snapshot for rebuilding
/// optional derived indexes. Implementations must not return memories from a
/// different session and must preserve the store's normal visibility data.
/// </summary>
public interface IGameMemorySnapshotSource
{
    IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public interface IGameMemoryRanker
{
    ValueTask<IReadOnlyList<GameMemory>> RankAsync(
        GameMemoryQuery query,
        IReadOnlyList<GameMemory> candidates,
        CancellationToken cancellationToken);
}

public sealed class RankedGameMemoryStore : IGameMemoryStore
{
    private readonly IGameMemoryStore _inner;
    private readonly IGameMemoryRanker _ranker;
    private readonly int _candidateMultiplier;
    private readonly int _maximumCandidates;

    public RankedGameMemoryStore(
        IGameMemoryStore inner,
        IGameMemoryRanker ranker,
        int candidateMultiplier = 4,
        int maximumCandidates = 512)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ranker = ranker ?? throw new ArgumentNullException(nameof(ranker));
        if (candidateMultiplier < 1 || candidateMultiplier > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateMultiplier));
        }

        if (maximumCandidates < 1 || maximumCandidates > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        _candidateMultiplier = candidateMultiplier;
        _maximumCandidates = maximumCandidates;
    }

    public ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken) =>
        _inner.AppendAsync(memory, cancellationToken);

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

        var expandedLimit = (int)Math.Min(
            _maximumCandidates,
            Math.Max((long)query.Limit, (long)query.Limit * _candidateMultiplier));
        var expanded = new GameMemoryQuery(
            query.SessionId,
            expandedLimit,
            query.OwnerId,
            query.Scopes,
            query.Kinds,
            query.Tags,
            query.Text,
            query.AtOrBefore,
            query.MinimumImportance);
        var candidates = await _inner.SearchAsync(expanded, cancellationToken).ConfigureAwait(false);
        if (candidates is null)
        {
            throw new InvalidOperationException("The memory store returned null.");
        }

        if (candidates.Count > expandedLimit)
        {
            throw new InvalidOperationException("The memory store exceeded the requested candidate limit.");
        }

        var candidateById = new Dictionary<(string OwnerId, string MemoryId), GameMemory>();
        foreach (var memory in candidates)
        {
            if (memory is null || !candidateById.TryAdd((memory.OwnerId, memory.MemoryId), memory))
            {
                throw new InvalidOperationException("The memory store returned a null or duplicate candidate.");
            }

            if (!MatchesQuery(memory, query))
            {
                throw new InvalidOperationException("The memory store returned a candidate outside the requested visibility filters.");
            }
        }

        var ranked = await _ranker.RankAsync(query, candidates, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The memory ranker returned null.");
        if (ranked.Count > candidates.Count)
        {
            throw new InvalidOperationException("The memory ranker returned more memories than it received.");
        }

        var returnedIds = new HashSet<(string OwnerId, string MemoryId)>();
        var canonical = new List<GameMemory>(ranked.Count);
        foreach (var memory in ranked)
        {
            if (memory is null
                || !candidateById.TryGetValue((memory.OwnerId, memory.MemoryId), out var candidate)
                || !returnedIds.Add((memory.OwnerId, memory.MemoryId)))
            {
                throw new InvalidOperationException("The memory ranker returned an unknown, duplicate, or null memory.");
            }

            canonical.Add(candidate);
        }

        return Array.AsReadOnly(canonical.Take(query.Limit).ToArray());
    }

    private static bool MatchesQuery(GameMemory memory, GameMemoryQuery query)
    {
        if (!string.Equals(memory.SessionId, query.SessionId, StringComparison.Ordinal)
            || (query.OwnerId is not null
                && !string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
            || (query.Scopes.Count > 0 && !query.Scopes.Contains(memory.Scope, StringComparer.Ordinal))
            || (query.Kinds.Count > 0 && !query.Kinds.Contains(memory.Kind))
            || query.Tags.Any(tag => !memory.Tags.Contains(tag, StringComparer.Ordinal))
            || memory.Importance < query.MinimumImportance)
        {
            return false;
        }

        if (query.AtOrBefore is not { } moment)
        {
            return true;
        }

        return string.Equals(memory.Moment.TimelineId, moment.TimelineId, StringComparison.Ordinal)
            && memory.Moment.Tick <= moment.Tick
            && (memory.ExpiresAt is null || moment.Tick < memory.ExpiresAt.Value.Tick);
    }
}

public sealed class InMemoryGameMemoryStore :
    IGameMemoryStore,
    IGameMemorySnapshotSource,
    IGameMemoryPartitionSnapshotSource,
    IGameMemorySearchSnapshotSource
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SessionId, string OwnerId, string MemoryId), GameMemory> _memories = new();
    private readonly int _capacity;

    public InMemoryGameMemoryStore(int capacity = 100_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memory is null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        lock (_gate)
        {
            var key = (memory.SessionId, memory.OwnerId, memory.MemoryId);
            if (_memories.TryGetValue(key, out var existing))
            {
                if (!Equivalent(existing, memory))
                {
                    throw new InvalidOperationException("A memory ID cannot be reused for different content.");
                }

                return default;
            }

            if (_memories.Count >= _capacity)
            {
                throw new GameRuntimeLimitException(nameof(_capacity), "The memory store reached its capacity.");
            }

            _memories.Add(key, memory);
        }

        return default;
    }

    public async ValueTask<IReadOnlyList<GameMemory>> SearchAsync(
        GameMemoryQuery query,
        CancellationToken cancellationToken)
    {
        var snapshot = await SearchSnapshotAsync(query, _capacity, cancellationToken).ConfigureAwait(false);
        return snapshot.Memories;
    }

    public ValueTask<GameMemorySearchSnapshot> SearchSnapshotAsync(
        GameMemoryQuery query,
        int maximumSnapshotEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (maximumSnapshotEntries < 1 || maximumSnapshotEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotEntries));
        }

        maximumSnapshotEntries = Math.Min(maximumSnapshotEntries, _capacity);

        var snapshotStartedAt = Stopwatch.GetTimestamp();
        GameMemory[] snapshot;
        lock (_gate)
        {
            snapshot = _memories.Values
                .Where(memory => string.Equals(memory.SessionId, query.SessionId, StringComparison.Ordinal))
                .Where(memory => query.OwnerId is null || string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
                .OrderBy(memory => memory.OwnerId, StringComparer.Ordinal)
                .ThenBy(memory => memory.MemoryId, StringComparer.Ordinal)
                .Take(maximumSnapshotEntries + 1)
                .ToArray();
        }

        if (snapshot.Length > maximumSnapshotEntries)
        {
            throw new GameRuntimeLimitException(
                nameof(maximumSnapshotEntries),
                "The authoritative memory partition exceeded the requested snapshot bound.");
        }

        var snapshotDuration = Elapsed(snapshotStartedAt);
        var lexicalStartedAt = Stopwatch.GetTimestamp();

        var scopes = new HashSet<string>(query.Scopes, StringComparer.Ordinal);
        var kinds = new HashSet<GameMemoryKind>(query.Kinds);
        var tags = new HashSet<string>(query.Tags, StringComparer.Ordinal);
        var visible = snapshot
            .Where(memory => scopes.Count == 0 || scopes.Contains(memory.Scope))
            .Where(memory => kinds.Count == 0 || kinds.Contains(memory.Kind))
            .Where(memory => tags.Count == 0 || tags.All(tag => memory.Tags.Contains(tag, StringComparer.Ordinal)))
            .Where(memory => memory.Importance >= query.MinimumImportance)
            .Where(memory => IsVisibleAt(memory, query.AtOrBefore))
            .ToArray();
        var candidates = ScoreCandidates(visible, query)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Memory.Moment.Tick)
            .ThenBy(candidate => candidate.Memory.MemoryId, StringComparer.Ordinal)
            .Take(query.Limit)
            .Select(candidate => candidate.Memory)
            .ToArray();

        var result = new GameMemorySearchSnapshot(
            candidates,
            snapshot,
            new[]
            {
                new GameMemorySearchStageMetric(
                    GameMemorySearchStageKind.AuthoritativeSnapshot,
                    snapshotDuration,
                    snapshot.Length,
                    snapshot.Length),
                new GameMemorySearchStageMetric(
                    GameMemorySearchStageKind.LexicalSearch,
                    Elapsed(lexicalStartedAt),
                    snapshot.Length,
                    candidates.Length),
            });
        return new ValueTask<GameMemorySearchSnapshot>(result);
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        GameMemory[] snapshot;
        lock (_gate)
        {
            snapshot = _memories.Values
                .Where(memory => string.Equals(memory.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(memory => memory.OwnerId, StringComparer.Ordinal)
                .ThenBy(memory => memory.MemoryId, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var memory in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return memory;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        string ownerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ownerId = GameJson.RequireId(ownerId, nameof(ownerId));
        GameMemory[] snapshot;
        lock (_gate)
        {
            snapshot = _memories.Values
                .Where(memory => string.Equals(memory.SessionId, sessionId, StringComparison.Ordinal)
                                 && string.Equals(memory.OwnerId, ownerId, StringComparison.Ordinal))
                .OrderBy(memory => memory.MemoryId, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var memory in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return memory;
            await Task.Yield();
        }
    }

    private static IReadOnlyList<MemoryCandidate> ScoreCandidates(
        IReadOnlyList<GameMemory> memories,
        GameMemoryQuery query)
    {
        var queryTerms = Tokenize(query.Text).Distinct(StringComparer.Ordinal).ToArray();
        if (query.Text is not null && queryTerms.Length == 0)
        {
            memories = memories.Where(memory =>
                    (memory.SearchableText?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ?? false)
                    || memory.PayloadJson.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var tokenized = memories.Select(memory => new MemoryCandidate(
            memory,
            Tokenize((memory.SearchableText ?? string.Empty) + "\n" + memory.PayloadJson))).ToArray();
        if (queryTerms.Length == 0)
        {
            foreach (var candidate in tokenized)
            {
                candidate.Score = BaseScore(candidate.Memory, query);
            }

            return tokenized;
        }

        var averageLength = tokenized.Length == 0 ? 1d : tokenized.Average(candidate => Math.Max(1, candidate.Length));
        var documentFrequencies = queryTerms.ToDictionary(term => term, _ => 0, StringComparer.Ordinal);
        foreach (var candidate in tokenized)
        {
            foreach (var term in candidate.Terms.Keys)
            {
                if (documentFrequencies.TryGetValue(term, out var frequency))
                {
                    documentFrequencies[term] = frequency + 1;
                }
            }
        }


        var documentCount = tokenized.Length;
        tokenized = tokenized.Where(candidate => queryTerms.Any(candidate.Terms.ContainsKey)).ToArray();

        foreach (var candidate in tokenized)
        {
            var lexical = 0d;
            foreach (var term in queryTerms)
            {
                if (!candidate.Terms.TryGetValue(term, out var frequency))
                {
                    continue;
                }

                var documentFrequency = documentFrequencies[term];
                var inverseFrequency = Math.Log(1d + ((documentCount - documentFrequency + 0.5d) / (documentFrequency + 0.5d)));
                const double saturation = 1.2d;
                const double lengthNormalization = 0.75d;
                var normalizedFrequency = (frequency * (saturation + 1d))
                    / (frequency + (saturation * (1d - lengthNormalization
                        + (lengthNormalization * candidate.Length / averageLength))));
                lexical += inverseFrequency * normalizedFrequency;
            }

            candidate.Score = BaseScore(candidate.Memory, query) + lexical;
        }

        return tokenized;
    }

    private static double BaseScore(GameMemory memory, GameMemoryQuery query)
    {
        var tagMatches = query.Tags.Count(tag => memory.Tags.Contains(tag, StringComparer.Ordinal));
        return memory.Importance + (tagMatches * 0.1);
    }

    private static IReadOnlyList<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        const int maximumCharacters = 100_000;
        const int maximumTokens = 4_096;
        var tokens = new List<string>();
        var word = new StringBuilder();
        char? previousCjk = null;
        var length = Math.Min(value.Length, maximumCharacters);
        for (var index = 0; index < length && tokens.Count < maximumTokens; index++)
        {
            var character = value[index];
            if (IsCjk(character))
            {
                FlushWord(word, tokens, maximumTokens);
                var current = character.ToString();
                tokens.Add(current);
                if (previousCjk is { } previous && tokens.Count < maximumTokens)
                {
                    tokens.Add(previous.ToString() + current);
                }

                previousCjk = character;
                continue;
            }

            previousCjk = null;
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else
            {
                FlushWord(word, tokens, maximumTokens);
            }
        }

        FlushWord(word, tokens, maximumTokens);
        return tokens;
    }

    private static void FlushWord(StringBuilder word, ICollection<string> tokens, int maximumTokens)
    {
        if (word.Length > 0 && tokens.Count < maximumTokens)
        {
            tokens.Add(word.ToString());
            word.Clear();
        }
    }

    private static bool IsCjk(char value) =>
        value is >= '\u3400' and <= '\u4DBF'
        or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';

    private sealed class MemoryCandidate
    {
        public MemoryCandidate(GameMemory memory, IReadOnlyList<string> tokens)
        {
            Memory = memory;
            Length = tokens.Count;
            Terms = tokens.GroupBy(token => token, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        }

        public GameMemory Memory { get; }

        public Dictionary<string, int> Terms { get; }

        public int Length { get; }

        public double Score { get; set; }
    }

    private static bool IsVisibleAt(GameMemory memory, GameMoment? atOrBefore)
    {
        if (atOrBefore is null)
        {
            return true;
        }

        return string.Equals(memory.Moment.TimelineId, atOrBefore.Value.TimelineId, StringComparison.Ordinal)
            && memory.Moment.Tick <= atOrBefore.Value.Tick
            && (memory.ExpiresAt is null || atOrBefore.Value.Tick < memory.ExpiresAt.Value.Tick);
    }

    private static bool Equivalent(GameMemory left, GameMemory right) =>
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
        && string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && string.Equals(left.PayloadJson, right.PayloadJson, StringComparison.Ordinal)
        && left.Moment == right.Moment
        && left.Importance.Equals(right.Importance)
        && string.Equals(left.SearchableText, right.SearchableText, StringComparison.Ordinal)
        && left.Tags.SequenceEqual(right.Tags)
        && string.Equals(left.SourceInputId, right.SourceInputId, StringComparison.Ordinal)
        && left.ExpiresAt == right.ExpiresAt
        && left.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private static TimeSpan Elapsed(long startedAt)
    {
        var ticks = checked(Stopwatch.GetTimestamp() - startedAt);
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}
