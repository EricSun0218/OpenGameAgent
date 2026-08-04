using System;
using System.Threading;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Unity
{
    public sealed class UnityRuntimeEventPublisher :
        INonBlockingRuntimeEventPublisher
    {
        private readonly UnityMainThreadDispatcher _dispatcher;
        private readonly Action<RuntimeEvent> _publish;
        private long _droppedEvents;

        public UnityRuntimeEventPublisher(
            UnityMainThreadDispatcher dispatcher,
            Action<RuntimeEvent> publish)
        {
            _dispatcher = dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));
            _publish = publish
                ?? throw new ArgumentNullException(nameof(publish));
        }

        public long DroppedEvents
        {
            get { return Interlocked.Read(ref _droppedEvents); }
        }

        public void Publish(RuntimeEvent runtimeEvent)
        {
            if (runtimeEvent == null)
            {
                throw new ArgumentNullException(nameof(runtimeEvent));
            }

            RuntimeEvent snapshot;
            try
            {
                snapshot = ProtocolJson.DeserializeRuntimeEvent(
                    ProtocolJson.Serialize(runtimeEvent));
            }
            catch
            {
                Interlocked.Increment(ref _droppedEvents);
                return;
            }

            if (!_dispatcher.TryPost(() => _publish(snapshot)))
            {
                Interlocked.Increment(ref _droppedEvents);
            }
        }
    }
}
