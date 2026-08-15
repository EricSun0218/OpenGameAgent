using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

/// <summary>
/// Bridges background action requests to a host-owned thread.
/// The host must call <see cref="Pump"/> from the thread that owns game state.
/// </summary>
public sealed class QueuedGameActionHandler : IGameActionHandler, IDisposable
{
    private const int DefaultCapacity = 256;
    private const int MaximumCapacity = 100_000;

    private readonly object _gate = new();
    private readonly Queue<PendingRequest> _requests = new();
    private readonly Func<GameActionIntent, GameActionReceipt> _execute;
    private readonly Func<GameActionIntent, GameActionReceipt?> _recover;
    private readonly int _capacity;
    private int _queuedCount;
    private int _stopped;

    public QueuedGameActionHandler(
        Func<GameActionIntent, GameActionReceipt> execute,
        Func<GameActionIntent, GameActionReceipt?> recover,
        int capacity = DefaultCapacity)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        if (capacity <= 0 || capacity > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queuedCount;
            }
        }
    }

    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    public async ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        var request = Enqueue(intent, isRecovery: false, cancellationToken);
        var receipt = await request.Completion.Task.ConfigureAwait(false);
        return receipt ?? throw new InvalidOperationException("The execute callback returned a null receipt.");
    }

    public async ValueTask<GameActionReceipt?> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        var request = Enqueue(intent, isRecovery: true, cancellationToken);
        return await request.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes up to <paramref name="maximumWorkItems"/> non-cancelled requests on the caller's thread.
    /// </summary>
    public int Pump(int maximumWorkItems = 64)
    {
        if (maximumWorkItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkItems));
        }

        var executed = 0;
        while (executed < maximumWorkItems)
        {
            PendingRequest? request;
            lock (_gate)
            {
                if (_requests.Count == 0)
                {
                    break;
                }

                request = _requests.Dequeue();
                if (!request.TryStart())
                {
                    request.DisposeRegistration();
                    request = null;
                }
                else
                {
                    _queuedCount--;
                }
            }

            if (request is null)
            {
                continue;
            }

            executed++;
            request.Run(_execute, _recover);
        }

        return executed;
    }

    /// <summary>
    /// Rejects new requests and fails requests that have not started.
    /// A request already running inside <see cref="Pump"/> is allowed to finish.
    /// </summary>
    public void Stop(Exception? reason = null)
    {
        var stoppedRequests = new List<PendingRequest>();
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            while (_requests.Count > 0)
            {
                var request = _requests.Dequeue();
                if (request.TryStop())
                {
                    _queuedCount--;
                    stoppedRequests.Add(request);
                }
            }
        }

        var failure = reason ?? new InvalidOperationException("The game action handler has stopped.");
        foreach (var request in stoppedRequests)
        {
            request.Fail(failure);
        }
    }

    public void Dispose() => Stop(new ObjectDisposedException(nameof(QueuedGameActionHandler)));

    private PendingRequest Enqueue(
        GameActionIntent intent,
        bool isRecovery,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        var request = new PendingRequest(intent, isRecovery, cancellationToken);
        lock (_gate)
        {
            ThrowIfStopped();
            if (_queuedCount >= _capacity)
            {
                throw new GameRuntimeLimitException(
                    nameof(_capacity),
                    "The queued game action handler reached its capacity.");
            }

            _requests.Enqueue(request);
            _queuedCount++;
            if (cancellationToken.CanBeCanceled)
            {
                request.AttachRegistration(cancellationToken.Register(
                    static state =>
                    {
                        var cancellation = (CancellationRegistrationState)state!;
                        cancellation.Owner.Cancel(cancellation.Request);
                    },
                    new CancellationRegistrationState(this, request)));
            }
        }

        return request;
    }

    private void Cancel(PendingRequest request)
    {
        var cancelled = false;
        lock (_gate)
        {
            if (request.TryCancel())
            {
                _queuedCount--;
                cancelled = true;
            }
        }

        if (cancelled)
        {
            request.CancelCompletion();
            request.DisposeRegistration();
        }
    }

    private void ThrowIfStopped()
    {
        if (IsStopped)
        {
            throw new InvalidOperationException("The game action handler has stopped.");
        }
    }

    private sealed class CancellationRegistrationState
    {
        public CancellationRegistrationState(QueuedGameActionHandler owner, PendingRequest request)
        {
            Owner = owner;
            Request = request;
        }

        public QueuedGameActionHandler Owner { get; }

        public PendingRequest Request { get; }
    }

    private sealed class PendingRequest
    {
        private readonly object _stateGate = new();
        private readonly bool _isRecovery;
        private readonly CancellationToken _cancellationToken;
        private RequestState _state;
        private CancellationTokenRegistration _registration;
        private bool _registrationAttached;

        public PendingRequest(
            GameActionIntent intent,
            bool isRecovery,
            CancellationToken cancellationToken)
        {
            Intent = intent;
            _isRecovery = isRecovery;
            _cancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<GameActionReceipt?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public GameActionIntent Intent { get; }

        public TaskCompletionSource<GameActionReceipt?> Completion { get; }

        public bool TryStart() => TryTransition(RequestState.Queued, RequestState.Started);

        public bool TryCancel() => TryTransition(RequestState.Queued, RequestState.Cancelled);

        public bool TryStop() => TryTransition(RequestState.Queued, RequestState.Stopped);

        public void AttachRegistration(CancellationTokenRegistration registration)
        {
            var dispose = false;
            lock (_stateGate)
            {
                _registration = registration;
                _registrationAttached = true;
                dispose = _state != RequestState.Queued;
            }

            if (dispose)
            {
                registration.Dispose();
            }
        }

        public void DisposeRegistration()
        {
            CancellationTokenRegistration registration;
            lock (_stateGate)
            {
                if (!_registrationAttached)
                {
                    return;
                }

                registration = _registration;
                _registrationAttached = false;
            }

            registration.Dispose();
        }

        public void CancelCompletion() => Completion.TrySetCanceled(_cancellationToken);

        public void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
            DisposeRegistration();
        }

        public void Run(
            Func<GameActionIntent, GameActionReceipt> execute,
            Func<GameActionIntent, GameActionReceipt?> recover)
        {
            try
            {
                var receipt = _isRecovery ? recover(Intent) : execute(Intent);
                if (!_isRecovery && receipt is null)
                {
                    throw new InvalidOperationException("The execute callback returned a null receipt.");
                }

                Completion.TrySetResult(receipt);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
            finally
            {
                DisposeRegistration();
            }
        }

        private bool TryTransition(RequestState expected, RequestState next)
        {
            lock (_stateGate)
            {
                if (_state != expected)
                {
                    return false;
                }

                _state = next;
                return true;
            }
        }
    }

    private enum RequestState
    {
        Queued,
        Started,
        Cancelled,
        Stopped,
    }
}
