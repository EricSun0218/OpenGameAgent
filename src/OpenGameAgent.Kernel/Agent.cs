using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Kernel;

public delegate ValueTask AgentEventHandler(AgentEvent agentEvent, CancellationToken cancellationToken);

public sealed class Agent
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private IModelProvider _provider;
    private readonly AgentLimits _limits;
    private AgentHooks _hooks;
    private ModelParameters _parameters;
    private ToolExecutionMode _toolExecution;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _runIdFactory;
    private string? _sessionId;
    private readonly PendingMessageQueue _steering;
    private readonly PendingMessageQueue _followUps;
    private readonly List<Subscriber> _subscribers = new();
    private readonly List<AgentMessage> _messages;
    private readonly List<AgentTool> _tools;
    private readonly HashSet<string> _pendingToolCallIds = new(StringComparer.Ordinal);
    private readonly List<string> _runSubscriberErrors = new();
    private long _nextSubscriberId;
    private string _systemPrompt;
    private string _model;
    private bool _isRunning;
    private bool _acceptsActiveControl;
    private AgentMessage? _streamingMessage;
    private ModelStreamEvent? _streamingEvent;
    private string? _error;
    private CancellationTokenSource? _activeCancellation;
    private Task<AgentRunResult>? _activeTask;

    public Agent(AgentOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _limits = options.Limits?.Copy()
            ?? throw new ArgumentException("Agent limits are required.", nameof(options));
        ValidateQueueMode(options.SteeringMode, nameof(options.SteeringMode));
        ValidateQueueMode(options.FollowUpMode, nameof(options.FollowUpMode));
        ValidateToolExecutionMode(options.ToolExecution, nameof(options.ToolExecution));
        AgentValidator.ValidateOptions(
            options.Model,
            options.SessionId,
            options.Parameters,
            _limits,
            options.Clock,
            options.RunIdFactory);
        _provider = options.Provider;
        _model = options.Model;
        _systemPrompt = options.SystemPrompt
            ?? throw new ArgumentException("A system prompt value is required.", nameof(options));
        _sessionId = options.SessionId;
        _parameters = options.Parameters.Copy();
        _hooks = CopyHooks(options.Hooks
            ?? throw new ArgumentException("Agent hooks are required.", nameof(options)));
        _toolExecution = options.ToolExecution;
        _clock = options.Clock;
        _runIdFactory = options.RunIdFactory;
        _messages = options.InitialMessages.ToList();
        _tools = options.Tools.ToList();
        _steering = new PendingMessageQueue(options.SteeringMode, _limits.MaxQueuedMessages);
        _followUps = new PendingMessageQueue(options.FollowUpMode, _limits.MaxQueuedMessages);
        AgentValidator.ValidateContext(new AgentContext(_systemPrompt, _messages, _tools), _limits);
    }

    public AgentState State
    {
        get
        {
            lock (_gate)
            {
                return new AgentState(
                    _systemPrompt,
                    _provider,
                    _model,
                    _sessionId,
                    _parameters,
                    _tools,
                    _messages,
                    _isRunning,
                    _streamingMessage,
                    _streamingEvent,
                    _pendingToolCallIds,
                    _error);
            }
        }
    }

    public QueueMode SteeringMode
    {
        get => _steering.Mode;
        set
        {
            ValidateQueueMode(value, nameof(value));
            _steering.Mode = value;
        }
    }

    public QueueMode FollowUpMode
    {
        get => _followUps.Mode;
        set
        {
            ValidateQueueMode(value, nameof(value));
            _followUps.Mode = value;
        }
    }

    public IDisposable Subscribe(AgentEventHandler handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        long id;
        lock (_gate)
        {
            if (_subscribers.Count >= _limits.MaxSubscribers)
            {
                throw new InvalidOperationException($"The agent already has the maximum of {_limits.MaxSubscribers} subscribers.");
            }

            id = ++_nextSubscriberId;
            _subscribers.Add(new Subscriber(id, handler));
        }

        return new Subscription(this, id);
    }

    public Task<AgentRunResult> RunAsync(string text, CancellationToken cancellationToken = default) =>
        RunAsync(new[] { AgentMessage.User(text, _clock()) }, cancellationToken);

    public Task<AgentRunResult> RunAsync(AgentMessage message, CancellationToken cancellationToken = default) =>
        RunAsync(new[] { message ?? throw new ArgumentNullException(nameof(message)) }, cancellationToken);

    public Task<AgentRunResult> RunAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var copied = messages.ToArray();
        AgentValidator.ValidateMessages(copied, _limits);
        return StartRun(
            (context, options, emit, token) => AgentLoop.RunAsync(copied, context, options, emit, token),
            cancellationToken);
    }

    public Task<AgentRunResult> ContinueAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureIdle();
            if (_messages.Count == 0)
            {
                throw new InvalidOperationException("Cannot continue because the transcript is empty.");
            }

            if (_messages[_messages.Count - 1].Role == AgentRole.Assistant)
            {
                IReadOnlyList<AgentMessage> queuedPrompts = _steering.Drain();
                if (queuedPrompts.Count > 0)
                {
                    return StartRun(
                        (context, options, emit, token) => AgentLoop.RunQueuedAsync(queuedPrompts, context, options, emit, token),
                        cancellationToken);
                }

                queuedPrompts = _followUps.Drain();
                if (queuedPrompts.Count == 0)
                {
                    throw new InvalidOperationException("Cannot continue from an assistant message without queued input.");
                }

                return StartRun(
                    (context, options, emit, token) => AgentLoop.RunAsync(queuedPrompts, context, options, emit, token),
                    cancellationToken);
            }

            return StartRun(
                (context, options, emit, token) => AgentLoop.ContinueAsync(context, options, emit, token),
                cancellationToken);
        }
    }

    public void Steer(AgentMessage message)
    {
        AgentValidator.ValidateMessage(message ?? throw new ArgumentNullException(nameof(message)), _limits);
        _steering.Enqueue(message);
    }

    public void Steer(string text) => Steer(AgentMessage.User(text, _clock()));

    public bool TrySteer(AgentMessage message)
    {
        AgentValidator.ValidateMessage(message ?? throw new ArgumentNullException(nameof(message)), _limits);
        lock (_gate)
        {
            if (!_isRunning || !_acceptsActiveControl)
            {
                return false;
            }

            _steering.Enqueue(message);
            return true;
        }
    }

    public bool TrySteer(string text) => TrySteer(AgentMessage.User(text, _clock()));

    public void FollowUp(AgentMessage message)
    {
        AgentValidator.ValidateMessage(message ?? throw new ArgumentNullException(nameof(message)), _limits);
        _followUps.Enqueue(message);
    }

    public void FollowUp(string text) => FollowUp(AgentMessage.User(text, _clock()));

    public void ClearSteeringQueue() => _steering.Clear();

    public void ClearFollowUpQueue() => _followUps.Clear();

    public void ClearQueues()
    {
        _steering.Clear();
        _followUps.Clear();
    }

    public bool HasQueuedMessages => _steering.Count > 0 || _followUps.Count > 0;

    public void Abort()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _activeCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run completion won the race after the cancellation source was captured.
        }
        catch (AggregateException)
        {
            // A cancellation callback cannot prevent the abort request from being recorded.
        }
    }

    public bool TryAbort()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (!_isRunning || !_acceptsActiveControl)
            {
                return false;
            }

            cancellation = _activeCancellation;
        }

        try
        {
            cancellation?.Cancel();
            return cancellation is not null;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            // Cancellation was requested even though a callback failed.
            return true;
        }
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _activeTask ?? Task.CompletedTask;
        }
    }

    public string? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _sessionId;
            }
        }
    }

    public void SetSessionId(string? sessionId)
    {
        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateOptions(_model, sessionId, _parameters, _limits, _clock, _runIdFactory);
            _sessionId = sessionId;
        }
    }

    public void SetModel(string model)
    {
        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateOptions(model, _sessionId, _parameters, _limits, _clock, _runIdFactory);
            _model = model;
        }
    }

    public void SetModel(IModelProvider provider, string model)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateOptions(model, _sessionId, _parameters, _limits, _clock, _runIdFactory);
            _provider = provider;
            _model = model;
        }
    }

    public void SetModelParameters(ModelParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateOptions(_model, _sessionId, parameters, _limits, _clock, _runIdFactory);
            _parameters = parameters.Copy();
        }
    }

    public void SetHooks(AgentHooks hooks)
    {
        if (hooks is null)
        {
            throw new ArgumentNullException(nameof(hooks));
        }

        lock (_gate)
        {
            EnsureIdle();
            _hooks = CopyHooks(hooks);
        }
    }

    public void SetToolExecution(ToolExecutionMode toolExecution)
    {
        ValidateToolExecutionMode(toolExecution, nameof(toolExecution));
        lock (_gate)
        {
            EnsureIdle();
            _toolExecution = toolExecution;
        }
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        if (systemPrompt is null)
        {
            throw new ArgumentNullException(nameof(systemPrompt));
        }

        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateContext(new AgentContext(systemPrompt, _messages, _tools), _limits);
            _systemPrompt = systemPrompt;
        }
    }

    public void SetTools(IEnumerable<AgentTool> tools)
    {
        var copied = tools?.ToList() ?? throw new ArgumentNullException(nameof(tools));
        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateContext(new AgentContext(_systemPrompt, _messages, copied), _limits);
            _tools.Clear();
            _tools.AddRange(copied);
        }
    }

    public void ReplaceMessages(IEnumerable<AgentMessage> messages)
    {
        var copied = messages?.ToList() ?? throw new ArgumentNullException(nameof(messages));
        lock (_gate)
        {
            EnsureIdle();
            AgentValidator.ValidateContext(new AgentContext(_systemPrompt, copied, _tools), _limits);
            _messages.Clear();
            _messages.AddRange(copied);
            _error = null;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            EnsureIdle();
            _messages.Clear();
            _pendingToolCallIds.Clear();
            _streamingMessage = null;
            _streamingEvent = null;
            _error = null;
            _steering.Clear();
            _followUps.Clear();
        }
    }

    private Task<AgentRunResult> StartRun(
        Func<
            AgentContext,
            AgentLoopOptions,
            Func<AgentEvent, CancellationToken, ValueTask>,
            CancellationToken,
            Task<AgentRunResult>> operation,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureIdle();
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isRunning = true;
            _acceptsActiveControl = true;
            _streamingMessage = null;
            _streamingEvent = null;
            _error = null;
            _pendingToolCallIds.Clear();
            _runSubscriberErrors.Clear();
            var context = new AgentContext(_systemPrompt, _messages, _tools);
            var options = CreateLoopOptions();
            _activeTask = ExecuteRunAsync(operation, context, options, _activeCancellation);
            return _activeTask;
        }
    }

    private async Task<AgentRunResult> ExecuteRunAsync(
        Func<
            AgentContext,
            AgentLoopOptions,
            Func<AgentEvent, CancellationToken, ValueTask>,
            CancellationToken,
            Task<AgentRunResult>> operation,
        AgentContext context,
        AgentLoopOptions options,
        CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            var result = await operation(context, options, ProcessEventAsync, cancellation.Token).ConfigureAwait(false);
            string[] subscriberErrors;
            lock (_gate)
            {
                subscriberErrors = _runSubscriberErrors.ToArray();
                if (subscriberErrors.Length > 0 && _error is null)
                {
                    _error = "One or more agent event subscribers failed.";
                }
            }

            return subscriberErrors.Length == 0 ? result : result.WithSubscriberErrors(subscriberErrors);
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                _acceptsActiveControl = false;
                _streamingMessage = null;
                _streamingEvent = null;
                _pendingToolCallIds.Clear();
                _activeCancellation = null;
                _activeTask = null;
            }

            cancellation.Dispose();
        }
    }

    private AgentLoopOptions CreateLoopOptions()
    {
        return new AgentLoopOptions(_provider, _model)
        {
            SessionId = _sessionId,
            Parameters = _parameters.Copy(),
            Limits = _limits.Copy(),
            Hooks = CopyHooks(_hooks),
            ToolExecution = _toolExecution,
            Clock = _clock,
            RunIdFactory = _runIdFactory,
            GetSteeringMessagesAsync = _ => new ValueTask<IReadOnlyList<AgentMessage>>(_steering.Drain()),
            GetFollowUpMessagesAsync = _ => new ValueTask<IReadOnlyList<AgentMessage>>(_followUps.Drain()),
            FinalizePendingMessages = FinalizePendingMessages,
            NotifyRunFinishing = CloseActiveControl,
        };
    }

    private IReadOnlyList<AgentMessage> FinalizePendingMessages()
    {
        lock (_gate)
        {
            IReadOnlyList<AgentMessage> pending = _steering.Drain();
            if (pending.Count == 0)
            {
                pending = _followUps.Drain();
            }

            if (pending.Count == 0)
            {
                _acceptsActiveControl = false;
            }

            return pending;
        }
    }

    private void CloseActiveControl()
    {
        lock (_gate)
        {
            _acceptsActiveControl = false;
        }
    }

    private async ValueTask ProcessEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Subscriber[] subscribers;
            CancellationToken subscriberToken;
            lock (_gate)
            {
                switch (agentEvent.Kind)
                {
                    case AgentEventKind.MessageStarted:
                    case AgentEventKind.MessageUpdated:
                        if (agentEvent.Message?.Role == AgentRole.Assistant)
                        {
                            _streamingMessage = agentEvent.Message;
                            _streamingEvent = agentEvent.ModelEvent;
                        }
                        break;

                    case AgentEventKind.MessageEnded:
                        _streamingMessage = null;
                        _streamingEvent = null;
                        if (agentEvent.Message is not null)
                        {
                            _messages.Add(agentEvent.Message);
                        }
                        break;

                    case AgentEventKind.ToolStarted:
                        if (agentEvent.ToolCall is not null)
                        {
                            _pendingToolCallIds.Add(agentEvent.ToolCall.Id);
                        }
                        break;

                    case AgentEventKind.ToolEnded:
                        if (agentEvent.ToolCall is not null)
                        {
                            _pendingToolCallIds.Remove(agentEvent.ToolCall.Id);
                        }
                        break;

                    case AgentEventKind.RunFaulted:
                        _error = agentEvent.Error;
                        break;

                    case AgentEventKind.RunEnded:
                        _streamingMessage = null;
                        _streamingEvent = null;
                        _pendingToolCallIds.Clear();
                        break;
                }

                subscribers = _subscribers.ToArray();
                subscriberToken = _activeCancellation?.Token ?? cancellationToken;
            }

            foreach (var subscriber in subscribers)
            {
                try
                {
                    await subscriber.Handler(agentEvent, subscriberToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        var error = exception.Message ?? exception.GetType().Name;
                        if (error.Length > _limits.MaxTextCharactersPerPart)
                        {
                            error = error.Substring(0, _limits.MaxTextCharactersPerPart);
                        }

                        _runSubscriberErrors.Add(error);
                        _subscribers.RemoveAll(candidate => candidate.Id == subscriber.Id);
                    }
                }
            }
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(candidate => candidate.Id == id);
        }
    }

    private void EnsureIdle()
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("The agent is already running. Queue steering or follow-up input, or wait for it to become idle.");
        }
    }

    private static AgentHooks CopyHooks(AgentHooks hooks)
    {
        return new AgentHooks
        {
            TransformContextAsync = hooks.TransformContextAsync,
            BeforeModelRequestAsync = hooks.BeforeModelRequestAsync,
            ShouldStopAfterTurnAsync = hooks.ShouldStopAfterTurnAsync,
            PrepareNextTurnAsync = hooks.PrepareNextTurnAsync,
            BeforeToolCallAsync = hooks.BeforeToolCallAsync,
            AuthorizeToolCallAsync = hooks.AuthorizeToolCallAsync,
            AfterToolCallAsync = hooks.AfterToolCallAsync,
        };
    }

    private static void ValidateQueueMode(QueueMode mode, string parameterName)
    {
        if (!Enum.IsDefined(typeof(QueueMode), mode))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateToolExecutionMode(ToolExecutionMode mode, string parameterName)
    {
        if (!Enum.IsDefined(typeof(ToolExecutionMode), mode))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private sealed class Subscriber
    {
        public Subscriber(long id, AgentEventHandler handler)
        {
            Id = id;
            Handler = handler;
        }

        public long Id { get; }

        public AgentEventHandler Handler { get; }
    }

    private sealed class Subscription : IDisposable
    {
        private Agent? _owner;
        private readonly long _id;

        public Subscription(Agent owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_id);
        }
    }

    private sealed class PendingMessageQueue
    {
        private readonly object _queueGate = new();
        private readonly List<AgentMessage> _messages = new();
        private readonly int _capacity;
        private QueueMode _mode;

        public PendingMessageQueue(QueueMode mode, int capacity)
        {
            _mode = mode;
            _capacity = capacity;
        }

        public QueueMode Mode
        {
            get
            {
                lock (_queueGate)
                {
                    return _mode;
                }
            }
            set
            {
                lock (_queueGate)
                {
                    _mode = value;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_queueGate)
                {
                    return _messages.Count;
                }
            }
        }

        public void Enqueue(AgentMessage message)
        {
            lock (_queueGate)
            {
                if (_messages.Count >= _capacity)
                {
                    throw new InvalidOperationException($"The message queue reached its capacity of {_capacity}.");
                }

                _messages.Add(message);
            }
        }

        public IReadOnlyList<AgentMessage> Drain()
        {
            lock (_queueGate)
            {
                if (_messages.Count == 0)
                {
                    return Array.Empty<AgentMessage>();
                }

                if (_mode == QueueMode.All)
                {
                    var all = _messages.ToArray();
                    _messages.Clear();
                    return all;
                }

                var first = _messages[0];
                _messages.RemoveAt(0);
                return new[] { first };
            }
        }

        public void Clear()
        {
            lock (_queueGate)
            {
                _messages.Clear();
            }
        }
    }
}
