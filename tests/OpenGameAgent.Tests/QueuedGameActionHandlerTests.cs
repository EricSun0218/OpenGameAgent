using System;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class QueuedGameActionHandlerTests
{
    [Fact]
    public async Task RequestsWaitForPumpAndRespectThePumpLimit()
    {
        var executed = new int[2];
        using var handler = new QueuedGameActionHandler(
            intent =>
            {
                executed[int.Parse(intent.OperationId, System.Globalization.CultureInfo.InvariantCulture) - 1]++;
                return GameActionReceipt.Committed(intent, "{}");
            },
            _ => null,
            capacity: 4);

        var first = handler.ExecuteAsync(Intent("1"), CancellationToken.None).AsTask();
        var second = handler.ExecuteAsync(Intent("2"), CancellationToken.None).AsTask();

        Assert.False(first.IsCompleted);
        Assert.Equal(2, handler.PendingCount);
        Assert.Equal(1, handler.Pump(1));
        Assert.Equal("1", (await first).OperationId);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, handler.PendingCount);

        Assert.Equal(1, handler.Pump());
        Assert.Equal("2", (await second).OperationId);
        Assert.Equal(new[] { 1, 1 }, executed);
    }

    [Fact]
    public async Task QueueCapacityIsBoundedAndQueuedCancellationFreesCapacity()
    {
        using var handler = new QueuedGameActionHandler(
            intent => GameActionReceipt.Committed(intent, "{}"),
            _ => null,
            capacity: 1);
        using var cancellation = new CancellationTokenSource();

        var cancelled = handler.ExecuteAsync(Intent("cancelled"), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, handler.PendingCount);

        var accepted = handler.ExecuteAsync(Intent("accepted"), CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<GameRuntimeLimitException>(async () =>
            await handler.ExecuteAsync(Intent("overflow"), CancellationToken.None).AsTask());

        handler.Pump();
        await accepted;
    }

    [Fact]
    public async Task StartedRequestIsNotCancelledWhenItsCallerTimesOut()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new QueuedGameActionHandler(
            intent =>
            {
                cancellation.Cancel();
                return GameActionReceipt.Committed(intent, "{}");
            },
            _ => null);

        var task = handler.ExecuteAsync(Intent("started"), cancellation.Token).AsTask();
        Assert.Equal(1, handler.Pump());

        var receipt = await task;
        Assert.Equal(GameActionStatus.Committed, receipt.Status);
    }

    [Fact]
    public async Task StopDoesNotCancelARequestAlreadyClaimedByPump()
    {
        QueuedGameActionHandler? handler = null;
        handler = new QueuedGameActionHandler(
            intent =>
            {
                handler!.Stop();
                return GameActionReceipt.Committed(intent, "{}");
            },
            _ => null);

        var task = handler.ExecuteAsync(Intent("shutdown-race"), CancellationToken.None).AsTask();
        Assert.Equal(1, handler.Pump());

        Assert.Equal(GameActionStatus.Committed, (await task).Status);
    }

    [Fact]
    public async Task RecoveryUsesTheSameHostThreadPump()
    {
        var pumpThread = Environment.CurrentManagedThreadId;
        var recoveryThread = -1;
        using var handler = new QueuedGameActionHandler(
            intent => GameActionReceipt.Committed(intent, "{}"),
            _ =>
            {
                recoveryThread = Environment.CurrentManagedThreadId;
                return null;
            });

        var task = handler.RecoverAsync(Intent("recover"), CancellationToken.None).AsTask();
        Assert.Equal(1, handler.Pump());

        Assert.Null(await task);
        Assert.Equal(pumpThread, recoveryThread);
    }

    [Fact]
    public async Task StopFailsPendingRequestsAndRejectsNewRequests()
    {
        using var handler = new QueuedGameActionHandler(
            intent => GameActionReceipt.Committed(intent, "{}"),
            _ => null);
        var pending = handler.ExecuteAsync(Intent("pending"), CancellationToken.None).AsTask();

        handler.Stop();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.ExecuteAsync(Intent("after-stop"), CancellationToken.None).AsTask());
        Assert.True(handler.IsStopped);
        Assert.Equal(0, handler.PendingCount);
    }

    [Fact]
    public async Task CallbackFailureDoesNotPreventLaterRequests()
    {
        using var handler = new QueuedGameActionHandler(
            intent =>
            {
                if (intent.OperationId == "fail")
                {
                    throw new InvalidOperationException("test failure");
                }

                return GameActionReceipt.Committed(intent, "{}");
            },
            _ => null);
        var failed = handler.ExecuteAsync(Intent("fail"), CancellationToken.None).AsTask();
        var completed = handler.ExecuteAsync(Intent("complete"), CancellationToken.None).AsTask();

        Assert.Equal(2, handler.Pump(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal(GameActionStatus.Committed, (await completed).Status);
    }

    [Fact]
    public async Task DurableDispatcherStillDeduplicatesQueuedOperations()
    {
        var executeCount = 0;
        using var handler = new QueuedGameActionHandler(
            intent =>
            {
                executeCount++;
                return GameActionReceipt.Committed(intent, "{}");
            },
            _ => null);
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var intent = Intent("duplicate");

        var first = dispatcher.ExecuteAsync(intent, CancellationToken.None).AsTask();
        var second = dispatcher.ExecuteAsync(intent, CancellationToken.None).AsTask();
        for (var attempt = 0; attempt < 100 && handler.PendingCount == 0; attempt++)
        {
            await Task.Yield();
        }

        Assert.Equal(1, handler.PendingCount);
        handler.Pump();

        Assert.Equal(GameActionStatus.Committed, (await first).Status);
        Assert.Equal(GameActionStatus.Committed, (await second).Status);
        Assert.Equal(1, executeCount);
    }

    private static GameActionIntent Intent(string operationId) =>
        new(
            operationId,
            "input",
            "session",
            "actor",
            "action",
            "{}",
            new GameMoment("world", 1));
}
