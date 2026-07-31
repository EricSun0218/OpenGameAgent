using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class MultiActorCoordinatorOptions
{
    public MultiActorCoordinatorOptions(
        int maxBatchSize = 1_024,
        int maxConcurrentRuns = 32,
        int maxContextCandidatesPerRun = 512,
        int maxActiveSkillsPerRun = 128,
        int maxTranscriptMessagesPerRun = 2_048,
        int maxSnapshotUtf8BytesPerRun = 4 * 1_048_576,
        int maxBatchSnapshotUtf8Bytes = 64 * 1_048_576,
        int maxSnapshotJsonNodesPerRun = 65_536,
        int maxBatchSnapshotJsonNodes = 1_048_576,
        TimeSpan? lifecycleSettlementTimeout = null,
        int maxDetachedLifecycleNotifications = 32,
        TimeSpan? batchAbortSettlementTimeout = null,
        int maxDetachedAbortNotifications = 4,
        int maxConcurrentParticipantResumes = 32,
        int maxConcurrentBatches = 64,
        int? maxQueuedParticipants = null)
    {
        if (maxBatchSize is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        }

        if (maxConcurrentRuns is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRuns));
        }

        if (maxContextCandidatesPerRun is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxContextCandidatesPerRun));
        }

        if (maxActiveSkillsPerRun is < 1
            or > DurableRunInputJournalCodec.MaxActiveSkills)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActiveSkillsPerRun));
        }

        if (maxTranscriptMessagesPerRun is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTranscriptMessagesPerRun));
        }

        if (maxSnapshotUtf8BytesPerRun is < 4_096
            or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSnapshotUtf8BytesPerRun));
        }

        if (maxBatchSnapshotUtf8Bytes < maxSnapshotUtf8BytesPerRun
            || maxBatchSnapshotUtf8Bytes > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchSnapshotUtf8Bytes));
        }

        if (maxSnapshotJsonNodesPerRun is < 64 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSnapshotJsonNodesPerRun));
        }

        if (maxBatchSnapshotJsonNodes < maxSnapshotJsonNodesPerRun
            || maxBatchSnapshotJsonNodes > 16 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchSnapshotJsonNodes));
        }

        var resolvedLifecycleTimeout = lifecycleSettlementTimeout
                                       ?? TimeSpan.FromSeconds(5);
        if (resolvedLifecycleTimeout < TimeSpan.FromMilliseconds(1)
            || resolvedLifecycleTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifecycleSettlementTimeout));
        }

        if (maxDetachedLifecycleNotifications is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDetachedLifecycleNotifications));
        }

        var resolvedAbortTimeout = batchAbortSettlementTimeout
                                   ?? TimeSpan.FromSeconds(5);
        if (resolvedAbortTimeout < TimeSpan.FromMilliseconds(1)
            || resolvedAbortTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchAbortSettlementTimeout));
        }

        if (maxDetachedAbortNotifications is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDetachedAbortNotifications));
        }

        if (maxConcurrentParticipantResumes is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentParticipantResumes));
        }

        if (maxConcurrentBatches is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentBatches));
        }

        var resolvedQueuedParticipants =
            maxQueuedParticipants ?? Math.Max(4_096, maxBatchSize);
        if (resolvedQueuedParticipants < maxBatchSize
            || resolvedQueuedParticipants > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxQueuedParticipants));
        }

        MaxBatchSize = maxBatchSize;
        MaxConcurrentRuns = maxConcurrentRuns;
        MaxContextCandidatesPerRun = maxContextCandidatesPerRun;
        MaxActiveSkillsPerRun = maxActiveSkillsPerRun;
        MaxTranscriptMessagesPerRun = maxTranscriptMessagesPerRun;
        MaxSnapshotUtf8BytesPerRun = maxSnapshotUtf8BytesPerRun;
        MaxBatchSnapshotUtf8Bytes = maxBatchSnapshotUtf8Bytes;
        MaxSnapshotJsonNodesPerRun = maxSnapshotJsonNodesPerRun;
        MaxBatchSnapshotJsonNodes = maxBatchSnapshotJsonNodes;
        LifecycleSettlementTimeout = resolvedLifecycleTimeout;
        MaxDetachedLifecycleNotifications =
            maxDetachedLifecycleNotifications;
        BatchAbortSettlementTimeout = resolvedAbortTimeout;
        MaxDetachedAbortNotifications = maxDetachedAbortNotifications;
        MaxConcurrentParticipantResumes =
            maxConcurrentParticipantResumes;
        MaxConcurrentBatches = maxConcurrentBatches;
        MaxQueuedParticipants = resolvedQueuedParticipants;
    }

    public int MaxBatchSize { get; }

    public int MaxConcurrentRuns { get; }

    public int MaxContextCandidatesPerRun { get; }

    public int MaxActiveSkillsPerRun { get; }

    public int MaxTranscriptMessagesPerRun { get; }

    public int MaxSnapshotUtf8BytesPerRun { get; }

    public int MaxBatchSnapshotUtf8Bytes { get; }

    public int MaxSnapshotJsonNodesPerRun { get; }

    public int MaxBatchSnapshotJsonNodes { get; }

    public TimeSpan LifecycleSettlementTimeout { get; }

    public int MaxDetachedLifecycleNotifications { get; }

    public TimeSpan BatchAbortSettlementTimeout { get; }

    public int MaxDetachedAbortNotifications { get; }

    public int MaxConcurrentParticipantResumes { get; }

    public int MaxConcurrentBatches { get; }

    public int MaxQueuedParticipants { get; }
}

/// <summary>
/// A set of actors deciding against one immutable game-context coordinate.
/// The game chooses the decision key and remains responsible for validation,
/// conflict resolution, and world mutation.
/// </summary>
public sealed class MultiActorDecisionBatch
{
    public MultiActorDecisionBatch(
        string batchId,
        GameContextCoordinate coordinate,
        IEnumerable<DurableRunRequest> runs,
        MultiActorBatchBudget? aggregateBudget = null)
    {
        BatchId = RuntimeGuard.RequiredId(batchId, nameof(batchId));
        Coordinate = coordinate
                     ?? throw new ArgumentNullException(nameof(coordinate));
        if (runs is null)
        {
            throw new ArgumentNullException(nameof(runs));
        }

        var materialized = new List<DurableRunRequest>();
        foreach (var run in runs)
        {
            if (materialized.Count >= 16_384)
            {
                throw new RuntimeContentLimitException(
                    nameof(runs),
                    "multi_actor_batch_hard_limit_exceeded",
                    "A multi-actor batch cannot exceed 16384 runs.");
            }

            materialized.Add(run);
        }

        Runs = new ReadOnlyCollection<DurableRunRequest>(materialized);
        AggregateBudget = aggregateBudget;
    }

    public string BatchId { get; }

    public GameContextCoordinate Coordinate { get; }

    public IReadOnlyList<DurableRunRequest> Runs { get; }

    /// <summary>
    /// Optional aggregate reservation cap. The coordinator admits the batch
    /// only when the sum of every participant's hard run budget fits.
    /// </summary>
    public MultiActorBatchBudget? AggregateBudget { get; }
}

/// <summary>
/// Bounds the aggregate hard-budget reservation of one concurrent actor
/// batch. Reservation is conservative by design: every admitted participant
/// may consume its complete run budget without exceeding this cap.
/// </summary>
public sealed class MultiActorBatchBudget
{
    private readonly decimal _maxCost;

    public MultiActorBatchBudget(
        long maxTokens,
        long maxActions,
        long maxDurationMs,
        string maxCostUsd)
    {
        if (maxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        if (maxActions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActions));
        }

        if (maxDurationMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDurationMs));
        }

        if (!TryParseCost(maxCostUsd, out _maxCost))
        {
            throw new ArgumentException(
                "The aggregate cost budget must be a non-negative decimal.",
                nameof(maxCostUsd));
        }

        MaxTokens = maxTokens;
        MaxActions = maxActions;
        MaxDurationMs = maxDurationMs;
        MaxCostUsd = maxCostUsd;
    }

    public long MaxTokens { get; }

    public long MaxActions { get; }

    public long MaxDurationMs { get; }

    public string MaxCostUsd { get; }

    internal decimal MaxCost => _maxCost;

    internal static bool TryParseCost(string? value, out decimal cost)
    {
        cost = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var index = 0;
        if (value[0] == '0')
        {
            index = 1;
            if (value.Length > 1 && value[1] != '.')
            {
                return false;
            }
        }
        else if (value[0] is >= '1' and <= '9')
        {
            index = 1;
            while (index < value.Length
                   && value[index] is >= '0' and <= '9')
            {
                index++;
            }
        }
        else
        {
            return false;
        }

        if (index < value.Length)
        {
            if (value[index] != '.' || index == value.Length - 1)
            {
                return false;
            }

            index++;
            while (index < value.Length)
            {
                if (value[index] is not (>= '0' and <= '9'))
                {
                    return false;
                }

                index++;
            }
        }

        return decimal.TryParse(
                   value,
                   System.Globalization.NumberStyles.AllowDecimalPoint,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out cost)
               && cost >= 0;
    }
}

public sealed class MultiActorBatchBudgetReservation
{
    internal MultiActorBatchBudgetReservation(
        MultiActorBatchBudget limit,
        long reservedTokens,
        long reservedActions,
        long reservedDurationMs,
        decimal reservedCost)
    {
        Limit = limit;
        ReservedTokens = reservedTokens;
        ReservedActions = reservedActions;
        ReservedDurationMs = reservedDurationMs;
        ReservedCostUsd = reservedCost.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public MultiActorBatchBudget Limit { get; }

    public long ReservedTokens { get; }

    public long ReservedActions { get; }

    public long ReservedDurationMs { get; }

    public string ReservedCostUsd { get; }
}

public sealed class MultiActorRunResult
{
    internal MultiActorRunResult(
        int inputIndex,
        string agentId,
        string decisionKey,
        DurableRunOutcome? outcome,
        Exception? error)
    {
        InputIndex = inputIndex;
        AgentId = agentId;
        DecisionKey = decisionKey;
        Outcome = outcome;
        Error = error;
    }

    public int InputIndex { get; }

    public string AgentId { get; }

    public string DecisionKey { get; }

    public DurableRunOutcome? Outcome { get; }

    public Exception? Error { get; }

    public bool Succeeded =>
        Outcome is not null
        && Error is null
        && string.Equals(
            Outcome.Run.State,
            RunStates.Completed,
            StringComparison.Ordinal);
}

public sealed class MultiActorBatchOutcome
{
    internal MultiActorBatchOutcome(
        MultiActorBatchManifest manifest,
        IReadOnlyList<MultiActorRunResult> results)
    {
        Manifest = manifest;
        Results = results;
    }

    /// <summary>
    /// Durable participant identities that a host can persist and later use
    /// for guarded resume or abandonment.
    /// </summary>
    public MultiActorBatchManifest Manifest { get; }

    public string BatchId => Manifest.BatchId;

    public GameContextCoordinate Coordinate => Manifest.Coordinate;

    /// <summary>
    /// Results retain input order even though actor runs execute concurrently.
    /// </summary>
    public IReadOnlyList<MultiActorRunResult> Results { get; }
}

public sealed class MultiActorBatchParticipant
{
    public MultiActorBatchParticipant(
        int inputIndex,
        string agentId,
        string runId,
        string decisionKey)
    {
        if (inputIndex is < 0 or >= 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(inputIndex));
        }

        InputIndex = inputIndex;
        AgentId = RuntimeGuard.RequiredId(agentId, nameof(agentId));
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        DecisionKey = MultiActorDecisionCoordinator.RequiredDecisionKey(
            decisionKey,
            nameof(decisionKey));
    }

    public int InputIndex { get; }

    public string AgentId { get; }

    public string RunId { get; }

    public string DecisionKey { get; }
}

public sealed class MultiActorBatchManifest
{
    internal MultiActorBatchManifest(
        string batchId,
        GameContextCoordinate coordinate,
        IReadOnlyList<MultiActorBatchParticipant> participants,
        MultiActorBatchBudgetReservation? budgetReservation)
    {
        BatchId = batchId;
        Coordinate = coordinate;
        Participants = participants;
        BudgetReservation = budgetReservation;
    }

    public string BatchId { get; }

    public GameContextCoordinate Coordinate { get; }

    public IReadOnlyList<MultiActorBatchParticipant> Participants { get; }

    public MultiActorBatchBudgetReservation? BudgetReservation { get; }
}

/// <summary>
/// An immutable, deeply snapshotted multi-actor admission result. Preparing a
/// batch performs every count, byte, node, coordinate, identity, and aggregate
/// budget check before a caller persists a dispatch manifest.
/// </summary>
public sealed class MultiActorPreparedBatch
{
    private readonly IReadOnlyList<DurableRunRequest> _requests;

    internal MultiActorPreparedBatch(
        object coordinatorIdentity,
        MultiActorBatchManifest manifest,
        IReadOnlyList<DurableRunRequest> requests,
        IReadOnlyList<string> requestDigests,
        string digest)
    {
        CoordinatorIdentity = coordinatorIdentity;
        Manifest = manifest;
        _requests = requests;
        RequestDigests = requestDigests;
        Digest = digest;
    }

    internal object CoordinatorIdentity { get; }

    internal IReadOnlyList<DurableRunRequest> Requests => _requests;

    public MultiActorBatchManifest Manifest { get; }

    /// <summary>
    /// Canonical digests of the exact prepared requests in original input
    /// order, including reserved participant metadata.
    /// </summary>
    public IReadOnlyList<string> RequestDigests { get; }

    public string Digest { get; }
}

public sealed class MultiActorParticipantNotStartedException
    : InvalidOperationException
{
    public MultiActorParticipantNotStartedException(string runId)
        : base(
            "The participant has no durable run and policy forbids starting "
            + "one during this continuation.")
    {
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
    }

    public string RunId { get; }
}

public sealed class MultiActorBatchAlreadyActiveException
    : InvalidOperationException
{
    public MultiActorBatchAlreadyActiveException(string batchId)
        : base("The multi-actor batch already has an active execution.")
    {
        BatchId = RuntimeGuard.RequiredId(batchId, nameof(batchId));
    }

    public string BatchId { get; }
}

public sealed class MultiActorBatchCapacityExceededException
    : InvalidOperationException
{
    public MultiActorBatchCapacityExceededException(int limit)
        : base($"No multi-actor batch capacity remains (limit {limit}).")
    {
        Limit = limit;
    }

    public int Limit { get; }
}

public sealed class MultiActorQueuedParticipantCapacityExceededException
    : InvalidOperationException
{
    public MultiActorQueuedParticipantCapacityExceededException(int limit)
        : base(
            "No multi-actor queued-participant capacity remains "
            + $"(limit {limit}).")
    {
        Limit = limit;
    }

    public int Limit { get; }
}

/// <summary>
/// Optional lifecycle notifications for a host that stages simultaneous
/// actions. BatchStarted supplies the expected participants before any run can
/// submit an action. ActorFinished marks a participant that will submit no more
/// actions, including a participant that failed or was explicitly abandoned.
/// Every callback must be idempotent: process recovery and an uncertain
/// callback outcome can cause the same notification to be retried.
/// An unsettled callback is a reconciliation boundary and is not followed by
/// a concurrent abort notification, because the late callback may still land.
/// </summary>
public interface IMultiActorDecisionLifecycle
{
    ValueTask BatchStartedAsync(
        MultiActorBatchManifest manifest,
        CancellationToken cancellationToken);

    ValueTask ActorFinishedAsync(
        string batchId,
        MultiActorRunResult result,
        CancellationToken cancellationToken);

    ValueTask BatchAbortedAsync(
        string batchId,
        string reasonCode,
        CancellationToken cancellationToken);
}

public sealed class MultiActorBatchAbortUncertainException : Exception
{
    public MultiActorBatchAbortUncertainException(
        string batchId,
        string reasonCode,
        string message)
        : base(message)
    {
        BatchId = batchId;
        ReasonCode = reasonCode;
    }

    public string BatchId { get; }

    public string ReasonCode { get; }
}

public sealed class MultiActorParticipantAbandonedException : Exception
{
    public MultiActorParticipantAbandonedException(
        string runId,
        string reasonCode)
        : base(
            "The game host explicitly abandoned this multi-actor participant.")
    {
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        ReasonCode = RuntimeGuard.RequiredId(
            reasonCode,
            nameof(reasonCode));
    }

    public string RunId { get; }

    public string ReasonCode { get; }
}

public sealed class MultiActorParticipantResumeCapacityExceededException
    : InvalidOperationException
{
    public MultiActorParticipantResumeCapacityExceededException(int limit)
        : base(
            $"No multi-actor participant resume capacity remains (limit {limit}).")
    {
        Limit = limit;
    }

    public int Limit { get; }
}

public sealed class MultiActorBatchExecutionUncertainException : Exception
{
    internal MultiActorBatchExecutionUncertainException(
        string batchId,
        IReadOnlyList<string> runIds,
        IReadOnlyList<Exception> errors)
        : base(
            "One or more participants failed without a durable outcome; "
            + "the host staging window was aborted.",
            new AggregateException(errors))
    {
        BatchId = batchId;
        RunIds = runIds;
    }

    public string BatchId { get; }

    public IReadOnlyList<string> RunIds { get; }
}

/// <summary>
/// Runs many actor decisions concurrently with bounded pressure and isolated
/// failures. It coordinates thinking; it deliberately does not adjudicate
/// game rules. The batch and decision identifiers are propagated to every
/// action request so an IGameHost can stage or resolve simultaneous actions.
/// </summary>
public sealed class MultiActorDecisionCoordinator
{
    private const string ParticipantInputIndexExtension =
        "gameAgent.multiActorInputIndex";
    private static readonly ConditionalWeakTable<
        IDurableAgentRuntime,
        ConcurrentDictionary<string, byte>> SharedActiveBatchOperations =
        new();

    private readonly IDurableAgentRuntime _runtime;
    private readonly MultiActorCoordinatorOptions _options;
    private readonly IMultiActorDecisionLifecycle? _lifecycle;
    private readonly SemaphoreSlim _lifecycleNotificationSlots;
    private readonly SemaphoreSlim _abortNotificationSlots;
    private readonly SemaphoreSlim _participantRunSlots;
    private readonly SemaphoreSlim _participantResumeSlots;
    private readonly SemaphoreSlim _activeBatchSlots;
    private readonly object _preparationIdentity = new();
    private readonly ConcurrentDictionary<string, byte>
        _activeBatchOperations;
    private readonly ConcurrentDictionary<string, byte>
        _activeParticipantOperations = new(StringComparer.Ordinal);
    private int _queuedParticipantCount;

    public MultiActorDecisionCoordinator(
        IDurableAgentRuntime runtime,
        MultiActorCoordinatorOptions? options = null,
        IMultiActorDecisionLifecycle? lifecycle = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _activeBatchOperations = SharedActiveBatchOperations.GetValue(
            _runtime,
            static _ => new ConcurrentDictionary<string, byte>(
                StringComparer.Ordinal));
        _options = options ?? new MultiActorCoordinatorOptions();
        _lifecycle = lifecycle;
        _lifecycleNotificationSlots = new SemaphoreSlim(
            _options.MaxDetachedLifecycleNotifications,
            _options.MaxDetachedLifecycleNotifications);
        _abortNotificationSlots = new SemaphoreSlim(
            _options.MaxDetachedAbortNotifications,
            _options.MaxDetachedAbortNotifications);
        _participantRunSlots = new SemaphoreSlim(
            _options.MaxConcurrentRuns,
            _options.MaxConcurrentRuns);
        _participantResumeSlots = new SemaphoreSlim(
            _options.MaxConcurrentParticipantResumes,
            _options.MaxConcurrentParticipantResumes);
        _activeBatchSlots = new SemaphoreSlim(
            _options.MaxConcurrentBatches,
            _options.MaxConcurrentBatches);
    }

    public int ActiveParticipantOperationCount =>
        _activeParticipantOperations.Count;

    public int ActiveBatchOperationCount => _activeBatchOperations.Count;

    public int QueuedParticipantCount =>
        Volatile.Read(ref _queuedParticipantCount);

    public MultiActorPreparedBatch PrepareBatch(
        MultiActorDecisionBatch batch,
        CancellationToken cancellationToken = default)
    {
        if (batch is null)
        {
            throw new ArgumentNullException(nameof(batch));
        }

        var prepared = ValidateAndPrepare(
            batch,
            cancellationToken,
            out var budgetReservation);
        var manifest = new MultiActorBatchManifest(
            batch.BatchId,
            batch.Coordinate,
            new ReadOnlyCollection<MultiActorBatchParticipant>(
                prepared
                    .Select(
                        item => new MultiActorBatchParticipant(
                            item.InputIndex,
                            item.AgentId,
                            item.Request.Run.RunId,
                            item.DecisionKey))
                    .ToArray()),
            budgetReservation);
        var requests = new ReadOnlyCollection<DurableRunRequest>(
            prepared.Select(item => item.Request).ToArray());
        var requestDigests = new ReadOnlyCollection<string>(
            requests.Select(ComputePreparedRequestDigest).ToArray());
        return new MultiActorPreparedBatch(
            _preparationIdentity,
            manifest,
            requests,
            requestDigests,
            ComputePreparedBatchDigest(manifest, requestDigests));
    }

    public ValueTask<MultiActorBatchOutcome> RunAsync(
        MultiActorDecisionBatch batch,
        CancellationToken cancellationToken = default)
    {
        return RunPreparedBatchAsync(
            PrepareBatch(batch, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Starts the exact immutable requests returned by <see cref="PrepareBatch"/>.
    /// </summary>
    public async ValueTask<MultiActorBatchOutcome> RunPreparedBatchAsync(
        MultiActorPreparedBatch batch,
        CancellationToken cancellationToken = default)
    {
        EnsurePreparedByThisCoordinator(batch);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = batch.Manifest;
        var requests = batch.Requests;
        using var batchOperation = EnterBatchOperation(
            manifest.BatchId,
            requests.Count);
        try
        {
            if (_lifecycle is not null)
            {
                await NotifyLifecycleAsync(
                        manifest.BatchId,
                        "batch_start_uncertain",
                        token => _lifecycle.BatchStartedAsync(
                            manifest,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var results = new MultiActorRunResult[requests.Count];
            var nextIndex = -1;
            var workerCount = Math.Min(
                _options.MaxConcurrentRuns,
                requests.Count);
            var workers = new Task[workerCount];
            for (var worker = 0; worker < workerCount; worker++)
            {
                workers[worker] = WorkAsync();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            if (_lifecycle is not null)
            {
                var uncertainIndices = Enumerable.Range(0, results.Length)
                    .Where(index => results[index].Outcome is null)
                    .ToArray();
                if (uncertainIndices.Length > 0)
                {
                    throw new MultiActorBatchExecutionUncertainException(
                        manifest.BatchId,
                        new ReadOnlyCollection<string>(
                            uncertainIndices
                                .Select(
                                    index => requests[index]
                                        .Run.RunId)
                                .ToArray()),
                        new ReadOnlyCollection<Exception>(
                            uncertainIndices
                                .Select(
                                    index => results[index].Error
                                             ?? new InvalidOperationException(
                                                 "A participant returned no durable outcome."))
                                .ToArray()));
                }
            }

            return new MultiActorBatchOutcome(
                manifest,
                new ReadOnlyCollection<MultiActorRunResult>(results));

            async Task WorkAsync()
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= requests.Count)
                    {
                        return;
                    }

                    var request = requests[index];
                    var participant = manifest.Participants[index];
                    var operationKey = ParticipantOperationKey(
                        participant.RunId);
                    if (!_activeParticipantOperations.TryAdd(
                            operationKey,
                            0))
                    {
                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome: null,
                            new InvalidOperationException(
                                "The requested participant already has an "
                                + "active operation."));
                        continue;
                    }

                    var runSlotHeld = false;
                    try
                    {
                        await _participantRunSlots.WaitAsync(
                                cancellationToken)
                            .ConfigureAwait(false);
                        runSlotHeld = true;
                        var outcome = await _runtime.RunAsync(
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);
                        ParticipantFromDurableRun(
                            manifest.BatchId,
                            participant,
                            outcome.Run);
                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome,
                            error: null);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome: null,
                            exception);
                    }
                    finally
                    {
                        if (runSlotHeld)
                        {
                            _participantRunSlots.Release();
                        }

                        _activeParticipantOperations.TryRemove(
                            operationKey,
                            out _);
                    }

                    var result = results[index];
                    if (result.Outcome?.IsTerminal == true
                        && _lifecycle is not null)
                    {
                        await NotifyLifecycleAsync(
                                manifest.BatchId,
                                "actor_finish_uncertain",
                                token => _lifecycle.ActorFinishedAsync(
                                    manifest.BatchId,
                                    result,
                                    token),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (_lifecycle is not null
                && exception
                is not MultiActorBatchAbortUncertainException)
            {
                try
                {
                    await NotifyBatchAbortedAsync(
                            manifest.BatchId,
                            cancellationToken.IsCancellationRequested
                                ? "cancelled"
                                : "batch_execution_failed")
                        .ConfigureAwait(false);
                }
                catch (Exception abortException)
                    when (abortException is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    throw new AggregateException(
                        "The decision batch and its abort notification failed.",
                        exception,
                        abortException);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Continues every participant in one previously prepared admission
    /// boundary. Existing durable runs are guarded and resumed. A run is
    /// started only after an explicit not-found result and only when both the
    /// caller and that participant's continuation allow it.
    /// </summary>
    public async ValueTask<MultiActorBatchOutcome>
        ResumeOrStartPreparedBatchAsync(
            MultiActorPreparedBatch batch,
            IReadOnlyDictionary<string, DurableRunContinuation>?
                continuations = null,
            IReadOnlyDictionary<string, DurableRunSemanticExpectation>?
                semanticExpectations = null,
            IGameOperationReconciler? reconciler = null,
            bool startMissing = true,
            CancellationToken cancellationToken = default)
    {
        EnsurePreparedByThisCoordinator(batch);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGuardedResumeSupported();
        var manifest = batch.Manifest;
        var requests = batch.Requests;
        using var batchOperation = EnterBatchOperation(
            manifest.BatchId,
            requests.Count);
        var continuationSnapshot = SnapshotContinuations(
            manifest,
            continuations,
            cancellationToken);
        var expectationSnapshot = SnapshotSemanticExpectations(
            manifest,
            semanticExpectations,
            cancellationToken);
        try
        {
            if (_lifecycle is not null)
            {
                await NotifyLifecycleAsync(
                        manifest.BatchId,
                        "batch_start_uncertain",
                        token => _lifecycle.BatchStartedAsync(
                            manifest,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var results = new MultiActorRunResult[requests.Count];
            var nextIndex = -1;
            var workerCount = Math.Min(
                Math.Min(
                    _options.MaxConcurrentRuns,
                    _options.MaxConcurrentParticipantResumes),
                requests.Count);
            var workers = new Task[workerCount];
            for (var worker = 0; worker < workerCount; worker++)
            {
                workers[worker] = WorkAsync();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            if (_lifecycle is not null)
            {
                var uncertainIndices = Enumerable.Range(0, results.Length)
                    .Where(index => results[index].Outcome is null)
                    .ToArray();
                if (uncertainIndices.Length > 0)
                {
                    throw new MultiActorBatchExecutionUncertainException(
                        manifest.BatchId,
                        new ReadOnlyCollection<string>(
                            uncertainIndices
                                .Select(index => requests[index].Run.RunId)
                                .ToArray()),
                        new ReadOnlyCollection<Exception>(
                            uncertainIndices
                                .Select(
                                    index => results[index].Error
                                             ?? new InvalidOperationException(
                                                 "A participant returned no durable outcome."))
                                .ToArray()));
                }
            }

            return new MultiActorBatchOutcome(
                manifest,
                new ReadOnlyCollection<MultiActorRunResult>(results));

            async Task WorkAsync()
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= requests.Count)
                    {
                        return;
                    }

                    var request = requests[index];
                    var participant = manifest.Participants[index];
                    var continuation = continuationSnapshot[
                        participant.RunId];
                    var operationKey = ParticipantOperationKey(
                        participant.RunId);
                    if (!_activeParticipantOperations.TryAdd(
                            operationKey,
                            0))
                    {
                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome: null,
                            new InvalidOperationException(
                                "The requested participant already has an "
                                + "active operation."));
                        continue;
                    }

                    var runSlotHeld = false;
                    try
                    {
                        await _participantRunSlots.WaitAsync(
                                cancellationToken)
                            .ConfigureAwait(false);
                        runSlotHeld = true;
                        if (!_participantResumeSlots.Wait(
                                0,
                                cancellationToken))
                        {
                            results[index] = new MultiActorRunResult(
                                index,
                                participant.AgentId,
                                participant.DecisionKey,
                                outcome: null,
                                new
                                    MultiActorParticipantResumeCapacityExceededException(
                                        _options
                                            .MaxConcurrentParticipantResumes));
                            continue;
                        }

                        var resumeSlotHeld = true;
                        DurableRunOutcome outcome;
                        try
                        {
                            expectationSnapshot.TryGetValue(
                                participant.RunId,
                                out var expectation);
                            outcome = await _runtime.ResumeAsync(
                                    participant.RunId,
                                    continuation,
                                    reconciler,
                                    cancellationToken,
                                    ResumeGuard(
                                        manifest.BatchId,
                                        participant,
                                        expectation))
                                .ConfigureAwait(false);
                            ParticipantFromDurableRun(
                                manifest.BatchId,
                                participant,
                                outcome.Run);
                        }
                        catch (DurableRunNotFoundException)
                        {
                            if (!startMissing
                                || continuation.RequestCancellation)
                            {
                                results[index] = new MultiActorRunResult(
                                    index,
                                    participant.AgentId,
                                    participant.DecisionKey,
                                    outcome: null,
                                    new MultiActorParticipantNotStartedException(
                                        participant.RunId));
                                continue;
                            }

                            outcome = await _runtime.RunAsync(
                                    request,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            ParticipantFromDurableRun(
                                manifest.BatchId,
                                participant,
                                outcome.Run);
                        }
                        finally
                        {
                            if (resumeSlotHeld)
                            {
                                _participantResumeSlots.Release();
                            }
                        }

                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome,
                            error: null);
                        if (outcome.IsTerminal && _lifecycle is not null)
                        {
                            await NotifyLifecycleAsync(
                                    manifest.BatchId,
                                    "actor_finish_uncertain",
                                    token => _lifecycle.ActorFinishedAsync(
                                        manifest.BatchId,
                                        results[index],
                                        token),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (MultiActorBatchAbortUncertainException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[index] = new MultiActorRunResult(
                            index,
                            participant.AgentId,
                            participant.DecisionKey,
                            outcome: null,
                            exception);
                    }
                    finally
                    {
                        if (runSlotHeld)
                        {
                            _participantRunSlots.Release();
                        }

                        _activeParticipantOperations.TryRemove(
                            operationKey,
                            out _);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (_lifecycle is not null
                && exception
                is not MultiActorBatchAbortUncertainException)
            {
                try
                {
                    await NotifyBatchAbortedAsync(
                            manifest.BatchId,
                            cancellationToken.IsCancellationRequested
                                ? "cancelled"
                                : "batch_execution_failed")
                        .ConfigureAwait(false);
                }
                catch (Exception abortException)
                    when (abortException is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    throw new AggregateException(
                        "The decision batch and its abort notification failed.",
                        exception,
                        abortException);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Resumes one nonterminal participant while preserving its original batch
    /// identity. The participant descriptor must come from the batch manifest
    /// and is checked against the durable run before ownership or any provider,
    /// reconciler, or host side effect. The lifecycle receives ActorFinished
    /// only after the resumed run becomes terminal. A terminal run can be
    /// resumed again to replay an uncertain idempotent ActorFinished callback.
    /// </summary>
    public ValueTask<MultiActorRunResult> ResumeParticipantAsync(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        return ResumeParticipantCoreAsync(
            batchId,
            participant,
            continuation,
            reconciler,
            semanticExpectation: null,
            cancellationToken);
    }

    /// <summary>
    /// Resumes a participant only if the durable semantic extension still
    /// matches a caller-provided expectation derived from current game state.
    /// </summary>
    public ValueTask<MultiActorRunResult> ResumeParticipantAsync(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunSemanticExpectation semanticExpectation,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        if (semanticExpectation is null)
        {
            throw new ArgumentNullException(nameof(semanticExpectation));
        }

        return ResumeParticipantCoreAsync(
            batchId,
            participant,
            continuation,
            reconciler,
            semanticExpectation,
            cancellationToken);
    }

    private async ValueTask<MultiActorRunResult> ResumeParticipantCoreAsync(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        DurableRunSemanticExpectation? semanticExpectation,
        CancellationToken cancellationToken)
    {
        batchId = RuntimeGuard.RequiredId(batchId, nameof(batchId));
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureGuardedResumeSupported();
        var key = ParticipantOperationKey(participant.RunId);
        if (!_activeParticipantOperations.TryAdd(key, 0))
        {
            throw new InvalidOperationException(
                "The requested participant already has an active operation.");
        }

        var runSlotHeld = false;
        var resumeSlotHeld = false;
        try
        {
            await _participantRunSlots.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            runSlotHeld = true;
            if (!_participantResumeSlots.Wait(0, cancellationToken))
            {
                throw new
                    MultiActorParticipantResumeCapacityExceededException(
                        _options.MaxConcurrentParticipantResumes);
            }

            resumeSlotHeld = true;
            var outcome = await _runtime.ResumeAsync(
                    participant.RunId,
                    continuation,
                    reconciler,
                    cancellationToken,
                    ResumeGuard(
                        batchId,
                        participant,
                        semanticExpectation))
                .ConfigureAwait(false);
            ParticipantFromDurableRun(
                batchId,
                participant,
                outcome.Run);
            var result = new MultiActorRunResult(
                participant.InputIndex,
                participant.AgentId,
                participant.DecisionKey,
                outcome,
                error: null);

            if (outcome.IsTerminal && _lifecycle is not null)
            {
                await NotifyLifecycleAsync(
                        batchId,
                        "actor_finish_uncertain",
                        token => _lifecycle.ActorFinishedAsync(
                            batchId,
                            result,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (resumeSlotHeld)
            {
                _participantResumeSlots.Release();
            }

            if (runSlotHeld)
            {
                _participantRunSlots.Release();
            }

            _activeParticipantOperations.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Durably requests cancellation for a participant that the game has
    /// decided never to resume. Pending game operations must first be
    /// reconciled; ActorFinished is emitted only after the durable run is
    /// terminal. Retrying this method safely replays terminal settlement.
    /// </summary>
    public async ValueTask<MultiActorRunResult>
        ReconcileAbandonedParticipantAsync(
            string batchId,
            MultiActorBatchParticipant participant,
            string reasonCode,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
    {
        if (_lifecycle is null)
        {
            throw new InvalidOperationException(
                "Participant abandonment requires a lifecycle host.");
        }

        batchId = RuntimeGuard.RequiredId(batchId, nameof(batchId));
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }

        reasonCode = RuntimeGuard.RequiredId(
            reasonCode,
            nameof(reasonCode));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGuardedResumeSupported();
        var key = ParticipantOperationKey(participant.RunId);
        if (!_activeParticipantOperations.TryAdd(key, 0))
        {
            throw new InvalidOperationException(
                "The requested participant already has an active operation.");
        }

        var runSlotHeld = false;
        var resumeSlotHeld = false;
        try
        {
            await _participantRunSlots.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            runSlotHeld = true;
            if (!_participantResumeSlots.Wait(0, cancellationToken))
            {
                throw new
                    MultiActorParticipantResumeCapacityExceededException(
                        _options.MaxConcurrentParticipantResumes);
            }

            resumeSlotHeld = true;
            var outcome = await _runtime.ResumeAsync(
                    participant.RunId,
                    new DurableRunContinuation
                    {
                        RequestCancellation = true
                    },
                    reconciler,
                    cancellationToken,
                    ResumeGuard(batchId, participant))
                .ConfigureAwait(false);
            ParticipantFromDurableRun(
                batchId,
                participant,
                outcome.Run);
            var result = new MultiActorRunResult(
                participant.InputIndex,
                participant.AgentId,
                participant.DecisionKey,
                outcome,
                string.Equals(
                    outcome.Run.State,
                    RunStates.Cancelled,
                    StringComparison.Ordinal)
                    ? new MultiActorParticipantAbandonedException(
                        participant.RunId,
                        reasonCode)
                    : null);
            if (outcome.IsTerminal)
            {
                await NotifyLifecycleAsync(
                        batchId,
                        "actor_finish_uncertain",
                        token => _lifecycle.ActorFinishedAsync(
                            batchId,
                            result,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (resumeSlotHeld)
            {
                _participantResumeSlots.Release();
            }

            if (runSlotHeld)
            {
                _participantRunSlots.Release();
            }

            _activeParticipantOperations.TryRemove(key, out _);
        }
    }

    private async Task NotifyLifecycleAsync(
        string batchId,
        string reasonCode,
        Func<CancellationToken, ValueTask> notification,
        CancellationToken callerCancellation)
    {
        callerCancellation.ThrowIfCancellationRequested();
        if (!await _lifecycleNotificationSlots.WaitAsync(0)
                .ConfigureAwait(false))
        {
            throw new MultiActorBatchAbortUncertainException(
                batchId,
                reasonCode,
                "No bounded lifecycle-notification capacity remains; host reconciliation is required.");
        }

        var deadline = new CancellationTokenSource(
            _options.LifecycleSettlementTimeout);
        Task operation;
        try
        {
            operation = Task.Run(
                async () => await notification(deadline.Token)
                    .ConfigureAwait(false));
        }
        catch
        {
            deadline.Dispose();
            _lifecycleNotificationSlots.Release();
            throw;
        }

        var callerCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = callerCancellation.Register(
            () => callerCancelled.TrySetResult(true));
        var timeout = Task.Delay(_options.LifecycleSettlementTimeout);
        var completed = await Task.WhenAny(
                operation,
                timeout,
                callerCancelled.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, operation))
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            finally
            {
                deadline.Dispose();
                _lifecycleNotificationSlots.Release();
            }

            return;
        }

        _ = ObserveDetachedLifecycleNotificationAsync(
            operation,
            deadline,
            _lifecycleNotificationSlots);
        throw new MultiActorBatchAbortUncertainException(
            batchId,
            reasonCode,
            callerCancellation.IsCancellationRequested
                ? "Caller cancellation raced an unsettled lifecycle "
                  + "notification; host reconciliation is required."
                : "A lifecycle notification did not settle; host "
                  + "reconciliation is required.");
    }

    private void EnsurePreparedByThisCoordinator(
        MultiActorPreparedBatch? batch)
    {
        if (batch is null)
        {
            throw new ArgumentNullException(nameof(batch));
        }

        if (!ReferenceEquals(
                batch.CoordinatorIdentity,
                _preparationIdentity))
        {
            throw new ArgumentException(
                "The prepared batch belongs to a different coordinator.",
                nameof(batch));
        }

        if (batch.Requests.Count == 0
            || batch.Requests.Count
            != batch.Manifest.Participants.Count
            || batch.RequestDigests.Count != batch.Requests.Count)
        {
            throw new InvalidDataException(
                "The prepared batch manifest is incomplete.");
        }
    }

    private IReadOnlyDictionary<string, DurableRunContinuation>
        SnapshotContinuations(
            MultiActorBatchManifest manifest,
            IReadOnlyDictionary<string, DurableRunContinuation>? source,
            CancellationToken cancellationToken)
    {
        var supplied = source
                       ?? new Dictionary<string, DurableRunContinuation>(
                           StringComparer.Ordinal);
        if (supplied.Count > manifest.Participants.Count)
        {
            throw new ArgumentException(
                "Continuation count exceeds the participant manifest.",
                nameof(source));
        }

        var known = new HashSet<string>(
            manifest.Participants.Select(item => item.RunId),
            StringComparer.Ordinal);
        var suppliedCount = 0;
        foreach (var pair in supplied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            suppliedCount++;
            if (suppliedCount > manifest.Participants.Count)
            {
                throw new ArgumentException(
                    "Continuation enumeration exceeds the participant "
                    + "manifest.",
                    nameof(source));
            }

            if (!known.Contains(pair.Key) || pair.Value is null)
            {
                throw new ArgumentException(
                    "Continuations must target known non-null participants.",
                    nameof(source));
            }
        }

        var snapshot = new Dictionary<string, DurableRunContinuation>(
            manifest.Participants.Count,
            StringComparer.Ordinal);
        long batchSnapshotBytes = 0;
        long batchSnapshotNodes = 0;
        foreach (var participant in manifest.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = supplied.TryGetValue(
                participant.RunId,
                out var continuation)
                ? continuation
                : new DurableRunContinuation();
            var continuationSnapshot = new DurableRunContinuation
            {
                Context = SnapshotContext(
                    value.Context,
                    cancellationToken),
                ActiveSkills = SnapshotSkills(
                    value.ActiveSkills,
                    cancellationToken),
                ReplaceActiveSkills = value.ReplaceActiveSkills,
                LaneId = SnapshotLaneId(
                    value.LaneId,
                    nameof(source)),
                WorkloadClass = value.WorkloadClass is null
                    ? null
                    : ProviderWorkloadClasses.Normalize(
                        value.WorkloadClass,
                        nameof(source)),
                RequestCancellation = value.RequestCancellation,
                FinalOutputContract =
                    value.FinalOutputContract?.Snapshot()
            };
            cancellationToken.ThrowIfCancellationRequested();
            var measurement = MeasureContinuationSnapshot(
                continuationSnapshot);
            if (batchSnapshotBytes + measurement.Utf8Bytes
                > _options.MaxBatchSnapshotUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(source),
                    "multi_actor_batch_snapshot_bytes_exceeded",
                    "The aggregate continuation snapshot exceeds the "
                    + "multi-actor byte budget.");
            }

            if (batchSnapshotNodes + measurement.Nodes
                > _options.MaxBatchSnapshotJsonNodes)
            {
                throw new RuntimeContentLimitException(
                    nameof(source),
                    "multi_actor_batch_snapshot_nodes_exceeded",
                    "The aggregate continuation snapshot exceeds the "
                    + "multi-actor node budget.");
            }

            batchSnapshotBytes += measurement.Utf8Bytes;
            batchSnapshotNodes += measurement.Nodes;
            snapshot.Add(participant.RunId, continuationSnapshot);
        }

        return new ReadOnlyDictionary<string, DurableRunContinuation>(
            snapshot);
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
                maxUtf8Bytes: _options.MaxSnapshotUtf8BytesPerRun,
                maxDepth: 64,
                maxNodes: _options.MaxSnapshotJsonNodesPerRun,
                maxStringUtf8Bytes:
                    _options.MaxSnapshotUtf8BytesPerRun,
                maxContainerItems: Math.Max(
                    4_096,
                    Math.Max(
                        _options.MaxContextCandidatesPerRun,
                        _options.MaxActiveSkillsPerRun))),
            nameof(continuation));
    }

    private static IReadOnlyDictionary<
        string,
        DurableRunSemanticExpectation> SnapshotSemanticExpectations(
            MultiActorBatchManifest manifest,
            IReadOnlyDictionary<string, DurableRunSemanticExpectation>?
                source,
            CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return new ReadOnlyDictionary<
                string,
                DurableRunSemanticExpectation>(
                new Dictionary<
                    string,
                    DurableRunSemanticExpectation>(
                    StringComparer.Ordinal));
        }

        if (source.Count > manifest.Participants.Count)
        {
            throw new ArgumentException(
                "Semantic expectation count exceeds the participant "
                + "manifest.",
                nameof(source));
        }

        var known = new HashSet<string>(
            manifest.Participants.Select(item => item.RunId),
            StringComparer.Ordinal);
        var snapshot = new Dictionary<
            string,
            DurableRunSemanticExpectation>(
            source.Count,
            StringComparer.Ordinal);
        var suppliedCount = 0;
        foreach (var pair in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            suppliedCount++;
            if (suppliedCount > manifest.Participants.Count)
            {
                throw new ArgumentException(
                    "Semantic expectation enumeration exceeds the "
                    + "participant manifest.",
                    nameof(source));
            }

            if (!known.Contains(pair.Key) || pair.Value is null)
            {
                throw new ArgumentException(
                    "Semantic expectations must target known non-null "
                    + "participants.",
                    nameof(source));
            }

            snapshot.Add(
                pair.Key,
                new DurableRunSemanticExpectation(
                    pair.Value.ExtensionName,
                    pair.Value.ExpectedSha256));
        }

        return new ReadOnlyDictionary<
            string,
            DurableRunSemanticExpectation>(snapshot);
    }

    private static string ComputePreparedRequestDigest(
        DurableRunRequest request)
    {
        var envelope = JsonArrayBuilder.Object(
            ("run", ProtocolJson.ToElement(request.Run)),
            ("runInput", DurableRunInputJournalCodec.Encode(
                request.Context,
                request.ActiveSkills,
                request.WorkloadClass)),
            ("transcript", JsonArrayBuilder.Array(
                request.InitialTranscript.Select(
                    NormalizedMessageJournalCodec.Encode))),
            ("laneId", request.LaneId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(request.LaneId)),
            ("finalOutputContract",
                request.FinalOutputContract is null
                    ? JsonArrayBuilder.Null()
                    : request.FinalOutputContract.ToJson()));
        return CanonicalJsonDigest.ComputeSha256(envelope);
    }

    private static string ComputePreparedBatchDigest(
        MultiActorBatchManifest manifest,
        IReadOnlyList<string> requestDigests)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "multi_actor_prepared_batch.v1");
        digest.Add("batchId", manifest.BatchId);
        foreach (var requestDigest in requestDigests)
        {
            digest.Add("requestDigest", requestDigest);
        }

        var reservation = manifest.BudgetReservation;
        if (reservation is not null)
        {
            digest.Add(
                "maxTokens",
                reservation.Limit.MaxTokens.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add(
                "maxActions",
                reservation.Limit.MaxActions.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add(
                "maxDurationMs",
                reservation.Limit.MaxDurationMs.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add(
                "maxCostUsd",
                reservation.Limit.MaxCostUsd);
            digest.Add(
                "reservedTokens",
                reservation.ReservedTokens.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add(
                "reservedActions",
                reservation.ReservedActions.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add(
                "reservedDurationMs",
                reservation.ReservedDurationMs.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            digest.Add("reservedCostUsd", reservation.ReservedCostUsd);
        }

        return digest.Finish();
    }

    private IReadOnlyList<PreparedRun> ValidateAndPrepare(
        MultiActorDecisionBatch batch,
        CancellationToken cancellationToken,
        out MultiActorBatchBudgetReservation? budgetReservation)
    {
        budgetReservation = null;
        var runCount = batch.Runs.Count;
        if (runCount == 0)
        {
            throw new ArgumentException(
                "A multi-actor batch requires at least one run.",
                nameof(batch));
        }

        if (runCount > _options.MaxBatchSize)
        {
            throw new RuntimeContentLimitException(
                nameof(batch),
                "multi_actor_batch_size_exceeded",
                $"The batch exceeds {_options.MaxBatchSize} runs.");
        }

        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        var decisionKeys = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<PreparedRun>(runCount);
        var batchSnapshotBytes = 0L;
        var batchSnapshotNodes = 0L;
        var reservedTokens = 0L;
        var reservedActions = 0L;
        var reservedDurationMs = 0L;
        var reservedCost = 0m;
        if (runCount > 1 && batch.Coordinate.Observer is not null)
        {
            throw new ArgumentException(
                "A shared multi-actor coordinate cannot identify one observer.",
                nameof(batch));
        }

        for (var requestIndex = 0; requestIndex < runCount; requestIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = batch.Runs[requestIndex];
            if (request?.Run is null)
            {
                throw new ArgumentException(
                    "Batch runs cannot contain null requests.",
                    nameof(batch));
            }

            var source = request.Run;
            var runLimits = new JsonValueLimits();
            var safeRun =
                RuntimeProtocolInputGuard.ValidateAgentRunBeforeSerialization(
                    source,
                    runLimits,
                    1_048_576,
                    nameof(request.Run));
            var clonedRun = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(safeRun));
            if (clonedRun.Extensions is null)
            {
                throw new ArgumentException(
                    "A batch run requires an extension collection.",
                    nameof(batch));
            }

            if (clonedRun.Extensions.ContainsKey(
                    ParticipantInputIndexExtension))
            {
                throw new ArgumentException(
                    "A batch run cannot supply reserved participant metadata.",
                    nameof(batch));
            }

            var requiredExtensionSlots =
                clonedRun.Extensions.ContainsKey(
                    GameContextEnvelope.ExtensionName)
                    ? 1
                    : 2;
            if (clonedRun.Extensions.Count
                > ProtocolLimits.MaxProtocolExtensions
                - requiredExtensionSlots)
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_run_extensions_exceeded",
                    "A batch run has no capacity for participant metadata.");
            }

            var runId = RuntimeGuard.RequiredId(
                clonedRun.RunId,
                nameof(clonedRun.RunId));
            var agentId = RuntimeGuard.RequiredId(
                clonedRun.AgentId,
                nameof(clonedRun.AgentId));
            var decisionKey = RequiredDecisionKey(
                clonedRun.DecisionKey ?? string.Empty,
                nameof(clonedRun.DecisionKey));
            if (!runIds.Add(runId))
            {
                throw new ArgumentException(
                    "Batch run identifiers must be unique.",
                    nameof(batch));
            }

            if (!agentIds.Add(agentId))
            {
                throw new ArgumentException(
                    "An actor can appear only once in one decision batch.",
                    nameof(batch));
            }

            if (!decisionKeys.Add(decisionKey))
            {
                throw new ArgumentException(
                    "Decision keys must be unique within a batch.",
                    nameof(batch));
            }

            if (!string.Equals(
                    clonedRun.WorldId,
                    batch.Coordinate.WorldId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every batch run must target the coordinate world.",
                    nameof(batch));
            }

            if (!string.Equals(
                    clonedRun.SessionId,
                    batch.Coordinate.SessionId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every batch run must target the shared coordinate "
                    + "session.",
                    nameof(batch));
            }

            var remainingBatchBytes =
                _options.MaxBatchSnapshotUtf8Bytes - batchSnapshotBytes;
            var remainingBatchNodes =
                _options.MaxBatchSnapshotJsonNodes - batchSnapshotNodes;
            if (remainingBatchBytes <= 0)
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_bytes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its byte budget.");
            }

            if (remainingBatchNodes <= 0)
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_nodes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its node budget.");
            }

            var contextSource = SnapshotInputReferences(
                request.Context,
                _options.MaxContextCandidatesPerRun,
                "multi_actor_context_count_exceeded",
                "A batch run requires a context collection.",
                "Context collections cannot contain null entries.",
                cancellationToken);
            var skillSource = SnapshotInputReferences(
                request.ActiveSkills,
                _options.MaxActiveSkillsPerRun,
                "multi_actor_active_skill_count_exceeded",
                "A batch run requires an active-skill collection.",
                "Active-skill collections cannot contain null entries.",
                cancellationToken);
            var transcriptSource = SnapshotInputReferences(
                request.InitialTranscript,
                _options.MaxTranscriptMessagesPerRun,
                "multi_actor_transcript_count_exceeded",
                "A batch run requires a transcript collection.",
                "Transcript collections cannot contain null entries.",
                cancellationToken);
            var workloadClass = ProviderWorkloadClasses.Normalize(
                request.WorkloadClass,
                nameof(request.WorkloadClass));
            var laneId = SnapshotLaneId(
                request.LaneId,
                nameof(request.LaneId));
            var finalOutputContract =
                request.FinalOutputContract?.Snapshot();
            var boundedRequest = new DurableRunRequest
            {
                Run = clonedRun,
                Context = contextSource,
                ActiveSkills = skillSource,
                InitialTranscript = transcriptSource,
                LaneId = laneId,
                WorkloadClass = workloadClass,
                FinalOutputContract = finalOutputContract
            };
            try
            {
                PreflightSnapshot(
                    boundedRequest,
                    (int)Math.Min(
                        _options.MaxSnapshotUtf8BytesPerRun,
                        remainingBatchBytes),
                    (int)Math.Min(
                        _options.MaxSnapshotJsonNodesPerRun,
                        remainingBatchNodes),
                    cancellationToken);
            }
            catch (RuntimeContentLimitException exception)
                when (remainingBatchBytes
                          < _options.MaxSnapshotUtf8BytesPerRun
                      && exception.LimitCode.Contains(
                          "bytes",
                          StringComparison.Ordinal))
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_bytes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its byte budget.");
            }
            catch (RuntimeContentLimitException exception)
                when (remainingBatchNodes
                          < _options.MaxSnapshotJsonNodesPerRun
                      && exception.LimitCode.Contains(
                          "nodes",
                          StringComparison.Ordinal))
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_nodes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its node budget.");
            }
            if (batch.AggregateBudget is not null)
            {
                ReserveBatchBudget(
                    batch.AggregateBudget,
                    clonedRun.Budget,
                    ref reservedTokens,
                    ref reservedActions,
                    ref reservedDurationMs,
                    ref reservedCost);
            }

            clonedRun.BatchId = batch.BatchId;
            var inputIndex = prepared.Count;
            clonedRun.Extensions[ParticipantInputIndexExtension] =
                JsonArrayBuilder.Number(inputIndex);
            var participantCoordinate = ParticipantCoordinate(
                clonedRun,
                batch.Coordinate,
                runCount);
            GameContextEnvelope.Attach(clonedRun, participantCoordinate);
            ProtocolValidator.EnsureValid(clonedRun);
            var context = SnapshotContext(
                contextSource,
                cancellationToken);
            var activeSkills = SnapshotSkills(
                skillSource,
                cancellationToken);
            var transcript = SnapshotTranscript(
                transcriptSource,
                cancellationToken);
            var measurement = MeasureSnapshot(
                clonedRun,
                context,
                activeSkills,
                transcript,
                workloadClass,
                laneId,
                finalOutputContract);
            if (batchSnapshotBytes + measurement.Utf8Bytes
                > _options.MaxBatchSnapshotUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_bytes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its byte budget.");
            }

            if (batchSnapshotNodes + measurement.Nodes
                > _options.MaxBatchSnapshotJsonNodes)
            {
                throw new RuntimeContentLimitException(
                    nameof(batch),
                    "multi_actor_batch_snapshot_nodes_exceeded",
                    "The aggregate multi-actor snapshot exceeds its node budget.");
            }

            batchSnapshotBytes += measurement.Utf8Bytes;
            batchSnapshotNodes += measurement.Nodes;
            prepared.Add(
                new PreparedRun(
                    inputIndex,
                    new DurableRunRequest
                    {
                        Run = clonedRun,
                        Context = context,
                        ActiveSkills = activeSkills,
                        InitialTranscript = transcript,
                        LaneId = laneId,
                        WorkloadClass = workloadClass,
                        FinalOutputContract = finalOutputContract
                    },
                    agentId,
                    decisionKey));
        }

        if (batch.AggregateBudget is not null)
        {
            budgetReservation = new MultiActorBatchBudgetReservation(
                batch.AggregateBudget,
                reservedTokens,
                reservedActions,
                reservedDurationMs,
                reservedCost);
        }

        return new ReadOnlyCollection<PreparedRun>(prepared);
    }

    private static void ReserveBatchBudget(
        MultiActorBatchBudget aggregate,
        AgentBudget participant,
        ref long reservedTokens,
        ref long reservedActions,
        ref long reservedDurationMs,
        ref decimal reservedCost)
    {
        if (participant.MaxTokens > aggregate.MaxTokens - reservedTokens)
        {
            throw BatchBudgetExceeded(
                "multi_actor_batch_token_budget_exceeded",
                "token");
        }

        if (participant.MaxActions > aggregate.MaxActions - reservedActions)
        {
            throw BatchBudgetExceeded(
                "multi_actor_batch_action_budget_exceeded",
                "action");
        }

        if (participant.MaxDurationMs
            > aggregate.MaxDurationMs - reservedDurationMs)
        {
            throw BatchBudgetExceeded(
                "multi_actor_batch_duration_budget_exceeded",
                "duration");
        }

        if (!MultiActorBatchBudget.TryParseCost(
                participant.MaxCostUsd,
                out var participantCost)
            || participantCost > aggregate.MaxCost - reservedCost)
        {
            throw BatchBudgetExceeded(
                "multi_actor_batch_cost_budget_exceeded",
                "cost");
        }

        reservedTokens += participant.MaxTokens;
        reservedActions += participant.MaxActions;
        reservedDurationMs += participant.MaxDurationMs;
        reservedCost += participantCost;
    }

    private static RuntimeContentLimitException BatchBudgetExceeded(
        string code,
        string dimension)
    {
        return new RuntimeContentLimitException(
            "batch",
            code,
            $"The aggregate multi-actor {dimension} reservation was exceeded.");
    }

    private async Task NotifyBatchAbortedAsync(
        string batchId,
        string reasonCode)
    {
        if (_lifecycle is null)
        {
            return;
        }

        if (!await _abortNotificationSlots.WaitAsync(0).ConfigureAwait(false))
        {
            throw new MultiActorBatchAbortUncertainException(
                batchId,
                reasonCode,
                "No bounded abort-notification capacity remains; host reconciliation is required.");
        }

        var cancellation = new CancellationTokenSource(
            _options.BatchAbortSettlementTimeout);
        Task notification;
        try
        {
            notification = Task.Run(
                async () => await _lifecycle
                    .BatchAbortedAsync(
                        batchId,
                        reasonCode,
                        cancellation.Token)
                    .ConfigureAwait(false));
        }
        catch
        {
            cancellation.Dispose();
            _abortNotificationSlots.Release();
            throw;
        }

        var deadline = Task.Delay(_options.BatchAbortSettlementTimeout);
        var completed = await Task.WhenAny(notification, deadline)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, notification))
        {
            try
            {
                await notification.ConfigureAwait(false);
            }
            finally
            {
                cancellation.Dispose();
                _abortNotificationSlots.Release();
            }

            return;
        }

        _ = ObserveDetachedAbortNotificationAsync(
            notification,
            cancellation,
            _abortNotificationSlots);
        throw new MultiActorBatchAbortUncertainException(
            batchId,
            reasonCode,
            "The host did not confirm batch abort before the settlement deadline; reconciliation is required.");
    }

    private static async Task ObserveDetachedLifecycleNotificationAsync(
        Task notification,
        CancellationTokenSource cancellation,
        SemaphoreSlim slots)
    {
        try
        {
            await notification.ConfigureAwait(false);
        }
        catch
        {
            // The caller already received an explicit uncertain result.
        }
        finally
        {
            cancellation.Dispose();
            slots.Release();
        }
    }

    private static void ParticipantFromDurableRun(
        string batchId,
        MultiActorBatchParticipant participant,
        AgentRun run)
    {
        if (run is null)
        {
            throw new InvalidOperationException(
                "The resumed participant did not return a run.");
        }

        if (!string.Equals(
                run.RunId,
                participant.RunId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The resumed participant changed its run identity.");
        }

        if (!string.Equals(run.BatchId, batchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The resumed participant changed its batch identity.");
        }

        if (run.Extensions is null
            || !run.Extensions.TryGetValue(
                ParticipantInputIndexExtension,
                out var inputIndexElement)
            || inputIndexElement.ValueKind != JsonValueKind.Number
            || !inputIndexElement.TryGetInt32(out var inputIndex)
            || inputIndex is < 0 or >= 16_384)
        {
            throw new InvalidOperationException(
                "The resumed participant lacks valid durable batch metadata.");
        }

        if (run.DecisionKey is null)
        {
            throw new InvalidOperationException(
                "The resumed participant lacks a decision key.");
        }

        if (inputIndex != participant.InputIndex
            || !string.Equals(
                run.AgentId,
                participant.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                run.DecisionKey,
                participant.DecisionKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The resumed participant changed its durable identity.");
        }
    }

    private static DurableRunResumeGuard ResumeGuard(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunSemanticExpectation? semanticExpectation = null)
    {
        return new DurableRunResumeGuard
        {
            ExpectedBatchId = batchId,
            ExpectedAgentId = participant.AgentId,
            ExpectedDecisionKey = participant.DecisionKey,
            RequiredInt32ExtensionName =
                ParticipantInputIndexExtension,
            MinimumInt32ExtensionValue = 0,
            MaximumInt32ExtensionValue = 16_383,
            ExpectedInt32ExtensionValue = participant.InputIndex,
            SemanticExtensionName = semanticExpectation?.ExtensionName,
            ExpectedSemanticExtensionSha256 =
                semanticExpectation?.ExpectedSha256
        };
    }

    private static string ParticipantOperationKey(string runId)
    {
        return runId;
    }

    private void EnsureGuardedResumeSupported()
    {
        if (_runtime is not IGuardedDurableAgentRuntime)
        {
            throw new DurableRunResumeGuardException(
                DurableRunResumeGuardReasonCodes.NotSupported);
        }
    }

    private IDisposable EnterBatchOperation(
        string batchId,
        int participantCount)
    {
        if (!_activeBatchOperations.TryAdd(batchId, 0))
        {
            throw new MultiActorBatchAlreadyActiveException(batchId);
        }

        if (!_activeBatchSlots.Wait(0))
        {
            _activeBatchOperations.TryRemove(batchId, out _);
            throw new MultiActorBatchCapacityExceededException(
                _options.MaxConcurrentBatches);
        }

        try
        {
            ReserveQueuedParticipants(participantCount);
            return new ActiveBatchOperation(
                this,
                batchId,
                participantCount);
        }
        catch
        {
            _activeBatchSlots.Release();
            _activeBatchOperations.TryRemove(batchId, out _);
            throw;
        }
    }

    private void ReserveQueuedParticipants(int participantCount)
    {
        while (true)
        {
            var current = Volatile.Read(ref _queuedParticipantCount);
            var next = checked(current + participantCount);
            if (next > _options.MaxQueuedParticipants)
            {
                throw new
                    MultiActorQueuedParticipantCapacityExceededException(
                        _options.MaxQueuedParticipants);
            }

            if (Interlocked.CompareExchange(
                    ref _queuedParticipantCount,
                    next,
                    current) == current)
            {
                return;
            }
        }
    }

    private void ExitBatchOperation(
        string batchId,
        int participantCount)
    {
        var remaining = Interlocked.Add(
            ref _queuedParticipantCount,
            -participantCount);
        System.Diagnostics.Debug.Assert(
            remaining >= 0,
            "The multi-actor queued-participant count became invalid.");
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _queuedParticipantCount, 0);
        }

        _activeBatchSlots.Release();
        _activeBatchOperations.TryRemove(batchId, out _);
    }

    private static string? SnapshotLaneId(
        string? laneId,
        string parameterName)
    {
        return string.IsNullOrWhiteSpace(laneId)
            ? laneId
            : RuntimeGuard.RequiredUtf8(
                laneId,
                256,
                parameterName);
    }

    internal static string RequiredDecisionKey(
        string value,
        string parameterName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException(
                "A decision key is required.",
                parameterName);
        }

        var scalars = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException(
                        "A decision key must contain valid Unicode.",
                        parameterName);
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new ArgumentException(
                    "A decision key must contain valid Unicode.",
                    parameterName);
            }

            scalars++;
            if (scalars > 256)
            {
                throw new ArgumentException(
                    "A decision key cannot exceed 256 Unicode scalar values.",
                    parameterName);
            }
        }

        if (Encoding.UTF8.GetByteCount(value) > 1_024)
        {
            throw new ArgumentException(
                "A decision key cannot exceed 1024 UTF-8 bytes.",
                parameterName);
        }

        return value;
    }

    private static async Task ObserveDetachedAbortNotificationAsync(
        Task notification,
        CancellationTokenSource cancellation,
        SemaphoreSlim slot)
    {
        try
        {
            await notification.ConfigureAwait(false);
        }
        catch
        {
            // The caller already received an uncertain-abort result.
        }
        finally
        {
            cancellation.Dispose();
            slot.Release();
        }
    }

    private sealed class ActiveBatchOperation : IDisposable
    {
        private readonly MultiActorDecisionCoordinator _owner;
        private readonly int _participantCount;
        private string? _batchId;

        public ActiveBatchOperation(
            MultiActorDecisionCoordinator owner,
            string batchId,
            int participantCount)
        {
            _owner = owner;
            _batchId = batchId;
            _participantCount = participantCount;
        }

        public void Dispose()
        {
            var batchId = Interlocked.Exchange(ref _batchId, null);
            if (batchId is not null)
            {
                _owner.ExitBatchOperation(
                    batchId,
                    _participantCount);
            }
        }
    }

    private static T[] SnapshotInputReferences<T>(
        IReadOnlyList<T>? source,
        int maximumItems,
        string limitCode,
        string missingMessage,
        string nullItemMessage,
        CancellationToken cancellationToken)
        where T : class
    {
        if (source is null)
        {
            throw new ArgumentException(missingMessage, nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var count = source.Count;
        if (count > maximumItems)
        {
            throw new RuntimeContentLimitException(
                nameof(source),
                limitCode,
                $"The input collection exceeds {maximumItems} items.");
        }

        var snapshot = new T[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? item;
            try
            {
                item = source[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new InvalidDataException(
                    "The input collection changed while it was being "
                    + "snapshotted.",
                    exception);
            }

            snapshot[index] = item
                              ?? throw new ArgumentException(
                                  nullItemMessage,
                                  nameof(source));
        }

        return snapshot;
    }

    private void PreflightSnapshot(
        DurableRunRequest request,
        int maximumUtf8Bytes,
        int maximumJsonNodes,
        CancellationToken cancellationToken)
    {
        var budget = new SnapshotPreflightBudget(
            maximumUtf8Bytes,
            maximumJsonNodes,
            Math.Max(
                4_096,
                Math.Max(
                    _options.MaxTranscriptMessagesPerRun,
                    _options.MaxContextCandidatesPerRun)));
        var run = request.Run;
        budget.AddString(run.ProtocolVersion);
        budget.AddString(run.SchemaVersion);
        budget.AddString(run.RunId);
        budget.AddString(run.AgentId);
        budget.AddString(run.WorldId);
        budget.AddString(run.SessionId);
        budget.AddString(run.DecisionKey);
        budget.AddString(run.BatchId);
        budget.AddString(request.LaneId);
        if (request.FinalOutputContract is not null)
        {
            budget.AddJson(request.FinalOutputContract.ToJson());
        }

        foreach (var extension in run.Extensions
                 ?? throw new ArgumentException(
                     "A batch run requires an extension collection.",
                     nameof(request)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.AddString(extension.Key);
            budget.AddJson(extension.Value);
        }

        var context = request.Context
                      ?? throw new ArgumentException(
                          "A batch run requires a context collection.",
                          nameof(request));
        if (context.Count > _options.MaxContextCandidatesPerRun)
        {
            throw new RuntimeContentLimitException(
                nameof(request),
                "multi_actor_context_count_exceeded",
                "The context collection exceeds its item budget.");
        }

        foreach (var item in context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null)
            {
                throw new ArgumentException(
                    "Context collections cannot contain null entries.",
                    nameof(request));
            }

            budget.ChargeStructure(12);
            budget.AddString(item.Id);
            budget.AddString(item.Category);
            budget.AddString(item.Provenance);
            if (item.Content.HasValue)
            {
                budget.AddJson(item.Content.Value);
            }
            else if (item.Resource is not null)
            {
                budget.AddString(item.Resource.Uri);
                budget.AddString(item.Resource.MediaType);
                budget.AddString(item.Resource.Digest);
            }
        }

        var skills = request.ActiveSkills
                     ?? throw new ArgumentException(
                         "A batch run requires an active-skill collection.",
                         nameof(request));
        if (skills.Count > _options.MaxActiveSkillsPerRun)
        {
            throw new RuntimeContentLimitException(
                nameof(request),
                "multi_actor_active_skill_count_exceeded",
                "The active-skill collection exceeds its item budget.");
        }

        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skill is null)
            {
                throw new ArgumentException(
                    "Active-skill collections cannot contain null entries.",
                    nameof(request));
            }

            budget.ChargeStructure(4);
            budget.AddString(skill.SkillId);
            budget.AddString(skill.Version);
        }

        var transcript = request.InitialTranscript
                         ?? throw new ArgumentException(
                             "A batch run requires a transcript collection.",
                             nameof(request));
        if (transcript.Count > _options.MaxTranscriptMessagesPerRun)
        {
            throw new RuntimeContentLimitException(
                nameof(request),
                "multi_actor_transcript_count_exceeded",
                "The transcript collection exceeds its item budget.");
        }

        foreach (var message in transcript)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message is null)
            {
                throw new ArgumentException(
                    "Transcript collections cannot contain null entries.",
                    nameof(request));
            }

            if (message.Parts is null
                || message.Parts.Count is < 1 or > 2_048)
            {
                throw new RuntimeContentLimitException(
                    nameof(request),
                    "multi_actor_transcript_part_count_exceeded",
                    "A transcript message has an invalid number of parts.");
            }

            budget.ChargeStructure(8);
            budget.AddString(message.MessageId);
            budget.AddString(message.Role);
            foreach (var part in message.Parts)
            {
                if (part is null)
                {
                    throw new ArgumentException(
                        "Transcript message parts cannot contain null entries.",
                        nameof(request));
                }

                budget.ChargeStructure(10);
                budget.AddString(part.Type);
                budget.AddString(part.Text);
                budget.AddString(part.ToolCallId);
                budget.AddString(part.ToolName);
                budget.AddString(part.ToolVersion);
                budget.AddString(part.ToolEffect);
                budget.AddString(part.ToolDescriptorDigest);
                if (part.Json.HasValue)
                {
                    budget.AddJson(part.Json.Value);
                }
            }
        }
    }

    private GameContextCoordinate ParticipantCoordinate(
        AgentRun run,
        GameContextCoordinate shared,
        int participantCount)
    {
        var existing = GameContextEnvelope.ValidateForRun(
            run,
            nameof(run));
        if (existing is not null)
        {
            EnsureSharedCoordinateMatches(existing, shared);
        }

        var observer = existing?.Observer;
        if (participantCount == 1 && observer is null)
        {
            observer = shared.Observer;
        }

        return new GameContextCoordinate(
            shared.WorldId,
            shared.TimelineId,
            shared.SaveRevision,
            observer,
            existing?.SceneId ?? shared.SceneId,
            existing?.RegionId ?? shared.RegionId,
            shared.StateVersion,
            shared.GameTime,
            shared.Causality,
            shared.SessionId);
    }

    private static void EnsureSharedCoordinateMatches(
        GameContextCoordinate participant,
        GameContextCoordinate shared)
    {
        if (!string.Equals(
                participant.WorldId,
                shared.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                participant.TimelineId,
                shared.TimelineId,
                StringComparison.Ordinal)
            || !string.Equals(
                participant.SessionId,
                shared.SessionId,
                StringComparison.Ordinal)
            || participant.SaveRevision != shared.SaveRevision
            || participant.StateVersion is not null
            && !string.Equals(
                participant.StateVersion,
                shared.StateVersion,
                StringComparison.Ordinal)
            || participant.GameTime is not null
            && !SameGameTime(participant.GameTime, shared.GameTime)
            || participant.Causality is not null
            && !SameCausality(participant.Causality, shared.Causality))
        {
            throw new ArgumentException(
                "A participant game context contradicts the shared snapshot.",
                nameof(participant));
        }
    }

    private static bool SameGameTime(
        GameTimePoint left,
        GameTimePoint? right)
    {
        return right is not null
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

    private static bool SameCausality(
        GameCausalityStamp left,
        GameCausalityStamp? right)
    {
        return right is not null
               && string.Equals(
                   left.EventId,
                   right.EventId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.BasedOnStateVersion,
                   right.BasedOnStateVersion,
                   StringComparison.Ordinal)
               && left.ParentEventIds.SequenceEqual(
                   right.ParentEventIds,
                   StringComparer.Ordinal);
    }

    private JsonValueMeasurement MeasureSnapshot(
        AgentRun run,
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills,
        IReadOnlyList<NormalizedMessage> transcript,
        string workloadClass,
        string? laneId,
        FinalOutputContract? finalOutputContract)
    {
        var envelope = JsonArrayBuilder.Object(
            ("run", ProtocolJson.ToElement(run)),
            ("runInput", DurableRunInputJournalCodec.Encode(
                context,
                activeSkills,
                workloadClass)),
            ("transcript", JsonArrayBuilder.Array(
                transcript.Select(NormalizedMessageJournalCodec.Encode))),
            ("laneId", laneId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(laneId)),
            ("finalOutputContract", finalOutputContract is null
                ? JsonArrayBuilder.Null()
                : finalOutputContract.ToJson()));
        return JsonValueInspector.ValidateAndMeasureDetailed(
            envelope,
            new JsonValueLimits(
                maxUtf8Bytes: _options.MaxSnapshotUtf8BytesPerRun,
                maxDepth: 64,
                maxNodes: _options.MaxSnapshotJsonNodesPerRun,
                maxStringUtf8Bytes:
                    _options.MaxSnapshotUtf8BytesPerRun,
                maxContainerItems: Math.Max(
                    4_096,
                    Math.Max(
                        _options.MaxTranscriptMessagesPerRun,
                        Math.Max(
                            _options.MaxContextCandidatesPerRun,
                            _options.MaxActiveSkillsPerRun)))),
            nameof(run));
    }

    private IReadOnlyList<ContextCandidate> SnapshotContext(
        IReadOnlyList<ContextCandidate>? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A batch run requires a context collection.",
                nameof(source));
        }

        return RuntimeInputGuard.CopyBounded(
            source,
            _options.MaxContextCandidatesPerRun,
            item => item is null
                ? throw new ArgumentException(
                    "Context collections cannot contain null entries.",
                    nameof(source))
                : item.Clone(),
            nameof(source),
            "multi_actor_context_count_exceeded",
            cancellationToken);
    }

    private IReadOnlyList<SkillReference> SnapshotSkills(
        IReadOnlyList<SkillReference>? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A batch run requires an active-skill collection.",
                nameof(source));
        }

        return RuntimeInputGuard.CopyBounded(
            source,
            _options.MaxActiveSkillsPerRun,
            item => item is null
                ? throw new ArgumentException(
                    "Active-skill collections cannot contain null entries.",
                    nameof(source))
                : new SkillReference(item.SkillId, item.Version),
            nameof(source),
            "multi_actor_active_skill_count_exceeded",
            cancellationToken);
    }

    private IReadOnlyList<NormalizedMessage> SnapshotTranscript(
        IReadOnlyList<NormalizedMessage>? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A batch run requires a transcript collection.",
                nameof(source));
        }

        return RuntimeInputGuard.CopyBounded(
            source,
            _options.MaxTranscriptMessagesPerRun,
            item => item is null
                ? throw new ArgumentException(
                    "Transcript collections cannot contain null entries.",
                    nameof(source))
                : NormalizedMessageJournalCodec.Decode(
                    NormalizedMessageJournalCodec.Encode(item)),
            nameof(source),
            "multi_actor_transcript_count_exceeded",
            cancellationToken);
    }

    private sealed class PreparedRun
    {
        public PreparedRun(
            int inputIndex,
            DurableRunRequest request,
            string agentId,
            string decisionKey)
        {
            InputIndex = inputIndex;
            Request = request;
            AgentId = agentId;
            DecisionKey = decisionKey;
        }

        public int InputIndex { get; }

        public DurableRunRequest Request { get; }

        public string AgentId { get; }

        public string DecisionKey { get; }
    }

    private sealed class SnapshotPreflightBudget
    {
        private readonly int _maximumUtf8Bytes;
        private readonly int _maximumJsonNodes;
        private readonly int _maximumContainerItems;
        private int _utf8Bytes = 512;
        private int _jsonNodes = 1;

        public SnapshotPreflightBudget(
            int maximumUtf8Bytes,
            int maximumJsonNodes,
            int maximumContainerItems)
        {
            _maximumUtf8Bytes = maximumUtf8Bytes;
            _maximumJsonNodes = maximumJsonNodes;
            _maximumContainerItems = maximumContainerItems;
            EnsureWithinLimits();
        }

        public void AddString(string? value)
        {
            if (value is null)
            {
                return;
            }

            var remaining = _maximumUtf8Bytes - _utf8Bytes;
            if (remaining <= 2 || value.Length > remaining - 2)
            {
                ThrowBytes();
            }

            var rawBytes = Encoding.UTF8.GetByteCount(value);
            if (rawBytes > remaining - 2)
            {
                ThrowBytes();
            }

            var encodedBytes = JsonEncodedText
                .Encode(value)
                .EncodedUtf8Bytes
                .Length;
            _utf8Bytes = checked(_utf8Bytes + encodedBytes + 2);
            EnsureWithinLimits();
        }

        public void AddJson(JsonElement value)
        {
            var remainingBytes = _maximumUtf8Bytes - _utf8Bytes;
            var remainingNodes = _maximumJsonNodes - _jsonNodes;
            if (remainingBytes < 1)
            {
                ThrowBytes();
            }

            if (remainingNodes < 1)
            {
                ThrowNodes();
            }

            var measurement = JsonValueInspector.ValidateAndMeasureDetailed(
                value,
                new JsonValueLimits(
                    maxUtf8Bytes: remainingBytes,
                    maxDepth: 64,
                    maxNodes: remainingNodes,
                    maxStringUtf8Bytes: remainingBytes,
                    maxContainerItems: _maximumContainerItems),
                "multiActorSnapshot");
            _utf8Bytes = checked(_utf8Bytes + measurement.Utf8Bytes);
            _jsonNodes = checked(_jsonNodes + measurement.Nodes);
            EnsureWithinLimits();
        }

        public void ChargeStructure(int nodes)
        {
            _utf8Bytes = checked(_utf8Bytes + nodes * 8);
            _jsonNodes = checked(_jsonNodes + nodes);
            EnsureWithinLimits();
        }

        private void EnsureWithinLimits()
        {
            if (_utf8Bytes > _maximumUtf8Bytes)
            {
                ThrowBytes();
            }

            if (_jsonNodes > _maximumJsonNodes)
            {
                ThrowNodes();
            }
        }

        private static void ThrowBytes()
        {
            throw new RuntimeContentLimitException(
                "request",
                "multi_actor_snapshot_bytes_exceeded",
                "A multi-actor run snapshot exceeds its byte budget.");
        }

        private static void ThrowNodes()
        {
            throw new RuntimeContentLimitException(
                "request",
                "multi_actor_snapshot_nodes_exceeded",
                "A multi-actor run snapshot exceeds its JSON-node budget.");
        }
    }
}
