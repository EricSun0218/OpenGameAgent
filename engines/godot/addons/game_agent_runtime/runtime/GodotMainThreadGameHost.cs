using System.Collections.Concurrent;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Godot;

public sealed class GodotMainThreadGameHost : IGameHost
{
    private readonly ConcurrentDictionary<
        string,
        Func<
            ActionRequest,
            CancellationToken,
            ValueTask<ActionReceipt>>> _handlers =
        new(StringComparer.Ordinal);
    private readonly GodotMainThreadDispatcher _dispatcher;
    private readonly IRuntimeClock _clock;

    public GodotMainThreadGameHost(
        GodotMainThreadDispatcher dispatcher,
        IRuntimeClock clock)
    {
        _dispatcher = dispatcher
            ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Register(
        string actionName,
        Func<ActionRequest, ActionReceipt> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        Register(
            actionName,
            (request, _) =>
                new ValueTask<ActionReceipt>(handler(request)));
    }

    public void Register(
        string actionName,
        Func<
            ActionRequest,
            CancellationToken,
            ValueTask<ActionReceipt>> handler)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            throw new ArgumentException(
                "An action name is required.",
                nameof(actionName));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (!_handlers.TryAdd(actionName, handler))
        {
            throw new InvalidOperationException(
                $"A Godot action handler is already registered for '{actionName}'.");
        }
    }

    public bool Unregister(string actionName) =>
        _handlers.TryRemove(actionName, out _);

    public async ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(request.ActionName, out var handler))
        {
            return Failure(
                request.OperationId,
                ReceiptStatuses.Rejected,
                "godot_handler_not_found",
                false);
        }

        try
        {
            return await _dispatcher
                .InvokeAsync(
                    dispatchToken => handler(request, dispatchToken),
                    request.OperationId,
                    request.Deadline,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GodotDispatcherQueueFullException)
        {
            return Failure(
                request.OperationId,
                ReceiptStatuses.Failed,
                "godot_dispatch_queue_full",
                true);
        }
        catch (TimeoutException)
        {
            return Failure(
                request.OperationId,
                ReceiptStatuses.Failed,
                "godot_dispatch_deadline",
                true);
        }
        catch (GodotDispatchCancelledBeforeExecutionException)
        {
            return Failure(
                request.OperationId,
                ReceiptStatuses.Failed,
                "godot_dispatch_cancelled",
                true);
        }
    }

    private ActionReceipt Failure(
        string operationId,
        string status,
        string errorCode,
        bool retryable)
    {
        return new ActionReceipt
        {
            OperationId = operationId,
            Revision = 0,
            Status = status,
            ErrorCode = errorCode,
            Retryable = retryable,
            ReceivedAt = _clock.UtcNow
        };
    }
}
