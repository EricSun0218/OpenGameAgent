using GameAgent.Protocol;

namespace GameAgent.Core;

public interface IAtomicJournalBatchStore
{
    ValueTask<IReadOnlyList<JournalAppendResult>> AppendAtomicBatchAsync(
        IReadOnlyList<RuntimeEvent> runtimeEvents,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default);
}

public interface IDurableSessionStore :
    ISessionStore,
    IAtomicJournalBatchStore,
    IAsyncDisposable
{
    ValueTask<JournalAppendResult> AppendAtomicAsync(
        RuntimeEvent runtimeEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default);

    ValueTask<RunJournalCursor> GetRunCursorAsync(
        string runId,
        CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

public interface IOperationLedger
{
    ValueTask<OperationLedgerEntry?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OperationLedgerEntry>> ReadPendingOperationsAsync(
        string? runId = null,
        CancellationToken cancellationToken = default);

    ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
        RuntimeEvent receiptEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default);
}

public sealed class RunJournalCursor
{
    public RunJournalCursor(string runId, long nextSequence, long revision)
    {
        RunId = runId;
        NextSequence = nextSequence;
        Revision = revision;
    }

    public string RunId { get; }

    public long NextSequence { get; }

    public long Revision { get; }
}

public sealed class JournalAppendResult
{
    public JournalAppendResult(long sequence, long revision, bool wasDuplicate)
    {
        Sequence = sequence;
        Revision = revision;
        WasDuplicate = wasDuplicate;
    }

    public long Sequence { get; }

    public long Revision { get; }

    public bool WasDuplicate { get; }
}

public sealed class OperationLedgerEntry
{
    public OperationLedgerEntry(
        ActionRequest request,
        ActionReceipt? latestReceipt,
        long requestSequence,
        long requestRunRevision,
        long? latestReceiptSequence,
        long? latestReceiptRunRevision)
    {
        Request = request;
        LatestReceipt = latestReceipt;
        RequestSequence = requestSequence;
        RequestRunRevision = requestRunRevision;
        LatestReceiptSequence = latestReceiptSequence;
        LatestReceiptRunRevision = latestReceiptRunRevision;
    }

    public string OperationId => Request.OperationId;

    public string RunId => Request.RunId;

    public ActionRequest Request { get; }

    public ActionReceipt? LatestReceipt { get; }

    public long RequestSequence { get; }

    public long RequestRunRevision { get; }

    public long? LatestReceiptSequence { get; }

    public long? LatestReceiptRunRevision { get; }

    public bool IsPending =>
        LatestReceipt is null
        || string.Equals(
            LatestReceipt.Status,
            ReceiptStatuses.Unknown,
            StringComparison.Ordinal);
}

public sealed class ReceiptReconcileResult
{
    public ReceiptReconcileResult(
        JournalAppendResult append,
        OperationLedgerEntry operation)
    {
        Append = append;
        Operation = operation;
    }

    public JournalAppendResult Append { get; }

    public OperationLedgerEntry Operation { get; }
}

public sealed class RunRevisionConflictException : InvalidOperationException
{
    public RunRevisionConflictException(
        string runId,
        long expectedRevision,
        long actualRevision)
        : base(
            $"Run '{runId}' revision conflict: expected "
            + $"{expectedRevision}, actual {actualRevision}.")
    {
        RunId = runId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string RunId { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}

public sealed class JournalEntryConflictException : InvalidOperationException
{
    public JournalEntryConflictException(string message)
        : base(message)
    {
    }
}

public sealed class OperationLedgerConflictException : InvalidOperationException
{
    public OperationLedgerConflictException(string operationId, string message)
        : base($"Operation '{operationId}' conflict: {message}")
    {
        OperationId = operationId;
    }

    public string OperationId { get; }
}
