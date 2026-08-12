using System.Collections.ObjectModel;

namespace OpenGameAgent.Memory;

public sealed class GameAwareMemoryRerankerOptions
{
    public GameAwareMemoryRerankerOptions(
        int sourceOrderWeight = 1_000_000,
        int importanceWeight = 100_000,
        int gameTimeRecencyWeight = 50_000,
        int diversityPenalty = 10_000,
        int maximumGreedySelections = 512)
    {
        ValidateWeight(sourceOrderWeight, nameof(sourceOrderWeight));
        ValidateWeight(importanceWeight, nameof(importanceWeight));
        ValidateWeight(gameTimeRecencyWeight, nameof(gameTimeRecencyWeight));
        ValidateWeight(diversityPenalty, nameof(diversityPenalty));
        if (maximumGreedySelections < 1 || maximumGreedySelections > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGreedySelections));
        }

        SourceOrderWeight = sourceOrderWeight;
        ImportanceWeight = importanceWeight;
        GameTimeRecencyWeight = gameTimeRecencyWeight;
        DiversityPenalty = diversityPenalty;
        MaximumGreedySelections = maximumGreedySelections;
    }

    public int SourceOrderWeight { get; }

    public int ImportanceWeight { get; }

    public int GameTimeRecencyWeight { get; }

    public int DiversityPenalty { get; }

    public int MaximumGreedySelections { get; }

    private static void ValidateWeight(int value, string parameterName)
    {
        if (value < 0 || value > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// Reorders already-authorized candidates using source relevance, explicit
/// importance, game-time recency, and bounded diversity. Wall-clock recency is
/// intentionally absent because game worlds own their time semantics.
/// </summary>
public sealed class GameAwareMemoryReranker : IGameMemoryRanker
{
    private readonly GameAwareMemoryRerankerOptions _options;

    public GameAwareMemoryReranker(GameAwareMemoryRerankerOptions? options = null)
    {
        _options = options ?? new GameAwareMemoryRerankerOptions();
    }

    public ValueTask<IReadOnlyList<GameMemory>> RankAsync(
        GameMemoryQuery query,
        IReadOnlyList<GameMemory> candidates,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (candidates is null)
        {
            throw new ArgumentNullException(nameof(candidates));
        }

        var remaining = candidates
            .Select((memory, index) => new Candidate(memory, index, BaseScore(query, memory, index, candidates.Count)))
            .ToList();
        var selected = new List<GameMemory>(remaining.Count);
        var greedyCount = Math.Min(remaining.Count, _options.MaximumGreedySelections);
        while (remaining.Count > 0 && selected.Count < greedyCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate? winner = null;
            long winnerScore = long.MinValue;
            foreach (var candidate in remaining)
            {
                var score = candidate.BaseScore
                    - ((long)candidate.MaximumDiversityOverlap * _options.DiversityPenalty);
                if (winner is null
                    || score > winnerScore
                    || (score == winnerScore && Compare(candidate, winner) < 0))
                {
                    winner = candidate;
                    winnerScore = score;
                }
            }

            remaining.Remove(winner!);
            selected.Add(winner!.Memory);
            foreach (var candidate in remaining)
            {
                candidate.MaximumDiversityOverlap = Math.Max(
                    candidate.MaximumDiversityOverlap,
                    DiversityOverlap(candidate.Memory, winner.Memory));
            }
        }

        selected.AddRange(remaining.OrderBy(candidate => candidate.InputIndex).Select(candidate => candidate.Memory));
        return new ValueTask<IReadOnlyList<GameMemory>>(new ReadOnlyCollection<GameMemory>(selected));
    }

    private long BaseScore(GameMemoryQuery query, GameMemory memory, int inputIndex, int candidateCount)
    {
        var sourceOrder = candidateCount - inputIndex;
        var score = (long)sourceOrder * _options.SourceOrderWeight;
        score += (long)Math.Round(memory.Importance * _options.ImportanceWeight, MidpointRounding.AwayFromZero);
        if (_options.GameTimeRecencyWeight > 0
            && query.AtOrBefore is { } moment
            && string.Equals(moment.TimelineId, memory.Moment.TimelineId, StringComparison.Ordinal)
            && moment.Tick >= memory.Moment.Tick)
        {
            var distance = moment.Tick - memory.Moment.Tick;
            score += (long)_options.GameTimeRecencyWeight * 1_000 / (1 + Math.Min(distance, 1_000_000));
        }

        return score;
    }

    private static int DiversityOverlap(GameMemory left, GameMemory right)
    {
        var overlap = left.Tags.Intersect(right.Tags, StringComparer.Ordinal).Count();
        if (string.Equals(left.Scope, right.Scope, StringComparison.Ordinal))
        {
            overlap++;
        }

        if (left.Kind == right.Kind)
        {
            overlap++;
        }

        return overlap;
    }

    private static int Compare(Candidate left, Candidate right)
    {
        var byInput = left.InputIndex.CompareTo(right.InputIndex);
        if (byInput != 0)
        {
            return byInput;
        }

        var byOwner = string.CompareOrdinal(left.Memory.OwnerId, right.Memory.OwnerId);
        return byOwner != 0 ? byOwner : string.CompareOrdinal(left.Memory.MemoryId, right.Memory.MemoryId);
    }

    private sealed class Candidate
    {
        public Candidate(GameMemory memory, int inputIndex, long baseScore)
        {
            Memory = memory ?? throw new InvalidOperationException("A memory candidate cannot be null.");
            InputIndex = inputIndex;
            BaseScore = baseScore;
        }

        public GameMemory Memory { get; }

        public int InputIndex { get; }

        public long BaseScore { get; }

        public int MaximumDiversityOverlap { get; set; }
    }
}
