using GameAgent.Core;

namespace GameAgent.World;

public sealed class WorldEventDefinitionKey
{
    public WorldEventDefinitionKey(
        string worldId,
        string timelineId,
        long timelineEpoch,
        string definitionId,
        string definitionVersion)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        DefinitionId = WorldValidation.Required(
            definitionId,
            nameof(definitionId));
        DefinitionVersion = WorldValidation.Required(
            definitionVersion,
            nameof(definitionVersion),
            96);
        TimelineEpoch = timelineEpoch;
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public string DefinitionId { get; }

    public string DefinitionVersion { get; }

    internal string StableKey =>
        WorldValidation.ComposeStableKey(
            WorldId,
            TimelineId,
            TimelineEpoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            DefinitionId,
            DefinitionVersion);
}

public sealed class WorldEventDefinitionHistory
{
    public WorldEventDefinitionHistory(
        long occurrenceCount,
        GameTimePoint? lastOccurredAt)
    {
        if (occurrenceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceCount));
        }

        if (occurrenceCount == 0 && lastOccurredAt is not null)
        {
            throw new ArgumentException(
                "An empty history cannot have a last occurrence.",
                nameof(lastOccurredAt));
        }

        OccurrenceCount = occurrenceCount;
        LastOccurredAt = lastOccurredAt;
    }

    public long OccurrenceCount { get; }

    public GameTimePoint? LastOccurredAt { get; }

    public static WorldEventDefinitionHistory Empty { get; } =
        new(0, null);
}

public sealed class WorldEventHistoryRecord
{
    public WorldEventHistoryRecord(
        string instanceId,
        WorldEventDefinitionKey definition,
        string triggerId,
        string resolutionKey,
        string planFingerprint,
        GameTimePoint? occurredAt,
        string? parentInstanceId = null)
    {
        InstanceId = WorldValidation.Required(
            instanceId,
            nameof(instanceId));
        Definition = definition
                     ?? throw new ArgumentNullException(nameof(definition));
        TriggerId = WorldValidation.Required(
            triggerId,
            nameof(triggerId));
        ResolutionKey = WorldValidation.Required(
            resolutionKey,
            nameof(resolutionKey));
        PlanFingerprint = WorldValidation.Required(
            planFingerprint,
            nameof(planFingerprint),
            128);
        ParentInstanceId = WorldValidation.Optional(
            parentInstanceId,
            nameof(parentInstanceId));
        if (occurredAt is not null
            && (!string.Equals(
                    occurredAt.TimelineId,
                    definition.TimelineId,
                    StringComparison.Ordinal)
                || occurredAt.Epoch != definition.TimelineEpoch))
        {
            throw new ArgumentException(
                "Occurrence time must use the definition timeline and epoch.",
                nameof(occurredAt));
        }

        OccurredAt = occurredAt;
    }

    public string InstanceId { get; }

    public WorldEventDefinitionKey Definition { get; }

    public string TriggerId { get; }

    public string ResolutionKey { get; }

    public string PlanFingerprint { get; }

    public GameTimePoint? OccurredAt { get; }

    public string? ParentInstanceId { get; }

    public static WorldEventHistoryRecord FromInstance(
        WorldEventInstance instance)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        return new WorldEventHistoryRecord(
            instance.InstanceId,
            new WorldEventDefinitionKey(
                instance.WorldId,
                instance.TimelineId,
                instance.TimelineEpoch,
                instance.DefinitionId,
                instance.DefinitionVersion),
            instance.TriggerId,
            instance.ResolutionKey,
            instance.PlanFingerprint,
            instance.OccurredAt,
            instance.ParentInstanceId);
    }

    internal bool IsEquivalentTo(WorldEventHistoryRecord other)
    {
        return other is not null
               && string.Equals(
                   InstanceId,
                   other.InstanceId,
                   StringComparison.Ordinal)
               && string.Equals(
                   Definition.StableKey,
                   other.Definition.StableKey,
                   StringComparison.Ordinal)
               && string.Equals(
                   TriggerId,
                   other.TriggerId,
                   StringComparison.Ordinal)
               && string.Equals(
                   ResolutionKey,
                   other.ResolutionKey,
                   StringComparison.Ordinal)
               && string.Equals(
                   PlanFingerprint,
                   other.PlanFingerprint,
                   StringComparison.Ordinal)
               && string.Equals(
                   ParentInstanceId,
                   other.ParentInstanceId,
                   StringComparison.Ordinal)
               && SameTime(OccurredAt, other.OccurredAt);
    }

    private static bool SameTime(GameTimePoint? left, GameTimePoint? right)
    {
        return left is null
            ? right is null
            : right is not null
              && string.Equals(
                  left.ClockId,
                  right.ClockId,
                  StringComparison.Ordinal)
              && string.Equals(
                  left.TimelineId,
                  right.TimelineId,
                  StringComparison.Ordinal)
              && left.Epoch == right.Epoch
              && left.Tick == right.Tick;
    }
}

public enum WorldEventHistoryAppendResult
{
    Appended = 0,
    AlreadyExists = 1
}

/// <summary>
/// Durable hosts implement this interface at their own transaction boundary.
/// An append must be atomic by instance identifier and idempotent for an
/// equivalent record.
/// </summary>
public interface IWorldEventHistory
{
    ValueTask<WorldEventDefinitionHistory> ReadDefinitionAsync(
        WorldEventDefinitionKey definition,
        CancellationToken cancellationToken);

    ValueTask<WorldEventHistoryRecord?> FindInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldEventHistoryAppendResult> TryAppendAsync(
        WorldEventHistoryRecord record,
        CancellationToken cancellationToken);
}

/// <summary>
/// Process-local reference implementation for tests and non-durable hosts.
/// Its contents are not persisted across process restarts.
/// </summary>
public sealed class InMemoryWorldEventHistory : IWorldEventHistory
{
    private readonly object _sync = new();

    private readonly Dictionary<string, WorldEventHistoryRecord> _instances =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, DefinitionState> _definitions =
        new(StringComparer.Ordinal);

    public ValueTask<WorldEventDefinitionHistory> ReadDefinitionAsync(
        WorldEventDefinitionKey definition,
        CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_definitions.TryGetValue(
                    definition.StableKey,
                    out var state))
            {
                return new ValueTask<WorldEventDefinitionHistory>(
                    WorldEventDefinitionHistory.Empty);
            }

            return new ValueTask<WorldEventDefinitionHistory>(
                new WorldEventDefinitionHistory(
                    state.OccurrenceCount,
                    state.LastOccurredAt));
        }
    }

    public ValueTask<WorldEventHistoryRecord?> FindInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var normalized = WorldValidation.Required(
            instanceId,
            nameof(instanceId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _instances.TryGetValue(normalized, out var record);
            return new ValueTask<WorldEventHistoryRecord?>(record);
        }
    }

    public ValueTask<WorldEventHistoryAppendResult> TryAppendAsync(
        WorldEventHistoryRecord record,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_instances.TryGetValue(record.InstanceId, out var existing))
            {
                if (!existing.IsEquivalentTo(record))
                {
                    throw new WorldEventConfigurationException(
                        WorldEvolutionReasonCodes.InvalidHistory,
                        "An instance identifier maps to conflicting history.");
                }

                return new ValueTask<WorldEventHistoryAppendResult>(
                    WorldEventHistoryAppendResult.AlreadyExists);
            }

            var hasState = _definitions.TryGetValue(
                record.Definition.StableKey,
                out var state);
            state ??= new DefinitionState();
            var nextLastOccurredAt = state.LastOccurredAt;
            if (record.OccurredAt is not null)
            {
                if (nextLastOccurredAt is null)
                {
                    nextLastOccurredAt = record.OccurredAt;
                }
                else if (!nextLastOccurredAt.IsComparableTo(
                             record.OccurredAt))
                {
                    throw new WorldEventConfigurationException(
                        WorldEvolutionReasonCodes.InvalidHistory,
                        "Definition history mixes incompatible game clocks.");
                }
                else if (nextLastOccurredAt.CompareTo(record.OccurredAt) < 0)
                {
                    nextLastOccurredAt = record.OccurredAt;
                }
            }

            long nextOccurrenceCount;
            checked
            {
                nextOccurrenceCount = state.OccurrenceCount + 1;
            }

            _instances.Add(record.InstanceId, record);
            if (!hasState)
            {
                _definitions.Add(record.Definition.StableKey, state);
            }

            state.OccurrenceCount = nextOccurrenceCount;
            state.LastOccurredAt = nextLastOccurredAt;
            return new ValueTask<WorldEventHistoryAppendResult>(
                WorldEventHistoryAppendResult.Appended);
        }
    }

    private sealed class DefinitionState
    {
        public long OccurrenceCount { get; set; }

        public GameTimePoint? LastOccurredAt { get; set; }
    }
}
