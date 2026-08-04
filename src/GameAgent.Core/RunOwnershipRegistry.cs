namespace GameAgent.Core;

public static class RunWorkloadCapacityReasonCodes
{
    public const string MaxActiveRuns = "max_active_runs";

    public const string MaxLanes = "max_lanes";

    public const string MaxWaitersPerLane = "max_waiters_per_lane";
}

public sealed class DuplicateRunException : InvalidOperationException
{
    public const string StableReasonCode = "duplicate_run";

    public DuplicateRunException(string runId)
        : base($"Run '{runId}' already has an active executor.")
    {
        RunId = runId;
    }

    public string RunId { get; }

    public string ReasonCode => StableReasonCode;
}

public sealed class RunWorkloadCapacityExceededException :
    InvalidOperationException
{
    public RunWorkloadCapacityExceededException(
        string reasonCode,
        int limit)
        : base(CapacityMessage(reasonCode, limit))
    {
        ReasonCode = reasonCode;
        Limit = limit;
    }

    public string ReasonCode { get; }

    public int Limit { get; }

    private static string CapacityMessage(string reasonCode, int limit)
    {
        return reasonCode switch
        {
            RunWorkloadCapacityReasonCodes.MaxActiveRuns =>
                $"The runtime reached its active-run limit of {limit}.",
            RunWorkloadCapacityReasonCodes.MaxLanes =>
                $"The runtime reached its ownership-lane limit of {limit}.",
            RunWorkloadCapacityReasonCodes.MaxWaitersPerLane =>
                $"An ownership lane reached its waiting-run limit of {limit}.",
            _ => throw new ArgumentException(
                "Unknown run workload capacity reason.",
                nameof(reasonCode))
        };
    }
}

public sealed class RunOwnershipLimits
{
    public const int DefaultMaxActiveRuns = 256;
    public const int DefaultMaxLanes = 256;
    public const int DefaultMaxWaitersPerLane = 64;

    public RunOwnershipLimits(
        int maxActiveRuns = DefaultMaxActiveRuns,
        int maxLanes = DefaultMaxLanes,
        int maxWaitersPerLane = DefaultMaxWaitersPerLane)
    {
        MaxActiveRuns = Positive(maxActiveRuns, nameof(maxActiveRuns));
        MaxLanes = Positive(maxLanes, nameof(maxLanes));
        MaxWaitersPerLane = Positive(
            maxWaitersPerLane,
            nameof(maxWaitersPerLane));
    }

    public int MaxActiveRuns { get; }

    public int MaxLanes { get; }

    public int MaxWaitersPerLane { get; }

    private static int Positive(int value, string parameterName)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed class RunOwnershipDiagnostics
{
    internal RunOwnershipDiagnostics(
        int activeRunCount,
        int waitingRunCount,
        int laneCount,
        RunOwnershipLimits limits)
    {
        ActiveRunCount = activeRunCount;
        WaitingRunCount = waitingRunCount;
        LaneCount = laneCount;
        Limits = limits;
    }

    public int ActiveRunCount { get; }

    public int WaitingRunCount { get; }

    public int LaneCount { get; }

    public RunOwnershipLimits Limits { get; }
}

public sealed class RunOwnershipRegistry
{
    private readonly object _sync = new();
    private readonly HashSet<string> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaneEntry> _lanes =
        new(StringComparer.Ordinal);
    private readonly RunOwnershipLimits _limits;
    private int _waitingRunCount;

    public RunOwnershipRegistry()
        : this(new RunOwnershipLimits())
    {
    }

    public RunOwnershipRegistry(RunOwnershipLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public int ActiveRunCount
    {
        get
        {
            lock (_sync)
            {
                return _activeRuns.Count;
            }
        }
    }

    public int WaitingRunCount
    {
        get
        {
            lock (_sync)
            {
                return _waitingRunCount;
            }
        }
    }

    public int LaneCount
    {
        get
        {
            lock (_sync)
            {
                return _lanes.Count;
            }
        }
    }

    public RunOwnershipDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            return new RunOwnershipDiagnostics(
                _activeRuns.Count,
                _waitingRunCount,
                _lanes.Count,
                _limits);
        }
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string runId,
        string laneId,
        CancellationToken cancellationToken)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        laneId = RuntimeGuard.RequiredUtf8(laneId, 256, nameof(laneId));
        cancellationToken.ThrowIfCancellationRequested();

        LaneEntry lane;
        var acquiredImmediately = false;
        lock (_sync)
        {
            if (_activeRuns.Contains(runId))
            {
                throw new DuplicateRunException(runId);
            }

            if (_activeRuns.Count >= _limits.MaxActiveRuns)
            {
                throw Capacity(
                    RunWorkloadCapacityReasonCodes.MaxActiveRuns,
                    _limits.MaxActiveRuns);
            }

            if (!_lanes.TryGetValue(laneId, out var existingLane))
            {
                if (_lanes.Count >= _limits.MaxLanes)
                {
                    throw Capacity(
                        RunWorkloadCapacityReasonCodes.MaxLanes,
                        _limits.MaxLanes);
                }

                lane = new LaneEntry();
                _lanes.Add(laneId, lane);
            }
            else
            {
                lane = existingLane;
            }

            acquiredImmediately = lane.Semaphore.Wait(0);
            if (!acquiredImmediately
                && lane.WaiterCount >= _limits.MaxWaitersPerLane)
            {
                throw Capacity(
                    RunWorkloadCapacityReasonCodes.MaxWaitersPerLane,
                    _limits.MaxWaitersPerLane);
            }

            if (!_activeRuns.Add(runId))
            {
                throw new InvalidOperationException(
                    "Run ownership admission is inconsistent.");
            }

            lane.ReferenceCount++;
            if (!acquiredImmediately)
            {
                lane.WaiterCount++;
                _waitingRunCount++;
            }
        }

        if (acquiredImmediately)
        {
            return new ValueTask<IAsyncDisposable>(
                new Lease(this, runId, laneId, lane));
        }

        return new ValueTask<IAsyncDisposable>(
            WaitForLaneAsync(
                runId,
                laneId,
                lane,
                cancellationToken));
    }

    private async Task<IAsyncDisposable> WaitForLaneAsync(
        string runId,
        string laneId,
        LaneEntry lane,
        CancellationToken cancellationToken)
    {
        try
        {
            await lane.Semaphore
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseWaitingReservation(runId, laneId, lane);
            throw;
        }

        lock (_sync)
        {
            if (lane.WaiterCount <= 0 || _waitingRunCount <= 0)
            {
                lane.Semaphore.Release();
                throw new InvalidOperationException(
                    "Run ownership waiter accounting is inconsistent.");
            }

            lane.WaiterCount--;
            _waitingRunCount--;
        }

        return new Lease(this, runId, laneId, lane);
    }

    private void ReleaseWaitingReservation(
        string runId,
        string laneId,
        LaneEntry lane)
    {
        var dispose = false;
        lock (_sync)
        {
            if (!_activeRuns.Remove(runId)
                || lane.WaiterCount <= 0
                || _waitingRunCount <= 0
                || lane.ReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "Run ownership cancellation accounting is inconsistent.");
            }

            lane.WaiterCount--;
            _waitingRunCount--;
            lane.ReferenceCount--;
            dispose = RemoveLaneIfUnreferenced(laneId, lane);
        }

        if (dispose)
        {
            lane.Semaphore.Dispose();
        }
    }

    private void ReleaseLease(
        string runId,
        string laneId,
        LaneEntry lane)
    {
        var dispose = false;
        lock (_sync)
        {
            if (!_activeRuns.Remove(runId)
                || lane.ReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "Run ownership lease accounting is inconsistent.");
            }

            lane.Semaphore.Release();
            lane.ReferenceCount--;
            dispose = RemoveLaneIfUnreferenced(laneId, lane);
        }

        if (dispose)
        {
            lane.Semaphore.Dispose();
        }
    }

    private bool RemoveLaneIfUnreferenced(
        string laneId,
        LaneEntry lane)
    {
        if (lane.ReferenceCount != 0)
        {
            return false;
        }

        if (lane.WaiterCount != 0
            || !_lanes.TryGetValue(laneId, out var current)
            || !ReferenceEquals(current, lane)
            || !_lanes.Remove(laneId))
        {
            throw new InvalidOperationException(
                "Run ownership lane accounting is inconsistent.");
        }

        return true;
    }

    private static RunWorkloadCapacityExceededException Capacity(
        string reasonCode,
        int limit)
    {
        return new RunWorkloadCapacityExceededException(reasonCode, limit);
    }

    private sealed class LaneEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public int WaiterCount { get; set; }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly RunOwnershipRegistry _owner;
        private readonly string _runId;
        private readonly string _laneId;
        private LaneEntry? _lane;

        public Lease(
            RunOwnershipRegistry owner,
            string runId,
            string laneId,
            LaneEntry lane)
        {
            _owner = owner;
            _runId = runId;
            _laneId = laneId;
            _lane = lane;
        }

        public ValueTask DisposeAsync()
        {
            var lane = Interlocked.Exchange(ref _lane, null);
            if (lane is not null)
            {
                _owner.ReleaseLease(_runId, _laneId, lane);
            }

            return default;
        }
    }
}
