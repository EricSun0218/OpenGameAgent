namespace OpenGameAgent.Realtime;

public sealed class RealtimeConversationManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IRealtimeTransport _transport;
    private readonly IRealtimeBehaviorHandler? _behaviorHandler;
    private readonly List<RealtimeConversationEventHandler> _handlers = new();
    private ConversationState? _active;
    private Task _stopTask = Task.CompletedTask;
    private RealtimeConversationState _state = RealtimeConversationState.Idle;
    private int _disposed;

    public RealtimeConversationManager(
        IRealtimeTransport transport,
        IRealtimeBehaviorHandler? behaviorHandler = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _behaviorHandler = behaviorHandler;
    }

    public RealtimeConversationState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long DroppedAudioFrames
    {
        get
        {
            lock (_gate)
            {
                return _active?.DroppedAudioFrames ?? 0;
            }
        }
    }

    public IDisposable RegisterHandler(RealtimeConversationEventHandler handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _handlers.Add(handler);
        }

        return new HandlerRegistration(this, handler);
    }

    public async ValueTask StartAsync(
        RealtimeConversationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        ThrowIfDisposed();
        var snapshot = options.Snapshot();
        ConversationState? previous;
        lock (_gate)
        {
            previous = _active;
            _active = null;
            _state = RealtimeConversationState.Starting;
        }

        if (previous is not null)
        {
            await previous.StopAsync(snapshot.ShutdownTimeoutMilliseconds, CancellationToken.None)
                .ConfigureAwait(false);
        }

        IRealtimeTransportSession session;
        try
        {
            session = await _transport.ConnectAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _state = RealtimeConversationState.Faulted;
            }

            throw;
        }

        var state = new ConversationState(this, session, snapshot, _behaviorHandler);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _state = RealtimeConversationState.Closed;
            }
            else
            {
                _active = state;
                _state = RealtimeConversationState.Active;
                state.Start();
                return;
            }
        }

        await state.StopAsync(snapshot.ShutdownTimeoutMilliseconds, CancellationToken.None)
            .ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(RealtimeConversationManager));
    }

    public bool TrySendAudio(RealtimeAudioFrame frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        ConversationState? state;
        lock (_gate)
        {
            state = _active;
        }

        if (state is null || !state.TrySendAudio(frame))
        {
            state?.IncrementDroppedAudioFrames();
            return false;
        }

        return true;
    }

    public ValueTask SendTextAsync(
        string text,
        RealtimeTextRole role = RealtimeTextRole.User,
        CancellationToken cancellationToken = default) =>
        RequireActive().SendCommandAsync(new TextCommand(text, role), cancellationToken);

    public ValueTask SendHandoffProgressAsync(
        string handoffId,
        string text,
        RealtimeHandoffPhase phase = RealtimeHandoffPhase.Commentary,
        CancellationToken cancellationToken = default) =>
        RequireActive().SendCommandAsync(
            new HandoffCommand(handoffId, text, phase, completed: false),
            cancellationToken);

    public ValueTask CompleteHandoffAsync(
        string handoffId,
        string text,
        RealtimeHandoffPhase phase = RealtimeHandoffPhase.Final,
        CancellationToken cancellationToken = default) =>
        RequireActive().SendCommandAsync(
            new HandoffCommand(handoffId, text, phase, completed: true),
            cancellationToken);

    public ValueTask CancelResponseAsync(CancellationToken cancellationToken = default) =>
        RequireActive().SendCommandAsync(CancelResponseCommand.Instance, cancellationToken);

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_gate)
        {
            if (_active is { } active)
            {
                _active = null;
                _state = RealtimeConversationState.Stopping;
                _stopTask = active.StopAsync(
                        active.Options.ShutdownTimeoutMilliseconds,
                        CancellationToken.None)
                    .AsTask();
            }

            stopTask = _stopTask;
        }

        try
        {
            await WaitWithCancellationAsync(stopTask, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_stopTask, stopTask) && _active is null)
                {
                    _state = RealtimeConversationState.Closed;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _handlers.Clear();
        }
    }

    private ConversationState RequireActive()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            return _active ?? throw new InvalidOperationException("No realtime conversation is active.");
        }
    }

    private async ValueTask PublishAsync(
        RealtimeConversationEvent value,
        CancellationToken cancellationToken,
        int handlerTimeoutMilliseconds)
    {
        RealtimeConversationEventHandler[] handlers;
        lock (_gate)
        {
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            using var timeout = new CancellationTokenSource(handlerTimeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                var callback = Task.Run(
                    async () => await handler(value, linked.Token).ConfigureAwait(false),
                    CancellationToken.None);
                await WaitWithCancellationAsync(callback, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                RemoveHandler(handler);
            }
            catch
            {
                RemoveHandler(handler);
            }
        }
    }

    private static async Task WaitWithCancellationAsync(
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

    private void RemoveHandler(RealtimeConversationEventHandler handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    private void OnStateTerminated(ConversationState state, bool faulted, Task sessionDisposal)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, state))
            {
                _active = null;
                _stopTask = sessionDisposal;
                _state = faulted ? RealtimeConversationState.Faulted : RealtimeConversationState.Closed;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RealtimeConversationManager));
        }
    }

    private abstract class OutboundCommand
    {
        public virtual int TextLength => 0;

        public abstract ValueTask ExecuteAsync(
            IRealtimeTransportSession session,
            CancellationToken cancellationToken);
    }

    private sealed class TextCommand : OutboundCommand
    {
        private readonly string _text;
        private readonly RealtimeTextRole _role;

        public TextCommand(string text, RealtimeTextRole role)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _role = role;
        }

        public override int TextLength => _text.Length;

        public override ValueTask ExecuteAsync(
            IRealtimeTransportSession session,
            CancellationToken cancellationToken) =>
            session.SendTextAsync(_text, _role, cancellationToken);
    }

    private sealed class HandoffCommand : OutboundCommand
    {
        private readonly string _handoffId;
        private readonly string _text;
        private readonly RealtimeHandoffPhase _phase;
        private readonly bool _completed;

        public HandoffCommand(
            string handoffId,
            string text,
            RealtimeHandoffPhase phase,
            bool completed)
        {
            _handoffId = handoffId ?? throw new ArgumentNullException(nameof(handoffId));
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _phase = phase;
            _completed = completed;
        }

        public override int TextLength => _text.Length;

        public override ValueTask ExecuteAsync(
            IRealtimeTransportSession session,
            CancellationToken cancellationToken) =>
            session.SendHandoffAsync(_handoffId, _text, _phase, _completed, cancellationToken);
    }

    private sealed class CancelResponseCommand : OutboundCommand
    {
        public static CancelResponseCommand Instance { get; } = new();

        public override ValueTask ExecuteAsync(
            IRealtimeTransportSession session,
            CancellationToken cancellationToken) =>
            session.CancelResponseAsync(cancellationToken);
    }

    private sealed class TruncateAudioCommand : OutboundCommand
    {
        private readonly string _itemId;
        private readonly int _audioEndMilliseconds;

        public TruncateAudioCommand(string itemId, int audioEndMilliseconds)
        {
            _itemId = itemId;
            _audioEndMilliseconds = audioEndMilliseconds;
        }

        public override async ValueTask ExecuteAsync(
            IRealtimeTransportSession session,
            CancellationToken cancellationToken)
        {
            await session.CancelResponseAsync(cancellationToken).ConfigureAwait(false);
            await session.TruncateAudioAsync(_itemId, _audioEndMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class ConversationState
    {
        private readonly RealtimeConversationManager _owner;
        private readonly IRealtimeTransportSession _session;
        private readonly IRealtimeBehaviorHandler? _behaviorHandler;
        private readonly CancellationTokenSource _stop = new();
        private readonly BoundedAsyncQueue<RealtimeAudioFrame> _audio;
        private readonly BoundedAsyncQueue<OutboundCommand> _commands;
        private readonly BoundedAsyncQueue<RealtimeConversationEvent> _events;
        private readonly object _behaviorGate = new();
        private readonly object _audioOutputGate = new();
        private readonly object _transcriptGate = new();
        private readonly object _sessionDisposeGate = new();
        private readonly Dictionary<string, BehaviorExecution> _behaviors = new(StringComparer.Ordinal);
        private readonly System.Text.StringBuilder _inputTranscriptTail = new();
        private Task[] _tasks = Array.Empty<Task>();
        private long _droppedAudioFrames;
        private int _stopped;
        private int _faulted;
        private Task? _sessionDisposeTask;
        private int _tailFlushed;
        private int _closedObserved;
        private string? _outputItemId;
        private long _outputAudioSamplesPerChannel;
        private int _outputAudioSampleRate;

        public ConversationState(
            RealtimeConversationManager owner,
            IRealtimeTransportSession session,
            RealtimeConversationOptions options,
            IRealtimeBehaviorHandler? behaviorHandler)
        {
            _owner = owner;
            _session = session;
            Options = options;
            _behaviorHandler = behaviorHandler;
            _audio = new BoundedAsyncQueue<RealtimeAudioFrame>(options.AudioQueueCapacity);
            _commands = new BoundedAsyncQueue<OutboundCommand>(options.CommandQueueCapacity);
            _events = new BoundedAsyncQueue<RealtimeConversationEvent>(options.EventQueueCapacity);
        }

        public RealtimeConversationOptions Options { get; }

        public long DroppedAudioFrames => Interlocked.Read(ref _droppedAudioFrames);

        public void Start()
        {
            _tasks = new[]
            {
                Task.Run(PumpAudioAsync),
                Task.Run(PumpCommandsAsync),
                Task.Run(PumpIncomingAsync),
                Task.Run(PumpEventsAsync),
            };
        }

        public bool TrySendAudio(RealtimeAudioFrame frame)
        {
            if (frame.Pcm16.Length > Options.MaximumAudioFrameBytes)
            {
                throw new ArgumentException("The realtime audio frame exceeds the configured limit.", nameof(frame));
            }

            return _audio.TryEnqueue(frame);
        }

        public void IncrementDroppedAudioFrames() => Interlocked.Increment(ref _droppedAudioFrames);

        public ValueTask SendCommandAsync(OutboundCommand command, CancellationToken cancellationToken)
        {
            if (command.TextLength > Options.MaximumTextCharacters)
            {
                throw new ArgumentException("The realtime text exceeds the configured limit.");
            }

            return _commands.EnqueueAsync(command, cancellationToken);
        }

        public async ValueTask StopAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await FlushTranscriptTailAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }

            await EnsureClosedQueuedAsync(linked.Token).ConfigureAwait(false);

            _stop.Cancel();
            _audio.Complete();
            _commands.Complete();
            _events.Complete();
            CancelAllBehaviors();

            try
            {
                try
                {
                    await _session.CloseAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }

                try
                {
                    await WaitWithCancellationAsync(Task.WhenAll(_tasks), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }
            }
            finally
            {
                try
                {
                    await WaitWithCancellationAsync(
                            Task.Run(
                                async () => await DisposeSessionAsync().ConfigureAwait(false),
                                CancellationToken.None),
                            linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                }

                _stop.Dispose();
            }
        }

        private async Task PumpAudioAsync()
        {
            try
            {
                while (await _audio.DequeueAsync(_stop.Token).ConfigureAwait(false) is { } frame)
                {
                    await _session.SendAudioAsync(frame, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task PumpCommandsAsync()
        {
            try
            {
                while (await _commands.DequeueAsync(_stop.Token).ConfigureAwait(false) is { } command)
                {
                    await command.ExecuteAsync(_session, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(exception).ConfigureAwait(false);
            }
        }

        private async Task PumpIncomingAsync()
        {
            try
            {
                await foreach (var value in _session.ReadEventsAsync(_stop.Token).ConfigureAwait(false))
                {
                    var normalized = NormalizeIncoming(value);
                    TrackOutputAudio(normalized);
                    if (normalized.Kind == RealtimeConversationEventKind.BehaviorRequested
                        && normalized.Behavior is not null)
                    {
                        ScheduleBehavior(normalized.Behavior);
                    }

                    await _events.EnqueueAsync(normalized, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(exception).ConfigureAwait(false);
            }
            finally
            {
                using var timeout = new CancellationTokenSource(Options.EventHandlerTimeoutMilliseconds);
                try
                {
                    await FlushTranscriptTailAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }

                await EnsureClosedQueuedAsync(timeout.Token).ConfigureAwait(false);

                _events.Complete();
                _audio.Complete();
                _commands.Complete();
                CancelAllBehaviors();
                _stop.Cancel();
                var sessionDisposal = Task.Run(DisposeSessionAfterTransportClosedAsync);
                _owner.OnStateTerminated(
                    this,
                    Volatile.Read(ref _faulted) != 0,
                    sessionDisposal);
            }
        }

        private RealtimeConversationEvent NormalizeIncoming(RealtimeConversationEvent value)
        {
            if (value.Kind == RealtimeConversationEventKind.Closed)
            {
                Interlocked.Exchange(ref _closedObserved, 1);
            }

            var eventCharacters = checked(
                (value.Text?.Length ?? 0)
                + (value.Error?.Length ?? 0)
                + (value.Handoff?.Transcript.Length ?? 0)
                + (value.Handoff?.ContextJson?.Length ?? 0)
                + (value.Behavior?.ArgumentsJson.Length ?? 0));
            if (eventCharacters > Options.MaximumEventCharacters
                || value.Audio is { } audio && audio.Pcm16.Length > Options.MaximumAudioFrameBytes)
            {
                throw new InvalidDataException("A realtime provider event exceeded the configured limit.");
            }

            if (value.Kind == RealtimeConversationEventKind.InputTranscriptDone
                && !string.IsNullOrWhiteSpace(value.Text))
            {
                lock (_transcriptGate)
                {
                    if (_inputTranscriptTail.Length > 0)
                    {
                        _inputTranscriptTail.Append('\n');
                    }

                    _inputTranscriptTail.Append(value.Text);
                    if (_inputTranscriptTail.Length > Options.MaximumTextCharacters)
                    {
                        _inputTranscriptTail.Remove(
                            0,
                            _inputTranscriptTail.Length - Options.MaximumTextCharacters);
                    }
                }

                return value;
            }

            if (value.Kind != RealtimeConversationEventKind.HandoffRequested
                || value.Handoff is null)
            {
                return value;
            }

            lock (_transcriptGate)
            {
                _inputTranscriptTail.Clear();
            }

            var handoff = new RealtimeHandoffRequest(
                value.Handoff.HandoffId,
                value.Handoff.Transcript,
                value.Handoff.ContextJson,
                Options.ClientManagedHandoffs || value.Handoff.ClientManaged,
                value.Handoff.IsTranscriptTail);
            return new RealtimeConversationEvent(
                value.Kind,
                value.Text,
                value.Audio,
                handoff,
                value.Behavior,
                value.ItemId,
                value.ResponseId,
                value.Error);
        }

        private async ValueTask EnsureClosedQueuedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _closedObserved, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await _events.EnqueueAsync(
                        new RealtimeConversationEvent(RealtimeConversationEventKind.Closed),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async ValueTask FlushTranscriptTailAsync(CancellationToken cancellationToken)
        {
            if (!Options.FlushTranscriptTailOnClose
                || Interlocked.Exchange(ref _tailFlushed, 1) != 0)
            {
                return;
            }

            string transcript;
            lock (_transcriptGate)
            {
                transcript = _inputTranscriptTail.ToString().Trim();
                _inputTranscriptTail.Clear();
            }

            if (transcript.Length == 0)
            {
                return;
            }

            var handoff = new RealtimeHandoffRequest(
                $"transcript-tail-{Guid.NewGuid():N}",
                transcript,
                clientManaged: Options.ClientManagedHandoffs,
                isTranscriptTail: true);
            await _owner.PublishAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.HandoffRequested,
                        handoff: handoff),
                    cancellationToken,
                    Options.EventHandlerTimeoutMilliseconds)
                .ConfigureAwait(false);
        }

        private void TrackOutputAudio(RealtimeConversationEvent value)
        {
            if (value.Kind == RealtimeConversationEventKind.AudioOutput
                && value.Audio is { ItemId: { } itemId } audio)
            {
                lock (_audioOutputGate)
                {
                    if (!string.Equals(_outputItemId, itemId, StringComparison.Ordinal))
                    {
                        _outputItemId = itemId;
                        _outputAudioSamplesPerChannel = 0;
                        _outputAudioSampleRate = audio.SampleRate;
                    }
                    else if (_outputAudioSampleRate != audio.SampleRate)
                    {
                        throw new InvalidDataException(
                            "Output audio for one realtime item changed sample rate.");
                    }

                    _outputAudioSamplesPerChannel = checked(
                        _outputAudioSamplesPerChannel + audio.SamplesPerChannel);
                }

                return;
            }

            if (value.Kind == RealtimeConversationEventKind.InputSpeechStarted)
            {
                string? interruptedItemId;
                int milliseconds;
                lock (_audioOutputGate)
                {
                    interruptedItemId = _outputItemId;
                    milliseconds = _outputAudioSampleRate == 0
                        ? 0
                        : checked((int)Math.Min(
                            int.MaxValue,
                            _outputAudioSamplesPerChannel * 1_000L / _outputAudioSampleRate));
                    _outputItemId = null;
                    _outputAudioSamplesPerChannel = 0;
                    _outputAudioSampleRate = 0;
                }

                if (interruptedItemId is not null && milliseconds > 0)
                {
                    _ = EnqueueInterruptionAsync(interruptedItemId, milliseconds);
                }

                return;
            }

            if (value.Kind is RealtimeConversationEventKind.ResponseDone
                or RealtimeConversationEventKind.ResponseCancelled)
            {
                lock (_audioOutputGate)
                {
                    _outputItemId = null;
                    _outputAudioSamplesPerChannel = 0;
                    _outputAudioSampleRate = 0;
                }
            }
        }

        private async Task EnqueueInterruptionAsync(string itemId, int milliseconds)
        {
            try
            {
                await _commands.EnqueueAsync(
                        new TruncateAudioCommand(itemId, milliseconds),
                        _stop.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task PumpEventsAsync()
        {
            while (await _events.DequeueAsync(CancellationToken.None).ConfigureAwait(false) is { } value)
            {
                await _owner.PublishAsync(
                        value,
                        value.Kind is RealtimeConversationEventKind.Error
                            or RealtimeConversationEventKind.Closed
                            ? CancellationToken.None
                            : _stop.Token,
                        Options.EventHandlerTimeoutMilliseconds)
                    .ConfigureAwait(false);
            }
        }

        private void ScheduleBehavior(RealtimeBehaviorRequest request)
        {
            if (_behaviorHandler is null)
            {
                _ = _commands.EnqueueAsync(
                    new BehaviorResultCommand(new RealtimeBehaviorResult(
                        request.BehaviorId,
                        RealtimeBehaviorDisposition.Rejected,
                        "{\"reason\":\"handler_unavailable\"}")),
                    _stop.Token);
                return;
            }

            BehaviorExecution execution;
            lock (_behaviorGate)
            {
                if (_behaviors.Remove(request.Channel, out var previous))
                {
                    previous.Cancellation.Cancel();
                }
                else if (_behaviors.Count >= Options.MaximumConcurrentBehaviors)
                {
                    _ = EnqueueBehaviorResultAsync(new RealtimeBehaviorResult(
                        request.BehaviorId,
                        RealtimeBehaviorDisposition.Rejected,
                        "{\"reason\":\"concurrency_limit\"}"));
                    return;
                }

                execution = new BehaviorExecution(new CancellationTokenSource());
                _behaviors[request.Channel] = execution;
            }

            execution.Task = Task.Run(() => RunBehaviorAsync(request, execution));
        }

        private async Task RunBehaviorAsync(
            RealtimeBehaviorRequest request,
            BehaviorExecution execution)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _stop.Token,
                execution.Cancellation.Token);
            RealtimeBehaviorResult result;
            try
            {
                result = await _behaviorHandler!
                    .ExecuteAsync(request, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                result = new RealtimeBehaviorResult(
                    request.BehaviorId,
                    RealtimeBehaviorDisposition.Cancelled);
            }
            catch (Exception exception)
            {
                result = new RealtimeBehaviorResult(
                    request.BehaviorId,
                    RealtimeBehaviorDisposition.Failed,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        error = Bound(exception.Message, 1_024),
                    }));
            }
            finally
            {
                lock (_behaviorGate)
                {
                    if (_behaviors.TryGetValue(request.Channel, out var current)
                        && ReferenceEquals(current, execution))
                    {
                        _behaviors.Remove(request.Channel);
                    }
                }

                execution.Cancellation.Dispose();
            }

            await EnqueueBehaviorResultAsync(result).ConfigureAwait(false);
        }

        private async Task EnqueueBehaviorResultAsync(RealtimeBehaviorResult result)
        {
            try
            {
                await _commands.EnqueueAsync(new BehaviorResultCommand(result), _stop.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private void CancelAllBehaviors()
        {
            lock (_behaviorGate)
            {
                foreach (var execution in _behaviors.Values)
                {
                    execution.Cancellation.Cancel();
                    if (execution.Task is { } task)
                    {
                        _ = task.ContinueWith(
                            static completed => _ = completed.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }

                _behaviors.Clear();
            }
        }

        private async ValueTask ReportFailureAsync(Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) != 0)
            {
                return;
            }

            var error = new RealtimeConversationEvent(
                RealtimeConversationEventKind.Error,
                error: Bound(exception.Message, 4_096));
            _events.TryEnqueue(error);
            _events.Complete();
            _stop.Cancel();
            await Task.Yield();
        }

        private async Task DisposeSessionAfterTransportClosedAsync()
        {
            try
            {
                await DisposeSessionAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private ValueTask DisposeSessionAsync()
        {
            Task task;
            lock (_sessionDisposeGate)
            {
                task = _sessionDisposeTask ??= _session.DisposeAsync().AsTask();
            }

            return new ValueTask(task);
        }

        private static string Bound(string value, int maximum) =>
            value.Length <= maximum ? value : value.Substring(0, maximum);

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancellation);
            if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await task.ConfigureAwait(false);
        }

        private sealed class BehaviorExecution
        {
            public BehaviorExecution(CancellationTokenSource cancellation)
            {
                Cancellation = cancellation;
            }

            public CancellationTokenSource Cancellation { get; }

            public Task? Task { get; set; }
        }

        private sealed class BehaviorResultCommand : OutboundCommand
        {
            private readonly RealtimeBehaviorResult _result;

            public BehaviorResultCommand(RealtimeBehaviorResult result)
            {
                _result = result;
            }

            public override ValueTask ExecuteAsync(
                IRealtimeTransportSession session,
                CancellationToken cancellationToken) =>
                session.SendBehaviorResultAsync(_result, cancellationToken);
        }
    }

    private sealed class HandlerRegistration : IDisposable
    {
        private RealtimeConversationManager? _owner;
        private readonly RealtimeConversationEventHandler _handler;

        public HandlerRegistration(
            RealtimeConversationManager owner,
            RealtimeConversationEventHandler handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveHandler(_handler);
    }
}

internal sealed class BoundedAsyncQueue<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Queue<T> _queue = new();
    private readonly SemaphoreSlim _items = new(0);
    private readonly SemaphoreSlim _slots;
    private bool _completed;

    public BoundedAsyncQueue(int capacity)
    {
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    public bool TryEnqueue(T value)
    {
        if (!_slots.Wait(0))
        {
            return false;
        }

        lock (_gate)
        {
            if (_completed)
            {
                _slots.Release();
                return false;
            }

            _queue.Enqueue(value);
        }

        _items.Release();
        return true;
    }

    public async ValueTask EnqueueAsync(T value, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_completed)
            {
                _slots.Release();
                throw new InvalidOperationException("The realtime queue is closed.");
            }

            _queue.Enqueue(value);
        }

        _items.Release();
    }

    public async ValueTask<T?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _items.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    var value = _queue.Dequeue();
                    _slots.Release();
                    return value;
                }

                if (_completed)
                {
                    return null;
                }
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _items.Release();
    }
}
