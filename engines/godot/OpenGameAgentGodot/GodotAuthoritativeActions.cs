using OpenGameAgent.Protocol;

namespace OpenGameAgent.Godot;

public interface IGodotAuthoritativeActionAdapter
{
    GodotActionInspection Inspect(GodotActionDelivery delivery);

    void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context);

    bool RecheckExpectedAuthority(GodotActionDelivery delivery);
}

public sealed class GodotActionExecutionContext
{
    private readonly Func<Func<GodotActionCompletion?>, bool> tryCommit;
    private readonly Func<GodotActionCompletion, bool> tryComplete;
    private int completed;

    internal GodotActionExecutionContext(
        long epoch,
        CancellationToken cancellationToken,
        Func<Func<GodotActionCompletion?>, bool> tryCommit,
        Func<GodotActionCompletion, bool> tryComplete)
    {
        Epoch = epoch;
        CancellationToken = cancellationToken;
        this.tryCommit = tryCommit;
        this.tryComplete = tryComplete;
    }

    public long Epoch { get; }

    public CancellationToken CancellationToken { get; }

    public bool TryCommit(Func<GodotActionCompletion> transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (CancellationToken.IsCancellationRequested
            || Interlocked.CompareExchange(ref completed, 1, 0) != 0)
        {
            return false;
        }

        bool transactionStarted = false;
        bool accepted = tryCommit(() =>
        {
            transactionStarted = true;
            return transaction();
        });
        if (!accepted && !transactionStarted) Volatile.Write(ref completed, 0);
        return accepted;
    }

    public bool TryCompleteWithoutWorldWrite(GodotActionCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Outcome == ReceiptOutcome.Succeeded
            || CancellationToken.IsCancellationRequested
            || Interlocked.CompareExchange(ref completed, 1, 0) != 0)
        {
            return false;
        }
        return tryComplete(completion);
    }
}

public sealed class GodotActionCompletion
{
    public required ReceiptOutcome Outcome { get; init; }

    public object? Result { get; init; }

    public object? ModelResult { get; init; }

    public GodotAuthorityVersion? AuthorityAfter { get; init; }
}

public sealed class GodotActionInspection
{
    public required Resolution Resolution { get; init; }

    public object? Result { get; init; }

    public object? ModelResult { get; init; }

    public GodotAuthorityVersion? AuthorityAfter { get; init; }
}

public sealed class GodotActionDelivery
{
    public required RuntimeActionDeliveryKind Kind { get; init; }

    public required string SessionId { get; init; }

    public required string ActorId { get; init; }

    public required string OperationId { get; init; }

    public required string ActionName { get; init; }

    public required string OperationKey { get; init; }

    public required object Payload { get; init; }

    public required string IntentFingerprint { get; init; }

    public required string PayloadFingerprint { get; init; }

    public required GodotActionCoordinate Coordinate { get; init; }

    public required GodotAuthorityVersion ExpectedAuthority { get; init; }

    public required string[] ConflictKeys { get; init; }

    public required ReplayPolicy ReplayPolicy { get; init; }

    public required RuntimeActionRecordStatus Status { get; init; }

    public required string ClaimToken { get; init; }

    public required string Attempt { get; init; }

    public required string ClaimExpiresAt { get; init; }
}

public sealed class GodotActionCoordinate
{
    public required string SessionId { get; init; }

    public required string ActorId { get; init; }

    public required string RunId { get; init; }

    public required string Turn { get; init; }
}

public sealed class GodotAuthorityVersion
{
    public required string TimelineId { get; init; }

    public required string Generation { get; init; }

    public required string Revision { get; init; }
}
