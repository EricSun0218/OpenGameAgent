using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.OpenAICompatible;

public sealed class OpenAiCompatibleStreamingProvider :
    IStreamingModelProvider,
    IPreparedStreamingModelProvider,
    IProviderRouteMetadataSource,
    ICalibratingProviderPromptTokenEstimator
{
    private const int MaxRequestBodyUtf8Bytes = 8 * 1_048_576;
    private const int MaxDirectTools = 128;
    private const string RoutePolicyVersion =
        "openai-compatible.route-policy.v4";
    private const string RequestLayoutPolicy =
        "chat-completions.request-layout.v2";
    private const string UsageParsingPolicy =
        "chat-completions.usage-accounting.v2";
    private const string PricingPolicy =
        "configured-token-pricing.v2";
    private const string WireContentType =
        "application/json; charset=utf-8";

    private static readonly ProviderDialectContract DialectContract = new(
        "openai.chat-completions.sse.v1",
        ProviderRequestFamily.ChatCompletions,
        "chat-completions.request.v1",
        ProviderStreamFraming.ServerSentEvents,
        "sse.data-json.v1",
        "chat-completions.tool-calls.v1",
        "chat-completions.streaming-usage.v2",
        "chat-completions.reasoning-content.v1",
        WireContentType);

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly IProviderCredentialSource _credentials;
    private readonly IStreamingHttpTransport _transport;
    private readonly Uri _endpoint;
    private readonly ProviderCapabilities _capabilities;
    private readonly ProviderRouteMetadata _routeMetadata;
    private readonly IProviderPromptTokenEstimator _promptTokenEstimator;
    private readonly string _promptTokenEstimatorId;
    private readonly string _promptTokenEstimatorVersion;

    public OpenAiCompatibleStreamingProvider(
        OpenAiCompatibleProviderOptions options,
        IProviderCredentialSource credentials,
        IStreamingHttpTransport transport,
        IProviderPromptTokenEstimator? promptTokenEstimator = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _credentials =
            credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transport =
            transport ?? throw new ArgumentNullException(nameof(transport));
        _promptTokenEstimator =
            promptTokenEstimator ?? new CalibratingProviderTokenEstimator();
        try
        {
            _promptTokenEstimatorId = ValidateEstimatorIdentity(
                _promptTokenEstimator.EstimatorId,
                nameof(IProviderPromptTokenEstimator.EstimatorId),
                128);
            _promptTokenEstimatorVersion = ValidateEstimatorIdentity(
                _promptTokenEstimator.Version,
                nameof(IProviderPromptTokenEstimator.Version),
                64);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            and not StackOverflowException)
        {
            throw new ArgumentException(
                "The provider prompt-token estimator identity is invalid.",
                nameof(promptTokenEstimator));
        }

        _endpoint = BuildEndpoint(_options);
        _capabilities = new ProviderCapabilities
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            ReasoningInput =
                _options.ReplayReasoningContent
                && (!_options.ReasoningContentReplayRequiresThinkingMode
                    || string.Equals(
                        _options.ThinkingMode,
                        "enabled",
                        StringComparison.Ordinal)),
            ParallelToolCalls = _options.ParallelToolCalls is not false,
            RequiresCompleteToolPairs = true,
            MaxTools = MaxDirectTools,
            MaxContextTokens = _options.MaxContextTokens
        };
        _routeMetadata = new ProviderRouteMetadata(
            _options.Model,
            DialectContract,
            RoutePolicyVersion,
            ComputeRoutePolicyDigest(
                _options,
                _endpoint,
                _promptTokenEstimatorId,
                _promptTokenEstimatorVersion));
    }

    public string ProviderId => _options.ProviderId;

    public ProviderRouteMetadata RouteMetadata => _routeMetadata;

    public string EstimatorId => _promptTokenEstimatorId;

    public string Version => _promptTokenEstimatorVersion;

    public ProviderCapabilities Capabilities => new()
    {
        Streaming = _capabilities.Streaming,
        ToolCalling = _capabilities.ToolCalling,
        JsonOutput = _capabilities.JsonOutput,
        ReasoningInput = _capabilities.ReasoningInput,
        ParallelToolCalls = _capabilities.ParallelToolCalls,
        RequiresCompleteToolPairs = _capabilities.RequiresCompleteToolPairs,
        MaxTools = _capabilities.MaxTools,
        MaxToolSchemaUtf8Bytes =
            _capabilities.MaxToolSchemaUtf8Bytes,
        MaxContextTokens = _capabilities.MaxContextTokens
    };

    public int EstimatePromptTokens(
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<ToolDescriptor> tools)
    {
        return _promptTokenEstimator.EstimatePromptTokens(messages, tools);
    }

    public void ObserveActualInputTokens(
        int estimatedTokens,
        int actualInputTokens)
    {
        if (_promptTokenEstimator
            is ICalibratingProviderPromptTokenEstimator calibrating)
        {
            calibrating.ObserveActualInputTokens(
                estimatedTokens,
                actualInputTokens);
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var routeIdentity = new ProviderRouteIdentity(
            ProviderId,
            _routeMetadata,
            _capabilities);
        var prepared = await PrepareStreamAsync(
                new ProviderStreamPreparationContext(
                    ProviderId,
                    routeIdentity,
                    request),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await foreach (var item in prepared
                               .StreamAsync(cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<PreparedProviderStream> PrepareStreamAsync(
        ProviderStreamPreparationContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                context.ProviderId,
                ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                context.RouteIdentity.ProviderId,
                ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                context.RouteIdentity.ModelId,
                _routeMetadata.ModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                context.RouteIdentity.RoutePolicyDigest,
                _routeMetadata.RoutePolicyDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                context.RouteIdentity.DialectSemanticDigest,
                DialectContract.SemanticDigest,
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_route_identity_mismatch",
                "provider",
                "The provider route identity does not match this adapter.",
                false,
                usageKnownToBeZero: true);
        }

        StreamingModelRequest requestSnapshot;
        try
        {
            requestSnapshot = SnapshotRequest(
                context.Request,
                cancellationToken);
            ValidateRequest(requestSnapshot, cancellationToken);
        }
        catch (ProviderException exception)
        {
            throw KnownZero(exception);
        }

        byte[] body;
        try
        {
            body = BuildRequestBody(requestSnapshot);
        }
        catch (ProviderException exception)
        {
            throw KnownZero(exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                "provider_request_encoding_failed",
                "validation",
                "The provider request could not be encoded.",
                false,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        try
        {
            var evidence = ProviderWireRequestEvidence.CreateAvailable(
                body,
                WireContentType,
                context.RouteIdentity);
            return new ValueTask<PreparedProviderStream>(
                new OpenAiPreparedProviderStream(
                    this,
                    requestSnapshot.StreamAttemptId,
                    body,
                    evidence));
        }
        catch
        {
            Array.Clear(body, 0, body.Length);
            throw;
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamPreparedAsync(
        string streamAttemptId,
        byte[] body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _credentials
                .GetBearerTokenAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The provider credential is unavailable.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        try
        {
            token = BearerTokenValidator.ValidateAndTrim(
                token,
                nameof(token));
        }
        catch (ArgumentException exception)
        {
            token = string.Empty;
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The provider credential is unavailable.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        IStreamingHttpResponse response;
        try
        {
            response = await _transport.SendAsync(
                    new StreamingHttpRequest
                    {
                        Uri = _endpoint,
                        BearerToken = token,
                        Body = body,
                        ContentType = WireContentType
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                "provider_connect_failed",
                "network",
                "The provider connection failed.",
                true,
                innerException: exception);
        }
        finally
        {
            token = string.Empty;
            Array.Clear(body, 0, body.Length);
        }

        using (response)
        {
            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                throw MapHttpError(
                    response.StatusCode,
                    response.GetHeader("Retry-After"));
            }

            using var cancellationRegistration =
                cancellationToken.Register(response.Dispose);
            var parser = new SseChunkParser(
                streamAttemptId,
                _options.MaxSseEventCharacters,
                _options.InputCacheHitUsdPerMillionTokens,
                _options.InputCacheMissUsdPerMillionTokens,
                _options.InputCacheWriteUsdPerMillionTokens,
                _options.OutputUsdPerMillionTokens);
            using var reader = new StreamReader(
                response.Content,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);
            var lineReader = new BoundedTextLineReader(
                reader,
                _options.MaxSseLineCharacters);
            var data = new StringBuilder();
            var doneSeen = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line;
                try
                {
                    line = await lineReader
                        .ReadLineAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                    or ObjectDisposedException
                    or DecoderFallbackException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ProviderException(
                        "provider_stream_read_failed",
                        "network",
                        "The provider stream could not be read.",
                        true,
                        innerException: exception);
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var payload = data.ToString();
                    data.Clear();
                    if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                    {
                        doneSeen = true;
                        break;
                    }

                    foreach (var item in parser.Parse(payload))
                    {
                        yield return item;
                    }

                    continue;
                }

                if (line[0] == ':')
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = line.Substring(5);
                if (value.StartsWith(" ", StringComparison.Ordinal))
                {
                    value = value.Substring(1);
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
                if (data.Length > _options.MaxSseEventCharacters)
                {
                    throw new ProviderException(
                        "provider_sse_event_too_large",
                        "provider",
                        "The provider emitted an oversized SSE event.",
                        false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length > 0)
            {
                throw new ProviderException(
                    "provider_sse_truncated_event",
                    "network",
                    "The provider stream ended during an SSE event.",
                    true);
            }

            if (!doneSeen)
            {
                throw new ProviderException(
                    "provider_sse_done_missing",
                    "network",
                    "The provider stream ended before its completion sentinel.",
                    true);
            }
        }
    }

    private sealed class OpenAiPreparedProviderStream :
        PreparedProviderStream
    {
        private readonly object _gate = new();
        private readonly OpenAiCompatibleStreamingProvider _owner;
        private readonly string _streamAttemptId;
        private readonly TaskCompletionSource<bool> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _body;
        private bool _claimed;
        private bool _active;
        private bool _disposeRequested;

        public OpenAiPreparedProviderStream(
            OpenAiCompatibleStreamingProvider owner,
            string streamAttemptId,
            byte[] body,
            ProviderWireRequestEvidence evidence)
            : base(evidence)
        {
            _owner = owner;
            _streamAttemptId = streamAttemptId;
            _body = body;
        }

        public override IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            CancellationToken cancellationToken)
        {
            return EnumerateAsync(cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposeRequested = true;
                if (!_active)
                {
                    ClearBodyUnderLock();
                    _completed.TrySetResult(true);
                    return default;
                }

                return new ValueTask(_completed.Task);
            }
        }

        private async IAsyncEnumerable<ModelStreamEvent> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            byte[] body;
            lock (_gate)
            {
                if (_claimed || _disposeRequested || _body is null)
                {
                    throw new ProviderException(
                        "provider_prepared_stream_unavailable",
                        "provider",
                        "The prepared provider stream is no longer available.",
                        false,
                        usageKnownToBeZero: true);
                }

                _claimed = true;
                _active = true;
                body = _body;
            }

            try
            {
                await foreach (var item in _owner
                                   .StreamPreparedAsync(
                                       _streamAttemptId,
                                       body,
                                       cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _active = false;
                    ClearBodyUnderLock();
                }

                _completed.TrySetResult(true);
            }
        }

        private void ClearBodyUnderLock()
        {
            var body = _body;
            _body = null;
            if (body is not null)
            {
                Array.Clear(body, 0, body.Length);
            }
        }
    }

    private static Uri BuildEndpoint(OpenAiCompatibleProviderOptions options)
    {
        var baseText = options.BaseUri.AbsoluteUri.TrimEnd('/') + "/";
        var baseUri = new Uri(baseText, UriKind.Absolute);
        var endpoint = new Uri(
            baseUri,
            options.ChatCompletionsPath.Substring(1));
        if (!string.Equals(
                endpoint.Scheme,
                baseUri.Scheme,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                endpoint.IdnHost,
                baseUri.IdnHost,
                StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != baseUri.Port
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !endpoint.AbsolutePath.StartsWith(
                baseUri.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ChatCompletionsPath must preserve the provider endpoint boundary.",
                nameof(options.ChatCompletionsPath));
        }

        return endpoint;
    }

    private static string ComputeRoutePolicyDigest(
        OpenAiCompatibleProviderOptions options,
        Uri endpoint,
        string promptTokenEstimatorId,
        string promptTokenEstimatorVersion)
    {
        var canonical = new StringBuilder();
        AddRoutePolicyField(canonical, "type", RoutePolicyVersion);
        AddRoutePolicyField(
            canonical,
            "origin",
            CanonicalOrigin(endpoint));
        AddRoutePolicyField(
            canonical,
            "basePath",
            options.BaseUri.AbsolutePath);
        AddRoutePolicyField(
            canonical,
            "requestPathAndQuery",
            endpoint.PathAndQuery);
        AddRoutePolicyField(
            canonical,
            "requestLayoutPolicy",
            RequestLayoutPolicy);
        AddRoutePolicyField(
            canonical,
            "usageParsingPolicy",
            UsageParsingPolicy);
        AddRoutePolicyField(
            canonical,
            "promptTokenEstimatorId",
            promptTokenEstimatorId);
        AddRoutePolicyField(
            canonical,
            "promptTokenEstimatorVersion",
            promptTokenEstimatorVersion);
        AddRoutePolicyField(
            canonical,
            "pricingPolicy",
            PricingPolicy);
        AddRoutePolicyField(
            canonical,
            "maxRequestBodyUtf8Bytes",
            MaxRequestBodyUtf8Bytes.ToString(
                CultureInfo.InvariantCulture));
        AddRoutePolicyField(
            canonical,
            "maxDirectTools",
            MaxDirectTools.ToString(CultureInfo.InvariantCulture));
        AddRoutePolicyField(
            canonical,
            "maxOutputTokens",
            options.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        AddRoutePolicyField(
            canonical,
            "maxOutputTokensField",
            options.MaxOutputTokensField);
        AddRoutePolicyField(
            canonical,
            "thinkingMode",
            options.ThinkingMode ?? string.Empty);
        AddRoutePolicyField(
            canonical,
            "reasoningEffort",
            options.ReasoningEffort ?? string.Empty);
        AddRoutePolicyField(
            canonical,
            "reasoningEffortRequiresThinkingMode",
            options.ReasoningEffortRequiresThinkingMode ? "true" : "false");
        AddRoutePolicyField(
            canonical,
            "toolChoice",
            options.ToolChoice ?? string.Empty);
        AddRoutePolicyField(
            canonical,
            "parallelToolCalls",
            options.ParallelToolCalls.HasValue
                ? options.ParallelToolCalls.Value ? "true" : "false"
                : "unspecified");
        AddRoutePolicyField(
            canonical,
            "strictToolSchemas",
            options.StrictToolSchemas ? "true" : "false");
        AddRoutePolicyField(
            canonical,
            "includeUsage",
            options.IncludeUsage ? "true" : "false");
        AddRoutePolicyField(
            canonical,
            "replayReasoningContent",
            options.ReplayReasoningContent ? "true" : "false");
        AddRoutePolicyField(
            canonical,
            "reasoningContentReplayRequiresThinkingMode",
            options.ReasoningContentReplayRequiresThinkingMode
                ? "true"
                : "false");
        AddRoutePolicyField(
            canonical,
            "maxSseEventCharacters",
            options.MaxSseEventCharacters.ToString(
                CultureInfo.InvariantCulture));
        AddRoutePolicyField(
            canonical,
            "maxSseLineCharacters",
            options.MaxSseLineCharacters.ToString(
                CultureInfo.InvariantCulture));
        AddRoutePolicyField(
            canonical,
            "cacheReadPrice",
            CanonicalPrice(
                options.InputCacheHitUsdPerMillionTokens));
        AddRoutePolicyField(
            canonical,
            "cacheMissPrice",
            CanonicalPrice(
                options.InputCacheMissUsdPerMillionTokens));
        AddRoutePolicyField(
            canonical,
            "cacheWritePrice",
            options.InputCacheWriteUsdPerMillionTokens is null
                ? "unavailable"
                : CanonicalPrice(
                    options.InputCacheWriteUsdPerMillionTokens));
        AddRoutePolicyField(
            canonical,
            "outputPrice",
            CanonicalPrice(options.OutputUsdPerMillionTokens));

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        var digest = sha.ComputeHash(bytes);
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static string ValidateEstimatorIdentity(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The estimator identity is required.",
                parameterName);
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "The estimator identity contains malformed Unicode.",
                parameterName);
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "The estimator identity exceeds its UTF-8 limit.",
                parameterName);
        }

        return value;
    }

    private static string CanonicalOrigin(Uri endpoint)
    {
        var host = endpoint.IdnHost.ToLowerInvariant();
        if (host.IndexOf(':') >= 0)
        {
            host = "[" + host + "]";
        }

        var defaultPort =
            string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
                ? 443
                : 80;
        return endpoint.Scheme.ToLowerInvariant()
               + "://"
               + host
               + (endpoint.Port == defaultPort
                   ? string.Empty
                   : ":" + endpoint.Port.ToString(
                       CultureInfo.InvariantCulture));
    }

    private static string CanonicalPrice(string value)
    {
        return decimal.Parse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture)
            .ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
    }

    private static void AddRoutePolicyField(
        StringBuilder output,
        string name,
        string value)
    {
        output.Append(Encoding.UTF8.GetByteCount(name));
        output.Append(':');
        output.Append(name);
        output.Append(Encoding.UTF8.GetByteCount(value));
        output.Append(':');
        output.Append(value);
    }

    private static ProviderException KnownZero(ProviderException exception)
    {
        if (exception.UsageKnownToBeZero)
        {
            return exception;
        }

        return new ProviderException(
            exception.Code,
            exception.Category,
            exception.Message,
            exception.Disposition,
            exception.RetryAfter,
            exception,
            usageKnownToBeZero: true);
    }

    private static StreamingModelRequest SnapshotRequest(
        StreamingModelRequest source,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceMessages = source.Messages;
            var sourceTools = source.Tools;
            if (sourceMessages is null || sourceTools is null)
            {
                throw RequestSnapshotInvalid();
            }

            var messageCount = sourceMessages.Count;
            var toolCount = sourceTools.Count;
            if (messageCount < 0
                || messageCount > ProviderRequestContentGuard.MaxMessages
                || toolCount < 0
                || toolCount > ProviderRequestContentGuard.MaxTools)
            {
                throw RequestSnapshotInvalid();
            }

            if (toolCount > MaxDirectTools)
            {
                throw new ProviderException(
                    "provider_tool_limit",
                    "validation",
                    "The provider accepts at most 128 direct tools.",
                    false,
                    usageKnownToBeZero: true);
            }

            var messages = new NormalizedMessage[messageCount];
            var totalParts = 0;
            for (var messageIndex = 0;
                 messageIndex < messageCount;
                 messageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceMessage = sourceMessages[messageIndex];
                if (sourceMessage is null)
                {
                    throw RequestSnapshotInvalid();
                }

                var sourceParts = sourceMessage.Parts;
                if (sourceParts is null)
                {
                    throw RequestSnapshotInvalid();
                }

                var partCount = sourceParts.Count;
                if (partCount < 0
                    || partCount
                    > ProviderRequestContentGuard.MaxParts - totalParts)
                {
                    throw RequestSnapshotInvalid();
                }

                totalParts += partCount;
                var parts = new List<NormalizedContentPart>(partCount);
                for (var partIndex = 0;
                     partIndex < partCount;
                     partIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePart = sourceParts[partIndex];
                    if (sourcePart is null)
                    {
                        throw RequestSnapshotInvalid();
                    }

                    parts.Add(new NormalizedContentPart
                    {
                        Type = sourcePart.Type,
                        Text = sourcePart.Text,
                        Json = sourcePart.Json,
                        ToolCallId = sourcePart.ToolCallId,
                        ToolName = sourcePart.ToolName,
                        ToolVersion = sourcePart.ToolVersion,
                        ToolEffect = sourcePart.ToolEffect,
                        ToolDescriptorDigest =
                            sourcePart.ToolDescriptorDigest
                    });
                }

                messages[messageIndex] = new NormalizedMessage
                {
                    MessageId = sourceMessage.MessageId,
                    Role = sourceMessage.Role,
                    CreatedAt = sourceMessage.CreatedAt,
                    Parts = parts
                };
            }

            var tools = new ToolDescriptor[toolCount];
            var totalToolCollectionItems = 0;
            for (var toolIndex = 0; toolIndex < toolCount; toolIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceTool = sourceTools[toolIndex];
                if (sourceTool is null)
                {
                    throw RequestSnapshotInvalid();
                }

                var sourceScopes = sourceTool.ConflictScopes;
                var sourceExtensions = sourceTool.Extensions;
                if (sourceScopes is null || sourceExtensions is null)
                {
                    throw RequestSnapshotInvalid();
                }

                var scopeCount = sourceScopes.Count;
                if (scopeCount < 0
                    || scopeCount > ProtocolLimits.MaxToolConflictScopes
                    || scopeCount
                    > ProviderRequestContentGuard.MaxJsonNodes
                    - totalToolCollectionItems)
                {
                    throw RequestSnapshotInvalid();
                }

                totalToolCollectionItems += scopeCount;
                var scopes = new List<string>(scopeCount);
                for (var scopeIndex = 0;
                     scopeIndex < scopeCount;
                     scopeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scope = sourceScopes[scopeIndex];
                    if (scope is null)
                    {
                        throw RequestSnapshotInvalid();
                    }

                    scopes.Add(scope);
                }

                var extensionCount = sourceExtensions.Count;
                if (extensionCount < 0
                    || extensionCount > 4_096
                    || extensionCount
                    > ProviderRequestContentGuard.MaxJsonNodes
                    - totalToolCollectionItems)
                {
                    throw RequestSnapshotInvalid();
                }

                totalToolCollectionItems += extensionCount;
                var extensions =
                    new Dictionary<string, JsonElement>(
                        extensionCount,
                        StringComparer.Ordinal);
                var capturedExtensions = 0;
                foreach (var extension in sourceExtensions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (extension.Key is null
                        || !extensions.TryAdd(
                            extension.Key,
                            extension.Value))
                    {
                        throw RequestSnapshotInvalid();
                    }

                    capturedExtensions++;
                    if (capturedExtensions > extensionCount)
                    {
                        throw RequestSnapshotInvalid();
                    }
                }

                if (capturedExtensions != extensionCount)
                {
                    throw RequestSnapshotInvalid();
                }

                tools[toolIndex] = new ToolDescriptor
                {
                    ProtocolVersion = sourceTool.ProtocolVersion,
                    SchemaVersion = sourceTool.SchemaVersion,
                    Extensions = extensions,
                    Name = sourceTool.Name,
                    Version = sourceTool.Version,
                    Description = sourceTool.Description,
                    ParametersSchema = sourceTool.ParametersSchema,
                    ResultSchema = sourceTool.ResultSchema,
                    Effect = sourceTool.Effect,
                    ConflictScopes = scopes,
                    ThreadAffinity = sourceTool.ThreadAffinity,
                    TimeoutMs = sourceTool.TimeoutMs,
                    RetryPolicy = sourceTool.RetryPolicy,
                    IdempotencyPolicy = sourceTool.IdempotencyPolicy,
                    Toolset = sourceTool.Toolset,
                    Visibility = sourceTool.Visibility
                };
            }

            var snapshot = new StreamingModelRequest
            {
                RunId = source.RunId,
                RunAttemptId = source.RunAttemptId,
                TurnId = source.TurnId,
                ProviderAttemptId = source.ProviderAttemptId,
                StreamAttemptId = source.StreamAttemptId,
                Messages = messages,
                Tools = tools,
                MaxOutputTokens = source.MaxOutputTokens,
                OpaqueContinuationState =
                    source.OpaqueContinuationState?.Snapshot()
            };

            ProviderRequestContentGuard.EnsureInputWithinLimits(
                snapshot.Messages,
                snapshot.Tools,
                cancellationToken);
            CloneCapturedJson(snapshot, cancellationToken);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception)
        {
            throw RequestSnapshotInvalid();
        }
    }

    private static void CloneCapturedJson(
        StreamingModelRequest snapshot,
        CancellationToken cancellationToken)
    {
        for (var messageIndex = 0;
             messageIndex < snapshot.Messages.Count;
             messageIndex++)
        {
            var parts = snapshot.Messages[messageIndex].Parts;
            for (var partIndex = 0;
                 partIndex < parts.Count;
                 partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = parts[partIndex];
                if (part.Json.HasValue)
                {
                    part.Json = part.Json.Value.Clone();
                }
            }
        }

        for (var toolIndex = 0;
             toolIndex < snapshot.Tools.Count;
             toolIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = snapshot.Tools[toolIndex];
            tool.ParametersSchema = tool.ParametersSchema.Clone();
            if (tool.ResultSchema.HasValue)
            {
                tool.ResultSchema = tool.ResultSchema.Value.Clone();
            }

            var extensionKeys = tool.Extensions.Keys.ToArray();
            for (var extensionIndex = 0;
                 extensionIndex < extensionKeys.Length;
                 extensionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = extensionKeys[extensionIndex];
                tool.Extensions[key] = tool.Extensions[key].Clone();
            }
        }
    }

    private static ProviderException RequestSnapshotInvalid()
    {
        return new ProviderException(
            "provider_request_input_limit",
            "validation",
            "The provider request exceeds the runtime input limit.",
            false,
            usageKnownToBeZero: true);
    }

    private void ValidateRequest(
        StreamingModelRequest request,
        CancellationToken cancellationToken)
    {
        ProviderRequestContentGuard.EnsureInputWithinLimits(
            request.Messages,
            request.Tools,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.StreamAttemptId))
        {
            throw new ProviderException(
                "provider_request_invalid",
                "validation",
                "The provider request is missing runtime identity.",
                false);
        }

        if (request.Messages.Count == 0)
        {
            throw new ProviderException(
                "provider_messages_empty",
                "validation",
                "At least one model message is required.",
                false);
        }

        if (request.Tools.Count > MaxDirectTools)
        {
            throw new ProviderException(
                "provider_tool_limit",
                "validation",
                "The provider accepts at most 128 direct tools.",
                false);
        }

        if (request.Tools.Count == 0
            && string.Equals(
                _options.ToolChoice,
                "required",
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_tool_choice_requires_tools",
                "validation",
                "Required tool choice needs at least one tool definition.",
                false,
                usageKnownToBeZero: true);
        }

        if (request.MaxOutputTokens < 1)
        {
            throw new ProviderException(
                "provider_output_limit_invalid",
                "validation",
                "The provider output-token limit is invalid.",
                false);
        }

        if (request.OpaqueContinuationState is not null)
        {
            throw new ProviderException(
                "provider_opaque_state_unsupported",
                "capability",
                "This provider route does not support opaque continuation state.",
                false,
                usageKnownToBeZero: true);
        }
    }

    private byte[] BuildRequestBody(StreamingModelRequest request)
    {
        using var buffer = new BoundedByteBufferWriter(
            MaxRequestBodyUtf8Bytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber(
                _options.MaxOutputTokensField,
                request.MaxOutputTokens.HasValue
                    ? Math.Min(
                        request.MaxOutputTokens.Value,
                        _options.MaxOutputTokens)
                    : _options.MaxOutputTokens);
            if (_options.ThinkingMode is not null)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", _options.ThinkingMode);
                writer.WriteEndObject();
            }

            if (_options.ReasoningEffort is not null
                && (!_options.ReasoningEffortRequiresThinkingMode
                    || string.Equals(
                        _options.ThinkingMode,
                        "enabled",
                        StringComparison.Ordinal)))
            {
                writer.WriteString(
                    "reasoning_effort",
                    _options.ReasoningEffort);
            }

            if (_options.IncludeUsage)
            {
                writer.WritePropertyName("stream_options");
                writer.WriteStartObject();
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                WriteMessage(writer, message);
            }

            writer.WriteEndArray();
            if (request.Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Tools)
                {
                    WriteTool(
                        writer,
                        tool,
                        _options.StrictToolSchemas);
                }

                writer.WriteEndArray();
                if (_options.ToolChoice is not null)
                {
                    writer.WriteString(
                        "tool_choice",
                        _options.ToolChoice);
                }

                if (_options.ParallelToolCalls.HasValue)
                {
                    writer.WriteBoolean(
                        "parallel_tool_calls",
                        _options.ParallelToolCalls.Value);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed class BoundedByteBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int Utf8JsonWriterSlackBytes = 4_096;

        private readonly int _maximumBytes;
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        public BoundedByteBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(
                Math.Min(
                    4_096,
                    maximumBytes + Utf8JsonWriterSlackBytes));
        }

        public ReadOnlySpan<byte> WrittenSpan =>
            _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            ThrowIfDisposed();
            if (count < 0 || count > _maximumBytes - _written)
            {
                throw RequestBodyLimit();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            var available = Math.Min(
                _buffer.Length - _written,
                checked(
                    _maximumBytes - _written
                    + Utf8JsonWriterSlackBytes));
            return _buffer.AsMemory(_written, available);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return GetMemory(sizeHint).Span;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var buffer = _buffer;
            _buffer = Array.Empty<byte>();
            _written = 0;
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ThrowIfDisposed();
            var maximumCapacity = checked(
                _maximumBytes + Utf8JsonWriterSlackBytes);
            var requested = sizeHint == 0 ? 256 : sizeHint;
            if (requested < 0
                || requested > maximumCapacity - _written)
            {
                throw RequestBodyLimit();
            }

            if (requested <= _buffer.Length - _written)
            {
                return;
            }

            var required = checked(_written + requested);
            var doubled = Math.Min(
                maximumCapacity,
                Math.Max(_buffer.Length, 256) * 2L);
            var target = (int)Math.Min(
                maximumCapacity,
                Math.Max(required, doubled));
            var replacement = ArrayPool<byte>.Shared.Rent(target);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            var old = _buffer;
            _buffer = replacement;
            Array.Clear(old, 0, old.Length);
            ArrayPool<byte>.Shared.Return(old);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedByteBufferWriter));
            }
        }

        internal static ProviderException RequestBodyLimit()
        {
            return new ProviderException(
                "provider_request_body_limit",
                "validation",
                "The encoded provider request exceeds the transport limit.",
                false,
                usageKnownToBeZero: true);
        }
    }

    private void WriteMessage(Utf8JsonWriter writer, NormalizedMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);
        switch (message.Role)
        {
            case NormalizedRoles.System:
            case NormalizedRoles.User:
                WriteBoundedString(
                    writer,
                    "content",
                    FlattenContent(message.Parts));
                break;
            case NormalizedRoles.Assistant:
                WriteAssistantMessage(writer, message.Parts);
                break;
            case NormalizedRoles.Tool:
                WriteToolResultMessage(writer, message.Parts);
                break;
            default:
                throw new ProviderException(
                    "provider_role_unsupported",
                    "validation",
                    "A normalized message has an unsupported role.",
                    false);
        }

        writer.WriteEndObject();
    }

    private void WriteAssistantMessage(
        Utf8JsonWriter writer,
        IReadOnlyList<NormalizedContentPart> parts)
    {
        var text = string.Join(
            string.Empty,
            parts.Where(item => item.Type == NormalizedPartTypes.Text)
                .Select(item => item.Text));
        var reasoning = string.Join(
            string.Empty,
            parts.Where(item => item.Type == NormalizedPartTypes.Reasoning)
                .Select(item => item.Text));
        var calls = parts
            .Where(item => item.Type == NormalizedPartTypes.ToolCall)
            .ToArray();

        WriteBoundedString(writer, "content", text);
        if (_options.ReplayReasoningContent
            && !string.IsNullOrEmpty(reasoning)
            && (!_options.ReasoningContentReplayRequiresThinkingMode
                || string.Equals(
                    _options.ThinkingMode,
                    "enabled",
                    StringComparison.Ordinal)))
        {
            WriteBoundedString(
                writer,
                "reasoning_content",
                reasoning);
        }

        if (calls.Length > 0)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in calls)
            {
                if (string.IsNullOrWhiteSpace(call.ToolCallId)
                    || string.IsNullOrWhiteSpace(call.ToolName)
                    || call.Json is null)
                {
                    throw new ProviderException(
                        "provider_tool_history_invalid",
                        "validation",
                        "An assistant tool-call message is incomplete.",
                        false);
                }

                writer.WriteStartObject();
                WriteBoundedString(writer, "id", call.ToolCallId);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                WriteBoundedString(writer, "name", call.ToolName);
                WriteBoundedString(
                    writer,
                    "arguments",
                    call.Json.Value.GetRawText());
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }

    private static void WriteToolResultMessage(
        Utf8JsonWriter writer,
        IReadOnlyList<NormalizedContentPart> parts)
    {
        var results = parts
            .Where(item => item.Type == NormalizedPartTypes.ToolResult)
            .ToArray();
        if (results.Length != 1
            || string.IsNullOrWhiteSpace(results[0].ToolCallId)
            || results[0].Json is null)
        {
            throw new ProviderException(
                "provider_tool_result_history_invalid",
                "validation",
                "Each tool message must contain exactly one complete result.",
                false);
        }

        var result = results[0];
        WriteBoundedString(
            writer,
            "tool_call_id",
            result.ToolCallId);
        WriteBoundedString(
            writer,
            "content",
            result.Json!.Value.GetRawText());
    }

    private static string FlattenContent(
        IReadOnlyList<NormalizedContentPart> parts)
    {
        var values = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Type == NormalizedPartTypes.Text && part.Text is not null)
            {
                values.Add(part.Text);
            }
            else if (part.Type == NormalizedPartTypes.Json && part.Json is not null)
            {
                values.Add(part.Json.Value.GetRawText());
            }
            else
            {
                throw new ProviderException(
                    "provider_content_unsupported",
                    "validation",
                    "The provider cannot encode a normalized content part.",
                    false);
            }
        }

        return string.Join("\n", values);
    }

    private static void WriteTool(
        Utf8JsonWriter writer,
        ToolDescriptor tool,
        bool strictSchema)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)
            || tool.Name.Length > 64
            || !tool.Name.All(
                character =>
                    IsAsciiLetterOrDigit(character)
                    || character == '_'
                    || character == '-')
            || tool.ParametersSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderException(
                "provider_tool_invalid",
                "validation",
                "A direct tool has an invalid name or parameter schema.",
                false);
        }

        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WritePropertyName("function");
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        WriteBoundedString(writer, "description", tool.Description);
        writer.WritePropertyName("parameters");
        WriteBoundedJsonValue(writer, tool.ParametersSchema);
        if (strictSchema)
        {
            writer.WriteBoolean("strict", true);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteBoundedString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var encodedLength = MeasureJsonStringUtf8Bytes(value);
        byte[]? rented = null;
        Span<byte> encoded = encodedLength <= 512
            ? stackalloc byte[encodedLength]
            : (rented = ArrayPool<byte>.Shared.Rent(encodedLength));
        try
        {
            EncodeJsonString(value, encoded[..encodedLength]);
            writer.WriteRawValue(
                encoded[..encodedLength],
                skipInputValidation: true);
        }
        finally
        {
            if (rented is not null)
            {
                Array.Clear(rented, 0, rented.Length);
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static int MeasureJsonStringUtf8Bytes(string value)
    {
        var source = value.AsSpan();
        Span<char> encoded = stackalloc char[512];
        var bytes = 2L;
        while (!source.IsEmpty)
        {
            var status = JavaScriptEncoder.Default.Encode(
                source,
                encoded,
                out var consumed,
                out var written,
                isFinalBlock: true);
            if (status is not OperationStatus.Done
                and not OperationStatus.DestinationTooSmall
                || consumed == 0 && written == 0)
            {
                throw new InvalidDataException(
                    "The provider request contains invalid text.");
            }

            bytes = checked(
                bytes + StrictUtf8.GetByteCount(encoded[..written]));
            if (bytes > MaxRequestBodyUtf8Bytes)
            {
                throw BoundedByteBufferWriter.RequestBodyLimit();
            }

            source = source[consumed..];
        }

        return (int)bytes;
    }

    private static void EncodeJsonString(
        string value,
        Span<byte> destination)
    {
        destination[0] = (byte)'"';
        var offset = 1;
        var source = value.AsSpan();
        Span<char> encoded = stackalloc char[512];
        while (!source.IsEmpty)
        {
            var status = JavaScriptEncoder.Default.Encode(
                source,
                encoded,
                out var consumed,
                out var written,
                isFinalBlock: true);
            if (status is not OperationStatus.Done
                and not OperationStatus.DestinationTooSmall
                || consumed == 0 && written == 0)
            {
                throw new InvalidDataException(
                    "The provider request contains invalid text.");
            }

            offset += StrictUtf8.GetBytes(
                encoded[..written],
                destination[offset..]);
            source = source[consumed..];
        }

        destination[offset++] = (byte)'"';
        if (offset != destination.Length)
        {
            throw new InvalidDataException(
                "The provider request text length changed while encoding.");
        }
    }

    private static void WriteBoundedJsonValue(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        var raw = value.GetRawText();
        var length = StrictUtf8.GetByteCount(raw);
        if (length > MaxRequestBodyUtf8Bytes)
        {
            throw BoundedByteBufferWriter.RequestBodyLimit();
        }

        byte[]? rented = null;
        Span<byte> encoded = length <= 512
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var written = StrictUtf8.GetBytes(raw.AsSpan(), encoded);
            writer.WriteRawValue(
                encoded[..written],
                skipInputValidation: true);
        }
        finally
        {
            if (rented is not null)
            {
                Array.Clear(rented, 0, rented.Length);
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static bool IsAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }

    private static ProviderException MapHttpError(
        int statusCode,
        string? retryAfterHeader)
    {
        var retryAfter = ParseRetryAfter(retryAfterHeader);
        return statusCode switch
        {
            400 or 422 => new ProviderException(
                "provider_invalid_request",
                "validation",
                "The provider rejected the request format.",
                false,
                usageKnownToBeZero: true),
            401 or 403 => new ProviderException(
                "provider_auth_failed",
                "auth",
                "The provider rejected its credential.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            402 => new ProviderException(
                "provider_balance_exhausted",
                "auth",
                "The provider account cannot fund this request.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            404 or 405 or 410 => new ProviderException(
                "provider_route_unavailable",
                "routing",
                "The configured provider route is unavailable.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            408 => new ProviderException(
                "provider_request_timeout",
                "network",
                "The provider timed out after accepting the request.",
                true,
                retryAfter),
            425 or 429 => new ProviderException(
                "provider_throttled",
                "rate_limit",
                "The provider temporarily refused the request.",
                true,
                retryAfter,
                usageKnownToBeZero: true),
            >= 500 and <= 599 => new ProviderException(
                "provider_unavailable",
                "overload",
                "The provider is temporarily unavailable.",
                true,
                retryAfter),
            >= 300 and <= 399 => new ProviderException(
                "provider_redirect_rejected",
                "network",
                "The provider attempted an unsafe redirect.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            _ => new ProviderException(
                "provider_http_error",
                "provider",
                "The provider returned an unsupported HTTP status.",
                false)
        };
    }

    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 300));
        }

        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay > TimeSpan.FromMinutes(5)
                    ? TimeSpan.FromMinutes(5)
                    : delay;
            }
        }

        return null;
    }

    private sealed class BoundedTextLineReader
    {
        private readonly TextReader _reader;
        private readonly int _maximumCharacters;
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _count;
        private bool _skipLeadingLineFeed;

        public BoundedTextLineReader(TextReader reader, int maximumCharacters)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            if (maximumCharacters < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
            }

            _maximumCharacters = maximumCharacters;
        }

        public async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            var line = new StringBuilder(Math.Min(_maximumCharacters, 256));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset >= _count)
                {
                    _count = await _reader
                        .ReadAsync(_buffer, 0, _buffer.Length)
                        .ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        return line.Length == 0 ? null : line.ToString();
                    }
                }

                var character = _buffer[_offset++];
                if (_skipLeadingLineFeed)
                {
                    _skipLeadingLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character == '\r')
                {
                    _skipLeadingLineFeed = true;
                    return line.ToString();
                }

                if (character == '\n')
                {
                    return line.ToString();
                }

                if (line.Length >= _maximumCharacters)
                {
                    throw new ProviderException(
                        "provider_sse_line_too_large",
                        "provider",
                        "The provider emitted an oversized SSE line.",
                        false);
                }

                line.Append(character);
            }
        }
    }

    private sealed class SseChunkParser
    {
        private readonly string _streamAttemptId;
        private readonly int _maxCharacters;
        private readonly decimal _cacheHitPrice;
        private readonly decimal _cacheMissPrice;
        private readonly decimal? _cacheWritePrice;
        private readonly decimal _outputPrice;
        private readonly Dictionary<int, string> _toolCallIds = new();
        private long _ordinal;

        public SseChunkParser(
            string streamAttemptId,
            int maxCharacters,
            string cacheHitPrice,
            string cacheMissPrice,
            string? cacheWritePrice,
            string outputPrice)
        {
            _streamAttemptId = streamAttemptId;
            _maxCharacters = maxCharacters;
            _cacheHitPrice = ParsePrice(cacheHitPrice);
            _cacheMissPrice = ParsePrice(cacheMissPrice);
            _cacheWritePrice = cacheWritePrice is null
                ? null
                : ParsePrice(cacheWritePrice);
            _outputPrice = ParsePrice(outputPrice);
        }

        public IReadOnlyList<ModelStreamEvent> Parse(string payload)
        {
            if (payload.Length > _maxCharacters)
            {
                throw new ProviderException(
                    "provider_chunk_too_large",
                    "provider",
                    "The provider emitted an oversized JSON chunk.",
                    false);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    payload,
                    new JsonDocumentOptions
                    {
                        MaxDepth = 64,
                        CommentHandling = JsonCommentHandling.Disallow,
                        AllowTrailingCommas = false
                    });
            }
            catch (JsonException exception)
            {
                throw new ProviderException(
                    "provider_chunk_invalid_json",
                    "provider",
                    "The provider emitted invalid stream JSON.",
                    true,
                    innerException: exception);
            }

            using (document)
            {
                var events = new List<ModelStreamEvent>();
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw ProtocolError("The provider chunk must be an object.");
                }

                if (root.TryGetProperty("choices", out var choices))
                {
                    if (choices.ValueKind != JsonValueKind.Array)
                    {
                        throw ProtocolError("The provider choices field is invalid.");
                    }

                    if (choices.GetArrayLength() > 1)
                    {
                        throw ProtocolError(
                            "The runtime accepts exactly one provider choice.");
                    }

                    if (choices.GetArrayLength() == 1)
                    {
                        ParseChoice(choices[0], events);
                    }
                }

                if (root.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    events.Add(
                        Event(
                            ModelStreamEventKinds.Usage,
                            usage: ParseUsage(usage)));
                }

                return events;
            }
        }

        private void ParseChoice(
            JsonElement choice,
            ICollection<ModelStreamEvent> events)
        {
            if (choice.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError("A provider choice must be an object.");
            }

            if (choice.TryGetProperty("index", out var choiceIndex)
                && (choiceIndex.ValueKind != JsonValueKind.Number
                    || !choiceIndex.TryGetInt32(out var index)
                    || index != 0))
            {
                throw ProtocolError("The provider choice index is invalid.");
            }

            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object)
            {
                if (TryReadNullableString(delta, "reasoning_content", out var reasoning)
                    && reasoning is not null)
                {
                    events.Add(
                        Event(
                            ModelStreamEventKinds.ReasoningDelta,
                            reasoningDelta: reasoning));
                }

                if (TryReadNullableString(delta, "content", out var content)
                    && content is not null)
                {
                    events.Add(
                        Event(
                            ModelStreamEventKinds.TextDelta,
                            textDelta: content));
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls))
                {
                    ParseToolCalls(toolCalls, events);
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finish))
            {
                if (finish.ValueKind == JsonValueKind.String)
                {
                    events.Add(
                        Event(
                            ModelStreamEventKinds.Completed,
                            finishReason: finish.GetString()));
                }
                else if (finish.ValueKind != JsonValueKind.Null)
                {
                    throw ProtocolError(
                        "The provider finish reason must be a string or null.");
                }
            }
        }

        private void ParseToolCalls(
            JsonElement toolCalls,
            ICollection<ModelStreamEvent> events)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolError("The provider tool_calls field is invalid.");
            }

            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object
                    || !call.TryGetProperty("index", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)
                    || index < 0)
                {
                    throw ProtocolError(
                        "A provider tool-call fragment has no valid index.");
                }

                string? id = null;
                if (TryReadNullableString(call, "id", out var incomingId)
                    && !string.IsNullOrWhiteSpace(incomingId))
                {
                    if (_toolCallIds.TryGetValue(index, out var existing)
                        && !string.Equals(
                            existing,
                            incomingId,
                            StringComparison.Ordinal))
                    {
                        throw ProtocolError(
                            "A provider changed a streamed tool-call id.");
                    }

                    id = incomingId;
                    _toolCallIds[index] = id;
                }
                else
                {
                    _toolCallIds.TryGetValue(index, out id);
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    throw ProtocolError(
                        "A provider tool-call fragment arrived before its id.");
                }

                string? name = null;
                string? arguments = null;
                if (call.TryGetProperty("function", out var function))
                {
                    if (function.ValueKind != JsonValueKind.Object)
                    {
                        throw ProtocolError(
                            "A provider tool-call function is invalid.");
                    }

                    TryReadNullableString(function, "name", out name);
                    TryReadNullableString(function, "arguments", out arguments);
                }

                events.Add(
                    Event(
                        ModelStreamEventKinds.ToolCallDelta,
                        toolCallId: id,
                        toolNameDelta: name,
                        argumentsJsonDelta: arguments));
            }
        }

        private ModelStreamEvent Event(
            string kind,
            string? textDelta = null,
            string? reasoningDelta = null,
            string? toolCallId = null,
            string? toolNameDelta = null,
            string? argumentsJsonDelta = null,
            ProviderUsage? usage = null,
            string? finishReason = null)
        {
            return new ModelStreamEvent
            {
                StreamAttemptId = _streamAttemptId,
                Ordinal = _ordinal++,
                Kind = kind,
                TextDelta = textDelta,
                ReasoningDelta = reasoningDelta,
                ToolCallId = toolCallId,
                ToolNameDelta = toolNameDelta,
                ArgumentsJsonDelta = argumentsJsonDelta,
                Usage = usage,
                FinishReason = finishReason
            };
        }

        private ProviderUsage ParseUsage(JsonElement usage)
        {
            var input = ReadNonNegativeInt(
                usage,
                "prompt_tokens",
                required: true);
            var output = ReadNonNegativeInt(
                usage,
                "completion_tokens",
                required: true);
            var cacheRead = ReadOptionalNonNegativeInt(
                usage,
                "prompt_cache_hit_tokens",
                out var cacheReadPresent);
            var cacheMiss = ReadOptionalNonNegativeInt(
                usage,
                "prompt_cache_miss_tokens",
                out var cacheMissPresent);
            if (!cacheReadPresent
                && !cacheMissPresent
                && TryReadNestedNonNegativeInt(
                    usage,
                    "prompt_tokens_details",
                    "cached_tokens",
                    out var nestedCacheRead))
            {
                if (nestedCacheRead > input)
                {
                    throw ProtocolError(
                        "The provider usage cached-token count exceeds prompt usage.");
                }

                cacheRead = nestedCacheRead;
                cacheMiss = input - nestedCacheRead;
                cacheReadPresent = true;
                cacheMissPresent = true;
            }

            if (cacheReadPresent
                && cacheMissPresent
                && (long)cacheRead!.Value + cacheMiss!.Value != input)
            {
                throw ProtocolError(
                    "The provider usage cache-token counts are inconsistent.");
            }

            var cacheWrite = ReadOptionalNonNegativeInt(
                usage,
                "prompt_cache_write_tokens",
                out var cacheWritePresent);
            var reasoning = TryReadNestedNonNegativeInt(
                usage,
                "completion_tokens_details",
                "reasoning_tokens",
                out var reasoningTokens)
                ? reasoningTokens
                : (int?)null;
            var providerTotal = ReadOptionalNonNegativeInt(
                usage,
                "total_tokens",
                out _);

            var cacheCostAvailable =
                cacheReadPresent && cacheMissPresent
                || _cacheHitPrice == _cacheMissPrice;
            var cacheWriteCostAvailable =
                _cacheWritePrice.HasValue
                    ? cacheWritePresent
                    : !cacheWritePresent || cacheWrite == 0;
            var costAvailable =
                cacheCostAvailable && cacheWriteCostAvailable;
            var cost = 0m;
            if (costAvailable)
            {
                var cacheInputCost =
                    cacheReadPresent && cacheMissPresent
                    ? cacheRead!.Value * _cacheHitPrice
                      + cacheMiss!.Value * _cacheMissPrice
                    : input * _cacheMissPrice;
                var cacheWriteCost =
                    (cacheWrite ?? 0)
                    * (_cacheWritePrice ?? 0m);
                cost = (
                    cacheInputCost
                    + cacheWriteCost
                    + output * _outputPrice) / 1_000_000m;
            }

            return new ProviderUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens =
                    cacheReadPresent ? cacheRead : null,
                CacheWriteTokens =
                    cacheWritePresent ? cacheWrite : null,
                CacheMissTokens =
                    cacheMissPresent ? cacheMiss : null,
                ReasoningTokens = reasoning,
                ProviderTotalTokens = providerTotal,
                Availability = costAvailable
                    ? UsageAvailabilityStates.CostAvailable
                    : UsageAvailabilityStates.CostUnavailable,
                CostUsd = cost.ToString(
                    "0.############################",
                    CultureInfo.InvariantCulture)
            };
        }

        private static decimal ParsePrice(string value)
        {
            return decimal.Parse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int ReadNonNegativeInt(
            JsonElement value,
            string propertyName,
            bool required)
        {
            if (!value.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                if (required)
                {
                    throw ProtocolError(
                        "The provider usage object is missing a token count.");
                }

                return 0;
            }

            if (property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var result)
                || result < 0)
            {
                throw ProtocolError(
                    "The provider usage object contains an invalid token count.");
            }

            return result;
        }

        private static int? ReadOptionalNonNegativeInt(
            JsonElement value,
            string propertyName,
            out bool present)
        {
            present = value.TryGetProperty(
                propertyName,
                out var property)
                      && property.ValueKind != JsonValueKind.Null;
            return present
                ? ReadNonNegativeInt(
                    value,
                    propertyName,
                    required: true)
                : null;
        }

        private static bool TryReadNestedNonNegativeInt(
            JsonElement value,
            string objectPropertyName,
            string valuePropertyName,
            out int result)
        {
            result = 0;
            if (!value.TryGetProperty(
                    objectPropertyName,
                    out var nested)
                || nested.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            if (nested.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError(
                    "The provider usage detail object is invalid.");
            }

            if (!nested.TryGetProperty(valuePropertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            if (property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out result)
                || result < 0)
            {
                throw ProtocolError(
                    "The provider usage object contains an invalid token count.");
            }

            return true;
        }

        private static bool TryReadNullableString(
            JsonElement value,
            string propertyName,
            out string? result)
        {
            result = null;
            if (!value.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError(
                    "A provider stream string field has an invalid type.");
            }

            result = property.GetString();
            return true;
        }

        private static ProviderException ProtocolError(string message)
        {
            return new ProviderException(
                "provider_protocol_invalid",
                "provider",
                message,
                true);
        }
    }
}
