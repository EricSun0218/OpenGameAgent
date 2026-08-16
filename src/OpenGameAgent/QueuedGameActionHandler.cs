namespace OpenGameAgent;

/// <summary>
/// Marshals authoritative game actions onto a host-owned pump thread while preserving the
/// durable action protocol implemented by <see cref="DurableGameActionDispatcher"/>.
/// </summary>
/// <remarks>
/// Call <see cref="Pump"/> from the engine thread that is allowed to touch game state. The
/// first call binds the handler to that managed thread. Caller cancellation removes work only
/// while it is still queued; after an action starts, its outcome is allowed to settle so that a
/// caller timeout cannot silently cancel a world mutation.
/// </remarks>
public sealed class QueuedGameActionHandler : IGameActionHandler, IDisposable, IAsyncDisposable
{
    private const int MaximumSupportedCapacity = 1_000_000;

    private readonly object _gate = new();
    private readonly IGameActionHandler _innerHandler;
    private readonly LinkedList<WorkItem> _queue = new();
    private readonly TaskCompletionSource<object?> _stoppedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maximumPendingActions;
    private readonly int _maximumActiveActions;
    private bool _accepting = true;
    private int _activeCount;
    private int _pumpThreadId;
    private int _pumping;

    public QueuedGameActionHandler(
        IGameActionHandler innerHandler,
        int maximumPendingActions = 1_024,
        int maximumActiveActions = 64)
    {
        _innerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
        if (maximumPendingActions <= 0 || maximumPendingActions > MaximumSupportedCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingActions));
        }

        if (maximumActiveActions <= 0 || maximumActiveActions > MaximumSupportedCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveActions));
        }

        _maximumPendingActions = maximumPendingActions;
        _maximumActiveActions = maximumActiveActions;
    }

    public int MaximumPendingActions => _maximumPendingActions;

    public int MaximumActiveActions => _maximumActiveActions;

    public bool IsAccepting
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _activeCount;
            }
        }
    }

    public ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        var completion = Enqueue(intent, WorkKind.Execute, cancellationToken);
        return AwaitRequiredReceiptAsync(completion);
    }

    public ValueTask<GameActionReceipt?> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken) =>
        new(Enqueue(intent, WorkKind.Recover, cancellationToken));

    /// <summary>
    /// Starts up to <paramref name="maximumWorkItems"/> queued operations on the calling thread.
    /// Incomplete asynchronous handlers remain bounded by <see cref="MaximumActiveActions"/>.
    /// </summary>
    /// <returns>The number of work items started by this call.</returns>
    public int Pump(int maximumWorkItems)
    {
        if (maximumWorkItems <= 0 || maximumWorkItems > MaximumSupportedCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkItems));
        }

        BindPumpThread();
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
        {
            throw new InvalidOperationException("The game action pump cannot run concurrently or reentrantly.");
        }

        try
        {
            var started = 0;
            while (started < maximumWorkItems)
            {
                WorkItem? item;
                CancellationTokenRegistration cancellationRegistration;
                lock (_gate)
                {
                    if (_queue.First is null || _activeCount >= _maximumActiveActions)
                    {
                        break;
                    }

                    item = _queue.First.Value;
                    _queue.RemoveFirst();
                    item.Node = null;
                    item.State = WorkState.Started;
                    _activeCount++;
                    cancellationRegistration = item.CancellationRegistration;
                    item.CancellationRegistration = default;
                }

                cancellationRegistration.Dispose();
                Start(item);
                started++;
            }

            return started;
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    /// <summary>
    /// Rejects new work and faults work that has not started. Active operations are not canceled.
    /// </summary>
    public void Stop()
    {
        List<WorkItem>? pending = null;
        var stopped = false;
        lock (_gate)
        {
            if (!_accepting)
            {
                return;
            }

            _accepting = false;
            if (_queue.Count > 0)
            {
                pending = new List<WorkItem>(_queue.Count);
                while (_queue.First is { } node)
                {
                    _queue.RemoveFirst();
                    node.Value.Node = null;
                    node.Value.State = WorkState.Stopped;
                    pending.Add(node.Value);
                }
            }

            stopped = _activeCount == 0;
        }

        if (pending is not null)
        {
            foreach (var item in pending)
            {
                item.CancellationRegistration.Dispose();
                item.CancellationRegistration = default;
                item.Completion.TrySetException(
                    new ObjectDisposedException(nameof(QueuedGameActionHandler), "The game action queue has stopped."));
            }
        }

        if (stopped)
        {
            _stoppedCompletion.TrySetResult(null);
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _stoppedCompletion.Task.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task<GameActionReceipt?> Enqueue(
        GameActionIntent intent,
        WorkKind kind,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<GameActionReceipt?>(cancellationToken);
        }

        var item = new WorkItem(this, intent, kind);
        lock (_gate)
        {
            if (!_accepting)
            {
                throw new ObjectDisposedException(nameof(QueuedGameActionHandler), "The game action queue has stopped.");
            }

            if (_queue.Count >= _maximumPendingActions)
            {
                throw new GameRuntimeLimitException(
                    nameof(_maximumPendingActions),
                    "The game action queue reached its pending capacity.");
            }

            item.Node = _queue.AddLast(item);
            if (cancellationToken.CanBeCanceled)
            {
                var state = new CancellationState(item, cancellationToken);
                item.CancellationRegistration = cancellationToken.Register(
                    static callbackState =>
                    {
                        var cancellation = (CancellationState)callbackState!;
                        cancellation.Item.Owner.CancelQueued(cancellation.Item, cancellation.Token);
                    },
                    state);

                if (item.State != WorkState.Queued)
                {
                    QueueCancellationRegistrationRelease(item);
                }
            }
        }

        return item.Completion.Task;
    }

    private void CancelQueued(WorkItem item, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (item.State != WorkState.Queued || item.Node is null)
            {
                return;
            }

            _queue.Remove(item.Node);
            item.Node = null;
            item.State = WorkState.Canceled;
        }

        item.Completion.TrySetCanceled(cancellationToken);
        QueueCancellationRegistrationRelease(item);
    }

    private static void QueueCancellationRegistrationRelease(WorkItem item)
    {
        ThreadPool.QueueUserWorkItem(
            static state =>
            {
                var workItem = (WorkItem)state!;
                workItem.Owner.ReleaseCancellationRegistration(workItem);
            },
            item);
    }

    private void ReleaseCancellationRegistration(WorkItem item)
    {
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            registration = item.CancellationRegistration;
            item.CancellationRegistration = default;
        }

        registration.Dispose();
    }

    private void BindPumpThread()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var pumpThreadId = Volatile.Read(ref _pumpThreadId);
        if (pumpThreadId == 0)
        {
            pumpThreadId = Interlocked.CompareExchange(ref _pumpThreadId, currentThreadId, 0);
            if (pumpThreadId == 0)
            {
                pumpThreadId = currentThreadId;
            }
        }

        if (pumpThreadId != currentThreadId)
        {
            throw new InvalidOperationException("The game action pump must always run on the thread that first called Pump.");
        }
    }

    private void Start(WorkItem item)
    {
        if (item.Kind == WorkKind.Execute)
        {
            StartExecute(item);
            return;
        }

        StartRecover(item);
    }

    private void StartExecute(WorkItem item)
    {
        try
        {
            var operation = _innerHandler.ExecuteAsync(item.Intent, CancellationToken.None);
            if (operation.IsCompleted)
            {
                Complete(item, operation.GetAwaiter().GetResult(), exception: null, canceled: false);
            }
            else
            {
                _ = ObserveExecuteAsync(item, operation);
            }
        }
        catch (OperationCanceledException exception)
        {
            Complete(item, receipt: null, exception, canceled: true);
        }
        catch (Exception exception)
        {
            Complete(item, receipt: null, exception, canceled: false);
        }
    }

    private void StartRecover(WorkItem item)
    {
        try
        {
            var operation = _innerHandler.RecoverAsync(item.Intent, CancellationToken.None);
            if (operation.IsCompleted)
            {
                Complete(item, operation.GetAwaiter().GetResult(), exception: null, canceled: false);
            }
            else
            {
                _ = ObserveRecoverAsync(item, operation);
            }
        }
        catch (OperationCanceledException exception)
        {
            Complete(item, receipt: null, exception, canceled: true);
        }
        catch (Exception exception)
        {
            Complete(item, receipt: null, exception, canceled: false);
        }
    }

    private async Task ObserveExecuteAsync(WorkItem item, ValueTask<GameActionReceipt> operation)
    {
        try
        {
            var receipt = await operation.ConfigureAwait(false);
            Complete(item, receipt, exception: null, canceled: false);
        }
        catch (OperationCanceledException exception)
        {
            Complete(item, receipt: null, exception, canceled: true);
        }
        catch (Exception exception)
        {
            Complete(item, receipt: null, exception, canceled: false);
        }
    }

    private async Task ObserveRecoverAsync(WorkItem item, ValueTask<GameActionReceipt?> operation)
    {
        try
        {
            var receipt = await operation.ConfigureAwait(false);
            Complete(item, receipt, exception: null, canceled: false);
        }
        catch (OperationCanceledException exception)
        {
            Complete(item, receipt: null, exception, canceled: true);
        }
        catch (Exception exception)
        {
            Complete(item, receipt: null, exception, canceled: false);
        }
    }

    private void Complete(
        WorkItem item,
        GameActionReceipt? receipt,
        Exception? exception,
        bool canceled)
    {
        var stopped = false;
        lock (_gate)
        {
            if (item.State != WorkState.Started)
            {
                return;
            }

            item.State = WorkState.Completed;
            _activeCount--;
            stopped = !_accepting && _activeCount == 0;
        }

        if (canceled)
        {
            item.Completion.TrySetCanceled();
        }
        else if (exception is not null)
        {
            item.Completion.TrySetException(exception);
        }
        else
        {
            item.Completion.TrySetResult(receipt);
        }

        if (stopped)
        {
            _stoppedCompletion.TrySetResult(null);
        }
    }

    private static async ValueTask<GameActionReceipt> AwaitRequiredReceiptAsync(
        Task<GameActionReceipt?> completion)
    {
        var receipt = await completion.ConfigureAwait(false);
        return receipt ?? throw new InvalidOperationException("The queued execute handler returned a null receipt.");
    }

    private enum WorkKind
    {
        Execute,
        Recover,
    }

    private enum WorkState
    {
        Queued,
        Started,
        Completed,
        Canceled,
        Stopped,
    }

    private sealed class WorkItem
    {
        public WorkItem(QueuedGameActionHandler owner, GameActionIntent intent, WorkKind kind)
        {
            Owner = owner;
            Intent = intent;
            Kind = kind;
        }

        public QueuedGameActionHandler Owner { get; }

        public GameActionIntent Intent { get; }

        public WorkKind Kind { get; }

        public TaskCompletionSource<GameActionReceipt?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkState State { get; set; }

        public LinkedListNode<WorkItem>? Node { get; set; }

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed class CancellationState
    {
        public CancellationState(WorkItem item, CancellationToken token)
        {
            Item = item;
            Token = token;
        }

        public WorkItem Item { get; }

        public CancellationToken Token { get; }
    }
}
