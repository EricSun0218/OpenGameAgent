using System.Collections.ObjectModel;

namespace GameAgent.World;

public sealed class WorldTransactionalEventEffectContext
{
    internal WorldTransactionalEventEffectContext(
        WorldEventInstance instance,
        IWorldAuthoritativeTransaction transaction,
        object? hostContext)
    {
        Instance = instance;
        Transaction = transaction;
        HostContext = hostContext;
    }

    public WorldEventInstance Instance { get; }

    public IWorldAuthoritativeTransaction Transaction { get; }

    public WorldAuthoritativeStateSnapshot Source => Transaction.Source;

    public IWorldStateDraft Draft => Transaction.Draft;

    public object? HostContext { get; }
}

/// <summary>
/// A transaction-local effect. Implementations may change only the supplied
/// draft and return a typed result; external side effects require a separate
/// durable outbox protocol owned by the host.
/// </summary>
public interface IWorldTransactionalEventEffect
{
    ValueTask<WorldEventEffectResult> ApplyAsync(
        WorldTransactionalEventEffectContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional admission metadata supplied by a typed effect.
/// </summary>
public interface IWorldTransactionalEffectAdmission
{
    string CommandId { get; }

    string OperationId { get; }

    string PayloadDigest { get; }

    IReadOnlyList<WorldEntityIncarnationExpectation>
        ExpectedIncarnations
    { get; }
}

/// <summary>
/// Signals that an effect implementation cannot prove whether an external
/// operation settled. The executor leaves ownership pending for explicit
/// reconciliation and never invokes the effect again automatically.
/// </summary>
public sealed class WorldEffectOutcomeUnknownException : Exception
{
    public WorldEffectOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public WorldEffectOutcomeUnknownException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WorldEventTransactionExecutionRequest
{
    public WorldEventTransactionExecutionRequest(
        WorldEventInstance instance,
        WorldAuthoritativeCoordinate expectedCoordinate,
        string commandId,
        string operationId,
        IWorldTransactionalEventEffect effect,
        IReadOnlyList<WorldEntityIncarnationExpectation>?
            additionalIncarnationExpectations = null,
        object? hostContext = null)
    {
        Instance = instance
                   ?? throw new ArgumentNullException(nameof(instance));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        CommandId = WorldValidation.Required(
            commandId,
            nameof(commandId));
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId));
        Effect = effect ?? throw new ArgumentNullException(nameof(effect));
        if (!string.Equals(
                instance.WorldId,
                expectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                instance.TimelineId,
                expectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || instance.TimelineEpoch != expectedCoordinate.TimelineEpoch)
        {
            throw new ArgumentException(
                "The event instance must use the expected coordinate.",
                nameof(expectedCoordinate));
        }

        var expectations = new List<WorldEntityIncarnationExpectation>();
        AddExpectations(
            expectations,
            instance.Participants.Select(
                participant => new WorldEntityIncarnationExpectation(
                    participant.EntityId,
                    participant.Incarnation)),
            nameof(instance));
        if (additionalIncarnationExpectations is not null)
        {
            AddExpectations(
                expectations,
                additionalIncarnationExpectations,
                nameof(additionalIncarnationExpectations));
        }

        var payloadDigest = instance.PlanFingerprint;
        if (effect is IWorldTransactionalEffectAdmission admission)
        {
            if (!string.Equals(
                    admission.CommandId,
                    CommandId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    admission.OperationId,
                    OperationId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Effect admission identifiers must match the command.",
                    nameof(effect));
            }

            payloadDigest = admission.PayloadDigest;
            AddExpectations(
                expectations,
                admission.ExpectedIncarnations,
                nameof(effect));
        }

        ExpectedIncarnations = MergeExpectations(expectations);
        HostContext = hostContext;
        TransactionRequest = new WorldTransactionRequest(
            OperationId,
            CommandId,
            payloadDigest,
            ExpectedCoordinate,
            ExpectedIncarnations,
            WorldEventHistoryRecord.FromInstance(instance));
    }

    public WorldEventInstance Instance { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string CommandId { get; }

    public string OperationId { get; }

    public IWorldTransactionalEventEffect Effect { get; }

    public IReadOnlyList<WorldEntityIncarnationExpectation>
        ExpectedIncarnations
    { get; }

    public object? HostContext { get; }

    public WorldTransactionRequest TransactionRequest { get; }

    private static IReadOnlyList<WorldEntityIncarnationExpectation>
        MergeExpectations(
            IEnumerable<WorldEntityIncarnationExpectation> values)
    {
        var merged = new SortedDictionary<string, long>(
            StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    "Incarnation expectations cannot contain null entries.",
                    nameof(values));
            }

            if (merged.TryGetValue(value.EntityId, out var existing)
                && existing != value.Incarnation)
            {
                throw new ArgumentException(
                    "One entity cannot require conflicting incarnations.",
                    nameof(values));
            }

            merged[value.EntityId] = value.Incarnation;
        }

        return new ReadOnlyCollection<WorldEntityIncarnationExpectation>(
            merged.Select(
                    pair => new WorldEntityIncarnationExpectation(
                        pair.Key,
                        pair.Value))
                .ToArray());
    }

    private static void AddExpectations(
        ICollection<WorldEntityIncarnationExpectation> destination,
        IEnumerable<WorldEntityIncarnationExpectation> values,
        string parameterName)
    {
        var remaining =
            WorldValidation.MaximumParticipants - destination.Count;
        var bounded = WorldValidation.MaterializeBounded(
            values,
            remaining,
            parameterName);
        foreach (var value in bounded)
        {
            destination.Add(value);
        }
    }
}

public enum WorldTransactionExecutionStatus
{
    Committed = 0,
    Replayed = 1,
    Rejected = 2,
    Cancelled = 3,
    Busy = 4,
    ReconciliationRequired = 5,
    IdempotencyConflict = 6,
    NotFound = 7
}

public sealed class WorldTransactionExecutionResult
{
    internal WorldTransactionExecutionResult(
        WorldTransactionExecutionStatus status,
        string reasonCode,
        WorldCommandReceipt? receipt)
    {
        Status = status;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        if (receipt is not null
            && (!ReceiptStatusMatches(status, receipt.Status)
                || !string.Equals(
                    ReasonCode,
                    receipt.OutcomeCode,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The transaction execution status does not match its "
                + "terminal receipt.");
        }

        Receipt = receipt;
    }

    public WorldTransactionExecutionStatus Status { get; }

    public string ReasonCode { get; }

    public WorldCommandReceipt? Receipt { get; }

    public bool IsTerminal => Receipt is not null;

    private static bool ReceiptStatusMatches(
        WorldTransactionExecutionStatus execution,
        WorldCommandReceiptStatus receipt)
    {
        return receipt switch
        {
            WorldCommandReceiptStatus.Applied =>
                execution
                is WorldTransactionExecutionStatus.Committed
                    or WorldTransactionExecutionStatus.Replayed,
            WorldCommandReceiptStatus.Rejected =>
                execution == WorldTransactionExecutionStatus.Rejected,
            WorldCommandReceiptStatus.Cancelled =>
                execution == WorldTransactionExecutionStatus.Cancelled,
            _ => false
        };
    }
}

/// <summary>
/// Coordinates a prepared event effect with one authoritative store commit.
/// It never calls an effect after the operation has become pending-unknown.
/// </summary>
public sealed class WorldEventTransactionExecutor
{
    private readonly IWorldAuthoritativeTransactionStore _store;

    public WorldEventTransactionExecutor(
        IWorldAuthoritativeTransactionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<WorldTransactionExecutionResult> ExecuteAsync(
        WorldEventTransactionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        WorldTransactionBeginResult begin;
        try
        {
            begin = await _store.BeginAsync(
                    request.TransactionRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return await ReconcileAfterUncertainCallAsync(
                    request.TransactionRequest)
                .ConfigureAwait(false);
        }
        catch
        {
            return await ReconcileAfterUncertainCallAsync(
                    request.TransactionRequest)
                .ConfigureAwait(false);
        }

        if (begin.Status != WorldTransactionBeginStatus.Acquired)
        {
            return MapBegin(begin, request.TransactionRequest);
        }

        var transaction = begin.Transaction
                          ?? throw new InvalidOperationException(
                              "An acquired result requires a transaction.");
        EnsureRequestMatches(
            request.TransactionRequest,
            transaction.Request);
        await using (transaction.ConfigureAwait(false))
        {
            WorldEventEffectResult effectResult;
            try
            {
                effectResult = await request.Effect
                    .ApplyAsync(
                        new WorldTransactionalEventEffectContext(
                            request.Instance,
                            transaction,
                            request.HostContext),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (effectResult is null)
                {
                    return await CompleteWithoutMutationAsync(
                            transaction,
                            WorldCommandReceiptStatus.Rejected,
                            WorldTransactionReasonCodes.EffectFailed,
                            null)
                        .ConfigureAwait(false);
                }
            }
            catch (WorldEffectOutcomeUnknownException)
            {
                return NonTerminal(
                    WorldTransactionExecutionStatus
                        .ReconciliationRequired,
                    WorldTransactionReasonCodes.ReconciliationRequired);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return await CompleteWithoutMutationAsync(
                        transaction,
                        WorldCommandReceiptStatus.Cancelled,
                        WorldTransactionReasonCodes.Cancelled,
                        null)
                    .ConfigureAwait(false);
            }
            catch
            {
                return await CompleteWithoutMutationAsync(
                        transaction,
                        WorldCommandReceiptStatus.Rejected,
                        WorldTransactionReasonCodes.EffectFailed,
                        null)
                    .ConfigureAwait(false);
            }

            var effect = new WorldEffectReceipt(
                effectResult.Applied,
                effectResult.OutcomeCode,
                effectResult.TypedResult);
            if (!effect.Applied)
            {
                return await CompleteWithoutMutationAsync(
                        transaction,
                        WorldCommandReceiptStatus.Rejected,
                        effect.OutcomeCode,
                        effect)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await CompleteWithoutMutationAsync(
                        transaction,
                        WorldCommandReceiptStatus.Cancelled,
                        WorldTransactionReasonCodes.Cancelled,
                        null)
                    .ConfigureAwait(false);
            }

            try
            {
                var commit = await transaction.CommitEventAsync(
                        effect,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (commit.Status == WorldTransactionCommitStatus.Committed)
                {
                    return FromReceipt(
                        WorldTransactionExecutionStatus.Committed,
                        commit.Receipt!,
                        request.TransactionRequest);
                }

                return await ReconcileAfterUncertainCallAsync(
                        request.TransactionRequest)
                    .ConfigureAwait(false);
            }
            catch
            {
                return await ReconcileAfterUncertainCallAsync(
                        request.TransactionRequest)
                    .ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<WorldTransactionExecutionResult> ReconcileAsync(
        WorldTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var result = await _store.ReconcileAsync(
                request.ExpectedCoordinate.Scope,
                request.OperationId,
                request.RequestFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        return MapReconciliation(result, request);
    }

    public async ValueTask<WorldTransactionExecutionResult>
        CancelPendingAsync(
            WorldTransactionRequest request,
            string outcomeCode,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var result = await _store.CancelPendingAsync(
                request.ExpectedCoordinate.Scope,
                request.OperationId,
                request.RequestFingerprint,
                outcomeCode,
                cancellationToken)
            .ConfigureAwait(false);
        return MapReconciliation(result, request);
    }

    private async ValueTask<WorldTransactionExecutionResult>
        CompleteWithoutMutationAsync(
            IWorldAuthoritativeTransaction transaction,
            WorldCommandReceiptStatus status,
            string outcomeCode,
            WorldEffectReceipt? effect)
    {
        try
        {
            var completion = await transaction
                .CompleteWithoutMutationAsync(
                    status,
                    outcomeCode,
                    effect,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (completion.Status
                == WorldTransactionCommitStatus.Committed)
            {
                return FromReceipt(
                    status == WorldCommandReceiptStatus.Cancelled
                        ? WorldTransactionExecutionStatus.Cancelled
                        : WorldTransactionExecutionStatus.Rejected,
                    completion.Receipt!,
                    transaction.Request);
            }
        }
        catch
        {
            // Reconciliation below determines whether the terminal receipt
            // became durable before acknowledgement was lost.
        }

        return await ReconcileAfterUncertainCallAsync(transaction.Request)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorldTransactionExecutionResult>
        ReconcileAfterUncertainCallAsync(WorldTransactionRequest request)
    {
        try
        {
            var reconciliation = await _store.ReconcileAsync(
                    request.ExpectedCoordinate.Scope,
                    request.OperationId,
                    request.RequestFingerprint,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return MapReconciliation(reconciliation, request);
        }
        catch
        {
            return NonTerminal(
                WorldTransactionExecutionStatus.ReconciliationRequired,
                WorldTransactionReasonCodes.ReconciliationRequired);
        }
    }

    private static WorldTransactionExecutionResult MapBegin(
        WorldTransactionBeginResult begin,
        WorldTransactionRequest request)
    {
        return begin.Status switch
        {
            WorldTransactionBeginStatus.TerminalReceipt =>
                MapTerminalReceipt(
                    begin.Receipt!,
                    request,
                    replayApplied: true),
            WorldTransactionBeginStatus.Busy => NonTerminal(
                WorldTransactionExecutionStatus.Busy,
                begin.ReasonCode),
            WorldTransactionBeginStatus.ReconciliationRequired =>
                NonTerminal(
                    WorldTransactionExecutionStatus
                        .ReconciliationRequired,
                    begin.ReasonCode),
            WorldTransactionBeginStatus.IdempotencyConflict =>
                NonTerminal(
                    WorldTransactionExecutionStatus.IdempotencyConflict,
                    begin.ReasonCode),
            _ => throw new InvalidOperationException(
                "The begin result is internally inconsistent.")
        };
    }

    private static WorldTransactionExecutionResult MapReconciliation(
        WorldTransactionReconciliationResult result,
        WorldTransactionRequest request)
    {
        return result.Status switch
        {
            WorldTransactionReconciliationStatus.TerminalReceipt =>
                MapTerminalReceipt(
                    result.Receipt!,
                    request,
                    replayApplied: true),
            WorldTransactionReconciliationStatus.Pending => NonTerminal(
                WorldTransactionExecutionStatus.ReconciliationRequired,
                result.ReasonCode),
            WorldTransactionReconciliationStatus.NotFound => NonTerminal(
                WorldTransactionExecutionStatus.NotFound,
                result.ReasonCode),
            WorldTransactionReconciliationStatus.IdempotencyConflict =>
                NonTerminal(
                    WorldTransactionExecutionStatus.IdempotencyConflict,
                    result.ReasonCode),
            _ => throw new InvalidOperationException(
                "The reconciliation result is internally inconsistent.")
        };
    }

    private static WorldTransactionExecutionResult MapTerminalReceipt(
        WorldCommandReceipt receipt,
        WorldTransactionRequest request,
        bool replayApplied)
    {
        var status = receipt.Status switch
        {
            WorldCommandReceiptStatus.Applied => replayApplied
                ? WorldTransactionExecutionStatus.Replayed
                : WorldTransactionExecutionStatus.Committed,
            WorldCommandReceiptStatus.Rejected =>
                WorldTransactionExecutionStatus.Rejected,
            WorldCommandReceiptStatus.Cancelled =>
                WorldTransactionExecutionStatus.Cancelled,
            _ => throw new InvalidOperationException(
                "The receipt status is invalid.")
        };
        return FromReceipt(status, receipt, request);
    }

    private static WorldTransactionExecutionResult FromReceipt(
        WorldTransactionExecutionStatus status,
        WorldCommandReceipt receipt,
        WorldTransactionRequest request)
    {
        EnsureRequestMatches(request, receipt.Request);
        return new WorldTransactionExecutionResult(
            status,
            receipt.OutcomeCode,
            receipt);
    }

    private static void EnsureRequestMatches(
        WorldTransactionRequest expected,
        WorldTransactionRequest actual)
    {
        if (!string.Equals(
                expected.OperationId,
                actual.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.CommandId,
                actual.CommandId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.RequestFingerprint,
                actual.RequestFingerprint,
                StringComparison.Ordinal)
            || !expected.ExpectedCoordinate.IsExactMatch(
                actual.ExpectedCoordinate))
        {
            throw new InvalidDataException(
                "The authoritative store returned evidence for a different "
                + "world transaction request.");
        }
    }

    private static WorldTransactionExecutionResult NonTerminal(
        WorldTransactionExecutionStatus status,
        string reasonCode)
    {
        return new WorldTransactionExecutionResult(
            status,
            reasonCode,
            null);
    }
}
