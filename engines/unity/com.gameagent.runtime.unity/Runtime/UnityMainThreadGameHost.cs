using System;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Unity
{
    public delegate ValueTask<ActionReceipt> UnityActionHandler(
        ActionRequest request,
        CancellationToken cancellationToken);

    public sealed class UnityMainThreadGameHost : IGameHost
    {
        private readonly UnityMainThreadDispatcher _dispatcher;
        private readonly UnityActionHandler _handler;
        private readonly IRuntimeClock _clock;

        public UnityMainThreadGameHost(
            UnityMainThreadDispatcher dispatcher,
            UnityActionHandler handler,
            IRuntimeClock clock = null)
        {
            _dispatcher = dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));
            _handler = handler
                ?? throw new ArgumentNullException(nameof(handler));
            _clock = clock ?? new SystemRuntimeClock();
        }

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (HasExpired(request))
            {
                return DeadlineFailure(request.OperationId);
            }

            try
            {
                return await _dispatcher.InvokeAsync(
                        token => InvokeHandler(request, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnityDispatcherQueueFullException)
            {
                return Failure(
                    request.OperationId,
                    "unity_dispatch_queue_full",
                    retryable: true);
            }
            catch (UnityDispatchCancelledBeforeExecutionException)
            {
                return Failure(
                    request.OperationId,
                    "unity_dispatch_cancelled",
                    retryable: true);
            }
        }

        private ValueTask<ActionReceipt> InvokeHandler(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            if (HasExpired(request))
            {
                return new ValueTask<ActionReceipt>(
                    DeadlineFailure(request.OperationId));
            }

            return _handler(request, cancellationToken);
        }

        private bool HasExpired(ActionRequest request)
        {
            return request.Deadline.HasValue
                && _clock.UtcNow >= request.Deadline.Value;
        }

        private ActionReceipt DeadlineFailure(string operationId)
        {
            return Failure(
                operationId,
                "unity_dispatch_deadline",
                retryable: true);
        }

        private ActionReceipt Failure(
            string operationId,
            string errorCode,
            bool retryable)
        {
            return new ActionReceipt
            {
                OperationId = operationId,
                Revision = 0,
                Status = ReceiptStatuses.Failed,
                ErrorCode = errorCode,
                Retryable = retryable,
                ReceivedAt = _clock.UtcNow
            };
        }
    }
}
