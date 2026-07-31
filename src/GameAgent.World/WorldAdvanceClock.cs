using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class WorldAdvanceClockCommand
{
    public WorldAdvanceClockCommand(
        string commandId,
        string operationId,
        WorldAuthoritativeCoordinate expectedCoordinate,
        string clockId,
        long expectedClockTick,
        int ticks)
    {
        CommandId = WorldValidation.Required(
            commandId,
            nameof(commandId));
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        if (ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        _ = checked(expectedClockTick + ticks);
        ExpectedClockTick = expectedClockTick;
        Ticks = ticks;
    }

    public string CommandId { get; }

    public string OperationId { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string ClockId { get; }

    public long ExpectedClockTick { get; }

    public int Ticks { get; }
}

public enum WorldAdvanceClockStatus
{
    Completed = 0,
    PartiallyCompleted = 1,
    Rejected = 2,
    Cancelled = 3,
    Busy = 4,
    ReconciliationRequired = 5,
    IdempotencyConflict = 6
}

public sealed class WorldAdvanceClockTickResult
{
    internal WorldAdvanceClockTickResult(
        int tickIndex,
        long targetTick,
        WorldTransactionExecutionResult execution)
    {
        TickIndex = tickIndex;
        TargetTick = targetTick;
        Execution = execution;
    }

    public int TickIndex { get; }

    public long TargetTick { get; }

    public WorldTransactionExecutionResult Execution { get; }

    public bool Committed =>
        Execution.Status is WorldTransactionExecutionStatus.Committed
            or WorldTransactionExecutionStatus.Replayed;
}

public sealed class WorldAdvanceClockResult
{
    internal WorldAdvanceClockResult(
        WorldAdvanceClockStatus status,
        string reasonCode,
        int completedTicks,
        WorldAuthoritativeCoordinate coordinate,
        IEnumerable<WorldAdvanceClockTickResult> ticks)
    {
        Status = status;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        CompletedTicks = completedTicks;
        Coordinate = coordinate;
        TickResults =
            new ReadOnlyCollection<WorldAdvanceClockTickResult>(
                ticks.ToArray());
    }

    public WorldAdvanceClockStatus Status { get; }

    public string ReasonCode { get; }

    public int CompletedTicks { get; }

    public WorldAuthoritativeCoordinate Coordinate { get; }

    public IReadOnlyList<WorldAdvanceClockTickResult> TickResults { get; }

    public bool Succeeded => Status == WorldAdvanceClockStatus.Completed;
}

public sealed class WorldAdvanceClockRunnerOptions
{
    public WorldAdvanceClockRunnerOptions(
        int maxTicksPerCommand = 1_024,
        NativeWorldExecutionLimits? executionLimits = null)
    {
        if (maxTicksPerCommand is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTicksPerCommand));
        }

        MaxTicksPerCommand = maxTicksPerCommand;
        ExecutionLimits = executionLimits ?? new NativeWorldExecutionLimits();
    }

    public int MaxTicksPerCommand { get; }

    public NativeWorldExecutionLimits ExecutionLimits { get; }
}

/// <summary>
/// Runs a typed discrete-clock command as a committed prefix of independent
/// tick transactions. A retry replays durable tick receipts; cancellation or
/// failure cannot undo an earlier committed tick.
/// </summary>
public sealed class WorldAdvanceClockRunner
{
    private readonly ActivatedWorldPackage _package;

    private readonly WorldEventTransactionExecutor _transactions;

    private readonly WorldAdvanceClockRunnerOptions _options;

    public WorldAdvanceClockRunner(
        ActivatedWorldPackage package,
        IWorldAuthoritativeTransactionStore store,
        WorldAdvanceClockRunnerOptions? options = null)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _transactions = new WorldEventTransactionExecutor(
            store ?? throw new ArgumentNullException(nameof(store)));
        _options = options ?? new WorldAdvanceClockRunnerOptions();
    }

    public async ValueTask<WorldAdvanceClockResult> ExecuteAsync(
        WorldAdvanceClockCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        ValidateCommand(command);
        var clock = _package.FindClock(command.ClockId)!;
        var tickResults = new List<WorldAdvanceClockTickResult>(
            command.Ticks);
        var coordinate = command.ExpectedCoordinate;
        var completed = 0;
        for (var index = 0; index < command.Ticks; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(
                    WorldAdvanceClockStatus.Cancelled,
                    WorldTransactionReasonCodes.Cancelled,
                    completed,
                    coordinate,
                    tickResults);
            }

            var fromTick = checked(command.ExpectedClockTick + index);
            var toTick = checked(fromTick + 1);
            var commandId = NativeWorldIdentity.Derive(
                "world.tick.command",
                command.CommandId,
                command.OperationId,
                index.ToString(CultureInfo.InvariantCulture));
            var operationId = NativeWorldIdentity.Derive(
                "world.tick.operation",
                command.CommandId,
                command.OperationId,
                index.ToString(CultureInfo.InvariantCulture));
            var effect = new NativeWorldClockTickEffect(
                _package,
                clock,
                commandId,
                operationId,
                fromTick,
                toTick,
                index,
                command.Ticks,
                _options.ExecutionLimits);
            var instance = BuildTickInstance(
                command,
                clock,
                coordinate,
                fromTick,
                toTick,
                index);
            var execution = await _transactions.ExecuteAsync(
                    new WorldEventTransactionExecutionRequest(
                        instance,
                        coordinate,
                        commandId,
                        operationId,
                        effect),
                    cancellationToken)
                .ConfigureAwait(false);
            tickResults.Add(
                new WorldAdvanceClockTickResult(
                    index,
                    toTick,
                    execution));
            if (execution.Status is WorldTransactionExecutionStatus.Committed
                or WorldTransactionExecutionStatus.Replayed)
            {
                completed++;
                coordinate = execution.Receipt?.ResultingCoordinate
                             ?? throw new InvalidOperationException(
                                 "A terminal tick result requires a "
                                 + "durable receipt.");
                continue;
            }

            return Finish(
                MapStatus(execution.Status, completed),
                execution.ReasonCode,
                completed,
                coordinate,
                tickResults);
        }

        return Finish(
            WorldAdvanceClockStatus.Completed,
            NativeWorldExecutionReasonCodes.Applied,
            completed,
            coordinate,
            tickResults);
    }

    private void ValidateCommand(WorldAdvanceClockCommand command)
    {
        if (command.Ticks > _options.MaxTicksPerCommand)
        {
            throw new WorldEvolutionLimitException(
                WorldEvolutionReasonCodes.BatchLimitExceeded,
                "The clock command exceeds its tick limit.");
        }

        if (_package.FindClock(command.ClockId) is null)
        {
            throw new ArgumentException(
                "The clock does not exist in the activated package.",
                nameof(command));
        }

        var coordinate = command.ExpectedCoordinate;
        if (!string.Equals(
                coordinate.WorldId,
                _package.World.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                coordinate.CatalogDigest,
                _package.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The command does not bind this activated world catalog.",
                nameof(command));
        }
    }

    private WorldEventInstance BuildTickInstance(
        WorldAdvanceClockCommand command,
        NativeWorldClockDefinition clock,
        WorldAuthoritativeCoordinate coordinate,
        long fromTick,
        long toTick,
        int index)
    {
        var clockResource = "clock:" + clock.ClockId;
        var writes = new SortedSet<string>(StringComparer.Ordinal)
        {
            clockResource
        };
        foreach (var eventDefinition in _package.Events)
        {
            foreach (var resourceKey in eventDefinition.WriteResourceKeys)
            {
                AddTickResource(writes, resourceKey, additionalCount: 0);
            }
        }

        var reads = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var eventDefinition in _package.Events)
        {
            foreach (var resourceKey in eventDefinition.ReadResourceKeys)
            {
                if (!writes.Contains(resourceKey))
                {
                    AddTickResource(
                        reads,
                        resourceKey,
                        writes.Count);
                }
            }
        }

        var writeKeys = writes.ToArray();
        var readKeys = reads.ToArray();
        var definitionId = NativeWorldIdentity.Derive(
            "world.tick.definition",
            _package.CatalogDigest,
            clock.ClockId);
        var definition = new WorldEventDefinition(
            definitionId,
            "1",
            "advance_clock",
            priority: 0,
            "native.tick.condition",
            "native.tick.selector",
            "native.tick.resolver",
            "native.tick.effect",
            readKeys,
            writeKeys);
        var payload = BuildTriggerPayload(
            command,
            fromTick,
            toTick,
            index);
        var trigger = new WorldEvolutionTrigger(
            NativeWorldIdentity.Derive(
                "world.tick.trigger",
                command.CommandId,
                command.OperationId,
                index.ToString(CultureInfo.InvariantCulture)),
            "advance_clock",
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            new GameTimePoint(
                clock.ClockId,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                toTick),
            payload);
        var resolution = new WorldEventResolution(
            "tick:"
            + index.ToString(CultureInfo.InvariantCulture),
            readResourceKeys: readKeys,
            writeResourceKeys: writeKeys);
        return new WorldEventInstance(
            definition,
            trigger,
            resolution,
            cascadeDepth: 0,
            parentInstanceId: null,
            readKeys,
            writeKeys);
    }

    private static void AddTickResource(
        ISet<string> resources,
        string resourceKey,
        int additionalCount)
    {
        if (resources.Add(resourceKey)
            && resources.Count + additionalCount
            > WorldValidation.MaximumResourceKeys)
        {
            throw new WorldEvolutionLimitException(
                WorldEvolutionReasonCodes.ResourceLimitExceeded,
                "The activated world exceeds the per-tick resource limit.");
        }
    }

    private static JsonElement BuildTriggerPayload(
        WorldAdvanceClockCommand command,
        long fromTick,
        long toTick,
        int index)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("clockId", command.ClockId);
            writer.WriteString(
                "fromTick",
                fromTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "toTick",
                toTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "tickIndex",
                index.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static WorldAdvanceClockStatus MapStatus(
        WorldTransactionExecutionStatus status,
        int completed)
    {
        if (completed > 0)
        {
            return WorldAdvanceClockStatus.PartiallyCompleted;
        }

        return status switch
        {
            WorldTransactionExecutionStatus.Cancelled =>
                WorldAdvanceClockStatus.Cancelled,
            WorldTransactionExecutionStatus.Busy =>
                WorldAdvanceClockStatus.Busy,
            WorldTransactionExecutionStatus.ReconciliationRequired =>
                WorldAdvanceClockStatus.ReconciliationRequired,
            WorldTransactionExecutionStatus.IdempotencyConflict =>
                WorldAdvanceClockStatus.IdempotencyConflict,
            _ => WorldAdvanceClockStatus.Rejected
        };
    }

    private static WorldAdvanceClockResult Finish(
        WorldAdvanceClockStatus status,
        string reasonCode,
        int completed,
        WorldAuthoritativeCoordinate coordinate,
        IEnumerable<WorldAdvanceClockTickResult> ticks)
    {
        return new WorldAdvanceClockResult(
            status,
            reasonCode,
            completed,
            coordinate,
            ticks);
    }
}

internal sealed class NativeWorldClockTickEffect
    : IWorldTransactionalEventEffect,
      IWorldTransactionalEffectAdmission
{
    private readonly ActivatedWorldPackage _package;

    private readonly NativeWorldClockDefinition _clock;

    private readonly long _fromTick;

    private readonly long _toTick;

    private readonly int _tickIndex;

    private readonly int _totalTicks;

    private readonly NativeWorldExecutionLimits _limits;

    public NativeWorldClockTickEffect(
        ActivatedWorldPackage package,
        NativeWorldClockDefinition clock,
        string commandId,
        string operationId,
        long fromTick,
        long toTick,
        int tickIndex,
        int totalTicks,
        NativeWorldExecutionLimits limits)
    {
        _package = package;
        _clock = clock;
        CommandId = commandId;
        OperationId = operationId;
        _fromTick = fromTick;
        _toTick = toTick;
        _tickIndex = tickIndex;
        _totalTicks = totalTicks;
        _limits = limits;
        PayloadDigest = BuildPayloadDigest();
    }

    public string CommandId { get; }

    public string OperationId { get; }

    public string PayloadDigest { get; }

    public IReadOnlyList<WorldEntityIncarnationExpectation>
        ExpectedIncarnations =>
            Array.Empty<WorldEntityIncarnationExpectation>();

    public async ValueTask<WorldEventEffectResult> ApplyAsync(
        WorldTransactionalEventEffectContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    context.Source.Coordinate.CatalogDigest,
                    _package.CatalogDigest,
                    StringComparison.Ordinal))
            {
                return Rejected(
                    NativeWorldExecutionReasonCodes.CatalogMismatch);
            }

            if (!TryAdvanceClock(context.Draft))
            {
                return Rejected(
                    NativeWorldExecutionReasonCodes.ClockStale);
            }

            var rootPayload = BuildClockPayload();
            var queue = new Queue<EventSignal>();
            queue.Enqueue(
                EventSignal.Clock(
                    _clock.ClockId,
                    rootPayload));
            var occurrenceIds = new List<string>();
            var emittedCount = 0;
            var signalSequence = 0;
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signal = queue.Dequeue();
                foreach (var definition in _package.Events)
                {
                    if (!Matches(definition.Trigger, signal))
                    {
                        continue;
                    }

                    IReadOnlyList<NativeWorldEvaluationSubject?>
                        selections;
                    if (definition.Selector
                        is NativeWorldSingletonSelector)
                    {
                        selections =
                            new NativeWorldEvaluationSubject?[] { null };
                    }
                    else
                    {
                        selections =
                            NativeWorldDeclarativeRuntime.Select(
                                    definition.Selector,
                                    _package,
                                    context.Draft.State,
                                    context.Draft.EntityIncarnations)
                                .Cast<NativeWorldEvaluationSubject?>()
                                .ToArray();
                    }

                    foreach (var subject in selections)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var conditionContext =
                            new NativeWorldConditionEvaluationContext(
                                _package,
                                context.Draft.State,
                                _clock.ClockId,
                                _toTick,
                                subject,
                                signal.Payload,
                                stateAlreadyValidated: true);
                        if (!NativeWorldConditionEvaluator.Evaluate(
                                definition.Condition,
                                conditionContext))
                        {
                            continue;
                        }

                        if (occurrenceIds.Count
                            >= _limits.MaxEventOccurrencesPerTick)
                        {
                            return Rejected(
                                NativeWorldExecutionReasonCodes
                                    .EventLimitExceeded);
                        }

                        var occurrenceId = NativeWorldIdentity.Derive(
                            "world.native.occurrence",
                            _package.CatalogDigest,
                            _clock.ClockId,
                            _toTick.ToString(
                                CultureInfo.InvariantCulture),
                            definition.DefinitionId,
                            definition.Version,
                            subject?.Identity.EntityId ?? "__world__",
                            subject?.Identity.Incarnation.ToString(
                                CultureInfo.InvariantCulture) ?? "0",
                            signal.ParentOccurrenceId ?? string.Empty,
                            signalSequence.ToString(
                                CultureInfo.InvariantCulture));
                        signalSequence++;
                        IReadOnlyList<IWorldMutationIntent> intents;
                        try
                        {
                            intents =
                                NativeWorldDeclarativeRuntime.Materialize(
                                    definition.Effects,
                                    subject,
                                    Array.Empty<GameEntityIdentity>(),
                                    occurrenceId,
                                    context.Draft.EntityIncarnations);
                        }
                        catch (NativeWorldExecutionException exception)
                        {
                            return Rejected(exception.ReasonCode);
                        }

                        var applied =
                            await NativeWorldDeclarativeRuntime
                                .ApplyIntentsAsync(
                                    intents,
                                    _package,
                                    context,
                                    CommandId,
                                    OperationId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        if (!applied.Applied)
                        {
                            return applied;
                        }

                        occurrenceIds.Add(occurrenceId);
                        foreach (var emission in definition.Effects
                                     .OfType<NativeWorldEmitEventEffect>())
                        {
                            if (emittedCount
                                >= _limits.MaxEmittedEventsPerTick)
                            {
                                return Rejected(
                                    NativeWorldExecutionReasonCodes
                                        .EventLimitExceeded);
                            }

                            var nextDepth = signal.Depth + 1;
                            if (nextDepth > _limits.MaxCascadeDepth)
                            {
                                return Rejected(
                                    NativeWorldExecutionReasonCodes
                                        .CascadeLimitExceeded);
                            }

                            queue.Enqueue(
                                EventSignal.Emitted(
                                    emission.EventKind,
                                    emission.Payload,
                                    nextDepth,
                                    occurrenceId));
                            emittedCount++;
                        }
                    }
                }
            }

            return new WorldEventEffectResult(
                applied: true,
                NativeWorldExecutionReasonCodes.Applied,
                BuildTypedResult(occurrenceIds));
        }
        catch (NativeWorldExecutionException exception)
        {
            return Rejected(exception.ReasonCode);
        }
        catch (WorldJsonMutationException)
        {
            return Rejected(
                NativeWorldExecutionReasonCodes.InvalidState);
        }
        catch (JsonException)
        {
            return Rejected(
                NativeWorldExecutionReasonCodes.InvalidState);
        }
    }

    private bool TryAdvanceClock(IWorldStateDraft draft)
    {
        var root = JsonNode.Parse(draft.State.GetRawText());
        if (root is not JsonObject
            || !WorldJsonTree.TryGet(
                root,
                _clock.StatePath,
                out var node)
            || node is not JsonValue value
            || !value.TryGetValue<string>(out var current)
            || !NativeWorldConditionEvaluator.TryParseCanonicalInt64(
                current,
                out var actual)
            || actual != _fromTick)
        {
            return false;
        }

        WorldJsonTree.Set(
            root,
            _clock.StatePath,
            JsonValue.Create(
                _toTick.ToString(CultureInfo.InvariantCulture)),
            createParents: false);
        using var document = JsonDocument.Parse(root.ToJsonString());
        var state = document.RootElement.Clone();
        draft.ReplaceState(state);
        return true;
    }

    private bool Matches(
        NativeWorldEventTrigger trigger,
        EventSignal signal)
    {
        switch (trigger)
        {
            case NativeWorldClockEventTrigger clock
                when signal.IsClock:
                return string.Equals(
                           clock.ClockId,
                           signal.Kind,
                           StringComparison.Ordinal)
                       && PositiveMod(_toTick, clock.EveryTicks)
                       == PositiveMod(
                           clock.OffsetTicks,
                           clock.EveryTicks);
            case NativeWorldEmittedEventTrigger emitted
                when !signal.IsClock:
                return string.Equals(
                    emitted.EventKind,
                    signal.Kind,
                    StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private JsonElement BuildClockPayload()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("clockId", _clock.ClockId);
            writer.WriteString(
                "fromTick",
                _fromTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "toTick",
                _toTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private JsonElement BuildTypedResult(
        IReadOnlyList<string> occurrenceIds)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("clockId", _clock.ClockId);
            writer.WriteString(
                "fromTick",
                _fromTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "toTick",
                _toTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "tickIndex",
                _tickIndex.ToString(CultureInfo.InvariantCulture));
            writer.WritePropertyName("occurrenceIds");
            writer.WriteStartArray();
            foreach (var occurrenceId in occurrenceIds)
            {
                writer.WriteStringValue(occurrenceId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private string BuildPayloadDigest()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("commandId", CommandId);
            writer.WriteString("operationId", OperationId);
            writer.WriteString("catalogDigest", _package.CatalogDigest);
            writer.WriteString("clockId", _clock.ClockId);
            writer.WriteString(
                "fromTick",
                _fromTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "toTick",
                _toTick.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "tickIndex",
                _tickIndex.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "totalTicks",
                _totalTicks.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return CanonicalJsonDigest.ComputeSha256(document.RootElement);
    }

    private static long PositiveMod(long value, long divisor)
    {
        var result = (BigInteger)value % divisor;
        return (long)(result.Sign < 0 ? result + divisor : result);
    }

    private static WorldEventEffectResult Rejected(string reasonCode)
    {
        return new WorldEventEffectResult(
            applied: false,
            reasonCode);
    }

    private sealed class EventSignal
    {
        private EventSignal(
            bool isClock,
            string kind,
            JsonElement? payload,
            int depth,
            string? parentOccurrenceId)
        {
            IsClock = isClock;
            Kind = kind;
            Payload = payload?.Clone();
            Depth = depth;
            ParentOccurrenceId = parentOccurrenceId;
        }

        public bool IsClock { get; }

        public string Kind { get; }

        public JsonElement? Payload { get; }

        public int Depth { get; }

        public string? ParentOccurrenceId { get; }

        public static EventSignal Clock(
            string clockId,
            JsonElement payload)
        {
            return new EventSignal(
                true,
                clockId,
                payload,
                depth: 0,
                parentOccurrenceId: null);
        }

        public static EventSignal Emitted(
            string eventKind,
            JsonElement? payload,
            int depth,
            string parentOccurrenceId)
        {
            return new EventSignal(
                false,
                eventKind,
                payload,
                depth,
                parentOccurrenceId);
        }
    }
}
