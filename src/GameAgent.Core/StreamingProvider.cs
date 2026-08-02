using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

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

    public bool ReasoningInput { get; set; } = true;

    public bool ParallelToolCalls { get; set; } = true;

    public bool RequiresCompleteToolPairs { get; set; }

    public int MaxTools { get; set; }

    public int MaxToolSchemaUtf8Bytes { get; set; }

    public int MaxContextTokens { get; set; }
}

public sealed class ProviderRouteMetadata
{
    private const string UnspecifiedPolicyVersion =
        "provider-route-policy.unspecified.v1";
    private const string UnspecifiedPolicyDigest =
        "e3b0c44298fc1c149afbf4c8996fb924"
        + "27ae41e4649b934ca495991b7852b855";
    private readonly bool _bindDialectSemantics;

    public ProviderRouteMetadata(
        string modelId,
        string transportDialect)
        : this(
            modelId,
            ProviderDialectContract.LegacyCustom(transportDialect),
            UnspecifiedPolicyVersion,
            UnspecifiedPolicyDigest,
            bindDialectSemantics: false)
    {
    }

    public ProviderRouteMetadata(
        string modelId,
        string transportDialect,
        string routePolicyVersion,
        string routePolicyDigest)
        : this(
            modelId,
            ProviderDialectContract.LegacyCustom(transportDialect),
            routePolicyVersion,
            routePolicyDigest,
            bindDialectSemantics: false)
    {
    }

    public ProviderRouteMetadata(
        string modelId,
        ProviderDialectContract dialectContract)
        : this(
            modelId,
            dialectContract,
            UnspecifiedPolicyVersion,
            UnspecifiedPolicyDigest,
            bindDialectSemantics: true)
    {
    }

    public ProviderRouteMetadata(
        string modelId,
        ProviderDialectContract dialectContract,
        string routePolicyVersion,
        string routePolicyDigest)
        : this(
            modelId,
            dialectContract,
            routePolicyVersion,
            routePolicyDigest,
            bindDialectSemantics: true)
    {
    }

    private ProviderRouteMetadata(
        string modelId,
        ProviderDialectContract dialectContract,
        string routePolicyVersion,
        string routePolicyDigest,
        bool bindDialectSemantics)
    {
        ModelId = RuntimeGuard.RequiredUtf8(
            modelId,
            256,
            nameof(modelId));
        DialectContract =
            (dialectContract
             ?? throw new ArgumentNullException(nameof(dialectContract)))
            .Snapshot();
        TransportDialect = DialectContract.Identifier;
        RoutePolicyVersion = RuntimeGuard.RequiredUtf8(
            routePolicyVersion,
            128,
            nameof(routePolicyVersion));
        if (!CanonicalJsonDigest.IsSha256(routePolicyDigest))
        {
            throw new ArgumentException(
                "The route-policy digest must be a lowercase SHA-256 digest.",
                nameof(routePolicyDigest));
        }

        DeclaredRoutePolicyDigest = routePolicyDigest;
        _bindDialectSemantics = bindDialectSemantics;
        if (bindDialectSemantics)
        {
            var digest = new CanonicalDigestBuilder();
            digest.Add("type", "provider-route-policy-with-dialect.v1");
            digest.Add("declaredRoutePolicyDigest", routePolicyDigest);
            digest.Add(
                "dialectSemanticDigest",
                DialectContract.SemanticDigest);
            RoutePolicyDigest = digest.Finish();
        }
        else
        {
            RoutePolicyDigest = routePolicyDigest;
        }
    }

    public string ModelId { get; }

    public string TransportDialect { get; }

    public ProviderDialectContract DialectContract { get; }

    public bool HasBoundDialectSemantics => _bindDialectSemantics;

    public string RoutePolicyVersion { get; }

    public string RoutePolicyDigest { get; }

    internal string DeclaredRoutePolicyDigest { get; }

    internal ProviderRouteMetadata Snapshot()
    {
        return new ProviderRouteMetadata(
            ModelId,
            DialectContract,
            RoutePolicyVersion,
            DeclaredRoutePolicyDigest,
            _bindDialectSemantics);
    }
}

public sealed class ProviderRouteIdentity
{
    public ProviderRouteIdentity(
        string providerId,
        ProviderRouteMetadata metadata,
        ProviderCapabilities capabilities)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (capabilities is null)
        {
            throw new ArgumentNullException(nameof(capabilities));
        }

        ModelId = metadata.ModelId;
        TransportDialect = metadata.TransportDialect;
        DialectContract = metadata.DialectContract.Snapshot();
        HasBoundDialectSemantics =
            metadata.HasBoundDialectSemantics;
        RoutePolicyVersion = metadata.RoutePolicyVersion;
        RoutePolicyDigest = metadata.RoutePolicyDigest;

        var capabilityDigest = new CanonicalDigestBuilder();
        capabilityDigest.Add("type", "provider-capabilities");
        capabilityDigest.Add(
            "streaming",
            capabilities.Streaming ? "true" : "false");
        capabilityDigest.Add(
            "toolCalling",
            capabilities.ToolCalling ? "true" : "false");
        capabilityDigest.Add(
            "jsonOutput",
            capabilities.JsonOutput ? "true" : "false");
        capabilityDigest.Add(
            "reasoningInput",
            capabilities.ReasoningInput ? "true" : "false");
        capabilityDigest.Add(
            "parallelToolCalls",
            capabilities.ParallelToolCalls ? "true" : "false");
        capabilityDigest.Add(
            "requiresCompleteToolPairs",
            capabilities.RequiresCompleteToolPairs ? "true" : "false");
        capabilityDigest.Add("maxTools", capabilities.MaxTools);
        capabilityDigest.Add(
            "maxToolSchemaUtf8Bytes",
            capabilities.MaxToolSchemaUtf8Bytes);
        capabilityDigest.Add(
            "maxContextTokens",
            capabilities.MaxContextTokens);
        CapabilityDigest = capabilityDigest.Finish();

        RouteDigest = ComputeRouteDigest(
            ProviderId,
            ModelId,
            TransportDialect,
            CapabilityDigest,
            RoutePolicyVersion,
            RoutePolicyDigest);
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string TransportDialect { get; }

    public ProviderDialectContract DialectContract { get; }

    public bool HasBoundDialectSemantics { get; }

    public string DialectSemanticDigest => DialectContract.SemanticDigest;

    public string CapabilityDigest { get; }

    public string RoutePolicyVersion { get; }

    public string RoutePolicyDigest { get; }

    public string RouteDigest { get; }

    // Retained for journals written before route-policy identity was added.
    internal static string ComputeRouteDigest(
        string providerId,
        string modelId,
        string transportDialect,
        string capabilityDigest)
    {
        var routeDigest = new CanonicalDigestBuilder();
        routeDigest.Add("type", "provider-route");
        routeDigest.Add("providerId", providerId);
        routeDigest.Add("modelId", modelId);
        routeDigest.Add("transportDialect", transportDialect);
        routeDigest.Add("capabilityDigest", capabilityDigest);
        return routeDigest.Finish();
    }

    internal static string ComputeRouteDigest(
        string providerId,
        string modelId,
        string transportDialect,
        string capabilityDigest,
        string routePolicyVersion,
        string routePolicyDigest)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "provider-route.v2");
        digest.Add("providerId", providerId);
        digest.Add("modelId", modelId);
        digest.Add("transportDialect", transportDialect);
        digest.Add("capabilityDigest", capabilityDigest);
        digest.Add("routePolicyVersion", routePolicyVersion);
        digest.Add("routePolicyDigest", routePolicyDigest);
        return digest.Finish();
    }
}

internal static class ProviderRouteJournalExtensions
{
    internal const string PolicyVersion = "providerRoutePolicyVersion";

    internal const string PolicyDigest = "providerRoutePolicyDigest";
}

public interface IProviderRouteMetadataSource
{
    ProviderRouteMetadata RouteMetadata { get; }
}

public sealed class ProviderUsage
{
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public string CostUsd { get; set; } = "0";

    public int Samples { get; set; } = 1;

    public int? CacheReadTokens { get; set; }

    public int? CacheWriteTokens { get; set; }

    public int? CacheMissTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public int? ProviderTotalTokens { get; set; }

    public string Availability { get; set; } =
        UsageAvailabilityStates.CostAvailable;
}

internal static class ProviderUsageAccounting
{
    internal static void AccumulateDetails(
        AgentUsage aggregate,
        ProviderUsage delta)
    {
        var hadSamples = aggregate.ProviderUsageSamples > 0
                         || aggregate.InputTokens > 0
                         || aggregate.OutputTokens > 0
                         || !string.Equals(
                             aggregate.CostUsd,
                             "0",
                             StringComparison.Ordinal);
        if (!hadSamples)
        {
            aggregate.CacheReadTokens = delta.CacheReadTokens;
            aggregate.CacheWriteTokens = delta.CacheWriteTokens;
            aggregate.CacheMissTokens = delta.CacheMissTokens;
            aggregate.ReasoningTokens = delta.ReasoningTokens;
            aggregate.ProviderTotalTokens = delta.ProviderTotalTokens;
            aggregate.Availability = delta.Availability;
        }
        else
        {
            aggregate.CacheReadTokens = AddAvailable(
                aggregate.CacheReadTokens,
                delta.CacheReadTokens);
            aggregate.CacheWriteTokens = AddAvailable(
                aggregate.CacheWriteTokens,
                delta.CacheWriteTokens);
            aggregate.CacheMissTokens = AddAvailable(
                aggregate.CacheMissTokens,
                delta.CacheMissTokens);
            aggregate.ReasoningTokens = AddAvailable(
                aggregate.ReasoningTokens,
                delta.ReasoningTokens);
            aggregate.ProviderTotalTokens = AddAvailable(
                aggregate.ProviderTotalTokens,
                delta.ProviderTotalTokens);
            if (!string.Equals(
                    aggregate.Availability,
                    UsageAvailabilityStates.CostAvailable,
                    StringComparison.Ordinal)
                || !string.Equals(
                    delta.Availability,
                    UsageAvailabilityStates.CostAvailable,
                    StringComparison.Ordinal))
            {
                aggregate.Availability =
                    UsageAvailabilityStates.CostUnavailable;
            }
        }

        var priorSamples = aggregate.ProviderUsageSamples;
        if (hadSamples && priorSamples == 0)
        {
            priorSamples = 1;
        }

        aggregate.ProviderUsageSamples = (int)Math.Min(
            int.MaxValue,
            (long)priorSamples + delta.Samples);
    }

    private static int? AddAvailable(int? current, int? delta)
    {
        if (!current.HasValue || !delta.HasValue)
        {
            return null;
        }

        return (int)Math.Min(
            int.MaxValue,
            (long)current.Value + delta.Value);
    }
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

    /// <summary>
    /// A provider-private continuation update is accepted only on the
    /// completed event and is bound to the active route by the runner.
    /// </summary>
    public ProviderOpaqueContinuationUpdate? OpaqueContinuationUpdate
    {
        get;
        set;
    }
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

    public ModelInferenceOptions? Inference { get; set; }

    public ProviderOpaqueContinuationState? OpaqueContinuationState
    {
        get;
        set;
    }
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
        : this(
            code,
            category,
            safeMessage,
            retryable
                ? ProviderFailureDisposition.RetryThenFailover
                : ProviderFailureDisposition.AbortRun,
            retryAfter,
            innerException,
            usageKnownToBeZero)
    {
    }

    public ProviderException(
        string code,
        string category,
        string safeMessage,
        ProviderFailureDisposition disposition,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        bool usageKnownToBeZero = false)
        : base(
            RuntimeGuard.RequiredUtf8(
                safeMessage,
                2_048,
                nameof(safeMessage)))
    {
        // Provider boundaries may wrap credentials, request bodies, URLs, or
        // third-party adapter state. Never retain an arbitrary exception
        // object because Exception.ToString() recursively renders its message
        // and data. Code and category are the bounded diagnostic contract.
        _ = innerException;
        Code = RuntimeGuard.RequiredReasonCode(code, nameof(code));
        Category = RuntimeGuard.RequiredUtf8(
            category,
            96,
            nameof(category));
        if (!Enum.IsDefined(typeof(ProviderFailureDisposition), disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Disposition = disposition;
        RetryAfter = retryAfter;
        UsageKnownToBeZero = usageKnownToBeZero;
    }

    public string Code { get; }

    public string Category { get; }

    public ProviderFailureDisposition Disposition { get; }

    public bool Retryable =>
        Disposition == ProviderFailureDisposition.RetryThenFailover;

    public bool FallbackEligible =>
        Disposition != ProviderFailureDisposition.AbortRun;

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

    public TimeSpan RequestPreparationTimeout { get; set; } =
        TimeSpan.FromSeconds(10);

    public TimeSpan StreamStartTimeout { get; set; } =
        TimeSpan.FromSeconds(10);

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

        if (RequestPreparationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestPreparationTimeout));
        }

        if (StreamStartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StreamStartTimeout));
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
            RequestPreparationTimeout = RequestPreparationTimeout,
            StreamStartTimeout = StreamStartTimeout,
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

    public ProviderRouteIdentity? RouteIdentity { get; set; }

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string? Text { get; set; }

    public string? ReasoningContent { get; set; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; set; } =
        Array.Empty<ModelToolCall>();

    public ProviderUsage Usage { get; set; } = new();

    public string? FinishReason { get; set; }

    public ProviderOpaqueContinuationState? OpaqueContinuationState
    {
        get;
        set;
    }
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

    public ProviderRouteIdentity RouteIdentity { get; set; } = null!;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public ProviderRequestPreparationReport RequestPreparation { get; set; } =
        null!;

    public ProviderWireRequestEvidence WireRequestEvidence { get; set; } =
        null!;
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

    /// <summary>
    /// Identifies the provider attempt being abandoned. This is null when a
    /// route is skipped before an attempt is dispatched.
    /// </summary>
    public string? ProviderAttemptId { get; set; }

    /// <summary>
    /// Identifies the stream attempt being abandoned. Presentation consumers
    /// use this identity to supersede partial output before retry or fallback.
    /// This is null when a route is skipped before dispatch.
    /// </summary>
    public string? StreamAttemptId { get; set; }

    public int AttemptNumber { get; set; }

    public string ErrorCode { get; set; } = string.Empty;

    public string ErrorCategory { get; set; } = string.Empty;

    public long DelayMilliseconds { get; set; }
}

/// <summary>
/// An immutable capability and route-identity capture. A caller that records
/// the primary identity before a turn must pass the same plan to RunAsync so
/// dispatch cannot observe a later mutable capability view.
/// </summary>
public sealed class ProviderRoutePlan
{
    private readonly object _owner;
    private readonly int[] _providerIndexes;
    private readonly ProviderCapabilities[] _capabilities;
    private readonly ProviderRouteIdentity[] _identities;

    internal ProviderRoutePlan(
        object owner,
        int[] providerIndexes,
        ProviderCapabilities[] capabilities,
        ProviderRouteIdentity[] identities)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _providerIndexes = providerIndexes
                           ?? throw new ArgumentNullException(
                               nameof(providerIndexes));
        _capabilities =
            capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _identities =
            identities ?? throw new ArgumentNullException(nameof(identities));
        if (_capabilities.Length == 0
            || _providerIndexes.Length != _capabilities.Length
            || _capabilities.Length != _identities.Length)
        {
            throw new ArgumentException("The provider route plan is invalid.");
        }
    }

    public ProviderRouteIdentity PrimaryRouteIdentity => _identities[0];

    public int Count => _identities.Length;

    public IReadOnlyList<ProviderRouteIdentity> RouteIdentities =>
        Array.AsReadOnly(_identities);

    internal bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);

    internal ProviderCapabilities CapabilitiesAt(int index) =>
        _capabilities[index];

    internal int ProviderIndexAt(int index) => _providerIndexes[index];

    internal ProviderRouteIdentity IdentityAt(int index) =>
        _identities[index];
}

public sealed class ProviderAttemptRunner
{
    private static readonly TimeSpan CancellationCleanupGrace =
        TimeSpan.FromMilliseconds(50);

    private readonly IReadOnlyList<IStreamingModelProvider> _providers;
    private readonly IReadOnlyList<string> _providerIds;
    private readonly IReadOnlyList<ProviderRouteMetadata> _routeMetadata;
    private readonly ProviderRetryPolicy _policy;
    private readonly IRuntimeDelay _delay;
    private readonly IRuntimeDelay _eventWaitDelay;
    private readonly IRuntimeIdGenerator _ids;
    private readonly ProviderStreamLimits _streamLimits;
    private readonly ProviderRouteHealthRegistry _routeHealth;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly BoundedCallbackExecutionDispatcher
        _callbackExecutionDispatcher;
    private readonly object _routePlanOwner = new();
    private readonly ConcurrentDictionary<string, int> _quarantinedProviders =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failedCleanupProviders =
        new(StringComparer.Ordinal);

    public ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits = null,
        ProviderRouteResilienceOptions? routeResilienceOptions = null,
        IRuntimeClock? clock = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var providerCount = providers.Count;
        if (providerCount == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        if (providerCount is < 0 or > 16)
        {
            throw new ArgumentException(
                "A fallback chain cannot contain more than 16 providers.",
                nameof(providers));
        }

        var stableProviders =
            new IStreamingModelProvider[providerCount];
        for (var index = 0; index < providerCount; index++)
        {
            try
            {
                stableProviders[index] = providers[index]
                    ?? throw new ArgumentException(
                        "Provider lists cannot contain null entries.",
                        nameof(providers));
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new InvalidDataException(
                    "The provider list changed while it was being "
                    + "snapshotted.",
                    exception);
            }
        }

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        var stableProviderIds = new string[providerCount];
        var stableRouteMetadata =
            new ProviderRouteMetadata[providerCount];
        for (var index = 0; index < providerCount; index++)
        {
            var provider = stableProviders[index];
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

            stableProviderIds[index] = providerId;
            var metadata = provider is IProviderRouteMetadataSource source
                ? source.RouteMetadata
                : new ProviderRouteMetadata(
                    "unspecified",
                    "custom.streaming.v1");
            if (metadata is null)
            {
                throw new ArgumentException(
                    "Provider route metadata cannot be null.",
                    nameof(providers));
            }

            stableRouteMetadata[index] =
                metadata.Snapshot();
        }

        _providers = stableProviders;
        _providerIds = stableProviderIds;
        _routeMetadata = stableRouteMetadata;
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy)))
            .Snapshot();
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _eventWaitDelay = new SystemRuntimeDelay();
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _streamLimits = streamLimits ?? new ProviderStreamLimits();
        _routeHealth = new ProviderRouteHealthRegistry(
            routeResilienceOptions ?? new ProviderRouteResilienceOptions(),
            clock ?? new SystemRuntimeClock());
        _cancellationDispatcher =
            BoundedCancellationDispatcher.Shared;
        _callbackExecutionDispatcher =
            BoundedCallbackExecutionDispatcher.ProviderShared;
    }

    internal ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits,
        IRuntimeDelay eventWaitDelay)
        : this(
            providers,
            policy,
            delay,
            ids,
            streamLimits,
            eventWaitDelay,
            BoundedCancellationDispatcher.Shared,
            BoundedCallbackExecutionDispatcher.ProviderShared)
    {
    }

    internal ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits,
        IRuntimeDelay eventWaitDelay,
        BoundedCancellationDispatcher cancellationDispatcher)
        : this(
            providers,
            policy,
            delay,
            ids,
            streamLimits,
            eventWaitDelay,
            cancellationDispatcher,
            BoundedCallbackExecutionDispatcher.ProviderShared)
    {
    }

    internal ProviderAttemptRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        ProviderRetryPolicy policy,
        IRuntimeDelay delay,
        IRuntimeIdGenerator ids,
        ProviderStreamLimits? streamLimits,
        IRuntimeDelay eventWaitDelay,
        BoundedCancellationDispatcher cancellationDispatcher,
        BoundedCallbackExecutionDispatcher callbackExecutionDispatcher)
        : this(providers, policy, delay, ids, streamLimits)
    {
        _eventWaitDelay = eventWaitDelay
            ?? throw new ArgumentNullException(nameof(eventWaitDelay));
        _cancellationDispatcher = cancellationDispatcher
                                   ?? throw new ArgumentNullException(
                                       nameof(cancellationDispatcher));
        _callbackExecutionDispatcher = callbackExecutionDispatcher
                                       ?? throw new ArgumentNullException(
                                           nameof(
                                               callbackExecutionDispatcher));
    }

    public string PrimaryProviderId => _providerIds[0];

    public ProviderRouteMetadata PrimaryRouteMetadata =>
        _routeMetadata[0];

    public ProviderRoutePlan CaptureRoutePlan(
        CancellationToken cancellationToken = default)
    {
        return CaptureRoutePlan(preference: null, cancellationToken);
    }

    public ProviderRoutePlan CaptureRoutePlan(
        ProviderRoutePreference? preference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedIndexes = ResolveProviderIndexes(preference);
        var capabilities =
            new ProviderCapabilities[selectedIndexes.Length];
        var identities =
            new ProviderRouteIdentity[selectedIndexes.Length];
        for (var index = 0; index < selectedIndexes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerIndex = selectedIndexes[index];
            try
            {
                capabilities[index] = SnapshotCapabilities(
                    _providers[providerIndex].Capabilities);
                identities[index] = new ProviderRouteIdentity(
                    _providerIds[providerIndex],
                    _routeMetadata[providerIndex],
                    capabilities[index]);
            }
            catch (ProviderException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new ProviderException(
                    "provider_capabilities_invalid",
                    "capability",
                    "The provider returned invalid capabilities.",
                    false,
                    innerException: exception,
                    usageKnownToBeZero: true);
            }
        }

        return new ProviderRoutePlan(
            _routePlanOwner,
            selectedIndexes,
            capabilities,
            identities);
    }

    private int[] ResolveProviderIndexes(
        ProviderRoutePreference? preference)
    {
        if (preference is null)
        {
            return Enumerable.Range(0, _providers.Count).ToArray();
        }

        var snapshot = preference.CloneValidated();
        var indexes = new List<int>(_providers.Count);
        foreach (var id in snapshot.ProviderIds)
        {
            var index = -1;
            for (var candidate = 0;
                 candidate < _providerIds.Count;
                 candidate++)
            {
                if (string.Equals(
                        _providerIds[candidate],
                        id,
                        StringComparison.Ordinal))
                {
                    index = candidate;
                    break;
                }
            }

            if (index < 0)
            {
                throw new ArgumentException(
                    "A preferred provider route is not configured.",
                    nameof(preference));
            }

            indexes.Add(index);
        }

        if (snapshot.AllowUnlistedFallback)
        {
            for (var index = 0; index < _providers.Count; index++)
            {
                if (!indexes.Contains(index))
                {
                    indexes.Add(index);
                }
            }
        }

        return indexes.ToArray();
    }

    /// <summary>
    /// Captures an identity for inspection only. Use CaptureRoutePlan and pass
    /// that plan to RunAsync when the recorded identity must equal dispatch.
    /// </summary>
    public ProviderRouteIdentity CapturePrimaryRouteIdentity(
        CancellationToken cancellationToken = default)
    {
        return CaptureRoutePlan(cancellationToken).PrimaryRouteIdentity;
    }

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
            onResultDiscarded = null,
        ProviderOpaqueContinuationState? opaqueContinuationState = null,
        ProviderRoutePlan? routePlan = null,
        ModelInferenceOptions? inference = null)
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

        cancellationToken.ThrowIfCancellationRequested();
        var capturedRoutePlan = routePlan ?? CaptureRoutePlan(
            cancellationToken);
        if (!capturedRoutePlan.IsOwnedBy(_routePlanOwner))
        {
            throw new ArgumentException(
                "The provider route plan belongs to a different runner.",
                nameof(routePlan));
        }

        var continuationSnapshot =
            opaqueContinuationState?.Snapshot();
        var inferenceSnapshot = inference?.CloneValidated();
        var messageReferences = SnapshotRequestReferences(
            messages,
            ProviderRequestContentGuard.MaxMessages,
            "Provider message lists cannot contain null entries.",
            cancellationToken);
        var toolReferences = SnapshotRequestReferences(
            tools,
            ProviderRequestContentGuard.MaxTools,
            "Provider tool lists cannot contain null entries.",
            cancellationToken);
        ProviderRequestContentGuard.EnsureInputWithinLimits(
            messageReferences,
            toolReferences,
            cancellationToken);
        var messageSnapshot = SnapshotMessages(
            messageReferences,
            cancellationToken);
        var toolSnapshot = SnapshotTools(
            toolReferences,
            cancellationToken);

        ProviderException? lastError = null;
        var aggregateUsage = new ProviderUsage { Samples = 0 };
        var usageSettledStreams = new HashSet<string>(StringComparer.Ordinal);
        for (var routeIndex = 0;
             routeIndex < capturedRoutePlan.Count;
             routeIndex++)
        {
            var providerIndex = capturedRoutePlan.ProviderIndexAt(routeIndex);
            var provider = _providers[providerIndex];
            var providerId = _providerIds[providerIndex];
            var routeIdentity =
                capturedRoutePlan.IdentityAt(routeIndex);
            string? lastProviderAttemptId = null;
            string? lastStreamAttemptId = null;
            if (IsProviderQuarantined(providerId))
            {
                lastError = new ProviderException(
                    "provider_cleanup_pending",
                    "provider",
                    "A previous attempt for this provider is still shutting down.",
                    false);
                NotifyFallback(
                    onLifecycleNotice,
                    capturedRoutePlan,
                    routeIndex,
                    attemptNumber: 0,
                    lastError);
                continue;
            }

            using var routeAdmission = _routeHealth.Acquire(
                routeIdentity.RouteDigest);
            if (!routeAdmission.IsAdmitted)
            {
                lastError = new ProviderException(
                    routeAdmission.Rejection
                    == ProviderRouteAdmissionRejection.ProbeInProgress
                        ? "provider_route_probe_in_progress"
                        : "provider_route_cooldown",
                    "provider",
                    routeAdmission.Rejection
                    == ProviderRouteAdmissionRejection.ProbeInProgress
                        ? "Another run is probing this provider route."
                        : "This provider route is temporarily cooling down.",
                    ProviderFailureDisposition.Failover,
                    usageKnownToBeZero: true);
                NotifyFallback(
                    onLifecycleNotice,
                    capturedRoutePlan,
                    routeIndex,
                    attemptNumber: 0,
                    lastError);
                continue;
            }

            ProviderCapabilities capabilities;
            int? routeEstimatedPromptTokens;
            try
            {
                capabilities = SnapshotCapabilities(
                    capturedRoutePlan.CapabilitiesAt(routeIndex));
                EnsureCapabilities(
                    providerId,
                    capabilities,
                    toolSnapshot,
                    cancellationToken);
                routeEstimatedPromptTokens = ResolvePromptTokenEstimate(
                    provider,
                    messageSnapshot,
                    toolSnapshot,
                    estimatedPromptTokens,
                    cancellationToken);
                _ = ResolveMaxOutputTokens(
                    capabilities,
                    routeEstimatedPromptTokens,
                    maxOutputTokens);
            }
            catch (ProviderException exception)
            {
                lastError = exception;
                if (exception.FallbackEligible)
                {
                    routeAdmission.ReportRouteFailure();
                }

                NotifyFallback(
                    onLifecycleNotice,
                    capturedRoutePlan,
                    routeIndex,
                    attemptNumber: 0,
                    exception);
                continue;
            }

            ProviderException? routeError = null;
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
                    routeEstimatedPromptTokens,
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
                    Messages = SnapshotMessages(
                        messageSnapshot,
                        cancellationToken),
                    Tools = SnapshotTools(
                        toolSnapshot,
                        cancellationToken),
                    MaxOutputTokens = providerMaxOutputTokens,
                    Inference = inferenceSnapshot?.CloneValidated(),
                    OpaqueContinuationState =
                        continuationSnapshot is not null
                        && continuationSnapshot.Matches(routeIdentity)
                            ? continuationSnapshot.Snapshot()
                            : null
                };
                ProviderPreparedRequest prepared;
                PreparedProviderStream? preparedStream = null;
                ProviderWireRequestEvidence wireEvidence;
                try
                {
                    var sanitizer = new ProviderRequestSanitizer();
                    var safe = await sanitizer.PrepareRequestAsync(
                            new ProviderRequestPreparationContext(
                                providerId,
                                routeIdentity,
                                capabilities,
                                request),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (provider is IProviderRequestAdapter adapter)
                    {
                        var adapterBaseline =
                            ProviderRequestSanitizer.Unchanged(
                                safe.Request,
                                cancellationToken);
                        var preparationContext =
                            new ProviderRequestPreparationContext(
                                providerId,
                                routeIdentity,
                                capabilities,
                                safe.Request,
                                adapterBaseline.Report);
                        var adapted = await PrepareRequestWithDeadlineAsync(
                                adapter,
                                preparationContext,
                                (candidate, validationToken) =>
                                {
                                    var adaptedRequest =
                                        ValidatePreparedRequest(
                                            adapterBaseline.Request,
                                            adapterBaseline.Report,
                                            candidate,
                                            providerId,
                                            capabilities,
                                            validationToken);
                                    return new ProviderPreparedRequest(
                                        adaptedRequest,
                                        candidate.Report);
                                },
                                providerId,
                                cancellationToken,
                                onDetachedCleanup)
                            .ConfigureAwait(false);
                        prepared = new ProviderPreparedRequest(
                            adapted.Request,
                            CombinePreparationReports(
                                safe.Report,
                                adapted.Report));
                    }
                    else
                    {
                        prepared = safe;
                    }

                    request = ValidatePreparedRequest(
                        request,
                        baseline: null,
                        prepared,
                        providerId,
                        capabilities,
                        cancellationToken,
                        validateEvidence: false);
                    if (provider is IPreparedStreamingModelProvider
                        preparedProvider)
                    {
                        preparedStream =
                            await PrepareStreamWithDeadlineAsync(
                                    preparedProvider,
                                    new ProviderStreamPreparationContext(
                                        providerId,
                                        routeIdentity,
                                        request),
                                    routeIdentity,
                                    providerId,
                                    cancellationToken,
                                    onDetachedCleanup)
                                .ConfigureAwait(false);
                        wireEvidence = preparedStream.Evidence;
                    }
                    else
                    {
                        wireEvidence =
                            ProviderWireRequestEvidence.CreateUnavailable(
                                routeIdentity);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    fence.Invalidate();
                    if (preparedStream is not null)
                    {
                        await DisposePreparedBeforeDispatchAsync(
                                preparedStream,
                                providerId,
                                onDetachedCleanup)
                            .ConfigureAwait(false);
                        preparedStream = null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    routeError = KnownZeroPreparationFailure(exception);
                    lastError = routeError;
                    break;
                }

                try
                {
                    if (onDispatch is not null)
                    {
                        await onDispatch(
                                new ProviderDispatchNotice
                                {
                                    ProviderId = providerId,
                                    RouteIdentity = routeIdentity,
                                    ProviderAttemptId = providerAttemptId,
                                    StreamAttemptId = streamAttemptId,
                                    RequestPreparation = prepared.Report,
                                    WireRequestEvidence = wireEvidence
                                })
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                    fence.Invalidate();
                    if (preparedStream is not null)
                    {
                        await DisposePreparedBeforeDispatchAsync(
                                preparedStream,
                                providerId,
                                onDetachedCleanup)
                            .ConfigureAwait(false);
                    }

                    throw;
                }

                lastProviderAttemptId = providerAttemptId;
                lastStreamAttemptId = streamAttemptId;
                PreparedStreamProviderAdapter? preparedAdapter = null;
                try
                {
                    IStreamingModelProvider attemptProvider = provider;
                    if (preparedStream is not null)
                    {
                        preparedAdapter = new PreparedStreamProviderAdapter(
                            provider,
                            preparedStream,
                            _callbackExecutionDispatcher,
                            candidate =>
                                DisposePreparedBeforeDispatchAsync(
                                    candidate,
                                    providerId,
                                    onDetachedCleanup));
                        attemptProvider = preparedAdapter;
                        preparedStream = null;
                    }

                    var result = await ConsumeAttemptAsync(
                            attemptProvider,
                            providerId,
                            routeIdentity,
                            request,
                            identity,
                            generation,
                            fence,
                            onCurrentEvent,
                            cancellationToken,
                            onDetachedCleanup,
                            ObserveUsageAsync,
                            actualInputTokens => ObserveActualInputTokens(
                                provider,
                                routeEstimatedPromptTokens,
                                actualInputTokens),
                            onUsageUncertain)
                        .ConfigureAwait(false);
                    if (preparedAdapter is not null)
                    {
                        await preparedAdapter
                            .DisposeIfUnclaimedAsync()
                            .ConfigureAwait(false);
                    }

                    result.RouteIdentity = routeIdentity;
                    result.Usage = CloneUsage(aggregateUsage);
                    routeAdmission.ReportSuccess();
                    return result;
                }
                catch (ProviderException exception)
                {
                    if (preparedAdapter is not null)
                    {
                        await preparedAdapter
                            .DisposeIfUnclaimedAsync()
                            .ConfigureAwait(false);
                    }

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

                    if (!exception.FallbackEligible)
                    {
                        throw;
                    }

                    routeError = exception;
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

                    if (!exception.Retryable
                        || routeAdmission.IsHalfOpenProbe)
                    {
                        break;
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
                            ProviderAttemptId = providerAttemptId,
                            StreamAttemptId = streamAttemptId,
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
                            cancellationToken,
                            _cancellationDispatcher,
                            providerId,
                            onDetachedCleanup)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (preparedAdapter is not null)
                    {
                        await preparedAdapter
                            .DisposeIfUnclaimedAsync()
                            .ConfigureAwait(false);
                    }

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

            if (routeError is not null)
            {
                if (routeError.FallbackEligible)
                {
                    routeAdmission.ReportRouteFailure();
                }

                NotifyFallback(
                    onLifecycleNotice,
                    capturedRoutePlan,
                    routeIndex,
                    _policy.MaxAttemptsPerProvider,
                    routeError,
                    lastProviderAttemptId,
                    lastStreamAttemptId);
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
        ProviderRoutePlan routePlan,
        int routeIndex,
        int attemptNumber,
        ProviderException exception,
        string? providerAttemptId = null,
        string? streamAttemptId = null)
    {
        if (routeIndex + 1 >= routePlan.Count)
        {
            return;
        }

        var current = routePlan.IdentityAt(routeIndex);
        var next = routePlan.IdentityAt(routeIndex + 1);

        Notify(
            notify,
            new ProviderAttemptNotice
            {
                Kind = ProviderAttemptNoticeKinds.Fallback,
                ProviderId = current.ProviderId,
                NextProviderId = next.ProviderId,
                ProviderAttemptId = providerAttemptId,
                StreamAttemptId = streamAttemptId,
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
        ProviderRouteIdentity routeIdentity,
        StreamingModelRequest request,
        AttemptIdentity identity,
        long generation,
        AttemptFence fence,
        Func<ModelStreamEvent, ValueTask>? onCurrentEvent,
        CancellationToken cancellationToken,
        Action<Task>? onDetachedCleanup,
        Func<ProviderUsageNotice, ValueTask> onUsage,
        Action<int> onCompletedInputUsage,
        Func<ProviderUsageUncertainNotice, ValueTask>? onUsageUncertain)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new ToolCallFragmentAssembler(_streamLimits);
        var usage = new ProviderUsage();
        var usageSeen = false;
        string? finishReason = null;
        var completedSeen = false;
        ProviderOpaqueContinuationState? opaqueContinuationState = null;
        long lastOrdinal = -1;
        var eventCount = 0;
        var textUtf8Bytes = 0;
        var reasoningUtf8Bytes = 0;
        var elapsed = Stopwatch.StartNew();
        if (!_cancellationDispatcher.TryReserve(
                out var attemptCancellationReservation))
        {
            throw new ProviderException(
                "provider_cancellation_capacity_exceeded",
                "capacity",
                "Provider cancellation cleanup capacity is exhausted.",
                false,
                usageKnownToBeZero: true);
        }

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

        IAsyncEnumerator<ModelStreamEvent>? enumerator = null;
        var cleanupHandled = false;
        Task<IAsyncEnumerator<ModelStreamEvent>> startOperation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            startOperation = StartProviderCallback(
                () =>
                {
                    var stream = provider.StreamAsync(
                                     request,
                                     attemptCancellation.Token)
                                 ?? throw new InvalidOperationException(
                                     "The provider returned a null stream.");
                    var candidate = stream.GetAsyncEnumerator(
                                        attemptCancellation.Token)
                                    ?? throw new InvalidOperationException(
                                        "The provider returned a null stream enumerator.");
                    return new ValueTask<
                        IAsyncEnumerator<ModelStreamEvent>>(candidate);
                },
                usageKnownToBeZero: true);
            var startTimeout = _policy.StreamStartTimeout
                               < _policy.TotalTimeout
                ? _policy.StreamStartTimeout
                : _policy.TotalTimeout;
            var callerCancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state!)
                    .TrySetResult(true),
                callerCancelled);
            var started = await Task.WhenAny(
                    startOperation,
                    Task.Delay(startTimeout),
                    callerCancelled.Task)
                .ConfigureAwait(false);
            if (!ReferenceEquals(started, startOperation))
            {
                fence.Invalidate();
                cleanupHandled = true;
                var cancellation = CancelDetachedAsync(
                    attemptCancellation,
                    attemptCancellationReservation!);
                var cleanup = CompleteDetachedStreamStartAsync(
                    startOperation,
                    attemptCancellation,
                    cancellation,
                    attemptCancellationReservation!);
                attemptCancellationReservation = null;
                var observed = RegisterDetachedCleanup(providerId, cleanup);
                NotifyDetachedCleanup(onDetachedCleanup, observed);
                await ReportUsageUncertainAsync(
                        cancellationToken.IsCancellationRequested
                            ? "provider_cancelled_before_usage"
                            : "provider_stream_start_timeout")
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ProviderException(
                    "provider_stream_start_timeout",
                    "network",
                    "The provider did not create its stream in time.",
                    false);
            }

            enumerator = await startOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            if (!cleanupHandled)
            {
                RegisterCancellationCleanup(
                    attemptCancellation,
                    attemptCancellationReservation!,
                    providerId,
                    onDetachedCleanup);
            }

            await ReportUsageUncertainAsync(
                    "provider_cancelled_before_usage")
                .ConfigureAwait(false);
            throw;
        }
        catch (ProviderException exception) when (
            exception.UsageKnownToBeZero)
        {
            if (!cleanupHandled)
            {
                RegisterCancellationCleanup(
                    attemptCancellation,
                    attemptCancellationReservation!,
                    providerId,
                    onDetachedCleanup);
            }

            throw;
        }
        catch (ProviderException exception)
        {
            if (!cleanupHandled)
            {
                RegisterCancellationCleanup(
                    attemptCancellation,
                    attemptCancellationReservation!,
                    providerId,
                    onDetachedCleanup);
            }

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
            if (!cleanupHandled)
            {
                RegisterCancellationCleanup(
                    attemptCancellation,
                    attemptCancellationReservation!,
                    providerId,
                    onDetachedCleanup);
            }

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
                var waitStartedAt = elapsed.Elapsed;
                if (!_cancellationDispatcher.TryReserve(
                        out var waitCancellationReservation))
                {
                    fence.Invalidate();
                    throw new ProviderException(
                        "provider_cancellation_capacity_exceeded",
                        "capacity",
                        "Provider cancellation cleanup capacity is exhausted.",
                        false);
                }

                var waitCancellation = new CancellationTokenSource();
                Task idle;
                try
                {
                    idle = _eventWaitDelay
                        .DelayAsync(wait, waitCancellation.Token)
                        .AsTask();
                }
                catch
                {
                    waitCancellation.Dispose();
                    waitCancellationReservation!.Dispose();
                    throw;
                }

                var cancellationSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenRegistration callerCancellationRegistration;
                try
                {
                    callerCancellationRegistration =
                        cancellationToken.Register(
                            () => cancellationSignal.TrySetResult(true));
                }
                catch
                {
                    await TrackDetachedCancellationCleanupIfPendingAsync(
                            providerId,
                            CancelObserveAndDisposeAsync(
                                idle,
                                waitCancellation,
                                waitCancellationReservation!),
                            onDetachedCleanup)
                        .ConfigureAwait(false);
                    throw;
                }

                Task<bool> moveNext;
                using (callerCancellationRegistration)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        moveNext = StartProviderCallback(
                            () => enumerator!.MoveNextAsync(),
                            usageKnownToBeZero: false);
                    }
                    catch
                    {
                        await TrackDetachedCancellationCleanupIfPendingAsync(
                                providerId,
                                CancelObserveAndDisposeAsync(
                                    idle,
                                    waitCancellation,
                                    waitCancellationReservation!),
                                onDetachedCleanup)
                            .ConfigureAwait(false);
                        throw;
                    }

                    var completed = await Task.WhenAny(
                            moveNext,
                            idle,
                            cancellationSignal.Task)
                        .ConfigureAwait(false);
                    var completedAt = elapsed.Elapsed;
                    var moveNextWithinDeadline = IsMoveNextWithinDeadline(
                        completed,
                        moveNext,
                        completedAt,
                        waitStartedAt,
                        wait,
                        _policy.TotalTimeout);
                    var waitCleanup = CancelObserveAndDisposeAsync(
                        idle,
                        waitCancellation,
                        waitCancellationReservation!);
                    waitCancellationReservation = null;
                    var waitCleanupWinner = await Task.WhenAny(
                            waitCleanup,
                            Task.Delay(CancellationCleanupGrace))
                        .ConfigureAwait(false);
                    if (ReferenceEquals(waitCleanupWinner, waitCleanup))
                    {
                        await waitCleanup.ConfigureAwait(false);
                    }
                    else
                    {
                        TrackDetachedCancellationCleanup(
                            providerId,
                            waitCleanup,
                            onDetachedCleanup);
                    }

                    if (!moveNextWithinDeadline)
                    {
                        fence.Invalidate();
                        var cancellationCleanup = CancelDetachedAsync(
                            attemptCancellation,
                            attemptCancellationReservation!);
                        var cleanup = CompleteAndDisposeDetachedAsync(
                            moveNext,
                            enumerator,
                            attemptCancellation,
                            cancellationCleanup,
                            attemptCancellationReservation!);
                        attemptCancellationReservation = null;
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
                                    try
                                    {
                                        await cleanup.ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        MarkCleanupFailed(providerId);
                                        throw;
                                    }
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
                var currentRemaining = _policy.TotalTimeout - elapsed.Elapsed;
                if (currentRemaining <= TimeSpan.Zero)
                {
                    throw new ProviderException(
                        "provider_total_timeout",
                        "network",
                        "The provider exceeded the total turn timeout.",
                        true);
                }

                var currentWait = currentRemaining < _policy.IdleTimeout
                    ? currentRemaining
                    : _policy.IdleTimeout;
                var currentStartedAt = elapsed.Elapsed;
                var currentOperation = StartProviderCallback(
                    () => new ValueTask<ModelStreamEvent?>(
                        enumerator!.Current),
                    usageKnownToBeZero: false);
                using var currentSignals = new OperationDeadlineSignals(
                    currentWait,
                    cancellationToken);
                var currentCompleted = await Task.WhenAny(
                        currentOperation,
                        currentSignals.Timeout,
                        currentSignals.Cancellation)
                    .ConfigureAwait(false);
                var currentCompletedAt = elapsed.Elapsed;
                var currentWithinDeadline =
                    ReferenceEquals(currentCompleted, currentOperation)
                    && currentCompletedAt < _policy.TotalTimeout
                    && currentCompletedAt - currentStartedAt < currentWait;
                if (!currentWithinDeadline)
                {
                    fence.Invalidate();
                    var cancellationCleanup = CancelDetachedAsync(
                        attemptCancellation,
                        attemptCancellationReservation!);
                    var cleanup = CompleteAndDisposeDetachedAsync(
                        currentOperation,
                        enumerator,
                        attemptCancellation,
                        cancellationCleanup,
                        attemptCancellationReservation!);
                    attemptCancellationReservation = null;
                    cleanupHandled = true;
                    var observedCleanup = RegisterDetachedCleanup(
                        providerId,
                        cleanup);
                    NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ProviderException(
                        elapsed.Elapsed >= _policy.TotalTimeout
                            ? "provider_total_timeout"
                            : "provider_event_materialization_timeout",
                        "provider",
                        "The provider did not materialize its current event in time.",
                        false);
                }

                var item = await currentOperation.ConfigureAwait(false);
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
                if (item.OpaqueContinuationUpdate is not null
                    && !string.Equals(
                        item.Kind,
                        ModelStreamEventKinds.Completed,
                        StringComparison.Ordinal))
                {
                    throw new ProviderException(
                        "provider_opaque_state_event_invalid",
                        "provider",
                        "A provider continuation update appeared outside the completion event.",
                        false);
                }

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
                        if (item.OpaqueContinuationUpdate is not null)
                        {
                            try
                            {
                                opaqueContinuationState =
                                    ProviderOpaqueContinuationState.Bind(
                                        routeIdentity,
                                        item.OpaqueContinuationUpdate);
                            }
                            catch (
                                ProviderOpaqueContinuationStateException)
                            {
                                throw new ProviderException(
                                    "provider_opaque_state_invalid",
                                    "provider",
                                    "The provider emitted an invalid continuation update.",
                                    false);
                            }
                        }

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
            exception.FallbackEligible
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
                    attemptCancellation,
                    attemptCancellationReservation!);
                var cleanup = DisposeAttemptAsync(
                    enumerator!,
                    attemptCancellation,
                    cancellationCleanup,
                    attemptCancellationReservation!);
                attemptCancellationReservation = null;
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
                                FallbackEligible: true
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

        if (usageSeen)
        {
            onCompletedInputUsage(usage.InputTokens);
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
            FinishReason = finishReason,
            OpaqueContinuationState =
                opaqueContinuationState?.Snapshot()
        };
    }

    internal static bool IsMoveNextWithinDeadline(
        Task completed,
        Task<bool> moveNext,
        TimeSpan completedAt,
        TimeSpan waitStartedAt,
        TimeSpan idleTimeout,
        TimeSpan totalTimeout)
    {
        return ReferenceEquals(completed, moveNext)
               && completedAt < totalTimeout
               && completedAt - waitStartedAt < idleTimeout;
    }

    private static void EnsureCapabilities(
        string providerId,
        ProviderCapabilities capabilities,
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        ProviderRequestSanitizer.ValidateProviderLimits(
            capabilities,
            tools,
            cancellationToken);
    }

    private static int? ResolvePromptTokenEstimate(
        IStreamingModelProvider provider,
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools,
        int? runtimeEstimate,
        CancellationToken cancellationToken)
    {
        if (provider is not IProviderPromptTokenEstimator estimator)
        {
            return runtimeEstimate;
        }

        try
        {
            _ = RuntimeGuard.RequiredUtf8(
                estimator.EstimatorId,
                128,
                nameof(IProviderPromptTokenEstimator.EstimatorId));
            _ = RuntimeGuard.RequiredUtf8(
                estimator.Version,
                64,
                nameof(IProviderPromptTokenEstimator.Version));
            var estimatorMessages = SnapshotMessages(
                messages,
                cancellationToken);
            var estimatorTools = SnapshotTools(
                tools,
                cancellationToken);
            var estimate = estimator.EstimatePromptTokens(
                estimatorMessages,
                estimatorTools);
            if (estimate < 0
                || estimate == 0 && (messages.Count > 0 || tools.Count > 0))
            {
                throw new ProviderException(
                    "provider_token_estimate_invalid",
                    "capability",
                    "The provider returned an invalid prompt-token estimate.",
                    ProviderFailureDisposition.Failover,
                    usageKnownToBeZero: true);
            }

            return estimate;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            and not StackOverflowException)
        {
            throw new ProviderException(
                "provider_token_estimator_failed",
                "capability",
                "The provider could not estimate its prompt tokens.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }
    }

    private static void ObserveActualInputTokens(
        IStreamingModelProvider provider,
        int? estimatedPromptTokens,
        int actualInputTokens)
    {
        if (estimatedPromptTokens is not > 0
            || provider is not ICalibratingProviderPromptTokenEstimator
                estimator)
        {
            return;
        }

        try
        {
            estimator.ObserveActualInputTokens(
                estimatedPromptTokens.Value,
                actualInputTokens);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            and not StackOverflowException)
        {
            // Calibration is advisory state learned after a completed,
            // accounted attempt. A custom observer must not invalidate that
            // result or trigger another billable request.
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

        if (capabilities.MaxContextTokens < 0
            || capabilities.MaxTools < 0
            || capabilities.MaxToolSchemaUtf8Bytes < 0)
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
            ReasoningInput = capabilities.ReasoningInput,
            ParallelToolCalls = capabilities.ParallelToolCalls,
            RequiresCompleteToolPairs =
                capabilities.RequiresCompleteToolPairs,
            MaxTools = capabilities.MaxTools,
            MaxToolSchemaUtf8Bytes =
                capabilities.MaxToolSchemaUtf8Bytes,
            MaxContextTokens = capabilities.MaxContextTokens
        };
    }

    private async ValueTask<ProviderPreparedRequest>
        PrepareRequestWithDeadlineAsync(
            IProviderRequestAdapter adapter,
            ProviderRequestPreparationContext context,
            Func<
                ProviderPreparedRequest,
                CancellationToken,
                ProviderPreparedRequest> validator,
            string providerId,
            CancellationToken cancellationToken,
            Action<Task>? onDetachedCleanup)
    {
        _ = validator ?? throw new ArgumentNullException(nameof(validator));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_cancellationDispatcher.TryReserve(
                out var cancellationReservation))
        {
            throw new ProviderException(
                "provider_preparation_cancellation_capacity_exceeded",
                "capacity",
                "Provider request-preparation cancellation capacity is exhausted.",
                false,
                usageKnownToBeZero: true);
        }

        var preparationCancellation = new CancellationTokenSource();
        Task<ProviderPreparedRequest> operation;
        try
        {
            var providerOperation = StartProviderCallback(
                () => adapter.PrepareRequestAsync(
                    context,
                    preparationCancellation.Token),
                usageKnownToBeZero: true);
            operation = ValidatePreparedRequestCallbackAsync(
                providerOperation,
                preparationCancellation.Token,
                validator);
        }
        catch
        {
            preparationCancellation.Dispose();
            cancellationReservation!.Dispose();
            throw;
        }

        var timeout = Task.Delay(
            _policy.RequestPreparationTimeout,
            cancellationToken);
        var completed = await Task.WhenAny(operation, timeout)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, operation))
        {
            try
            {
                var prepared = await operation.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return prepared;
            }
            finally
            {
                preparationCancellation.Dispose();
                cancellationReservation!.Dispose();
            }
        }

        var cancellation = CancelDetachedAsync(
            preparationCancellation,
            cancellationReservation!);
        var cleanup = CompleteDetachedPreparationAsync(
            operation,
            preparationCancellation,
            cancellation,
            cancellationReservation!);
        var observedCleanup = RegisterDetachedCleanup(providerId, cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);

        cancellationToken.ThrowIfCancellationRequested();
        throw new ProviderException(
            "provider_request_preparation_timeout",
            "provider",
            "Provider request preparation did not finish in time.",
            false,
            usageKnownToBeZero: true);
    }

    private static async Task<ProviderPreparedRequest>
        ValidatePreparedRequestCallbackAsync(
            Task<ProviderPreparedRequest> providerOperation,
            CancellationToken cancellationToken,
            Func<
                ProviderPreparedRequest,
                CancellationToken,
                ProviderPreparedRequest> validator)
    {
        var candidate = await providerOperation.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return validator(candidate, cancellationToken);
    }

    private async ValueTask<PreparedProviderStream>
        PrepareStreamWithDeadlineAsync(
            IPreparedStreamingModelProvider provider,
            ProviderStreamPreparationContext context,
            ProviderRouteIdentity routeIdentity,
            string providerId,
            CancellationToken cancellationToken,
            Action<Task>? onDetachedCleanup)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!routeIdentity.HasBoundDialectSemantics)
        {
            throw new ProviderException(
                "provider_dialect_contract_unspecified",
                "capability",
                "Prepared streaming requires a fully specified provider dialect.",
                false,
                usageKnownToBeZero: true);
        }

        if (!_cancellationDispatcher.TryReserve(
                out var cancellationReservation))
        {
            throw new ProviderException(
                "provider_preparation_cancellation_capacity_exceeded",
                "capacity",
                "Provider wire preparation cancellation capacity is exhausted.",
                false,
                usageKnownToBeZero: true);
        }

        var preparationCancellation = new CancellationTokenSource();
        Task<PreparedProviderStream> operation;
        try
        {
            var providerOperation = StartProviderCallback(
                () => provider.PrepareStreamAsync(
                    context,
                    preparationCancellation.Token),
                usageKnownToBeZero: true);
            operation = ValidatePreparedStreamCallbackAsync(
                providerOperation,
                routeIdentity,
                providerId,
                preparationCancellation.Token);
        }
        catch
        {
            preparationCancellation.Dispose();
            cancellationReservation!.Dispose();
            throw;
        }

        var timeout = Task.Delay(
            _policy.RequestPreparationTimeout,
            cancellationToken);
        var completed = await Task.WhenAny(operation, timeout)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, operation))
        {
            PreparedProviderStream? prepared = null;
            try
            {
                prepared = await operation.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var result = prepared;
                prepared = null;
                return result;
            }
            finally
            {
                try
                {
                    if (prepared is not null)
                    {
                        try
                        {
                            await RunProviderCleanupCallbackAsync(
                                    () => prepared.DisposeAsync())
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            MarkCleanupFailed(providerId);
                            throw PreparedStreamCleanupFailure(exception);
                        }
                    }
                }
                finally
                {
                    preparationCancellation.Dispose();
                    cancellationReservation!.Dispose();
                }
            }
        }

        var cancellation = CancelDetachedAsync(
            preparationCancellation,
            cancellationReservation!);
        var cleanup = CompleteDetachedPreparedStreamAsync(
            operation,
            preparationCancellation,
            cancellation,
            cancellationReservation!);
        var observedCleanup = RegisterDetachedCleanup(providerId, cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);

        cancellationToken.ThrowIfCancellationRequested();
        throw new ProviderException(
            "provider_wire_preparation_timeout",
            "provider",
            "Provider wire preparation did not finish in time.",
            false,
            usageKnownToBeZero: true);
    }

    private async Task<PreparedProviderStream>
        ValidatePreparedStreamCallbackAsync(
            Task<PreparedProviderStream> providerOperation,
            ProviderRouteIdentity routeIdentity,
            string providerId,
            CancellationToken cancellationToken)
    {
        PreparedProviderStream? candidate = null;
        try
        {
            candidate = await providerOperation.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate is null)
            {
                throw new ProviderException(
                    "provider_prepared_stream_invalid",
                    "provider",
                    "The provider returned an invalid prepared stream.",
                    false,
                    usageKnownToBeZero: true);
            }

            candidate.Evidence.ValidateForRoute(
                routeIdentity,
                requireAvailable: true);
            var result = candidate;
            candidate = null;
            return result;
        }
        finally
        {
            if (candidate is not null)
            {
                try
                {
                    await RunProviderCleanupCallbackAsync(
                            () => candidate.DisposeAsync())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    MarkCleanupFailed(providerId);
                    throw PreparedStreamCleanupFailure(exception);
                }
            }
        }
    }

    private async ValueTask DisposePreparedBeforeDispatchAsync(
        PreparedProviderStream prepared,
        string providerId,
        Action<Task>? onDetachedCleanup)
    {
        Task cleanup;
        try
        {
            cleanup = RunProviderCleanupCallbackAsync(
                () => prepared.DisposeAsync());
        }
        catch (Exception exception)
        {
            MarkCleanupFailed(providerId);
            throw PreparedStreamCleanupFailure(exception);
        }

        var completed = await Task.WhenAny(
                cleanup,
                Task.Delay(_policy.CleanupTimeout))
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, cleanup))
        {
            try
            {
                await cleanup.ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                MarkCleanupFailed(providerId);
                throw PreparedStreamCleanupFailure(exception);
            }
        }

        var observedCleanup = RegisterDetachedCleanup(
            providerId,
            cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);
        throw new ProviderException(
            "provider_prepared_stream_cleanup_timeout",
            "provider",
            "The prepared provider stream did not shut down in time.",
            false,
            usageKnownToBeZero: true);
    }

    private static ProviderException KnownZeroPreparationFailure(
        Exception exception)
    {
        if (exception is ProviderException providerException)
        {
            return providerException.UsageKnownToBeZero
                ? providerException
                : new ProviderException(
                    providerException.Code,
                    providerException.Category,
                    providerException.Message,
                    providerException.Disposition,
                    providerException.RetryAfter,
                    providerException,
                    usageKnownToBeZero: true);
        }

        return new ProviderException(
            "provider_request_preparation_failed",
            "provider",
            "The provider could not prepare a safe request.",
            false,
            innerException: exception,
            usageKnownToBeZero: true);
    }

    private static ProviderException PreparedStreamCleanupFailure(
        Exception exception)
    {
        return exception is ProviderException
        {
            Code: "provider_prepared_stream_cleanup_failed"
        } providerException
            ? providerException
            : new ProviderException(
                "provider_prepared_stream_cleanup_failed",
                "provider",
                "The prepared provider stream failed during shutdown.",
                false,
                innerException: exception,
                usageKnownToBeZero: true);
    }

    private static async Task CompleteDetachedPreparationAsync(
        Task operation,
        CancellationTokenSource cancellation,
        Task<bool> cancellationDispatch,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            try
            {
                await cancellationDispatch.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation failure cannot release a quarantined adapter.
            }

            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                // The caller already received a bounded preparation failure.
            }
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private async Task CompleteDetachedPreparedStreamAsync(
        Task<PreparedProviderStream> operation,
        CancellationTokenSource cancellation,
        Task<bool> cancellationDispatch,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            try
            {
                await cancellationDispatch.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation failure keeps the provider quarantined.
            }

            PreparedProviderStream? prepared = null;
            try
            {
                prepared = await operation.ConfigureAwait(false);
            }
            catch (ProviderException exception) when (
                string.Equals(
                    exception.Code,
                    "provider_prepared_stream_cleanup_failed",
                    StringComparison.Ordinal))
            {
                throw;
            }
            catch
            {
                // The bounded caller already received a preparation failure.
            }

            if (prepared is not null)
            {
                try
                {
                    await RunProviderCleanupCallbackAsync(
                            () => prepared.DisposeAsync())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new ProviderException(
                        "provider_prepared_stream_cleanup_failed",
                        "provider",
                        "The prepared provider stream failed during shutdown.",
                        false,
                        innerException: exception,
                        usageKnownToBeZero: true);
                }
            }
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private async Task CompleteDetachedStreamStartAsync(
        Task<IAsyncEnumerator<ModelStreamEvent>> operation,
        CancellationTokenSource cancellation,
        Task<bool> cancellationDispatch,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            try
            {
                await cancellationDispatch.ConfigureAwait(false);
            }
            catch
            {
                // The provider remains quarantined until its start task ends.
            }

            IAsyncEnumerator<ModelStreamEvent>? enumerator = null;
            try
            {
                enumerator = await operation.ConfigureAwait(false);
            }
            catch
            {
                // The bounded caller already received the start failure.
            }

            if (enumerator is not null)
            {
                try
                {
                    await RunProviderCleanupCallbackAsync(
                            () => enumerator.DisposeAsync())
                        .ConfigureAwait(false);
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
            }
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private static StreamingModelRequest ValidatePreparedRequest(
        StreamingModelRequest original,
        ProviderRequestPreparationReport? baseline,
        ProviderPreparedRequest prepared,
        string providerId,
        ProviderCapabilities capabilities,
        CancellationToken cancellationToken,
        bool validateEvidence = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared is null
            || prepared.Request is null
            || prepared.Report is null)
        {
            throw new ProviderException(
                "provider_request_adapter_invalid",
                "provider",
                $"Provider '{providerId}' returned an invalid prepared request.",
                false,
                usageKnownToBeZero: true);
        }

        var request = SnapshotPreparedRequest(
            prepared.Request,
            providerId,
            cancellationToken);
        PreflightPreparedRequest(
            request,
            providerId,
            cancellationToken);
        if (!string.Equals(request.RunId, original.RunId, StringComparison.Ordinal)
            || !string.Equals(
                request.RunAttemptId,
                original.RunAttemptId,
                StringComparison.Ordinal)
            || !string.Equals(request.TurnId, original.TurnId, StringComparison.Ordinal)
            || !string.Equals(
                request.ProviderAttemptId,
                original.ProviderAttemptId,
                StringComparison.Ordinal)
            || !string.Equals(
                request.StreamAttemptId,
                original.StreamAttemptId,
                StringComparison.Ordinal)
            || (original.MaxOutputTokens.HasValue
                && (!request.MaxOutputTokens.HasValue
                    || request.MaxOutputTokens.Value
                        > original.MaxOutputTokens.Value))
            || !OpaqueStateEquivalent(
                original.OpaqueContinuationState,
                request.OpaqueContinuationState)
            || !InferenceEquivalent(
                original.Inference,
                request.Inference)
            || !string.Equals(
                ToolSetDigest(request.Tools, cancellationToken),
                ToolSetDigest(original.Tools, cancellationToken),
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_request_adapter_identity_changed",
                "provider",
                $"Provider '{providerId}' changed protected request fields.",
                false,
                usageKnownToBeZero: true);
        }

        var outputDigest =
            ProviderRequestSanitizer.DigestMessages(
                request.Messages,
                cancellationToken);
        var report = prepared.Report;
        if (validateEvidence
            && (baseline is null
                || report.InputMessageCount != baseline.InputMessageCount
            || report.OutputMessageCount != request.Messages.Count
            || report.RemovedReasoningParts < 0
            || report.RemovedOrphanToolResults < 0
            || report.RemovedDuplicateToolCalls < 0
            || report.SynthesizedToolResults < 0
            || !string.Equals(
                report.InputDigest,
                baseline?.InputDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                report.OutputDigest,
                outputDigest,
                StringComparison.Ordinal)))
        {
            throw new ProviderException(
                "provider_request_adapter_evidence_invalid",
                "provider",
                $"Provider '{providerId}' returned stale request-preparation evidence.",
                false,
                usageKnownToBeZero: true);
        }

        if (!capabilities.ReasoningInput
            && ContainsReasoning(request.Messages, cancellationToken))
        {
            throw new ProviderException(
                "provider_request_adapter_reasoning_reintroduced",
                "provider",
                $"Provider '{providerId}' reintroduced private reasoning.",
                false,
                usageKnownToBeZero: true);
        }

        return request;
    }

    private static bool ContainsReasoning(
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(
                        part.Type,
                        NormalizedPartTypes.Reasoning,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ProviderRequestPreparationReport
        CombinePreparationReports(
            ProviderRequestPreparationReport sanitizer,
            ProviderRequestPreparationReport adapter)
    {
        return new ProviderRequestPreparationReport(
            sanitizer.InputMessageCount,
            adapter.OutputMessageCount,
            checked(
                sanitizer.RemovedReasoningParts
                + adapter.RemovedReasoningParts),
            checked(
                sanitizer.RemovedOrphanToolResults
                + adapter.RemovedOrphanToolResults),
            checked(
                sanitizer.RemovedDuplicateToolCalls
                + adapter.RemovedDuplicateToolCalls),
            checked(
                sanitizer.SynthesizedToolResults
                + adapter.SynthesizedToolResults),
            sanitizer.InputDigest,
            adapter.OutputDigest);
    }

    private static void PreflightPreparedRequest(
        StreamingModelRequest request,
        string providerId,
        CancellationToken cancellationToken)
    {
        ProviderRequestContentGuard.EnsurePreparedWithinLimits(
            request.Messages,
            request.Tools,
            providerId,
            cancellationToken);
    }

    private static string ToolSetDigest(
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools,
        CancellationToken cancellationToken)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "provider-tool-set");
        var toolCount = tools.Count;
        digest.Add("count", toolCount);
        for (var toolIndex = 0;
             toolIndex < toolCount;
             toolIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = tools[toolIndex];
            digest.Add(
                "tool",
                GameAgent.Protocol.ProtocolJson.Serialize(tool));
        }

        return digest.Finish();
    }

    private static StreamingModelRequest SnapshotPreparedRequest(
        StreamingModelRequest request,
        string providerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Capture every adapter-controlled reference and scalar once. The
        // bounded indexed copies below deliberately never enumerate the
        // adapter's lists or re-read their Count values.
        var runId = request.RunId;
        var runAttemptId = request.RunAttemptId;
        var turnId = request.TurnId;
        var providerAttemptId = request.ProviderAttemptId;
        var streamAttemptId = request.StreamAttemptId;
        var messageSource = request.Messages;
        var toolSource = request.Tools;
        var maxOutputTokens = request.MaxOutputTokens;
        var inference = request.Inference?.CloneValidated();
        var opaqueContinuationState =
            request.OpaqueContinuationState?.Snapshot();
        var messages = SnapshotPreparedList(
            messageSource,
            ProviderRequestContentGuard.MaxMessages,
            providerId,
            cancellationToken);
        var tools = SnapshotPreparedList(
            toolSource,
            ProviderRequestContentGuard.MaxTools,
            providerId,
            cancellationToken);

        var shallowSnapshot = new StreamingModelRequest
        {
            RunId = runId,
            RunAttemptId = runAttemptId,
            TurnId = turnId,
            ProviderAttemptId = providerAttemptId,
            StreamAttemptId = streamAttemptId,
            Messages = messages,
            Tools = tools,
            MaxOutputTokens = maxOutputTokens,
            Inference = inference,
            OpaqueContinuationState = opaqueContinuationState
        };
        PreflightPreparedRequest(
            shallowSnapshot,
            providerId,
            cancellationToken);

        return new StreamingModelRequest
        {
            RunId = runId,
            RunAttemptId = runAttemptId,
            TurnId = turnId,
            ProviderAttemptId = providerAttemptId,
            StreamAttemptId = streamAttemptId,
            Messages = SnapshotMessages(messages, cancellationToken),
            Tools = SnapshotTools(tools, cancellationToken),
            MaxOutputTokens = maxOutputTokens,
            Inference = shallowSnapshot.Inference?.CloneValidated(),
            OpaqueContinuationState =
                opaqueContinuationState?.Snapshot()
        };
    }

    private static bool OpaqueStateEquivalent(
        ProviderOpaqueContinuationState? left,
        ProviderOpaqueContinuationState? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
                   left.ProviderId,
                   right.ProviderId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.ProviderRouteDigest,
                   right.ProviderRouteDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.StateVersion,
                   right.StateVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.PayloadDigest,
                   right.PayloadDigest,
                   StringComparison.Ordinal)
               && left.Persistence == right.Persistence;
    }

    private static bool InferenceEquivalent(
        ModelInferenceOptions? left,
        ModelInferenceOptions? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var a = left.CloneValidated();
        var b = right.CloneValidated();
        return a.ReasoningEnabled == b.ReasoningEnabled
               && string.Equals(
                   a.ReasoningEffort,
                   b.ReasoningEffort,
                   StringComparison.Ordinal)
               && a.ReasoningTokenBudget == b.ReasoningTokenBudget
               && a.Temperature == b.Temperature
               && a.TopP == b.TopP
               && a.Seed == b.Seed
               && a.PromptCachingEnabled == b.PromptCachingEnabled
               && string.Equals(
                   a.PromptCacheKey,
                   b.PromptCacheKey,
                   StringComparison.Ordinal)
               && string.Equals(
                   a.PromptCacheRetention,
                   b.PromptCacheRetention,
                   StringComparison.Ordinal);
    }

    private static T[] SnapshotPreparedList<T>(
        IReadOnlyList<T>? source,
        int maxCount,
        string providerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
        {
            throw ProviderRequestContentGuard.PreparedLimitExceeded(
                providerId);
        }

        var count = source.Count;
        if (count < 0 || count > maxCount)
        {
            throw ProviderRequestContentGuard.PreparedLimitExceeded(
                providerId);
        }

        var snapshot = new T[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot[index] = source[index];
        }

        return snapshot;
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
            CostUsd = usage.CostUsd,
            Samples = usage.Samples,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            CacheMissTokens = usage.CacheMissTokens,
            ReasoningTokens = usage.ReasoningTokens,
            ProviderTotalTokens = usage.ProviderTotalTokens,
            Availability = usage.Availability
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
        if (aggregate.Samples == 0)
        {
            aggregate.CacheReadTokens = usage.CacheReadTokens;
            aggregate.CacheWriteTokens = usage.CacheWriteTokens;
            aggregate.CacheMissTokens = usage.CacheMissTokens;
            aggregate.ReasoningTokens = usage.ReasoningTokens;
            aggregate.ProviderTotalTokens = usage.ProviderTotalTokens;
            aggregate.Availability = usage.Availability;
            aggregate.CostUsd = usage.CostUsd;
            aggregate.Samples = usage.Samples;
            return;
        }

        aggregate.CacheReadTokens = AddAvailableTokenCounts(
            aggregate.CacheReadTokens,
            usage.CacheReadTokens);
        aggregate.CacheWriteTokens = AddAvailableTokenCounts(
            aggregate.CacheWriteTokens,
            usage.CacheWriteTokens);
        aggregate.CacheMissTokens = AddAvailableTokenCounts(
            aggregate.CacheMissTokens,
            usage.CacheMissTokens);
        aggregate.ReasoningTokens = AddAvailableTokenCounts(
            aggregate.ReasoningTokens,
            usage.ReasoningTokens);
        aggregate.ProviderTotalTokens = AddAvailableTokenCounts(
            aggregate.ProviderTotalTokens,
            usage.ProviderTotalTokens);
        aggregate.Samples = (int)Math.Min(
            int.MaxValue,
            (long)aggregate.Samples + usage.Samples);
        if (string.Equals(
                aggregate.Availability,
                UsageAvailabilityStates.CostAvailable,
                StringComparison.Ordinal)
            && string.Equals(
                usage.Availability,
                UsageAvailabilityStates.CostAvailable,
                StringComparison.Ordinal))
        {
            aggregate.CostUsd = RuntimePromptBuilder.AddCost(
                aggregate.CostUsd,
                usage.CostUsd);
        }
        else
        {
            aggregate.Availability =
                UsageAvailabilityStates.CostUnavailable;
            aggregate.CostUsd = RuntimePromptBuilder.AddCost(
                aggregate.CostUsd,
                usage.CostUsd);
        }
    }

    private static int? AddAvailableTokenCounts(
        int? current,
        int? delta)
    {
        if (!current.HasValue || !delta.HasValue)
        {
            return null;
        }

        return (int)Math.Min(
            int.MaxValue,
            (long)current.Value + delta.Value);
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
        var observed = ObserveQuarantinedCleanupAsync(
            providerId,
            cleanup);
        _ = observed.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return observed;
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
            throw;
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
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken)
    {
        var messageCount = messages.Count;
        var snapshot = new NormalizedMessage[messageCount];
        for (var index = 0; index < messageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[index]
                ?? throw new ArgumentException(
                    "Provider message lists cannot contain null entries.",
                    nameof(messages));
            if (message.Role is not NormalizedRoles.System
                and not NormalizedRoles.User
                and not NormalizedRoles.Assistant
                and not NormalizedRoles.Tool)
            {
                throw new ProviderException(
                    "provider_role_unsupported",
                    "validation",
                    "A normalized message has an unsupported role.",
                    false,
                    usageKnownToBeZero: true);
            }

            try
            {
                snapshot[index] =
                    NormalizedMessageJournalCodec.CloneValidated(
                        message,
                        cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new ProviderException(
                    "provider_message_invalid",
                    "validation",
                    "A normalized message is invalid.",
                    false,
                    innerException: exception,
                    usageKnownToBeZero: true);
            }
        }

        return snapshot;
    }

    private static T[] SnapshotRequestReferences<T>(
        IReadOnlyList<T> source,
        int maximumItems,
        string nullItemMessage,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = source.Count;
        if (count is < 0 || count > maximumItems)
        {
            throw ProviderRequestInputLimitExceeded();
        }

        var snapshot = new T[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? item;
            try
            {
                item = source[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new ProviderException(
                    "provider_request_input_changed",
                    "validation",
                    "The provider request input changed while it was "
                    + "being snapshotted.",
                    false,
                    innerException: exception,
                    usageKnownToBeZero: true);
            }

            snapshot[index] = item
                              ?? throw new ProviderException(
                                  "provider_request_input_limit",
                                  "validation",
                                  nullItemMessage,
                                  false,
                                  usageKnownToBeZero: true);
        }

        return snapshot;
    }

    private static ProviderException ProviderRequestInputLimitExceeded()
    {
        return new ProviderException(
            "provider_request_input_limit",
            "validation",
            "The provider request exceeds the runtime input limit.",
            false,
            usageKnownToBeZero: true);
    }

    private static IReadOnlyList<GameAgent.Protocol.ToolDescriptor> SnapshotTools(
        IReadOnlyList<GameAgent.Protocol.ToolDescriptor> tools,
        CancellationToken cancellationToken)
    {
        var toolCount = tools.Count;
        var snapshot = new GameAgent.Protocol.ToolDescriptor[toolCount];
        for (var index = 0; index < toolCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        CancellationTokenSource cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            return cancellationReservation.DispatchAsync(
                () => TryCancel(cancellation));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private void RegisterCancellationCleanup(
        CancellationTokenSource cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation,
        string providerId,
        Action<Task>? onDetachedCleanup)
    {
        var cleanup = CancelAndDisposeAsync(
            cancellation,
            cancellationReservation);
        var observedCleanup = RegisterDetachedCleanup(providerId, cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observedCleanup);
    }

    private static async Task CancelAndDisposeAsync(
        CancellationTokenSource cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await CancelDetachedAsync(
                    cancellation,
                    cancellationReservation)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private async ValueTask DelayWithDetachedCancellationAsync(
        IRuntimeDelay delay,
        TimeSpan duration,
        CancellationToken cancellationToken,
        BoundedCancellationDispatcher cancellationDispatcher,
        string providerId,
        Action<Task>? onDetachedCleanup)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationDispatcher.TryReserve(
                out var cancellationReservation))
        {
            throw new ProviderException(
                "provider_cancellation_capacity_exceeded",
                "capacity",
                "Provider cancellation cleanup capacity is exhausted.",
                false);
        }

        var delayCancellation = new CancellationTokenSource();
        Task delayTask;
        try
        {
            delayTask = delay.DelayAsync(
                    duration,
                    delayCancellation.Token)
                .AsTask();
        }
        catch
        {
            delayCancellation.Dispose();
            cancellationReservation!.Dispose();
            throw;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            try
            {
                await delayTask.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    delayCancellation.Dispose();
                }
                finally
                {
                    cancellationReservation!.Dispose();
                }
            }

            return;
        }

        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration;
        try
        {
            registration = cancellationToken.Register(
                () => cancellationSignal.TrySetResult(true));
        }
        catch
        {
            await TrackDetachedCancellationCleanupIfPendingAsync(
                    providerId,
                    CancelObserveAndDisposeAsync(
                        delayTask,
                        delayCancellation,
                        cancellationReservation!),
                    onDetachedCleanup)
                .ConfigureAwait(false);
            throw;
        }

        using (registration)
        {
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
                    try
                    {
                        delayCancellation.Dispose();
                    }
                    finally
                    {
                        cancellationReservation!.Dispose();
                    }
                }

                return;
            }

            await TrackDetachedCancellationCleanupIfPendingAsync(
                    providerId,
                    CancelObserveAndDisposeAsync(
                        delayTask,
                        delayCancellation,
                        cancellationReservation!),
                    onDetachedCleanup)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async ValueTask
        TrackDetachedCancellationCleanupIfPendingAsync(
            string providerId,
            Task cleanup,
            Action<Task>? onDetachedCleanup)
    {
        var completed = await Task.WhenAny(
                cleanup,
                Task.Delay(CancellationCleanupGrace))
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, cleanup))
        {
            await cleanup.ConfigureAwait(false);
            return;
        }

        TrackDetachedCancellationCleanup(
            providerId,
            cleanup,
            onDetachedCleanup);
    }

    private void TrackDetachedCancellationCleanup(
        string providerId,
        Task cleanup,
        Action<Task>? onDetachedCleanup)
    {
        var observed = RegisterDetachedCleanup(providerId, cleanup);
        NotifyDetachedCleanup(onDetachedCleanup, observed);
    }

    private static async Task CancelObserveAndDisposeAsync(
        Task operation,
        CancellationTokenSource cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        Task cancellationTask;
        if (operation.IsCompleted)
        {
            cancellationTask = Task.CompletedTask;
        }
        else
        {
            cancellationTask = CancelDetachedAsync(
                cancellation,
                cancellationReservation);
        }

        try
        {
            await ObserveDetachedAsync(operation).ConfigureAwait(false);
            await cancellationTask.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
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

    private async Task DisposeAttemptAsync(
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        CancellationTokenSource attemptCancellation,
        Task cancellationCleanup,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await cancellationCleanup.ConfigureAwait(false);
            await RunProviderCleanupCallbackAsync(
                    () => enumerator.DisposeAsync())
                .ConfigureAwait(false);
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
            try
            {
                attemptCancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private async Task CompleteAndDisposeDetachedAsync(
        Task providerOperation,
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        CancellationTokenSource attemptCancellation,
        Task cancellationCleanup,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await providerOperation.ConfigureAwait(false);
        }
        catch
        {
            // The attempt has already been fenced off. Detached cleanup must not
            // surface provider failures on the finalizer path.
        }

        await DisposeAttemptAsync(
                enumerator,
                attemptCancellation,
                cancellationCleanup,
                cancellationReservation)
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
        if (usage.InputTokens < 0
            || usage.OutputTokens < 0
            || usage.Samples < 1
            || IsNegative(usage.CacheReadTokens)
            || IsNegative(usage.CacheWriteTokens)
            || IsNegative(usage.CacheMissTokens)
            || IsNegative(usage.ReasoningTokens)
            || IsNegative(usage.ProviderTotalTokens))
        {
            throw LimitExceeded(
                "provider_usage_invalid",
                "The provider emitted invalid token usage.");
        }

        if (!string.Equals(
                usage.Availability,
                UsageAvailabilityStates.CostAvailable,
                StringComparison.Ordinal)
            && !string.Equals(
                usage.Availability,
                UsageAvailabilityStates.CostUnavailable,
                StringComparison.Ordinal))
        {
            throw LimitExceeded(
                "provider_usage_invalid",
                "The provider emitted an invalid usage-availability state.");
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

        if (usage.CacheReadTokens.HasValue
            && usage.CacheMissTokens.HasValue
            && (long)usage.CacheReadTokens.Value
               + usage.CacheMissTokens.Value
               != usage.InputTokens)
        {
            throw LimitExceeded(
                "provider_usage_invalid",
                "The provider emitted inconsistent cache-token usage.");
        }
    }

    private static bool IsNegative(int? value)
    {
        return value.HasValue && value.Value < 0;
    }

    private sealed class PreparedStreamProviderAdapter :
        IStreamingModelProvider
    {
        private readonly IStreamingModelProvider _provider;
        private readonly PreparedProviderStream _prepared;
        private readonly BoundedCallbackExecutionDispatcher
            _callbackExecutionDispatcher;
        private readonly Func<PreparedProviderStream, ValueTask>
            _disposeIfUnclaimed;
        private int _ownership;

        public PreparedStreamProviderAdapter(
            IStreamingModelProvider provider,
            PreparedProviderStream prepared,
            BoundedCallbackExecutionDispatcher callbackExecutionDispatcher,
            Func<PreparedProviderStream, ValueTask> disposeIfUnclaimed)
        {
            _provider = provider;
            _prepared = prepared;
            _callbackExecutionDispatcher = callbackExecutionDispatcher
                                           ?? throw new ArgumentNullException(
                                               nameof(
                                                   callbackExecutionDispatcher));
            _disposeIfUnclaimed = disposeIfUnclaimed
                                  ?? throw new ArgumentNullException(
                                      nameof(disposeIfUnclaimed));
        }

        public string ProviderId => _provider.ProviderId;

        public ProviderCapabilities Capabilities => _provider.Capabilities;

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request ?? throw new ArgumentNullException(nameof(request));
            if (Interlocked.CompareExchange(
                    ref _ownership,
                    1,
                    comparand: 0) != 0)
            {
                throw new ProviderException(
                    "provider_prepared_stream_unavailable",
                    "provider",
                    "The prepared provider stream is no longer available.",
                    false,
                    usageKnownToBeZero: true);
            }

            return new OwnedPreparedEnumerable(
                _prepared,
                cancellationToken,
                _callbackExecutionDispatcher);
        }

        public async ValueTask DisposeIfUnclaimedAsync()
        {
            if (Interlocked.CompareExchange(
                    ref _ownership,
                    2,
                    comparand: 0) != 0)
            {
                return;
            }

            await _disposeIfUnclaimed(_prepared)
                .ConfigureAwait(false);
        }

        private sealed class OwnedPreparedEnumerable :
            IAsyncEnumerable<ModelStreamEvent>
        {
            private readonly PreparedProviderStream _prepared;
            private readonly CancellationToken _streamCancellation;
            private readonly BoundedCallbackExecutionDispatcher
                _callbackExecutionDispatcher;
            private int _claimed;

            public OwnedPreparedEnumerable(
                PreparedProviderStream prepared,
                CancellationToken streamCancellation,
                BoundedCallbackExecutionDispatcher callbackExecutionDispatcher)
            {
                _prepared = prepared;
                _streamCancellation = streamCancellation;
                _callbackExecutionDispatcher = callbackExecutionDispatcher;
            }

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _claimed, 1) != 0)
                {
                    throw new ProviderException(
                        "provider_prepared_stream_unavailable",
                        "provider",
                        "The prepared provider stream is no longer available.",
                        false,
                        usageKnownToBeZero: true);
                }

                return new OwnedPreparedEnumerator(
                    _prepared,
                    cancellationToken.CanBeCanceled
                        ? cancellationToken
                        : _streamCancellation,
                    _callbackExecutionDispatcher);
            }
        }

        private sealed class OwnedPreparedEnumerator :
            IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly PreparedProviderStream _prepared;
            private readonly CancellationToken _cancellationToken;
            private readonly BoundedCallbackExecutionDispatcher
                _callbackExecutionDispatcher;
            private IAsyncEnumerator<ModelStreamEvent>? _inner;
            private int _disposed;

            public OwnedPreparedEnumerator(
                PreparedProviderStream prepared,
                CancellationToken cancellationToken,
                BoundedCallbackExecutionDispatcher callbackExecutionDispatcher)
            {
                _prepared = prepared;
                _cancellationToken = cancellationToken;
                _callbackExecutionDispatcher = callbackExecutionDispatcher;
            }

            public ModelStreamEvent Current =>
                _inner?.Current
                ?? throw new InvalidOperationException(
                    "The prepared provider stream has no current event.");

            public ValueTask<bool> MoveNextAsync()
            {
                return MoveNextCoreAsync();
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Exception? cleanupFailure = null;
                if (_inner is not null)
                {
                    try
                    {
                        await RunProviderCleanupCallbackAsync(
                                _callbackExecutionDispatcher,
                                () => _inner.DisposeAsync())
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = exception;
                    }
                }

                try
                {
                    await RunProviderCleanupCallbackAsync(
                            _callbackExecutionDispatcher,
                            () => _prepared.DisposeAsync())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                if (cleanupFailure is not null)
                {
                    throw new ProviderException(
                        "provider_prepared_stream_cleanup_failed",
                        "provider",
                        "The prepared provider stream failed during shutdown.",
                        false,
                        innerException: cleanupFailure);
                }
            }

            private async ValueTask<bool> MoveNextCoreAsync()
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                try
                {
                    if (_inner is null)
                    {
                        _inner = await StartProviderCallback(
                                _callbackExecutionDispatcher,
                                () =>
                                {
                                    var stream = _prepared.StreamAsync(
                                                     _cancellationToken)
                                                 ?? throw new ProviderException(
                                                     "provider_prepared_stream_invalid",
                                                     "provider",
                                                     "The provider returned an invalid prepared stream.",
                                                     false,
                                                     usageKnownToBeZero: true);
                                    var inner = stream.GetAsyncEnumerator(
                                                    _cancellationToken)
                                                ?? throw new ProviderException(
                                                    "provider_prepared_stream_invalid",
                                                    "provider",
                                                    "The provider returned an invalid prepared stream.",
                                                    false,
                                                    usageKnownToBeZero: true);
                                    return new ValueTask<IAsyncEnumerator<
                                        ModelStreamEvent>>(inner);
                                },
                                usageKnownToBeZero: true)
                            .ConfigureAwait(false);
                    }

                    return await StartProviderCallback(
                            _callbackExecutionDispatcher,
                            () => _inner.MoveNextAsync(),
                            usageKnownToBeZero: false)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
    }

    private Task<TResult> StartProviderCallback<TResult>(
        Func<ValueTask<TResult>> callback,
        bool usageKnownToBeZero)
    {
        return StartProviderCallback(
            _callbackExecutionDispatcher,
            callback,
            usageKnownToBeZero);
    }

    private Task StartProviderCallback(
        Func<ValueTask> callback,
        bool usageKnownToBeZero)
    {
        return StartProviderCallback(
            _callbackExecutionDispatcher,
            callback,
            usageKnownToBeZero);
    }

    private Task RunProviderCleanupCallbackAsync(
        Func<ValueTask> callback)
    {
        return RunProviderCleanupCallbackAsync(
            _callbackExecutionDispatcher,
            callback);
    }

    private static Task<TResult> StartProviderCallback<TResult>(
        BoundedCallbackExecutionDispatcher dispatcher,
        Func<ValueTask<TResult>> callback,
        bool usageKnownToBeZero)
    {
        if (!dispatcher.TryExecute(callback, out var operation))
        {
            throw ProviderExecutionCapacityExceeded(usageKnownToBeZero);
        }

        return operation;
    }

    private static Task StartProviderCallback(
        BoundedCallbackExecutionDispatcher dispatcher,
        Func<ValueTask> callback,
        bool usageKnownToBeZero)
    {
        if (!dispatcher.TryExecute(callback, out var operation))
        {
            throw ProviderExecutionCapacityExceeded(usageKnownToBeZero);
        }

        return operation;
    }

    private static async Task RunProviderCleanupCallbackAsync(
        BoundedCallbackExecutionDispatcher dispatcher,
        Func<ValueTask> callback)
    {
        _ = callback ?? throw new ArgumentNullException(nameof(callback));
        await dispatcher.ExecuteWhenAvailableAsync(callback)
            .ConfigureAwait(false);
    }

    private static ProviderException ProviderExecutionCapacityExceeded(
        bool usageKnownToBeZero)
    {
        return new ProviderException(
            "provider_execution_capacity_exhausted",
            "capacity",
            "Provider callback execution capacity is exhausted.",
            false,
            usageKnownToBeZero: usageKnownToBeZero);
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
