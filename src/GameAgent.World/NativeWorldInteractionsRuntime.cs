using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class NativeWorldPlanningContext
{
    public NativeWorldPlanningContext(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(
            nameof(snapshot));
    }

    public WorldAuthoritativeStateSnapshot Snapshot { get; }
}

/// <summary>
/// Evaluates package-authored interaction availability against one exact
/// immutable state snapshot. Package data cannot replace this snapshot or
/// gain access to engine services.
/// </summary>
public sealed class NativeWorldInteractionAdmissionEvaluator
    : IInteractionAdmissionEvaluator
{
    private readonly ActivatedWorldPackage _package;

    private readonly WorldAuthoritativeStateSnapshot _snapshot;

    private readonly IReadOnlyDictionary<
        string,
        NativeWorldInteractionDefinition> _definitions;

    internal NativeWorldInteractionAdmissionEvaluator(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        _package = package;
        _snapshot = snapshot;
        _definitions =
            new ReadOnlyDictionary<
                string,
                NativeWorldInteractionDefinition>(
                package.NativeInteractions.ToDictionary(
                    item => Key(
                        item.Definition.InteractionId,
                        item.Definition.Version),
                    StringComparer.Ordinal));
    }

    public ValueTask<InteractionAdmissionDecision> EvaluateAsync(
        InteractionAdmissionContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                context.Catalog.Digest,
                _package.CatalogDigest,
                StringComparison.Ordinal)
            || !FenceMatches(context.Request)
            || !_definitions.TryGetValue(
                Key(
                    context.Definition.InteractionId,
                    context.Definition.Version),
                out var native)
            || !string.Equals(
                context.Definition.ContentDigest,
                native.Definition.ContentDigest,
                StringComparison.Ordinal)
            || !IncarnationsMatch(
                _snapshot,
                context.Request.Actor,
                context.Request.Targets))
        {
            return Decision(
                InteractionAvailabilityState.Unavailable,
                InteractiveWorldReasonCodes.StaleState);
        }

        var targetContract =
            context.Definition.Details?.TargetContract;
        var minimumTargets = targetContract?.MinimumTargets ?? 0;
        var maximumTargets = targetContract?.MaximumTargets ?? 0;
        if (context.Request.Targets.Count < minimumTargets
            || context.Request.Targets.Count > maximumTargets)
        {
            return Decision(
                InteractionAvailabilityState.Unavailable,
                InteractionReasonCodes.InvalidTargetCount);
        }

        var (clockId, tick) = CurrentClock();
        var subject = new NativeWorldEvaluationSubject(
            context.Request.Actor,
            "actor");
        var available = NativeWorldConditionEvaluator.Evaluate(
            native.Availability,
            new NativeWorldConditionEvaluationContext(
                _package,
                _snapshot.State,
                clockId,
                tick,
                subject,
                context.Request.Context,
                stateAlreadyValidated: true));
        return available
            ? Decision(
                InteractionAvailabilityState.Available,
                "interaction_available")
            : Decision(
                InteractionAvailabilityState.Unavailable,
                NativeWorldExecutionReasonCodes.ConditionRejected);
    }

    private bool FenceMatches(InteractionQueryRequest request)
    {
        var coordinate = _snapshot.Coordinate;
        return string.Equals(
                   request.WorldId,
                   coordinate.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   request.TimelineId,
                   coordinate.TimelineId,
                   StringComparison.Ordinal)
               && request.TimelineEpoch == coordinate.TimelineEpoch
               && request.SaveRevision == coordinate.SaveRevision
                && string.Equals(
                    request.StateVersion,
                    coordinate.StateVersion.ToString(
                        CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
    }

    private (string ClockId, long Tick) CurrentClock()
    {
        var clock = _package.Clocks.FirstOrDefault();
        if (clock is not null
            && NativeWorldConditionEvaluator.TryReadClock(
                _package,
                _snapshot.State,
                clock.ClockId,
                "__none__",
                0,
                out var tick))
        {
            return (clock.ClockId, tick);
        }

        return ("__none__", 0);
    }

    private static ValueTask<InteractionAdmissionDecision> Decision(
        InteractionAvailabilityState state,
        string reasonCode)
    {
        return new ValueTask<InteractionAdmissionDecision>(
            new InteractionAdmissionDecision(state, reasonCode));
    }

    private static bool IncarnationsMatch(
        WorldAuthoritativeStateSnapshot snapshot,
        GameEntityIdentity actor,
        IReadOnlyList<GameEntityIdentity> targets)
    {
        return new[] { actor }
            .Concat(targets)
            .All(
                identity =>
                    snapshot.TryGetIncarnation(
                        identity.EntityId,
                        out var incarnation)
                    && incarnation == identity.Incarnation);
    }

    private static string Key(string interactionId, string version)
    {
        return WorldValidation.ComposeStableKey(
            interactionId,
            version);
    }
}

internal static class NativeWorldInteractionRuntime
{
    public static NativeWorldInteractionRuntimeBundle Build(
        ActivatedWorldPackage package,
        IReadOnlyList<NativeWorldInteractionDefinition> definitions)
    {
        var handlers = new WorldEventHandlerRegistryBuilder();
        var transactional =
            new WorldTransactionalEventEffectRegistryBuilder();
        foreach (var native in definitions)
        {
            var definition = native.Definition;
            handlers
                .AddCondition(
                    definition.AvailabilityHandlerId,
                    new AvailabilityCondition(package, native))
                .AddAdmission(
                    definition.CostAdmissionHandlerId,
                    new AlwaysAdmission())
                .AddParticipantSelector(
                    definition.ParticipantSelectorId,
                    new InteractionParticipants())
                .AddResolver(
                    definition.ResolverId,
                    new SingleResolution())
                .AddEffect(
                    definition.EffectHandlerId,
                    new PlanningEffect());
            transactional.Add(
                definition.EffectHandlerId,
                new InteractionEffectFactory(package, native));
        }

        return new NativeWorldInteractionRuntimeBundle(
            handlers.Build(),
            transactional.Build());
    }

    private sealed class AvailabilityCondition : IWorldEventCondition
    {
        private readonly ActivatedWorldPackage _package;

        private readonly NativeWorldInteractionDefinition _definition;

        public AvailabilityCondition(
            ActivatedWorldPackage package,
            NativeWorldInteractionDefinition definition)
        {
            _package = package;
            _definition = definition;
        }

        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Trigger is not InteractionExecutionTrigger trigger
                || context.HostContext
                is not NativeWorldPlanningContext planning
                || !FenceMatches(trigger, planning.Snapshot)
                || !DefinitionMatches(trigger, _definition)
                || !string.Equals(
                    planning.Snapshot.Coordinate.CatalogDigest,
                    _package.CatalogDigest,
                    StringComparison.Ordinal)
                || !IncarnationsMatch(
                    planning.Snapshot,
                    trigger.Actor,
                    trigger.Targets))
            {
                return new ValueTask<bool>(false);
            }

            if (!TryCurrentClock(
                    _package,
                    planning.Snapshot.State,
                    trigger.GameTime,
                    out var currentClock))
            {
                return new ValueTask<bool>(false);
            }

            return new ValueTask<bool>(
                NativeWorldConditionEvaluator.Evaluate(
                    _definition.Availability,
                    new NativeWorldConditionEvaluationContext(
                        _package,
                        planning.Snapshot.State,
                        currentClock.ClockId,
                        currentClock.Tick,
                        new NativeWorldEvaluationSubject(
                            trigger.Actor,
                            "actor"),
                        trigger.Payload,
                        stateAlreadyValidated: true)));
        }
    }

    private sealed class AlwaysAdmission : IWorldEventAdmissionHandler
    {
        public ValueTask<WorldEventAdmissionDecision> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventAdmissionDecision>(
                WorldEventAdmissionDecision.Accept());
        }
    }

    private sealed class InteractionParticipants
        : IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Trigger is not InteractionExecutionTrigger trigger)
            {
                return new ValueTask<
                    IReadOnlyList<WorldEventParticipant>>(
                    Array.Empty<WorldEventParticipant>());
            }

            var result = new List<WorldEventParticipant>
            {
                new(
                    trigger.Actor.EntityId,
                    trigger.Actor.Incarnation,
                    "actor")
            };
            for (var index = 0; index < trigger.Targets.Count; index++)
            {
                var target = trigger.Targets[index];
                result.Add(
                    new WorldEventParticipant(
                        target.EntityId,
                        target.Incarnation,
                        "target:"
                        + index.ToString(CultureInfo.InvariantCulture)));
            }

            return new ValueTask<
                IReadOnlyList<WorldEventParticipant>>(
                new ReadOnlyCollection<WorldEventParticipant>(result));
        }
    }

    private sealed class SingleResolution : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventResolution> result = new[]
            {
                new WorldEventResolution(
                    context.Trigger.PayloadDigest
                    ?? context.Trigger.TriggerId,
                    selectedParticipants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(result);
        }
    }

    private sealed class PlanningEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(
                    applied: true,
                    "interaction_planned"));
        }
    }

    private sealed class InteractionEffectFactory
        : IWorldTransactionalEventEffectFactory
    {
        private readonly ActivatedWorldPackage _package;

        private readonly NativeWorldInteractionDefinition _definition;

        public InteractionEffectFactory(
            ActivatedWorldPackage package,
            NativeWorldInteractionDefinition definition)
        {
            _package = package;
            _definition = definition;
        }

        public ValueTask<IWorldTransactionalEventEffect> CreateAsync(
            WorldTransactionalEffectFactoryContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Instance.Trigger
                    is not InteractionExecutionTrigger trigger
                || !DefinitionMatches(trigger, _definition))
            {
                throw new InvalidOperationException(
                    "The native interaction effect requires its exact "
                    + "compiled interaction trigger.");
            }

            return new ValueTask<IWorldTransactionalEventEffect>(
                new InteractionTransactionalEffect(
                    _package,
                    _definition,
                    trigger,
                    context.CommandId,
                    context.OperationId));
        }
    }

    private sealed class InteractionTransactionalEffect
        : IWorldTransactionalEventEffect,
          IWorldTransactionalEffectAdmission
    {
        private readonly ActivatedWorldPackage _package;

        private readonly NativeWorldInteractionDefinition _definition;

        private readonly InteractionExecutionTrigger _trigger;

        public InteractionTransactionalEffect(
            ActivatedWorldPackage package,
            NativeWorldInteractionDefinition definition,
            InteractionExecutionTrigger trigger,
            string commandId,
            string operationId)
        {
            _package = package;
            _definition = definition;
            _trigger = trigger;
            CommandId = commandId;
            OperationId = operationId;
            ExpectedIncarnations =
                new ReadOnlyCollection<
                    WorldEntityIncarnationExpectation>(
                    new[] { trigger.Actor }
                        .Concat(trigger.Targets)
                        .OrderBy(
                            item => item.EntityId,
                            StringComparer.Ordinal)
                        .Select(
                            item =>
                                new WorldEntityIncarnationExpectation(
                                    item.EntityId,
                                    item.Incarnation))
                        .ToArray());
            PayloadDigest = NativeWorldIdentity.Derive(
                    "native.interaction.payload",
                    definition.Digest,
                    trigger.PayloadDigest ?? string.Empty,
                    commandId,
                    operationId)
                .Substring(
                    "native.interaction.payload.".Length);
        }

        public string CommandId { get; }

        public string OperationId { get; }

        public string PayloadDigest { get; }

        public IReadOnlyList<WorldEntityIncarnationExpectation>
            ExpectedIncarnations
        { get; }

        public async ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldTransactionalEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FenceMatches(_trigger, context.Source)
                || !string.Equals(
                    context.Source.Coordinate.CatalogDigest,
                    _package.CatalogDigest,
                    StringComparison.Ordinal))
            {
                return Rejected(
                    NativeWorldExecutionReasonCodes.CatalogMismatch);
            }

            if (!TryCurrentClock(
                    _package,
                    context.Draft.State,
                    _trigger.GameTime,
                    out var currentClock))
            {
                return Rejected(
                    NativeWorldExecutionReasonCodes.ClockStale);
            }

            var subject = new NativeWorldEvaluationSubject(
                _trigger.Actor,
                "actor");
            if (!NativeWorldConditionEvaluator.Evaluate(
                    _definition.Availability,
                    new NativeWorldConditionEvaluationContext(
                        _package,
                        context.Draft.State,
                        currentClock.ClockId,
                        currentClock.Tick,
                        subject,
                        _trigger.Payload,
                        stateAlreadyValidated: true)))
            {
                return Rejected(
                    NativeWorldExecutionReasonCodes.ConditionRejected);
            }

            IReadOnlyList<IWorldMutationIntent> intents;
            try
            {
                intents = NativeWorldDeclarativeRuntime.Materialize(
                    _definition.Effects,
                    subject,
                    _trigger.Targets,
                    context.Instance.InstanceId,
                    context.Draft.EntityIncarnations);
            }
            catch (NativeWorldExecutionException exception)
            {
                return Rejected(exception.ReasonCode);
            }

            return await NativeWorldDeclarativeRuntime.ApplyIntentsAsync(
                    intents,
                    _package,
                    context,
                    CommandId,
                    OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static WorldEventEffectResult Rejected(string reasonCode)
        {
            return new WorldEventEffectResult(
                applied: false,
                reasonCode);
        }
    }

    private static bool FenceMatches(
        InteractionExecutionTrigger trigger,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        return string.Equals(
                   trigger.WorldId,
                   coordinate.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   trigger.TimelineId,
                   coordinate.TimelineId,
                   StringComparison.Ordinal)
               && trigger.TimelineEpoch == coordinate.TimelineEpoch
               && trigger.ExpectedSaveRevision
               == coordinate.SaveRevision
               && string.Equals(
                   trigger.ExpectedStateVersion,
                   coordinate.StateVersion.ToString(
                       CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
               && string.Equals(
                    trigger.CatalogDigest,
                    coordinate.CatalogDigest,
                    StringComparison.Ordinal);
    }

    private static bool DefinitionMatches(
        InteractionExecutionTrigger trigger,
        NativeWorldInteractionDefinition definition)
    {
        if (!string.Equals(
                trigger.InteractionId,
                definition.Definition.InteractionId,
                StringComparison.Ordinal)
            || !string.Equals(
                trigger.InteractionVersion,
                definition.Definition.Version,
                StringComparison.Ordinal)
            || !string.Equals(
                trigger.InputSchemaId,
                definition.Definition.InputSchemaId,
                StringComparison.Ordinal)
            || !trigger.Payload.HasValue)
        {
            return false;
        }

        var payload = trigger.Payload.Value;
        return payload.TryGetProperty(
                   "definitionDigest",
                   out var digest)
               && digest.ValueKind == JsonValueKind.String
               && string.Equals(
                   digest.GetString(),
                   definition.Definition.ContentDigest,
                   StringComparison.Ordinal);
    }

    private static bool IncarnationsMatch(
        WorldAuthoritativeStateSnapshot snapshot,
        GameEntityIdentity actor,
        IReadOnlyList<GameEntityIdentity> targets)
    {
        return new[] { actor }
            .Concat(targets)
            .All(
                identity =>
                    snapshot.TryGetIncarnation(
                        identity.EntityId,
                        out var incarnation)
                    && incarnation == identity.Incarnation);
    }

    private static bool TryCurrentClock(
        ActivatedWorldPackage package,
        JsonElement state,
        GameTimePoint? requested,
        out (string ClockId, long Tick) current)
    {
        if (requested is not null)
        {
            if (package.FindClock(requested.ClockId) is null
                || !NativeWorldConditionEvaluator.TryReadClock(
                    package,
                    state,
                    requested.ClockId,
                    "__none__",
                    0,
                    out var requestedTick)
                || requestedTick != requested.Tick)
            {
                current = default;
                return false;
            }

            current = (requested.ClockId, requestedTick);
            return true;
        }

        var clock = package.Clocks.FirstOrDefault();
        if (clock is not null
            && NativeWorldConditionEvaluator.TryReadClock(
                package,
                state,
                clock.ClockId,
                "__none__",
                0,
                out var tick))
        {
            current = (clock.ClockId, tick);
            return true;
        }

        current = ("__none__", 0);
        return true;
    }
}

internal sealed class NativeWorldInteractionRuntimeBundle
{
    public NativeWorldInteractionRuntimeBundle(
        IWorldEventHandlerRegistry eventHandlers,
        IWorldTransactionalEventEffectRegistry transactionalEffects)
    {
        EventHandlers = eventHandlers;
        TransactionalEffects = transactionalEffects;
    }

    public IWorldEventHandlerRegistry EventHandlers { get; }

    public IWorldTransactionalEventEffectRegistry TransactionalEffects
    { get; }
}
