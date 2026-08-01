namespace GameAgent.Core;

/// <summary>
/// Bounds stateless provider calls that do not enter an agent loop or touch a
/// durable session.
/// </summary>
public sealed class SimpleCompletionRuntimeOptions
{
    public int MaxConcurrentProviderCalls { get; set; } = 4;

    public int? MaxConcurrentBackgroundProviderCalls { get; set; }

    public int MaxMessages { get; set; } = 256;

    public int MaxPromptUtf8Bytes { get; set; } = 1_048_576;

    public int EstimatedPromptBytesPerToken { get; set; } = 4;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal SimpleCompletionRuntimeOptions Snapshot()
    {
        if (MaxConcurrentProviderCalls < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentProviderCalls));
        }

        if (MaxConcurrentBackgroundProviderCalls.HasValue
            && (MaxConcurrentBackgroundProviderCalls.Value < 1
                || MaxConcurrentBackgroundProviderCalls.Value
                > MaxConcurrentProviderCalls))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentBackgroundProviderCalls));
        }

        if (MaxMessages < 1 || MaxMessages > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessages));
        }

        if (MaxPromptUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPromptUtf8Bytes));
        }

        if (EstimatedPromptBytesPerToken is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EstimatedPromptBytesPerToken));
        }

        if (ShutdownTimeout < TimeSpan.FromMilliseconds(10)
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new SimpleCompletionRuntimeOptions
        {
            MaxConcurrentProviderCalls = MaxConcurrentProviderCalls,
            MaxConcurrentBackgroundProviderCalls =
                MaxConcurrentBackgroundProviderCalls,
            MaxMessages = MaxMessages,
            MaxPromptUtf8Bytes = MaxPromptUtf8Bytes,
            EstimatedPromptBytesPerToken = EstimatedPromptBytesPerToken,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

public sealed class SimpleCompletionRequest
{
    /// <summary>
    /// Optional caller identity used only for provider diagnostics. The
    /// runtime generates an identity when this value is null.
    /// </summary>
    public string? OperationId { get; set; }

    public IReadOnlyList<NormalizedMessage> Messages { get; set; } =
        Array.Empty<NormalizedMessage>();

    public string WorkloadClass { get; set; } =
        ProviderWorkloadClasses.Interactive;

    public int? EstimatedPromptTokens { get; set; }

    public int? MaxOutputTokens { get; set; }

    public ModelInferenceOptions? Inference { get; set; }

    public ProviderRoutePreference? RoutePreference { get; set; }
}

public sealed class SimpleCompletionOutcome
{
    public string OperationId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public ProviderRouteIdentity? RouteIdentity { get; set; }

    public string? Text { get; set; }

    public string? ReasoningContent { get; set; }

    public ProviderUsage Usage { get; set; } = new();

    public string? FinishReason { get; set; }
}

public interface ISimpleCompletionRuntime
{
    ValueTask<SimpleCompletionOutcome> CompleteAsync(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes one stateless model request with the normal provider
/// retry/fallback and stream-fencing behavior. It never exposes tools,
/// persists a transcript, invokes memory, or starts an agent turn.
/// </summary>
public sealed class SimpleCompletionRuntime :
    ISimpleCompletionRuntime,
    IAsyncDisposable
{
    private readonly ProviderAttemptRunner _provider;
    private readonly IRuntimeIdGenerator _ids;
    private readonly ProviderWorkloadAdmission _admission;
    private readonly bool _ownsAdmission;
    private readonly SimpleCompletionRuntimeOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private TaskCompletionSource<bool>? _drained;
    private Task? _shutdownCancellationTask;
    private int _active;
    private int _shutdownCancellationCompleted;
    private int _state;

    public SimpleCompletionRuntime(
        ProviderAttemptRunner provider,
        IRuntimeIdGenerator ids,
        SimpleCompletionRuntimeOptions? options = null)
        : this(
            provider,
            ids,
            (options ?? new SimpleCompletionRuntimeOptions()).Snapshot(),
            admission: null)
    {
    }

    internal SimpleCompletionRuntime(
        ProviderAttemptRunner provider,
        IRuntimeIdGenerator ids,
        SimpleCompletionRuntimeOptions options,
        ProviderWorkloadAdmission? admission)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _options = (options
                    ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _ownsAdmission = admission is null;
        _admission = admission ?? new ProviderWorkloadAdmission(
            _options.MaxConcurrentProviderCalls,
            _options.MaxConcurrentBackgroundProviderCalls);
    }

    public async ValueTask<SimpleCompletionOutcome> CompleteAsync(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var messages = SnapshotMessages(request.Messages, cancellationToken);
        var workloadClass = ProviderWorkloadClasses.Normalize(
            request.WorkloadClass,
            nameof(request.WorkloadClass));
        var estimatedPromptTokens = request.EstimatedPromptTokens;
        var maxOutputTokens = request.MaxOutputTokens;
        if (estimatedPromptTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.EstimatedPromptTokens));
        }

        if (maxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxOutputTokens));
        }

        var operationId = request.OperationId is null
            ? _ids.NewId("completion")
            : RuntimeGuard.RequiredId(
                request.OperationId,
                nameof(request.OperationId));
        using var active = Enter(cancellationToken);
        var inference = request.Inference?.CloneValidated();
        var routePreference = request.RoutePreference?.CloneValidated();
        var routePlan = _provider.CaptureRoutePlan(
            routePreference,
            active.Token);
        using var lease = await _admission
            .AcquireAsync(workloadClass, active.Token)
            .ConfigureAwait(false);
        var result = await _provider.RunAsync(
                operationId,
                _ids.NewId("completion-attempt"),
                _ids.NewId("completion-turn"),
                messages,
                Array.Empty<GameAgent.Protocol.ToolDescriptor>(),
                new AttemptFence(),
                onCurrentEvent: null,
                active.Token,
                estimatedPromptTokens: estimatedPromptTokens,
                maxOutputTokens: maxOutputTokens,
                onDetachedCleanup: TrackDetachedCleanup,
                routePlan: routePlan,
                inference: inference)
            .ConfigureAwait(false);
        if (result.ToolCalls.Count != 0)
        {
            throw new InvalidDataException(
                "A stateless completion returned tool calls even though no tools were exposed.");
        }

        return new SimpleCompletionOutcome
        {
            OperationId = operationId,
            ProviderId = result.ProviderId,
            RouteIdentity = result.RouteIdentity,
            Text = result.Text,
            ReasoningContent = result.ReasoningContent,
            Usage = SnapshotUsage(result.Usage),
            FinishReason = result.FinishReason
        };
    }

    public async ValueTask StopAsync()
    {
        _ = await StopWithDrainResultAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops admission and reports whether active calls and provider cleanup
    /// tasks drained inside the configured bounded shutdown window.
    /// </summary>
    public async ValueTask<bool> StopWithDrainResultAsync()
    {
        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            if (_state == 2)
            {
                return true;
            }

            if (_state == 0)
            {
                _state = 1;
                _drained ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownCancellationTask = StartShutdownCancellation();
            }

            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _drained!.Task;
        }

        var all = Task.WhenAll(cancellation, drain);
        var completed = await Task.WhenAny(
                all,
                Task.Delay(_options.ShutdownTimeout))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
        {
            return false;
        }

        await all.ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _ = await StopWithDrainResultAsync().ConfigureAwait(false);
        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _state == 2
                ? Task.CompletedTask
                : _drained!.Task;
        }

        await Task.WhenAll(cancellation, drain).ConfigureAwait(false);
    }

    private ActiveCall Enter(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (_state != 0)
            {
                throw new ObjectDisposedException(
                    nameof(SimpleCompletionRuntime));
            }

            _active++;
        }

        try
        {
            return new ActiveCall(this, cancellationToken, _shutdown.Token);
        }
        catch
        {
            Exit();
            throw;
        }
    }

    private void Exit()
    {
        lock (_lifecycleSync)
        {
            _active--;
            if (_active == 0
                && _state == 1
                && Volatile.Read(ref _shutdownCancellationCompleted) != 0)
            {
                FinishStopLocked();
            }
        }
    }

    private Task StartShutdownCancellation()
    {
        return Task.Run(
            () =>
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    // Provider cancellation callbacks are untrusted. Active
                    // calls still own their resources until they settle.
                }
                finally
                {
                    lock (_lifecycleSync)
                    {
                        Volatile.Write(
                            ref _shutdownCancellationCompleted,
                            1);
                        if (_active == 0 && _state == 1)
                        {
                            FinishStopLocked();
                        }
                    }
                }
            });
    }

    private void TrackDetachedCleanup(Task cleanup)
    {
        if (cleanup is null)
        {
            return;
        }

        lock (_lifecycleSync)
        {
            _active = checked(_active + 1);
        }

        _ = ObserveDetachedCleanupAsync(cleanup);
    }

    private async Task ObserveDetachedCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // ProviderAttemptRunner owns the failure classification. This
            // lifecycle reference only prevents premature resource disposal.
        }
        finally
        {
            Exit();
        }
    }

    private void FinishStopLocked()
    {
        if (_state == 2)
        {
            return;
        }

        _state = 2;
        if (_ownsAdmission)
        {
            _admission.Dispose();
        }

        _shutdown.Dispose();
        _drained?.TrySetResult(true);
    }

    private IReadOnlyList<NormalizedMessage> SnapshotMessages(
        IReadOnlyList<NormalizedMessage>? messages,
        CancellationToken cancellationToken)
    {
        if (messages is null)
        {
            throw new ArgumentException(
                "A simple completion requires a message collection.",
                nameof(messages));
        }

        var snapshots = RuntimeInputGuard.CopyBounded(
            messages,
            _options.MaxMessages,
            message => NormalizedMessageJournalCodec.CloneValidated(
                message
                ?? throw new ArgumentException(
                    "Completion messages cannot contain null entries.",
                    nameof(messages))),
            nameof(messages),
            "completion_message_count_exceeded",
            cancellationToken);
        if (snapshots.Length == 0)
        {
            throw new ArgumentException(
                "A simple completion requires at least one message.",
                nameof(messages));
        }

        _ = RuntimePromptBuilder.MeasurePrompt(
            snapshots,
            Array.Empty<GameAgent.Protocol.ToolDescriptor>(),
            _options.MaxMessages,
            _options.MaxPromptUtf8Bytes,
            _options.EstimatedPromptBytesPerToken,
            ScriptAwareTokenEstimator.Shared);
        return snapshots;
    }

    private static ProviderUsage SnapshotUsage(ProviderUsage source)
    {
        return new ProviderUsage
        {
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens,
            CostUsd = source.CostUsd,
            Samples = source.Samples,
            CacheReadTokens = source.CacheReadTokens,
            CacheWriteTokens = source.CacheWriteTokens,
            CacheMissTokens = source.CacheMissTokens,
            ReasoningTokens = source.ReasoningTokens,
            ProviderTotalTokens = source.ProviderTotalTokens,
            Availability = source.Availability
        };
    }

    private sealed class ActiveCall : IDisposable
    {
        private SimpleCompletionRuntime? _owner;
        private readonly CancellationTokenSource _linked;

        public ActiveCall(
            SimpleCompletionRuntime owner,
            CancellationToken caller,
            CancellationToken shutdown)
        {
            _owner = owner;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                shutdown);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            _linked.Dispose();
            owner.Exit();
        }
    }
}
