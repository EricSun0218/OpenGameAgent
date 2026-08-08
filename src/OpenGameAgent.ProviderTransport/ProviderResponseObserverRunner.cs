using System.Runtime.CompilerServices;

namespace OpenGameAgent.ProviderTransport;

public enum ProviderResponseObserverOutcome
{
    NotConfigured,
    Completed,
    Failed,
    TimedOut,
    Suppressed,
}

public static class ProviderResponseObserverRunner
{
    public const int DefaultTimeoutMilliseconds = 500;
    public const int MaximumConcurrentObservers = 64;

    private static readonly ConditionalWeakTable<ProviderResponseObserver, ObserverState> States = new();
    private static int activeObservers;

    public static async ValueTask<ProviderResponseObserverOutcome> NotifyAsync(
        ProviderResponseObserver? observer,
        ProviderResponseObservation observation,
        int timeoutMilliseconds = DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        if (timeoutMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (observer is null)
        {
            return ProviderResponseObserverOutcome.NotConfigured;
        }

        var state = States.GetValue(observer, static _ => new ObserverState());
        if (!state.TryEnter())
        {
            return ProviderResponseObserverOutcome.Suppressed;
        }

        var active = Interlocked.Increment(ref activeObservers);
        if (active > MaximumConcurrentObservers)
        {
            Interlocked.Decrement(ref activeObservers);
            state.Exit();
            return ProviderResponseObserverOutcome.Suppressed;
        }

        var observerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observerTask = Task.Run(
            async () =>
            {
                try
                {
                    await observer(observation, observerCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    observerCancellation.Dispose();
                    Interlocked.Decrement(ref activeObservers);
                    state.Exit();
                }
            });

        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeoutMilliseconds, timeoutCancellation.Token);
        var callerCancellation = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerRegistration = cancellationToken.Register(
            static value => ((TaskCompletionSource<object?>)value!).TrySetResult(null),
            callerCancellation);
        var completed = await Task.WhenAny(observerTask, timeoutTask, callerCancellation.Task).ConfigureAwait(false);

        if (completed == observerTask)
        {
            timeoutCancellation.Cancel();
            try
            {
                await observerTask.ConfigureAwait(false);
                return ProviderResponseObserverOutcome.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return ProviderResponseObserverOutcome.Failed;
            }
        }

        TryCancel(observerCancellation);
        ObserveFailure(observerTask);
        cancellationToken.ThrowIfCancellationRequested();
        return ProviderResponseObserverOutcome.TimedOut;
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The observer completed between the race decision and cancellation.
        }
    }

    private static void ObserveFailure(Task task)
    {
        _ = ObserveFailureAsync(task);
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // A detached observer cannot affect the provider request.
        }
    }

    private sealed class ObserverState
    {
        private int active;

        public bool TryEnter() => Interlocked.CompareExchange(ref active, 1, 0) == 0;

        public void Exit() => Volatile.Write(ref active, 0);
    }
}
