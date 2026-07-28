using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public static class ModelStreamEventKinds
{
    public const string TextDelta = "text_delta";
    public const string ReasoningDelta = "reasoning_delta";
    public const string ToolCallDelta = "tool_call_delta";
    public const string Usage = "usage";
    public const string Completed = "completed";
}

public sealed class ProviderCapabilities
{
    public bool Streaming { get; set; } = true;

    public bool ToolCalling { get; set; } = true;

    public bool JsonOutput { get; set; } = true;

    public int MaxContextTokens { get; set; }
}

public sealed class ProviderUsage
{
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public string CostUsd { get; set; } = "0";
}

public sealed class ModelStreamEvent
{
    public string StreamAttemptId { get; set; } = string.Empty;

    public long Ordinal { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? TextDelta { get; set; }

    public string? ReasoningDelta { get; set; }

    public string? ToolCallId { get; set; }

    public string? ToolNameDelta { get; set; }

    public string? ArgumentsJsonDelta { get; set; }

    public ProviderUsage? Usage { get; set; }

    public string? FinishReason { get; set; }
}

public sealed class StreamingModelRequest
{
    public string RunId { get; set; } = string.Empty;

    public string RunAttemptId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public IReadOnlyList<NormalizedMessage> Messages { get; set; } =
        Array.Empty<NormalizedMessage>();

    public IReadOnlyList<GameAgent.Protocol.ToolDescriptor> Tools { get; set; } =
        Array.Empty<GameAgent.Protocol.ToolDescriptor>();

    public int? MaxOutputTokens { get; set; }
}

public interface IStreamingModelProvider
{
    string ProviderId { get; }

    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        CancellationToken cancellationToken);
}

public sealed class ProviderException : Exception
{
    public ProviderException(
        string code,
        string category,
        string safeMessage,
        bool retryable,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        bool usageKnownToBeZero = false)
        : base(safeMessage, innerException)
    {
        Code = code;
        Category = category;
        Retryable = retryable;
        RetryAfter = retryAfter;
        UsageKnownToBeZero = usageKnownToBeZero;
    }

    public string Code { get; }

    public string Category { get; }

    public bool Retryable { get; }

    public TimeSpan? RetryAfter { get; }

    public bool UsageKnownToBeZero { get; }
}

public sealed class ProviderRetryPolicy
{
    public int MaxAttemptsPerProvider { get; set; } = 2;

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(4);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal ProviderRetryPolicy Snapshot()
    {
        if (MaxAttemptsPerProvider is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttemptsPerProvider),
                "Provider attempts must be between 1 and 10.");
        }

        if (InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        }

        if (MaxDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDelay),
                "Maximum delay cannot be shorter than the initial delay.");
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
        }

        if (TotalTimeout <= TimeSpan.Zero || TotalTimeout < IdleTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TotalTimeout),
                "Total timeout must be positive and at least the idle timeout.");
        }

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
        }

        return new ProviderRetryPolicy
        {
            MaxAttemptsPerProvider = MaxAttemptsPerProvider,
            InitialDelay = InitialDelay,
            MaxDelay = MaxDelay,
            IdleTimeout = IdleTimeout,
            TotalTimeout = TotalTimeout,
            CleanupTimeout = CleanupTimeout
        };
    }
}

public sealed class ProviderStreamLimits
{
    public ProviderStreamLimits(
        int maxEventsPerAttempt = 8_192,
        int maxTextUtf8Bytes = 1_048_576,
        int maxReasoningUtf8Bytes = 1_048_576,
        int maxToolCalls = 128,
        int maxToolNameUtf8Bytes = 512,
        int maxToolArgumentsUtf8Bytes = 262_144,
        int maxTotalToolArgumentsUtf8Bytes = 1_048_576)
    {
        if (maxEventsPerAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEventsPerAttempt));
        }

        if (maxTextUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTextUtf8Bytes));
        }

        if (maxReasoningUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReasoningUtf8Bytes));
        }

        if (maxToolCalls < 1 || maxToolCalls > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls));
        }

        if (maxToolNameUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolNameUtf8Bytes));
        }

        if (maxToolArgumentsUtf8Bytes < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolArgumentsUtf8Bytes));
        }

        if (maxTotalToolArgumentsUtf8Bytes < maxToolArgumentsUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalToolArgumentsUtf8Bytes),
                "The aggregate tool-argument limit cannot be smaller than the per-call limit.");
        }

        MaxEventsPerAttempt = maxEventsPerAttempt;
        MaxTextUtf8Bytes = maxTextUtf8Bytes;
        MaxReasoningUtf8Bytes = maxReasoningUtf8Bytes;
        MaxToolCalls = maxToolCalls;
        MaxToolNameUtf8Bytes = maxToolNameUtf8Bytes;
        MaxToolArgumentsUtf8Bytes = maxToolArgumentsUtf8Bytes;
        MaxTotalToolArgumentsUtf8Bytes = maxTotalToolArgumentsUtf8Bytes;
    }

    public int MaxEventsPerAttempt { get; }

    public int MaxTextUtf8Bytes { get; }

    public int MaxReasoningUtf8Bytes { get; }

    public int MaxToolCalls { get; }

    public int MaxToolNameUtf8Bytes { get; }

    public int MaxToolArgumentsUtf8Bytes { get; }

    public int MaxTotalToolArgumentsUtf8Bytes { get; }
}

public interface IRuntimeDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemRuntimeDelay : IRuntimeDelay
{
    public async ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ProviderAttemptResult
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string? Text { get; set; }

    public string? ReasoningContent { get; set; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; set; } =
        Array.Empty<ModelToolCall>();

    public ProviderUsage Usage { get; set; } = new();

    public string? FinishReason { get; set; }
}

public sealed class ProviderUsageNotice
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public ProviderUsage Usage { get; set; } = new();
}

public sealed class ProviderDispatchNotice
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;
}

public sealed class ProviderDispatchKnownZeroNotice
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class ProviderResultDiscardedNotice
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class ProviderUsageUncertainNotice
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;
}

public static class ProviderAttemptNoticeKinds
{
    public const string Retry = "retry";

    public const string Fallback = "fallback";
}

public sealed class ProviderAttemptNotice
{
    public string Kind { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string? NextProviderId { get; set; }

    public int AttemptNumber { get; set; }

    public string ErrorCode { get; set; } = string.Empty;

    public string ErrorCategory { get; set; } = string.Empty;

    public long DelayMilliseconds { get; set; }
}

public sealed class ProviderAttemptRunner
{
    private static readonly TimeSpan CancellationCleanupGrace =
        TimeSpan.FromMilliseconds(50);

    private readonly IReadOnlyList<IStreamingModelProvider> _providers;
    private readonly IReadOnlyList<string> _providerIds;
    private readonly ProviderRetryPolicy _policy;
    private readonly IRuntimeDelay _delay;
    private readonly IRuntimeDelay _eventWaitDelay;
    private readonly IRuntimeIdGenerator _ids;
    private readonly ProviderStreamLimits _streamLimits;
    private readonly ConcurrentDictionary<string, int> _quarantinedProviders =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failedCleanupProviders =
        new(StringComparer.Ordinal);

    public ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        if (providers.Count == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        if (providers.Count > 16)
        {
            throw new ArgumentException(
                "A fallback chain cannot contain more than 16 providers.",
                nameof(providers));
        }

        if (providers.Any(provider => provider is null))
        {
            throw new ArgumentException(
                "Provider lists cannot contain null entries.",
                nameof(providers));
        }

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        var stableProviderIds = new List<string>(providers.Count);
        foreach (var provider in providers)
        {
            var providerId = RuntimeGuard.RequiredUtf8(
                provider.ProviderId,
                128,
                nameof(providers));
            if (!providerIds.Add(providerId))
            {
                throw new ArgumentException(
                    "Provider ids must be unique within a fallback chain.",
                    nameof(providers));
            }

            stableProviderIds.Add(providerId);
        }

        _providers = providers.ToArray();
        _providerIds = stableProviderIds.ToArray();
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy)))
            .Snapshot();
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _eventWaitDelay = new SystemRuntimeDelay();
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _streamLimits = streamLimits ?? new ProviderStreamLimits();
    }

    internal ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits,
        IRuntimeDelay eventWaitDelay)
        : this(providers, policy, delay, ids, streamLimits)
    {
        _eventWaitDelay = eventWaitDelay
            ?? throw new ArgumentNullException(nameof(eventWaitDelay));
    }

    public string PrimaryProviderId => _providerIds[0];

    public async ValueTask<ProviderAttemptResult> RunAsync(
        string runId,
        string runAttemptId,
        string turnId,
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools,
        AttemptFence fence,
        Func<ModelStreamEvent, ValueTask>? onCurrentEvent,
        CancellationToken cancellationToken,
        Action<ProviderAttemptNotice>? onLifecycleNotice = null,
        int? estimatedPromptTokens = null,
        int? maxOutputTokens = null,
        Action<Task>? onDetachedCleanup = null,
        Func<ProviderUsageNotice, ValueTask>? onUsage = null,
        Func<ProviderUsageUncertainNotice, ValueTask>? onUsageUncertain = null,
        Func<ProviderDispatchNotice, ValueTask>? onDispatch = null,
        Func<ProviderDispatchKnownZeroNotice, ValueTask>?
            onDispatchKnownZero = null,
        Func<ProviderResultDiscardedNotice, ValueTask>?
            onResultDiscarded = null)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        if (estimatedPromptTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedPromptTokens));
        }

        if (maxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        var messageSnapshot = SnapshotMessages(messages);
        var toolSnapshot = SnapshotTools(tools);

        ProviderException? lastError = null;
        var aggregateUsage = new ProviderUsage();
        var usageSettledStreams = new HashSet<string>(StringComparer.Ordinal);
        for (var providerIndex = 0;
             providerIndex < _providers.Count;
             providerIndex++)
        {
            var provider = _providers[providerIndex];
            var providerId = _providerIds[providerIndex];
            if (IsProviderQuarantined(providerId))
            {
                lastError = new ProviderException(
                    "provider_cleanup_pending",
                    "provider",
                    "A previous attempt for this provider is still shutting down.",
                    false);
                NotifyFallback(
                    onLifecycleNotice,
                    providerIndex,
                    attemptNumber: 0,
                    lastError);
                continue;
            }

            ProviderCapabilities capabilities;
            try
            {
                capabilities = SnapshotCapabilities(provider.Capabilities);
                EnsureCapabilities(
                    providerId,
                    capabilities,
                    toolSnapshot);
                _ = ResolveMaxOutputTokens(
                    capabilities,
                    estimatedPromptTokens,
                    maxOutputTokens);
            }
            catch (ProviderException exception)
            {
                lastError = exception;
                NotifyFallback(
                    onLifecycleNotice,
                    providerIndex,
                    attemptNumber: 0,
                    exception);
                continue;
            }

            for (var attemptNumber = 0;
                 attemptNumber < _policy.MaxAttemptsPerProvider;
                 attemptNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingOutputTokens =
                    RemainingOutputTokens(
                        maxOutputTokens,
                        aggregateUsage);
                if (remainingOutputTokens.HasValue
                    && remainingOutputTokens.Value < 1)
                {
                    throw new ProviderException(
                        "provider_token_budget_exhausted",
                        "budget",
                        "Earlier provider attempts exhausted the turn token budget.",
                        false);
                }

                var providerMaxOutputTokens = ResolveMaxOutputTokens(
                    capabilities,
                    estimatedPromptTokens,
                    remainingOutputTokens);
                var providerAttemptId = _ids.NewId("provider-attempt");
                var streamAttemptId = _ids.NewId("stream-attempt");
                var identity = new AttemptIdentity
                {
                    RunAttemptId = runAttemptId,
                    TurnId = turnId,
                    ProviderAttemptId = providerAttemptId,
                    StreamAttemptId = streamAttemptId
                };
                var generation = fence.Activate(identity);
                var request = new StreamingModelRequest
                {
                    RunId = runId,
                    RunAttemptId = runAttemptId,
                    TurnId = turnId,
                    ProviderAttemptId = providerAttemptId,
                    StreamAttemptId = streamAttemptId,
                    Messages = SnapshotMessages(messageSnapshot),
                    Tools = SnapshotTools(toolSnapshot),
                    MaxOutputTokens = providerMaxOutputTokens
                };

                if (onDispatch is not null)
                {
                    await onDispatch(
                            new ProviderDispatchNotice
                            {
                                ProviderId = providerId,
                                ProviderAttemptId = providerAttemptId,
                                StreamAttemptId = streamAttemptId
                            })
                        .ConfigureAwait(false);
                }

                try
                {
                    var result = await ConsumeAttemptAsync(
                            provider,
                            providerId,
                            request,
                            identity,
                            generation,
                            fence,
                            onCurrentEvent,
                            cancellationToken,
                            onDetachedCleanup,
                            ObserveUsageAsync,
                            onUsageUncertain)
                        .ConfigureAwait(false);
                    result.Usage = CloneUsage(aggregateUsage);
                    return result;
                }
                catch (ProviderException exception)
                {
                    fence.Invalidate();
                    var usageWasSettled = usageSettledStreams.Contains(
                        streamAttemptId);
                    await DiscardSettledResultAsync(
                            providerId,
                            providerAttemptId,
                            streamAttemptId,
                            exception.Code)
                        .ConfigureAwait(false);
                    if (exception.UsageKnownToBeZero
                        && !usageWasSettled
                        && onDispatchKnownZero is not null)
                    {
                        await onDispatchKnownZero(
                                new ProviderDispatchKnownZeroNotice
                                {
                                    ProviderId = providerId,
                                    ProviderAttemptId = providerAttemptId,
                                    StreamAttemptId = streamAttemptId,
                                    ReasonCode = exception.Code
                                })
                            .ConfigureAwait(false);
                    }

                    if (!exception.Retryable)
                    {
                        throw;
                    }

                    lastError = exception;
                    if (IsProviderQuarantined(providerId))
                    {
                        throw new ProviderException(
                            "provider_cleanup_pending",
                            "provider",
                            "The provider attempt failed and is still shutting down.",
                            false,
                            innerException: exception);
                    }

                    if (attemptNumber + 1 >= _policy.MaxAttemptsPerProvider)
                    {
                        break;
                    }

                    var delay = exception.RetryAfter
                        ?? Backoff(attemptNumber);
                    Notify(
                        onLifecycleNotice,
                        new ProviderAttemptNotice
                        {
                            Kind = ProviderAttemptNoticeKinds.Retry,
                            ProviderId = providerId,
                            AttemptNumber = attemptNumber + 1,
                            ErrorCode = exception.Code,
                            ErrorCategory = exception.Category,
                            DelayMilliseconds = Math.Max(
                                0,
                                (long)delay.TotalMilliseconds)
                        });
                    await DelayWithDetachedCancellationAsync(
                            _delay,
                            delay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    fence.Invalidate();
                    await DiscardSettledResultAsync(
                            providerId,
                            providerAttemptId,
                            streamAttemptId,
                            exception is OperationCanceledException
                                ? "provider_attempt_cancelled"
                                : "provider_attempt_failed")
                        .ConfigureAwait(false);
                    throw;
                }
            }

            if (lastError is not null)
            {
                NotifyFallback(
                    onLifecycleNotice,
                    providerIndex,
                    _policy.MaxAttemptsPerProvider,
                    lastError);
            }
        }

        throw lastError
            ?? new ProviderException(
                "provider_exhausted",
                "provider",
                "No compatible provider completed the turn.",
                false);

        async ValueTask ObserveUsageAsync(ProviderUsageNotice notice)
        {
            AddUsage(aggregateUsage, notice.Usage);
            if (onUsage is not null)
            {
                await onUsage(notice).ConfigureAwait(false);
            }

            usageSettledStreams.Add(notice.StreamAttemptId);
        }

        async ValueTask DiscardSettledResultAsync(
            string providerId,
            string providerAttemptId,
            string streamAttemptId,
            string reasonCode)
        {
            if (!usageSettledStreams.Remove(streamAttemptId)
                || onResultDiscarded is null)
            {
                return;
            }

            await onResultDiscarded(
                    new ProviderResultDiscardedNotice
                    {
                        ProviderId = providerId,
                        ProviderAttemptId = providerAttemptId,
                        StreamAttemptId = streamAttemptId,
                        ReasonCode = reasonCode
                    })
                .ConfigureAwait(false);
        }
    }

    private void NotifyFallback(
        Action<ProviderAttemptNotice>? notify,
        int providerIndex,
        int attemptNumber,
        ProviderException exception)
    {
        if (providerIndex + 1 >= _providers.Count)
        {
            return;
        }

        Notify(
            notify,
            new ProviderAttemptNotice
            {
                Kind = ProviderAttemptNoticeKinds.Fallback,
                ProviderId = _providerIds[providerIndex],
                NextProviderId = _providerIds[providerIndex + 1],
                AttemptNumber = attemptNumber,
                ErrorCode = exception.Code,
                ErrorCategory = exception.Category
            });
    }

    private static void Notify(
        Action<ProviderAttemptNotice>? notify,
        ProviderAttemptNotice notice)
    {
        if (notify is null)
        {
            return;
        }

        try
        {
            notify(notice);
        }
        catch
        {
            // Lifecycle notifications must not change provider failover.
        }
    }

    private async ValueTask<ProviderAttemptResult> ConsumeAttemptAsync(
        IStreamingModelProvider provider,
        string providerId,
        StreamingModelRequest request,
        AttemptIdentity identity,
        long generation,
        AttemptFence fence,
        Func<ModelStreamEvent, ValueTask>? onCurrentEvent,
        CancellationToken cancellationToken,
        Action<Task>? onDetachedCleanup,
        Func<ProviderUsageNotice, ValueTask> onUsage,
        Func<ProviderUsageUncertainNotice, ValueTask>? onUsageUncertain)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new ToolCallFragmentAssembler(_streamLimits);
        var usage = new ProviderUsage();
        var usageSeen = false;
        string? finishReason = null;
        var completedSeen = false;
        long lastOrdinal = -1;
        var eventCount = 0;
        var textUtf8Bytes = 0;
        var reasoningUtf8Bytes = 0;
        var elapsed = Stopwatch.StartNew();
        var attemptCancellation = new CancellationTokenSource();
        var usageUncertainReported = false;

        async ValueTask ReportUsageUncertainAsync(string reasonCode)
        {
            if (usageUncertainReported)
            {
                return;
            }

            usageUncertainReported = true;
            if (onUsageUncertain is not null)
            {
                await onUsageUncertain(
                        new ProviderUsageUncertainNotice
                        {
                            ProviderId = providerId,
                            ProviderAttemptId = identity.ProviderAttemptId,
                            StreamAttemptId = identity.StreamAttemptId,
                            ReasonCode = reasonCode
                        })
                    .ConfigureAwait(false);
            }
        }

        IAsyncEnumerator<ModelStreamEvent> enumerator;
        try
        {
            var stream = provider.StreamAsync(
                    request,
                    attemptCancellation.Token)
                ?? throw new InvalidOperationException(
                    "The provider returned a null stream.");
            enumerator = stream.GetAsyncEnumerator(attemptCancellation.Token)
                ?? throw new InvalidOperationException(
                    "The provider returned a null stream enumerator.");
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            RegisterCancellationCleanup(
                attemptCancellation,
                providerId,
                onDetachedCleanup);
            await ReportUsageUncertainAsync(
                    "provider_cancelled_before_usage")
                .ConfigureAwait(false);
            throw;
        }
        catch (ProviderException exception) when (
            exception.UsageKnownToBeZero)
        {
            RegisterCancellationCleanup(
                attemptCancellation,
                providerId,
                onDetachedCleanup);
            throw;
        }
        catch (ProviderException exception)
        {
            RegisterCancellationCleanup(
                attemptCancellation,
                providerId,
                onDetachedCleanup);
            await ReportUsageUncertainAsync(
                    exception.Code)
                .ConfigureAwait(false);
            throw exception.Retryable
                ? new ProviderException(
                    "provider_usage_unknown",
                    "provider",
                    "The provider attempt failed before usage was accounted.",
                    false,
                    innerException: exception)
                : exception;
        }
        catch (Exception exception)
        {
            RegisterCancellationCleanup(
                attemptCancellation,
                providerId,
                onDetachedCleanup);
            await ReportUsageUncertainAsync(
                    "provider_usage_unknown")
                .ConfigureAwait(false);
            throw new ProviderException(
                "provider_usage_unknown",
                "provider",
                "The provider attempt failed before usage was accounted.",
                false,
                innerException: exception);
        }

        var cleanupHandled = false;
        Exception? primaryFailure = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= _policy.TotalTimeout)
                {
                    fence.Invalidate();
                    throw new ProviderException(
                        "provider_total_timeout",
                        "network",
                        "The provider exceeded the total turn timeout.",
                        true);
                }

                var moveNext = enumerator.MoveNextAsync().AsTask();
                var remaining = _policy.TotalTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    fence.Invalidate();
                    throw new ProviderException(
                        "provider_total_timeout",
                        "network",
                        "The provider exceeded the total turn timeout.",
                        true);
                }

                var wait = remaining < _policy.IdleTimeout
                    ? remaining
                    : _policy.IdleTimeout;
                var waitCancellation = new CancellationTokenSource();
                var idle = _eventWaitDelay
                    .DelayAsync(wait, waitCancellation.Token)
                    .AsTask();
                var cancellationSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(
                    () => cancellationSignal.TrySetResult(true)))
                {
                    var completed = await Task.WhenAny(
                            moveNext,
                            idle,
                            cancellationSignal.Task)
                        .ConfigureAwait(false);
                    var waitCleanup = CancelObserveAndDisposeAsync(
                        idle,
                        waitCancellation);
                    _ = await Task.WhenAny(
                            waitCleanup,
                            Task.Delay(CancellationCleanupGrace))
                        .ConfigureAwait(false);

                    if (ReferenceEquals(completed, moveNext)
                        || moveNext.IsCompleted)
                    {
                        completed = moveNext;
                    }

                    if (completed != moveNext && !moveNext.IsCompleted)
                    {
                        fence.Invalidate();
                        var cancellationCleanup = CancelDetachedAsync(
                            attemptCancellation);
                        var cleanup = CompleteAndDisposeDetachedAsync(
                            moveNext,
                            enumerator,
                            attemptCancellation,
                            cancellationCleanup);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cleanupHandled = true;
                            var cancellationWinner = await Task.WhenAny(
                                    cancellationCleanup,
                                    Task.Delay(CancellationCleanupGrace))
                                .ConfigureAwait(false);
                            if (ReferenceEquals(
                                    cancellationWinner,
                                    cancellationCleanup)
                                && await cancellationCleanup
                                    .ConfigureAwait(false))
                            {
                                var cooperativeCleanupWinner =
                                    await Task.WhenAny(
                                            cleanup,
                                            Task.Delay(
                                                _policy.CleanupTimeout))
                                        .ConfigureAwait(false);
                                if (ReferenceEquals(
                                        cooperativeCleanupWinner,
                                        cleanup))
                                {
                                    await cleanup.ConfigureAwait(false);
                                    cancellationToken
                                        .ThrowIfCancellationRequested();
                                }
                            }

                            var observedCleanup = RegisterDetachedCleanup(
                                providerId,
                                cleanup);
                            NotifyDetachedCleanup(
                                onDetachedCleanup,
                                observedCleanup);
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var cleanupWinner = await Task.WhenAny(
                                cleanup,
                                Task.Delay(_policy.CleanupTimeout))
                            .ConfigureAwait(false);
                        cleanupHandled = true;
                        if (!ReferenceEquals(cleanupWinner, cleanup))
                        {
                            var observedCleanup = RegisterDetachedCleanup(
                                providerId,
                                cleanup);
                            NotifyDetachedCleanup(
                                onDetachedCleanup,
                                observedCleanup);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new ProviderException(
                                elapsed.Elapsed >= _policy.TotalTimeout
                                    ? "provider_total_timeout"
                                    : "provider_idle_timeout",
                                "network",
                                "The provider stream stopped producing events and did not shut down in time.",
                                false);
                        }

                        try
                        {
                            await cleanup.ConfigureAwait(false);
                        }
                        catch
                        {
                            MarkCleanupFailed(providerId);
                            throw;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed >= _policy.TotalTimeout)
                        {
                            throw new ProviderException(
                                "provider_total_timeout",
                                "network",
                                "The provider exceeded the total turn timeout.",
                                true);
                        }

                        throw new ProviderException(
                            "provider_idle_timeout",
                            "network",
                            "The provider stream stopped producing events.",
                            true);
                    }
                }

                if (!await moveNext.ConfigureAwait(false))
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var item = enumerator.Current;
                if (item is null)
                {
                    throw new ProviderException(
                        "provider_null_event",
                        "provider",
                        "The provider emitted a null stream event.",
                        false);
                }

                if (!fence.IsCurrent(generation, identity)
                    || !string.Equals(
                        item.StreamAttemptId,
                        identity.StreamAttemptId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (item.Ordinal <= lastOrdinal)
                {
                    throw new ProviderException(
                        "provider_stream_order",
                        "provider",
                        "The provider emitted an out-of-order stream event.",
                        true);
                }

                if ((usageSeen || completedSeen)
                    && !string.Equals(
                        item.Kind,
                        ModelStreamEventKinds.Usage,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        item.Kind,
                        ModelStreamEventKinds.Completed,
                        StringComparison.Ordinal))
                {
                    throw new ProviderException(
                        "provider_content_after_terminal_marker",
                        "provider",
                        "The provider emitted content after a terminal stream marker.",
                        false);
                }

                eventCount++;
                if (eventCount > _streamLimits.MaxEventsPerAttempt)
                {
                    throw LimitExceeded(
                        "provider_event_limit",
                        "The provider stream exceeded the event limit.");
                }

                lastOrdinal = item.Ordinal;
                switch (item.Kind)
                {
                    case ModelStreamEventKinds.TextDelta:
                        AddUtf8Bytes(
                            item.TextDelta,
                            ref textUtf8Bytes,
                            _streamLimits.MaxTextUtf8Bytes,
                            "provider_text_limit",
                            "The provider text exceeded the output limit.");
                        text.Append(item.TextDelta);
                        break;
                    case ModelStreamEventKinds.ReasoningDelta:
                        AddUtf8Bytes(
                            item.ReasoningDelta,
                            ref reasoningUtf8Bytes,
                            _streamLimits.MaxReasoningUtf8Bytes,
                            "provider_reasoning_limit",
                            "The provider reasoning content exceeded the output limit.");
                        reasoning.Append(item.ReasoningDelta);
                        break;
                    case ModelStreamEventKinds.ToolCallDelta:
                        toolCalls.Append(item);
                        break;
                    case ModelStreamEventKinds.Usage:
                        if (usageSeen)
                        {
                            throw LimitExceeded(
                                "provider_usage_duplicate",
                                "The provider emitted more than one usage event.");
                        }

                        if (item.Usage is null)
                        {
                            await ReportUsageUncertainAsync(
                                    "provider_usage_invalid")
                                .ConfigureAwait(false);
                            throw LimitExceeded(
                                "provider_usage_invalid",
                                "The provider emitted an invalid usage event.");
                        }

                        EnsureValidUsage(item.Usage);
                        usage = CloneUsage(item.Usage);
                        usageSeen = true;
                        await onUsage(
                                new ProviderUsageNotice
                                {
                                    ProviderId = providerId,
                                    ProviderAttemptId =
                                        identity.ProviderAttemptId,
                                    StreamAttemptId =
                                        identity.StreamAttemptId,
                                    Usage = CloneUsage(usage)
                                })
                            .ConfigureAwait(false);
                        break;
                    case ModelStreamEventKinds.Completed:
                        if (completedSeen)
                        {
                            throw new ProviderException(
                                "provider_duplicate_completion",
                                "provider",
                                "The provider emitted more than one completion marker.",
                                true);
                        }

                        completedSeen = true;
                        finishReason = item.FinishReason;
                        break;
                    default:
                        throw new ProviderException(
                            "provider_unknown_event",
                            "provider",
                            "The provider emitted an unsupported stream event.",
                            false);
                }

                if (onCurrentEvent is not null)
                {
                    await onCurrentEvent(item).ConfigureAwait(false);
                }
            }
        }
        catch (ProviderException exception) when (
            exception.UsageKnownToBeZero)
        {
            primaryFailure = exception;
            throw;
        }
        catch (ProviderException exception) when (
            exception.Retryable
            && !usageSeen
            && !exception.UsageKnownToBeZero)
        {
            await ReportUsageUncertainAsync(
                    "provider_usage_unknown")
                .ConfigureAwait(false);
            primaryFailure = new ProviderException(
                "provider_usage_unknown",
                "provider",
                "The provider attempt failed before usage was accounted.",
                false,
                innerException: exception);
            throw primaryFailure;
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested)
        {
            if (!usageSeen)
            {
                await ReportUsageUncertainAsync(
                        "provider_usage_unknown")
                    .ConfigureAwait(false);
                primaryFailure = new ProviderException(
                    "provider_usage_unknown",
                    "provider",
                    "The provider attempt ended before usage was accounted.",
                    false,
                    innerException: exception);
            }
            else
            {
                primaryFailure = new ProviderException(
                    "provider_stream_cancelled",
                    "network",
                    "The provider cancelled its stream unexpectedly.",
                    true,
                    innerException: exception);
            }

            throw primaryFailure;
        }
        catch (Exception exception)
        {
            if (!usageSeen)
            {
                await ReportUsageUncertainAsync(
                        exception is ProviderException providerFailure
                            ? providerFailure.Code
                            : exception is OperationCanceledException
                                ? "provider_cancelled_before_usage"
                                : "provider_usage_unknown")
                    .ConfigureAwait(false);
            }

            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (!cleanupHandled)
            {
                var cancellationCleanup = CancelDetachedAsync(
                    attemptCancellation);
                var cleanup = DisposeAttemptAsync(
                    enumerator,
                    attemptCancellation,
                    cancellationCleanup);
                var completed = await Task.WhenAny(
                        cleanup,
                        Task.Delay(_policy.CleanupTimeout))
                    .ConfigureAwait(false);
                if (ReferenceEquals(completed, cleanup))
                {
                    try
                    {
                        await cleanup.ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure) when (
                        primaryFailure is not null)
                    {
                        MarkCleanupFailed(providerId);
                        // Preserve terminal control and budget results. A
                        // retryable provider failure is downgraded because a
                        // failed cleanup cannot safely admit another attempt.
                        if (primaryFailure is ProviderException
                            {
                                Retryable: true
                            })
                        {
                            throw new ProviderException(
                                "provider_cleanup_failed",
                                "provider",
                                "The provider stream failed during shutdown.",
                                false,
                                innerException: cleanupFailure);
                        }
                    }
                    catch
                    {
                        MarkCleanupFailed(providerId);
                        if (!usageSeen)
                        {
                            await ReportUsageUncertainAsync(
                                    "provider_cleanup_failed")
                                .ConfigureAwait(false);
                        }

                        throw;
                    }
                }
                else
                {
                    cleanupHandled = true;
                    var observedCleanup = RegisterDetachedCleanup(
                        providerId,
                        cleanup);
                    NotifyDetachedCleanup(
                        onDetachedCleanup,
                        observedCleanup);
                    if (primaryFailure is null)
                    {
                        if (!usageSeen)
                        {
                            await ReportUsageUncertainAsync(
                                    "provider_cleanup_timeout")
                                .ConfigureAwait(false);
                        }

                        throw new ProviderException(
                            "provider_cleanup_timeout",
                            "provider",
                            "The provider stream did not shut down in time.",
                            false);
                    }
                }
            }
        }

        if (cancellationToken.IsCancellationRequested && !usageSeen)
        {
            await ReportUsageUncertainAsync(
                    "provider_cancelled_before_usage")
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!fence.IsCurrent(generation, identity))
        {
            if (!usageSeen)
            {
                await ReportUsageUncertainAsync(
                        "provider_attempt_superseded")
                    .ConfigureAwait(false);
            }

            throw new OperationCanceledException(
                "The provider attempt was superseded.",
                cancellationToken);
        }

        if (!usageSeen)
        {
            await ReportUsageUncertainAsync(
                    "provider_usage_missing")
                .ConfigureAwait(false);
        }

        if (!completedSeen || string.IsNullOrWhiteSpace(finishReason))
        {
            throw new ProviderException(
                usageSeen
                    ? "provider_stream_incomplete"
                    : "provider_usage_unknown",
                "network",
                usageSeen
                    ? "The provider stream ended without a completion reason."
                    : "The provider attempt ended before usage was accounted.",
                usageSeen);
        }

        if (string.Equals(finishReason, "length", StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_output_incomplete",
                "provider",
                "The provider could not complete the model response.",
                false);
        }

        if (string.Equals(
                finishReason,
                "insufficient_system_resource",
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_output_incomplete",
                "provider",
                "The provider could not complete the model response.",
                usageSeen);
        }

        if (string.Equals(finishReason, "content_filter", StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_content_filtered",
                "provider",
                "The provider withheld the model response.",
                false);
        }

        if (!usageSeen)
        {
            throw new ProviderException(
                "provider_usage_missing",
                "provider",
                "The provider completed without usage accounting.",
                false);
        }

        var assembledToolCalls = toolCalls.Complete();
        if (assembledToolCalls.Count > 0
            && !string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_tool_finish_mismatch",
                "provider",
                "The provider emitted tool calls without a tool-call completion reason.",
                usageSeen);
        }

        if (text.Length == 0 && assembledToolCalls.Count == 0)
        {
            throw new ProviderException(
                "provider_empty_response",
                "provider",
                "The provider completed without text or tool calls.",
                true);
        }

        return new ProviderAttemptResult
        {
            ProviderId = providerId,
            ProviderAttemptId = identity.ProviderAttemptId,
            StreamAttemptId = identity.StreamAttemptId,
            Text = text.Length == 0 ? null : text.ToString(),
            ReasoningContent =
                reasoning.Length == 0 ? null : reasoning.ToString(),
            ToolCalls = assembledToolCalls,
            Usage = usage,
            FinishReason = finishReason
        };
    }

    private static void EnsureCapabilities(
        string providerId,
        ProviderCapabilities capabilities,
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools)
    {
        if (!capabilities.Streaming)
        {
            throw new ProviderException(
                "provider_missing_streaming",
                "capability",
                $"Provider '{providerId}' does not support streaming.",
                false);
        }

        if (tools.Count > 0 && !capabilities.ToolCalling)
        {
            throw new ProviderException(
                "provider_missing_tools",
                "capability",
                $"Provider '{providerId}' does not support tool calling.",
                false);
        }
    }

    private static int? ResolveMaxOutputTokens(
        ProviderCapabilities capabilities,
        int? estimatedPromptTokens,
        int? requestedMaxOutputTokens)
    {
        var contextLimit = capabilities.MaxContextTokens;
        if (contextLimit <= 0 || !estimatedPromptTokens.HasValue)
        {
            return requestedMaxOutputTokens;
        }

        var remainingContext = (long)contextLimit - estimatedPromptTokens.Value;
        if (remainingContext < 1)
        {
            throw new ProviderException(
                "provider_context_limit_exceeded",
                "capability",
                "The compiled prompt does not fit the provider context window.",
                false);
        }

        var contextOutputLimit = (int)Math.Min(int.MaxValue, remainingContext);
        return requestedMaxOutputTokens.HasValue
            ? Math.Min(requestedMaxOutputTokens.Value, contextOutputLimit)
            : contextOutputLimit;
    }

    private static ProviderCapabilities SnapshotCapabilities(
        ProviderCapabilities capabilities)
    {
        if (capabilities is null)
        {
            throw new ProviderException(
                "provider_capabilities_invalid",
                "capability",
                "The provider returned invalid capabilities.",
                false,
                usageKnownToBeZero: true);
        }

        if (capabilities.MaxContextTokens < 0)
        {
            throw new ProviderException(
                "provider_capabilities_invalid",
                "capability",
                "The provider returned invalid context capabilities.",
                false,
                usageKnownToBeZero: true);
        }

        return new ProviderCapabilities
        {
            Streaming = capabilities.Streaming,
            ToolCalling = capabilities.ToolCalling,
            JsonOutput = capabilities.JsonOutput,
            MaxContextTokens = capabilities.MaxContextTokens
        };
    }

    private static int? RemainingOutputTokens(
        int? initialOutputTokens,
        ProviderUsage aggregateUsage)
    {
        if (!initialOutputTokens.HasValue)
        {
            return null;
        }

        var consumed = (long)aggregateUsage.InputTokens
                       + aggregateUsage.OutputTokens;
        var remaining = (long)initialOutputTokens.Value - consumed;
        return remaining < 1
            ? 0
            : (int)Math.Min(int.MaxValue, remaining);
    }

    private static ProviderUsage CloneUsage(ProviderUsage usage)
    {
        return new ProviderUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CostUsd = usage.CostUsd
        };
    }

    private static void AddUsage(
        ProviderUsage aggregate,
        ProviderUsage usage)
    {
        aggregate.InputTokens = (int)Math.Min(
            int.MaxValue,
            (long)aggregate.InputTokens + usage.InputTokens);
        aggregate.OutputTokens = (int)Math.Min(
            int.MaxValue,
            (long)aggregate.OutputTokens + usage.OutputTokens);
        aggregate.CostUsd = RuntimePromptBuilder.AddCost(
            aggregate.CostUsd,
            usage.CostUsd);
    }

    private TimeSpan Backoff(int attemptNumber)
    {
        var factor = Math.Pow(2, attemptNumber);
        var milliseconds = Math.Min(
            _policy.InitialDelay.TotalMilliseconds * factor,
            _policy.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private bool IsProviderQuarantined(string providerId)
    {
        return _failedCleanupProviders.ContainsKey(providerId)
               || (_quarantinedProviders.TryGetValue(
                       providerId,
                       out var attempts)
                   && attempts > 0);
    }

    private void MarkCleanupFailed(string providerId)
    {
        _failedCleanupProviders.TryAdd(providerId, 0);
    }

    private Task RegisterDetachedCleanup(
        string providerId,
        Task cleanup)
    {
        _quarantinedProviders.AddOrUpdate(
            providerId,
            1,
            static (_, attempts) => checked(attempts + 1));
        return ObserveQuarantinedCleanupAsync(providerId, cleanup);
    }

    private async Task ObserveQuarantinedCleanupAsync(
        string providerId,
        Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // The caller already received a bounded provider failure.
            MarkCleanupFailed(providerId);
        }
        finally
        {
            _quarantinedProviders.AddOrUpdate(
                providerId,
                0,
                static (_, attempts) => Math.Max(0, attempts - 1));
            if (_quarantinedProviders.TryGetValue(
                    providerId,
                    out var remaining)
                && remaining == 0)
            {
                _ = ((ICollection<KeyValuePair<string, int>>)
                        _quarantinedProviders)
                    .Remove(new KeyValuePair<string, int>(providerId, 0));
            }
        }
    }

    private static IReadOnlyList<NormalizedMessage> SnapshotMessages(
        IReadOnlyList<NormalizedMessage> messages)
    {
        var snapshot = new NormalizedMessage[messages.Count];
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index]
                ?? throw new ArgumentException(
                    "Provider message lists cannot contain null entries.",
                    nameof(messages));
            snapshot[index] = NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(message));
        }

        return snapshot;
    }

    private static IReadOnlyList<GameAgent.Protocol.ToolDescriptor> SnapshotTools(
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools)
    {
        var snapshot = new GameAgent.Protocol.ToolDescriptor[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index]
                ?? throw new ArgumentException(
                    "Provider tool lists cannot contain null entries.",
                    nameof(tools));
            snapshot[index] =
                GameAgent.Protocol.ProtocolJson.DeserializeToolDescriptor(
                    GameAgent.Protocol.ProtocolJson.Serialize(tool));
        }

        return snapshot;
    }

    private static void NotifyDetachedCleanup(
        Action<Task>? notify,
        Task cleanup)
    {
        if (notify is null)
        {
            return;
        }

        try
        {
            notify(cleanup);
        }
        catch
        {
            // Cleanup still remains quarantined by this runner.
        }
    }

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch
        {
            // Cancellation callbacks cannot bypass fencing, cleanup,
            // or the detached-attempt quarantine.
            return false;
        }
    }

    private static Task<bool> CancelDetachedAsync(
        CancellationTokenSource cancellation)
    {
        return Task.Run(() => TryCancel(cancellation));
    }

    private void RegisterCancellationCleanup(
        CancellationTokenSource cancellation,
        string providerId,
        Action<Task>? onDetachedCleanup)
    {
        var cleanup = CancelAndDisposeAsync(cancellation);
        var observedCleanup = RegisterDetachedCleanup(providerId, cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);
    }

    private static async Task CancelAndDisposeAsync(
        CancellationTokenSource cancellation)
    {
        try
        {
            await CancelDetachedAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static async ValueTask DelayWithDetachedCancellationAsync(
        IRuntimeDelay delay,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delayCancellation = new CancellationTokenSource();
        var delayTask = delay.DelayAsync(
                duration,
                delayCancellation.Token)
            .AsTask();
        if (!cancellationToken.CanBeCanceled)
        {
            try
            {
                await delayTask.ConfigureAwait(false);
            }
            finally
            {
                delayCancellation.Dispose();
            }

            return;
        }

        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => cancellationSignal.TrySetResult(true));
        var completed = await Task.WhenAny(
                delayTask,
                cancellationSignal.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, delayTask))
        {
            try
            {
                await delayTask.ConfigureAwait(false);
            }
            finally
            {
                delayCancellation.Dispose();
            }

            return;
        }

        _ = CancelObserveAndDisposeAsync(
            delayTask,
            delayCancellation);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task CancelObserveAndDisposeAsync(
        Task operation,
        CancellationTokenSource cancellation)
    {
        var cancellationTask = CancelDetachedAsync(cancellation);
        try
        {
            await ObserveDetachedAsync(operation).ConfigureAwait(false);
            await cancellationTask.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static async Task ObserveDetachedAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The owning attempt already selected a terminal outcome.
        }
    }

    private static async Task DisposeAttemptAsync(
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        CancellationTokenSource attemptCancellation,
        Task cancellationCleanup)
    {
        try
        {
            await cancellationCleanup.ConfigureAwait(false);
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                "provider_cleanup_failed",
                "provider",
                "The provider stream failed during shutdown.",
                false,
                innerException: exception);
        }
        finally
        {
            attemptCancellation.Dispose();
        }
    }

    private static async Task CompleteAndDisposeDetachedAsync(
        Task<bool> moveNext,
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        CancellationTokenSource attemptCancellation,
        Task cancellationCleanup)
    {
        try
        {
            await moveNext.ConfigureAwait(false);
        }
        catch
        {
            // The attempt has already been fenced off. Detached cleanup must not
            // surface provider failures on the finalizer path.
        }

        await DisposeAttemptAsync(
                enumerator,
                attemptCancellation,
                cancellationCleanup)
            .ConfigureAwait(false);
    }

    private static void AddUtf8Bytes(
        string? value,
        ref int total,
        int maximum,
        string code,
        string message)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if ((long)total + bytes > maximum)
        {
            throw LimitExceeded(code, message);
        }

        total += bytes;
    }

    private static void EnsureValidUsage(ProviderUsage usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0)
        {
            throw LimitExceeded(
                "provider_usage_invalid",
                "The provider emitted invalid token usage.");
        }

        if (usage.CostUsd is null
            || usage.CostUsd.Length > 64
            || !decimal.TryParse(
                usage.CostUsd,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var cost)
            || cost < 0)
        {
            throw LimitExceeded(
                "provider_usage_invalid",
                "The provider emitted invalid cost usage.");
        }
    }

    private static ProviderException LimitExceeded(string code, string message)
    {
        return new ProviderException(
            code,
            "provider",
            message,
            false);
    }
}

internal sealed class ToolCallFragmentAssembler
{
    private readonly ProviderStreamLimits _limits;
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Fragment> _fragments =
        new(StringComparer.Ordinal);
    private int _totalArgumentsUtf8Bytes;

    public ToolCallFragmentAssembler(ProviderStreamLimits limits)
    {
        _limits = limits;
    }

    public void Append(ModelStreamEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.ToolCallId))
        {
            throw new ProviderException(
                "provider_tool_call_id_missing",
                "provider",
                "A tool-call stream fragment omitted its identifier.",
                true);
        }

        if (!_fragments.TryGetValue(item.ToolCallId, out var fragment))
        {
            RuntimeGuard.RequiredId(item.ToolCallId, nameof(item.ToolCallId));
            if (_fragments.Count >= _limits.MaxToolCalls)
            {
                throw new ProviderException(
                    "provider_tool_call_limit",
                    "provider",
                    "The provider emitted too many tool calls.",
                    false);
            }

            fragment = new Fragment();
            _fragments.Add(item.ToolCallId, fragment);
            _order.Add(item.ToolCallId);
        }

        AddFragmentBytes(
            item.ToolNameDelta,
            ref fragment.NameUtf8Bytes,
            _limits.MaxToolNameUtf8Bytes,
            "provider_tool_name_limit",
            "A provider tool name exceeded the output limit.");
        AddFragmentBytes(
            item.ArgumentsJsonDelta,
            ref fragment.ArgumentsUtf8Bytes,
            _limits.MaxToolArgumentsUtf8Bytes,
            "provider_tool_arguments_limit",
            "Provider tool arguments exceeded the per-call output limit.");
        if (!string.IsNullOrEmpty(item.ArgumentsJsonDelta))
        {
            var argumentBytes = Encoding.UTF8.GetByteCount(item.ArgumentsJsonDelta);
            if ((long)_totalArgumentsUtf8Bytes + argumentBytes
                > _limits.MaxTotalToolArgumentsUtf8Bytes)
            {
                throw new ProviderException(
                    "provider_tool_arguments_total_limit",
                    "provider",
                    "Provider tool arguments exceeded the aggregate output limit.",
                    false);
            }

            _totalArgumentsUtf8Bytes += argumentBytes;
        }

        fragment.Name.Append(item.ToolNameDelta);
        fragment.Arguments.Append(item.ArgumentsJsonDelta);
    }

    public IReadOnlyList<ModelToolCall> Complete()
    {
        var calls = new List<ModelToolCall>(_order.Count);
        foreach (var id in _order)
        {
            var fragment = _fragments[id];
            JsonElement arguments;
            try
            {
                using var document = JsonDocument.Parse(fragment.Arguments.ToString());
                arguments = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new ProviderException(
                    "provider_tool_arguments_invalid",
                    "provider",
                    "A streamed tool call contained invalid JSON arguments.",
                    true,
                    innerException: exception);
            }

            calls.Add(new ModelToolCall
            {
                ToolCallId = id,
                Name = fragment.Name.ToString(),
                Arguments = arguments
            });
        }

        return calls;
    }

    private static void AddFragmentBytes(
        string? value,
        ref int total,
        int maximum,
        string code,
        string message)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if ((long)total + bytes > maximum)
        {
            throw new ProviderException(code, "provider", message, false);
        }

        total += bytes;
    }

    private sealed class Fragment
    {
        public StringBuilder Name { get; } = new();

        public StringBuilder Arguments { get; } = new();

        public int NameUtf8Bytes;

        public int ArgumentsUtf8Bytes;
    }
}
