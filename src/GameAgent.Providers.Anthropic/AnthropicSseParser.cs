using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.Anthropic;

internal sealed class AnthropicSseParser
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _streamAttemptId;
    private readonly int _maxToolArgumentsUtf8Bytes;
    private readonly decimal? _inputPrice;
    private readonly decimal? _cacheReadPrice;
    private readonly decimal? _cacheWrite5mPrice;
    private readonly decimal? _cacheWrite1hPrice;
    private readonly decimal? _outputPrice;
    private readonly HashSet<string> _toolUseIds =
        new(StringComparer.Ordinal);
    private ActiveBlock? _activeBlock;
    private UsageState? _usage;
    private string? _stopReason;
    private int _nextContentIndex;
    private long _ordinal;
    private bool _messageStarted;
    private bool _messageDeltaSeen;
    private bool _toolUseSeen;
    private bool _usageEmitted;

    internal AnthropicSseParser(
        string streamAttemptId,
        AnthropicProviderOptions options)
    {
        _streamAttemptId = streamAttemptId;
        _maxToolArgumentsUtf8Bytes =
            options.MaxToolArgumentsUtf8Bytes;
        _inputPrice = ParsePrice(options.InputUsdPerMillionTokens);
        _cacheReadPrice = ParsePrice(
            options.CacheReadUsdPerMillionTokens);
        _cacheWrite5mPrice = ParsePrice(
            options.CacheWrite5mUsdPerMillionTokens);
        _cacheWrite1hPrice = ParsePrice(
            options.CacheWrite1hUsdPerMillionTokens);
        _outputPrice = ParsePrice(options.OutputUsdPerMillionTokens);
    }

    internal bool IsComplete { get; private set; }

    internal IReadOnlyList<ModelStreamEvent> Parse(
        string eventName,
        string payload)
    {
        if (IsComplete)
        {
            throw Protocol("Anthropic emitted data after message_stop.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                "provider_chunk_invalid_json",
                "provider",
                "Anthropic emitted invalid stream JSON.",
                true,
                innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Protocol(
                    "An Anthropic SSE payload must be an object.");
            }

            EnsureUniqueJsonProperties(root);
            var payloadType = ReadString(
                root,
                "type",
                128,
                allowEmpty: false);
            if (!string.Equals(
                    payloadType,
                    eventName,
                    StringComparison.Ordinal))
            {
                throw Protocol(
                    "The Anthropic SSE name and payload type do not match.");
            }

            return eventName switch
            {
                "message_start" => ParseMessageStart(root),
                "content_block_start" => ParseContentBlockStart(root),
                "content_block_delta" => ParseContentBlockDelta(root),
                "content_block_stop" => ParseContentBlockStop(root),
                "message_delta" => ParseMessageDelta(root),
                "message_stop" => ParseMessageStop(),
                "ping" => Array.Empty<ModelStreamEvent>(),
                "error" => throw ParseStreamError(root),
                _ => Array.Empty<ModelStreamEvent>()
            };
        }
    }

    private IReadOnlyList<ModelStreamEvent> ParseMessageStart(
        JsonElement root)
    {
        if (_messageStarted)
        {
            throw Protocol(
                "Anthropic emitted more than one message_start.");
        }

        var message = RequiredObject(root, "message");
        if (!string.Equals(
                ReadString(message, "type", 64, allowEmpty: false),
                "message",
                StringComparison.Ordinal)
            || !string.Equals(
                ReadString(message, "role", 64, allowEmpty: false),
                "assistant",
                StringComparison.Ordinal))
        {
            throw Protocol(
                "The Anthropic message_start envelope is invalid.");
        }

        _ = ReadString(message, "id", 256, allowEmpty: false);
        _ = ReadString(message, "model", 256, allowEmpty: false);
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() != 0
            || !IsNullProperty(message, "stop_reason")
            || !IsNullProperty(message, "stop_sequence"))
        {
            throw Protocol(
                "The Anthropic message_start state is invalid.");
        }

        _usage = ParseInitialUsage(RequiredObject(message, "usage"));
        _messageStarted = true;
        return Array.Empty<ModelStreamEvent>();
    }

    private IReadOnlyList<ModelStreamEvent> ParseContentBlockStart(
        JsonElement root)
    {
        RequireContentPhase();
        if (_messageDeltaSeen || _activeBlock is not null)
        {
            throw Protocol(
                "An Anthropic content block started out of order.");
        }

        var index = ReadIndex(root);
        if (index != _nextContentIndex)
        {
            throw Protocol(
                "Anthropic content-block indexes must be contiguous.");
        }

        var block = RequiredObject(root, "content_block");
        var type = ReadString(
            block,
            "type",
            64,
            allowEmpty: false);
        switch (type)
        {
            case "text":
                {
                    var initial = ReadString(
                        block,
                        "text",
                        1_048_576,
                        allowEmpty: true);
                    _activeBlock = ActiveBlock.Text(index);
                    return initial.Length == 0
                        ? Array.Empty<ModelStreamEvent>()
                        : new[] { EventText(initial) };
                }
            case "tool_use":
                {
                    var id = ReadString(
                        block,
                        "id",
                        256,
                        allowEmpty: false);
                    if (!_toolUseIds.Add(id))
                    {
                        throw Protocol(
                            "Anthropic repeated a tool_use identifier.");
                    }

                    var name = ReadString(
                        block,
                        "name",
                        64,
                        allowEmpty: false);
                    if (!IsToolName(name))
                    {
                        throw Protocol(
                            "An Anthropic tool_use block has an invalid name.");
                    }
                    if (!block.TryGetProperty("input", out var input)
                        || input.ValueKind != JsonValueKind.Object)
                    {
                        throw Protocol(
                            "An Anthropic tool_use block has invalid input.");
                    }

                    var initialInput = input.GetRawText();
                    EnsureUtf8Limit(
                        initialInput,
                        _maxToolArgumentsUtf8Bytes,
                        "An Anthropic tool input exceeded its limit.");
                    _activeBlock = ActiveBlock.Tool(
                        index,
                        id,
                        name,
                        initialInput,
                        input.EnumerateObject().Any());
                    _toolUseSeen = true;
                    return new[]
                    {
                    EventTool(
                        id,
                        toolNameDelta: name,
                        argumentsJsonDelta: null)
                };
                }
            default:
                throw new ProviderException(
                    "provider_content_block_unsupported",
                    "provider",
                    "Anthropic emitted a content block outside the text-and-tools dialect.",
                    ProviderFailureDisposition.Failover);
        }
    }

    private IReadOnlyList<ModelStreamEvent> ParseContentBlockDelta(
        JsonElement root)
    {
        RequireContentPhase();
        var active = _activeBlock
                     ?? throw Protocol(
                         "An Anthropic content delta has no active block.");
        if (ReadIndex(root) != active.Index)
        {
            throw Protocol(
                "An Anthropic content delta changed block index.");
        }

        var delta = RequiredObject(root, "delta");
        var type = ReadString(
            delta,
            "type",
            64,
            allowEmpty: false);
        if (active.Kind == ActiveBlockKind.Text)
        {
            if (!string.Equals(type, "text_delta", StringComparison.Ordinal))
            {
                throw Protocol(
                    "An Anthropic text block received a non-text delta.");
            }

            return new[]
            {
                EventText(
                    ReadString(
                        delta,
                        "text",
                        1_048_576,
                        allowEmpty: true))
            };
        }

        if (!string.Equals(
                type,
                "input_json_delta",
                StringComparison.Ordinal))
        {
            throw Protocol(
                "An Anthropic tool block received a non-JSON delta.");
        }

        var partial = ReadString(
            delta,
            "partial_json",
            _maxToolArgumentsUtf8Bytes,
            allowEmpty: true);
        active.AppendToolInput(
            partial,
            _maxToolArgumentsUtf8Bytes);
        return new[]
        {
            EventTool(
                active.ToolId!,
                toolNameDelta: null,
                argumentsJsonDelta: partial)
        };
    }

    private IReadOnlyList<ModelStreamEvent> ParseContentBlockStop(
        JsonElement root)
    {
        RequireContentPhase();
        var active = _activeBlock
                     ?? throw Protocol(
                         "An Anthropic content stop has no active block.");
        if (ReadIndex(root) != active.Index)
        {
            throw Protocol(
                "An Anthropic content stop changed block index.");
        }

        var events = new List<ModelStreamEvent>(1);
        if (active.Kind == ActiveBlockKind.Tool)
        {
            var finalInput = active.FinalToolInput();
            ValidateToolInput(finalInput);
            if (!active.SawInputDelta)
            {
                events.Add(
                    EventTool(
                        active.ToolId!,
                        toolNameDelta: null,
                        argumentsJsonDelta: finalInput));
            }
            else if (active.InputLength == 0)
            {
                events.Add(
                    EventTool(
                        active.ToolId!,
                        toolNameDelta: null,
                        argumentsJsonDelta: finalInput));
            }
        }

        _activeBlock = null;
        _nextContentIndex++;
        return events;
    }

    private IReadOnlyList<ModelStreamEvent> ParseMessageDelta(
        JsonElement root)
    {
        RequireContentPhase();
        if (_activeBlock is not null)
        {
            throw Protocol(
                "Anthropic changed the message while a content block was active.");
        }

        if (_usageEmitted)
        {
            throw Protocol(
                "Anthropic emitted a message_delta after final cumulative usage.");
        }

        var delta = RequiredObject(root, "delta");
        if (!delta.TryGetProperty(
                "stop_reason",
                out var stopReasonElement))
        {
            throw Protocol(
                "An Anthropic message_delta has no stop reason field.");
        }

        string? incomingStopReason;
        if (stopReasonElement.ValueKind == JsonValueKind.Null)
        {
            incomingStopReason = null;
        }
        else if (stopReasonElement.ValueKind == JsonValueKind.String)
        {
            incomingStopReason = stopReasonElement.GetString();
            EnsureUtf8Limit(
                incomingStopReason ?? string.Empty,
                64,
                "The Anthropic stop reason is invalid.");
        }
        else
        {
            throw Protocol(
                "The Anthropic stop reason has an invalid type.");
        }

        if (delta.TryGetProperty(
                "stop_sequence",
                out var stopSequence)
            && stopSequence.ValueKind is not JsonValueKind.Null
                and not JsonValueKind.String)
        {
            throw Protocol(
                "The Anthropic stop sequence has an invalid type.");
        }

        if (incomingStopReason is not null)
        {
            if (_stopReason is not null
                && !string.Equals(
                    _stopReason,
                    incomingStopReason,
                    StringComparison.Ordinal))
            {
                throw Protocol(
                    "Anthropic changed the cumulative stop reason.");
            }

            _stopReason = incomingStopReason;
        }

        _usage!.MergeDelta(RequiredObject(root, "usage"));
        _messageDeltaSeen = true;
        if (incomingStopReason is null)
        {
            return Array.Empty<ModelStreamEvent>();
        }

        _usageEmitted = true;
        return new[]
        {
            EventUsage(
                _usage.ToProviderUsage(
                    _inputPrice,
                    _cacheReadPrice,
                    _cacheWrite5mPrice,
                    _cacheWrite1hPrice,
                    _outputPrice))
        };
    }

    private IReadOnlyList<ModelStreamEvent> ParseMessageStop()
    {
        RequireContentPhase();
        if (_activeBlock is not null
            || !_messageDeltaSeen
            || !_usageEmitted
            || string.IsNullOrEmpty(_stopReason))
        {
            throw Protocol(
                "Anthropic emitted message_stop before the message was complete.");
        }

        var finishReason = MapFinishReason(_stopReason, _toolUseSeen);
        IsComplete = true;
        return new[]
        {
            EventCompleted(finishReason)
        };
    }

    private UsageState ParseInitialUsage(JsonElement usage)
    {
        var input = ReadCounter(
            usage,
            "input_tokens",
            required: true)!.Value;
        var output = ReadCounter(
            usage,
            "output_tokens",
            required: true)!.Value;
        var cacheCreation = ReadCounter(
            usage,
            "cache_creation_input_tokens",
            required: false);
        var cacheRead = ReadCounter(
            usage,
            "cache_read_input_tokens",
            required: false);
        var reasoning = ReadReasoningTokens(usage);
        var cacheBreakdown = ReadCacheBreakdown(usage, cacheCreation);
        if (reasoning.HasValue && reasoning.Value > output)
        {
            throw Protocol(
                "Anthropic reasoning usage exceeds output usage.");
        }

        return new UsageState(
            input,
            output,
            cacheCreation,
            cacheRead,
            cacheBreakdown.FiveMinute,
            cacheBreakdown.OneHour,
            reasoning);
    }

    private void RequireContentPhase()
    {
        if (!_messageStarted || IsComplete || _usage is null)
        {
            throw Protocol(
                "Anthropic emitted a stream event before message_start.");
        }
    }

    private static int ReadIndex(JsonElement root)
    {
        if (!root.TryGetProperty("index", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var index)
            || index < 0)
        {
            throw Protocol(
                "An Anthropic content block has an invalid index.");
        }

        return index;
    }

    private static JsonElement RequiredObject(
        JsonElement source,
        string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Protocol(
                "An Anthropic stream object field is missing or invalid.");
        }

        return value;
    }

    private static string ReadString(
        JsonElement source,
        string propertyName,
        int maximumUtf8Bytes,
        bool allowEmpty)
    {
        if (!source.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Protocol(
                "An Anthropic stream string field is missing or invalid.");
        }

        var result = value.GetString() ?? string.Empty;
        if (!allowEmpty && result.Length == 0)
        {
            throw Protocol(
                "An Anthropic stream string field is empty.");
        }

        EnsureUtf8Limit(
            result,
            maximumUtf8Bytes,
            "An Anthropic stream string exceeded its limit.");
        return result;
    }

    private static bool IsNullProperty(
        JsonElement source,
        string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Null;
    }

    private static bool IsToolName(string value)
    {
        return value.Length is >= 1 and <= 64
               && value.All(character =>
                   character is >= 'a' and <= 'z'
                       or >= 'A' and <= 'Z'
                       or >= '0' and <= '9'
                       or '_'
                       or '-');
    }

    private static long? ReadCounter(
        JsonElement source,
        string propertyName,
        bool required)
    {
        if (!source.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                throw Protocol(
                    "Anthropic usage is missing a required token count.");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < 0
            || result > int.MaxValue)
        {
            throw Protocol(
                "Anthropic usage contains an invalid token count.");
        }

        return result;
    }

    private static long? ReadReasoningTokens(JsonElement source)
    {
        if (!source.TryGetProperty(
                "output_tokens_details",
                out var details)
            || details.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (details.ValueKind != JsonValueKind.Object)
        {
            throw Protocol(
                "Anthropic output token details are invalid.");
        }

        return ReadCounter(
            details,
            "thinking_tokens",
            required: true);
    }

    private static (long? FiveMinute, long? OneHour) ReadCacheBreakdown(
        JsonElement usage,
        long? cacheCreation)
    {
        if (!usage.TryGetProperty("cache_creation", out var breakdown)
            || breakdown.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        if (breakdown.ValueKind != JsonValueKind.Object)
        {
            throw Protocol(
                "Anthropic cache-creation details are invalid.");
        }

        var fiveMinute = ReadCounter(
            breakdown,
            "ephemeral_5m_input_tokens",
            required: true)!.Value;
        var oneHour = ReadCounter(
            breakdown,
            "ephemeral_1h_input_tokens",
            required: true)!.Value;
        var total = checked(fiveMinute + oneHour);
        if (!cacheCreation.HasValue || cacheCreation.Value != total)
        {
            throw Protocol(
                "Anthropic cache-creation usage is inconsistent.");
        }

        return (fiveMinute, oneHour);
    }

    private static void ValidateToolInput(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(
                value,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Protocol(
                    "Anthropic tool input must finish as a JSON object.");
            }

            EnsureUniqueJsonProperties(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                "provider_tool_input_invalid_json",
                "provider",
                "Anthropic emitted invalid tool input JSON.",
                true,
                innerException: exception);
        }
    }

    private static ProviderException ParseStreamError(JsonElement root)
    {
        var error = RequiredObject(root, "error");
        var type = ReadString(
            error,
            "type",
            128,
            allowEmpty: false);
        _ = ReadString(
            error,
            "message",
            2_048,
            allowEmpty: false);
        return type switch
        {
            "overloaded_error" or "rate_limit_error"
                or "api_error" or "timeout_error" =>
                new ProviderException(
                    "provider_stream_error",
                    "overload",
                    "Anthropic reported a transient in-stream error.",
                    true),
            "authentication_error" or "permission_error"
                or "billing_error" or "not_found_error" =>
                new ProviderException(
                    "provider_stream_auth_error",
                    "auth",
                    "Anthropic rejected this route during streaming.",
                    ProviderFailureDisposition.Failover),
            "invalid_request_error" or "request_too_large" =>
                new ProviderException(
                    "provider_stream_request_error",
                    "validation",
                    "Anthropic rejected the request during streaming.",
                    false),
            _ => new ProviderException(
                "provider_stream_error",
                "provider",
                "Anthropic reported an unsupported in-stream error.",
                false)
        };
    }

    private static void EnsureUniqueJsonProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                        {
                            throw Protocol(
                                "Anthropic emitted a JSON object with duplicate properties.");
                        }

                        EnsureUniqueJsonProperties(property.Value);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    EnsureUniqueJsonProperties(item);
                }

                break;
        }
    }

    private static string MapFinishReason(
        string stopReason,
        bool toolUseSeen)
    {
        return stopReason switch
        {
            "end_turn" => "stop",
            "stop_sequence" => "stop",
            "tool_use" when toolUseSeen => "tool_calls",
            "max_tokens" => "length",
            "model_context_window_exceeded" => "length",
            "refusal" => "content_filter",
            "tool_use" => throw Protocol(
                "Anthropic stopped for tool use without a tool_use block."),
            "pause_turn" => throw new ProviderException(
                "provider_stop_reason_unsupported",
                "provider",
                "Anthropic paused a server-side tool turn outside this dialect.",
                ProviderFailureDisposition.Failover),
            _ => throw new ProviderException(
                "provider_stop_reason_unsupported",
                "provider",
                "Anthropic returned an unsupported stop reason.",
                ProviderFailureDisposition.Failover)
        };
    }

    private ModelStreamEvent EventText(string text)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = _streamAttemptId,
            Ordinal = _ordinal++,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = text
        };
    }

    private ModelStreamEvent EventTool(
        string id,
        string? toolNameDelta,
        string? argumentsJsonDelta)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = _streamAttemptId,
            Ordinal = _ordinal++,
            Kind = ModelStreamEventKinds.ToolCallDelta,
            ToolCallId = id,
            ToolNameDelta = toolNameDelta,
            ArgumentsJsonDelta = argumentsJsonDelta
        };
    }

    private ModelStreamEvent EventUsage(ProviderUsage usage)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = _streamAttemptId,
            Ordinal = _ordinal++,
            Kind = ModelStreamEventKinds.Usage,
            Usage = usage
        };
    }

    private ModelStreamEvent EventCompleted(string finishReason)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = _streamAttemptId,
            Ordinal = _ordinal++,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = finishReason
        };
    }

    private static decimal? ParsePrice(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture);
    }

    private static void EnsureUtf8Limit(
        string value,
        int maximumBytes,
        string message)
    {
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw AnthropicMessagesStreamingProvider.ProtocolLimit(
                    "provider_stream_field_too_large",
                    message);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProviderException(
                "provider_stream_unicode_invalid",
                "provider",
                "Anthropic emitted malformed Unicode.",
                true,
                innerException: exception);
        }
    }

    private static ProviderException Protocol(string message)
    {
        return AnthropicMessagesStreamingProvider.ProtocolError(message);
    }

    private enum ActiveBlockKind
    {
        Text,
        Tool
    }

    private sealed class ActiveBlock
    {
        private readonly string _initialInput;
        private readonly bool _initialInputHasProperties;
        private readonly StringBuilder _input = new();
        private int _inputUtf8Bytes;

        private ActiveBlock(
            int index,
            ActiveBlockKind kind,
            string? toolId,
            string? toolName,
            string initialInput,
            bool initialInputHasProperties)
        {
            Index = index;
            Kind = kind;
            ToolId = toolId;
            ToolName = toolName;
            _initialInput = initialInput;
            _initialInputHasProperties = initialInputHasProperties;
        }

        internal int Index { get; }

        internal ActiveBlockKind Kind { get; }

        internal string? ToolId { get; }

        internal string? ToolName { get; }

        internal bool SawInputDelta { get; private set; }

        internal int InputLength => _input.Length;

        internal static ActiveBlock Text(int index)
        {
            return new ActiveBlock(
                index,
                ActiveBlockKind.Text,
                null,
                null,
                string.Empty,
                initialInputHasProperties: false);
        }

        internal static ActiveBlock Tool(
            int index,
            string id,
            string name,
            string initialInput,
            bool initialInputHasProperties)
        {
            return new ActiveBlock(
                index,
                ActiveBlockKind.Tool,
                id,
                name,
                initialInput,
                initialInputHasProperties);
        }

        internal void AppendToolInput(
            string partial,
            int maximumUtf8Bytes)
        {
            if (_initialInputHasProperties)
            {
                throw Protocol(
                    "Anthropic streamed tool input after a non-empty initial input.");
            }

            int bytes;
            try
            {
                bytes = StrictUtf8.GetByteCount(partial);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ProviderException(
                    "provider_stream_unicode_invalid",
                    "provider",
                    "Anthropic emitted malformed Unicode.",
                    true,
                    innerException: exception);
            }

            if ((long)_inputUtf8Bytes + bytes > maximumUtf8Bytes)
            {
                throw AnthropicMessagesStreamingProvider.ProtocolLimit(
                    "provider_tool_input_too_large",
                    "Anthropic tool input exceeded its limit.");
            }

            SawInputDelta = true;
            _inputUtf8Bytes += bytes;
            _input.Append(partial);
        }

        internal string FinalToolInput()
        {
            return !SawInputDelta || _input.Length == 0
                ? _initialInput
                : _input.ToString();
        }
    }

    private sealed class UsageState
    {
        private long _input;
        private long _output;
        private long? _cacheCreation;
        private long? _cacheRead;
        private readonly long? _cacheCreation5m;
        private readonly long? _cacheCreation1h;
        private long? _reasoning;

        internal UsageState(
            long input,
            long output,
            long? cacheCreation,
            long? cacheRead,
            long? cacheCreation5m,
            long? cacheCreation1h,
            long? reasoning)
        {
            _input = input;
            _output = output;
            _cacheCreation = cacheCreation;
            _cacheRead = cacheRead;
            _cacheCreation5m = cacheCreation5m;
            _cacheCreation1h = cacheCreation1h;
            _reasoning = reasoning;
        }

        internal void MergeDelta(JsonElement usage)
        {
            var output = ReadCounter(
                usage,
                "output_tokens",
                required: true)!.Value;
            if (output < _output)
            {
                throw Protocol(
                    "Anthropic cumulative output usage decreased.");
            }

            _output = output;
            MergeOptional(
                ref _input,
                ReadCounter(usage, "input_tokens", required: false),
                "input");
            MergeOptional(
                ref _cacheCreation,
                ReadCounter(
                    usage,
                    "cache_creation_input_tokens",
                    required: false),
                "cache creation");
            MergeOptional(
                ref _cacheRead,
                ReadCounter(
                    usage,
                    "cache_read_input_tokens",
                    required: false),
                "cache read");
            var reasoning = ReadReasoningTokens(usage);
            MergeOptional(ref _reasoning, reasoning, "reasoning");
            if (_reasoning.HasValue && _reasoning.Value > _output)
            {
                throw Protocol(
                    "Anthropic reasoning usage exceeds output usage.");
            }
        }

        internal ProviderUsage ToProviderUsage(
            decimal? inputPrice,
            decimal? cacheReadPrice,
            decimal? cacheWrite5mPrice,
            decimal? cacheWrite1hPrice,
            decimal? outputPrice)
        {
            if (_cacheCreation.HasValue != _cacheRead.HasValue)
            {
                throw Protocol(
                    "Anthropic cache usage has an incomplete read/write pair.");
            }

            var cacheExact =
                _cacheCreation.HasValue && _cacheRead.HasValue;
            long totalInput = _input;
            long? cacheMiss = null;
            long? providerTotal = null;
            if (cacheExact)
            {
                cacheMiss = checked(_input + _cacheCreation!.Value);
                totalInput = checked(cacheMiss.Value + _cacheRead!.Value);
                providerTotal = checked(totalInput + _output);
            }

            EnsureInt32(totalInput);
            EnsureNullableInt32(cacheMiss);
            EnsureNullableInt32(providerTotal);

            var costAvailable = cacheExact
                                && PriceAvailable(_input, inputPrice)
                                && PriceAvailable(
                                    _cacheRead!.Value,
                                    cacheReadPrice)
                                && CacheWriteCostAvailable(
                                    _cacheCreation!.Value,
                                    _cacheCreation5m,
                                    _cacheCreation1h,
                                    cacheWrite5mPrice,
                                    cacheWrite1hPrice)
                                && PriceAvailable(_output, outputPrice);
            var cost = 0m;
            if (costAvailable)
            {
                try
                {
                    cost = checked(
                        (_input * inputPrice!.Value
                         + _cacheRead!.Value * cacheReadPrice!.Value
                         + (_cacheCreation5m ?? 0)
                         * (cacheWrite5mPrice ?? 0)
                         + (_cacheCreation1h ?? 0)
                         * (cacheWrite1hPrice ?? 0)
                         + _output * outputPrice!.Value)
                        / 1_000_000m);
                }
                catch (OverflowException exception)
                {
                    throw new ProviderException(
                        "provider_usage_cost_overflow",
                        "provider",
                        "Anthropic usage cost exceeded its numeric limit.",
                        false,
                        innerException: exception);
                }
            }

            return new ProviderUsage
            {
                InputTokens = (int)totalInput,
                OutputTokens = (int)_output,
                CacheReadTokens = cacheExact
                    ? (int)_cacheRead!.Value
                    : null,
                CacheWriteTokens = cacheExact
                    ? (int)_cacheCreation!.Value
                    : null,
                CacheMissTokens = cacheExact
                    ? (int)cacheMiss!.Value
                    : null,
                ReasoningTokens = _reasoning.HasValue
                    ? (int)_reasoning.Value
                    : null,
                ProviderTotalTokens = providerTotal.HasValue
                    ? (int)providerTotal.Value
                    : null,
                CostUsd = cost.ToString(
                    "0.############################",
                    CultureInfo.InvariantCulture),
                Availability = costAvailable
                    ? UsageAvailabilityStates.CostAvailable
                    : UsageAvailabilityStates.CostUnavailable,
                Samples = 1
            };
        }

        private static void MergeOptional(
            ref long current,
            long? incoming,
            string field)
        {
            if (!incoming.HasValue)
            {
                return;
            }

            if (incoming.Value < current)
            {
                throw Protocol(
                    $"Anthropic cumulative {field} usage decreased.");
            }

            current = incoming.Value;
        }

        private static void MergeOptional(
            ref long? current,
            long? incoming,
            string field)
        {
            if (!incoming.HasValue)
            {
                return;
            }

            if (current.HasValue && incoming.Value < current.Value)
            {
                throw Protocol(
                    $"Anthropic cumulative {field} usage decreased.");
            }

            current = incoming.Value;
        }

        private static bool PriceAvailable(
            long tokens,
            decimal? price)
        {
            return tokens == 0 || price.HasValue;
        }

        private static bool CacheWriteCostAvailable(
            long aggregate,
            long? fiveMinute,
            long? oneHour,
            decimal? fiveMinutePrice,
            decimal? oneHourPrice)
        {
            if (aggregate == 0)
            {
                return true;
            }

            return fiveMinute.HasValue
                   && oneHour.HasValue
                   && checked(fiveMinute.Value + oneHour.Value) == aggregate
                   && PriceAvailable(fiveMinute.Value, fiveMinutePrice)
                   && PriceAvailable(oneHour.Value, oneHourPrice);
        }

        private static void EnsureInt32(long value)
        {
            if (value is < 0 or > int.MaxValue)
            {
                throw Protocol(
                    "Anthropic aggregate usage exceeds the runtime limit.");
            }
        }

        private static void EnsureNullableInt32(long? value)
        {
            if (value.HasValue)
            {
                EnsureInt32(value.Value);
            }
        }
    }
}
