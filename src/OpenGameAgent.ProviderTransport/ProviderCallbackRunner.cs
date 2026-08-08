namespace OpenGameAgent.ProviderTransport;

public static class ProviderCallbackRunner
{
    public static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pending = callback(cancellationToken);
        if (pending.IsCompletedSuccessfully)
        {
            return pending.Result;
        }

        var operation = pending.AsTask();
        if (operation.IsCompleted)
        {
            return await operation.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static value => ((TaskCompletionSource<object?>)value!).TrySetResult(null),
            canceled);
        if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) == operation)
        {
            return await operation.ConfigureAwait(false);
        }

        ObserveFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
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
            // A callback that outlives its caller cannot surface a late failure.
        }
    }
}
