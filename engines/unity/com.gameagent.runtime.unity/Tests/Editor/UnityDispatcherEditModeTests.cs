using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameAgent.Unity.Tests
{
    public sealed class UnityDispatcherEditModeTests
    {
        [Test]
        public void BackgroundWorkRunsOnThePumpingThread()
        {
            using (var dispatcher = new UnityMainThreadDispatcher(8))
            {
                var expectedThread = Thread.CurrentThread.ManagedThreadId;
                var actualThread = 0;
                var pending = Task.Run(
                    async () => await dispatcher.InvokeAsync(
                        _ =>
                        {
                            actualThread =
                                Thread.CurrentThread.ManagedThreadId;
                            return new ValueTask<int>(7);
                        },
                        CancellationToken.None));

                Assert.That(
                    SpinWait.SpinUntil(
                        () => dispatcher.PendingCount == 1,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
                dispatcher.Pump(8, 10);

                Assert.That(pending.GetAwaiter().GetResult(), Is.EqualTo(7));
                Assert.That(actualThread, Is.EqualTo(expectedThread));
            }
        }

        [Test]
        public void BoundedQueueRejectsOverflow()
        {
            using (var dispatcher = new UnityMainThreadDispatcher(1))
            {
                var first = Task.Run(
                    async () => await dispatcher.InvokeAsync(
                        _ => new ValueTask<int>(1),
                        CancellationToken.None));
                Assert.That(
                    SpinWait.SpinUntil(
                        () => dispatcher.PendingCount == 1,
                        TimeSpan.FromSeconds(2)),
                    Is.True);

                var overflow = Task.Run(
                    async () => await dispatcher.InvokeAsync(
                        _ => new ValueTask<int>(2),
                        CancellationToken.None));
                Assert.Throws<UnityDispatcherQueueFullException>(
                    () => overflow.GetAwaiter().GetResult());

                dispatcher.Pump(1, 10);
                Assert.That(first.GetAwaiter().GetResult(), Is.EqualTo(1));
            }
        }

        [Test]
        public void CallerCancellationPreventsQueuedWorkFromRunning()
        {
            using (var dispatcher = new UnityMainThreadDispatcher(1))
            using (var cancellation = new CancellationTokenSource())
            {
                var invoked = false;
                var pending = Task.Run(
                    async () => await dispatcher.InvokeAsync(
                        _ =>
                        {
                            invoked = true;
                            return new ValueTask<int>(1);
                        },
                        cancellation.Token));
                Assert.That(
                    SpinWait.SpinUntil(
                        () => dispatcher.PendingCount == 1,
                        TimeSpan.FromSeconds(2)),
                    Is.True);

                cancellation.Cancel();
                Assert.Catch<OperationCanceledException>(
                    () => pending.GetAwaiter().GetResult());

                dispatcher.Pump(1, 10);
                Assert.That(invoked, Is.False);
                Assert.That(dispatcher.PendingCount, Is.Zero);
            }
        }
    }
}
