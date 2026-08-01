using System.Collections.ObjectModel;

namespace GameAgent.Core;

public interface IMemoryQueryTransformer
{
    string TransformerId { get; }

    string Version { get; }

    ValueTask<MemoryQuery> TransformAsync(
        MemoryQuery query,
        CancellationToken cancellationToken);
}

public interface IMemoryResultReranker
{
    string RerankerId { get; }

    string Version { get; }

    ValueTask<IReadOnlyList<MemorySearchResult>> RerankAsync(
        MemoryQuery query,
        IReadOnlyList<MemorySearchResult> candidates,
        CancellationToken cancellationToken);
}

public sealed class GameAwareMemoryRerankerOptions
{
    public int ImportanceWeight { get; set; } = 100;

    public int GameTimeRecencyWeight { get; set; } = 10_000;

    /// <summary>
    /// Disabled by default so a world's recall does not accidentally follow
    /// wall-clock recency. Enable only when real time is part of the game.
    /// </summary>
    public int RealTimeRecencyWeight { get; set; }

    public TimeSpan RealTimeHalfLife { get; set; } = TimeSpan.FromDays(7);

    public int DiversityPenalty { get; set; } = 2_000;

    /// <summary>
    /// Bounds the greedy diversity prefix. The untouched tail keeps provider
    /// order because recall admits at most 128 results and quadratic ranking
    /// across tens of thousands of retained candidates is not acceptable in
    /// an engine process.
    /// </summary>
    public int MaxGreedyDiversitySelections { get; set; } = 256;

    internal GameAwareMemoryRerankerOptions Snapshot()
    {
        if (ImportanceWeight is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ImportanceWeight));
        }

        if (GameTimeRecencyWeight is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GameTimeRecencyWeight));
        }

        if (RealTimeRecencyWeight is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RealTimeRecencyWeight));
        }

        if (DiversityPenalty is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DiversityPenalty));
        }

        if (MaxGreedyDiversitySelections is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxGreedyDiversitySelections));
        }

        if (RealTimeHalfLife < TimeSpan.FromSeconds(1)
            || RealTimeHalfLife > TimeSpan.FromDays(365_000))
        {
            throw new ArgumentOutOfRangeException(nameof(RealTimeHalfLife));
        }

        return new GameAwareMemoryRerankerOptions
        {
            ImportanceWeight = ImportanceWeight,
            GameTimeRecencyWeight = GameTimeRecencyWeight,
            RealTimeRecencyWeight = RealTimeRecencyWeight,
            RealTimeHalfLife = RealTimeHalfLife,
            DiversityPenalty = DiversityPenalty,
            MaxGreedyDiversitySelections =
                MaxGreedyDiversitySelections
        };
    }
}

/// <summary>
/// Deterministically combines provider score, explicit importance, game-time
/// recency, optional wall-clock recency, and greedy tag/source diversity.
/// </summary>
public sealed class GameAwareMemoryReranker : IMemoryResultReranker
{
    private readonly GameAwareMemoryRerankerOptions _options;

    public GameAwareMemoryReranker(
        GameAwareMemoryRerankerOptions? options = null)
    {
        _options = (options ?? new GameAwareMemoryRerankerOptions())
            .Snapshot();
    }

    public string RerankerId => "game-aware-memory";

    public string Version => "1";

    public ValueTask<IReadOnlyList<MemorySearchResult>> RerankAsync(
        MemoryQuery query,
        IReadOnlyList<MemorySearchResult> candidates,
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
            .Select(
                (item, index) => new Candidate(
                    item,
                    index,
                    BaseScore(query, item)))
            .ToList();
        var selected = new List<MemorySearchResult>(remaining.Count);
        var greedySelections = Math.Min(
            remaining.Count,
            Math.Max(
                query.MaxResults,
                _options.MaxGreedyDiversitySelections));
        while (remaining.Count > 0
               && selected.Count < greedySelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate? winner = null;
            long winnerScore = long.MinValue;
            foreach (var candidate in remaining)
            {
                var score = candidate.BaseScore
                            - (long)candidate.MaximumDiversityOverlap
                            * _options.DiversityPenalty;
                if (winner is null
                    || score > winnerScore
                    || score == winnerScore
                    && (candidate.InputIndex < winner.InputIndex
                        || candidate.InputIndex == winner.InputIndex
                        && string.CompareOrdinal(
                            candidate.Result.Record.MemoryId,
                            winner.Result.Record.MemoryId) < 0))
                {
                    winner = candidate;
                    winnerScore = score;
                }
            }

            remaining.Remove(winner!);
            selected.Add(
                new MemorySearchResult(
                    winner!.Result.Record,
                    ClampScore(winnerScore)));
            var updateIndex = 0;
            foreach (var candidate in remaining)
            {
                if ((updateIndex++ & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                candidate.MaximumDiversityOverlap = Math.Max(
                    candidate.MaximumDiversityOverlap,
                    DiversityOverlap(
                        candidate.Result.Record,
                        winner.Result.Record));
            }
        }


        foreach (var candidate in remaining
                     .OrderBy(item => item.InputIndex))
        {
            selected.Add(candidate.Result);
        }

        return new ValueTask<IReadOnlyList<MemorySearchResult>>(
            new ReadOnlyCollection<MemorySearchResult>(selected));
    }

    private long BaseScore(MemoryQuery query, MemorySearchResult result)
    {
        var record = result.Record;
        var score = (long)result.Score
                    + (long)record.Importance * _options.ImportanceWeight;
        if (_options.GameTimeRecencyWeight > 0
            && query.GameTime is not null
            && TryGetGameTick(record, query.GameTime, out var tick))
        {
            var distance = query.GameTime.Tick >= tick
                ? query.GameTime.Tick - tick
                : long.MaxValue;
            score += RecencyBonus(
                distance,
                _options.GameTimeRecencyWeight);
        }

        if (_options.RealTimeRecencyWeight > 0)
        {
            var age = query.Now >= record.UpdatedAt
                ? query.Now - record.UpdatedAt
                : TimeSpan.MaxValue;
            var units = age == TimeSpan.MaxValue
                ? long.MaxValue
                : (long)Math.Floor(
                    age.TotalSeconds
                    / _options.RealTimeHalfLife.TotalSeconds);
            score += RecencyBonus(
                units,
                _options.RealTimeRecencyWeight);
        }

        return score;
    }

    private static int DiversityOverlap(
        MemoryRecord candidate,
        MemoryRecord selected)
    {
        var overlap = SharedTags(candidate.Tags, selected.Tags);
        if (candidate.Provenance is not null
            && selected.Provenance is not null
            && string.Equals(
                candidate.Provenance.SourceRunId,
                selected.Provenance.SourceRunId,
                StringComparison.Ordinal))
        {
            overlap++;
        }

        return overlap;
    }

    private static int SharedTags(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var count = 0;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            var comparison = string.CompareOrdinal(
                left[leftIndex],
                right[rightIndex]);
            if (comparison == 0)
            {
                count++;
                leftIndex++;
                rightIndex++;
            }
            else if (comparison < 0)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return count;
    }

    private static bool TryGetGameTick(
        MemoryRecord record,
        GameTimePoint query,
        out long tick)
    {
        var point = record.GameTimeWindow?.ValidUntil
                    ?? record.GameTimeWindow?.ValidFrom;
        if (point is not null
            && string.Equals(
                point.ClockId,
                query.ClockId,
                StringComparison.Ordinal)
            && string.Equals(
                point.TimelineId,
                query.TimelineId,
                StringComparison.Ordinal)
            && point.Epoch == query.Epoch)
        {
            tick = point.Tick;
            return true;
        }

        tick = 0;
        return false;
    }

    private static long RecencyBonus(long distance, int weight)
    {
        if (distance < 0 || distance == long.MaxValue)
        {
            return 0;
        }

        return (long)weight * 1_000 / (1 + Math.Min(distance, 1_000_000));
    }

    private static int ClampScore(long value) =>
        value > int.MaxValue
            ? int.MaxValue
            : value < int.MinValue
                ? int.MinValue
                : (int)value;

    private sealed class Candidate
    {
        public Candidate(
            MemorySearchResult result,
            int inputIndex,
            long baseScore)
        {
            Result = result;
            InputIndex = inputIndex;
            BaseScore = baseScore;
        }

        public MemorySearchResult Result { get; }

        public int InputIndex { get; }

        public long BaseScore { get; }

        public int MaximumDiversityOverlap { get; set; }
    }
}
