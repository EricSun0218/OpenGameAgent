using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GameAgent.Unity
{
    internal sealed class UnityTerminalObserverQueue : IDisposable
    {
        private readonly ConcurrentQueue<Reservation> _ready =
            new ConcurrentQueue<Reservation>();
        private readonly object _gate = new object();
        private readonly int _capacity;
        private int _reserved;
        private int _unpublished;
        private int _accepting = 1;
        private int _disposed;
        private TaskCompletionSource<bool> _publishersDrained;

        internal UnityTerminalObserverQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        internal int ReservedCount
        {
            get { return Volatile.Read(ref _reserved); }
        }

        internal int PendingCount
        {
            get { return _ready.Count; }
        }

        internal bool TryReserve(out Reservation reservation)
        {
            lock (_gate)
            {
                if (_accepting == 0 || _reserved >= _capacity)
                {
                    reservation = null;
                    return false;
                }

                _reserved++;
                _unpublished++;
                reservation = new Reservation(this);
                return true;
            }
        }

        internal int Pump(
            int maxItems,
            double maxMilliseconds,
            Action<Exception> report)
        {
            if (maxItems < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxItems));
            }

            if (maxMilliseconds <= 0
                || double.IsNaN(maxMilliseconds)
                || double.IsInfinity(maxMilliseconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMilliseconds));
            }

            var processed = 0;
            var stopwatch = Stopwatch.StartNew();
            while (processed < maxItems)
            {
                if (processed != 0
                    && stopwatch.Elapsed.TotalMilliseconds
                    >= maxMilliseconds)
                {
                    break;
                }

                if (!_ready.TryDequeue(out var reservation))
                {
                    break;
                }

                reservation.Execute(report);
                processed++;
            }

            return processed;
        }

        internal Task StopAccepting()
        {
            lock (_gate)
            {
                _accepting = 0;
                if (_unpublished == 0)
                {
                    return Task.CompletedTask;
                }

                if (_publishersDrained == null)
                {
                    _publishersDrained = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return _publishersDrained.Task;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _accepting = 0;
                _disposed = 1;
            }

            while (_ready.TryDequeue(out var reservation))
            {
                reservation.DiscardPublished();
            }
        }

        private bool Publish(Reservation reservation)
        {
            TaskCompletionSource<bool> publishersDrained;
            var accepted = false;
            lock (_gate)
            {
                if (_unpublished <= 0)
                {
                    throw new InvalidOperationException(
                        "The Unity terminal publisher count underflowed.");
                }

                _unpublished--;
                if (_disposed == 0)
                {
                    _ready.Enqueue(reservation);
                    accepted = true;
                }

                publishersDrained = _unpublished == 0
                    ? _publishersDrained
                    : null;
            }

            if (publishersDrained != null)
            {
                publishersDrained.TrySetResult(true);
            }

            return accepted;
        }

        private void AbandonUnpublished()
        {
            TaskCompletionSource<bool> publishersDrained;
            lock (_gate)
            {
                if (_unpublished <= 0)
                {
                    throw new InvalidOperationException(
                        "The Unity terminal publisher count underflowed.");
                }

                if (_reserved <= 0)
                {
                    throw new InvalidOperationException(
                        "The Unity terminal-observer reservation count underflowed.");
                }

                _unpublished--;
                _reserved--;
                publishersDrained = _unpublished == 0
                    ? _publishersDrained
                    : null;
            }

            if (publishersDrained != null)
            {
                publishersDrained.TrySetResult(true);
            }
        }

        private void Release()
        {
            lock (_gate)
            {
                if (_reserved <= 0)
                {
                    throw new InvalidOperationException(
                        "The Unity terminal-observer reservation count underflowed.");
                }

                _reserved--;
            }
        }

        internal sealed class Reservation : IDisposable
        {
            private UnityTerminalObserverQueue _owner;
            private Action _observer;
            private int _state;

            internal Reservation(UnityTerminalObserverQueue owner)
            {
                _owner = owner;
            }

            internal bool Publish(Action observer)
            {
                if (observer == null)
                {
                    throw new ArgumentNullException(nameof(observer));
                }

                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                {
                    return false;
                }

                _observer = observer;
                var owner = Volatile.Read(ref _owner);
                if (owner != null && owner.Publish(this))
                {
                    return true;
                }

                DiscardPublished();
                return false;
            }

            internal void Execute(Action<Exception> report)
            {
                if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
                {
                    return;
                }

                try
                {
                    _observer();
                }
                catch (Exception exception)
                {
                    if (report != null)
                    {
                        report(exception);
                    }
                }
                finally
                {
                    ReleaseOwner();
                }
            }

            internal void DiscardPublished()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
                {
                    ReleaseOwner();
                }
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                {
                    _observer = null;
                    var owner = Interlocked.Exchange(ref _owner, null);
                    if (owner != null)
                    {
                        owner.AbandonUnpublished();
                    }
                }
            }

            private void ReleaseOwner()
            {
                _observer = null;
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    owner.Release();
                }
            }
        }
    }
}
