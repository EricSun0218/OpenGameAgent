using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using GameAgent.Core;

namespace GameAgent.Providers.Anthropic;

public sealed class AnthropicMessagesStreamingProvider :
    IStreamingModelProvider,
    IPreparedStreamingModelProvider,
    IProviderRouteMetadataSource
{
    private const string WireContentType =
        "application/json; charset=utf-8";
    private const string RoutePolicyVersion =
        "anthropic-messages.route-policy.v1";

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly AnthropicProviderOptions _options;
    private readonly IAnthropicApiKeySource _credentials;
    private readonly IAnthropicStreamingHttpTransport _transport;
    private readonly ProviderCapabilities _capabilities;
    private readonly ProviderRouteMetadata _routeMetadata;

    public AnthropicMessagesStreamingProvider(
        AnthropicProviderOptions options,
        IAnthropicApiKeySource credentials,
        IAnthropicStreamingHttpTransport transport)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _credentials =
            credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transport =
            transport ?? throw new ArgumentNullException(nameof(transport));
        _capabilities = new ProviderCapabilities
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = false,
            ReasoningInput = false,
            ParallelToolCalls = true,
            RequiresCompleteToolPairs = true,
            MaxTools = AnthropicWireRequest.MaxTools,
            MaxToolSchemaUtf8Bytes =
                AnthropicWireRequest.MaxToolSchemaUtf8Bytes,
            MaxContextTokens = _options.MaxContextTokens
        };
        var dialect = new ProviderDialectContract(
            "anthropic.messages.sse.2023-06-01.v1",
            ProviderRequestFamily.Custom,
            "anthropic.messages.request.2023-06-01.v1",
            ProviderStreamFraming.ServerSentEvents,
            "sse.named-event-json.2023-06-01.v1",
            "anthropic.tool-use-result.v1",
            "anthropic.cumulative-usage-cache.v1",
            "unsupported.v1",
            WireContentType);
        _routeMetadata = new ProviderRouteMetadata(
            _options.Model,
            dialect,
            RoutePolicyVersion,
            ComputeRoutePolicyDigest(_options));
    }

    public string ProviderId => _options.ProviderId;

    public ProviderRouteMetadata RouteMetadata => _routeMetadata;

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

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var route = new ProviderRouteIdentity(
            ProviderId,
            _routeMetadata,
            _capabilities);
        var prepared = await PrepareStreamAsync(
                new ProviderStreamPreparationContext(
                    ProviderId,
                    route,
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
        ValidateRouteIdentity(context);

        AnthropicWireRequest wireRequest;
        byte[] body;
        try
        {
            wireRequest = AnthropicWireRequest.Create(
                context.Request,
                _options,
                cancellationToken);
            body = wireRequest.Encode(_options);
        }
        catch (ProviderException exception)
        {
            throw KnownZero(exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new ProviderException(
                "provider_request_encoding_failed",
                "validation",
                "The Anthropic request could not be encoded.",
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
                new AnthropicPreparedProviderStream(
                    this,
                    wireRequest.StreamAttemptId,
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
        string apiKey;
        try
        {
            apiKey = await _credentials
                .GetApiKeyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The Anthropic credential is unavailable.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The Anthropic credential is unavailable.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        try
        {
            apiKey = AnthropicApiKeyValidator.ValidateAndTrim(
                apiKey,
                nameof(apiKey));
        }
        catch (ArgumentException exception)
        {
            apiKey = string.Empty;
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The Anthropic credential is unavailable.",
                ProviderFailureDisposition.Failover,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        IAnthropicStreamingHttpResponse response;
        try
        {
            response = await _transport.SendAsync(
                    new AnthropicStreamingHttpRequest
                    {
                        Uri = _options.Endpoint,
                        ApiKey = apiKey,
                        ApiVersion = _options.ApiVersion,
                        Body = body,
                        ContentType = WireContentType
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException(
                "provider_connect_failed",
                "network",
                "The Anthropic connection failed.",
                true,
                innerException: exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                "provider_connect_failed",
                "network",
                "The Anthropic connection failed.",
                true,
                innerException: exception);
        }
        finally
        {
            apiKey = string.Empty;
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
            using var reader = new StreamReader(
                response.Content,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4_096,
                leaveOpen: false);
            var lineReader = new BoundedLineReader(
                reader,
                _options.MaxSseLineCharacters,
                _options.MaxStreamCharacters);
            var parser = new AnthropicSseParser(
                streamAttemptId,
                _options);
            var data = new StringBuilder();
            string? eventName = null;
            var eventNameSeen = false;
            var eventCount = 0;

            while (!parser.IsComplete)
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
                        "The Anthropic stream could not be read.",
                        true,
                        innerException: exception);
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length == 0 && !eventNameSeen)
                    {
                        continue;
                    }

                    if (!eventNameSeen || string.IsNullOrEmpty(eventName))
                    {
                        throw ProtocolError(
                            "The Anthropic SSE event has no event name.");
                    }

                    if (data.Length == 0)
                    {
                        throw ProtocolError(
                            "The Anthropic SSE event has no data.");
                    }

                    eventCount++;
                    if (eventCount > _options.MaxSseEvents)
                    {
                        throw ProtocolLimit(
                            "provider_sse_event_limit",
                            "The Anthropic SSE stream emitted too many events.");
                    }

                    var payload = data.ToString();
                    data.Clear();
                    var dispatchedName = eventName;
                    eventName = null;
                    eventNameSeen = false;
                    foreach (var item in parser.Parse(
                                 dispatchedName,
                                 payload))
                    {
                        yield return item;
                    }

                    continue;
                }

                if (line[0] == ':')
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                var field = separator < 0
                    ? line
                    : line.Substring(0, separator);
                var value = separator < 0
                    ? string.Empty
                    : line.Substring(separator + 1);
                if (value.StartsWith(" ", StringComparison.Ordinal))
                {
                    value = value.Substring(1);
                }

                if (string.Equals(field, "event", StringComparison.Ordinal))
                {
                    if (eventNameSeen)
                    {
                        throw ProtocolError(
                            "The Anthropic SSE event repeated its event field.");
                    }

                    eventNameSeen = true;
                    eventName = value;
                }
                else if (string.Equals(field, "data", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    if (data.Length > _options.MaxSseEventCharacters)
                    {
                        throw ProtocolLimit(
                            "provider_sse_event_too_large",
                            "The Anthropic SSE event exceeded its limit.");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length > 0 || eventNameSeen)
            {
                throw new ProviderException(
                    "provider_sse_truncated_event",
                    "network",
                    "The Anthropic stream ended during an SSE event.",
                    true);
            }

            if (!parser.IsComplete)
            {
                throw new ProviderException(
                    "provider_sse_message_stop_missing",
                    "network",
                    "The Anthropic stream ended before message_stop.",
                    true);
            }
        }
    }

    private void ValidateRouteIdentity(
        ProviderStreamPreparationContext context)
    {
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
                _routeMetadata.DialectContract.SemanticDigest,
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_route_identity_mismatch",
                "provider",
                "The provider route identity does not match this Anthropic adapter.",
                false,
                usageKnownToBeZero: true);
        }
    }

    private static ProviderException MapHttpError(
        int statusCode,
        string? retryAfterHeader)
    {
        var retryAfter = ParseRetryAfter(retryAfterHeader);
        return statusCode switch
        {
            400 => new ProviderException(
                "provider_invalid_request",
                "validation",
                "Anthropic rejected the request format.",
                false,
                usageKnownToBeZero: true),
            401 or 403 => new ProviderException(
                "provider_auth_failed",
                "auth",
                "Anthropic rejected the credential or its permissions.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            402 => new ProviderException(
                "provider_balance_exhausted",
                "auth",
                "The Anthropic account cannot fund this request.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            404 => new ProviderException(
                "provider_route_unavailable",
                "routing",
                "The configured Anthropic route is unavailable.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            409 => new ProviderException(
                "provider_conflict",
                "provider",
                "Anthropic reported a transient request conflict.",
                true,
                retryAfter,
                usageKnownToBeZero: true),
            413 => new ProviderException(
                "provider_request_too_large",
                "validation",
                "Anthropic rejected the request size.",
                false,
                usageKnownToBeZero: true),
            429 => new ProviderException(
                "provider_throttled",
                "rate_limit",
                "Anthropic temporarily rate-limited the request.",
                true,
                retryAfter,
                usageKnownToBeZero: true),
            504 => new ProviderException(
                "provider_request_timeout",
                "network",
                "Anthropic timed out while processing the request.",
                true,
                retryAfter),
            500 or 529 => new ProviderException(
                "provider_unavailable",
                "overload",
                "Anthropic is temporarily unavailable.",
                true,
                retryAfter),
            >= 500 and <= 599 => new ProviderException(
                "provider_unavailable",
                "overload",
                "Anthropic is temporarily unavailable.",
                true,
                retryAfter),
            >= 300 and <= 399 => new ProviderException(
                "provider_redirect_rejected",
                "network",
                "Anthropic attempted an unsafe redirect.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            _ => new ProviderException(
                "provider_http_error",
                "provider",
                "Anthropic returned an unsupported HTTP status.",
                false)
        };
    }

    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (value is not null
            && value.Length <= 64
            && int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 300));
        }

        if (value is not null
            && value.Length <= 128
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
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

    private static string ComputeRoutePolicyDigest(
        AnthropicProviderOptions options)
    {
        var canonical = new StringBuilder();
        AddPolicy(canonical, "type", RoutePolicyVersion);
        AddPolicy(canonical, "endpoint", options.Endpoint.AbsoluteUri);
        AddPolicy(canonical, "apiVersion", options.ApiVersion);
        AddPolicy(canonical, "model", options.Model);
        AddPolicy(
            canonical,
            "maxOutputTokens",
            options.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxContextTokens",
            options.MaxContextTokens.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxSseLineCharacters",
            options.MaxSseLineCharacters.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxSseEventCharacters",
            options.MaxSseEventCharacters.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxStreamCharacters",
            options.MaxStreamCharacters.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxSseEvents",
            options.MaxSseEvents.ToString(CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "maxToolArgumentsUtf8Bytes",
            options.MaxToolArgumentsUtf8Bytes.ToString(
                CultureInfo.InvariantCulture));
        AddPolicy(
            canonical,
            "inputPrice",
            CanonicalPrice(options.InputUsdPerMillionTokens));
        AddPolicy(
            canonical,
            "cacheReadPrice",
            CanonicalPrice(options.CacheReadUsdPerMillionTokens));
        AddPolicy(
            canonical,
            "cacheWrite5mPrice",
            CanonicalPrice(options.CacheWrite5mUsdPerMillionTokens));
        AddPolicy(
            canonical,
            "cacheWrite1hPrice",
            CanonicalPrice(options.CacheWrite1hUsdPerMillionTokens));
        AddPolicy(
            canonical,
            "outputPrice",
            CanonicalPrice(options.OutputUsdPerMillionTokens));

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        try
        {
            var digest = sha.ComputeHash(bytes);
            var result = new StringBuilder(digest.Length * 2);
            foreach (var item in digest)
            {
                result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            }

            return result.ToString();
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static void AddPolicy(
        StringBuilder builder,
        string name,
        string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(name));
        builder.Append(':');
        builder.Append(name);
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }

    private static string CanonicalPrice(string? value)
    {
        return value is null
            ? "unavailable"
            : decimal.Parse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture)
                .ToString(
                    "0.############################",
                    CultureInfo.InvariantCulture);
    }

    private static ProviderException KnownZero(ProviderException exception)
    {
        return exception.UsageKnownToBeZero
            ? exception
            : new ProviderException(
                exception.Code,
                exception.Category,
                exception.Message,
                exception.Disposition,
                exception.RetryAfter,
                exception,
                usageKnownToBeZero: true);
    }

    internal static ProviderException ProtocolError(string message)
    {
        return new ProviderException(
            "provider_protocol_invalid",
            "provider",
            message,
            true);
    }

    internal static ProviderException ProtocolLimit(
        string code,
        string message)
    {
        return new ProviderException(
            code,
            "provider",
            message,
            false);
    }

    private sealed class AnthropicPreparedProviderStream :
        PreparedProviderStream
    {
        private readonly object _gate = new();
        private readonly AnthropicMessagesStreamingProvider _owner;
        private readonly string _streamAttemptId;
        private readonly TaskCompletionSource<bool> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _body;
        private bool _claimed;
        private bool _active;
        private bool _disposeRequested;

        public AnthropicPreparedProviderStream(
            AnthropicMessagesStreamingProvider owner,
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
                        "The prepared Anthropic stream is no longer available.",
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

    private sealed class BoundedLineReader
    {
        private readonly TextReader _reader;
        private readonly int _maximumCharacters;
        private readonly int _maximumStreamCharacters;
        private readonly char[] _buffer = new char[4_096];
        private int _offset;
        private int _count;
        private long _streamCharacters;
        private bool _skipLeadingLineFeed;

        public BoundedLineReader(
            TextReader reader,
            int maximumCharacters,
            int maximumStreamCharacters)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _maximumCharacters = maximumCharacters;
            _maximumStreamCharacters = maximumStreamCharacters;
        }

        public async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            var line = new StringBuilder(
                Math.Min(_maximumCharacters, 256));
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
                _streamCharacters++;
                if (_streamCharacters > _maximumStreamCharacters)
                {
                    throw ProtocolLimit(
                        "provider_sse_stream_too_large",
                        "The Anthropic SSE stream exceeded its limit.");
                }

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
                    throw ProtocolLimit(
                        "provider_sse_line_too_large",
                        "The Anthropic SSE line exceeded its limit.");
                }

                line.Append(character);
            }
        }
    }
}
