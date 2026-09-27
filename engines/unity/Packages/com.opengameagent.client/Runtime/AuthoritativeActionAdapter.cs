using System;
using System.Collections;
using System.Threading;
using OpenGameAgent.Unity.Protocol;

namespace OpenGameAgent.Unity
{
    public enum AuthoritativeActionPumpState
    {
        Idle,
        Inspecting,
        Executing,
        DeliveringReceipt,
        Reconciling,
        Halted,
        Stopped,
    }

    public interface IAuthoritativeActionAdapter
    {
        IEnumerator Execute(AuthoritativeActionExecutionContext context);

        IEnumerator Inspect(AuthoritativeActionInspectionContext context);
    }

    internal sealed class AuthoritativeActionFence
    {
        private AuthoritativeActionFence(RuntimeActionRecord action)
        {
            Claim claim = action.Claim
                ?? throw new ArgumentException("The Runtime action must include its claim fence.", nameof(action));
            SessionId = action.SessionId;
            ActorId = action.ActorId;
            OperationId = action.OperationId;
            Attempt = claim.Attempt;
            ClaimToken = claim.ClaimToken;
            ClaimExpiresAt = claim.ExpiresAt;
        }

        internal string SessionId { get; }

        internal string ActorId { get; }

        internal string OperationId { get; }

        internal string Attempt { get; }

        internal string ClaimToken { get; }

        internal string ClaimExpiresAt { get; }

        internal static AuthoritativeActionFence Capture(RuntimeActionRecord action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return new AuthoritativeActionFence(action);
        }
    }

    internal static class AuthoritativeActionEvidence
    {
        internal static RuntimeActionReceiptRequest SnapshotReceipt(
            RuntimeActionReceiptRequest value,
            AuthoritativeActionFence fence)
        {
            var bound = new RuntimeActionReceiptRequest
            {
                SessionId = fence.SessionId,
                ActorId = fence.ActorId,
                OperationId = fence.OperationId,
                ClaimToken = fence.ClaimToken,
                Attempt = fence.Attempt,
                Outcome = value.Outcome,
                AuthorityAfter = value.AuthorityAfter,
                Result = value.Result,
                ModelResult = value.ModelResult,
            };
            return RuntimeProtocolJson.Deserialize<RuntimeActionReceiptRequest>(
                RuntimeProtocolJson.Serialize(bound));
        }

        internal static RuntimeActionReconcileRequest SnapshotReconciliation(
            RuntimeActionReconcileRequest value,
            AuthoritativeActionFence fence)
        {
            var bound = new RuntimeActionReconcileRequest
            {
                SessionId = fence.SessionId,
                ActorId = fence.ActorId,
                OperationId = fence.OperationId,
                ClaimToken = fence.ClaimToken,
                Attempt = fence.Attempt,
                Resolution = value.Resolution,
                AuthorityAfter = value.AuthorityAfter,
                Result = value.Result,
                ModelResult = value.ModelResult,
            };
            return RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                RuntimeProtocolJson.Serialize(bound));
        }
    }

    public sealed class AuthoritativeActionExecutionContext
    {
        private readonly Func<long, bool> canBeginWrite;
        private readonly int mainThreadId;
        private int cancelled;
        private int completionState;
        private RuntimeActionReceiptRequest receipt;

        internal AuthoritativeActionExecutionContext(
            RuntimeActionRecord action,
            long epoch,
            Func<long, bool> canBeginWrite)
        {
            Fence = AuthoritativeActionFence.Capture(action);
            Action = action;
            Epoch = epoch;
            this.canBeginWrite = canBeginWrite ?? throw new ArgumentNullException(nameof(canBeginWrite));
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public RuntimeActionRecord Action { get; }

        public long Epoch { get; }

        public bool IsCancellationRequested => Volatile.Read(ref cancelled) != 0
            || (Thread.CurrentThread.ManagedThreadId == mainThreadId && !canBeginWrite(Epoch));

        public bool IsCompleted => Volatile.Read(ref completionState) == 2;

        public bool TryCommit(
            Func<RuntimeActionRecord, bool> recheckExpectedAuthority,
            Func<RuntimeActionRecord, RuntimeActionReceiptRequest> gameTransaction)
        {
            if (recheckExpectedAuthority == null) throw new ArgumentNullException(nameof(recheckExpectedAuthority));
            if (gameTransaction == null) throw new ArgumentNullException(nameof(gameTransaction));
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId
                || Interlocked.CompareExchange(ref completionState, 1, 0) != 0)
            {
                return false;
            }
            if (IsCancellationRequested) return false;
            if (!recheckExpectedAuthority(Action)) return false;
            if (IsCancellationRequested) return false;
            RuntimeActionReceiptRequest value = gameTransaction(Action)
                ?? throw new InvalidOperationException("The game transaction did not return an action receipt.");
            receipt = AuthoritativeActionEvidence.SnapshotReceipt(value, Fence);
            Volatile.Write(ref completionState, 2);
            return true;
        }

        public bool TryCompleteWithoutWrite(RuntimeActionReceiptRequest value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Outcome == ReceiptOutcome.Succeeded)
            {
                throw new InvalidOperationException("A succeeded action must be completed inside TryCommit.");
            }
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId
                || Interlocked.CompareExchange(ref completionState, 1, 0) != 0)
            {
                return false;
            }
            if (IsCancellationRequested) return false;
            receipt = AuthoritativeActionEvidence.SnapshotReceipt(value, Fence);
            Volatile.Write(ref completionState, 2);
            return true;
        }

        internal RuntimeActionReceiptRequest Receipt => receipt;

        internal AuthoritativeActionFence Fence { get; }

        internal void Cancel()
        {
            Volatile.Write(ref cancelled, 1);
        }
    }

    public sealed class AuthoritativeActionInspectionContext
    {
        private readonly Func<long, bool> isCurrent;
        private readonly int mainThreadId;
        private int cancelled;
        private int completionState;
        private RuntimeActionReconcileRequest reconciliation;

        internal AuthoritativeActionInspectionContext(
            RuntimeActionRecord action,
            long epoch,
            Func<long, bool> isCurrent)
        {
            Fence = AuthoritativeActionFence.Capture(action);
            Action = action;
            Epoch = epoch;
            this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public RuntimeActionRecord Action { get; }

        public long Epoch { get; }

        public bool IsCancellationRequested => Volatile.Read(ref cancelled) != 0
            || (Thread.CurrentThread.ManagedThreadId == mainThreadId && !isCurrent(Epoch));

        public bool IsCompleted => Volatile.Read(ref completionState) == 2;

        public bool TryComplete(RuntimeActionReconcileRequest value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId
                || Interlocked.CompareExchange(ref completionState, 1, 0) != 0
                || IsCancellationRequested)
            {
                return false;
            }
            reconciliation = AuthoritativeActionEvidence.SnapshotReconciliation(value, Fence);
            Volatile.Write(ref completionState, 2);
            return true;
        }

        internal RuntimeActionReconcileRequest Reconciliation => reconciliation;

        internal AuthoritativeActionFence Fence { get; }

        internal void Cancel()
        {
            Volatile.Write(ref cancelled, 1);
        }
    }
}
