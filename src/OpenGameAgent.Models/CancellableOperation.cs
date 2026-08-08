namespace OpenGameAgent.Models;

internal static class CancellableOperation
{
    public static async ValueTask<T> WaitAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken)
    {
        var task = operation.AsTask();
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            canceled);
        if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
        {
            ObserveLateFailure(task);
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    public static async ValueTask WaitAsync(
        ValueTask operation,
        CancellationToken cancellationToken)
    {
        var task = operation.AsTask();
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            canceled);
        if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
        {
            ObserveLateFailure(task);
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private static void ObserveLateFailure(Task task) =>
        _ = task.ContinueWith(
            static failed => _ = failed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
