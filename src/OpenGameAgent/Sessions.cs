using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public readonly struct GameSessionKey : IEquatable<GameSessionKey>
{
    public GameSessionKey(string sessionId, string actorId)
    {
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ActorId = GameJson.RequireId(actorId, nameof(actorId));
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public bool Equals(GameSessionKey other) =>
        string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
        && string.Equals(ActorId, other.ActorId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameSessionKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((SessionId is null ? 0 : StringComparer.Ordinal.GetHashCode(SessionId)) * 397)
                ^ (ActorId is null ? 0 : StringComparer.Ordinal.GetHashCode(ActorId));
        }
    }

    public override string ToString() => (SessionId ?? string.Empty) + ":" + (ActorId ?? string.Empty);

    public static bool operator ==(GameSessionKey left, GameSessionKey right) => left.Equals(right);

    public static bool operator !=(GameSessionKey left, GameSessionKey right) => !left.Equals(right);

    internal GameSessionKey EnsureValid(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(SessionId) || string.IsNullOrWhiteSpace(ActorId))
        {
            throw new ArgumentException("A valid game session key is required.", parameterName);
        }

        return this;
    }
}

public enum GameSessionUsageCause
{
    Assistant = 0,
    Tool = 1,
    Compaction = 2,
    BranchSummary = 3,
    DeferredFetch = 4,
    Hook = 5,
    Adjustment = 6,
}

public sealed class GameSessionUsageRecord
{
    public GameSessionUsageRecord(
        string recordId,
        GameSessionUsageCause cause,
        ModelUsage usage,
        string? runId = null,
        string? inputId = null,
        string? detailsJson = null)
    {
        if (!Enum.IsDefined(typeof(GameSessionUsageCause), cause))
        {
            throw new ArgumentOutOfRangeException(nameof(cause));
        }

        RecordId = GameJson.RequireId(recordId, nameof(recordId));
        Cause = cause;
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        RunId = runId is null ? null : GameJson.RequireId(runId, nameof(runId));
        InputId = inputId is null ? null : GameJson.RequireId(inputId, nameof(inputId));
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
    }

    public string RecordId { get; }

    public GameSessionUsageCause Cause { get; }

    public ModelUsage Usage { get; }

    public string? RunId { get; }

    public string? InputId { get; }

    public string? DetailsJson { get; }

    internal static bool ValueEquals(GameSessionUsageRecord left, GameSessionUsageRecord right) =>
        string.Equals(left.RecordId, right.RecordId, StringComparison.Ordinal)
        && left.Cause == right.Cause
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
        && string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal)
        && UsageEquals(left.Usage, right.Usage);

    private static bool UsageEquals(ModelUsage left, ModelUsage right) =>
        left.InputTokens == right.InputTokens
        && left.OutputTokens == right.OutputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.ReasoningTokens == right.ReasoningTokens
        && left.CacheWriteOneHourTokens == right.CacheWriteOneHourTokens
        && left.Cost.IsKnown == right.Cost.IsKnown
        && left.Cost.Input.Equals(right.Cost.Input)
        && left.Cost.Output.Equals(right.Cost.Output)
        && left.Cost.CacheRead.Equals(right.Cost.CacheRead)
        && left.Cost.CacheWrite.Equals(right.Cost.CacheWrite);
}

public sealed class GameSessionUsageTotals
{
    private static readonly GameSessionUsageTotals EmptyValue = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        costKnown: true);

    public GameSessionUsageTotals(
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long reasoningTokens,
        long cacheWriteOneHourTokens,
        double inputCost,
        double outputCost,
        double cacheReadCost,
        double cacheWriteCost,
        bool costKnown = true)
    {
        if (inputTokens < 0
            || outputTokens < 0
            || cacheReadTokens < 0
            || cacheWriteTokens < 0
            || reasoningTokens < 0
            || reasoningTokens > outputTokens
            || cacheWriteOneHourTokens < 0
            || cacheWriteOneHourTokens > cacheWriteTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Cumulative token counts are invalid.");
        }

        RequireCost(inputCost, nameof(inputCost));
        RequireCost(outputCost, nameof(outputCost));
        RequireCost(cacheReadCost, nameof(cacheReadCost));
        RequireCost(cacheWriteCost, nameof(cacheWriteCost));
        _ = checked(inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        ReasoningTokens = reasoningTokens;
        CacheWriteOneHourTokens = cacheWriteOneHourTokens;
        InputCost = inputCost;
        OutputCost = outputCost;
        CacheReadCost = cacheReadCost;
        CacheWriteCost = cacheWriteCost;
        CostKnown = costKnown;
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long CacheReadTokens { get; }

    public long CacheWriteTokens { get; }

    public long ReasoningTokens { get; }

    public long CacheWriteOneHourTokens { get; }

    public double InputCost { get; }

    public double OutputCost { get; }

    public double CacheReadCost { get; }

    public double CacheWriteCost { get; }

    public bool CostKnown { get; }

    public long TotalTokens => checked(InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens);

    public double CostTotal => InputCost + OutputCost + CacheReadCost + CacheWriteCost;

    public double? CostTotalIfKnown => CostKnown ? CostTotal : null;

    internal static GameSessionUsageTotals Empty => EmptyValue;

    internal static GameSessionUsageTotals Add(GameSessionUsageTotals left, GameSessionUsageTotals right) => new(
        checked(left.InputTokens + right.InputTokens),
        checked(left.OutputTokens + right.OutputTokens),
        checked(left.CacheReadTokens + right.CacheReadTokens),
        checked(left.CacheWriteTokens + right.CacheWriteTokens),
        checked(left.ReasoningTokens + right.ReasoningTokens),
        checked(left.CacheWriteOneHourTokens + right.CacheWriteOneHourTokens),
        AddCost(left.InputCost, right.InputCost),
        AddCost(left.OutputCost, right.OutputCost),
        AddCost(left.CacheReadCost, right.CacheReadCost),
        AddCost(left.CacheWriteCost, right.CacheWriteCost),
        left.CostKnown && right.CostKnown);

    internal static bool AtLeast(GameSessionUsageTotals candidate, GameSessionUsageTotals previous) =>
        candidate.InputTokens >= previous.InputTokens
        && candidate.OutputTokens >= previous.OutputTokens
        && candidate.CacheReadTokens >= previous.CacheReadTokens
        && candidate.CacheWriteTokens >= previous.CacheWriteTokens
        && candidate.ReasoningTokens >= previous.ReasoningTokens
        && candidate.CacheWriteOneHourTokens >= previous.CacheWriteOneHourTokens
        && (previous.CostKnown || !candidate.CostKnown)
        && candidate.InputCost >= previous.InputCost
        && candidate.OutputCost >= previous.OutputCost
        && candidate.CacheReadCost >= previous.CacheReadCost
        && candidate.CacheWriteCost >= previous.CacheWriteCost;

    internal static bool ValueEquals(GameSessionUsageTotals left, GameSessionUsageTotals right) =>
        left.InputTokens == right.InputTokens
        && left.OutputTokens == right.OutputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.ReasoningTokens == right.ReasoningTokens
        && left.CacheWriteOneHourTokens == right.CacheWriteOneHourTokens
        && left.CostKnown == right.CostKnown
        && left.InputCost.Equals(right.InputCost)
        && left.OutputCost.Equals(right.OutputCost)
        && left.CacheReadCost.Equals(right.CacheReadCost)
        && left.CacheWriteCost.Equals(right.CacheWriteCost);

    internal static GameSessionUsageTotals Aggregate(IEnumerable<GameSessionUsageRecord> records)
    {
        var inputTokens = 0L;
        var outputTokens = 0L;
        var cacheReadTokens = 0L;
        var cacheWriteTokens = 0L;
        var reasoningTokens = 0L;
        var cacheWriteOneHourTokens = 0L;
        var inputCost = 0d;
        var outputCost = 0d;
        var cacheReadCost = 0d;
        var cacheWriteCost = 0d;
        var costKnown = true;
        foreach (var record in records)
        {
            var usage = record.Usage;
            inputTokens = checked(inputTokens + usage.InputTokens);
            outputTokens = checked(outputTokens + usage.OutputTokens);
            cacheReadTokens = checked(cacheReadTokens + usage.CacheReadTokens);
            cacheWriteTokens = checked(cacheWriteTokens + usage.CacheWriteTokens);
            reasoningTokens = checked(reasoningTokens + (usage.ReasoningTokens ?? 0));
            cacheWriteOneHourTokens = checked(cacheWriteOneHourTokens + (usage.CacheWriteOneHourTokens ?? 0));
            inputCost = AddCost(inputCost, usage.Cost.Input);
            outputCost = AddCost(outputCost, usage.Cost.Output);
            cacheReadCost = AddCost(cacheReadCost, usage.Cost.CacheRead);
            cacheWriteCost = AddCost(cacheWriteCost, usage.Cost.CacheWrite);
            costKnown &= usage.Cost.IsKnown;
        }

        return new GameSessionUsageTotals(
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            reasoningTokens,
            cacheWriteOneHourTokens,
            inputCost,
            outputCost,
            cacheReadCost,
            cacheWriteCost,
            costKnown);
    }

    private static double AddCost(double left, double right)
    {
        var result = left + right;
        return double.IsNaN(result) || double.IsInfinity(result)
            ? throw new OverflowException("The cumulative model cost is too large.")
            : result;
    }

    private static void RequireCost(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, "Cumulative costs must be finite and non-negative.");
        }
    }
}

public sealed class GameSessionUsageStats
{
    private readonly IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> _byCause;

    internal GameSessionUsageStats(
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> totalsByCause)
    {
        Total = totalsByCause.Values.Aggregate(
            GameSessionUsageTotals.Empty,
            GameSessionUsageTotals.Add);
        _byCause = new ReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals>(
            new Dictionary<GameSessionUsageCause, GameSessionUsageTotals>(totalsByCause));
    }

    public GameSessionUsageTotals Total { get; }

    public long CachedTokens => Total.CacheReadTokens;

    public long UncachedTokens => checked(Total.InputTokens + Total.CacheWriteTokens);

    public long TotalTokens => Total.TotalTokens;

    public double CostTotal => Total.CostTotal;

    public GameSessionUsageTotals ForCause(GameSessionUsageCause cause)
    {
        if (!Enum.IsDefined(typeof(GameSessionUsageCause), cause))
        {
            throw new ArgumentOutOfRangeException(nameof(cause));
        }

        return _byCause.TryGetValue(cause, out var totals) ? totals : GameSessionUsageTotals.Empty;
    }
}

public sealed class GameSessionUsageLedger
{
    public const int DefaultRecentRecordCapacity = 256;
    private const int MaximumRecentRecordCapacity = 16_384;
    private readonly IReadOnlyDictionary<string, GameSessionUsageRecord> _byId;
    private readonly IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> _totalsByCause;

    public GameSessionUsageLedger(
        IEnumerable<GameSessionUsageRecord>? records = null,
        int recentRecordCapacity = DefaultRecentRecordCapacity)
        : this(PrepareInitial(records, recentRecordCapacity), recentRecordCapacity)
    {
    }

    private GameSessionUsageLedger(PreparedLedger prepared, int recentRecordCapacity)
        : this(
            prepared.RecentRecords,
            prepared.TotalsByCause,
            prepared.TotalRecordCount,
            recentRecordCapacity)
    {
    }

    private GameSessionUsageLedger(
        IReadOnlyList<GameSessionUsageRecord> recentRecords,
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals>? totalsByCause,
        long? totalRecordCount,
        int recentRecordCapacity)
    {
        ValidateCapacity(recentRecordCapacity);
        var copied = (recentRecords ?? throw new ArgumentNullException(nameof(recentRecords))).ToArray();
        if (copied.Any(record => record is null))
        {
            throw new ArgumentException("A usage ledger cannot contain null records.", nameof(recentRecords));
        }

        if (copied.Length > recentRecordCapacity)
        {
            throw new ArgumentException("The recent usage record window exceeds its configured capacity.", nameof(recentRecords));
        }

        var byId = new Dictionary<string, GameSessionUsageRecord>(StringComparer.Ordinal);
        foreach (var record in copied)
        {
            if (!byId.TryAdd(record.RecordId, record))
            {
                throw new ArgumentException($"Duplicate usage record ID '{record.RecordId}'.", nameof(recentRecords));
            }
        }

        var recentTotals = AggregateByCause(copied);
        var cumulative = totalsByCause is null
            ? recentTotals
            : CopyTotals(totalsByCause);
        foreach (var pair in recentTotals)
        {
            if (!cumulative.TryGetValue(pair.Key, out var total)
                || !GameSessionUsageTotals.AtLeast(total, pair.Value))
            {
                throw new ArgumentException(
                    $"Cumulative usage for '{pair.Key}' cannot be smaller than its retained records.",
                    nameof(totalsByCause));
            }
        }

        var count = totalRecordCount ?? copied.LongLength;
        if (count < copied.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRecordCount));
        }

        Records = Array.AsReadOnly(copied);
        _byId = new ReadOnlyDictionary<string, GameSessionUsageRecord>(byId);
        _totalsByCause = new ReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals>(cumulative);
        RecentRecordCapacity = recentRecordCapacity;
        TotalRecordCount = count;
        Stats = new GameSessionUsageStats(_totalsByCause);
    }

    /// <summary>
    /// Bounded recent records used for idempotent CAS replay and near-term audit. Historical usage is
    /// folded into cumulative per-cause totals, so snapshot size does not grow with session lifetime.
    /// </summary>
    public IReadOnlyList<GameSessionUsageRecord> Records { get; }

    public int RecentRecordCapacity { get; }

    public long TotalRecordCount { get; }

    public IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> TotalsByCause => _totalsByCause;

    public GameSessionUsageStats Stats { get; }

    public static GameSessionUsageLedger Restore(
        IEnumerable<GameSessionUsageRecord>? recentRecords,
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> totalsByCause,
        long totalRecordCount,
        int recentRecordCapacity = DefaultRecentRecordCapacity) => new(
            (recentRecords ?? Array.Empty<GameSessionUsageRecord>()).ToArray(),
            totalsByCause ?? throw new ArgumentNullException(nameof(totalsByCause)),
            totalRecordCount,
            recentRecordCapacity);

    public GameSessionUsageLedger Append(IEnumerable<GameSessionUsageRecord> records)
    {
        if (records is null)
        {
            throw new ArgumentNullException(nameof(records));
        }

        var combined = Records.ToList();
        var byId = Records.ToDictionary(record => record.RecordId, StringComparer.Ordinal);
        var totals = CopyTotals(_totalsByCause);
        var totalRecordCount = TotalRecordCount;
        foreach (var record in records)
        {
            if (record is null)
            {
                throw new ArgumentException("Usage record collections cannot contain null values.", nameof(records));
            }

            if (byId.TryGetValue(record.RecordId, out var existing))
            {
                if (!GameSessionUsageRecord.ValueEquals(existing, record))
                {
                    throw new InvalidOperationException(
                        $"Usage record '{record.RecordId}' was replayed with different content.");
                }

                continue;
            }

            byId.Add(record.RecordId, record);
            combined.Add(record);
            var added = GameSessionUsageTotals.Aggregate(new[] { record });
            totals[record.Cause] = totals.TryGetValue(record.Cause, out var current)
                ? GameSessionUsageTotals.Add(current, added)
                : added;
            totalRecordCount = checked(totalRecordCount + 1);
        }

        if (totalRecordCount == TotalRecordCount)
        {
            return this;
        }

        if (combined.Count > RecentRecordCapacity)
        {
            combined.RemoveRange(0, combined.Count - RecentRecordCapacity);
        }

        return new GameSessionUsageLedger(
            combined,
            totals,
            totalRecordCount,
            RecentRecordCapacity);
    }

    public void EnsureExtends(GameSessionUsageLedger previous)
    {
        if (previous is null)
        {
            throw new ArgumentNullException(nameof(previous));
        }

        if (RecentRecordCapacity != previous.RecentRecordCapacity)
        {
            throw new ArgumentException("A saved session cannot change its usage replay-window capacity.", nameof(previous));
        }

        if (TotalRecordCount < previous.TotalRecordCount)
        {
            throw new ArgumentException("A saved session cannot reduce its cumulative usage record count.", nameof(previous));
        }

        if (TotalRecordCount == previous.TotalRecordCount
            && (Records.Count != previous.Records.Count
                || !Records.Zip(previous.Records, GameSessionUsageRecord.ValueEquals).All(equal => equal)))
        {
            throw new ArgumentException(
                "A saved session cannot change recent usage records without appending usage.",
                nameof(previous));
        }

        foreach (GameSessionUsageCause cause in Enum.GetValues(typeof(GameSessionUsageCause)))
        {
            var candidate = _totalsByCause.TryGetValue(cause, out var candidateValue)
                ? candidateValue
                : GameSessionUsageTotals.Empty;
            var prior = previous._totalsByCause.TryGetValue(cause, out var priorValue)
                ? priorValue
                : GameSessionUsageTotals.Empty;
            if (!GameSessionUsageTotals.AtLeast(candidate, prior))
            {
                throw new ArgumentException(
                    $"A saved session cannot reduce cumulative usage for '{cause}'.",
                    nameof(previous));
            }

            if (TotalRecordCount == previous.TotalRecordCount
                && !GameSessionUsageTotals.ValueEquals(candidate, prior))
            {
                throw new ArgumentException(
                    $"A saved session cannot change cumulative usage for '{cause}' without appending usage.",
                    nameof(previous));
            }
        }

        foreach (var record in previous.Records)
        {
            if (_byId.TryGetValue(record.RecordId, out var retained)
                && !GameSessionUsageRecord.ValueEquals(record, retained))
            {
                throw new ArgumentException(
                    $"A saved session cannot rewrite usage record '{record.RecordId}'.",
                    nameof(previous));
            }
        }

        var retainedPrevious = previous.Records.Where(record => _byId.ContainsKey(record.RecordId)).ToArray();
        if (retainedPrevious.Length > 0)
        {
            var expectedSuffix = previous.Records.Skip(previous.Records.Count - retainedPrevious.Length);
            if (!expectedSuffix.Zip(retainedPrevious, GameSessionUsageRecord.ValueEquals).All(equal => equal)
                || !Records.Take(retainedPrevious.Length)
                    .Zip(retainedPrevious, GameSessionUsageRecord.ValueEquals)
                    .All(equal => equal))
            {
                throw new ArgumentException("Recent usage records must advance as an append-only window.", nameof(previous));
            }
        }
        else if (previous.Records.Count > 0
            && Records.Count < RecentRecordCapacity
            && TotalRecordCount > previous.TotalRecordCount)
        {
            throw new ArgumentException("Recent usage records were removed before the replay window filled.", nameof(previous));
        }

        var visibleNewRecords = Records.Skip(retainedPrevious.Length).ToArray();
        if (TotalRecordCount - previous.TotalRecordCount == visibleNewRecords.LongLength)
        {
            var expectedTotals = CopyTotals(previous._totalsByCause);
            foreach (var pair in AggregateByCause(visibleNewRecords))
            {
                expectedTotals[pair.Key] = expectedTotals.TryGetValue(pair.Key, out var total)
                    ? GameSessionUsageTotals.Add(total, pair.Value)
                    : pair.Value;
            }

            if (!TotalsEqual(expectedTotals, _totalsByCause))
            {
                throw new ArgumentException(
                    "Cumulative usage totals do not match the appended usage records.",
                    nameof(previous));
            }
        }
    }

    private static PreparedLedger PrepareInitial(
        IEnumerable<GameSessionUsageRecord>? records,
        int recentRecordCapacity)
    {
        ValidateCapacity(recentRecordCapacity);
        var copied = (records ?? Array.Empty<GameSessionUsageRecord>()).ToArray();
        if (copied.Any(record => record is null))
        {
            throw new ArgumentException("A usage ledger cannot contain null records.", nameof(records));
        }

        var duplicate = copied
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate usage record ID '{duplicate.Key}'.", nameof(records));
        }

        var recent = copied.Length <= recentRecordCapacity
            ? copied
            : copied.Skip(copied.Length - recentRecordCapacity).ToArray();
        return new PreparedLedger(recent, AggregateByCause(copied), copied.LongLength);
    }

    private static Dictionary<GameSessionUsageCause, GameSessionUsageTotals> AggregateByCause(
        IEnumerable<GameSessionUsageRecord> records) => records
        .GroupBy(record => record.Cause)
        .ToDictionary(group => group.Key, group => GameSessionUsageTotals.Aggregate(group));

    private static Dictionary<GameSessionUsageCause, GameSessionUsageTotals> CopyTotals(
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> source)
    {
        var copy = new Dictionary<GameSessionUsageCause, GameSessionUsageTotals>();
        foreach (var pair in source)
        {
            if (!Enum.IsDefined(typeof(GameSessionUsageCause), pair.Key) || pair.Value is null)
            {
                throw new ArgumentException("Cumulative usage contains an invalid cause or total.", nameof(source));
            }

            copy.Add(pair.Key, pair.Value);
        }

        return copy;
    }

    private static bool TotalsEqual(
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> left,
        IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var total)
            && GameSessionUsageTotals.ValueEquals(pair.Value, total));

    private static void ValidateCapacity(int recentRecordCapacity)
    {
        if (recentRecordCapacity is < 1 or > MaximumRecentRecordCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(recentRecordCapacity));
        }
    }

    private sealed class PreparedLedger
    {
        public PreparedLedger(
            IReadOnlyList<GameSessionUsageRecord> recentRecords,
            IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> totalsByCause,
            long totalRecordCount)
        {
            RecentRecords = recentRecords;
            TotalsByCause = totalsByCause;
            TotalRecordCount = totalRecordCount;
        }

        public IReadOnlyList<GameSessionUsageRecord> RecentRecords { get; }

        public IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> TotalsByCause { get; }

        public long TotalRecordCount { get; }
    }
}

public sealed class GameSessionUsageSnapshot
{
    public GameSessionUsageSnapshot(
        GameSessionKey key,
        long sessionRevision,
        GameSessionUsageLedger ledger)
    {
        Key = key.EnsureValid(nameof(key));
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public GameSessionKey Key { get; }

    public long SessionRevision { get; }

    public GameSessionUsageLedger Ledger { get; }
}

public sealed class GameSessionSnapshot
{
    public GameSessionSnapshot(
        GameSessionKey key,
        long revision,
        IReadOnlyList<AgentMessage>? messages = null,
        IReadOnlyCollection<string>? processedInputIds = null,
        GameMoment? lastMoment = null,
        IReadOnlyDictionary<string, string>? extensionState = null,
        string? pendingInputId = null,
        GameSessionUsageLedger? usageLedger = null)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Key = key.EnsureValid(nameof(key));
        Revision = revision;
        var copiedMessages = (messages ?? Array.Empty<AgentMessage>()).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A session transcript cannot contain null messages.", nameof(messages));
        }

        var copiedInputIds = (processedInputIds ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, nameof(processedInputIds)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Messages = Array.AsReadOnly(copiedMessages);
        ProcessedInputIds = Array.AsReadOnly(copiedInputIds);
        PendingInputId = pendingInputId is null
            ? null
            : GameJson.RequireId(pendingInputId, nameof(pendingInputId));
        if (PendingInputId is not null && ProcessedInputIds.Contains(PendingInputId, StringComparer.Ordinal))
        {
            throw new ArgumentException("A pending input cannot already be marked as processed.", nameof(pendingInputId));
        }

        LastMoment = lastMoment?.EnsureValid(nameof(lastMoment));
        var copiedExtensionState = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in extensionState ?? new Dictionary<string, string>())
        {
            var stateKey = GameJson.RequireId(pair.Key, nameof(extensionState));
            var value = GameJson.RequireValid(pair.Value, nameof(extensionState));
            if (!copiedExtensionState.TryAdd(stateKey, value))
            {
                throw new ArgumentException($"Duplicate extension state key '{stateKey}'.", nameof(extensionState));
            }
        }

        ExtensionState = new ReadOnlyDictionary<string, string>(copiedExtensionState);
        UsageLedger = usageLedger ?? new GameSessionUsageLedger();
    }

    public GameSessionKey Key { get; }

    public long Revision { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public IReadOnlyCollection<string> ProcessedInputIds { get; }

    /// <summary>
    /// Input whose completed tool turns were durably checkpointed but whose agent run has not reached
    /// a terminal commit. Resubmitting the same input resumes after the checkpoint; a different input
    /// is rejected until this one is settled or explicitly repaired by the host.
    /// </summary>
    public string? PendingInputId { get; }

    public GameMoment? LastMoment { get; }

    /// <summary>
    /// Namespaced extension-owned JSON state. It is persisted but never added to model context automatically.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtensionState { get; }

    public GameSessionUsageLedger UsageLedger { get; }
}

public sealed class GameSessionSaveResult
{
    public GameSessionSaveResult(bool saved, GameSessionSnapshot current)
    {
        Saved = saved;
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public bool Saved { get; }

    public GameSessionSnapshot Current { get; }
}

public interface IGameSessionStore
{
    ValueTask<GameSessionSnapshot?> LoadAsync(GameSessionKey key, CancellationToken cancellationToken);

    ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameSessionStore : IGameSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<GameSessionKey, GameSessionSnapshot> _sessions = new();
    private readonly int _capacity;

    public InMemoryGameSessionStore(int capacity = 10_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<GameSessionSnapshot?> LoadAsync(
        GameSessionKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        key.EnsureValid(nameof(key));
        lock (_gate)
        {
            return new ValueTask<GameSessionSnapshot?>(_sessions.TryGetValue(key, out var session) ? Copy(session) : null);
        }
    }

    public ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(snapshot.Key, out var current))
            {
                if (current.Revision != expectedRevision)
                {
                    return new ValueTask<GameSessionSaveResult>(
                        new GameSessionSaveResult(saved: false, Copy(current)));
                }

                snapshot.UsageLedger.EnsureExtends(current.UsageLedger);
            }
            else
            {
                if (expectedRevision != 0)
                {
                    return new ValueTask<GameSessionSaveResult>(
                        new GameSessionSaveResult(
                            saved: false,
                            new GameSessionSnapshot(snapshot.Key, 0)));
                }

                if (_sessions.Count >= _capacity)
                {
                    throw new GameRuntimeLimitException(nameof(_capacity), "The session store reached its capacity.");
                }
            }

            if (snapshot.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A saved snapshot revision must advance by exactly one.", nameof(snapshot));
            }

            var saved = Copy(snapshot);
            _sessions[snapshot.Key] = saved;
            return new ValueTask<GameSessionSaveResult>(new GameSessionSaveResult(saved: true, Copy(saved)));
        }
    }

    private static GameSessionSnapshot Copy(GameSessionSnapshot snapshot) =>
        new(
            snapshot.Key,
            snapshot.Revision,
            snapshot.Messages,
            snapshot.ProcessedInputIds,
            snapshot.LastMoment,
            snapshot.ExtensionState,
            snapshot.PendingInputId,
            snapshot.UsageLedger);
}
