using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.OpenAICompatible;

public sealed class OpenAiCompatibleStreamingProvider :
    IStreamingModelProvider,
    IProviderRouteMetadataSource
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly IProviderCredentialSource _credentials;
    private readonly IStreamingHttpTransport _transport;
    private readonly Uri _endpoint;
    private readonly ProviderCapabilities _capabilities;
    private readonly ProviderRouteMetadata _routeMetadata;

    public OpenAiCompatibleStreamingProvider(
        OpenAiCompatibleProviderOptions options,
        IProviderCredentialSource credentials,
        IStreamingHttpTransport transport)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _credentials =
            credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transport =
            transport ?? throw new ArgumentNullException(nameof(transport));
        _endpoint = BuildEndpoint(_options);
        _capabilities = new ProviderCapabilities
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = _options.MaxContextTokens
        };
        _routeMetadata = new ProviderRouteMetadata(
            _options.Model,
            "openai.chat-completions.sse.v1");
    }

    public string ProviderId => _options.ProviderId;

    public ProviderRouteMetadata RouteMetadata => _routeMetadata;

    public ProviderCapabilities Capabilities => new()
    {
        Streaming = _capabilities.Streaming,
        ToolCalling = _capabilities.ToolCalling,
        JsonOutput = _capabilities.JsonOutput,
        MaxContextTokens = _capabilities.MaxContextTokens
    };

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            ValidateRequest(request);
        }
        catch (ProviderException exception)
        {
            throw KnownZero(exception);
        }

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
                false,
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
            throw new ProviderException(
                "provider_auth_missing",
                "auth",
                "The provider credential is unavailable.",
                false,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        byte[] body;
        try
        {
            body = BuildRequestBody(request);
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
        IStreamingHttpResponse response;
        try
        {
            response = await _transport.SendAsync(
                    new StreamingHttpRequest
                    {
                        Uri = _endpoint,
                        BearerToken = token,
                        Body = body
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
                request.StreamAttemptId,
                _options.MaxSseEventCharacters,
                _options.InputCacheHitUsdPerMillionTokens,
                _options.InputCacheMissUsdPerMillionTokens,
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
            exception.Retryable,
            exception.RetryAfter,
            exception,
            usageKnownToBeZero: true);
    }

    private void ValidateRequest(StreamingModelRequest request)
    {
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

        if (request.Tools.Count > 128)
        {
            throw new ProviderException(
                "provider_tool_limit",
                "validation",
                "The provider accepts at most 128 direct tools.",
                false);
        }

        if (request.MaxOutputTokens < 1)
        {
            throw new ProviderException(
                "provider_output_limit_invalid",
                "validation",
                "The provider output-token limit is invalid.",
                false);
        }
    }

    private byte[] BuildRequestBody(StreamingModelRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber(
                "max_tokens",
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
                && string.Equals(
                    _options.ThinkingMode,
                    "enabled",
                    StringComparison.Ordinal))
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
                    WriteTool(writer, tool);
                }

                writer.WriteEndArray();
                // DeepSeek V4 thinking mode rejects tool_choice. Omitting it also
                // preserves the OpenAI-compatible default of auto.
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void WriteMessage(Utf8JsonWriter writer, NormalizedMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);
        switch (message.Role)
        {
            case NormalizedRoles.System:
            case NormalizedRoles.User:
                writer.WriteString("content", FlattenContent(message.Parts));
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

        writer.WriteString("content", text);
        if (_options.ReplayReasoningContent
            && !string.IsNullOrEmpty(reasoning)
            && string.Equals(
                _options.ThinkingMode,
                "enabled",
                StringComparison.Ordinal))
        {
            writer.WriteString("reasoning_content", reasoning);
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
                writer.WriteString("id", call.ToolCallId);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.ToolName);
                writer.WriteString(
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
        writer.WriteString("tool_call_id", result.ToolCallId);
        writer.WriteString("content", result.Json!.Value.GetRawText());
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

    private static void WriteTool(Utf8JsonWriter writer, ToolDescriptor tool)
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
        writer.WriteString("description", tool.Description);
        writer.WritePropertyName("parameters");
        tool.ParametersSchema.WriteTo(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
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
                false,
                usageKnownToBeZero: true),
            402 => new ProviderException(
                "provider_balance_exhausted",
                "auth",
                "The provider account cannot fund this request.",
                false,
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
                false,
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
        private readonly decimal _outputPrice;
        private readonly Dictionary<int, string> _toolCallIds = new();
        private long _ordinal;

        public SseChunkParser(
            string streamAttemptId,
            int maxCharacters,
            string cacheHitPrice,
            string cacheMissPrice,
            string outputPrice)
        {
            _streamAttemptId = streamAttemptId;
            _maxCharacters = maxCharacters;
            _cacheHitPrice = ParsePrice(cacheHitPrice);
            _cacheMissPrice = ParsePrice(cacheMissPrice);
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
            var cacheHit = ReadNonNegativeInt(
                usage,
                "prompt_cache_hit_tokens",
                required: false);
            var cacheMiss = ReadNonNegativeInt(
                usage,
                "prompt_cache_miss_tokens",
                required: false);
            if (cacheHit == 0 && cacheMiss == 0)
            {
                cacheMiss = input;
            }
            else if ((long)cacheHit + cacheMiss != input)
            {
                throw ProtocolError(
                    "The provider usage cache-token counts are inconsistent.");
            }

            var cost = (
                cacheHit * _cacheHitPrice
                + cacheMiss * _cacheMissPrice
                + output * _outputPrice) / 1_000_000m;
            return new ProviderUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CostUsd = cost.ToString(
                    "0.############################",
                    System.Globalization.CultureInfo.InvariantCulture)
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
