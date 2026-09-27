using System;
using System.Collections;
using System.Globalization;
using System.Threading;
using OpenGameAgent.Unity.Protocol;
using UnityEngine;

namespace OpenGameAgent.Unity
{
    public sealed class AuthoritativeActionPump : MonoBehaviour
    {
        [SerializeField] private string baseUrl;
        [SerializeField] private string accessToken;
        [SerializeField] private string sessionId;
        [SerializeField] private string actorId;
        [SerializeField] private int maxRequestBytes;
        [SerializeField] private int maxResponseBytes;
        [SerializeField] private int maxAttachmentResponseBytes;
        [SerializeField] private int timeoutSeconds;
        [SerializeField] private float idlePollIntervalSeconds;
        [SerializeField] private float executionDurationSeconds;
        [SerializeField] private float[] deliveryRetryDelaysSeconds;
        [SerializeField] private MonoBehaviour authoritativeAdapter;

        private AuthoritativeActionExecutionContext executionContext;
        private AuthoritativeActionInspectionContext inspectionContext;
        private Coroutine adapterCoroutine;
        private Coroutine pump;
        private long epoch;
        private double adapterDeadline;
        private int mainThreadId;
        private string boundSessionId;
        private string boundActorId;
        private AuthoritativeActionFence lastAuthoritativeReadFence;

        public AuthoritativeActionPumpState State { get; private set; } = AuthoritativeActionPumpState.Stopped;

        private void OnEnable()
        {
            if (!HasValidConfiguration())
            {
                Debug.LogError("OpenGameAgent action pump requires host-configured positive limits and finite policies.", this);
                enabled = false;
                return;
            }

            if (!(authoritativeAdapter is IAuthoritativeActionAdapter))
            {
                Debug.LogError("OpenGameAgent action pump requires an authoritative game action adapter.", this);
                enabled = false;
                return;
            }

            boundSessionId = sessionId;
            boundActorId = actorId;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            State = AuthoritativeActionPumpState.Idle;
            pump = StartCoroutine(Pump());
        }

        private void OnDisable()
        {
            executionContext?.Cancel();
            inspectionContext?.Cancel();
            StopAdapter();
            if (pump != null) StopCoroutine(pump);
            pump = null;
            State = AuthoritativeActionPumpState.Stopped;
        }

        private IEnumerator Pump()
        {
            UnityRuntimeClient client;
            try
            {
                client = new UnityRuntimeClient(
                    baseUrl,
                    accessToken,
                    maxRequestBytes,
                    maxResponseBytes,
                    maxAttachmentResponseBytes,
                    timeoutSeconds);
            }
            catch (Exception exception)
            {
                Halt(exception.Message);
                yield break;
            }

            var nextRequest = new RuntimeActionNextRequest
            {
                SessionId = boundSessionId,
                ActorId = boundActorId,
            };
            var recoveryRequest = new RuntimeActionRecoveryRequest
            {
                SessionId = boundSessionId,
                ActorId = boundActorId,
            };
            var adapter = (IAuthoritativeActionAdapter)authoritativeAdapter;
            bool recovering = true;

            while (State != AuthoritativeActionPumpState.Stopped && State != AuthoritativeActionPumpState.Halted)
            {
                State = AuthoritativeActionPumpState.Idle;
                RuntimeActionDelivery delivery;
                if (recovering)
                {
                    RuntimeClientResult<RuntimeActionRecoveryResponse> recovered = default;
                    yield return client.RecoverAction(recoveryRequest, result => recovered = result);
                    if (!recovered.Succeeded)
                    {
                        if (!IsAmbiguousFailure(recovered.Failure))
                        {
                            Halt("The Runtime rejected the recovery-only action request.");
                            yield break;
                        }
                        yield return new WaitForSecondsRealtime(idlePollIntervalSeconds);
                        continue;
                    }
                    if (recovered.Value.Delivery == null)
                    {
                        recovering = false;
                        continue;
                    }
                    delivery = new RuntimeActionDelivery
                    {
                        Kind = RuntimeActionDeliveryKind.Reconcile,
                        Action = recovered.Value.Delivery.Action,
                    };
                }
                else
                {
                    RuntimeClientResult<RuntimeActionNextResponse> next = default;
                    yield return client.NextAction(nextRequest, result => next = result);
                    if (!next.Succeeded)
                    {
                        if (!IsAmbiguousFailure(next.Failure))
                        {
                            Halt("The Runtime rejected the next-action request.");
                            yield break;
                        }
                        yield return new WaitForSecondsRealtime(idlePollIntervalSeconds);
                        continue;
                    }
                    if (next.Value.Delivery == null)
                    {
                        yield return new WaitForSecondsRealtime(idlePollIntervalSeconds);
                        continue;
                    }
                    delivery = next.Value.Delivery;
                }

                if (delivery.Action == null || delivery.Action.Claim == null)
                {
                    Halt("The Runtime delivered an action without its claim fence.");
                    yield break;
                }

                lastAuthoritativeReadFence = null;
                epoch += 1;
                IEnumerator operation = delivery.Kind == RuntimeActionDeliveryKind.Dispatch
                    ? Execute(adapter, client, delivery)
                    : Inspect(adapter, client, delivery);
                while (operation.MoveNext()) yield return operation.Current;
            }
        }

        private IEnumerator Execute(
            IAuthoritativeActionAdapter adapter,
            UnityRuntimeClient client,
            RuntimeActionDelivery delivery)
        {
            if (!HasExpectedSelector(delivery.Action))
            {
                Halt("The Runtime action delivery did not match this pump's startup selector.");
                yield break;
            }
            State = AuthoritativeActionPumpState.Executing;
            executionContext = new AuthoritativeActionExecutionContext(delivery.Action, epoch, CanBeginWorldWrite);
            AuthoritativeActionFence fence = executionContext.Fence;
            if (!TrySetExecutionDeadline(fence.ClaimExpiresAt))
            {
                executionContext.Cancel();
                executionContext = null;
                if (State == AuthoritativeActionPumpState.Stopped
                    || State == AuthoritativeActionPumpState.Halted)
                {
                    yield break;
                }
                IEnumerator expiredInspection = Inspect(adapter, client, delivery);
                while (expiredInspection.MoveNext()) yield return expiredInspection.Current;
                yield break;
            }
            Exception adapterFailure = null;
            bool adapterFinished = false;
            IEnumerator operation = null;
            try
            {
                operation = adapter.Execute(executionContext);
            }
            catch (Exception exception)
            {
                adapterFailure = exception;
                adapterFinished = true;
            }

            if (adapterFailure == null && operation != null && !executionContext.IsCompleted)
            {
                try
                {
                    adapterCoroutine = StartCoroutine(
                        RunAdapter(operation, exception => adapterFailure = exception, () => adapterFinished = true));
                }
                catch (Exception exception)
                {
                    adapterFailure = exception;
                    adapterFinished = true;
                }
            }
            else
            {
                adapterFinished = true;
            }
            while (!executionContext.IsCompleted && !adapterFinished && CanAwaitAdapter(epoch)) yield return null;
            StopAdapter();
            bool executionCompleted = executionContext.IsCompleted;
            RuntimeActionReceiptRequest receiptCandidate = executionContext.Receipt;
            executionContext.Cancel();
            executionContext = null;

            if (State == AuthoritativeActionPumpState.Stopped
                || State == AuthoritativeActionPumpState.Halted)
            {
                yield break;
            }
            if (adapterFailure != null || !executionCompleted)
            {
                IEnumerator uncertainInspection = Inspect(adapter, client, delivery);
                while (uncertainInspection.MoveNext()) yield return uncertainInspection.Current;
                yield break;
            }

            RuntimeActionReceiptRequest receipt = receiptCandidate;
            State = AuthoritativeActionPumpState.DeliveringReceipt;
            RuntimeClientResult<RuntimeActionRecordResponse> delivered = default;
            IEnumerator deliveryAttempt = DeliverRecord(
                completed => client.RecordActionReceipt(receipt, completed),
                value => delivered = value);
            while (deliveryAttempt.MoveNext()) yield return deliveryAttempt.Current;
            if (delivered.Succeeded)
            {
                if (receipt.Outcome == ReceiptOutcome.Unknown)
                {
                    Halt("The accepted action receipt remains unresolved.");
                }
                yield break;
            }
            if (delivered.Failure.Status == 409)
            {
                IEnumerator conflict = ResolveConflict(adapter, client, fence);
                while (conflict.MoveNext()) yield return conflict.Current;
                yield break;
            }
            Halt("The authoritative action receipt could not be delivered unambiguously.");
        }

        private IEnumerator Inspect(
            IAuthoritativeActionAdapter adapter,
            UnityRuntimeClient client,
            RuntimeActionDelivery delivery)
        {
            if (!HasExpectedSelector(delivery.Action))
            {
                Halt("The Runtime action delivery did not match this pump's startup selector.");
                yield break;
            }
            State = AuthoritativeActionPumpState.Inspecting;
            adapterDeadline = Time.realtimeSinceStartupAsDouble + executionDurationSeconds;
            inspectionContext = new AuthoritativeActionInspectionContext(
                delivery.Action,
                epoch,
                CanCompleteInspection);
            AuthoritativeActionFence fence = inspectionContext.Fence;
            Exception adapterFailure = null;
            bool adapterFinished = false;
            IEnumerator operation = null;
            try
            {
                operation = adapter.Inspect(inspectionContext);
            }
            catch (Exception exception)
            {
                adapterFailure = exception;
                adapterFinished = true;
            }

            if (adapterFailure == null && operation != null && !inspectionContext.IsCompleted)
            {
                try
                {
                    adapterCoroutine = StartCoroutine(
                        RunAdapter(operation, exception => adapterFailure = exception, () => adapterFinished = true));
                }
                catch (Exception exception)
                {
                    adapterFailure = exception;
                    adapterFinished = true;
                }
            }
            else
            {
                adapterFinished = true;
            }
            while (!inspectionContext.IsCompleted && !adapterFinished && CanAwaitAdapter(epoch)) yield return null;
            StopAdapter();
            bool inspectionCompleted = inspectionContext.IsCompleted;
            RuntimeActionReconcileRequest reconciliationCandidate = inspectionContext.Reconciliation;
            inspectionContext.Cancel();
            inspectionContext = null;

            if (adapterFailure != null)
            {
                Halt(adapterFailure.Message);
                yield break;
            }
            if (!inspectionCompleted)
            {
                Halt("The authoritative action adapter did not inspect the unresolved action before its deadline.");
                yield break;
            }

            RuntimeActionReconcileRequest reconciliation = reconciliationCandidate;
            State = AuthoritativeActionPumpState.Reconciling;
            RuntimeClientResult<RuntimeActionRecordResponse> delivered = default;
            IEnumerator deliveryAttempt = DeliverRecord(
                completed => client.ReconcileAction(reconciliation, completed),
                value => delivered = value);
            while (deliveryAttempt.MoveNext()) yield return deliveryAttempt.Current;
            if (delivered.Succeeded)
            {
                if (reconciliation.Resolution == Protocol.Resolution.Unknown)
                {
                    Halt("The accepted action reconciliation remains unresolved.");
                }
                yield break;
            }
            if (delivered.Failure.Status == 409)
            {
                IEnumerator conflict = ResolveConflict(adapter, client, fence);
                while (conflict.MoveNext()) yield return conflict.Current;
                yield break;
            }
            Halt("The action reconciliation could not be delivered unambiguously.");
        }

        private IEnumerator DeliverRecord(
            Func<Action<RuntimeClientResult<RuntimeActionRecordResponse>>, IEnumerator> send,
            Action<RuntimeClientResult<RuntimeActionRecordResponse>> completed)
        {
            for (int attempt = 0; ; attempt++)
            {
                RuntimeClientResult<RuntimeActionRecordResponse> result = default;
                yield return send(value => result = value);
                if (result.Succeeded || result.Failure.Status == 409 || !IsAmbiguousFailure(result.Failure))
                {
                    completed(result);
                    yield break;
                }
                if (attempt >= deliveryRetryDelaysSeconds.Length)
                {
                    completed(result);
                    yield break;
                }
                yield return new WaitForSecondsRealtime(deliveryRetryDelaysSeconds[attempt]);
            }
        }

        private IEnumerator ResolveConflict(
            IAuthoritativeActionAdapter adapter,
            UnityRuntimeClient client,
            AuthoritativeActionFence fence)
        {
            var request = new RuntimeActionGetRequest
            {
                SessionId = fence.SessionId,
                ActorId = fence.ActorId,
                OperationId = fence.OperationId,
            };
            for (int attempt = 0; ; attempt++)
            {
                RuntimeClientResult<RuntimeActionGetResponse> result = default;
                yield return client.GetAction(request, value => result = value);
                if (result.Succeeded)
                {
                    RuntimeActionRecord action = result.Value.Action;
                    if (action == null)
                    {
                        Halt("The conflicted action no longer exists in Runtime authority.");
                        yield break;
                    }
                    if (!HasExactIdentity(fence, action))
                    {
                        Halt("The conflicted Runtime action did not match the original action identity.");
                        yield break;
                    }
                    if (ShouldInspectAfterConflict(action.Status))
                    {
                        if (action.Claim == null)
                        {
                            Halt("The unresolved conflicted action has no current claim fence.");
                            yield break;
                        }
                        if (lastAuthoritativeReadFence != null
                            && HasExactFence(lastAuthoritativeReadFence, action))
                        {
                            Halt("The conflicted action made no claim-fence progress after its authoritative read.");
                            yield break;
                        }
                        lastAuthoritativeReadFence = AuthoritativeActionFence.Capture(action);
                        epoch += 1;
                        IEnumerator inspection = Inspect(
                            adapter,
                            client,
                            new RuntimeActionDelivery
                            {
                                Kind = RuntimeActionDeliveryKind.Reconcile,
                                Action = action,
                            });
                        while (inspection.MoveNext()) yield return inspection.Current;
                    }
                    yield break;
                }
                if (!IsAmbiguousFailure(result.Failure) || attempt >= deliveryRetryDelaysSeconds.Length)
                {
                    Halt("The conflicted action could not be read from Runtime authority.");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(deliveryRetryDelaysSeconds[attempt]);
            }
        }

        private static bool ShouldInspectAfterConflict(RuntimeActionRecordStatus status)
        {
            return status == RuntimeActionRecordStatus.Claimed
                || status == RuntimeActionRecordStatus.Uncertain;
        }

        private static bool HasExactIdentity(AuthoritativeActionFence fence, RuntimeActionRecord action)
        {
            return string.Equals(action.SessionId, fence.SessionId, StringComparison.Ordinal)
                && string.Equals(action.ActorId, fence.ActorId, StringComparison.Ordinal)
                && string.Equals(action.OperationId, fence.OperationId, StringComparison.Ordinal);
        }

        private static bool HasExactFence(AuthoritativeActionFence fence, RuntimeActionRecord action)
        {
            return HasExactIdentity(fence, action)
                && action.Claim != null
                && string.Equals(action.Claim.ClaimToken, fence.ClaimToken, StringComparison.Ordinal)
                && string.Equals(action.Claim.Attempt, fence.Attempt, StringComparison.Ordinal);
        }

        private bool HasExpectedSelector(RuntimeActionRecord action)
        {
            return string.Equals(action.SessionId, boundSessionId, StringComparison.Ordinal)
                && string.Equals(action.ActorId, boundActorId, StringComparison.Ordinal);
        }

        private bool TrySetExecutionDeadline(string claimExpiresAt)
        {
            if (!DateTimeOffset.TryParse(
                claimExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset expiresAt))
            {
                Halt("The Runtime action claim expiry is invalid.");
                return false;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            double remaining = (expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            if (remaining <= 0) return false;
            adapterDeadline = Math.Min(now + executionDurationSeconds, now + remaining);
            return true;
        }

        private bool CanBeginWorldWrite(long expectedEpoch)
        {
            return State == AuthoritativeActionPumpState.Executing
                && expectedEpoch == epoch
                && Thread.CurrentThread.ManagedThreadId == mainThreadId
                && Time.realtimeSinceStartupAsDouble < adapterDeadline;
        }

        private bool CanAwaitAdapter(long expectedEpoch)
        {
            return Thread.CurrentThread.ManagedThreadId == mainThreadId
                && expectedEpoch == epoch
                && State != AuthoritativeActionPumpState.Stopped
                && State != AuthoritativeActionPumpState.Halted
                && Time.realtimeSinceStartupAsDouble < adapterDeadline;
        }

        private bool CanCompleteInspection(long expectedEpoch)
        {
            return State == AuthoritativeActionPumpState.Inspecting
                && expectedEpoch == epoch
                && Thread.CurrentThread.ManagedThreadId == mainThreadId
                && Time.realtimeSinceStartupAsDouble < adapterDeadline;
        }

        private bool HasValidConfiguration()
        {
            if (maxRequestBytes <= 0
                || maxResponseBytes <= 0
                || maxAttachmentResponseBytes <= 0
                || timeoutSeconds <= 0
                || string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(actorId)
                || !IsPositiveFinite(idlePollIntervalSeconds)
                || !IsPositiveFinite(executionDurationSeconds)
                || deliveryRetryDelaysSeconds == null)
            {
                return false;
            }
            foreach (float delay in deliveryRetryDelaysSeconds)
            {
                if (float.IsNaN(delay) || float.IsInfinity(delay) || delay < 0) return false;
            }
            return true;
        }

        private void Halt(string message)
        {
            executionContext?.Cancel();
            inspectionContext?.Cancel();
            StopAdapter();
            State = AuthoritativeActionPumpState.Halted;
            Debug.LogError(message, this);
        }

        private void StopAdapter()
        {
            if (adapterCoroutine == null) return;
            StopCoroutine(adapterCoroutine);
            adapterCoroutine = null;
        }

        private static IEnumerator RunAdapter(
            IEnumerator operation,
            Action<Exception> faulted,
            Action finished)
        {
            while (true)
            {
                bool hasNext;
                object current;
                try
                {
                    hasNext = operation.MoveNext();
                    current = hasNext ? operation.Current : null;
                }
                catch (Exception exception)
                {
                    faulted(exception);
                    break;
                }
                if (!hasNext) break;
                yield return current;
            }
            finished();
        }

        private static bool IsAmbiguousFailure(RuntimeClientFailure failure)
        {
            return failure.Kind == RuntimeClientFailureKind.Transport
                || (failure.Kind == RuntimeClientFailureKind.Http
                    && failure.Status >= 500
                    && failure.Status <= 599);
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
