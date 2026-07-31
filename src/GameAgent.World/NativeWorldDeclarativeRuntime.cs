using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class NativeWorldExecutionReasonCodes
{
    public const string Applied = "world_native_tick_applied";

    public const string ClockMissing = "world_native_clock_missing";

    public const string ClockStale = "world_native_clock_stale";

    public const string InvalidState = "world_native_state_invalid";

    public const string SelectorLimitExceeded =
        "world_native_selector_limit_exceeded";

    public const string EventLimitExceeded =
        "world_native_event_limit_exceeded";

    public const string CascadeLimitExceeded =
        "world_native_cascade_limit_exceeded";

    public const string EntityUnavailable =
        "world_native_entity_unavailable";

    public const string ConditionRejected =
        "world_native_condition_rejected";

    public const string CatalogMismatch =
        "world_native_catalog_mismatch";
}

public sealed class NativeWorldExecutionLimits
{
    public NativeWorldExecutionLimits(
        int maxEventOccurrencesPerTick = 256,
        int maxCascadeDepth = 8,
        int maxEmittedEventsPerTick = 256)
    {
        MaxEventOccurrencesPerTick = InRange(
            maxEventOccurrencesPerTick,
            1,
            4_096,
            nameof(maxEventOccurrencesPerTick));
        MaxCascadeDepth = InRange(
            maxCascadeDepth,
            0,
            64,
            nameof(maxCascadeDepth));
        MaxEmittedEventsPerTick = InRange(
            maxEmittedEventsPerTick,
            0,
            4_096,
            nameof(maxEmittedEventsPerTick));
    }

    public int MaxEventOccurrencesPerTick { get; }

    public int MaxCascadeDepth { get; }

    public int MaxEmittedEventsPerTick { get; }

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

public sealed class NativeWorldEvaluationSubject
{
    public NativeWorldEvaluationSubject(
        GameEntityIdentity identity,
        string role = "subject")
    {
        Identity = identity ?? throw new ArgumentNullException(
            nameof(identity));
        Role = WorldValidation.Required(role, nameof(role), 128);
    }

    public GameEntityIdentity Identity { get; }

    public string Role { get; }
}

public sealed class NativeWorldConditionEvaluationContext
{
    private readonly JsonElement? _triggerPayload;

    public NativeWorldConditionEvaluationContext(
        ActivatedWorldPackage package,
        JsonElement state,
        string currentClockId,
        long currentClockTick,
        NativeWorldEvaluationSubject? subject = null,
        JsonElement? triggerPayload = null)
        : this(
            package,
            state,
            currentClockId,
            currentClockTick,
            subject,
            triggerPayload,
            stateAlreadyValidated: false)
    {
    }

    internal NativeWorldConditionEvaluationContext(
        ActivatedWorldPackage package,
        JsonElement state,
        string currentClockId,
        long currentClockTick,
        NativeWorldEvaluationSubject? subject,
        JsonElement? triggerPayload,
        bool stateAlreadyValidated)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        if (!stateAlreadyValidated)
        {
            WorldAuthoritativeStateSnapshot.ValidateState(
                state,
                nameof(state));
        }

        State = stateAlreadyValidated ? state : state.Clone();
        CurrentClockId = WorldValidation.Required(
            currentClockId,
            nameof(currentClockId));
        CurrentClockTick = currentClockTick;
        Subject = subject;
        if (triggerPayload.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                triggerPayload.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 65_536,
                    maxDepth: 24,
                    maxNodes: 4_096,
                    maxStringUtf8Bytes: 16_384,
                    maxContainerItems: 2_048),
                nameof(triggerPayload));
            _triggerPayload = triggerPayload.Value.Clone();
        }
    }

    public ActivatedWorldPackage Package { get; }

    public JsonElement State { get; }

    public string CurrentClockId { get; }

    public long CurrentClockTick { get; }

    public NativeWorldEvaluationSubject? Subject { get; }

    public JsonElement? TriggerPayload => _triggerPayload?.Clone();
}

public static class NativeWorldConditionEvaluator
{
    private const int MaximumConditionDepth = 64;

    private const int MaximumConditionNodes = 8_192;

    public static bool Evaluate(
        NativeWorldCondition condition,
        NativeWorldConditionEvaluationContext context)
    {
        if (condition is null)
        {
            throw new ArgumentNullException(nameof(condition));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        ValidateGraph(condition);
        return EvaluateValidated(condition, context);
    }

    private static bool EvaluateValidated(
        NativeWorldCondition condition,
        NativeWorldConditionEvaluationContext context)
    {
        return condition switch
        {
            NativeWorldAlwaysCondition => true,
            NativeWorldAllCondition all =>
                all.Children.All(
                    child => EvaluateValidated(child, context)),
            NativeWorldAnyCondition any =>
                any.Children.Any(
                    child => EvaluateValidated(child, context)),
            NativeWorldNotCondition not =>
                !EvaluateValidated(not.Child, context),
            NativeWorldPathCondition path => EvaluatePath(path, context),
            NativeWorldTagCondition tag => HasTag(tag.Tag, context),
            NativeWorldFixedPointCondition fixedPoint =>
                EvaluateFixedPoint(fixedPoint, context),
            NativeWorldClockCondition clock =>
                EvaluateClock(clock, context),
            _ => false
        };
    }

    private static void ValidateGraph(NativeWorldCondition root)
    {
        var pending = new Stack<(NativeWorldCondition Condition, int Depth)>();
        pending.Push((root, 1));
        var nodes = 0;
        while (pending.Count > 0)
        {
            var (condition, depth) = pending.Pop();
            nodes++;
            if (nodes > MaximumConditionNodes
                || depth > MaximumConditionDepth)
            {
                throw new ArgumentException(
                    "The condition graph exceeds its depth or node limit.",
                    nameof(root));
            }

            switch (condition)
            {
                case NativeWorldAllCondition all:
                    PushChildren(pending, all.Children, depth);
                    break;
                case NativeWorldAnyCondition any:
                    PushChildren(pending, any.Children, depth);
                    break;
                case NativeWorldNotCondition not:
                    pending.Push((not.Child, depth + 1));
                    break;
            }
        }
    }

    private static void PushChildren(
        Stack<(NativeWorldCondition Condition, int Depth)> pending,
        IReadOnlyList<NativeWorldCondition> children,
        int parentDepth)
    {
        for (var index = children.Count - 1; index >= 0; index--)
        {
            pending.Push((children[index], parentDepth + 1));
        }
    }

    internal static bool HasTag(
        string tag,
        NativeWorldConditionEvaluationContext context)
    {
        if (context.Subject is null)
        {
            return false;
        }

        var path = string.Concat(
            context.Package.World.EntityStateRootPath,
            "/",
            WorldJsonPointer.Escape(
                context.Subject.Identity.EntityId),
            "/tags");
        if (!TryResolve(context.State, path, out var tags)
            || tags.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return tags.EnumerateArray().Any(
            item => item.ValueKind == JsonValueKind.String
                    && string.Equals(
                        item.GetString(),
                        tag,
                        StringComparison.Ordinal));
    }

    internal static bool TryResolve(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        var current = root;
        foreach (var segment in WorldJsonPointer.Parse(pointer))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    internal static bool TryReadClock(
        ActivatedWorldPackage package,
        JsonElement state,
        string clockId,
        string currentClockId,
        long currentClockTick,
        out long tick)
    {
        if (string.Equals(
                clockId,
                currentClockId,
                StringComparison.Ordinal))
        {
            tick = currentClockTick;
            return true;
        }

        var clock = package.FindClock(clockId);
        if (clock is null
            || !TryResolve(state, clock.StatePath, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            tick = 0;
            return false;
        }

        return TryParseCanonicalInt64(value.GetString(), out tick);
    }

    internal static bool TryParseCanonicalInt64(
        string? value,
        out long result)
    {
        return long.TryParse(
                   value,
                   NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture,
                   out result)
               && string.Equals(
                   value,
                   result.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal);
    }

    private static bool EvaluatePath(
        NativeWorldPathCondition condition,
        NativeWorldConditionEvaluationContext context)
    {
        var found = TryResolveReference(
            condition.Path,
            context,
            out var actual);
        if (condition.Comparison == NativeWorldComparisonOperator.Exists)
        {
            return found;
        }

        if (condition.Comparison == NativeWorldComparisonOperator.Missing)
        {
            return !found;
        }

        if (!found || !condition.Value.HasValue)
        {
            return false;
        }

        return CompareJson(
            actual,
            condition.Value.Value,
            condition.Comparison);
    }

    private static bool EvaluateFixedPoint(
        NativeWorldFixedPointCondition condition,
        NativeWorldConditionEvaluationContext context)
    {
        if (!TryResolveReference(condition.Path, context, out var actual)
            || actual.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var schema = context.Package.FindNumericSchema(
            condition.NumericSchemaId);
        if (schema is null)
        {
            return false;
        }

        var binding = schema.TryBindCanonical(actual.GetString());
        var expected = schema.TryBind(condition.Value);
        if (!binding.Succeeded || !expected.Succeeded)
        {
            return false;
        }

        var comparison = WorldNumericMath.Compare(
            binding.Quantity!,
            expected.Quantity!);
        return comparison.Succeeded
               && CompareOrdering(
                   comparison.Comparison,
                   condition.Comparison);
    }

    private static bool EvaluateClock(
        NativeWorldClockCondition condition,
        NativeWorldConditionEvaluationContext context)
    {
        return TryReadClock(
                   context.Package,
                   context.State,
                   condition.ClockId,
                   context.CurrentClockId,
                   context.CurrentClockTick,
                   out var actual)
               && CompareOrdering(
                   actual.CompareTo(condition.Tick),
                   condition.Comparison);
    }

    private static bool TryResolveReference(
        NativeWorldPathReference reference,
        NativeWorldConditionEvaluationContext context,
        out JsonElement value)
    {
        switch (reference.Source)
        {
            case NativeWorldValueSourceKind.World:
                return TryResolve(context.State, reference.Path, out value);
            case NativeWorldValueSourceKind.Subject:
                if (context.Subject is null)
                {
                    value = default;
                    return false;
                }

                return TryResolve(
                    context.State,
                    string.Concat(
                        context.Package.World.EntityStateRootPath,
                        "/",
                        WorldJsonPointer.Escape(
                            context.Subject.Identity.EntityId),
                        reference.Path),
                    out value);
            case NativeWorldValueSourceKind.Trigger:
                if (!context.TriggerPayload.HasValue)
                {
                    value = default;
                    return false;
                }

                return TryResolve(
                    context.TriggerPayload.Value,
                    reference.Path,
                    out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool CompareJson(
        JsonElement left,
        JsonElement right,
        NativeWorldComparisonOperator comparison)
    {
        if (comparison is NativeWorldComparisonOperator.Equal
            or NativeWorldComparisonOperator.NotEqual)
        {
            var equal = left.ValueKind == right.ValueKind
                        && string.Equals(
                            CanonicalJsonDigest.ComputeSha256(left),
                            CanonicalJsonDigest.ComputeSha256(right),
                            StringComparison.Ordinal);
            return comparison == NativeWorldComparisonOperator.Equal
                ? equal
                : !equal;
        }

        if (left.ValueKind != JsonValueKind.String
            || right.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return CompareOrdering(
            string.CompareOrdinal(left.GetString(), right.GetString()),
            comparison);
    }

    private static bool CompareOrdering(
        int ordering,
        NativeWorldComparisonOperator comparison)
    {
        return comparison switch
        {
            NativeWorldComparisonOperator.Equal => ordering == 0,
            NativeWorldComparisonOperator.NotEqual => ordering != 0,
            NativeWorldComparisonOperator.LessThan => ordering < 0,
            NativeWorldComparisonOperator.LessThanOrEqual => ordering <= 0,
            NativeWorldComparisonOperator.GreaterThan => ordering > 0,
            NativeWorldComparisonOperator.GreaterThanOrEqual => ordering >= 0,
            _ => false
        };
    }
}

internal static class NativeWorldDeclarativeRuntime
{
    public static IReadOnlyList<NativeWorldEvaluationSubject> Select(
        NativeWorldParticipantSelector selector,
        ActivatedWorldPackage package,
        JsonElement state,
        IReadOnlyDictionary<string, long> incarnations)
    {
        switch (selector)
        {
            case NativeWorldSingletonSelector:
                return new[]
                {
                    new NativeWorldEvaluationSubject(
                        new GameEntityIdentity("__world__", 0),
                        "world")
                };
            case NativeWorldEntitySelector entity:
                if (!incarnations.TryGetValue(
                        entity.EntityId,
                        out var incarnation)
                    || (entity.RequiredIncarnation.HasValue
                        && entity.RequiredIncarnation.Value != incarnation))
                {
                    return Array.Empty<NativeWorldEvaluationSubject>();
                }

                return new[]
                {
                    new NativeWorldEvaluationSubject(
                        new GameEntityIdentity(
                            entity.EntityId,
                            incarnation),
                        entity.Role)
                };
            case NativeWorldTaggedEntitiesSelector tagged:
                var result = new List<NativeWorldEvaluationSubject>();
                foreach (var pair in incarnations.OrderBy(
                             item => item.Key,
                             StringComparer.Ordinal))
                {
                    var subject = new NativeWorldEvaluationSubject(
                        new GameEntityIdentity(pair.Key, pair.Value),
                        tagged.Role);
                    var context =
                        new NativeWorldConditionEvaluationContext(
                            package,
                            state,
                            package.Clocks.FirstOrDefault()?.ClockId
                            ?? "__none__",
                            0,
                            subject,
                            triggerPayload: null,
                            stateAlreadyValidated: true);
                    if (!NativeWorldConditionEvaluator.HasTag(
                            tagged.Tag,
                            context))
                    {
                        continue;
                    }

                    result.Add(subject);
                    if (result.Count > tagged.MaximumCandidates)
                    {
                        throw new NativeWorldExecutionException(
                            NativeWorldExecutionReasonCodes
                                .SelectorLimitExceeded);
                    }
                }

                return new ReadOnlyCollection<
                    NativeWorldEvaluationSubject>(result);
            default:
                return Array.Empty<NativeWorldEvaluationSubject>();
        }
    }

    public static IReadOnlyList<IWorldMutationIntent> Materialize(
        IReadOnlyList<NativeWorldEffect> effects,
        NativeWorldEvaluationSubject? subject,
        IReadOnlyList<GameEntityIdentity> interactionTargets,
        string occurrenceId,
        IReadOnlyDictionary<string, long> incarnations)
    {
        var result = new List<IWorldMutationIntent>();
        var index = 0;
        foreach (var effect in effects)
        {
            if (effect is NativeWorldEmitEventEffect)
            {
                continue;
            }

            var intentId = NativeWorldIdentity.Derive(
                "intent",
                occurrenceId,
                effect.EffectId,
                index.ToString(CultureInfo.InvariantCulture));
            switch (effect)
            {
                case NativeWorldValueEffect value:
                    result.Add(
                        new WorldValueMutationIntent(
                            intentId,
                            Resolve(
                                value.Entity,
                                subject,
                                interactionTargets,
                                incarnations),
                            value.Path,
                            value.ResourceKey,
                            value.MutationKind,
                            value.Value));
                    break;
                case NativeWorldNumericEffect numeric:
                    result.Add(
                        new WorldNumericMutationIntent(
                            intentId,
                            Resolve(
                                numeric.Entity,
                                subject,
                                interactionTargets,
                                incarnations),
                            numeric.Path,
                            numeric.ResourceKey,
                            numeric.NumericSchemaId,
                            numeric.MutationKind,
                            numeric.Operand));
                    break;
                case NativeWorldTransferEffect transfer:
                    result.Add(
                        new WorldTransferMutationIntent(
                            intentId,
                            Resolve(
                                transfer.Source,
                                subject,
                                interactionTargets,
                                incarnations),
                            transfer.SourcePath,
                            transfer.SourceResourceKey,
                            Resolve(
                                transfer.Target,
                                subject,
                                interactionTargets,
                                incarnations),
                            transfer.TargetPath,
                            transfer.TargetResourceKey,
                            transfer.NumericSchemaId,
                            transfer.Amount));
                    break;
                case NativeWorldRelationshipEffect relationship:
                    result.Add(
                        new WorldRelationshipMutationIntent(
                            intentId,
                            Resolve(
                                relationship.Source,
                                subject,
                                interactionTargets,
                                incarnations),
                            Resolve(
                                relationship.Target,
                                subject,
                                interactionTargets,
                                incarnations),
                            relationship.RelationshipTypeId,
                            relationship.ResourceKey,
                            relationship.MutationKind,
                            relationship.Value));
                    break;
                default:
                    throw new NativeWorldExecutionException(
                        WorldMutationApplyReasonCodes.UnknownIntent);
            }

            index++;
        }

        return new ReadOnlyCollection<IWorldMutationIntent>(result);
    }

    public static async ValueTask<WorldEventEffectResult> ApplyIntentsAsync(
        IReadOnlyList<IWorldMutationIntent> intents,
        ActivatedWorldPackage package,
        WorldTransactionalEventEffectContext outerContext,
        string commandId,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (intents.Count == 0)
        {
            return new WorldEventEffectResult(
                applied: true,
                NativeWorldExecutionReasonCodes.Applied);
        }

        var coordinate = outerContext.Source.Coordinate;
        var set = new WorldAtomicMutationSet(
            commandId,
            operationId,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            coordinate.CatalogDigest,
            intents);
        var effect = new WorldAtomicMutationEffect(
            set,
            package.NumericSchemas,
            new WorldEntityMutationPathResolver(
                package.World.EntityStateRootPath,
                package.World.RelationshipRootPath));
        await using var transaction =
            new CurrentDraftTransaction(outerContext.Transaction);
        return await effect.ApplyAsync(
                new WorldTransactionalEventEffectContext(
                    outerContext.Instance,
                    transaction,
                    outerContext.HostContext),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static GameEntityIdentity Resolve(
        NativeWorldEntityReference reference,
        NativeWorldEvaluationSubject? subject,
        IReadOnlyList<GameEntityIdentity> interactionTargets,
        IReadOnlyDictionary<string, long> incarnations)
    {
        GameEntityIdentity identity;
        switch (reference)
        {
            case NativeWorldSubjectReference:
                identity = subject?.Identity
                           ?? throw new NativeWorldExecutionException(
                               NativeWorldExecutionReasonCodes
                                   .EntityUnavailable);
                break;
            case NativeWorldLiteralEntityReference literal:
                identity = literal.Identity;
                break;
            case NativeWorldInteractionTargetReference target
                when target.TargetIndex < interactionTargets.Count:
                identity = interactionTargets[target.TargetIndex];
                break;
            default:
                throw new NativeWorldExecutionException(
                    NativeWorldExecutionReasonCodes.EntityUnavailable);
        }

        if (!incarnations.TryGetValue(
                identity.EntityId,
                out var current)
            || current != identity.Incarnation)
        {
            throw new NativeWorldExecutionException(
                NativeWorldExecutionReasonCodes.EntityUnavailable);
        }

        return identity;
    }

    private sealed class CurrentDraftTransaction
        : IWorldAuthoritativeTransaction
    {
        private readonly IWorldAuthoritativeTransaction _outer;

        public CurrentDraftTransaction(
            IWorldAuthoritativeTransaction outer)
        {
            _outer = outer;
            Source = new WorldAuthoritativeStateSnapshot(
                outer.Source.Coordinate,
                outer.Draft.State,
                outer.Draft.EntityIncarnations,
                ReadIssuedIncarnations(outer));
        }

        public WorldTransactionRequest Request => _outer.Request;

        public WorldAuthoritativeStateSnapshot Source { get; }

        public IWorldStateDraft Draft => _outer.Draft;

        public ValueTask<WorldTransactionCommitResult> CommitEventAsync(
            WorldEffectReceipt effect,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException(
                "A nested declarative effect cannot commit independently.");
        }

        public ValueTask<WorldTransactionCommitResult>
            CompleteWithoutMutationAsync(
                WorldCommandReceiptStatus status,
                string outcomeCode,
                WorldEffectReceipt? effect,
                CancellationToken cancellationToken)
        {
            throw new NotSupportedException(
                "A nested declarative effect cannot complete independently.");
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private static IEnumerable<WorldIssuedEntityIncarnation>
            ReadIssuedIncarnations(
                IWorldAuthoritativeTransaction transaction)
        {
            if (transaction.Draft
                is IWorldIssuedIncarnationDraft issuedDraft)
            {
                return issuedDraft.IssuedEntityIncarnations;
            }

            return transaction.Source.IssuedEntityIncarnations.Concat(
                transaction.Draft.EntityIncarnations
                    .Where(
                        pair => !transaction.Source
                            .WasIncarnationIssued(
                                pair.Key,
                                pair.Value))
                    .Select(
                        pair => new WorldIssuedEntityIncarnation(
                            pair.Key,
                            pair.Value)));
        }
    }
}

internal static class NativeWorldIdentity
{
    public static string Derive(string prefix, params string[] components)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(prefix);
            foreach (var component in components)
            {
                writer.WriteStringValue(component);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return prefix
               + "."
               + CanonicalJsonDigest.ComputeSha256(
                   document.RootElement);
    }
}

internal sealed class NativeWorldExecutionException : Exception
{
    public NativeWorldExecutionException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
