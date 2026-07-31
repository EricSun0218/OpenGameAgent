using System.Diagnostics;

namespace GameAgent.Core;

public sealed class SystemRuntimeClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidRuntimeIdGenerator : IRuntimeIdGenerator
{
    public string NewId(string category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || category.Length > 64)
        {
            throw new ArgumentException(
                "Runtime id category is invalid.",
                nameof(category));
        }

        return category + "-" + Guid.NewGuid().ToString("N");
    }
}

internal sealed class MonotonicDeadline
{
    private readonly long _startedAt;
    private readonly TimeSpan _duration;

    private MonotonicDeadline(TimeSpan duration)
    {
        _duration = duration > TimeSpan.Zero
            ? duration
            : TimeSpan.Zero;
        _startedAt = Stopwatch.GetTimestamp();
    }

    public TimeSpan Remaining
    {
        get
        {
            if (_duration <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - _startedAt;
            if (elapsedTicks <= 0)
            {
                return _duration;
            }

            var elapsedSeconds =
                (double)elapsedTicks / Stopwatch.Frequency;
            var remainingSeconds =
                _duration.TotalSeconds - elapsedSeconds;
            return remainingSeconds > 0
                ? TimeSpan.FromSeconds(remainingSeconds)
                : TimeSpan.Zero;
        }
    }

    public static MonotonicDeadline Start(TimeSpan duration)
    {
        return new MonotonicDeadline(duration);
    }
}

internal sealed class BoundedCancellationDispatcher
{
    internal const int DefaultCapacity = 64;

    private readonly SemaphoreSlim _capacity;
    private int _reservations;

    public BoundedCancellationDispatcher(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
    }

    public static BoundedCancellationDispatcher Shared { get; } = new();

    public static BoundedCancellationDispatcher LifecycleShared { get; } =
        new();

    public static BoundedCancellationDispatcher SkillContentResolverShared
    {
        get;
    } = new();

    internal int ActiveReservations =>
        Volatile.Read(ref _reservations);

    public bool TryReserve(
        out CancellationDispatchReservation? reservation)
    {
        if (!_capacity.Wait(0))
        {
            reservation = null;
            return false;
        }

        Interlocked.Increment(ref _reservations);
        reservation = new CancellationDispatchReservation(this);
        return true;
    }

    private void Release()
    {
        Interlocked.Decrement(ref _reservations);
        _capacity.Release();
    }

    internal sealed class CancellationDispatchReservation :
        IDisposable
    {
        private readonly BoundedCancellationDispatcher _owner;
        private int _state;

        internal CancellationDispatchReservation(
            BoundedCancellationDispatcher owner)
        {
            _owner = owner;
        }

        public Task DispatchAsync(CancellationTokenSource cancellation)
        {
            if (cancellation is null)
            {
                throw new ArgumentNullException(nameof(cancellation));
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A cancellation reservation can be dispatched only once.");
            }

            try
            {
                return Task.Run(() => SafeCancel(cancellation));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Task<bool> DispatchAsync(Func<bool> cancellation)
        {
            if (cancellation is null)
            {
                throw new ArgumentNullException(nameof(cancellation));
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A cancellation reservation can be dispatched only once.");
            }

            try
            {
                return Task.Run(
                    () =>
                    {
                        try
                        {
                            return cancellation();
                        }
                        catch
                        {
                            return false;
                        }
                    });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _state, 2);
            if (previous != 2)
            {
                _owner.Release();
            }
        }

        private static void SafeCancel(
            CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch
            {
                // Host callbacks cannot escape the isolated cancellation
                // worker or consume additional dispatcher reservations.
            }
        }
    }
}
