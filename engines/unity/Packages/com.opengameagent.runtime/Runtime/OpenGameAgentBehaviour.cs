#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using OpenGameAgent.Client;

namespace OpenGameAgent.Unity
{

[AddComponentMenu("OpenGameAgent/Agent Runtime")]
public sealed class OpenGameAgentBehaviour : MonoBehaviour
{
    [Serializable]
    public sealed class StringPairEvent : UnityEvent<string, string>
    {
    }

    [SerializeField]
    private int _maximumActiveRuns = 64;

    [SerializeField]
    private int _maximumQueuedCallbacks = 4096;

    [SerializeField]
    private int _maximumCallbacksPerFrame = 256;

    [SerializeField]
    private StringPairEvent _runEvent = new();

    [SerializeField]
    private StringPairEvent _runCompleted = new();

    [SerializeField]
    private StringPairEvent _runFailed = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);
    private readonly Queue<QueuedCallback> _callbacks = new();
    private GameAgentRuntime? _runtime;
    private ServerGameAgentClient? _remoteClient;
    private int _configuredMaximumActiveRuns = 64;
    private int _configuredMaximumQueuedCallbacks = 4096;
    private int _configuredMaximumCallbacksPerFrame = 256;
    private int _queuedTerminalCallbacks;
    private bool _destroying;

    public StringPairEvent RunEvent => _runEvent;

    public StringPairEvent RunCompleted => _runCompleted;

    public StringPairEvent RunFailed => _runFailed;

    public void Configure(GameAgentRuntime runtime)
    {
        lock (_gate)
        {
            if (_runs.Count > 0 || _callbacks.Count > 0)
            {
                throw new InvalidOperationException("The runtime cannot be replaced while runs or queued callbacks remain.");
            }

            ValidateLimits();
            CaptureLimits();
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _remoteClient = null;
            _destroying = false;
        }
    }

    public void ConfigureRemote(ServerGameAgentClient client)
    {
        lock (_gate)
        {
            if (_runs.Count > 0 || _callbacks.Count > 0)
            {
                throw new InvalidOperationException("The client cannot be replaced while runs or queued callbacks remain.");
            }

            ValidateLimits();
            CaptureLimits();
            _remoteClient = client ?? throw new ArgumentNullException(nameof(client));
            _runtime = null;
            _destroying = false;
        }
    }

    public string RunJson(string inputJson)
    {
        var input = GameAgentWire.ParseInput(inputJson);
        Start(input);
        return input.InputId;
    }

    public Task<GameAgentRunResult> RunAsync(GameInput input, CancellationToken cancellationToken = default)
    {
        GameAgentRuntime runtime;
        lock (_gate)
        {
            runtime = _runtime ?? throw new InvalidOperationException("Configure the component before running an agent.");
        }

        return runtime.RunAsync(input, cancellationToken);
    }

    public Task<RemoteGameAgentResult> RunRemoteAsync(GameInput input, CancellationToken cancellationToken = default)
    {
        ServerGameAgentClient client;
        lock (_gate)
        {
            client = _remoteClient ?? throw new InvalidOperationException("Configure a remote client before running remotely.");
        }

        return client.RunAsync(input, cancellationToken);
    }

    public Task<bool> SteerActorAsync(
        string sessionId,
        string actorId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        GameAgentRuntime? runtime;
        ServerGameAgentClient? client;
        lock (_gate)
        {
            runtime = _runtime;
            client = _remoteClient;
        }

        var key = new GameSessionKey(sessionId, actorId);
        if (runtime is not null)
        {
            return Task.FromResult(runtime.TrySteer(
                key,
                OpenGameAgent.Kernel.AgentMessage.UserJson(payloadJson)));
        }

        return (client ?? throw new InvalidOperationException("Configure the component before steering an agent."))
            .SteerAsync(key, payloadJson, cancellationToken);
    }

    public Task<bool> AbortActorAsync(
        string sessionId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        GameAgentRuntime? runtime;
        ServerGameAgentClient? client;
        lock (_gate)
        {
            runtime = _runtime;
            client = _remoteClient;
        }

        var key = new GameSessionKey(sessionId, actorId);
        if (runtime is not null)
        {
            return Task.FromResult(runtime.TryAbort(key));
        }

        return (client ?? throw new InvalidOperationException("Configure the component before aborting an agent."))
            .AbortAsync(key, cancellationToken);
    }

    public bool Cancel(string inputId)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (!_runs.TryGetValue(inputId, out cancellation!))
            {
                return false;
            }
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Update()
    {
        PumpCallbacks();
    }

    public int PumpCallbacks()
    {
        var processed = 0;
        while (processed < _configuredMaximumCallbacksPerFrame)
        {
            QueuedCallback callback;
            lock (_gate)
            {
                if (_callbacks.Count == 0)
                {
                    break;
                }

                callback = _callbacks.Dequeue();
                if (callback.Kind != CallbackKind.Event)
                {
                    _queuedTerminalCallbacks--;
                }
            }

            processed++;
            switch (callback.Kind)
            {
                case CallbackKind.Event:
                    _runEvent.Invoke(callback.InputId, callback.Payload);
                    break;
                case CallbackKind.Completed:
                    _runCompleted.Invoke(callback.InputId, callback.Payload);
                    break;
                case CallbackKind.Failed:
                    _runFailed.Invoke(callback.InputId, callback.Payload);
                    break;
            }
        }

        return processed;
    }

    private void OnDestroy()
    {
        CancellationTokenSource[] active;
        lock (_gate)
        {
            _destroying = true;
            active = new CancellationTokenSource[_runs.Count];
            _runs.Values.CopyTo(active, 0);
            _runs.Clear();
            _callbacks.Clear();
            _queuedTerminalCallbacks = 0;
        }

        foreach (var cancellation in active)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Start(GameInput input)
    {
        GameAgentRuntime? runtime;
        ServerGameAgentClient? remoteClient;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_destroying)
            {
                throw new InvalidOperationException("The component is being destroyed.");
            }

            runtime = _runtime;
            remoteClient = _remoteClient;
            if (runtime is null && remoteClient is null)
            {
                throw new InvalidOperationException("Configure the component before running an agent.");
            }
            if (_runs.Count >= _configuredMaximumActiveRuns)
            {
                throw new GameRuntimeLimitException(nameof(_maximumActiveRuns), "The Unity component has too many active runs.");
            }

            if (_runs.Count + _queuedTerminalCallbacks >= _configuredMaximumQueuedCallbacks)
            {
                throw new GameRuntimeLimitException(nameof(_maximumQueuedCallbacks), "The Unity component cannot reserve another terminal callback.");
            }

            if (_runs.ContainsKey(input.InputId))
            {
                throw new InvalidOperationException("The input ID is already running.");
            }

            cancellation = new CancellationTokenSource();
            _runs.Add(input.InputId, cancellation);
        }

        _ = runtime is not null
            ? ExecuteLocalAsync(runtime, input, cancellation)
            : ExecuteRemoteAsync(remoteClient!, input, cancellation);
    }

    private async Task ExecuteLocalAsync(
        GameAgentRuntime runtime,
        GameInput input,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await runtime.RunAsync(
                input,
                (_, agentEvent, _) =>
                {
                    Enqueue(new QueuedCallback(
                        CallbackKind.Event,
                        input.InputId,
                        GameAgentWire.SerializeEvent(agentEvent)),
                        terminal: false);
                    return default;
                },
                cancellation.Token).ConfigureAwait(false);
            Enqueue(
                new QueuedCallback(CallbackKind.Completed, input.InputId, GameAgentWire.SerializeResult(result)),
                terminal: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Enqueue(new QueuedCallback(CallbackKind.Failed, input.InputId, "canceled"), terminal: true);
        }
        catch (Exception exception)
        {
            Enqueue(new QueuedCallback(CallbackKind.Failed, input.InputId, exception.Message), terminal: true);
        }
        finally
        {
            lock (_gate)
            {
                _runs.Remove(input.InputId);
            }

            cancellation.Dispose();
        }
    }

    private async Task ExecuteRemoteAsync(
        ServerGameAgentClient client,
        GameInput input,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await client.StreamAsync(
                input,
                (agentEvent, _) =>
                {
                    if (string.Equals(agentEvent.Name, "agent", StringComparison.Ordinal))
                    {
                        Enqueue(new QueuedCallback(CallbackKind.Event, input.InputId, agentEvent.Json), terminal: false);
                    }

                    return default;
                },
                cancellation.Token).ConfigureAwait(false);
            Enqueue(new QueuedCallback(CallbackKind.Completed, input.InputId, result.Json), terminal: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Enqueue(new QueuedCallback(CallbackKind.Failed, input.InputId, "canceled"), terminal: true);
        }
        catch (Exception exception)
        {
            Enqueue(new QueuedCallback(CallbackKind.Failed, input.InputId, exception.Message), terminal: true);
        }
        finally
        {
            lock (_gate)
            {
                _runs.Remove(input.InputId);
            }

            cancellation.Dispose();
        }
    }

    private void Enqueue(QueuedCallback callback, bool terminal)
    {
        lock (_gate)
        {
            if (_destroying)
            {
                return;
            }

            if (_callbacks.Count >= _configuredMaximumQueuedCallbacks)
            {
                if (!terminal)
                {
                    return;
                }

                DropOldestNonTerminalCallback();
            }

            _callbacks.Enqueue(callback);
            if (terminal)
            {
                _queuedTerminalCallbacks++;
            }
        }
    }

    private void DropOldestNonTerminalCallback()
    {
        var count = _callbacks.Count;
        var dropped = false;
        for (var index = 0; index < count; index++)
        {
            var queued = _callbacks.Dequeue();
            if (!dropped && queued.Kind == CallbackKind.Event)
            {
                dropped = true;
                continue;
            }

            _callbacks.Enqueue(queued);
        }

        if (!dropped)
        {
            throw new InvalidOperationException("The Unity callback queue has no non-terminal callback to replace.");
        }
    }

    private void ValidateLimits()
    {
        if (_maximumActiveRuns <= 0 || _maximumActiveRuns > 4096)
        {
            throw new InvalidOperationException("Maximum Active Runs must be between 1 and 4096.");
        }

        if (_maximumQueuedCallbacks <= 0 || _maximumQueuedCallbacks > 1_000_000)
        {
            throw new InvalidOperationException("Maximum Queued Callbacks must be between 1 and 1000000.");
        }

        if (_maximumQueuedCallbacks < _maximumActiveRuns)
        {
            throw new InvalidOperationException("Maximum Queued Callbacks must reserve one terminal callback per active run.");
        }

        if (_maximumCallbacksPerFrame <= 0 || _maximumCallbacksPerFrame > 100_000)
        {
            throw new InvalidOperationException("Maximum Callbacks Per Frame must be between 1 and 100000.");
        }
    }

    private void CaptureLimits()
    {
        _configuredMaximumActiveRuns = _maximumActiveRuns;
        _configuredMaximumQueuedCallbacks = _maximumQueuedCallbacks;
        _configuredMaximumCallbacksPerFrame = _maximumCallbacksPerFrame;
    }

    private enum CallbackKind
    {
        Event,
        Completed,
        Failed,
    }

    private readonly struct QueuedCallback
    {
        public QueuedCallback(CallbackKind kind, string inputId, string payload)
        {
            Kind = kind;
            InputId = inputId;
            Payload = payload;
        }

        public CallbackKind Kind { get; }

        public string InputId { get; }

        public string Payload { get; }
    }
}
}
