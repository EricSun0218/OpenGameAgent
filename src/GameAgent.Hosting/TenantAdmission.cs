namespace GameAgent.Hosting;

public sealed class TenantAdmissionOptions
{
    public int MaxKnownTenants { get; set; } = 1_024;

    public int MaxConcurrentRuns { get; set; } = 256;

    public int MaxConcurrentRunsPerTenant { get; set; } = 16;

    public int MaxQueuedRunsPerTenant { get; set; } = 32;

    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal TenantAdmissionOptions Snapshot()
    {
        if (MaxKnownTenants is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxKnownTenants));
        }

        if (MaxConcurrentRuns is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRuns));
        }

        if (MaxConcurrentRunsPerTenant < 1
            || MaxConcurrentRunsPerTenant > MaxConcurrentRuns)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRunsPerTenant));
        }

        if (MaxQueuedRunsPerTenant is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedRunsPerTenant));
        }

        if (ShutdownDrainTimeout < TimeSpan.FromMilliseconds(100)
            || ShutdownDrainTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownDrainTimeout));
        }

        return new TenantAdmissionOptions
        {
            MaxKnownTenants = MaxKnownTenants,
            MaxConcurrentRuns = MaxConcurrentRuns,
            MaxConcurrentRunsPerTenant = MaxConcurrentRunsPerTenant,
            MaxQueuedRunsPerTenant = MaxQueuedRunsPerTenant,
            ShutdownDrainTimeout = ShutdownDrainTimeout
        };
    }
}

public sealed class TenantCapacityExceededException : InvalidOperationException
{
    public TenantCapacityExceededException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class TenantAdmissionController : IAsyncDisposable
{
    private readonly TenantAdmissionOptions _options;
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, TenantState> _tenants = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private int _disposeTimedOut;
    private int _resourcesDisposed;

    public TenantAdmissionController(TenantAdmissionOptions? options = null)
    {
        _options = (options ?? new TenantAdmissionOptions()).Snapshot();
        _global = new SemaphoreSlim(_options.MaxConcurrentRuns, _options.MaxConcurrentRuns);
    }

    public async ValueTask<TenantAdmissionLease> AcquireAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        ThrowIfDisposed();
        TenantState state;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_tenants.TryGetValue(tenantId, out state!))
            {
                if (_tenants.Count >= _options.MaxKnownTenants)
                {
                    throw new TenantCapacityExceededException(
                        "max_known_tenants",
                        "The hosting process has reached its tenant identity limit.");
                }

                state = new TenantState(_options.MaxConcurrentRunsPerTenant);
                _tenants.Add(tenantId, state);
            }

            if (state.Capacity.Wait(0))
            {
                if (_global.Wait(0))
                {
                    state.References++;
                    state.Active++;
                    return new TenantAdmissionLease(this, tenantId, state);
                }
                state.Capacity.Release();
            }

            if (state.Waiting >= _options.MaxQueuedRunsPerTenant)
            {
                if (state.References == 0)
                {
                    _tenants.Remove(tenantId);
                    state.Capacity.Dispose();
                }
                throw new TenantCapacityExceededException(
                    "max_queued_runs_per_tenant",
                    "The tenant has reached its queued-run limit.");
            }

            state.References++;
            state.Waiting++;
        }

        var tenantAcquired = false;
        var globalAcquired = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await state.Capacity.WaitAsync(linked.Token).ConfigureAwait(false);
            tenantAcquired = true;
            await _global.WaitAsync(linked.Token).ConfigureAwait(false);
            globalAcquired = true;
            lock (_sync)
            {
                state.Waiting--;
                state.Active++;
            }

            return new TenantAdmissionLease(this, tenantId, state);
        }
        catch
        {
            if (globalAcquired)
            {
                _global.Release();
            }
            if (tenantAcquired)
            {
                state.Capacity.Release();
            }
            ReleaseReference(tenantId, state, wasWaiting: true, wasActive: false);
            throw;
        }
    }

    public TenantAdmissionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var active = 0;
            var waiting = 0;
            foreach (var state in _tenants.Values)
            {
                active += state.Active;
                waiting += state.Waiting;
            }

            return new TenantAdmissionSnapshot(_tenants.Count, active, waiting);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        lock (_sync)
        {
            if (_tenants.Count == 0) _drained.TrySetResult(true);
        }
        var completed = await Task.WhenAny(
            _drained.Task,
            Task.Delay(_options.ShutdownDrainTimeout)).ConfigureAwait(false);
        if (ReferenceEquals(completed, _drained.Task))
        {
            FinalizeResources();
            return;
        }
        Volatile.Write(ref _disposeTimedOut, 1);
        if (_drained.Task.IsCompleted) FinalizeResources();
    }

    private void Release(string tenantId, TenantState state)
    {
        state.Capacity.Release();
        _global.Release();
        ReleaseReference(tenantId, state, wasWaiting: false, wasActive: true);
    }

    private void ReleaseReference(string tenantId, TenantState state, bool wasWaiting, bool wasActive)
    {
        var drained = false;
        lock (_sync)
        {
            if (wasWaiting)
            {
                state.Waiting--;
            }
            if (wasActive)
            {
                state.Active--;
            }
            state.References--;
            if (state.References == 0)
            {
                _tenants.Remove(tenantId);
                state.Capacity.Dispose();
            }
            if (Volatile.Read(ref _disposed) != 0 && _tenants.Count == 0)
            {
                drained = _drained.TrySetResult(true);
            }
        }
        if (drained && Volatile.Read(ref _disposeTimedOut) != 0) FinalizeResources();
    }

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 256)
        {
            throw new ArgumentException("A bounded tenant ID is required.", nameof(tenantId));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void FinalizeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _global.Dispose();
        _shutdown.Dispose();
    }

    internal sealed class TenantState
    {
        public TenantState(int capacity)
        {
            Capacity = new SemaphoreSlim(capacity, capacity);
        }

        public SemaphoreSlim Capacity { get; }
        public int References { get; set; }
        public int Active { get; set; }
        public int Waiting { get; set; }
    }

    public sealed class TenantAdmissionLease : IAsyncDisposable, IDisposable
    {
        private TenantAdmissionController? _owner;
        private TenantState? _state;
        private readonly string _tenantId;

        internal TenantAdmissionLease(TenantAdmissionController owner, string tenantId, TenantState state)
        {
            _owner = owner;
            _tenantId = tenantId;
            _state = state;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var state = Interlocked.Exchange(ref _state, null);
            if (owner is not null && state is not null)
            {
                owner.Release(_tenantId, state);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class TenantAdmissionSnapshot
{
    internal TenantAdmissionSnapshot(int tenantCount, int activeRuns, int waitingRuns)
    {
        TenantCount = tenantCount;
        ActiveRuns = activeRuns;
        WaitingRuns = waitingRuns;
    }

    public int TenantCount { get; }
    public int ActiveRuns { get; }
    public int WaitingRuns { get; }
}
