namespace GameAgent.Core;

public static class ProviderWorkloadClasses
{
    public const string Interactive = "interactive";

    public const string Background = "background";

    internal static string Normalize(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(
                value,
                Interactive,
                StringComparison.Ordinal))
        {
            return Interactive;
        }

        if (string.Equals(
                value,
                Background,
                StringComparison.Ordinal))
        {
            return Background;
        }

        throw new ArgumentException(
            "The provider workload class is not supported.",
            parameterName);
    }
}

/// <summary>
/// Bounds provider calls while optionally reserving capacity for interactive
/// game work. It does not reorder runs or assign game importance.
/// </summary>
internal sealed class ProviderWorkloadAdmission : IDisposable
{
    private readonly SemaphoreSlim _all;
    private readonly SemaphoreSlim? _background;
    private readonly RuntimeMetricsEmitter? _metrics;
    private int _queued;
    private int _disposed;

    public ProviderWorkloadAdmission(
        int maximumConcurrentCalls,
        int? maximumConcurrentBackgroundCalls,
        RuntimeMetricsEmitter? metrics = null)
    {
        if (maximumConcurrentCalls < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentCalls));
        }

        if (maximumConcurrentBackgroundCalls.HasValue
            && (maximumConcurrentBackgroundCalls.Value < 1
                || maximumConcurrentBackgroundCalls.Value
                > maximumConcurrentCalls))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentBackgroundCalls));
        }

        _all = new SemaphoreSlim(
            maximumConcurrentCalls,
            maximumConcurrentCalls);
        _metrics = metrics;
        if (maximumConcurrentBackgroundCalls.HasValue
            && maximumConcurrentBackgroundCalls.Value
            < maximumConcurrentCalls)
        {
            _background = new SemaphoreSlim(
                maximumConcurrentBackgroundCalls.Value,
                maximumConcurrentBackgroundCalls.Value);
        }
    }

    public async ValueTask<Lease> AcquireAsync(
        string workloadClass,
        CancellationToken cancellationToken)
    {
        workloadClass = ProviderWorkloadClasses.Normalize(
            workloadClass,
            nameof(workloadClass));
        ThrowIfDisposed();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var queued = Interlocked.Increment(ref _queued);
        _metrics?.Record(
            RuntimeMetricNames.WorkloadQueueDepth,
            RuntimeMetricKind.Gauge,
            queued,
            workloadClass);
        var backgroundAcquired = false;
        try
        {
            if (_background is not null
                && string.Equals(
                    workloadClass,
                    ProviderWorkloadClasses.Background,
                    StringComparison.Ordinal))
            {
                await _background.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                backgroundAcquired = true;
            }

            await _all.WaitAsync(cancellationToken).ConfigureAwait(false);
            RecordWait(RuntimeMetricOutcomes.Success);
            return new Lease(
                _all,
                backgroundAcquired ? _background : null);
        }
        catch (OperationCanceledException)
        {
            RecordWait(RuntimeMetricOutcomes.Canceled);
            if (backgroundAcquired)
            {
                _background!.Release();
            }
            throw;
        }
        catch
        {
            RecordWait(RuntimeMetricOutcomes.Failure);
            if (backgroundAcquired)
            {
                _background!.Release();
            }

            throw;
        }

        void RecordWait(string outcome)
        {
            var remaining = Interlocked.Decrement(ref _queued);
            _metrics?.Record(
                RuntimeMetricNames.WorkloadQueueDepth,
                RuntimeMetricKind.Gauge,
                remaining,
                workloadClass);
            _metrics?.Record(
                RuntimeMetricNames.WorkloadQueueWaitMilliseconds,
                RuntimeMetricKind.Histogram,
                RuntimeMetricsEmitter.ElapsedMilliseconds(started),
                workloadClass,
                outcome);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _background?.Dispose();
        _all.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(ProviderWorkloadAdmission));
        }
    }

    internal sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _all;
        private SemaphoreSlim? _background;

        internal Lease(
            SemaphoreSlim all,
            SemaphoreSlim? background)
        {
            _all = all;
            _background = background;
        }

        public void Dispose()
        {
            var all = Interlocked.Exchange(ref _all, null);
            var background = Interlocked.Exchange(
                ref _background,
                null);
            if (all is null)
            {
                return;
            }

            try
            {
                all.Release();
            }
            catch (ObjectDisposedException)
            {
                // Runtime shutdown may finish before a quarantined transport.
            }

            if (background is not null)
            {
                try
                {
                    background.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Runtime shutdown may finish before a quarantined transport.
                }
            }
        }
    }
}
