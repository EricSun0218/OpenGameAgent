using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.World;

namespace GameAgent.Runtime;

public interface IWorldAgentEvolutionRunner
{
    ValueTask<WorldAgentEvolutionResult> ExecuteAsync(
        WorldAgentEvolutionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<WorldAgentEvolutionResult> ResumeAsync(
        WorldAgentEvolutionCommand command,
        IReadOnlyDictionary<string, DurableRunContinuation>? continuations =
            null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default);

    ValueTask<WorldAgentEvolutionResult> CancelAsync(
        WorldAgentEvolutionCommand command,
        string reasonCode,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable composition root for simultaneous NPC decisions. It persists the
/// complete participant manifest before dispatch, resumes stable run IDs,
/// reduces results in manifest order, revalidates the live world, and submits
/// exactly one game-owned settlement transaction.
/// </summary>
public sealed class WorldAgentEvolutionRunner : IWorldAgentEvolutionRunner
{
    private const int MaxContinuationSnapshotUtf8BytesPerRun =
        4 * 1_048_576;
    private const int MaxContinuationSnapshotJsonNodesPerRun = 65_536;
    private const string WaitingReason = "world_evolution_waiting";
    private const string ReconciliationReason =
        "world_evolution_reconciliation_required";
    private const string CompletedReason = "world_evolution_completed";
    private const string RuntimePolicyStaleReason =
        "world_evolution_runtime_policy_stale";
    private const string ReducerPolicyStaleReason =
        "world_evolution_reducer_policy_stale";
    private const string ResumeInputChangedReason =
        "world_agent_resume_input_changed";

    private readonly WorldAgentRuntimeBridge _bridge;
    private readonly IWorldAuthoritativeTransactionStore _worldStore;
    private readonly IWorldAgentEvolutionStore _evolutionStore;
    private readonly IWorldAgentEvolutionReducerDescriptor _reducer;
    private readonly IWorldAgentRuntimePolicySnapshotSource
        _runtimePolicySource;
    private readonly WorldEventTransactionExecutor _transactions;
    private readonly MultiActorDecisionCoordinator _multiActor;
    private readonly WorldAgentEvolutionRunnerOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;

    public WorldAgentEvolutionRunner(
        IDurableAgentRuntime runtime,
        IWorldAgentRunInputFactory inputFactory,
        IWorldAuthoritativeTransactionStore worldStore,
        IWorldAgentEvolutionStore evolutionStore,
        IWorldAgentEvolutionReducerDescriptor reducer,
        IWorldAgentRuntimePolicySnapshotSource runtimePolicySource,
        WorldAgentEvolutionRunnerOptions? options = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }
        if (runtime is not IGuardedDurableAgentRuntime)
        {
            throw new ArgumentException(
                "World-agent evolution requires a durable runtime with "
                + "guarded resume support.",
                nameof(runtime));
        }

        _bridge = new WorldAgentRuntimeBridge(
            runtime,
            inputFactory
            ?? throw new ArgumentNullException(nameof(inputFactory)));
        _worldStore = worldStore
                      ?? throw new ArgumentNullException(nameof(worldStore));
        _evolutionStore = evolutionStore
                          ?? throw new ArgumentNullException(
                              nameof(evolutionStore));
        _reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
        _runtimePolicySource = runtimePolicySource
                               ?? throw new ArgumentNullException(
                                   nameof(runtimePolicySource));
        _ = EvolutionGuard.Required(
            _reducer.PolicyId,
            nameof(reducer),
            192);
        _ = EvolutionGuard.Digest(
            _reducer.PolicyDigest,
            nameof(reducer));
        _transactions = new WorldEventTransactionExecutor(worldStore);
        _options = options ?? new WorldAgentEvolutionRunnerOptions();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _multiActor = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: _options.MaxParticipants,
                maxConcurrentRuns: _options.MaxConcurrentActors,
                maxSnapshotUtf8BytesPerRun: Math.Min(
                    MaxContinuationSnapshotUtf8BytesPerRun,
                    _options.MaxBatchSnapshotUtf8Bytes),
                maxBatchSnapshotUtf8Bytes:
                    _options.MaxBatchSnapshotUtf8Bytes,
                maxSnapshotJsonNodesPerRun: Math.Min(
                    MaxContinuationSnapshotJsonNodesPerRun,
                    _options.MaxBatchSnapshotJsonNodes),
                maxBatchSnapshotJsonNodes:
                    _options.MaxBatchSnapshotJsonNodes));
    }

    public ValueTask<WorldAgentEvolutionResult> ExecuteAsync(
        WorldAgentEvolutionCommand command,
        CancellationToken cancellationToken = default)
    {
        return ContinueAsync(
            command,
            continuations: null,
            reconciler: null,
            requestCancellation: false,
            cancellationReason: null,
            cancellationToken);
    }

    public ValueTask<WorldAgentEvolutionResult> ResumeAsync(
        WorldAgentEvolutionCommand command,
        IReadOnlyDictionary<string, DurableRunContinuation>? continuations =
            null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        return ContinueAsync(
            command,
            continuations,
            reconciler,
            requestCancellation: false,
            cancellationReason: null,
            cancellationToken);
    }

    public ValueTask<WorldAgentEvolutionResult> CancelAsync(
        WorldAgentEvolutionCommand command,
        string reasonCode,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        reasonCode = EvolutionGuard.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        return ContinueAsync(
            command,
            continuations: null,
            reconciler,
            requestCancellation: true,
            cancellationReason: reasonCode,
            cancellationToken);
    }

    private async ValueTask<WorldAgentEvolutionResult> ContinueAsync(
        WorldAgentEvolutionCommand command,
        IReadOnlyDictionary<string, DurableRunContinuation>? continuations,
        IGameOperationReconciler? reconciler,
        bool requestCancellation,
        string? cancellationReason,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (command.Participants.Count > _options.MaxParticipants)
        {
            throw new ArgumentException(
                "The evolution batch exceeds the configured participant limit.",
                nameof(command));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var continuationSnapshot = SnapshotContinuations(
            command,
            continuations,
            cancellationToken);
        var ownerKey = "evolution-owner-" + Guid.NewGuid().ToString("N");
        var checkpoint = await _evolutionStore.ReadAsync(
                command.CommandId,
                cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is not null
            && !string.Equals(
                checkpoint.CommandDigest,
                command.SemanticDigest,
                StringComparison.Ordinal))
        {
            return Result(
                command,
                WorldAgentEvolutionStatus.Rejected,
                WorldAgentEvolutionStage.Rejected,
                "world_evolution_idempotency_conflict",
                checkpoint.Revision);
        }

        EvolutionState state;
        MultiActorPreparedBatch? initialBatch = null;
        var created = false;
        if (checkpoint is null)
        {
            if (!ReducerPolicyMatches(command))
            {
                return Result(
                    command,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    ReducerPolicyStaleReason,
                    checkpointRevision: 0);
            }

            var runtimePolicy = CaptureRuntimePolicy();
            if (!command.RuntimeGeneration.Matches(runtimePolicy))
            {
                return Result(
                    command,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    RuntimePolicyStaleReason,
                    checkpointRevision: 0);
            }

            var snapshot = await ReadExactSnapshotAsync(
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return Result(
                    command,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    "world_evolution_coordinate_stale",
                    checkpointRevision: 0);
            }

            var initialRequests = await PrepareRequestsAsync(
                    command,
                    runtimePolicy,
                    cancellationToken)
                .ConfigureAwait(false);
            initialBatch = _multiActor.PrepareBatch(
                CreateDecisionBatch(command, initialRequests),
                cancellationToken);
            state = EvolutionState.Create(
                command,
                snapshot.StateDigest,
                initialBatch.RequestDigests,
                initialBatch.Digest,
                ownerKey,
                _utcNow().Add(_options.OwnerLeaseDuration));
            var proposedCheckpoint = state.ToCheckpoint(
                command,
                revision: 1);
            var create = await _evolutionStore.CompareExchangeAsync(
                    proposedCheckpoint,
                    expectedRevision: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            checkpoint = create.Current;
            if (!string.Equals(
                    checkpoint.CommandDigest,
                    command.SemanticDigest,
                    StringComparison.Ordinal))
            {
                return Result(
                    command,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    "world_evolution_idempotency_conflict",
                    checkpoint.Revision);
            }

            if ((create.Status
                     is WorldAgentEvolutionStoreWriteStatus.Written
                     or WorldAgentEvolutionStoreWriteStatus.Duplicate)
                && CheckpointsMatch(proposedCheckpoint, create.Current))
            {
                created = true;
            }
            else if (create.Status
                     is WorldAgentEvolutionStoreWriteStatus.Written
                         or WorldAgentEvolutionStoreWriteStatus.Duplicate)
            {
                throw new InvalidDataException(
                    "The evolution store returned duplicate for a "
                    + "different initial checkpoint.");
            }
            else
            {
                initialBatch = null;
                state = EvolutionState.FromCheckpoint(
                    command,
                    checkpoint);
            }
        }
        else
        {
            state = EvolutionState.FromCheckpoint(command, checkpoint);
        }

        if (!string.Equals(
                checkpoint.CommandDigest,
                command.SemanticDigest,
                StringComparison.Ordinal))
        {
            return Result(
                command,
                WorldAgentEvolutionStatus.Rejected,
                WorldAgentEvolutionStage.Rejected,
                "world_evolution_idempotency_conflict",
                checkpoint.Revision);
        }

        if (state.IsTerminal)
        {
            var receipt = await ReadTerminalReceiptAsync(
                    command,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            return TerminalResult(
                command,
                state,
                checkpoint,
                replayCompleted: true,
                receipt);
        }

        if (!created)
        {
            var acquired = await TryAcquireAsync(
                    command,
                    checkpoint,
                    state,
                    ownerKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                return Result(
                    command,
                    WorldAgentEvolutionStatus.Busy,
                    state.Stage,
                    "world_evolution_owned",
                    checkpoint.Revision,
                    RehydrateActorResults(command, state));
            }

            checkpoint = acquired.Value.Checkpoint;
            state = acquired.Value.State;
        }

        var ownerId = new EvolutionOwnerToken(
            ownerKey,
            state.OwnerGeneration);
        var ownership = new EvolutionOwnershipCursor(
            checkpoint,
            state,
            ownerId);
        try
        {
            return await ContinueOwnedAsync(
                    command,
                    ownership,
                    initialBatch,
                    continuationSnapshot,
                    reconciler,
                    requestCancellation,
                    cancellationReason,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EvolutionOwnershipLostException)
        {
            var latest = await _evolutionStore.ReadAsync(
                    command.CommandId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return Result(
                command,
                WorldAgentEvolutionStatus.ReconciliationRequired,
                latest is null
                    ? state.Stage
                    : EvolutionState.FromCheckpoint(command, latest).Stage,
                "world_evolution_ownership_lost",
                latest?.Revision ?? checkpoint.Revision);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await TryReleaseOwnershipAsync(
                    command,
                    ownerId)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            await TryReleaseOwnershipAsync(
                    command,
                    ownerId)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<WorldAgentEvolutionResult>
        ContinueOwnedAsync(
            WorldAgentEvolutionCommand command,
            EvolutionOwnershipCursor ownership,
            MultiActorPreparedBatch? initialBatch,
            IReadOnlyDictionary<string, DurableRunContinuation> continuations,
            IGameOperationReconciler? reconciler,
            bool requestCancellation,
            string? cancellationReason,
            CancellationToken cancellationToken)
    {
        var state = ownership.State;
        var checkpoint = ownership.Checkpoint;
        var ownerId = ownership.Owner;
        if (state.WorldOperationMayExist)
        {
            var recovered = await ReconcileCommittedTransactionAsync(
                    command,
                    ownership,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovered is not null)
            {
                return recovered;
            }

            checkpoint = ownership.Checkpoint;
        }

        if (requestCancellation
            && state.Stage
            == WorldAgentEvolutionStage.ActorManifestCommitted)
        {
            state.SetActorResults(
                CancelUnsettledActors(command, state));
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Cancelled,
                    WorldAgentEvolutionStage.Cancelled,
                    cancellationReason ?? "world_evolution_cancelled",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!state.HasSettledActors)
        {
            if (!ReducerPolicyMatches(command))
            {
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        ReducerPolicyStaleReason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!command.RuntimeGeneration.Matches(
                    CaptureRuntimePolicy()))
            {
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        RuntimePolicyStaleReason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var currentRead =
                await RunOwnedOperationWithHeartbeatAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        token => ReadExactSnapshotAsync(command, token),
                        updated => checkpoint = updated,
                        cancellationToken)
                .ConfigureAwait(false);
            checkpoint = currentRead.Checkpoint;
            var current = currentRead.Value;
            if (current is null
                || !string.Equals(
                    current.StateDigest,
                    state.CapturedStateDigest,
                    StringComparison.Ordinal))
            {
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Rejected,
                        WorldAgentEvolutionStage.Rejected,
                        "world_evolution_coordinate_stale",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            checkpoint = await WriteOwnedAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStage.ActorsRunning,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<WorldAgentDecisionProposalResult> actorResults;
            try
            {
                if (!ReducerPolicyMatches(command))
                {
                    return await PauseAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            WorldAgentEvolutionStatus
                                .ReconciliationRequired,
                            WorldAgentEvolutionStage
                                .ReconciliationRequired,
                            ReducerPolicyStaleReason,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!command.RuntimeGeneration.Matches(
                        CaptureRuntimePolicy()))
                {
                    return await PauseAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            WorldAgentEvolutionStatus
                                .ReconciliationRequired,
                            WorldAgentEvolutionStage
                                .ReconciliationRequired,
                            RuntimePolicyStaleReason,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var actorOperation =
                    await RunOwnedOperationWithHeartbeatAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            token => initialBatch is not null
                                ? RunInitialBatchAsync(
                                    command,
                                    state,
                                    initialBatch,
                                    token)
                                : ResumeActorsAsync(
                                    command,
                                    state,
                                    continuations,
                                    reconciler,
                                    requestCancellation,
                                    token),
                            updated => checkpoint = updated,
                            cancellationToken)
                        .ConfigureAwait(false);
                checkpoint = actorOperation.Checkpoint;
                actorResults = actorOperation.Value;
                var reducerPolicyCurrent =
                    ReducerPolicyMatches(command);
                var runtimePolicyCurrent =
                    command.RuntimeGeneration.Matches(
                        CaptureRuntimePolicy());
                if (!reducerPolicyCurrent || !runtimePolicyCurrent)
                {
                    return await PauseAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            WorldAgentEvolutionStatus
                                .ReconciliationRequired,
                            WorldAgentEvolutionStage
                                .ReconciliationRequired,
                            !reducerPolicyCurrent
                                ? ReducerPolicyStaleReason
                                : RuntimePolicyStaleReason,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException)
            {
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Rejected,
                        WorldAgentEvolutionStage.Rejected,
                        "world_evolution_actor_manifest_rejected",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EvolutionPreparedBatchMismatchException)
            {
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        ResumeInputChangedReason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        "world_evolution_actor_dispatch_uncertain",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            state.SetActorResults(actorResults);

            var actorControl = ClassifyActors(
                state,
                requestCancellation);
            if (actorControl is not null)
            {
                var released = actorControl.Value.Status
                               is WorldAgentEvolutionStatus.Waiting
                                   or WorldAgentEvolutionStatus
                                       .ReconciliationRequired;
                return released
                    ? await PauseAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            actorControl.Value.Status,
                            actorControl.Value.Stage,
                            actorControl.Value.ReasonCode,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await FinishAsync(
                            command,
                            checkpoint,
                            state,
                            ownerId,
                            actorControl.Value.Status,
                            actorControl.Value.Stage,
                            requestCancellation
                                ? cancellationReason
                                  ?? actorControl.Value.ReasonCode
                                : actorControl.Value.ReasonCode,
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            state.HasSettledActors = true;
            checkpoint = await WriteOwnedAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStage.Reducing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (requestCancellation)
        {
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Cancelled,
                    WorldAgentEvolutionStage.Cancelled,
                    cancellationReason ?? "world_evolution_cancelled",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var capturedRead = await RunOwnedOperationWithHeartbeatAsync(
                command,
                checkpoint,
                state,
                ownerId,
                token => ReadExactSnapshotAsync(command, token),
                updated => checkpoint = updated,
                cancellationToken)
            .ConfigureAwait(false);
        checkpoint = capturedRead.Checkpoint;
        var captured = capturedRead.Value;
        if (captured is null
            || !string.Equals(
                captured.StateDigest,
                state.CapturedStateDigest,
                StringComparison.Ordinal))
        {
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    "world_evolution_coordinate_stale",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!ReducerPolicyMatches(command))
        {
            return await PauseAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.ReconciliationRequired,
                    WorldAgentEvolutionStage.ReconciliationRequired,
                    ReducerPolicyStaleReason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var actorResultSnapshot = RehydrateActorResults(command, state);
        WorldAgentEvolutionReduction reduction;
        try
        {
            var reductionOperation =
                await RunOwnedOperationWithHeartbeatAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        token => _reducer.ReduceAsync(
                            new WorldAgentEvolutionReductionContext(
                                command,
                                captured,
                                actorResultSnapshot),
                            token),
                        updated => checkpoint = updated,
                        cancellationToken)
                    .ConfigureAwait(false);
            checkpoint = reductionOperation.Checkpoint;
            reduction = reductionOperation.Value
                        ?? throw new InvalidOperationException(
                            "The evolution reducer returned null.");
            if (!ReducerPolicyMatches(command))
            {
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus
                            .ReconciliationRequired,
                        WorldAgentEvolutionStage
                            .ReconciliationRequired,
                        ReducerPolicyStaleReason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Failed,
                    WorldAgentEvolutionStage.Failed,
                    "world_evolution_reducer_failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var requestFingerprint =
            reduction.Transaction?.TransactionRequest.RequestFingerprint;
        if (state.ReductionEvidenceDigest is not null)
        {
            if (state.ReductionDisposition != reduction.Disposition
                || !string.Equals(
                    state.ReductionReasonCode,
                    reduction.ReasonCode,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.ReductionEvidenceDigest,
                    reduction.EvidenceDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.RequestFingerprint,
                    requestFingerprint,
                    StringComparison.Ordinal))
            {
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Rejected,
                        WorldAgentEvolutionStage.Rejected,
                        "world_evolution_reducer_nondeterministic",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            state.SetReduction(reduction, requestFingerprint);
        }

        switch (reduction.Disposition)
        {
            case WorldAgentEvolutionReductionDisposition.NoChange:
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Completed,
                        WorldAgentEvolutionStage.Completed,
                        reduction.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            case WorldAgentEvolutionReductionDisposition.Waiting:
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Waiting,
                        WorldAgentEvolutionStage.Waiting,
                        reduction.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            case WorldAgentEvolutionReductionDisposition.Rejected:
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Rejected,
                        WorldAgentEvolutionStage.Rejected,
                        reduction.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            case WorldAgentEvolutionReductionDisposition.Commit:
                break;
            default:
                throw new InvalidOperationException(
                    "Unknown evolution reduction disposition.");
        }

        var request = reduction.Transaction!;
        if (!ValidateSettlement(command, request))
        {
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    "world_evolution_settlement_invalid",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        checkpoint = await WriteOwnedAsync(
                command,
                checkpoint,
                state,
                ownerId,
                WorldAgentEvolutionStage.WorldCommitPending,
                cancellationToken)
            .ConfigureAwait(false);
        var freshRead = await RunOwnedOperationWithHeartbeatAsync(
                command,
                checkpoint,
                state,
                ownerId,
                token => ReadExactSnapshotAsync(command, token),
                updated => checkpoint = updated,
                cancellationToken)
            .ConfigureAwait(false);
        checkpoint = freshRead.Checkpoint;
        var fresh = freshRead.Value;
        if (fresh is null
            || !string.Equals(
                fresh.StateDigest,
                state.CapturedStateDigest,
                StringComparison.Ordinal))
        {
            return await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    "world_evolution_coordinate_stale",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        state.MarkWorldOperationMayExist();
        checkpoint = await WriteOwnedAsync(
                command,
                checkpoint,
                state,
                ownerId,
                WorldAgentEvolutionStage.WorldCommitPending,
                cancellationToken)
            .ConfigureAwait(false);
        OwnedOperationResult<WorldTransactionExecutionResult>
            transactionOperation;
        try
        {
            transactionOperation =
                await RunOwnedOperationWithHeartbeatAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        async token =>
                        {
                            var existing =
                                await _transactions.ReconcileAsync(
                                        request.TransactionRequest,
                                        token)
                                    .ConfigureAwait(false);
                            return existing.Status
                                   == WorldTransactionExecutionStatus.NotFound
                                ? await _transactions.ExecuteAsync(
                                        request,
                                        token)
                                    .ConfigureAwait(false)
                                : existing;
                        },
                        updated => checkpoint = updated,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EvolutionOwnershipLostException)
        {
            throw;
        }
        catch
        {
            return await PauseAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.ReconciliationRequired,
                    WorldAgentEvolutionStage.ReconciliationRequired,
                    "world_evolution_world_commit_uncertain",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        checkpoint = transactionOperation.Checkpoint;
        var execution = transactionOperation.Value;
        return await FinishTransactionAsync(
                command,
                checkpoint,
                state,
                ownerId,
                execution,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorldAgentEvolutionResult?>
        ReconcileCommittedTransactionAsync(
            WorldAgentEvolutionCommand command,
            EvolutionOwnershipCursor ownership,
            CancellationToken cancellationToken)
    {
        var checkpoint = ownership.Checkpoint;
        var state = ownership.State;
        var ownerId = ownership.Owner;
        var scope = new WorldTransactionScope(
            command.ExpectedCoordinate.WorldId,
            command.ExpectedCoordinate.TimelineId,
            command.ExpectedCoordinate.TimelineEpoch);
        OwnedOperationResult<WorldTransactionReconciliationResult>
            reconciliationOperation;
        try
        {
            reconciliationOperation =
                await RunOwnedOperationWithHeartbeatAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        token => _worldStore.ReconcileAsync(
                            scope,
                            command.OperationId,
                            state.RequestFingerprint!,
                            token),
                        updated =>
                        {
                            checkpoint = updated;
                            ownership.Checkpoint = updated;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EvolutionOwnershipLostException)
        {
            throw;
        }
        catch
        {
            return await PauseAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.ReconciliationRequired,
                    WorldAgentEvolutionStage.ReconciliationRequired,
                    "world_evolution_world_reconcile_uncertain",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        checkpoint = reconciliationOperation.Checkpoint;
        ownership.Checkpoint = checkpoint;
        var reconciliation = reconciliationOperation.Value;
        switch (reconciliation.Status)
        {
            case WorldTransactionReconciliationStatus.TerminalReceipt:
                var receipt = reconciliation.Receipt
                              ?? throw new InvalidDataException(
                                  "Terminal reconciliation omitted its receipt.");
                EnsureReceiptMatchesEvolution(command, state, receipt);
                state.TerminalStatus = receipt.Status
                    == WorldCommandReceiptStatus.Applied
                        ? WorldAgentEvolutionStatus.Completed
                        : receipt.Status
                            == WorldCommandReceiptStatus.Cancelled
                            ? WorldAgentEvolutionStatus.Cancelled
                            : WorldAgentEvolutionStatus.Rejected;
                state.TerminalReasonCode = receipt.OutcomeCode;
                state.ReceiptId = receipt.ReceiptId;
                state.Stage = state.TerminalStatus
                    == WorldAgentEvolutionStatus.Completed
                        ? WorldAgentEvolutionStage.Completed
                        : state.TerminalStatus
                            == WorldAgentEvolutionStatus.Cancelled
                            ? WorldAgentEvolutionStage.Cancelled
                            : WorldAgentEvolutionStage.Rejected;
                state.EnsureActiveOwner(ownerId, _utcNow());
                state.ClearOwner();
                checkpoint = await WriteCheckpointAsync(
                        command,
                        checkpoint,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                ownership.Checkpoint = checkpoint;
                return new WorldAgentEvolutionResult(
                    command.CommandId,
                    state.TerminalStatus
                    == WorldAgentEvolutionStatus.Completed
                        ? WorldAgentEvolutionStatus.Replayed
                        : state.TerminalStatus!.Value,
                    state.Stage,
                    state.TerminalReasonCode,
                    checkpoint.Revision,
                    RehydrateActorResults(command, state),
                    receipt: receipt);
            case WorldTransactionReconciliationStatus.Pending:
                return await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        reconciliation.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            case WorldTransactionReconciliationStatus.IdempotencyConflict:
                return await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Rejected,
                        WorldAgentEvolutionStage.Rejected,
                        reconciliation.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            case WorldTransactionReconciliationStatus.NotFound:
                return null;
            default:
                throw new InvalidOperationException(
                    "Unknown world reconciliation status.");
        }
    }

    private async ValueTask<WorldCommandReceipt?> ReadTerminalReceiptAsync(
        WorldAgentEvolutionCommand command,
        EvolutionState state,
        CancellationToken cancellationToken)
    {
        if (state.ReceiptId is null)
        {
            return null;
        }

        var reconciliation = await _worldStore.ReconcileAsync(
                new WorldTransactionScope(
                    command.ExpectedCoordinate.WorldId,
                    command.ExpectedCoordinate.TimelineId,
                command.ExpectedCoordinate.TimelineEpoch),
                command.OperationId,
                state.RequestFingerprint!,
                cancellationToken)
            .ConfigureAwait(false);
        var receipt = reconciliation.Receipt;
        if (receipt is not null)
        {
            EnsureReceiptMatchesEvolution(command, state, receipt);
        }

        var receiptStatus = receipt?.Status
            == WorldCommandReceiptStatus.Applied
                ? WorldAgentEvolutionStatus.Completed
                : receipt?.Status
                    == WorldCommandReceiptStatus.Cancelled
                    ? WorldAgentEvolutionStatus.Cancelled
                    : WorldAgentEvolutionStatus.Rejected;
        if (reconciliation.Status
            != WorldTransactionReconciliationStatus.TerminalReceipt
            || receipt is null
            || state.ReceiptId is not null
            && !string.Equals(
                state.ReceiptId,
                receipt.ReceiptId,
                StringComparison.Ordinal)
            || state.TerminalStatus != receiptStatus
            || !string.Equals(
                state.TerminalReasonCode,
                receipt.OutcomeCode,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Terminal evolution evidence does not match the world receipt.");
        }

        return receipt;
    }

    private async ValueTask<WorldAgentEvolutionResult>
        FinishTransactionAsync(
            WorldAgentEvolutionCommand command,
            WorldAgentEvolutionCheckpoint checkpoint,
            EvolutionState state,
            EvolutionOwnerToken ownerId,
            WorldTransactionExecutionResult execution,
            CancellationToken cancellationToken)
    {
        return execution.Status switch
        {
            WorldTransactionExecutionStatus.Committed =>
                await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Completed,
                        WorldAgentEvolutionStage.Completed,
                        execution.ReasonCode,
                        cancellationToken,
                        execution)
                    .ConfigureAwait(false),
            WorldTransactionExecutionStatus.Replayed =>
                await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Replayed,
                        WorldAgentEvolutionStage.Completed,
                        execution.ReasonCode,
                        cancellationToken,
                        execution)
                    .ConfigureAwait(false),
            WorldTransactionExecutionStatus.Busy =>
                await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Waiting,
                        WorldAgentEvolutionStage.WorldCommitPending,
                        execution.ReasonCode,
                        cancellationToken,
                        execution)
                    .ConfigureAwait(false),
            WorldTransactionExecutionStatus.ReconciliationRequired =>
                await PauseAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.ReconciliationRequired,
                        WorldAgentEvolutionStage.ReconciliationRequired,
                        execution.ReasonCode,
                        cancellationToken,
                        execution)
                    .ConfigureAwait(false),
            WorldTransactionExecutionStatus.Cancelled =>
                await FinishAsync(
                        command,
                        checkpoint,
                        state,
                        ownerId,
                        WorldAgentEvolutionStatus.Cancelled,
                        WorldAgentEvolutionStage.Cancelled,
                        execution.ReasonCode,
                        cancellationToken,
                        execution)
                    .ConfigureAwait(false),
            _ => await FinishAsync(
                    command,
                    checkpoint,
                    state,
                    ownerId,
                    WorldAgentEvolutionStatus.Rejected,
                    WorldAgentEvolutionStage.Rejected,
                    execution.ReasonCode,
                    cancellationToken,
                    execution)
                .ConfigureAwait(false)
        };
    }

    private async ValueTask<WorldAgentEvolutionResult> FinishAsync(
        WorldAgentEvolutionCommand command,
        WorldAgentEvolutionCheckpoint checkpoint,
        EvolutionState state,
        EvolutionOwnerToken ownerId,
        WorldAgentEvolutionStatus status,
        WorldAgentEvolutionStage stage,
        string reasonCode,
        CancellationToken cancellationToken,
        WorldTransactionExecutionResult? transaction = null)
    {
        if (transaction?.Receipt is { } receipt)
        {
            EnsureReceiptMatchesEvolution(command, state, receipt);
        }

        state.EnsureActiveOwner(ownerId, _utcNow());
        state.TerminalStatus = status
                               == WorldAgentEvolutionStatus.Replayed
            ? WorldAgentEvolutionStatus.Completed
            : status;
        state.TerminalReasonCode = reasonCode;
        state.ReceiptId = transaction?.Receipt?.ReceiptId;
        if (state.ReceiptId is null)
        {
            state.ClearWorldOperationMayExist();
        }

        state.Stage = stage;
        state.ClearOwner();
        checkpoint = await WriteCheckpointAsync(
                command,
                checkpoint,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        return new WorldAgentEvolutionResult(
            command.CommandId,
            status,
            stage,
            reasonCode,
            checkpoint.Revision,
            RehydrateActorResults(command, state),
            transaction);
    }

    private static void EnsureReceiptMatchesEvolution(
        WorldAgentEvolutionCommand command,
        EvolutionState state,
        WorldCommandReceipt receipt)
    {
        if (state.RequestFingerprint is null
            || !string.Equals(
                receipt.OperationId,
                command.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.CommandId,
                command.CommandId,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.RequestFingerprint,
                state.RequestFingerprint,
                StringComparison.Ordinal)
            || !command.ExpectedCoordinate.IsExactMatch(
                receipt.ExpectedCoordinate))
        {
            throw new InvalidDataException(
                "The world receipt is not bound to this evolution command.");
        }
    }

    private async ValueTask<WorldAgentEvolutionResult> PauseAsync(
        WorldAgentEvolutionCommand command,
        WorldAgentEvolutionCheckpoint checkpoint,
        EvolutionState state,
        EvolutionOwnerToken ownerId,
        WorldAgentEvolutionStatus status,
        WorldAgentEvolutionStage stage,
        string reasonCode,
        CancellationToken cancellationToken,
        WorldTransactionExecutionResult? transaction = null)
    {
        state.EnsureActiveOwner(ownerId, _utcNow());
        state.Stage = stage;
        state.PauseReasonCode = reasonCode;
        state.ClearOwner();
        checkpoint = await WriteCheckpointAsync(
                command,
                checkpoint,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        return new WorldAgentEvolutionResult(
            command.CommandId,
            status,
            stage,
            reasonCode,
            checkpoint.Revision,
            RehydrateActorResults(command, state),
            transaction);
    }

    private async ValueTask<
        (WorldAgentEvolutionCheckpoint Checkpoint, EvolutionState State)?>
        TryAcquireAsync(
            WorldAgentEvolutionCommand command,
            WorldAgentEvolutionCheckpoint checkpoint,
            EvolutionState state,
            string ownerId,
            CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (state.OwnerId is not null
            && state.OwnerLeaseExpiresAt.HasValue
            && state.OwnerLeaseExpiresAt.Value > now)
        {
            return null;
        }

        state.OwnerId = ownerId;
        state.OwnerGeneration = checked(state.OwnerGeneration + 1);
        state.OwnerLeaseExpiresAt =
            now.Add(_options.OwnerLeaseDuration);
        var proposedCheckpoint = state.ToCheckpoint(
            command,
            checked(checkpoint.Revision + 1));
        var write = await _evolutionStore.CompareExchangeAsync(
                proposedCheckpoint,
                checkpoint.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        if (write.Status == WorldAgentEvolutionStoreWriteStatus.Conflict)
        {
            return null;
        }

        if (!CheckpointsMatch(proposedCheckpoint, write.Current))
        {
            throw new InvalidDataException(
                "The evolution store acknowledged a different ownership "
                + "checkpoint.");
        }

        return (
            write.Current,
            EvolutionState.FromCheckpoint(command, write.Current));
    }

    private async ValueTask<WorldAgentEvolutionCheckpoint> WriteOwnedAsync(
        WorldAgentEvolutionCommand command,
        WorldAgentEvolutionCheckpoint checkpoint,
        EvolutionState state,
        EvolutionOwnerToken ownerId,
        WorldAgentEvolutionStage stage,
        CancellationToken cancellationToken)
    {
        state.EnsureActiveOwner(ownerId, _utcNow());
        var previousStage = state.Stage;
        state.Stage = stage;
        var previousExpiry = state.OwnerLeaseExpiresAt;
        state.OwnerLeaseExpiresAt =
            _utcNow().Add(_options.OwnerLeaseDuration);
        try
        {
            return await WriteCheckpointAsync(
                    command,
                    checkpoint,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            state.Stage = previousStage;
            state.OwnerLeaseExpiresAt = previousExpiry;
            throw;
        }
    }

    private async ValueTask<OwnedOperationResult<T>>
        RunOwnedOperationWithHeartbeatAsync<T>(
            WorldAgentEvolutionCommand command,
            WorldAgentEvolutionCheckpoint checkpoint,
            EvolutionState state,
            EvolutionOwnerToken ownerId,
            Func<CancellationToken, ValueTask<T>> operation,
            Action<WorldAgentEvolutionCheckpoint> checkpointUpdated,
            CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (checkpointUpdated is null)
        {
            throw new ArgumentNullException(nameof(checkpointUpdated));
        }

        state.EnsureActiveOwner(ownerId, _utcNow());
        using var executionSignal =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var heartbeat = new EvolutionHeartbeatState(checkpoint);
        var heartbeatTask = RunOwnershipHeartbeatLoopAsync(
            command,
            state,
            ownerId,
            heartbeat,
            executionSignal);
        Task<T>? operationTask = null;
        T? result = default;
        Exception? operationFailure = null;
        try
        {
            try
            {
                operationTask = operation(
                        executionSignal.Token)
                    .AsTask();
                result = await operationTask.ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                operationFailure = exception;
            }
        }
        finally
        {
            executionSignal.Cancel();
            await heartbeatTask.ConfigureAwait(false);
            checkpoint = heartbeat.Checkpoint;
            checkpointUpdated(checkpoint);
        }

        if (heartbeat.Failure is not null)
        {
            ExceptionDispatchInfo.Capture(heartbeat.Failure).Throw();
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return new OwnedOperationResult<T>(
            checkpoint,
            result!);
    }

    private async Task RunOwnershipHeartbeatLoopAsync(
        WorldAgentEvolutionCommand command,
        EvolutionState state,
        EvolutionOwnerToken ownerId,
        EvolutionHeartbeatState heartbeat,
        CancellationTokenSource executionSignal)
    {
        var intervalTicks = Math.Min(
            TimeSpan.FromSeconds(15).Ticks,
            Math.Max(
                TimeSpan.FromMilliseconds(100).Ticks,
                _options.OwnerLeaseDuration.Ticks / 3));
        var heartbeatInterval = TimeSpan.FromTicks(intervalTicks);
        try
        {
            while (true)
            {
                await Task.Delay(
                        heartbeatInterval,
                        executionSignal.Token)
                    .ConfigureAwait(false);
                var updated = await WriteOwnedAsync(
                        command,
                        heartbeat.Checkpoint,
                        state,
                        ownerId,
                        state.Stage,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                heartbeat.Checkpoint = updated;
            }
        }
        catch (OperationCanceledException)
            when (executionSignal.IsCancellationRequested)
        {
            // The operation settled or caller cancellation propagated.
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            heartbeat.Failure = exception;
            executionSignal.Cancel();
        }
    }

    private async ValueTask<WorldAgentEvolutionCheckpoint>
        WriteCheckpointAsync(
            WorldAgentEvolutionCommand command,
            WorldAgentEvolutionCheckpoint checkpoint,
            EvolutionState state,
            CancellationToken cancellationToken)
    {
        var proposedCheckpoint = state.ToCheckpoint(
            command,
            checked(checkpoint.Revision + 1));
        var write = await _evolutionStore.CompareExchangeAsync(
                proposedCheckpoint,
                checkpoint.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        if (write.Status == WorldAgentEvolutionStoreWriteStatus.Conflict)
        {
            throw new EvolutionOwnershipLostException();
        }

        if (!CheckpointsMatch(proposedCheckpoint, write.Current))
        {
            throw new InvalidDataException(
                "The evolution store acknowledged a different checkpoint.");
        }

        return write.Current;
    }

    private async ValueTask TryReleaseOwnershipAsync(
        WorldAgentEvolutionCommand command,
        EvolutionOwnerToken ownerId)
    {
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var checkpoint = await _evolutionStore.ReadAsync(
                        command.CommandId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (checkpoint is null)
                {
                    return;
                }

                var state = EvolutionState.FromCheckpoint(
                    command,
                    checkpoint);
                if (!string.Equals(
                        state.OwnerId,
                        ownerId.Id,
                        StringComparison.Ordinal)
                    || state.OwnerGeneration != ownerId.Generation)
                {
                    return;
                }

                state.ClearOwner();
                var write = await _evolutionStore.CompareExchangeAsync(
                        state.ToCheckpoint(
                            command,
                            checked(checkpoint.Revision + 1)),
                        checkpoint.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (write.Status
                    == WorldAgentEvolutionStoreWriteStatus.Written)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            // Recovery can safely take over after the persisted lease expires.
        }
    }

    private static IReadOnlyList<WorldAgentDecisionProposalResult>
        CancelUnsettledActors(
            WorldAgentEvolutionCommand command,
            EvolutionState state)
    {
        var results = new WorldAgentDecisionProposalResult[
            command.Participants.Count];
        for (var index = 0; index < results.Length; index++)
        {
            var evidence = state.Actors[index];
            results[index] = evidence.IsSettled
                ? RehydrateActorResult(
                        command.Participants[index],
                        evidence)
                    .ProposalResult
                : CancelledProposal(
                    command.Participants[index].Job);
        }

        return new ReadOnlyCollection<WorldAgentDecisionProposalResult>(
            results);
    }

    private async ValueTask<IReadOnlyList<DurableRunRequest>>
        PrepareRequestsAsync(
            WorldAgentEvolutionCommand command,
            WorldAgentRuntimeGeneration runtimePolicy,
            CancellationToken cancellationToken)
    {
        var requests = new DurableRunRequest[command.Participants.Count];
        var next = -1;
        var workers = new Task[
            Math.Min(_options.MaxConcurrentActors, requests.Length)];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = PrepareWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return new ReadOnlyCollection<DurableRunRequest>(requests);

        async Task PrepareWorkerAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref next);
                if (index >= requests.Length)
                {
                    return;
                }

                var participant = command.Participants[index];
                requests[index] =
                    await _bridge.PrepareEvolutionRequestAsync(
                        participant.Job,
                        participant.Job.Coordinate,
                        runtimePolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static MultiActorDecisionBatch CreateDecisionBatch(
        WorldAgentEvolutionCommand command,
        IReadOnlyList<DurableRunRequest> requests)
    {
        var participantCoordinate =
            command.Participants[0].Job.Coordinate;
        var sharedCoordinate = new GameContextCoordinate(
            participantCoordinate.WorldId,
            participantCoordinate.TimelineId,
            participantCoordinate.SaveRevision,
            observer: null,
            participantCoordinate.SceneId,
            participantCoordinate.RegionId,
            participantCoordinate.StateVersion,
            participantCoordinate.GameTime,
            participantCoordinate.Causality,
            participantCoordinate.SessionId);
        return new MultiActorDecisionBatch(
            command.BatchId,
            sharedCoordinate,
            requests,
            command.AggregateBudget);
    }

    private async ValueTask<IReadOnlyList<
        WorldAgentDecisionProposalResult>> RunInitialBatchAsync(
            WorldAgentEvolutionCommand command,
            EvolutionState state,
            MultiActorPreparedBatch batch,
            CancellationToken cancellationToken)
    {
        EnsurePreparedBatchMatches(state, batch);
        var outcome = await _multiActor.RunPreparedBatchAsync(
                batch,
                cancellationToken)
            .ConfigureAwait(false);
        var results =
            new WorldAgentDecisionProposalResult[command.Participants.Count];
        foreach (var actor in outcome.Results)
        {
            var participant = command.Participants[actor.InputIndex];
            results[actor.InputIndex] = actor.Outcome is null
                ? UncertainProposal(
                    participant.Job,
                    "world_agent_batch_run_uncertain")
                : BuildProposal(
                    participant,
                    _bridge.Interpret(
                        participant.Job,
                        actor.Outcome));
        }

        return new ReadOnlyCollection<WorldAgentDecisionProposalResult>(
            results);
    }

    private async ValueTask<IReadOnlyList<
        WorldAgentDecisionProposalResult>> ResumeActorsAsync(
            WorldAgentEvolutionCommand command,
            EvolutionState state,
            IReadOnlyDictionary<string, DurableRunContinuation> continuations,
            IGameOperationReconciler? reconciler,
            bool requestCancellation,
            CancellationToken cancellationToken)
    {
        var runtimePolicy = CaptureRuntimePolicy();
        if (!command.RuntimeGeneration.Matches(runtimePolicy))
        {
            throw new EvolutionPreparedBatchMismatchException();
        }

        var requests = await PrepareRequestsAsync(
                command,
                runtimePolicy,
                cancellationToken)
            .ConfigureAwait(false);
        var batch = _multiActor.PrepareBatch(
            CreateDecisionBatch(command, requests),
            cancellationToken);
        EnsurePreparedBatchMatches(state, batch);

        var resume = new Dictionary<string, DurableRunContinuation>(
            command.Participants.Count,
            StringComparer.Ordinal);
        var expectations =
            new Dictionary<string, DurableRunSemanticExpectation>(
                command.Participants.Count,
                StringComparer.Ordinal);
        for (var index = 0; index < command.Participants.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var participant = command.Participants[index];
            var runId = participant.Job.RunId;
            var settled = state.Actors[index].IsSettled;
            resume.Add(
                runId,
                requestCancellation || settled
                    ? new DurableRunContinuation
                    {
                        RequestCancellation = true
                    }
                    : continuations.TryGetValue(runId, out var supplied)
                        ? supplied
                        : new DurableRunContinuation());
            expectations.Add(
                runId,
                WorldAgentRuntimeBridge.EvolutionExpectation(
                    participant.Job,
                    command.RuntimeGeneration));
        }

        var outcome = await _multiActor.ResumeOrStartPreparedBatchAsync(
                batch,
                new ReadOnlyDictionary<
                    string,
                    DurableRunContinuation>(resume),
                new ReadOnlyDictionary<
                    string,
                    DurableRunSemanticExpectation>(expectations),
                reconciler,
                startMissing: !requestCancellation,
                cancellationToken)
            .ConfigureAwait(false);
        var results =
            new WorldAgentDecisionProposalResult[command.Participants.Count];
        foreach (var actor in outcome.Results)
        {
            var index = actor.InputIndex;
            var evidence = state.Actors[index];
            if (evidence.IsSettled)
            {
                results[index] = RehydrateActorResult(
                        command.Participants[index],
                        evidence)
                    .ProposalResult;
                continue;
            }

            var participant = command.Participants[index];
            results[index] = actor.Outcome is not null
                ? BuildProposal(
                    participant,
                    _bridge.Interpret(
                        participant.Job,
                        actor.Outcome))
                : requestCancellation
                  && actor.Error
                  is MultiActorParticipantNotStartedException
                    ? CancelledProposal(participant.Job)
                    : UncertainProposal(
                        participant.Job,
                        "world_agent_resume_uncertain");
        }

        return new ReadOnlyCollection<WorldAgentDecisionProposalResult>(
            results);
    }

    private static void EnsurePreparedBatchMatches(
        EvolutionState state,
        MultiActorPreparedBatch batch)
    {
        if (!string.Equals(
                state.PreparedBatchDigest,
                batch.Digest,
                StringComparison.Ordinal)
            || batch.RequestDigests.Count != state.Actors.Count)
        {
            throw new EvolutionPreparedBatchMismatchException();
        }

        for (var index = 0;
             index < batch.RequestDigests.Count;
             index++)
        {
            if (!string.Equals(
                    batch.RequestDigests[index],
                    state.Actors[index].RequestDigest,
                    StringComparison.Ordinal))
            {
                throw new EvolutionPreparedBatchMismatchException();
            }
        }
    }

    private static bool CheckpointsMatch(
        WorldAgentEvolutionCheckpoint expected,
        WorldAgentEvolutionCheckpoint actual)
    {
        return expected.Revision == actual.Revision
               && string.Equals(
                   expected.CommandId,
                   actual.CommandId,
                   StringComparison.Ordinal)
               && string.Equals(
                   expected.CommandDigest,
                   actual.CommandDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   expected.PayloadDigest,
                   actual.PayloadDigest,
                   StringComparison.Ordinal);
    }

    private static WorldAgentDecisionProposalResult BuildProposal(
        WorldAgentEvolutionParticipant participant,
        WorldAgentJobResult result)
    {
        return WorldAgentAuthoritativeDecisionCoordinator
            .BuildProposalResult(
                participant.Draft,
                participant.Job,
                result);
    }

    private static WorldAgentDecisionProposalResult UncertainProposal(
        WorldAgentJob job,
        string reasonCode)
    {
        return new WorldAgentDecisionProposalResult(
            WorldAgentDecisionProposalStatus.ReconciliationRequired,
            reasonCode,
            new WorldAgentJobResult(
                job.JobId,
                job.RunId,
                WorldAgentJobStatus.ReconciliationRequired,
                "unknown",
                reasonCode,
                output: null,
                usedFallback: false,
                authoritative: false));
    }

    private static WorldAgentDecisionProposalResult CancelledProposal(
        WorldAgentJob job)
    {
        const string reason = "world_agent_cancelled_before_dispatch";
        return new WorldAgentDecisionProposalResult(
            WorldAgentDecisionProposalStatus.Cancelled,
            reason,
            new WorldAgentJobResult(
                job.JobId,
                job.RunId,
                WorldAgentJobStatus.Cancelled,
                RunStates.Cancelled,
                reason,
                output: null,
                usedFallback: false,
                authoritative: false));
    }

    private static ActorControlResult? ClassifyActors(
        EvolutionState state,
        bool requestCancellation)
    {
        if (state.Actors.Any(
                actor => actor.ProposalStatus
                         == WorldAgentDecisionProposalStatus
                             .ReconciliationRequired))
        {
            return new ActorControlResult(
                WorldAgentEvolutionStatus.ReconciliationRequired,
                WorldAgentEvolutionStage.ReconciliationRequired,
                ReconciliationReason);
        }

        if (state.Actors.Any(
                actor => actor.ProposalStatus
                         is WorldAgentDecisionProposalStatus.Waiting
                             or WorldAgentDecisionProposalStatus
                                 .WaitingForInput
                         || actor.ProposalStatus is null))
        {
            return new ActorControlResult(
                WorldAgentEvolutionStatus.Waiting,
                WorldAgentEvolutionStage.Waiting,
                WaitingReason);
        }

        if (requestCancellation
            || state.Actors.Any(
                actor => actor.ProposalStatus
                         == WorldAgentDecisionProposalStatus.Cancelled))
        {
            return new ActorControlResult(
                WorldAgentEvolutionStatus.Cancelled,
                WorldAgentEvolutionStage.Cancelled,
                "world_evolution_cancelled");
        }

        if (state.Actors.Any(
                actor => actor.ProposalStatus
                         == WorldAgentDecisionProposalStatus.Rejected))
        {
            return new ActorControlResult(
                WorldAgentEvolutionStatus.Rejected,
                WorldAgentEvolutionStage.Rejected,
                "world_evolution_actor_rejected");
        }

        if (state.Actors.Any(
                actor => actor.ProposalStatus
                         == WorldAgentDecisionProposalStatus.Failed))
        {
            return new ActorControlResult(
                WorldAgentEvolutionStatus.Failed,
                WorldAgentEvolutionStage.Failed,
                "world_evolution_actor_failed");
        }

        return null;
    }

    private async ValueTask<WorldAuthoritativeStateSnapshot?>
        ReadExactSnapshotAsync(
            WorldAgentEvolutionCommand command,
            CancellationToken cancellationToken)
    {
        var snapshot = await _worldStore.ReadAsync(
                command.ExpectedCoordinate.Address,
                cancellationToken)
            .ConfigureAwait(false);
        return snapshot is not null
               && command.ExpectedCoordinate.IsExactMatch(
                   snapshot.Coordinate)
            ? snapshot
            : null;
    }

    private static bool ValidateSettlement(
        WorldAgentEvolutionCommand command,
        WorldEventTransactionExecutionRequest request)
    {
        return string.Equals(
                   request.CommandId,
                   command.CommandId,
                   StringComparison.Ordinal)
               && string.Equals(
                   request.OperationId,
                   command.OperationId,
                   StringComparison.Ordinal)
               && command.ExpectedCoordinate.IsExactMatch(
                   request.ExpectedCoordinate)
               && string.Equals(
                   request.Instance.WorldId,
                   command.ExpectedCoordinate.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   request.Instance.TimelineId,
                   command.ExpectedCoordinate.TimelineId,
                   StringComparison.Ordinal)
               && request.Instance.TimelineEpoch
               == command.ExpectedCoordinate.TimelineEpoch;
    }

    private IReadOnlyDictionary<string, DurableRunContinuation>
        SnapshotContinuations(
            WorldAgentEvolutionCommand command,
            IReadOnlyDictionary<string, DurableRunContinuation>? source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
        {
            return new ReadOnlyDictionary<string, DurableRunContinuation>(
                new Dictionary<string, DurableRunContinuation>(
                    StringComparer.Ordinal));
        }

        if (source.Count > command.Participants.Count)
        {
            throw new ArgumentException(
                "Continuation count exceeds the participant count.",
                nameof(source));
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in command.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            allowed.Add(participant.Job.RunId);
        }

        var copy = new Dictionary<string, DurableRunContinuation>(
            StringComparer.Ordinal);
        var emptyMeasurement = MeasureContinuationSnapshot(
            new DurableRunContinuation());
        long batchSnapshotBytes =
            (long)emptyMeasurement.Utf8Bytes * command.Participants.Count;
        long batchSnapshotNodes =
            (long)emptyMeasurement.Nodes * command.Participants.Count;
        EnsureContinuationBatchSnapshotBudget(
            batchSnapshotBytes,
            batchSnapshotNodes,
            nameof(source));
        var enumerated = 0;
        foreach (var pair in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerated++;
            if (enumerated > command.Participants.Count)
            {
                throw new ArgumentException(
                    "Continuation enumeration exceeds the participant "
                    + "count.",
                    nameof(source));
            }

            if (!allowed.Contains(pair.Key)
                || pair.Value is null
                || copy.ContainsKey(pair.Key))
            {
                throw new ArgumentException(
                    "Continuations must target distinct known non-null "
                    + "participant runs.",
                    nameof(source));
            }

            var continuationSnapshot = SnapshotContinuation(
                pair.Value,
                nameof(source),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var measurement = MeasureContinuationSnapshot(
                continuationSnapshot);
            var nextBatchSnapshotBytes =
                batchSnapshotBytes
                - emptyMeasurement.Utf8Bytes
                + measurement.Utf8Bytes;
            var nextBatchSnapshotNodes =
                batchSnapshotNodes
                - emptyMeasurement.Nodes
                + measurement.Nodes;
            EnsureContinuationBatchSnapshotBudget(
                nextBatchSnapshotBytes,
                nextBatchSnapshotNodes,
                nameof(source));
            batchSnapshotBytes = nextBatchSnapshotBytes;
            batchSnapshotNodes = nextBatchSnapshotNodes;
            copy.Add(pair.Key, continuationSnapshot);
        }

        return new ReadOnlyDictionary<string, DurableRunContinuation>(
            copy);
    }

    private JsonValueMeasurement MeasureContinuationSnapshot(
        DurableRunContinuation continuation)
    {
        var envelope = JsonArrayBuilder.Object(
            ("runInput", DurableRunInputJournalCodec.Encode(
                continuation.Context,
                continuation.ActiveSkills,
                continuation.WorkloadClass
                ?? ProviderWorkloadClasses.Interactive)),
            ("replaceActiveSkills", JsonArrayBuilder.Boolean(
                continuation.ReplaceActiveSkills)),
            ("laneId", continuation.LaneId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(continuation.LaneId)),
            ("requestCancellation", JsonArrayBuilder.Boolean(
                continuation.RequestCancellation)),
            ("finalOutputContract",
                continuation.FinalOutputContract is null
                    ? JsonArrayBuilder.Null()
                    : continuation.FinalOutputContract.ToJson()));
        return JsonValueInspector.ValidateAndMeasureDetailed(
            envelope,
            new JsonValueLimits(
                maxUtf8Bytes: Math.Min(
                    MaxContinuationSnapshotUtf8BytesPerRun,
                    _options.MaxBatchSnapshotUtf8Bytes),
                maxDepth: 64,
                maxNodes: Math.Min(
                    MaxContinuationSnapshotJsonNodesPerRun,
                    _options.MaxBatchSnapshotJsonNodes),
                maxStringUtf8Bytes: Math.Min(
                    MaxContinuationSnapshotUtf8BytesPerRun,
                    _options.MaxBatchSnapshotUtf8Bytes),
                maxContainerItems: 4_096),
            nameof(continuation));
    }

    private void EnsureContinuationBatchSnapshotBudget(
        long utf8Bytes,
        long nodes,
        string parameterName)
    {
        if (utf8Bytes > _options.MaxBatchSnapshotUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "multi_actor_batch_snapshot_bytes_exceeded",
                "The aggregate continuation snapshot exceeds the "
                + "multi-actor byte budget.");
        }

        if (nodes > _options.MaxBatchSnapshotJsonNodes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "multi_actor_batch_snapshot_nodes_exceeded",
                "The aggregate continuation snapshot exceeds the "
                + "multi-actor node budget.");
        }
    }

    private static DurableRunContinuation SnapshotContinuation(
        DurableRunContinuation source,
        string parameterName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = CopyBounded(
            source.Context,
            maximumItems: 512,
            parameterName,
            item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return item.Clone();
            },
            cancellationToken);
        var skills = CopyBounded(
            source.ActiveSkills,
            maximumItems: 128,
            parameterName,
            item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new SkillReference(
                    item.SkillId,
                    item.Version);
            },
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var workload = source.WorkloadClass is null
            ? null
            : ProviderWorkloadClasses.Normalize(
                source.WorkloadClass,
                parameterName);
        cancellationToken.ThrowIfCancellationRequested();
        return new DurableRunContinuation
        {
            Context = context,
            ActiveSkills = skills,
            ReplaceActiveSkills = source.ReplaceActiveSkills,
            LaneId = string.IsNullOrWhiteSpace(source.LaneId)
                ? source.LaneId
                : RuntimeGuard.RequiredUtf8(
                    source.LaneId,
                    256,
                    parameterName),
            WorkloadClass = workload,
            RequestCancellation = source.RequestCancellation,
            FinalOutputContract =
                source.FinalOutputContract?.Snapshot()
        };
    }

    private static IReadOnlyList<T> CopyBounded<T>(
        IEnumerable<T>? source,
        int maximumItems,
        string parameterName,
        Func<T, T> clone,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
        {
            return Array.Empty<T>();
        }

        var items = new List<T>(Math.Min(maximumItems, 128));
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count >= maximumItems)
            {
                throw new ArgumentException(
                    "Continuation content exceeds its item limit.",
                    parameterName);
            }

            items.Add(
                clone(
                    item
                    ?? throw new ArgumentException(
                        "Continuation collections cannot contain null.",
                        parameterName)));
        }

        return new ReadOnlyCollection<T>(items);
    }

    private static IReadOnlyList<WorldAgentEvolutionActorResult>
        RehydrateActorResults(
            WorldAgentEvolutionCommand command,
            EvolutionState state)
    {
        var results = new WorldAgentEvolutionActorResult[
            command.Participants.Count];
        for (var index = 0; index < results.Length; index++)
        {
            results[index] = RehydrateActorResult(
                command.Participants[index],
                state.Actors[index]);
        }

        return new ReadOnlyCollection<WorldAgentEvolutionActorResult>(
            results);
    }

    private static WorldAgentEvolutionActorResult RehydrateActorResult(
        WorldAgentEvolutionParticipant participant,
        ActorEvidence evidence)
    {
        var status = evidence.ProposalStatus
                     ?? WorldAgentDecisionProposalStatus.Waiting;
        WorldAgentAuthoritativeProposal? proposal = null;
        JsonElement? output = null;
        var usedFallback = false;
        if (evidence.ProposalEnvelope.HasValue)
        {
            proposal = WorldAgentAuthoritativeProposal.FromEnvelope(
                evidence.ProposalEnvelope.Value);
            output = SelectedOutput(proposal.OptionId);
            usedFallback = proposal.UsedFallback;
        }

        var jobStatus = status switch
        {
            WorldAgentDecisionProposalStatus.Proposed =>
                WorldAgentJobStatus.Completed,
            WorldAgentDecisionProposalStatus.Waiting =>
                WorldAgentJobStatus.Waiting,
            WorldAgentDecisionProposalStatus.WaitingForInput =>
                WorldAgentJobStatus.WaitingForInput,
            WorldAgentDecisionProposalStatus.ReconciliationRequired =>
                WorldAgentJobStatus.ReconciliationRequired,
            WorldAgentDecisionProposalStatus.Skipped =>
                WorldAgentJobStatus.Skipped,
            WorldAgentDecisionProposalStatus.Cancelled =>
                WorldAgentJobStatus.Cancelled,
            _ => WorldAgentJobStatus.Failed
        };
        var agent = new WorldAgentJobResult(
            participant.Job.JobId,
            participant.Job.RunId,
            jobStatus,
            evidence.RunState ?? "unknown",
            evidence.ReasonCode ?? WaitingReason,
            output,
            usedFallback,
            proposal is not null);
        var proposalResult = new WorldAgentDecisionProposalResult(
            status,
            evidence.ReasonCode ?? WaitingReason,
            agent,
            proposal);
        return new WorldAgentEvolutionActorResult(
            evidence.InputIndex,
            participant,
            proposalResult);
    }

    private static JsonElement SelectedOutput(string optionId)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("optionId", optionId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static WorldAgentEvolutionResult Result(
        WorldAgentEvolutionCommand command,
        WorldAgentEvolutionStatus status,
        WorldAgentEvolutionStage stage,
        string reasonCode,
        long checkpointRevision,
        IReadOnlyList<WorldAgentEvolutionActorResult>? actorResults = null)
    {
        return new WorldAgentEvolutionResult(
            command.CommandId,
            status,
            stage,
            reasonCode,
            checkpointRevision,
            actorResults);
    }

    private bool ReducerPolicyMatches(
        WorldAgentEvolutionCommand command)
    {
        return string.Equals(
                   _reducer.PolicyId,
                   command.ReducerPolicyId,
                   StringComparison.Ordinal)
               && string.Equals(
                   _reducer.PolicyDigest,
                   command.ReducerPolicyDigest,
                   StringComparison.Ordinal);
    }

    private WorldAgentRuntimeGeneration CaptureRuntimePolicy()
    {
        return _runtimePolicySource.CapturePolicySnapshot()
               ?? throw new InvalidOperationException(
                   "Runtime policy source returned null.");
    }

    private static WorldAgentEvolutionResult TerminalResult(
        WorldAgentEvolutionCommand command,
        EvolutionState state,
        WorldAgentEvolutionCheckpoint checkpoint,
        bool replayCompleted,
        WorldCommandReceipt? receipt)
    {
        var status = state.TerminalStatus
                     ?? throw new InvalidDataException(
                         "A terminal evolution omitted its status.");
        if (replayCompleted
            && status == WorldAgentEvolutionStatus.Completed)
        {
            status = WorldAgentEvolutionStatus.Replayed;
        }

        return new WorldAgentEvolutionResult(
            command.CommandId,
            status,
            state.Stage,
            state.TerminalReasonCode ?? CompletedReason,
            checkpoint.Revision,
            RehydrateActorResults(command, state),
            receipt: receipt);
    }

    private readonly struct ActorControlResult
    {
        public ActorControlResult(
            WorldAgentEvolutionStatus status,
            WorldAgentEvolutionStage stage,
            string reasonCode)
        {
            Status = status;
            Stage = stage;
            ReasonCode = reasonCode;
        }

        public WorldAgentEvolutionStatus Status { get; }

        public WorldAgentEvolutionStage Stage { get; }

        public string ReasonCode { get; }
    }

    private readonly struct EvolutionOwnerToken
    {
        public EvolutionOwnerToken(string id, long generation)
        {
            Id = id;
            Generation = generation;
        }

        public string Id { get; }

        public long Generation { get; }
    }

    private sealed class EvolutionOwnershipCursor
    {
        public EvolutionOwnershipCursor(
            WorldAgentEvolutionCheckpoint checkpoint,
            EvolutionState state,
            EvolutionOwnerToken owner)
        {
            Checkpoint = checkpoint;
            State = state;
            Owner = owner;
        }

        public WorldAgentEvolutionCheckpoint Checkpoint { get; set; }

        public EvolutionState State { get; }

        public EvolutionOwnerToken Owner { get; }
    }

    private sealed class EvolutionHeartbeatState
    {
        public EvolutionHeartbeatState(
            WorldAgentEvolutionCheckpoint checkpoint)
        {
            Checkpoint = checkpoint;
        }

        public WorldAgentEvolutionCheckpoint Checkpoint { get; set; }

        public Exception? Failure { get; set; }
    }

    private readonly struct OwnedOperationResult<T>
    {
        public OwnedOperationResult(
            WorldAgentEvolutionCheckpoint checkpoint,
            T value)
        {
            Checkpoint = checkpoint;
            Value = value;
        }

        public WorldAgentEvolutionCheckpoint Checkpoint { get; }

        public T Value { get; }
    }

    private sealed class EvolutionOwnershipLostException : Exception
    {
    }

    private sealed class EvolutionPreparedBatchMismatchException
        : Exception
    {
    }

    private sealed class EvolutionState
    {
        private const string Contract =
            "game-agent.world-agent-evolution-state.v2";

        public string CapturedStateDigest { get; private set; } =
            string.Empty;

        public string ManifestDigest { get; private set; } = string.Empty;

        public string PreparedBatchDigest { get; private set; } =
            string.Empty;

        public string? OwnerId { get; set; }

        public long OwnerGeneration { get; set; }

        public DateTimeOffset? OwnerLeaseExpiresAt { get; set; }

        public WorldAgentEvolutionStage Stage { get; set; }

        public List<ActorEvidence> Actors { get; } = new();

        public bool HasSettledActors { get; set; }

        public WorldAgentEvolutionReductionDisposition?
            ReductionDisposition
        {
            get;
            private set;
        }

        public string? ReductionReasonCode { get; private set; }

        public string? ReductionEvidenceDigest { get; private set; }

        public string? RequestFingerprint { get; private set; }

        public bool WorldOperationMayExist { get; private set; }

        public string? PauseReasonCode { get; set; }

        public WorldAgentEvolutionStatus? TerminalStatus { get; set; }

        public string? TerminalReasonCode { get; set; }

        public string? ReceiptId { get; set; }

        public bool IsTerminal =>
            TerminalStatus.HasValue
            && Stage is WorldAgentEvolutionStage.Completed
                or WorldAgentEvolutionStage.Rejected
                or WorldAgentEvolutionStage.Failed
                or WorldAgentEvolutionStage.Cancelled;

        public static EvolutionState Create(
            WorldAgentEvolutionCommand command,
            string capturedStateDigest,
            IReadOnlyList<string> requestDigests,
            string preparedBatchDigest,
            string ownerId,
            DateTimeOffset leaseExpiresAt)
        {
            if (requestDigests.Count != command.Participants.Count)
            {
                throw new ArgumentException(
                    "Prepared request digests do not match the participant "
                    + "manifest.",
                    nameof(requestDigests));
            }

            var state = new EvolutionState
            {
                CapturedStateDigest = EvolutionGuard.Digest(
                    capturedStateDigest,
                    nameof(capturedStateDigest)),
                PreparedBatchDigest = EvolutionGuard.Digest(
                    preparedBatchDigest,
                    nameof(preparedBatchDigest)),
                OwnerId = ownerId,
                OwnerGeneration = 1,
                OwnerLeaseExpiresAt = leaseExpiresAt,
                Stage =
                    WorldAgentEvolutionStage.ActorManifestCommitted
            };
            for (var index = 0; index < requestDigests.Count; index++)
            {
                var participant = command.Participants[index];
                state.Actors.Add(
                    new ActorEvidence
                    {
                        InputIndex = index,
                        JobId = participant.Job.JobId,
                        RunId = participant.Job.RunId,
                        AgentId = participant.Job.AgentId,
                        JobSemanticDigest =
                            participant.Job.SemanticDigest,
                        DraftId = participant.Draft.DraftId,
                        DraftDigest = participant.Draft.Digest,
                        RequestDigest = EvolutionGuard.Digest(
                            requestDigests[index],
                            nameof(requestDigests))
                    });
            }

            state.ManifestDigest = state.ComputeManifestDigest();
            return state;
        }

        public static EvolutionState FromCheckpoint(
            WorldAgentEvolutionCommand command,
            WorldAgentEvolutionCheckpoint checkpoint)
        {
            if (!string.Equals(
                    checkpoint.CommandDigest,
                    command.SemanticDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution command digest does not match its checkpoint.");
            }

            var payload = checkpoint.Payload;
            RequireObjectShape(
                payload,
                new[]
                {
                    "contract",
                    "capturedStateDigest",
                    "manifestDigest",
                    "preparedBatchDigest",
                    "ownerId",
                    "ownerGeneration",
                    "ownerLeaseExpiresAt",
                    "stage",
                    "hasSettledActors",
                    "worldOperationMayExist",
                    "actors",
                    "reduction",
                    "pauseReasonCode",
                    "terminal"
                });
            if (!string.Equals(
                    payload.GetProperty("contract").GetString(),
                    Contract,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution state contract is unsupported.");
            }

            var state = new EvolutionState
            {
                CapturedStateDigest = EvolutionGuard.Digest(
                    payload.GetProperty("capturedStateDigest")
                        .GetString()!,
                    "capturedStateDigest"),
                ManifestDigest = EvolutionGuard.Digest(
                    payload.GetProperty("manifestDigest").GetString()!,
                    "manifestDigest"),
                PreparedBatchDigest = EvolutionGuard.Digest(
                    payload.GetProperty("preparedBatchDigest")
                        .GetString()!,
                    "preparedBatchDigest"),
                OwnerId = OptionalString(
                    payload.GetProperty("ownerId")),
                OwnerGeneration =
                    payload.GetProperty("ownerGeneration").GetInt64(),
                OwnerLeaseExpiresAt = OptionalDateTimeOffset(
                    payload.GetProperty("ownerLeaseExpiresAt")),
                Stage = (WorldAgentEvolutionStage)payload
                    .GetProperty("stage")
                    .GetInt32(),
                HasSettledActors = payload
                    .GetProperty("hasSettledActors")
                    .GetBoolean(),
                WorldOperationMayExist = payload
                    .GetProperty("worldOperationMayExist")
                    .GetBoolean(),
                PauseReasonCode = OptionalString(
                    payload.GetProperty("pauseReasonCode"))
            };
            if (state.OwnerGeneration < 1
                || !Enum.IsDefined(
                    typeof(WorldAgentEvolutionStage),
                    state.Stage)
                || (state.OwnerId is null)
                != (state.OwnerLeaseExpiresAt is null))
            {
                throw new InvalidDataException(
                    "Evolution ownership or stage evidence is invalid.");
            }

            var actors = payload.GetProperty("actors");
            if (actors.ValueKind != JsonValueKind.Array
                || actors.GetArrayLength()
                != command.Participants.Count)
            {
                throw new InvalidDataException(
                    "Evolution actor evidence does not match the manifest.");
            }

            var index = 0;
            foreach (var actor in actors.EnumerateArray())
            {
                var evidence = ActorEvidence.FromJson(actor);
                evidence.Validate(command.Participants[index], index);
                state.Actors.Add(evidence);
                index++;
            }

            if (!string.Equals(
                    state.ManifestDigest,
                    state.ComputeManifestDigest(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution manifest digest does not match its evidence.");
            }

            state.ReadReduction(payload.GetProperty("reduction"));
            state.ReadTerminal(payload.GetProperty("terminal"));
            state.ValidateStateShape();
            if (state.IsTerminal && state.OwnerId is not null)
            {
                throw new InvalidDataException(
                    "A terminal evolution cannot retain an owner.");
            }

            return state;
        }

        public WorldAgentEvolutionCheckpoint ToCheckpoint(
            WorldAgentEvolutionCommand command,
            long revision)
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString("contract", Contract);
                writer.WriteString(
                    "capturedStateDigest",
                    CapturedStateDigest);
                writer.WriteString("manifestDigest", ManifestDigest);
                writer.WriteString(
                    "preparedBatchDigest",
                    PreparedBatchDigest);
                WriteOptionalString(writer, "ownerId", OwnerId);
                writer.WriteNumber("ownerGeneration", OwnerGeneration);
                WriteOptionalString(
                    writer,
                    "ownerLeaseExpiresAt",
                    OwnerLeaseExpiresAt?.ToString(
                        "O",
                        CultureInfo.InvariantCulture));
                writer.WriteNumber("stage", (int)Stage);
                writer.WriteBoolean(
                    "hasSettledActors",
                    HasSettledActors);
                writer.WriteBoolean(
                    "worldOperationMayExist",
                    WorldOperationMayExist);
                writer.WritePropertyName("actors");
                writer.WriteStartArray();
                foreach (var actor in Actors)
                {
                    actor.WriteTo(writer);
                }

                writer.WriteEndArray();
                WriteReduction(writer);
                WriteOptionalString(
                    writer,
                    "pauseReasonCode",
                    PauseReasonCode);
                WriteTerminal(writer);
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(output.ToArray());
            return new WorldAgentEvolutionCheckpoint(
                command.CommandId,
                revision,
                command.SemanticDigest,
                document.RootElement);
        }

        public void SetActorResults(
            IReadOnlyList<WorldAgentDecisionProposalResult> results)
        {
            if (results.Count != Actors.Count)
            {
                throw new ArgumentException(
                    "Actor result count does not match the manifest.",
                    nameof(results));
            }

            for (var index = 0; index < results.Count; index++)
            {
                Actors[index].SetResult(results[index]);
            }
        }

        public void SetReduction(
            WorldAgentEvolutionReduction reduction,
            string? requestFingerprint)
        {
            ReductionDisposition = reduction.Disposition;
            ReductionReasonCode = reduction.ReasonCode;
            ReductionEvidenceDigest = reduction.EvidenceDigest;
            RequestFingerprint = requestFingerprint;
        }

        public void MarkWorldOperationMayExist()
        {
            if (ReductionDisposition
                    != WorldAgentEvolutionReductionDisposition.Commit
                || RequestFingerprint is null)
            {
                throw new InvalidOperationException(
                    "Only a validated commit reduction may enter world "
                    + "operation reconciliation.");
            }

            WorldOperationMayExist = true;
        }

        public void ClearWorldOperationMayExist()
        {
            WorldOperationMayExist = false;
        }

        public void EnsureOwner(EvolutionOwnerToken owner)
        {
            if (!string.Equals(
                    OwnerId,
                    owner.Id,
                    StringComparison.Ordinal)
                || OwnerGeneration != owner.Generation)
            {
                throw new EvolutionOwnershipLostException();
            }
        }

        public void EnsureActiveOwner(
            EvolutionOwnerToken owner,
            DateTimeOffset now)
        {
            EnsureOwner(owner);
            if (!OwnerLeaseExpiresAt.HasValue
                || OwnerLeaseExpiresAt.Value <= now)
            {
                throw new EvolutionOwnershipLostException();
            }
        }

        public void ClearOwner()
        {
            OwnerId = null;
            OwnerLeaseExpiresAt = null;
        }

        private string ComputeManifestDigest()
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "contract",
                    "game-agent.world-agent-manifest.v2");
                writer.WriteString(
                    "preparedBatchDigest",
                    PreparedBatchDigest);
                writer.WritePropertyName("actors");
                writer.WriteStartArray();
                foreach (var actor in Actors)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("inputIndex", actor.InputIndex);
                    writer.WriteString("jobId", actor.JobId);
                    writer.WriteString("runId", actor.RunId);
                    writer.WriteString("agentId", actor.AgentId);
                    writer.WriteString(
                        "jobSemanticDigest",
                        actor.JobSemanticDigest);
                    writer.WriteString("draftId", actor.DraftId);
                    writer.WriteString(
                        "draftDigest",
                        actor.DraftDigest);
                    writer.WriteString(
                        "requestDigest",
                        actor.RequestDigest);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(output.ToArray());
            return CanonicalJsonDigest.ComputeSha256(
                document.RootElement);
        }

        private void WriteReduction(Utf8JsonWriter writer)
        {
            writer.WritePropertyName("reduction");
            if (!ReductionDisposition.HasValue)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteNumber(
                "disposition",
                (int)ReductionDisposition.Value);
            writer.WriteString(
                "reasonCode",
                ReductionReasonCode);
            writer.WriteString(
                "evidenceDigest",
                ReductionEvidenceDigest);
            WriteOptionalString(
                writer,
                "requestFingerprint",
                RequestFingerprint);
            writer.WriteEndObject();
        }

        private void ReadReduction(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireObjectShape(
                value,
                new[]
                {
                    "disposition",
                    "reasonCode",
                    "evidenceDigest",
                    "requestFingerprint"
                });
            ReductionDisposition =
                (WorldAgentEvolutionReductionDisposition)value
                    .GetProperty("disposition")
                    .GetInt32();
            if (!Enum.IsDefined(
                    typeof(WorldAgentEvolutionReductionDisposition),
                    ReductionDisposition.Value))
            {
                throw new InvalidDataException(
                    "Evolution reduction disposition is invalid.");
            }

            ReductionReasonCode = EvolutionGuard.Required(
                value.GetProperty("reasonCode").GetString()!,
                "reasonCode",
                96);
            ReductionEvidenceDigest = EvolutionGuard.Digest(
                value.GetProperty("evidenceDigest").GetString()!,
                "evidenceDigest");
            RequestFingerprint = OptionalString(
                value.GetProperty("requestFingerprint"));
            if (RequestFingerprint is not null)
            {
                EvolutionGuard.Digest(
                    RequestFingerprint,
                    "requestFingerprint");
            }
        }

        private void WriteTerminal(Utf8JsonWriter writer)
        {
            writer.WritePropertyName("terminal");
            if (!TerminalStatus.HasValue)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteNumber("status", (int)TerminalStatus.Value);
            writer.WriteString(
                "reasonCode",
                TerminalReasonCode);
            WriteOptionalString(writer, "receiptId", ReceiptId);
            writer.WriteEndObject();
        }

        private void ReadTerminal(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireObjectShape(
                value,
                new[] { "status", "reasonCode", "receiptId" });
            TerminalStatus = (WorldAgentEvolutionStatus)value
                .GetProperty("status")
                .GetInt32();
            if (!Enum.IsDefined(
                    typeof(WorldAgentEvolutionStatus),
                    TerminalStatus.Value)
                || TerminalStatus.Value
                is WorldAgentEvolutionStatus.Waiting
                    or WorldAgentEvolutionStatus
                        .ReconciliationRequired
                    or WorldAgentEvolutionStatus.Busy
                    or WorldAgentEvolutionStatus.Replayed)
            {
                throw new InvalidDataException(
                    "Evolution terminal status is invalid.");
            }

            TerminalReasonCode = EvolutionGuard.Required(
                value.GetProperty("reasonCode").GetString()!,
                "reasonCode",
                96);
            ReceiptId = OptionalString(
                value.GetProperty("receiptId"));
        }

        private void ValidateStateShape()
        {
            var actorsReady = Actors.All(
                actor => actor.ProposalStatus
                         is WorldAgentDecisionProposalStatus.Proposed
                             or WorldAgentDecisionProposalStatus.Skipped);
            if (HasSettledActors != actorsReady
                && HasSettledActors)
            {
                throw new InvalidDataException(
                    "Settled evolution evidence contains unresolved actors.");
            }

            if (Stage is WorldAgentEvolutionStage.Reducing
                    or WorldAgentEvolutionStage.Revalidating
                    or WorldAgentEvolutionStage.WorldCommitPending
                && !HasSettledActors)
            {
                throw new InvalidDataException(
                    "The evolution stage requires settled actor evidence.");
            }

            if (ReductionDisposition
                == WorldAgentEvolutionReductionDisposition.Commit)
            {
                if (RequestFingerprint is null)
                {
                    throw new InvalidDataException(
                        "A commit reduction requires a request fingerprint.");
                }
            }
            else if (RequestFingerprint is not null)
            {
                throw new InvalidDataException(
                    "Only a commit reduction can retain a request fingerprint.");
            }

            if (Stage is WorldAgentEvolutionStage.WorldCommitPending
                    or WorldAgentEvolutionStage.ReconciliationRequired
                && RequestFingerprint is not null
                && ReductionDisposition
                != WorldAgentEvolutionReductionDisposition.Commit)
            {
                throw new InvalidDataException(
                    "World-commit recovery evidence is incomplete.");
            }

            if (WorldOperationMayExist
                && (ReductionDisposition
                        != WorldAgentEvolutionReductionDisposition.Commit
                    || RequestFingerprint is null))
            {
                throw new InvalidDataException(
                    "World-operation reconciliation evidence is incomplete.");
            }

            if (ReceiptId is not null && !WorldOperationMayExist)
            {
                throw new InvalidDataException(
                    "A terminal world receipt requires submitted-operation "
                    + "evidence.");
            }

            if (TerminalStatus.HasValue)
            {
                var expectedStage = TerminalStatus.Value switch
                {
                    WorldAgentEvolutionStatus.Completed =>
                        WorldAgentEvolutionStage.Completed,
                    WorldAgentEvolutionStatus.Rejected =>
                        WorldAgentEvolutionStage.Rejected,
                    WorldAgentEvolutionStatus.Failed =>
                        WorldAgentEvolutionStage.Failed,
                    WorldAgentEvolutionStatus.Cancelled =>
                        WorldAgentEvolutionStage.Cancelled,
                    _ => throw new InvalidDataException(
                        "Evolution terminal status is unsupported.")
                };
                if (Stage != expectedStage)
                {
                    throw new InvalidDataException(
                        "Evolution terminal status and stage disagree.");
                }
            }
            else if (Stage is WorldAgentEvolutionStage.Completed
                         or WorldAgentEvolutionStage.Rejected
                         or WorldAgentEvolutionStage.Failed
                         or WorldAgentEvolutionStage.Cancelled)
            {
                throw new InvalidDataException(
                    "A terminal evolution stage requires terminal evidence.");
            }
        }

        internal static void RequireObjectShape(
            JsonElement value,
            IReadOnlyCollection<string> expectedNames)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Evolution evidence must be a JSON object.");
            }

            var expected = new HashSet<string>(
                expectedNames,
                StringComparer.Ordinal);
            var names = value.EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            if (names.Length != expected.Count
                || names.Distinct(StringComparer.Ordinal).Count()
                != names.Length
                || names.Any(name => !expected.Contains(name)))
            {
                throw new InvalidDataException(
                    "Evolution evidence has an unsupported shape.");
            }
        }

        internal static string? OptionalString(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => throw new InvalidDataException(
                    "Expected a string or null.")
            };
        }

        private static DateTimeOffset? OptionalDateTimeOffset(
            JsonElement value)
        {
            var text = OptionalString(value);
            if (text is null)
            {
                return null;
            }

            if (!DateTimeOffset.TryParseExact(
                    text,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                throw new InvalidDataException(
                    "Evolution lease timestamp is invalid.");
            }

            return parsed;
        }

        private static void WriteOptionalString(
            Utf8JsonWriter writer,
            string name,
            string? value)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value);
            }
        }
    }

    private sealed class ActorEvidence
    {
        public int InputIndex { get; set; }

        public string JobId { get; set; } = string.Empty;

        public string RunId { get; set; } = string.Empty;

        public string AgentId { get; set; } = string.Empty;

        public string JobSemanticDigest { get; set; } = string.Empty;

        public string DraftId { get; set; } = string.Empty;

        public string DraftDigest { get; set; } = string.Empty;

        public string RequestDigest { get; set; } = string.Empty;

        public WorldAgentDecisionProposalStatus? ProposalStatus
        {
            get;
            private set;
        }

        public string? ReasonCode { get; private set; }

        public string? RunState { get; private set; }

        public JsonElement? ProposalEnvelope { get; private set; }

        public bool IsSettled =>
            ProposalStatus is WorldAgentDecisionProposalStatus.Proposed
                or WorldAgentDecisionProposalStatus.Rejected
                or WorldAgentDecisionProposalStatus.Skipped
                or WorldAgentDecisionProposalStatus.Failed
                or WorldAgentDecisionProposalStatus.Cancelled;

        public void SetResult(
            WorldAgentDecisionProposalResult result)
        {
            ProposalStatus = result.Status;
            ReasonCode = result.ReasonCode;
            RunState = result.AgentResult?.RunState;
            ProposalEnvelope = result.Proposal?.ToEnvelope();
        }

        public void Validate(
            WorldAgentEvolutionParticipant participant,
            int inputIndex)
        {
            if (InputIndex != inputIndex
                || !string.Equals(
                    JobId,
                    participant.Job.JobId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RunId,
                    participant.Job.RunId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    AgentId,
                    participant.Job.AgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    JobSemanticDigest,
                    participant.Job.SemanticDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    DraftId,
                    participant.Draft.DraftId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    DraftDigest,
                    participant.Draft.Digest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evolution actor evidence does not match the command.");
            }

            if (ProposalEnvelope.HasValue)
            {
                var proposal =
                    WorldAgentAuthoritativeProposal.FromEnvelope(
                        ProposalEnvelope.Value);
                var proposalError =
                    WorldAgentAuthoritativeDecisionCoordinator
                        .ValidateProposal(
                            participant.Draft,
                            participant.Job,
                            proposal);
                if (proposalError is not null)
                {
                    throw new InvalidDataException(
                        "Evolution proposal evidence is not bound to its participant.");
                }
            }
        }

        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteNumber("inputIndex", InputIndex);
            writer.WriteString("jobId", JobId);
            writer.WriteString("runId", RunId);
            writer.WriteString("agentId", AgentId);
            writer.WriteString(
                "jobSemanticDigest",
                JobSemanticDigest);
            writer.WriteString("draftId", DraftId);
            writer.WriteString("draftDigest", DraftDigest);
            writer.WriteString("requestDigest", RequestDigest);
            if (ProposalStatus.HasValue)
            {
                writer.WriteNumber(
                    "proposalStatus",
                    (int)ProposalStatus.Value);
            }
            else
            {
                writer.WriteNull("proposalStatus");
            }

            WriteOptionalString(writer, "reasonCode", ReasonCode);
            WriteOptionalString(writer, "runState", RunState);
            writer.WritePropertyName("proposal");
            if (ProposalEnvelope.HasValue)
            {
                ProposalEnvelope.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        public static ActorEvidence FromJson(JsonElement value)
        {
            EvolutionState.RequireObjectShape(
                value,
                new[]
                {
                    "inputIndex",
                    "jobId",
                    "runId",
                    "agentId",
                    "jobSemanticDigest",
                    "draftId",
                    "draftDigest",
                    "requestDigest",
                    "proposalStatus",
                    "reasonCode",
                    "runState",
                    "proposal"
                });
            var evidence = new ActorEvidence
            {
                InputIndex = value.GetProperty("inputIndex").GetInt32(),
                JobId = EvolutionGuard.Required(
                    value.GetProperty("jobId").GetString()!,
                    "jobId",
                    128),
                RunId = EvolutionGuard.Required(
                    value.GetProperty("runId").GetString()!,
                    "runId",
                    128),
                AgentId = EvolutionGuard.Required(
                    value.GetProperty("agentId").GetString()!,
                    "agentId",
                    128),
                JobSemanticDigest = EvolutionGuard.Digest(
                    value.GetProperty("jobSemanticDigest")
                        .GetString()!,
                    "jobSemanticDigest"),
                DraftId = EvolutionGuard.Required(
                    value.GetProperty("draftId").GetString()!,
                    "draftId",
                    192),
                DraftDigest = EvolutionGuard.Digest(
                    value.GetProperty("draftDigest").GetString()!,
                    "draftDigest"),
                RequestDigest = EvolutionGuard.Digest(
                    value.GetProperty("requestDigest").GetString()!,
                    "requestDigest"),
                ReasonCode = EvolutionState.OptionalString(
                    value.GetProperty("reasonCode")),
                RunState = EvolutionState.OptionalString(
                    value.GetProperty("runState"))
            };
            var status = value.GetProperty("proposalStatus");
            if (status.ValueKind != JsonValueKind.Null)
            {
                evidence.ProposalStatus =
                    (WorldAgentDecisionProposalStatus)status.GetInt32();
                if (!Enum.IsDefined(
                        typeof(WorldAgentDecisionProposalStatus),
                        evidence.ProposalStatus.Value))
                {
                    throw new InvalidDataException(
                        "Evolution actor status is invalid.");
                }
            }

            var proposal = value.GetProperty("proposal");
            if (proposal.ValueKind != JsonValueKind.Null)
            {
                var parsed =
                    WorldAgentAuthoritativeProposal.FromEnvelope(
                        proposal);
                evidence.ProposalEnvelope = parsed.ToEnvelope();
            }

            if ((evidence.ProposalStatus
                 == WorldAgentDecisionProposalStatus.Proposed)
                != evidence.ProposalEnvelope.HasValue)
            {
                throw new InvalidDataException(
                    "Evolution proposal evidence is inconsistent.");
            }

            return evidence;
        }

        private static void WriteOptionalString(
            Utf8JsonWriter writer,
            string name,
            string? value)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value);
            }
        }
    }
}
