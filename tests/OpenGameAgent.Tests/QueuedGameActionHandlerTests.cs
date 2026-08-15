using System.Collections.Concurrent;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class QueuedGameActionHandlerTests
{
    [Fact]
    public async Task PumpRunsExecuteAndRecoverOnItsBoundThreadInFifoOrder()
    {
        var calls = new List<(string OperationId, string Kind, int ThreadId, string? GenerationId)>();
        var inner = new CallbackHandler(
            (intent, token) =>
            {
                calls.Add((intent.OperationId, "execute", Environment.CurrentManagedThreadId, intent.GenerationId));
                Assert.False(token.CanBeCanceled);
                return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}", 1));
            },
            (intent, token) =>
            {
                calls.Add((intent.OperationId, "recover", Environment.CurrentManagedThreadId, intent.GenerationId));
                Assert.False(token.CanBeCanceled);
                return new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(intent, "{}", 1));
            });
        using var handler = new QueuedGameActionHandler(inner);
        var pumpThread = Environment.CurrentManagedThreadId;

        var first = handler.ExecuteAsync(CreateIntent("operation-1"), CancellationToken.None).AsTask();
        var second = handler.RecoverAsync(CreateIntent("operation-2"), CancellationToken.None).AsTask();
        var third = handler.ExecuteAsync(CreateIntent("operation-3"), CancellationToken.None).AsTask();

        Assert.False(first.IsCompleted);
        Assert.Equal(2, handler.Pump(2));
        Assert.Equal(GameActionStatus.Committed, (await first).Status);
        Assert.Equal(GameActionStatus.Committed, (await second)!.Status);
        Assert.False(third.IsCompleted);
        Assert.Equal(1, handler.Pump(2));
        Assert.Equal(GameActionStatus.Committed, (await third).Status);
        Assert.Equal(
            new[]
            {
                ("operation-1", "execute", pumpThread, (string?)"save-generation-1"),
                ("operation-2", "recover", pumpThread, (string?)"save-generation-1"),
                ("operation-3", "execute", pumpThread, (string?)"save-generation-1"),
            },
            calls);
    }

    [Fact]
    public async Task QueuedCancellationRemovesWorkAndReleasesCapacity()
    {
        var executions = 0;
        var inner = new CallbackHandler(
            (intent, _) =>
            {
                executions++;
                return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}"));
            });
        using var handler = new QueuedGameActionHandler(inner, maximumPendingActions: 1);
        using var cancellation = new CancellationTokenSource();
        var canceled = handler.ExecuteAsync(CreateIntent("operation-canceled"), cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        Assert.Equal(0, handler.PendingCount);

        var accepted = handler.ExecuteAsync(CreateIntent("operation-accepted"), CancellationToken.None).AsTask();
        Assert.Equal(1, handler.Pump(1));
        Assert.Equal(GameActionStatus.Committed, (await accepted).Status);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task CallerCancellationAfterStartDoesNotCancelWorldMutation()
    {
        var completion = new TaskCompletionSource<GameActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var intent = CreateIntent("operation-started");
        var inner = new CallbackHandler(
            (_, token) =>
            {
                receivedToken = token;
                return new ValueTask<GameActionReceipt>(completion.Task);
            });
        using var handler = new QueuedGameActionHandler(inner);
        using var cancellation = new CancellationTokenSource();
        var action = handler.ExecuteAsync(intent, cancellation.Token).AsTask();

        Assert.Equal(1, handler.Pump(1));
        Assert.Equal(1, handler.ActiveCount);
        cancellation.Cancel();
        Assert.False(action.IsCompleted);
        Assert.False(receivedToken.CanBeCanceled);

        completion.SetResult(GameActionReceipt.Committed(intent, "{}", 2));
        Assert.Equal(GameActionStatus.Committed, (await action).Status);
        Assert.Equal(0, handler.ActiveCount);
    }

    [Fact]
    public async Task PendingAndActiveWorkAreIndependentlyBounded()
    {
        var completions = new ConcurrentDictionary<string, TaskCompletionSource<GameActionReceipt>>();
        var inner = new CallbackHandler(
            (intent, _) =>
            {
                var completion = new TaskCompletionSource<GameActionReceipt>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completions[intent.OperationId] = completion;
                return new ValueTask<GameActionReceipt>(completion.Task);
            });
        using var handler = new QueuedGameActionHandler(
            inner,
            maximumPendingActions: 2,
            maximumActiveActions: 1);

        var first = handler.ExecuteAsync(CreateIntent("operation-1"), CancellationToken.None).AsTask();
        var second = handler.ExecuteAsync(CreateIntent("operation-2"), CancellationToken.None).AsTask();
        Assert.Throws<GameRuntimeLimitException>(() =>
            handler.ExecuteAsync(CreateIntent("operation-overflow"), CancellationToken.None));

        Assert.Equal(1, handler.Pump(2));
        Assert.Equal(1, handler.ActiveCount);
        Assert.Equal(1, handler.PendingCount);
        Assert.Equal(0, handler.Pump(2));

        completions["operation-1"].SetResult(
            GameActionReceipt.Committed(CreateIntent("operation-1"), "{}"));
        Assert.True(SpinWait.SpinUntil(() => handler.ActiveCount == 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Pump(2));
        completions["operation-2"].SetResult(
            GameActionReceipt.Committed(CreateIntent("operation-2"), "{}"));
        Assert.Equal(GameActionStatus.Committed, (await first).Status);
        Assert.Equal(GameActionStatus.Committed, (await second).Status);
    }

    [Fact]
    public async Task HandlerFailureDoesNotBlockTheFollowingQueueItem()
    {
        var inner = new CallbackHandler(
            (intent, _) =>
            {
                if (intent.OperationId == "operation-failed")
                {
                    throw new InvalidOperationException("failed safely");
                }

                return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}"));
            });
        using var handler = new QueuedGameActionHandler(inner);
        var failed = handler.ExecuteAsync(CreateIntent("operation-failed"), CancellationToken.None).AsTask();
        var succeeded = handler.ExecuteAsync(CreateIntent("operation-succeeded"), CancellationToken.None).AsTask();

        Assert.Equal(2, handler.Pump(2));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await failed);
        Assert.Equal("failed safely", exception.Message);
        Assert.Equal(GameActionStatus.Committed, (await succeeded).Status);
    }

    [Fact]
    public async Task ConcurrentProducersCannotExceedPendingCapacity()
    {
        const int capacity = 32;
        var inner = new CallbackHandler(
            (intent, _) => new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}")));
        using var handler = new QueuedGameActionHandler(inner, maximumPendingActions: capacity);

        var submissions = await Task.WhenAll(
            Enumerable.Range(0, capacity * 4)
                .Select(index => Task.Run(() =>
                {
                    try
                    {
                        return (
                            Completion: (Task<GameActionReceipt>?)handler.ExecuteAsync(
                                CreateIntent($"operation-concurrent-{index}"),
                                CancellationToken.None).AsTask(),
                            Accepted: true);
                    }
                    catch (GameRuntimeLimitException)
                    {
                        return (Completion: (Task<GameActionReceipt>?)null, Accepted: false);
                    }
                })));

        var accepted = submissions
            .Where(static submission => submission.Accepted)
            .Select(static submission => submission.Completion!)
            .ToArray();
        Assert.Equal(capacity, accepted.Length);
        Assert.Equal(capacity, handler.PendingCount);
        Assert.Equal(capacity, handler.Pump(capacity));
        Assert.All(await Task.WhenAll(accepted), receipt => Assert.Equal(GameActionStatus.Committed, receipt.Status));
    }

    [Fact]
    public async Task PumpCannotBeReenteredByAnActionHandler()
    {
        QueuedGameActionHandler? handler = null;
        Exception? reentrantFailure = null;
        var inner = new CallbackHandler(
            (intent, _) =>
            {
                reentrantFailure = Record.Exception(() => handler!.Pump(1));
                return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}"));
            });
        handler = new QueuedGameActionHandler(inner);
        using (handler)
        {
            var action = handler.ExecuteAsync(CreateIntent("operation-reentrant"), CancellationToken.None).AsTask();
            Assert.Equal(1, handler.Pump(1));
            Assert.Equal(GameActionStatus.Committed, (await action).Status);
            Assert.IsType<InvalidOperationException>(reentrantFailure);
        }
    }

    [Fact]
    public async Task AsyncDisposeRejectsPendingWorkAndWaitsForStartedWork()
    {
        var intent = CreateIntent("operation-active");
        var completion = new TaskCompletionSource<GameActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new CallbackHandler(
            (current, _) => current.OperationId == intent.OperationId
                ? new ValueTask<GameActionReceipt>(completion.Task)
                : new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(current, "{}")));
        var handler = new QueuedGameActionHandler(inner);
        var active = handler.ExecuteAsync(intent, CancellationToken.None).AsTask();
        var pending = handler.ExecuteAsync(CreateIntent("operation-pending"), CancellationToken.None).AsTask();
        Assert.Equal(1, handler.Pump(1));

        var disposing = handler.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
        Assert.Throws<ObjectDisposedException>(() =>
            handler.ExecuteAsync(CreateIntent("operation-rejected"), CancellationToken.None));
        Assert.False(disposing.IsCompleted);
        Assert.False(handler.IsAccepting);

        completion.SetResult(GameActionReceipt.Committed(intent, "{}"));
        Assert.Equal(GameActionStatus.Committed, (await active).Status);
        await disposing;
        Assert.Equal(0, handler.ActiveCount);
    }

    [Fact]
    public void PumpRejectsASecondThreadAfterBinding()
    {
        using var handler = new QueuedGameActionHandler(new CallbackHandler());
        Assert.Equal(0, handler.Pump(1));

        Exception? exception = null;
        var secondThread = new Thread(() => exception = Record.Exception(() => handler.Pump(1)));
        secondThread.Start();
        Assert.True(secondThread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task DurableRestartRecoversDispatchedWorkWithoutReExecutingIt()
    {
        var journal = new InMemoryGameActionJournal();
        var firstInner = new CallbackHandler();
        var firstQueue = new QueuedGameActionHandler(firstInner);
        var firstDispatcher = new DurableGameActionDispatcher(journal, firstQueue);
        var intent = CreateIntent("operation-restart");
        var firstAttempt = firstDispatcher.ExecuteAsync(intent, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => firstQueue.PendingCount == 1);

        firstQueue.Stop();
        var uncertain = await firstAttempt;
        Assert.Equal(GameActionStatus.Uncertain, uncertain.Status);
        Assert.Equal(0, firstInner.ExecuteCount);

        var secondInner = new CallbackHandler(
            execute: null,
            recover: (current, _) =>
                new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(current, "{}", 3)));
        using var secondQueue = new QueuedGameActionHandler(secondInner);
        var restartedDispatcher = new DurableGameActionDispatcher(journal, secondQueue);
        var recovery = restartedDispatcher.ReconcileAsync(intent.OperationId, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => secondQueue.PendingCount == 1);

        Assert.Equal(1, secondQueue.Pump(1));
        Assert.Equal(GameActionStatus.Committed, (await recovery).Status);
        Assert.Equal(0, secondInner.ExecuteCount);
        Assert.Equal(1, secondInner.RecoverCount);
    }

    [Fact]
    public async Task CanceledDurableQueueItemIsRecoveredInsteadOfBlindlyExecuted()
    {
        var journal = new InMemoryGameActionJournal();
        var inner = new CallbackHandler(
            execute: null,
            recover: (intent, _) =>
                new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(intent, "{}", 4)));
        using var handler = new QueuedGameActionHandler(inner);
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var intent = CreateIntent("operation-cancel-recover");
        using var cancellation = new CancellationTokenSource();
        var firstAttempt = dispatcher.ExecuteAsync(intent, cancellation.Token).AsTask();
        await WaitUntilAsync(() => handler.PendingCount == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstAttempt);
        Assert.Equal(0, inner.ExecuteCount);
        Assert.Equal(0, handler.PendingCount);

        var recovery = dispatcher.ReconcileAsync(intent.OperationId, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => handler.PendingCount == 1);
        Assert.Equal(1, handler.Pump(1));
        Assert.Equal(GameActionStatus.Committed, (await recovery).Status);
        Assert.Equal(0, inner.ExecuteCount);
        Assert.Equal(1, inner.RecoverCount);
    }

    private static GameActionIntent CreateIntent(string operationId) =>
        new(
            operationId,
            "input-1",
            "session-1",
            "actor-1",
            "move",
            "{}",
            new GameMoment("timeline-1", 10),
            expectedRevision: 1,
            generationId: "save-generation-1");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class CallbackHandler : IGameActionHandler
    {
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>>? _execute;
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>>? _recover;
        private int _executeCount;
        private int _recoverCount;

        public CallbackHandler(
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>>? execute = null,
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>>? recover = null)
        {
            _execute = execute;
            _recover = recover;
        }

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public int RecoverCount => Volatile.Read(ref _recoverCount);

        public ValueTask<GameActionReceipt> ExecuteAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            return _execute is null
                ? throw new InvalidOperationException("Execute was not expected.")
                : _execute(intent, cancellationToken);
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _recoverCount);
            return _recover is null
                ? new ValueTask<GameActionReceipt?>((GameActionReceipt?)null)
                : _recover(intent, cancellationToken);
        }
    }
}
