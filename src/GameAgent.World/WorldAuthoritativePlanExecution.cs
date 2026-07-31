using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace GameAgent.World;

public static class WorldAuthoritativePlanReasonCodes
{
    public const string Applied = "world_plan_applied";
    public const string PartialFailure = "world_plan_partial_failure";
    public const string Rejected = "world_plan_rejected";
    public const string ReconciliationRequired =
        "world_plan_reconciliation_required";
    public const string EffectNotRegistered =
        "world_plan_effect_not_registered";
    public const string EffectFactoryFailed =
        "world_plan_effect_factory_failed";
    public const string InvalidArtifact = "world_plan_invalid_artifact";
    public const string ExecutionContextRequired =
        "world_plan_execution_context_required";
}

/// <summary>
/// Binds a deterministic event plan to the exact state and catalog from which
/// it was admitted. It is the executable artifact; an unbound plan remains
/// planning output only.
/// </summary>
public sealed class WorldAuthoritativeEventPlan
{
    public WorldAuthoritativeEventPlan(
        WorldEventPlan plan,
        WorldAuthoritativeCoordinate expectedCoordinate)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        if (!string.Equals(
                plan.Trigger.WorldId,
                expectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                plan.Trigger.TimelineId,
                expectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || plan.Trigger.TimelineEpoch
            != expectedCoordinate.TimelineEpoch)
        {
            throw new ArgumentException(
                "The plan and authoritative coordinate must share one scope.",
                nameof(expectedCoordinate));
        }

        ValidatePlanShape(plan);
        ValidateAdmissionFence(plan, expectedCoordinate);
    }

    public WorldEventPlan Plan { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string CatalogDigest => ExpectedCoordinate.CatalogDigest;

    private static void ValidatePlanShape(WorldEventPlan plan)
    {
        var instances = plan.Instances.ToDictionary(
            item => item.InstanceId,
            StringComparer.Ordinal);
        if (instances.Count != plan.Instances.Count)
        {
            throw new ArgumentException(
                "A plan cannot contain duplicate event instances.",
                nameof(plan));
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.ExecutionBatches.Count; index++)
        {
            var batch = plan.ExecutionBatches[index];
            if (batch.BatchIndex != index)
            {
                throw new ArgumentException(
                    "Execution batch indices must be contiguous.",
                    nameof(plan));
            }

            foreach (var instance in batch.Instances)
            {
                if (!instances.TryGetValue(
                        instance.InstanceId,
                        out var planned)
                    || !string.Equals(
                        planned.PlanFingerprint,
                        instance.PlanFingerprint,
                        StringComparison.Ordinal)
                    || !visited.Add(instance.InstanceId))
                {
                    throw new ArgumentException(
                        "Execution batches do not exactly cover the plan.",
                        nameof(plan));
                }
            }
        }

        if (visited.Count != instances.Count)
        {
            throw new ArgumentException(
                "Execution batches do not exactly cover the plan.",
                nameof(plan));
        }
    }

    private static void ValidateAdmissionFence(
        WorldEventPlan plan,
        WorldAuthoritativeCoordinate expectedCoordinate)
    {
        var fence = plan.AdmissionFence;
        if (fence is null
            || fence.CatalogDigest is null
            || !long.TryParse(
                fence.StateVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stateVersion)
            || !string.Equals(
                fence.StateVersion,
                stateVersion.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(
                fence.WorldId,
                expectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                fence.TimelineId,
                expectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || fence.TimelineEpoch != expectedCoordinate.TimelineEpoch
            || fence.SaveRevision != expectedCoordinate.SaveRevision
            || stateVersion != expectedCoordinate.StateVersion
            || !string.Equals(
                fence.CatalogDigest,
                expectedCoordinate.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The plan was not admitted from the supplied authoritative coordinate.",
                nameof(expectedCoordinate));
        }
    }
}

public sealed class WorldEventPlanExecutionRequest
{
    public WorldEventPlanExecutionRequest(
        WorldAuthoritativeEventPlan artifact,
        object? hostContext = null)
    {
        Artifact = artifact
                   ?? throw new ArgumentNullException(nameof(artifact));
        HostContext = hostContext;
    }

    public WorldAuthoritativeEventPlan Artifact { get; }

    public WorldEventPlan Plan => Artifact.Plan;

    public WorldAuthoritativeCoordinate ExpectedCoordinate =>
        Artifact.ExpectedCoordinate;

    public object? HostContext { get; }
}

/// <summary>
/// Typed context accepted by the general facade executor boundary.
/// </summary>
public sealed class WorldAuthoritativePlanExecutionContext
{
    public WorldAuthoritativePlanExecutionContext(
        WorldAuthoritativeEventPlan artifact,
        object? hostContext = null)
    {
        Artifact = artifact
                   ?? throw new ArgumentNullException(nameof(artifact));
        HostContext = hostContext;
    }

    public WorldAuthoritativeEventPlan Artifact { get; }

    public object? HostContext { get; }
}

public sealed class WorldTransactionalEffectFactoryContext
{
    internal WorldTransactionalEffectFactoryContext(
        WorldEventInstance instance,
        WorldAuthoritativeCoordinate expectedCoordinate,
        string commandId,
        string operationId,
        object? hostContext)
    {
        Instance = instance;
        ExpectedCoordinate = expectedCoordinate;
        CommandId = commandId;
        OperationId = operationId;
        HostContext = hostContext;
    }

    public WorldEventInstance Instance { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string CommandId { get; }

    public string OperationId { get; }

    public object? HostContext { get; }
}

/// <summary>
/// Creates one transaction-local effect from a fixed registered handler id.
/// Factories must not perform external effects; only the returned effect may
/// mutate the transaction draft.
/// </summary>
public interface IWorldTransactionalEventEffectFactory
{
    ValueTask<IWorldTransactionalEventEffect> CreateAsync(
        WorldTransactionalEffectFactoryContext context,
        CancellationToken cancellationToken);
}

public interface IWorldTransactionalEventEffectRegistry
{
    bool TryGetFactory(
        string effectHandlerId,
        out IWorldTransactionalEventEffectFactory? factory);
}

public sealed class WorldTransactionalEventEffectRegistryBuilder
{
    private readonly Dictionary<string, IWorldTransactionalEventEffectFactory>
        _factories = new(StringComparer.Ordinal);

    public WorldTransactionalEventEffectRegistryBuilder Add(
        string effectHandlerId,
        IWorldTransactionalEventEffectFactory factory)
    {
        var normalized = WorldValidation.Required(
            effectHandlerId,
            nameof(effectHandlerId));
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        if (!_factories.TryAdd(normalized, factory))
        {
            throw new ArgumentException(
                "An effect factory with the same identifier is already registered.",
                nameof(effectHandlerId));
        }

        return this;
    }

    public IWorldTransactionalEventEffectRegistry Build()
    {
        return new ImmutableTransactionalEventEffectRegistry(_factories);
    }

    private sealed class ImmutableTransactionalEventEffectRegistry
        : IWorldTransactionalEventEffectRegistry
    {
        private readonly IReadOnlyDictionary<
            string,
            IWorldTransactionalEventEffectFactory> _factories;

        public ImmutableTransactionalEventEffectRegistry(
            IReadOnlyDictionary<
                string,
                IWorldTransactionalEventEffectFactory> factories)
        {
            _factories = new ReadOnlyDictionary<
                string,
                IWorldTransactionalEventEffectFactory>(
                new Dictionary<
                    string,
                    IWorldTransactionalEventEffectFactory>(
                    factories,
                    StringComparer.Ordinal));
        }

        public bool TryGetFactory(
            string effectHandlerId,
            out IWorldTransactionalEventEffectFactory? factory)
        {
            return _factories.TryGetValue(effectHandlerId, out factory);
        }
    }
}

public enum WorldAuthoritativePlanExecutionStatus
{
    Completed = 0,
    PartiallyCompleted = 1,
    Rejected = 2,
    ReconciliationRequired = 3
}

public sealed class WorldEventPlanInstanceExecution
{
    internal WorldEventPlanInstanceExecution(
        int batchIndex,
        WorldEventInstance instance,
        string commandId,
        string operationId,
        WorldTransactionExecutionResult result)
    {
        BatchIndex = batchIndex;
        Instance = instance;
        CommandId = commandId;
        OperationId = operationId;
        Result = result;
    }

    public int BatchIndex { get; }

    public WorldEventInstance Instance { get; }

    public string CommandId { get; }

    public string OperationId { get; }

    public WorldTransactionExecutionResult Result { get; }

    public bool Succeeded =>
        Result.Status is WorldTransactionExecutionStatus.Committed
            or WorldTransactionExecutionStatus.Replayed;
}

public sealed class WorldAuthoritativePlanExecutionResult
{
    internal WorldAuthoritativePlanExecutionResult(
        WorldAuthoritativePlanExecutionStatus status,
        string reasonCode,
        IReadOnlyList<WorldEventPlanInstanceExecution> executions,
        WorldAuthoritativeCoordinate coordinate)
    {
        Status = status;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        Executions = executions;
        Coordinate = coordinate;
    }

    public WorldAuthoritativePlanExecutionStatus Status { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<WorldEventPlanInstanceExecution> Executions { get; }

    public WorldAuthoritativeCoordinate Coordinate { get; }

    public int SucceededCount => Executions.Count(item => item.Succeeded);

    public bool Succeeded =>
        Status == WorldAuthoritativePlanExecutionStatus.Completed
        && SucceededCount == Executions.Count;
}

public interface IWorldAuthoritativeEventPlanExecutor
{
    ValueTask<WorldAuthoritativePlanExecutionResult> ExecuteAsync(
        WorldEventPlanExecutionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes fixed event batches through the exactly-once transaction
/// executor. Batches and instances are processed in deterministic order.
/// </summary>
public sealed class WorldAuthoritativeEventPlanExecutor
    : IWorldAuthoritativeEventPlanExecutor,
      IWorldEventPlanExecutor
{
    private readonly IWorldAuthoritativeTransactionStore _store;
    private readonly WorldEventTransactionExecutor _transactions;
    private readonly IWorldTransactionalEventEffectRegistry _effects;

    public WorldAuthoritativeEventPlanExecutor(
        IWorldAuthoritativeTransactionStore store,
        IWorldTransactionalEventEffectRegistry effects)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transactions = new WorldEventTransactionExecutor(store);
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public async ValueTask<WorldAuthoritativePlanExecutionResult>
        ExecuteAsync(
            WorldEventPlanExecutionRequest request,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var coordinate = request.ExpectedCoordinate;
        var executions = new List<WorldEventPlanInstanceExecution>();
        foreach (var batch in request.Plan.ExecutionBatches)
        {
            foreach (var instance in batch.Instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var commandId = "world.command." + instance.InstanceId;
                var operationId = "world.operation." + instance.InstanceId;
                WorldTransactionExecutionResult result;
                var inspection = await _store.InspectAsync(
                        coordinate.Scope,
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (inspection.Status
                    != WorldTransactionInspectionStatus.NotFound)
                {
                    var storedFailure = ValidateStoredRecovery(
                        inspection,
                        coordinate,
                        instance,
                        commandId,
                        operationId);
                    if (storedFailure is not null)
                    {
                        result = new WorldTransactionExecutionResult(
                            WorldTransactionExecutionStatus.Rejected,
                            storedFailure,
                            null);
                    }
                    else
                    {
                        // Reconcile the exact request that was durably
                        // admitted. Recovery must not require rebuilding an
                        // effect or reproducing version-sensitive metadata.
                        result = await _transactions.ReconcileAsync(
                                inspection.Request!,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    if (!_effects.TryGetFactory(
                            instance.Definition.EffectHandlerId,
                            out var factory)
                        || factory is null)
                    {
                        executions.Add(
                            Failure(
                                batch.BatchIndex,
                                instance,
                                commandId,
                                operationId,
                                WorldAuthoritativePlanReasonCodes
                                    .EffectNotRegistered));
                        return Finish(executions, coordinate);
                    }

                    IWorldTransactionalEventEffect effect;
                    try
                    {
                        effect = await factory.CreateAsync(
                                new WorldTransactionalEffectFactoryContext(
                                    instance,
                                    coordinate,
                                    commandId,
                                    operationId,
                                    request.HostContext),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (effect is null)
                        {
                            throw new InvalidOperationException(
                                "The effect factory returned null.");
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        executions.Add(
                            Failure(
                                batch.BatchIndex,
                                instance,
                                commandId,
                                operationId,
                                WorldAuthoritativePlanReasonCodes
                                    .EffectFactoryFailed));
                        return Finish(executions, coordinate);
                    }

                    WorldEventTransactionExecutionRequest transactionRequest;
                    try
                    {
                        transactionRequest =
                            new WorldEventTransactionExecutionRequest(
                                instance,
                                coordinate,
                                commandId,
                                operationId,
                                effect,
                                hostContext: request.HostContext);
                    }
                    catch (ArgumentException)
                    {
                        executions.Add(
                            Failure(
                                batch.BatchIndex,
                                instance,
                                commandId,
                                operationId,
                                WorldAuthoritativePlanReasonCodes
                                    .EffectFactoryFailed));
                        return Finish(executions, coordinate);
                    }

                    // Compare the current exact state/catalog/incarnations
                    // before calling the transaction executor. BeginAsync
                    // repeats this fence under exclusive ownership.
                    var current = await _store.ReadAsync(
                            coordinate.Address,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var fenceFailure = ValidateCurrentFence(
                        current,
                        coordinate,
                        instance);
                    if (fenceFailure is not null)
                    {
                        result = new WorldTransactionExecutionResult(
                            WorldTransactionExecutionStatus.Rejected,
                            fenceFailure,
                            null);
                    }
                    else
                    {
                        result = await _transactions.ExecuteAsync(
                                transactionRequest,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                var execution = new WorldEventPlanInstanceExecution(
                    batch.BatchIndex,
                    instance,
                    commandId,
                    operationId,
                    result);
                executions.Add(execution);
                if (!execution.Succeeded)
                {
                    return Finish(executions, coordinate);
                }

                var next = result.Receipt?.ResultingCoordinate;
                if (next is null
                    || !string.Equals(
                        next.CatalogDigest,
                        request.Artifact.CatalogDigest,
                        StringComparison.Ordinal))
                {
                    executions.Add(
                        Failure(
                            batch.BatchIndex,
                            instance,
                            commandId,
                            operationId,
                            WorldAuthoritativePlanReasonCodes
                                .InvalidArtifact));
                    return Finish(executions, coordinate);
                }

                coordinate = next;
            }
        }

        return new WorldAuthoritativePlanExecutionResult(
            WorldAuthoritativePlanExecutionStatus.Completed,
            WorldAuthoritativePlanReasonCodes.Applied,
            new ReadOnlyCollection<WorldEventPlanInstanceExecution>(
                executions),
            coordinate);
    }

    public async ValueTask<WorldPlanExecutionResult> ExecuteAsync(
        WorldEventPlan plan,
        object? hostContext,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (hostContext is not WorldAuthoritativePlanExecutionContext context
            || !ReferenceEquals(context.Artifact.Plan, plan))
        {
            return new WorldPlanExecutionResult(
                WorldAuthoritativePlanReasonCodes.ExecutionContextRequired);
        }

        var typed = await ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    context.Artifact,
                    context.HostContext),
                cancellationToken)
            .ConfigureAwait(false);
        return new WorldPlanExecutionResult(
            typed.ReasonCode,
            WriteEvidence(typed));
    }

    private static WorldAuthoritativePlanExecutionResult Finish(
        IReadOnlyList<WorldEventPlanInstanceExecution> executions,
        WorldAuthoritativeCoordinate coordinate)
    {
        var succeeded = executions.Count(item => item.Succeeded);
        var last = executions.Last();
        if (last.Result.Status
            == WorldTransactionExecutionStatus.ReconciliationRequired)
        {
            return new WorldAuthoritativePlanExecutionResult(
                WorldAuthoritativePlanExecutionStatus
                    .ReconciliationRequired,
                WorldAuthoritativePlanReasonCodes.ReconciliationRequired,
                new ReadOnlyCollection<WorldEventPlanInstanceExecution>(
                    executions.ToArray()),
                coordinate);
        }

        return new WorldAuthoritativePlanExecutionResult(
            succeeded > 0
                ? WorldAuthoritativePlanExecutionStatus.PartiallyCompleted
                : WorldAuthoritativePlanExecutionStatus.Rejected,
            succeeded > 0
                ? WorldAuthoritativePlanReasonCodes.PartialFailure
                : last.Result.ReasonCode,
            new ReadOnlyCollection<WorldEventPlanInstanceExecution>(
                executions.ToArray()),
            coordinate);
    }

    private static WorldEventPlanInstanceExecution Failure(
        int batchIndex,
        WorldEventInstance instance,
        string commandId,
        string operationId,
        string reasonCode)
    {
        return new WorldEventPlanInstanceExecution(
            batchIndex,
            instance,
            commandId,
            operationId,
            new WorldTransactionExecutionResult(
                WorldTransactionExecutionStatus.Rejected,
                reasonCode,
                null));
    }

    private static string? ValidateCurrentFence(
        WorldAuthoritativeStateSnapshot? current,
        WorldAuthoritativeCoordinate expected,
        WorldEventInstance instance)
    {
        if (current is null)
        {
            return WorldTransactionReasonCodes.StateNotFound;
        }

        if (!expected.IsSameTimelineAs(current.Coordinate))
        {
            return WorldTransactionReasonCodes.StaleCoordinate;
        }

        if (!string.Equals(
                expected.CatalogDigest,
                current.Coordinate.CatalogDigest,
                StringComparison.Ordinal))
        {
            return WorldTransactionReasonCodes.StaleCatalog;
        }

        if (expected.SaveRevision != current.Coordinate.SaveRevision
            || expected.StateVersion != current.Coordinate.StateVersion)
        {
            return WorldTransactionReasonCodes.StaleVersion;
        }

        foreach (var participant in instance.Participants)
        {
            if (!current.EntityIncarnations.TryGetValue(
                    participant.EntityId,
                    out var incarnation)
                || incarnation != participant.Incarnation)
            {
                return WorldTransactionReasonCodes.StaleIncarnation;
            }
        }

        return null;
    }

    private static string? ValidateStoredRecovery(
        WorldTransactionInspectionResult inspection,
        WorldAuthoritativeCoordinate expected,
        WorldEventInstance instance,
        string commandId,
        string operationId)
    {
        var stored = inspection.Request;
        if (stored is null
            || !string.Equals(
                stored.CommandId,
                commandId,
                StringComparison.Ordinal)
            || !string.Equals(
                stored.OperationId,
                operationId,
                StringComparison.Ordinal)
            || !stored.ExpectedCoordinate.IsExactMatch(expected))
        {
            return WorldAuthoritativePlanReasonCodes.InvalidArtifact;
        }

        var occurrence = stored.EventOccurrence;
        if (occurrence is null
            || !occurrence.IsEquivalentTo(
                WorldEventHistoryRecord.FromInstance(instance)))
        {
            return WorldAuthoritativePlanReasonCodes.InvalidArtifact;
        }

        foreach (var participant in instance.Participants)
        {
            if (!stored.ExpectedIncarnations.Any(
                    expectation => string.Equals(
                                       expectation.EntityId,
                                       participant.EntityId,
                                       StringComparison.Ordinal)
                                   && expectation.Incarnation
                                   == participant.Incarnation))
            {
                return WorldAuthoritativePlanReasonCodes.InvalidArtifact;
            }
        }

        if (inspection.Status
            == WorldTransactionInspectionStatus.TerminalReceipt)
        {
            if (inspection.Receipt is null
                || !string.Equals(
                    inspection.Receipt.RequestFingerprint,
                    stored.RequestFingerprint,
                    StringComparison.Ordinal))
            {
                return WorldAuthoritativePlanReasonCodes.InvalidArtifact;
            }
        }
        else if (inspection.Receipt is not null)
        {
            return WorldAuthoritativePlanReasonCodes.InvalidArtifact;
        }

        return null;
    }

    private static JsonElement WriteEvidence(
        WorldAuthoritativePlanExecutionResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("status", result.Status.ToString());
            writer.WriteString("reasonCode", result.ReasonCode);
            writer.WriteString(
                "resultingSaveRevision",
                result.Coordinate.SaveRevision.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "resultingStateVersion",
                result.Coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString(
                "catalogDigest",
                result.Coordinate.CatalogDigest);
            writer.WritePropertyName("instances");
            writer.WriteStartArray();
            foreach (var execution in result.Executions)
            {
                writer.WriteStartObject();
                writer.WriteNumber("batchIndex", execution.BatchIndex);
                writer.WriteString(
                    "instanceId",
                    execution.Instance.InstanceId);
                writer.WriteString("commandId", execution.CommandId);
                writer.WriteString("operationId", execution.OperationId);
                writer.WriteString(
                    "status",
                    execution.Result.Status.ToString());
                writer.WriteString(
                    "reasonCode",
                    execution.Result.ReasonCode);
                if (execution.Result.Receipt is not null)
                {
                    writer.WriteString(
                        "receiptId",
                        execution.Result.Receipt.ReceiptId);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
}
