using System;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Runtime;

namespace GameAgent.Unity
{
    public interface IUnityAgentRuntimeBackend<TRequest, TOutcome>
    {
        ValueTask<TOutcome> RunAsync(
            TRequest request,
            CancellationToken cancellationToken);
    }

    public interface IUnityDurableAgentRuntimeBackend
        : IUnityAgentRuntimeBackend<DurableRunRequest, DurableRunOutcome>
    {
        RuntimeControlPlane Controls { get; }

        ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Optional durable-backend capability required by multi-actor participant
    /// recovery. The guard is evaluated before a recovered run can reach a
    /// provider, reconciler, or game-host side effect.
    /// </summary>
    public interface IUnityGuardedDurableAgentRuntimeBackend
        : IUnityDurableAgentRuntimeBackend
    {
        /// <summary>
        /// True only when guarded resume is supported by the entire wrapped
        /// backend chain. Adapters must not report the capability merely
        /// because they can defer a later not-supported exception.
        /// </summary>
        bool SupportsGuardedResume { get; }

        ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard guard);
    }

    public sealed class HeadlessUnityAgentRuntimeBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly HeadlessAgentRuntime _runtime;

        public HeadlessUnityAgentRuntimeBackend(
            HeadlessAgentRuntime runtime)
        {
            _runtime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
        }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            return _runtime.RunAsync(request, cancellationToken);
        }
    }

    public sealed class DurableUnityAgentRuntimeBackend
        : IUnityGuardedDurableAgentRuntimeBackend, IAsyncDisposable
    {
        private readonly IDurableAgentRuntime _runtime;
        private readonly bool _ownsRuntime;
        private readonly object _disposeSync = new object();
        private Task _disposeTask;
        private int _disposed;

        public DurableUnityAgentRuntimeBackend(
            IDurableAgentRuntime runtime,
            bool ownsRuntime = false)
        {
            _runtime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
            _ownsRuntime = ownsRuntime;
        }

        public bool SupportsGuardedResume
        {
            get { return _runtime is IGuardedDurableAgentRuntime; }
        }

        public RuntimeControlPlane Controls
        {
            get { return _runtime.Controls; }
        }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            return _runtime.RunAsync(request, cancellationToken);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken)
        {
            return _runtime.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard guard)
        {
            return _runtime.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard);
        }

        public ValueTask DisposeAsync()
        {
            if (!_ownsRuntime || Volatile.Read(ref _disposed) != 0)
            {
                return default(ValueTask);
            }

            lock (_disposeSync)
            {
                if (_disposed != 0)
                {
                    return default(ValueTask);
                }
                if (_disposeTask == null || _disposeTask.IsCompleted)
                {
                    _disposeTask = DisposeOwnedRuntimeAsync();
                }

                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeOwnedRuntimeAsync()
        {
            if (_runtime is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                ((IDisposable)_runtime).Dispose();
            }

            Volatile.Write(ref _disposed, 1);
        }
    }

    public sealed class BuiltUnityAgentRuntimeBackend
        : IUnityGuardedDurableAgentRuntimeBackend, IAsyncDisposable
    {
        private readonly BuiltGameAgentRuntime _built;
        private readonly bool _ownsBuiltRuntime;
        private readonly object _disposeSync = new object();
        private Task _disposeTask;
        private int _disposed;

        public BuiltUnityAgentRuntimeBackend(
            BuiltGameAgentRuntime built,
            bool ownsBuiltRuntime = true)
        {
            _built = built
                ?? throw new ArgumentNullException(nameof(built));
            _ownsBuiltRuntime = ownsBuiltRuntime;
        }

        public bool SupportsGuardedResume
        {
            get { return true; }
        }

        public RuntimeControlPlane Controls
        {
            get { return _built.Runtime.Controls; }
        }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            return _built.Runtime.RunAsync(request, cancellationToken);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken)
        {
            return _built.Runtime.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard guard)
        {
            return _built.Runtime.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard);
        }

        public ValueTask DisposeAsync()
        {
            if (!_ownsBuiltRuntime || Volatile.Read(ref _disposed) != 0)
            {
                return default(ValueTask);
            }

            lock (_disposeSync)
            {
                if (_disposed != 0)
                {
                    return default(ValueTask);
                }
                if (_disposeTask == null || _disposeTask.IsCompleted)
                {
                    _disposeTask = DisposeOwnedRuntimeAsync();
                }

                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeOwnedRuntimeAsync()
        {
            await _built.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
        }
    }
}
