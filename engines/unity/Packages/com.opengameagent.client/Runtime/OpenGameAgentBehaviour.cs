using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OpenGameAgent.Unity
{
    /// <summary>Owns a client lifecycle and keeps public callbacks on Unity's main thread.</summary>
    public sealed class OpenGameAgentBehaviour : MonoBehaviour
    {
        [SerializeField] private string serverUrl = "http://127.0.0.1:4317/";
        private OpenGameAgentClient _client;
        private CancellationTokenSource _lifetime;
        private Func<string> _authenticationJsonProvider;
        private SynchronizationContext _unityContext;
        private int _unityThreadId;

        public event Action<GameAgentStreamEvent> EventReceived;
        public event Action<Exception> RequestFailed;

        public OpenGameAgentClient Client
        {
            get
            {
                EnsureClient();
                return _client;
            }
        }

        public void ConfigureAuthentication(Func<string> authenticationJsonProvider)
        {
            if (_client != null) throw new InvalidOperationException("Configure authentication before the client is first used.");
            _authenticationJsonProvider = authenticationJsonProvider;
        }

        public async Task RunAsync(string inputJson, CancellationToken cancellationToken = default(CancellationToken), string runId = null)
        {
            EnsureClient();
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken))
            {
                try
                {
                    await _client.RunAsync(inputJson, DispatchEventAsync, linked.Token, runId);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    await InvokeOnUnityThreadAsync(() =>
                    {
                        Action<Exception> failed = RequestFailed;
                        if (failed != null) failed(error);
                        else Debug.LogException(error, this);
                    });
                }
            }
        }

        private Task DispatchEventAsync(GameAgentStreamEvent item)
        {
            return InvokeOnUnityThreadAsync(() =>
            {
                Action<GameAgentStreamEvent> callback = EventReceived;
                if (callback != null) callback(item);
            });
        }

        private void Awake()
        {
            _unityContext = SynchronizationContext.Current;
            _unityThreadId = Thread.CurrentThread.ManagedThreadId;
            _lifetime = new CancellationTokenSource();
        }

        private Task InvokeOnUnityThreadAsync(Action callback)
        {
            if (_unityContext == null || Thread.CurrentThread.ManagedThreadId == _unityThreadId)
            {
                callback();
                return Task.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>();
            _unityContext.Post(_ =>
            {
                try
                {
                    callback();
                    completion.SetResult(true);
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            }, null);
            return completion.Task;
        }

        private void OnDestroy()
        {
            if (_lifetime != null)
            {
                _lifetime.Cancel();
                _lifetime.Dispose();
                _lifetime = null;
            }
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
        }

        private void EnsureClient()
        {
            if (_lifetime == null) _lifetime = new CancellationTokenSource();
            if (_client == null) _client = new OpenGameAgentClient(serverUrl, _authenticationJsonProvider);
        }
    }
}
