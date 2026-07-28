using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeEventBusTests
{
    [Fact]
    public async Task SynchronousReadsConsumeAvailabilitySignals()
    {
        using var bus = new BoundedRuntimeEventBus();
        bus.Publish(Event("event-1", EventDurabilities.Durable));

        Assert.True(bus.TryRead(out var first));
        Assert.Equal("event-1", first!.EventId);
        Assert.False(bus.TryRead(out _));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = bus.ReadAsync(timeout.Token).AsTask();
        Assert.False(pending.IsCompleted);

        bus.Publish(Event("event-2", EventDurabilities.Durable));
        var second = await pending;
        Assert.Equal("event-2", second.EventId);
    }

    [Fact]
    public async Task DisposeWakesAllPendingReaders()
    {
        var bus = new BoundedRuntimeEventBus(capacity: 2);
        var first = bus.ReadAsync(CancellationToken.None).AsTask();
        var second = bus.ReadAsync(CancellationToken.None).AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        bus.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => first.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Throws<ObjectDisposedException>(() => bus.TryRead(out _));

        bus.Publish(Event("ignored", EventDurabilities.Ephemeral));
    }

    [Fact]
    public void FullBusDropsIncomingEphemeralBeforeDurableHistory()
    {
        using var bus = new BoundedRuntimeEventBus(capacity: 1);
        bus.Publish(Event("durable", EventDurabilities.Durable));
        bus.Publish(Event("delta", EventDurabilities.Ephemeral));

        Assert.Equal(1, bus.DroppedEphemeralEvents);
        Assert.Equal(0, bus.DroppedDurableNotifications);
        Assert.True(bus.TryRead(out var remaining));
        Assert.Equal("durable", remaining!.EventId);
    }

    [Fact]
    public void FullBusDropsIncomingDurableNotificationAndTracksIt()
    {
        using var bus = new BoundedRuntimeEventBus(capacity: 1);
        bus.Publish(Event("old", EventDurabilities.Durable));
        bus.Publish(Event("new", EventDurabilities.Durable));

        Assert.Equal(0, bus.DroppedEphemeralEvents);
        Assert.Equal(1, bus.DroppedDurableNotifications);
        Assert.True(bus.TryRead(out var remaining));
        Assert.Equal("old", remaining!.EventId);
        Assert.False(bus.TryRead(out _));
    }

    [Fact]
    public void IncomingDurableNotificationEvictsQueuedEphemeralEvent()
    {
        using var bus = new BoundedRuntimeEventBus(capacity: 2);
        bus.Publish(Event("delta", EventDurabilities.Ephemeral));
        bus.Publish(Event("old", EventDurabilities.Durable));
        bus.Publish(Event("new", EventDurabilities.Durable));

        Assert.Equal(1, bus.DroppedEphemeralEvents);
        Assert.Equal(0, bus.DroppedDurableNotifications);
        Assert.True(bus.TryRead(out var first));
        Assert.Equal("old", first!.EventId);
        Assert.True(bus.TryRead(out var second));
        Assert.Equal("new", second!.EventId);
        Assert.False(bus.TryRead(out _));
    }

    [Fact]
    public async Task BufferedPublisherIsolatesABlockingObserver()
    {
        var observer = new BlockingPublisher();
        using var publisher = new BufferedRuntimeEventPublisher(
            observer,
            capacity: 1);

        publisher.Publish(Event("first", EventDurabilities.Durable));
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var started = System.Diagnostics.Stopwatch.StartNew();
        publisher.Publish(Event("second", EventDurabilities.Durable));
        publisher.Publish(Event("third", EventDurabilities.Durable));
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, publisher.DroppedDurableNotifications);
        observer.Release();
    }

    [Fact]
    public async Task BufferedPublisherContainsObserverExceptions()
    {
        using var publisher = new BufferedRuntimeEventPublisher(
            new ThrowingPublisher());

        publisher.Publish(Event("event", EventDurabilities.Durable));

        Assert.True(
            await WaitUntilAsync(
                () => publisher.PublisherFailures == 1,
                TimeSpan.FromSeconds(2)));
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static RuntimeEvent Event(string eventId, string durability)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = "run-1",
            Kind = RuntimeEventKinds.RunCheckpoint,
            Durability = durability,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement("""{"ok":true}""")
        };
    }

    private sealed class BlockingPublisher : IRuntimeEventPublisher
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(RuntimeEvent runtimeEvent)
        {
            Started.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class ThrowingPublisher : IRuntimeEventPublisher
    {
        public void Publish(RuntimeEvent runtimeEvent)
        {
            throw new InvalidOperationException("observer failed");
        }
    }
}
