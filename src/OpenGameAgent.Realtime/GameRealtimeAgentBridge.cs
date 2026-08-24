using System.Text;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Realtime;

public delegate ValueTask<GameInput> RealtimeGameInputFactory(
    RealtimeHandoffRequest handoff,
    CancellationToken cancellationToken);

public sealed class GameRealtimeAgentBridgeOptions
{
    public int HandoffQueueCapacity { get; set; } = 32;

    public int MaximumHandoffCharacters { get; set; } = 1_000_000;

    public int HandoffFlushMilliseconds { get; set; } = 200;

    public int ShutdownTimeoutMilliseconds { get; set; } = 10_000;

    public bool SteerActiveRun { get; set; } = true;

    internal GameRealtimeAgentBridgeOptions Snapshot()
    {
        if (HandoffQueueCapacity is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(HandoffQueueCapacity));
        }

        if (MaximumHandoffCharacters is < 1 or > 4_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumHandoffCharacters));
        }

        if (HandoffFlushMilliseconds is < 10 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(nameof(HandoffFlushMilliseconds));
        }

        if (ShutdownTimeoutMilliseconds is < 100 or > 120_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeoutMilliseconds));
        }

        return (GameRealtimeAgentBridgeOptions)MemberwiseClone();
    }
}

/// <summary>
/// Runs realtime speech independently from the authoritative game agent loop. Handoffs start or
/// steer the actor loop, while model output is streamed back to speech in bounded time slices.
/// </summary>
public sealed class GameRealtimeAgentBridge : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly GameAgentRuntime _runtime;
    private readonly RealtimeConversationManager _conversation;
    private readonly GameSessionKey _key;
    private readonly RealtimeGameInputFactory _inputFactory;
    private readonly GameRealtimeAgentBridgeOptions _options;
    private readonly BoundedAsyncQueue<RealtimeHandoffRequest> _handoffs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _registration;
    private readonly Task _handoffPump;
    private ActiveRun? _active;
    private int _disposed;

    public GameRealtimeAgentBridge(
        GameAgentRuntime runtime,
        RealtimeConversationManager conversation,
        GameSessionKey key,
        RealtimeGameInputFactory inputFactory,
        GameRealtimeAgentBridgeOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        if (string.IsNullOrWhiteSpace(key.SessionId) || string.IsNullOrWhiteSpace(key.ActorId))
        {
            throw new ArgumentException("A valid session actor is required.", nameof(key));
        }
        _key = key;
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _options = (options ?? new GameRealtimeAgentBridgeOptions()).Snapshot();
        _handoffs = new BoundedAsyncQueue<RealtimeHandoffRequest>(_options.HandoffQueueCapacity);
        _registration = conversation.RegisterHandler(HandleRealtimeEventAsync);
        _handoffPump = Task.Run(PumpHandoffsAsync);
    }

    public bool HasActiveAgentRun
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    private async ValueTask HandleRealtimeEventAsync(
        RealtimeConversationEvent value,
        CancellationToken cancellationToken)
    {
        if (value.Kind != RealtimeConversationEventKind.HandoffRequested || value.Handoff is null)
        {
            return;
        }

        if (value.Handoff.ClientManaged)
        {
            return;
        }

        if (value.Handoff.Transcript.Length > _options.MaximumHandoffCharacters)
        {
            if (!value.Handoff.IsTranscriptTail)
            {
                await _conversation.CompleteHandoffAsync(
                        value.Handoff.HandoffId,
                        "The handoff request exceeded the configured limit.",
                        RealtimeHandoffPhase.Final,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        ActiveRun? active;
        lock (_gate)
        {
            active = _active;
        }

        if (_options.SteerActiveRun
            && active is not null
            && active.TryReadControlCoordinates(out var runId, out var turn)
            && _runtime.TrySteer(
                    _key,
                    AgentMessage.User(value.Handoff.Transcript),
                    runId,
                    turn)
                .Accepted)
        {
            if (!value.Handoff.IsTranscriptTail)
            {
                await _conversation.CompleteHandoffAsync(
                        value.Handoff.HandoffId,
                        string.Empty,
                        RealtimeHandoffPhase.Commentary,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        await _handoffs.EnqueueAsync(value.Handoff, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpHandoffsAsync()
    {
        try
        {
            while (await _handoffs.DequeueAsync(_lifetime.Token).ConfigureAwait(false) is { } handoff)
            {
                await RunHandoffAsync(handoff, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RunHandoffAsync(
        RealtimeHandoffRequest handoff,
        CancellationToken cancellationToken)
    {
        GameInput input;
        try
        {
            input = await AwaitWithCancellationAsync(
                    _inputFactory(handoff, cancellationToken).AsTask(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(input.SessionId, _key.SessionId, StringComparison.Ordinal)
                || !string.Equals(input.ActorId, _key.ActorId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The realtime input factory returned a different session actor.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!handoff.IsTranscriptTail)
            {
                await _conversation.CompleteHandoffAsync(
                        handoff.HandoffId,
                        Bound(exception.Message, 2_048),
                        RealtimeHandoffPhase.Final,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var active = new ActiveRun(
            handoff.HandoffId,
            _options.HandoffFlushMilliseconds,
            _conversation,
            emitOutput: !handoff.IsTranscriptTail,
            cancellationToken);
        lock (_gate)
        {
            _active = active;
        }

        try
        {
            var result = await _runtime.RunAsync(
                    input,
                    (currentInput, agentEvent, token) => ObserveAgentEventAsync(
                        active,
                        currentInput,
                        agentEvent,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            var tail = await active.StopAndDrainAsync().ConfigureAwait(false);
            var finalText = tail.Length > 0
                ? tail
                : FinalAssistantText(result.AgentResult);
            if (!result.Succeeded && finalText.Length == 0)
            {
                finalText = Bound(result.Error ?? "The game agent run failed.", 4_096);
            }

            if (!handoff.IsTranscriptTail)
            {
                await _conversation.CompleteHandoffAsync(
                        handoff.HandoffId,
                        finalText,
                        RealtimeHandoffPhase.Final,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!handoff.IsTranscriptTail)
            {
                await _conversation.CompleteHandoffAsync(
                        handoff.HandoffId,
                        Bound(exception.Message, 4_096),
                        RealtimeHandoffPhase.Final,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await active.StopAndDrainAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_active, active))
                {
                    _active = null;
                }
            }
        }
    }

    private ValueTask ObserveAgentEventAsync(
        ActiveRun active,
        GameInput input,
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(input.SessionId, _key.SessionId, StringComparison.Ordinal)
            || !string.Equals(input.ActorId, _key.ActorId, StringComparison.Ordinal))
        {
            return default;
        }

        active.Observe(agentEvent);
        if (agentEvent.Kind != AgentEventKind.MessageUpdated
            || agentEvent.ModelEvent?.Kind != ModelStreamEventKind.TextDelta
            || string.IsNullOrEmpty(agentEvent.ModelEvent.Delta))
        {
            return default;
        }

        active.Append(agentEvent.ModelEvent.Delta!);
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registration.Dispose();
        _lifetime.Cancel();
        _handoffs.Complete();
        ActiveRun? active;
        lock (_gate)
        {
            active = _active;
        }

        if (active is not null
            && active.TryReadControlCoordinates(out var runId, out var turn))
        {
            _runtime.TryAbort(_key, runId, turn);
        }
        try
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeoutMilliseconds);
            await AwaitWithCancellationAsync(_handoffPump, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private static string FinalAssistantText(AgentRunResult? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            result.NewMessages
                .Where(static message => message.Role == AgentRole.Assistant)
                .SelectMany(static message => message.Content)
                .OfType<TextContent>()
                .Select(static content => content.Text));
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value.Substring(0, maximum);

    private static async Task<T> AwaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        await AwaitWithCancellationAsync((Task)task, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    private static async Task AwaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private sealed class ActiveRun
    {
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private readonly int _flushMilliseconds;
        private readonly RealtimeConversationManager _conversation;
        private readonly bool _emitOutput;
        private readonly CancellationTokenSource _stop;
        private readonly Task _flushPump;
        private string? _runId;
        private int _turn;
        private bool _controlClosed;
        private int _stopped;

        public ActiveRun(
            string handoffId,
            int flushMilliseconds,
            RealtimeConversationManager conversation,
            bool emitOutput,
            CancellationToken cancellationToken)
        {
            HandoffId = handoffId;
            _flushMilliseconds = flushMilliseconds;
            _conversation = conversation;
            _emitOutput = emitOutput;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _flushPump = emitOutput ? Task.Run(FlushLoopAsync) : Task.CompletedTask;
        }

        public string HandoffId { get; }

        public void Observe(AgentEvent agentEvent)
        {
            lock (_gate)
            {
                if (_runId is null)
                {
                    if (agentEvent.Kind != AgentEventKind.RunStarted)
                    {
                        return;
                    }

                    _runId = agentEvent.RunId;
                }

                if (!string.Equals(_runId, agentEvent.RunId, StringComparison.Ordinal))
                {
                    return;
                }

                if (agentEvent.Turn > _turn)
                {
                    _turn = agentEvent.Turn;
                }

                if (agentEvent.Kind is AgentEventKind.RunFaulted or AgentEventKind.RunEnded)
                {
                    _controlClosed = true;
                }
            }
        }

        public bool TryReadControlCoordinates(out string runId, out int turn)
        {
            lock (_gate)
            {
                if (_controlClosed || _runId is null || _turn < 1)
                {
                    runId = string.Empty;
                    turn = 0;
                    return false;
                }

                runId = _runId;
                turn = _turn;
                return true;
            }
        }

        public void Append(string delta)
        {
            lock (_gate)
            {
                _buffer.Append(delta);
            }
        }

        public async ValueTask<string> StopAndDrainAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _stop.Cancel();
                try
                {
                    await _flushPump.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }

                _stop.Dispose();
            }

            lock (_gate)
            {
                return DrainCore();
            }
        }

        private async Task FlushLoopAsync()
        {
            while (true)
            {
                await Task.Delay(_flushMilliseconds, _stop.Token).ConfigureAwait(false);
                string chunk;
                lock (_gate)
                {
                    chunk = DrainCore();
                }

                if (chunk.Length > 0)
                {
                    await _conversation.SendHandoffProgressAsync(
                            HandoffId,
                            chunk,
                            RealtimeHandoffPhase.Commentary,
                            _stop.Token)
                        .ConfigureAwait(false);
                }
            }
        }

        private string DrainCore()
        {
            if (_buffer.Length == 0)
            {
                return string.Empty;
            }

            var value = _buffer.ToString();
            _buffer.Clear();
            return value;
        }
    }
}
