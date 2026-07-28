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
        : IUnityDurableAgentRuntimeBackend, IAsyncDisposable
    {
        private readonly IDurableAgentRuntime _runtime;
        private readonly bool _ownsRuntime;
        private int _disposed;

        public DurableUnityAgentRuntimeBackend(
            IDurableAgentRuntime runtime,
            bool ownsRuntime = false)
        {
            _runtime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
            _ownsRuntime = ownsRuntime;
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

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0
                || !_ownsRuntime)
            {
                return default(ValueTask);
            }

            if (_runtime is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            if (_runtime is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return default(ValueTask);
        }
    }

    public sealed class BuiltUnityAgentRuntimeBackend
        : IUnityDurableAgentRuntimeBackend, IAsyncDisposable
    {
        private readonly BuiltGameAgentRuntime _built;
        private readonly bool _ownsBuiltRuntime;
        private int _disposed;

        public BuiltUnityAgentRuntimeBackend(
            BuiltGameAgentRuntime built,
            bool ownsBuiltRuntime = true)
        {
            _built = built
                ?? throw new ArgumentNullException(nameof(built));
            _ownsBuiltRuntime = ownsBuiltRuntime;
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

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0
                || !_ownsBuiltRuntime)
            {
                return default(ValueTask);
            }

            return _built.DisposeAsync();
        }
    }
}
