using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace GameAgent.World;

public sealed class WorldEventPlannerOptions
{
    public WorldEventPlannerOptions(
        int maxDefinitions = 1_024,
        int maxCandidates = 4_096,
        int maxCandidatesPerDefinition = 512,
        int maxParticipantsPerSelection = 1_024,
        int maxParticipantsPerInstance = 256,
        int maxTotalParticipantReferences = 16_384,
        int maxResourceKeysPerInstance = 512,
        int maxCascadeDepth = 16,
        int maxEventsPerExecutionBatch = 256)
    {
        MaxDefinitions = InRange(
            maxDefinitions,
            1,
            16_384,
            nameof(maxDefinitions));
        MaxCandidates = InRange(
            maxCandidates,
            1,
            65_536,
            nameof(maxCandidates));
        MaxCandidatesPerDefinition = InRange(
            maxCandidatesPerDefinition,
            1,
            MaxCandidates,
            nameof(maxCandidatesPerDefinition));
        MaxParticipantsPerSelection = InRange(
            maxParticipantsPerSelection,
            1,
            WorldValidation.MaximumParticipants,
            nameof(maxParticipantsPerSelection));
        MaxParticipantsPerInstance = InRange(
            maxParticipantsPerInstance,
            1,
            MaxParticipantsPerSelection,
            nameof(maxParticipantsPerInstance));
        MaxTotalParticipantReferences = InRange(
            maxTotalParticipantReferences,
            1,
            1_000_000,
            nameof(maxTotalParticipantReferences));
        MaxResourceKeysPerInstance = InRange(
            maxResourceKeysPerInstance,
            1,
            WorldValidation.MaximumResourceKeys,
            nameof(maxResourceKeysPerInstance));
        MaxCascadeDepth = InRange(
            maxCascadeDepth,
            0,
            1_024,
            nameof(maxCascadeDepth));
        MaxEventsPerExecutionBatch = InRange(
            maxEventsPerExecutionBatch,
            1,
            65_536,
            nameof(maxEventsPerExecutionBatch));
    }

    public int MaxDefinitions { get; }

    public int MaxCandidates { get; }

    public int MaxCandidatesPerDefinition { get; }

    public int MaxParticipantsPerSelection { get; }

    public int MaxParticipantsPerInstance { get; }

    public int MaxTotalParticipantReferences { get; }

    public int MaxResourceKeysPerInstance { get; }

    public int MaxCascadeDepth { get; }

    public int MaxEventsPerExecutionBatch { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed class WorldEventPlanningRequest
{
    public WorldEventPlanningRequest(
        WorldEvolutionTrigger trigger,
        IReadOnlyList<WorldEventDefinition> definitions,
        int cascadeDepth = 0,
        string? parentInstanceId = null,
        object? hostContext = null)
    {
        Trigger = trigger
                  ?? throw new ArgumentNullException(nameof(trigger));
        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }

        var copy = WorldValidation.MaterializeBounded(
            definitions,
            16_384,
            nameof(definitions));
        if (copy.Any(item => item is null))
        {
            throw new ArgumentException(
                "Definitions cannot contain null entries.",
                nameof(definitions));
        }

        if (cascadeDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cascadeDepth));
        }

        if (cascadeDepth == 0 && parentInstanceId is not null)
        {
            throw new ArgumentException(
                "A root evolution wave cannot have a parent instance.",
                nameof(parentInstanceId));
        }

        if (cascadeDepth > 0 && parentInstanceId is null)
        {
            throw new ArgumentException(
                "A cascaded evolution wave requires a parent instance.",
                nameof(parentInstanceId));
        }

        Definitions = new ReadOnlyCollection<WorldEventDefinition>(copy);
        CascadeDepth = cascadeDepth;
        ParentInstanceId = WorldValidation.Optional(
            parentInstanceId,
            nameof(parentInstanceId));
        HostContext = hostContext;
    }

    public WorldEvolutionTrigger Trigger { get; }

    public IReadOnlyList<WorldEventDefinition> Definitions { get; }

    public int CascadeDepth { get; }

    public string? ParentInstanceId { get; }

    public object? HostContext { get; }
}

public enum WorldEventEvaluationStatus
{
    TriggerKindMismatch = 0,
    MaximumOccurrencesReached = 1,
    CooldownActive = 2,
    ConditionNotMet = 3,
    AdmissionRejected = 4,
    ResolvedNoCandidates = 5,
    AlreadyRecorded = 6,
    Planned = 7
}

public sealed class WorldEventEvaluation
{
    internal WorldEventEvaluation(
        string definitionId,
        string definitionVersion,
        WorldEventEvaluationStatus status,
        int plannedCount,
        int suppressedCount,
        string? reasonCode = null)
    {
        DefinitionId = definitionId;
        DefinitionVersion = definitionVersion;
        Status = status;
        PlannedCount = plannedCount;
        SuppressedCount = suppressedCount;
        ReasonCode = reasonCode;
    }

    public string DefinitionId { get; }

    public string DefinitionVersion { get; }

    public WorldEventEvaluationStatus Status { get; }

    public int PlannedCount { get; }

    public int SuppressedCount { get; }

    public string? ReasonCode { get; }
}

public sealed class WorldEventInstance
{
    internal WorldEventInstance(
        WorldEventDefinition definition,
        WorldEvolutionTrigger trigger,
        WorldEventResolution resolution,
        int cascadeDepth,
        string? parentInstanceId,
        IReadOnlyList<string> readResourceKeys,
        IReadOnlyList<string> writeResourceKeys)
    {
        Definition = definition;
        Trigger = trigger;
        ResolutionKey = resolution.ResolutionKey;
        Participants = resolution.Participants;
        Parameters = resolution.Parameters;
        ReadResourceKeys = readResourceKeys;
        WriteResourceKeys = writeResourceKeys;
        CascadeDepth = cascadeDepth;
        ParentInstanceId = parentInstanceId;
        InstanceId = WorldEventIdentity.ComputeInstanceId(this);
        PlanFingerprint = WorldEventIdentity.ComputePlanFingerprint(this);
    }

    public string InstanceId { get; }

    public string PlanFingerprint { get; }

    public WorldEventDefinition Definition { get; }

    public WorldEvolutionTrigger Trigger { get; }

    public string WorldId => Trigger.WorldId;

    public string TimelineId => Trigger.TimelineId;

    public long TimelineEpoch => Trigger.TimelineEpoch;

    public string TriggerId => Trigger.TriggerId;

    public string DefinitionId => Definition.DefinitionId;

    public string DefinitionVersion => Definition.Version;

    public int Priority => Definition.Priority;

    public string ResolutionKey { get; }

    public IReadOnlyList<WorldEventParticipant> Participants { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public int CascadeDepth { get; }

    public string? ParentInstanceId { get; }

    public GameAgent.Core.GameTimePoint? OccurredAt => Trigger.GameTime;
}

public sealed class WorldEventPlan
{
    internal WorldEventPlan(
        WorldEvolutionTrigger trigger,
        int cascadeDepth,
        IReadOnlyList<WorldEventInstance> instances,
        IReadOnlyList<WorldEventExecutionBatch> executionBatches,
        IReadOnlyList<WorldEventEvaluation> evaluations,
        WorldStateFence? admissionFence = null)
    {
        Trigger = trigger;
        CascadeDepth = cascadeDepth;
        Instances = instances;
        ExecutionBatches = executionBatches;
        Evaluations = evaluations;
        AdmissionFence = admissionFence;
    }

    public WorldEvolutionTrigger Trigger { get; }

    public int CascadeDepth { get; }

    public IReadOnlyList<WorldEventInstance> Instances { get; }

    public IReadOnlyList<WorldEventExecutionBatch> ExecutionBatches { get; }

    public IReadOnlyList<WorldEventEvaluation> Evaluations { get; }

    /// <summary>
    /// Exact state from which an engine-facing facade admitted this plan.
    /// Plans produced directly by the low-level planner are intentionally
    /// unbound and cannot enter the built-in authoritative executor.
    /// </summary>
    public WorldStateFence? AdmissionFence { get; }

    internal WorldEventPlan WithAdmissionFence(WorldStateFence fence)
    {
        if (fence is null)
        {
            throw new ArgumentNullException(nameof(fence));
        }

        if (!string.Equals(
                Trigger.WorldId,
                fence.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                Trigger.TimelineId,
                fence.TimelineId,
                StringComparison.Ordinal)
            || Trigger.TimelineEpoch != fence.TimelineEpoch)
        {
            throw new ArgumentException(
                "The plan and admission fence must share one scope.",
                nameof(fence));
        }

        return new WorldEventPlan(
            Trigger,
            CascadeDepth,
            Instances,
            ExecutionBatches,
            Evaluations,
            fence);
    }
}

/// <summary>
/// Evaluates fixed definitions into deterministic event instances. It plans
/// effects but never mutates world state or invokes an agent.
/// </summary>
public sealed class WorldEventPlanner
{
    private readonly IWorldEventHandlerRegistry _handlers;

    private readonly IWorldEventHistory _history;

    private readonly WorldEventPlannerOptions _options;

    public WorldEventPlanner(
        IWorldEventHandlerRegistry handlers,
        IWorldEventHistory history,
        WorldEventPlannerOptions? options = null)
    {
        _handlers = handlers
                    ?? throw new ArgumentNullException(nameof(handlers));
        _history = history
                   ?? throw new ArgumentNullException(nameof(history));
        _options = options ?? new WorldEventPlannerOptions();
    }

    public async ValueTask<WorldEventPlan> PlanAsync(
        WorldEventPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.Definitions.Count > _options.MaxDefinitions)
        {
            throw Limit(
                WorldEvolutionReasonCodes.DefinitionLimitExceeded,
                "The evolution wave exceeds its definition limit.");
        }

        if (request.CascadeDepth > _options.MaxCascadeDepth)
        {
            throw Limit(
                WorldEvolutionReasonCodes.CascadeLimitExceeded,
                "The evolution wave exceeds its cascade-depth limit.");
        }

        var definitions = request.Definitions
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueDefinitions(definitions);

        var instances = new List<WorldEventInstance>();
        var evaluations = new List<WorldEventEvaluation>(
            definitions.Length);
        var totalParticipantReferences = 0;
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    definition.TriggerKind,
                    request.Trigger.Kind,
                    StringComparison.Ordinal))
            {
                evaluations.Add(
                    Evaluation(
                        definition,
                        WorldEventEvaluationStatus.TriggerKindMismatch));
                continue;
            }

            var handlers = ResolveHandlers(definition);
            var historyKey = new WorldEventDefinitionKey(
                request.Trigger.WorldId,
                request.Trigger.TimelineId,
                request.Trigger.TimelineEpoch,
                definition.DefinitionId,
                definition.Version);
            var history = await _history
                .ReadDefinitionAsync(historyKey, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (history is null)
            {
                throw Configuration(
                    WorldEvolutionReasonCodes.InvalidHistory,
                    "The history provider returned a null definition state.");
            }

            if (definition.MaximumOccurrences.HasValue
                && history.OccurrenceCount
                >= definition.MaximumOccurrences.Value)
            {
                evaluations.Add(
                    Evaluation(
                        definition,
                        WorldEventEvaluationStatus
                            .MaximumOccurrencesReached));
                continue;
            }

            if (IsCooldownActive(
                    definition,
                    request.Trigger,
                    history))
            {
                evaluations.Add(
                    Evaluation(
                        definition,
                        WorldEventEvaluationStatus.CooldownActive));
                continue;
            }

            var context = new WorldEventEvaluationContext(
                request.Trigger,
                definition,
                history,
                request.CascadeDepth,
                request.ParentInstanceId,
                request.HostContext);
            if (!await handlers.Condition
                    .EvaluateAsync(context, cancellationToken)
                    .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                evaluations.Add(
                    Evaluation(
                        definition,
                        WorldEventEvaluationStatus.ConditionNotMet));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WorldEventAdmissionDecision? rejected = null;
            foreach (var admission in handlers.Admissions)
            {
                var decision = await admission
                    .EvaluateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (decision is null)
                {
                    throw Configuration(
                        WorldEvolutionReasonCodes.InvalidHandlerResult,
                        "An admission handler returned a null decision.");
                }

                if (!decision.Accepted)
                {
                    rejected = decision;
                    break;
                }
            }

            if (rejected is not null)
            {
                evaluations.Add(
                    new WorldEventEvaluation(
                        definition.DefinitionId,
                        definition.Version,
                        WorldEventEvaluationStatus.AdmissionRejected,
                        plannedCount: 0,
                        suppressedCount: 0,
                        rejected.ReasonCode));
                continue;
            }

            var selected = await handlers.Selector
                .SelectAsync(context, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedSelection = CopyParticipants(
                selected,
                _options.MaxParticipantsPerSelection,
                "participant selector");
            if (definition.MaximumParticipants.HasValue
                && normalizedSelection.Count
                > definition.MaximumParticipants.Value)
            {
                throw Limit(
                    WorldEvolutionReasonCodes.ParticipantLimitExceeded,
                    "A participant selector exceeds the definition limit.");
            }

            ChargeParticipants(
                ref totalParticipantReferences,
                normalizedSelection.Count);
            var resolutions = await handlers.Resolver
                .ResolveAsync(
                    context,
                    normalizedSelection,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolutions is null)
            {
                throw Configuration(
                    WorldEvolutionReasonCodes.InvalidHandlerResult,
                    "An event resolver returned a null collection.");
            }

            var remainingCandidates =
                _options.MaxCandidates - instances.Count;
            var maximumResolutions = Math.Min(
                _options.MaxCandidatesPerDefinition,
                remainingCandidates);
            var boundedResolutions = WorldValidation.MaterializeBounded(
                resolutions,
                maximumResolutions,
                () => Limit(
                    WorldEvolutionReasonCodes.CandidateLimitExceeded,
                    "An event resolver exceeds the candidate limit."));

            if (boundedResolutions.Length == 0)
            {
                evaluations.Add(
                    Evaluation(
                        definition,
                        WorldEventEvaluationStatus.ResolvedNoCandidates));
                continue;
            }

            var selectionKeys = normalizedSelection
                .Select(item => item.StableKey)
                .ToHashSet(StringComparer.Ordinal);
            var orderedResolutions = boundedResolutions
                .Select(
                    resolution => resolution
                                  ?? throw Configuration(
                                      WorldEvolutionReasonCodes
                                          .InvalidHandlerResult,
                                      "An event resolver returned a null "
                                      + "candidate."))
                .OrderBy(
                    resolution => resolution.ResolutionKey,
                    StringComparer.Ordinal)
                .ToArray();
            var definitionInstances = new List<WorldEventInstance>(
                orderedResolutions.Length);
            var seenResolutionKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var recordedSuppressed = 0;
            var occurrenceSuppressed = 0;
            foreach (var resolution in orderedResolutions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenResolutionKeys.Add(resolution.ResolutionKey))
                {
                    throw Configuration(
                        WorldEvolutionReasonCodes.InvalidHandlerResult,
                        "An event resolver returned duplicate resolution keys.");
                }

                if (resolution.Participants.Count
                    > _options.MaxParticipantsPerInstance)
                {
                    throw Limit(
                        WorldEvolutionReasonCodes.ParticipantLimitExceeded,
                        "An event candidate exceeds its participant limit.");
                }

                if (resolution.Participants.Any(
                        participant =>
                            !selectionKeys.Contains(participant.StableKey)))
                {
                    throw Configuration(
                        WorldEvolutionReasonCodes.InvalidHandlerResult,
                        "A resolver introduced an unselected participant.");
                }

                ChargeParticipants(
                    ref totalParticipantReferences,
                    resolution.Participants.Count);
                var writeKeys = MergeKeys(
                    definition.WriteResourceKeys,
                    resolution.WriteResourceKeys);
                var readKeys = MergeKeys(
                    definition.ReadResourceKeys,
                    resolution.ReadResourceKeys,
                    except: writeKeys);
                if (readKeys.Count + writeKeys.Count
                    > _options.MaxResourceKeysPerInstance)
                {
                    throw Limit(
                        WorldEvolutionReasonCodes.ResourceLimitExceeded,
                        "An event candidate exceeds its resource-key limit.");
                }

                if (definition.MaximumOccurrences.HasValue
                    && history.OccurrenceCount
                       + definitionInstances.Count
                    >= definition.MaximumOccurrences.Value)
                {
                    occurrenceSuppressed++;
                    continue;
                }

                var instance = new WorldEventInstance(
                    definition,
                    request.Trigger,
                    resolution,
                    request.CascadeDepth,
                    request.ParentInstanceId,
                    readKeys,
                    writeKeys);
                var recorded = await _history
                    .FindInstanceAsync(
                        instance.InstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (recorded is not null)
                {
                    var proposed =
                        WorldEventHistoryRecord.FromInstance(instance);
                    if (!recorded.IsEquivalentTo(proposed))
                    {
                        throw Configuration(
                            WorldEvolutionReasonCodes.InvalidHistory,
                            "Recorded instance identity conflicts with "
                            + "the planned event.");
                    }

                    recordedSuppressed++;
                    continue;
                }

                definitionInstances.Add(instance);
            }

            instances.AddRange(definitionInstances);
            evaluations.Add(
                new WorldEventEvaluation(
                    definition.DefinitionId,
                    definition.Version,
                    definitionInstances.Count > 0
                        ? WorldEventEvaluationStatus.Planned
                        : recordedSuppressed > 0
                            ? WorldEventEvaluationStatus.AlreadyRecorded
                            : WorldEventEvaluationStatus
                                .MaximumOccurrencesReached,
                    definitionInstances.Count,
                    recordedSuppressed + occurrenceSuppressed));
        }

        var ordered = instances
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.DefinitionVersion, StringComparer.Ordinal)
            .ThenBy(item => item.ResolutionKey, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var batches = WorldEventConflictBatchPlanner.Plan(
            ordered,
            _options.MaxEventsPerExecutionBatch,
            _options.MaxCandidates);
        return new WorldEventPlan(
            request.Trigger,
            request.CascadeDepth,
            new ReadOnlyCollection<WorldEventInstance>(ordered),
            batches,
            new ReadOnlyCollection<WorldEventEvaluation>(
                evaluations.ToArray()));
    }

    private static void EnsureUniqueDefinitions(
        IReadOnlyList<WorldEventDefinition> definitions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var key = string.Concat(
                definition.DefinitionId,
                "\u001f",
                definition.Version);
            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    "Definitions must be unique by identifier and version.");
            }
        }
    }

    private HandlerSet ResolveHandlers(WorldEventDefinition definition)
    {
        if (!_handlers.TryGetCondition(
                definition.ConditionHandlerId,
                out var condition)
            || condition is null
            || !_handlers.TryGetParticipantSelector(
                definition.ParticipantSelectorId,
                out var selector)
            || selector is null
            || !_handlers.TryGetResolver(
                definition.ResolverId,
                out var resolver)
            || resolver is null
            || !_handlers.TryGetEffect(
                definition.EffectHandlerId,
                out var effect)
            || effect is null)
        {
            throw Configuration(
                WorldEvolutionReasonCodes.MissingHandler,
                "A fixed event references an unregistered host handler.");
        }

        var admissions = new List<IWorldEventAdmissionHandler>(
            definition.AdmissionHandlerIds.Count);
        foreach (var handlerId in definition.AdmissionHandlerIds)
        {
            if (!_handlers.TryGetAdmission(handlerId, out var admission)
                || admission is null)
            {
                throw Configuration(
                    WorldEvolutionReasonCodes.MissingHandler,
                    "A fixed event references an unregistered "
                    + "admission handler.");
            }

            admissions.Add(admission);
        }

        return new HandlerSet(
            condition,
            new ReadOnlyCollection<IWorldEventAdmissionHandler>(
                admissions),
            selector,
            resolver,
            effect);
    }

    private static bool IsCooldownActive(
        WorldEventDefinition definition,
        WorldEvolutionTrigger trigger,
        WorldEventDefinitionHistory history)
    {
        if (definition.Cooldown is null)
        {
            return false;
        }

        if (trigger.GameTime is null)
        {
            throw Configuration(
                WorldEvolutionReasonCodes.InvalidHandlerResult,
                "A cooldown requires a game-time-bearing trigger.");
        }

        if (history.OccurrenceCount == 0)
        {
            return false;
        }

        var previous = history.LastOccurredAt;
        if (previous is null
            || !previous.IsComparableTo(trigger.GameTime))
        {
            throw Configuration(
                WorldEvolutionReasonCodes.InvalidHistory,
                "Cooldown history uses an incompatible game-time coordinate.");
        }

        if (trigger.GameTime.Tick < previous.Tick)
        {
            throw Configuration(
                WorldEvolutionReasonCodes.InvalidHistory,
                "Cooldown history is ahead of the current trigger.");
        }

        return previous.Tick
               > long.MaxValue - definition.Cooldown.MinimumTicks
               || trigger.GameTime.Tick
               < previous.Tick + definition.Cooldown.MinimumTicks;
    }

    private static IReadOnlyList<WorldEventParticipant> CopyParticipants(
        IReadOnlyList<WorldEventParticipant>? source,
        int maximum,
        string handlerKind)
    {
        if (source is null)
        {
            throw Configuration(
                WorldEvolutionReasonCodes.InvalidHandlerResult,
                "The " + handlerKind + " returned a null collection.");
        }

        var bounded = WorldValidation.MaterializeBounded(
            source,
            maximum,
            () => Limit(
                WorldEvolutionReasonCodes.ParticipantLimitExceeded,
                "The " + handlerKind
                + " exceeds its participant limit."));
        var copy = bounded
            .Select(
                item => item
                        ?? throw Configuration(
                            WorldEvolutionReasonCodes.InvalidHandlerResult,
                            "The " + handlerKind
                            + " returned a null participant."))
            .OrderBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].StableKey,
                    copy[index].StableKey,
                    StringComparison.Ordinal))
            {
                throw Configuration(
                    WorldEvolutionReasonCodes.InvalidHandlerResult,
                    "The " + handlerKind
                    + " returned duplicate participants.");
            }
        }

        return new ReadOnlyCollection<WorldEventParticipant>(copy);
    }

    private static IReadOnlyList<string> MergeKeys(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        IReadOnlyList<string>? except = null)
    {
        var values = new SortedSet<string>(left, StringComparer.Ordinal);
        values.UnionWith(right);
        if (except is not null)
        {
            values.ExceptWith(except);
        }

        return new ReadOnlyCollection<string>(values.ToArray());
    }

    private void ChargeParticipants(ref int total, int count)
    {
        if (count > _options.MaxTotalParticipantReferences - total)
        {
            throw Limit(
                WorldEvolutionReasonCodes.ParticipantLimitExceeded,
                "The evolution wave exceeds its total participant limit.");
        }

        total += count;
    }

    private static WorldEventEvaluation Evaluation(
        WorldEventDefinition definition,
        WorldEventEvaluationStatus status)
    {
        return new WorldEventEvaluation(
            definition.DefinitionId,
            definition.Version,
            status,
            plannedCount: 0,
            suppressedCount: 0);
    }

    private static WorldEvolutionLimitException Limit(
        string reasonCode,
        string message)
    {
        return new WorldEvolutionLimitException(reasonCode, message);
    }

    private static WorldEventConfigurationException Configuration(
        string reasonCode,
        string message)
    {
        return new WorldEventConfigurationException(reasonCode, message);
    }

    private sealed class HandlerSet
    {
        public HandlerSet(
            IWorldEventCondition condition,
            IReadOnlyList<IWorldEventAdmissionHandler> admissions,
            IWorldEventParticipantSelector selector,
            IWorldEventResolver resolver,
            IWorldEventEffectHandler effect)
        {
            Condition = condition;
            Admissions = admissions;
            Selector = selector;
            Resolver = resolver;
            Effect = effect;
        }

        public IWorldEventCondition Condition { get; }

        public IReadOnlyList<IWorldEventAdmissionHandler> Admissions { get; }

        public IWorldEventParticipantSelector Selector { get; }

        public IWorldEventResolver Resolver { get; }

        public IWorldEventEffectHandler Effect { get; }
    }
}

internal static class WorldEventIdentity
{
    public static string ComputeInstanceId(WorldEventInstance instance)
    {
        return "evt_" + Hash(
            writer =>
            {
                Write(writer, "world-event-instance-v1");
                Write(writer, instance.WorldId);
                Write(writer, instance.TimelineId);
                writer.Write(instance.TimelineEpoch);
                Write(writer, instance.TriggerId);
                Write(writer, instance.Trigger.Kind);
                WriteTime(writer, instance.OccurredAt);
                Write(writer, instance.Trigger.PayloadDigest);
                Write(writer, instance.DefinitionId);
                Write(writer, instance.DefinitionVersion);
                Write(writer, instance.ResolutionKey);
                writer.Write(instance.CascadeDepth);
                Write(writer, instance.ParentInstanceId);
            });
    }

    public static string ComputePlanFingerprint(WorldEventInstance instance)
    {
        return Hash(
            writer =>
            {
                Write(writer, "world-event-plan-v1");
                Write(writer, instance.InstanceId);
                writer.Write(instance.Priority);
                Write(writer, instance.Definition.TriggerKind);
                Write(writer, instance.Definition.ConditionHandlerId);
                WriteStrings(
                    writer,
                    instance.Definition.AdmissionHandlerIds);
                writer.Write(instance.Definition.Attributes.Count);
                foreach (var pair in instance.Definition.Attributes.OrderBy(
                             item => item.Key,
                             StringComparer.Ordinal))
                {
                    Write(writer, pair.Key);
                    Write(writer, pair.Value);
                }
                Write(writer, instance.Definition.ParticipantSelectorId);
                Write(writer, instance.Definition.ResolverId);
                Write(writer, instance.Definition.EffectHandlerId);
                writer.Write(
                    (int)instance.Definition.AgentInvocationPolicy);
                WriteParticipants(writer, instance.Participants);
                WriteStrings(writer, instance.ReadResourceKeys);
                WriteStrings(writer, instance.WriteResourceKeys);
                writer.Write(instance.Parameters.Count);
                foreach (var pair in instance.Parameters.OrderBy(
                             item => item.Key,
                             StringComparer.Ordinal))
                {
                    Write(writer, pair.Key);
                    Write(writer, pair.Value);
                }
            });
    }

    private static string Hash(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(
                   buffer,
                   new UTF8Encoding(
                       encoderShouldEmitUTF8Identifier: false,
                       throwOnInvalidBytes: true),
                   leaveOpen: true))
        {
            write(writer);
        }

        using var sha256 = SHA256.Create();
        var digest = sha256.ComputeHash(buffer.ToArray());
        var text = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            _ = text.Append(value.ToString("x2"));
        }

        return text.ToString();
    }

    private static void Write(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static void WriteStrings(
        BinaryWriter writer,
        IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            Write(writer, value);
        }
    }

    private static void WriteParticipants(
        BinaryWriter writer,
        IReadOnlyList<WorldEventParticipant> participants)
    {
        writer.Write(participants.Count);
        foreach (var participant in participants)
        {
            Write(writer, participant.Role);
            Write(writer, participant.EntityId);
            writer.Write(participant.Incarnation);
        }
    }

    private static void WriteTime(
        BinaryWriter writer,
        GameAgent.Core.GameTimePoint? value)
    {
        writer.Write(value is not null);
        if (value is null)
        {
            return;
        }

        Write(writer, value.ClockId);
        Write(writer, value.TimelineId);
        writer.Write(value.Epoch);
        writer.Write(value.Tick);
    }
}
