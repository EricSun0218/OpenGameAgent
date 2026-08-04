using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Hosting;

public interface IRemoteActionChannel
{
    ValueTask<ActionReceipt> SubmitAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteActionOutcomeUnknownException : IOException
{
    public RemoteActionOutcomeUnknownException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class RemoteGameHost : IGameHost
{
    private readonly IRemoteActionChannel _channel;
    private readonly IRuntimeClock _clock;

    public RemoteGameHost(IRemoteActionChannel channel, IRuntimeClock clock)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureValid(ProtocolValidator.Validate(request), "The outbound action request is invalid.");
        try
        {
            var receipt = await _channel.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureValid(ProtocolValidator.Validate(receipt), "The remote action receipt is invalid.");
            if (!string.Equals(receipt.OperationId, request.OperationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The remote receipt operation ID does not match its request.");
            }
            return receipt;
        }
        catch (RemoteActionOutcomeUnknownException)
        {
            return new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Unknown,
                ErrorCode = "remote_outcome_unknown",
                Retryable = false,
                ReceivedAt = _clock.UtcNow
            };
        }
    }

    private static void EnsureValid(IReadOnlyList<ProtocolValidationError> errors, string message)
    {
        if (errors.Count > 0)
        {
            throw new InvalidDataException(message + " " + errors[0].Code + " at " + errors[0].Path + ".");
        }
    }
}
