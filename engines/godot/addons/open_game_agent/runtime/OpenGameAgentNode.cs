#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenGameAgent.Client;

namespace OpenGameAgent.Godot;

[GlobalClass]
public partial class OpenGameAgentNode : Node
{
    public static readonly StringName RunEventSignal = new("run_event");
    public static readonly StringName RunCompletedSignal = new("run_completed");
    public static readonly StringName RunFailedSignal = new("run_failed");

    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);
    private readonly Queue<QueuedSignal> _signals = new();
    private GameAgentRuntime? _runtime;
    private ServerGameAgentClient? _remoteClient;
    private int _maximumConcurrentRuns = 64;
    private int _maximumQueuedSignals = 4096;
    private int _maximumSignalsPerFrame = 256;
    private int _queuedTerminalSignals;
    private bool _exiting;

    public override void _Ready()
    {
        AddUserSignal(RunEventSignal, new global::Godot.Collections.Array
        {
            SignalArgument("input_id", Variant.Type.String),
            SignalArgument("event_json", Variant.Type.String),
        });
        AddUserSignal(RunCompletedSignal, new global::Godot.Collections.Array
        {
            SignalArgument("input_id", Variant.Type.String),
            SignalArgument("result_json", Variant.Type.String),
        });
        AddUserSignal(RunFailedSignal, new global::Godot.Collections.Array
        {
            SignalArgument("input_id", Variant.Type.String),
            SignalArgument("error", Variant.Type.String),
        });
    }

    public void Configure(
        GameAgentRuntime runtime,
        int maximumConcurrentRuns = 64,
        int maximumQueuedSignals = 4096,
        int maximumSignalsPerFrame = 256)
    {
        ValidateLimits(maximumConcurrentRuns, maximumQueuedSignals, maximumSignalsPerFrame);

        lock (_gate)
        {
            if (_runs.Count > 0 || _signals.Count > 0)
            {
                throw new InvalidOperationException("The runtime cannot be replaced while runs or queued signals remain.");
            }

            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _remoteClient = null;
            _maximumConcurrentRuns = maximumConcurrentRuns;
            _maximumQueuedSignals = maximumQueuedSignals;
            _maximumSignalsPerFrame = maximumSignalsPerFrame;
            _exiting = false;
        }
    }

    public void ConfigureRemote(
        ServerGameAgentClient client,
        int maximumConcurrentRuns = 64,
        int maximumQueuedSignals = 4096,
        int maximumSignalsPerFrame = 256)
    {
        ValidateLimits(maximumConcurrentRuns, maximumQueuedSignals, maximumSignalsPerFrame);

        lock (_gate)
        {
            if (_runs.Count > 0 || _signals.Count > 0)
            {
                throw new InvalidOperationException("The client cannot be replaced while runs or queued signals remain.");
            }

            _remoteClient = client ?? throw new ArgumentNullException(nameof(client));
            _runtime = null;
            _maximumConcurrentRuns = maximumConcurrentRuns;
            _maximumQueuedSignals = maximumQueuedSignals;
            _maximumSignalsPerFrame = maximumSignalsPerFrame;
            _exiting = false;
        }
    }

    public override void _Process(double delta)
    {
        _ = delta;
        var processed = 0;
        while (processed < _maximumSignalsPerFrame)
        {
            QueuedSignal queued;
            lock (_gate)
            {
                if (_signals.Count == 0)
                {
                    break;
                }

                queued = _signals.Dequeue();
                if (queued.Terminal)
                {
                    _queuedTerminalSignals--;
                }
            }

            processed++;
            EmitSignal(queued.Signal, queued.InputId, queued.Payload);
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
            runtime = _runtime ?? throw new InvalidOperationException("Configure the node before running an agent.");
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

        return (client ?? throw new InvalidOperationException("Configure the node before steering an agent."))
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

        return (client ?? throw new InvalidOperationException("Configure the node before aborting an agent."))
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

    public override void _ExitTree()
    {
        CancellationTokenSource[] active;
        lock (_gate)
        {
            _exiting = true;
            active = new CancellationTokenSource[_runs.Count];
            _runs.Values.CopyTo(active, 0);
            _runs.Clear();
            _signals.Clear();
            _queuedTerminalSignals = 0;
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
            if (_exiting)
            {
                throw new InvalidOperationException("The node is leaving the scene tree.");
            }

            runtime = _runtime;
            remoteClient = _remoteClient;
            if (runtime is null && remoteClient is null)
            {
                throw new InvalidOperationException("Configure the node before running an agent.");
            }
            if (_runs.Count >= _maximumConcurrentRuns)
            {
                throw new GameRuntimeLimitException(nameof(_maximumConcurrentRuns), "The Godot node has too many active runs.");
            }

            if (_runs.Count + _queuedTerminalSignals >= _maximumQueuedSignals)
            {
                throw new GameRuntimeLimitException(nameof(_maximumQueuedSignals), "The Godot node cannot reserve another terminal signal.");
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
                    QueueSignal(RunEventSignal, input.InputId, GameAgentWire.SerializeEvent(agentEvent), terminal: false);
                    return default;
                },
                cancellation.Token).ConfigureAwait(false);
            QueueSignal(RunCompletedSignal, input.InputId, GameAgentWire.SerializeResult(result), terminal: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            QueueSignal(RunFailedSignal, input.InputId, "canceled", terminal: true);
        }
        catch (Exception exception)
        {
            QueueSignal(RunFailedSignal, input.InputId, exception.Message, terminal: true);
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
                        QueueSignal(RunEventSignal, input.InputId, agentEvent.Json, terminal: false);
                    }

                    return default;
                },
                cancellation.Token).ConfigureAwait(false);
            QueueSignal(RunCompletedSignal, input.InputId, result.Json, terminal: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            QueueSignal(RunFailedSignal, input.InputId, "canceled", terminal: true);
        }
        catch (Exception exception)
        {
            QueueSignal(RunFailedSignal, input.InputId, exception.Message, terminal: true);
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

    private void QueueSignal(StringName signal, string inputId, string payload, bool terminal)
    {
        lock (_gate)
        {
            if (_exiting)
            {
                return;
            }

            if (_signals.Count >= _maximumQueuedSignals)
            {
                if (!terminal)
                {
                    return;
                }

                DropOldestNonTerminalSignal();
            }

            _signals.Enqueue(new QueuedSignal(signal, inputId, payload, terminal));
            if (terminal)
            {
                _queuedTerminalSignals++;
            }
        }
    }

    private void DropOldestNonTerminalSignal()
    {
        var count = _signals.Count;
        var dropped = false;
        for (var index = 0; index < count; index++)
        {
            var queued = _signals.Dequeue();
            if (!dropped && !queued.Terminal)
            {
                dropped = true;
                continue;
            }

            _signals.Enqueue(queued);
        }

        if (!dropped)
        {
            throw new InvalidOperationException("The Godot signal queue has no non-terminal signal to replace.");
        }
    }

    private static void ValidateLimits(
        int maximumConcurrentRuns,
        int maximumQueuedSignals,
        int maximumSignalsPerFrame)
    {
        if (maximumConcurrentRuns <= 0 || maximumConcurrentRuns > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRuns));
        }

        if (maximumQueuedSignals <= 0 || maximumQueuedSignals > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedSignals));
        }

        if (maximumQueuedSignals < maximumConcurrentRuns)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedSignals), "The signal queue must reserve one terminal signal per active run.");
        }

        if (maximumSignalsPerFrame <= 0 || maximumSignalsPerFrame > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSignalsPerFrame));
        }
    }

    private static global::Godot.Collections.Dictionary SignalArgument(string name, Variant.Type type) => new()
    {
        ["name"] = name,
        ["type"] = (int)type,
    };

    private readonly struct QueuedSignal
    {
        public QueuedSignal(StringName signal, string inputId, string payload, bool terminal)
        {
            Signal = signal;
            InputId = inputId;
            Payload = payload;
            Terminal = terminal;
        }

        public StringName Signal { get; }

        public string InputId { get; }

        public string Payload { get; }

        public bool Terminal { get; }
    }
}
