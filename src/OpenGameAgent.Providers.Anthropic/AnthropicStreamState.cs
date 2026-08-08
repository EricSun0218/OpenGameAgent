using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Anthropic;

internal sealed class AnthropicStreamState
{
    private readonly string _requestModel;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly int _maximumCharacters;
    private readonly int _maximumToolCalls;
    private readonly SortedDictionary<int, Block> _blocks = new();
    private long _characters;
    private string? _responseId;
    private string? _responseModel;
    private ModelStopReason _stopReason = ModelStopReason.Pending;
    private string? _rawStopReason;
    private string? _errorMessage;
    private ModelUsage _usage = new();
    private bool _messageStarted;
    private bool _messageStopped;

    public AnthropicStreamState(
        string requestModel,
        string providerId,
        string apiId,
        int maximumCharacters,
        int maximumToolCalls)
    {
        _requestModel = requestModel;
        _providerId = providerId;
        _apiId = apiId;
        _maximumCharacters = maximumCharacters;
        _maximumToolCalls = maximumToolCalls;
    }

    public IReadOnlyList<ModelStreamEvent> Apply(string eventName, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "An Anthropic event must be a JSON object.");
            EnsureUnambiguous(root);
            var type = RequiredString(root, "type");
            if (!string.IsNullOrEmpty(eventName)
                && eventName != "ping"
                && !string.Equals(eventName, type, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Anthropic SSE event name does not match its JSON event type.");
            }

            var updates = new List<ModelStreamEvent>();
            switch (type)
            {
                case "ping":
                    break;
                case "message_start":
                    StartMessage(RequiredObject(root, "message"));
                    break;
                case "content_block_start":
                    StartBlock(RequiredIndex(root), RequiredObject(root, "content_block"), updates);
                    break;
                case "content_block_delta":
                    ApplyDelta(RequiredIndex(root), RequiredObject(root, "delta"), updates);
                    break;
                case "content_block_stop":
                    StopBlock(RequiredIndex(root), updates);
                    break;
                case "message_delta":
                    ApplyMessageDelta(root);
                    break;
                case "message_stop":
                    if (!_messageStarted || _messageStopped)
                    {
                        throw new InvalidDataException("The Anthropic stream stopped a missing or already stopped message.");
                    }

                    _messageStopped = true;
                    break;
                case "error":
                    var error = RequiredObject(root, "error");
                    throw new InvalidDataException(
                        $"Anthropic stream error {OptionalString(error, "type") ?? "unknown"}: "
                        + (OptionalString(error, "message") ?? "No message was supplied."));
                default:
                    throw new InvalidDataException("The Anthropic stream returned unsupported event type '" + type + "'.");
            }

            return updates;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Anthropic stream contained invalid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Anthropic stream did not match the expected response shape.", exception);
        }
    }

    public ModelResponse Partial() => BuildResponse(ModelStopReason.Pending, null);

    public ModelResponse Complete()
    {
        if (!_messageStopped)
        {
            throw new InvalidDataException("The Anthropic stream ended before message_stop.");
        }

        if (_stopReason == ModelStopReason.Pending)
        {
            throw new InvalidDataException("The Anthropic stream ended without a stop reason.");
        }

        if (_blocks.Values.Any(block => !block.Ended))
        {
            throw new InvalidDataException("The Anthropic stream ended with an incomplete content block.");
        }

        return BuildResponse(_stopReason, _errorMessage);
    }

    private ModelResponse BuildResponse(ModelStopReason reason, string? errorMessage)
    {
        var content = new List<AgentContent>();
        foreach (var block in _blocks.Values)
        {
            switch (block.Kind)
            {
                case BlockKind.Text:
                    content.Add(new TextContent(block.Buffer.ToString()));
                    break;
                case BlockKind.Thinking:
                    content.Add(new ReasoningContent(block.Buffer.ToString(), block.Signature.ToString()));
                    break;
                case BlockKind.RedactedThinking:
                    content.Add(new ReasoningContent("[Reasoning redacted]", block.Signature.ToString(), redacted: true));
                    break;
                case BlockKind.Tool:
                    if (!block.Ended && reason == ModelStopReason.Pending)
                    {
                        break;
                    }

                    var arguments = block.Buffer.Length > 0 ? block.Buffer.ToString() : block.InitialInput;
                    if (string.IsNullOrWhiteSpace(arguments))
                    {
                        arguments = "{}";
                    }

                    if (!IsJsonObject(arguments))
                    {
                        if (reason == ModelStopReason.Length)
                        {
                            arguments = "{}";
                        }
                        else
                        {
                            throw new InvalidDataException("A completed Anthropic tool call did not contain a JSON object.");
                        }
                    }

                    content.Add(new ToolCallContent(block.Id!, block.Name!, arguments));
                    break;
            }
        }

        return new ModelResponse(
            content,
            reason,
            _usage,
            errorMessage,
            _providerId,
            _apiId,
            _responseModel ?? _requestModel,
            _responseId,
            _rawStopReason);
    }

    private void StartMessage(JsonElement message)
    {
        if (_messageStarted)
        {
            throw new InvalidDataException("The Anthropic stream started more than one message.");
        }

        _messageStarted = true;
        _responseId = RequiredString(message, "id");
        _responseModel = OptionalString(message, "model") ?? _requestModel;
        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            ReadUsage(usage);
        }
    }

    private void StartBlock(int index, JsonElement content, ICollection<ModelStreamEvent> updates)
    {
        if (!_messageStarted || _messageStopped || _blocks.ContainsKey(index))
        {
            throw new InvalidDataException("An Anthropic content block started in an invalid state.");
        }

        var type = RequiredString(content, "type");
        Block? block = type switch
        {
            "text" => new Block(index, BlockKind.Text),
            "thinking" => new Block(index, BlockKind.Thinking),
            "redacted_thinking" => new Block(index, BlockKind.RedactedThinking),
            "tool_use" => CreateToolBlock(index, content),
            _ => null,
        };
        if (block is null)
        {
            return;
        }

        if (block.Kind == BlockKind.Tool
            && _blocks.Values.Count(value => value.Kind == BlockKind.Tool) >= _maximumToolCalls)
        {
            throw new InvalidDataException("The Anthropic response exceeded the configured tool-call limit.");
        }

        if (block.Kind == BlockKind.Text)
        {
            Append(block.Buffer, OptionalString(content, "text") ?? string.Empty);
        }
        else if (block.Kind == BlockKind.Thinking)
        {
            Append(block.Buffer, OptionalString(content, "thinking") ?? string.Empty);
            Append(block.Signature, OptionalString(content, "signature") ?? string.Empty);
        }
        else if (block.Kind == BlockKind.RedactedThinking)
        {
            Append(block.Signature, RequiredString(content, "data"));
        }

        _blocks.Add(index, block);
        var kind = block.Kind switch
        {
            BlockKind.Text => ModelStreamEventKind.TextStarted,
            BlockKind.Tool => ModelStreamEventKind.ToolCallStarted,
            _ => ModelStreamEventKind.ReasoningStarted,
        };
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            contentIndex: ContentIndex(index),
            toolCallId: block.Id,
            toolName: block.Name));
    }

    private static Block CreateToolBlock(int index, JsonElement content)
    {
        var block = new Block(index, BlockKind.Tool)
        {
            Id = RequiredString(content, "id"),
            Name = RequiredString(content, "name"),
        };
        if (content.TryGetProperty("input", out var input))
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Anthropic tool input must be a JSON object.");
            }

            block.InitialInput = input.GetRawText();
        }

        return block;
    }

    private void ApplyDelta(int index, JsonElement delta, ICollection<ModelStreamEvent> updates)
    {
        if (!_blocks.TryGetValue(index, out var block) || block.Ended)
        {
            throw new InvalidDataException("An Anthropic delta referenced a missing or ended content block.");
        }

        var type = RequiredString(delta, "type");
        switch (type)
        {
            case "text_delta" when block.Kind == BlockKind.Text:
                AddVisibleDelta(block, RequiredString(delta, "text"), ModelStreamEventKind.TextDelta, updates);
                break;
            case "thinking_delta" when block.Kind == BlockKind.Thinking:
                AddVisibleDelta(block, RequiredString(delta, "thinking"), ModelStreamEventKind.ReasoningDelta, updates);
                break;
            case "input_json_delta" when block.Kind == BlockKind.Tool:
                AddVisibleDelta(block, RequiredString(delta, "partial_json"), ModelStreamEventKind.ToolCallDelta, updates);
                break;
            case "signature_delta" when block.Kind == BlockKind.Thinking:
                Append(block.Signature, RequiredString(delta, "signature"));
                break;
            default:
                throw new InvalidDataException("An Anthropic content delta did not match its block type.");
        }
    }

    private void AddVisibleDelta(
        Block block,
        string delta,
        ModelStreamEventKind kind,
        ICollection<ModelStreamEvent> updates)
    {
        Append(block.Buffer, delta);
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            delta,
            ContentIndex(block.Index),
            block.Id,
            block.Name));
    }

    private void StopBlock(int index, ICollection<ModelStreamEvent> updates)
    {
        if (!_blocks.TryGetValue(index, out var block) || block.Ended)
        {
            throw new InvalidDataException("An Anthropic content block stopped in an invalid state.");
        }

        block.Ended = true;
        var kind = block.Kind switch
        {
            BlockKind.Text => ModelStreamEventKind.TextEnded,
            BlockKind.Tool => ModelStreamEventKind.ToolCallEnded,
            _ => ModelStreamEventKind.ReasoningEnded,
        };
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            contentIndex: ContentIndex(index),
            toolCallId: block.Id,
            toolName: block.Name));
    }

    private void ApplyMessageDelta(JsonElement root)
    {
        var delta = RequiredObject(root, "delta");
        if (delta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
        {
            if (reason.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Anthropic stop_reason must be a string or null.");
            }

            _rawStopReason = reason.GetString();
            MapStopReason(_rawStopReason!, delta);
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            ReadUsage(usage);
        }
    }

    private void MapStopReason(string reason, JsonElement delta)
    {
        switch (reason)
        {
            case "end_turn":
            case "pause_turn":
            case "stop_sequence":
                _stopReason = ModelStopReason.Stop;
                break;
            case "max_tokens":
                _stopReason = ModelStopReason.Length;
                break;
            case "tool_use":
                _stopReason = ModelStopReason.ToolUse;
                break;
            case "refusal":
                _stopReason = ModelStopReason.Error;
                _errorMessage = ReadStopExplanation(delta) ?? "The model refused to complete the request.";
                break;
            case "sensitive":
                _stopReason = ModelStopReason.Error;
                _errorMessage = "The provider stopped the response because it was marked sensitive.";
                break;
            default:
                _stopReason = ModelStopReason.Error;
                _errorMessage = "The provider returned unsupported stop reason '" + reason + "'.";
                break;
        }
    }

    private static string? ReadStopExplanation(JsonElement delta)
    {
        if (!delta.TryGetProperty("stop_details", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return OptionalString(details, "explanation");
    }

    private void ReadUsage(JsonElement usage)
    {
        var input = OptionalNonNegativeLong(usage, "input_tokens") ?? _usage.InputTokens;
        var output = OptionalNonNegativeLong(usage, "output_tokens") ?? _usage.OutputTokens;
        var cacheRead = OptionalNonNegativeLong(usage, "cache_read_input_tokens") ?? _usage.CacheReadTokens;
        var cacheWrite = OptionalNonNegativeLong(usage, "cache_creation_input_tokens") ?? _usage.CacheWriteTokens;
        var oneHour = _usage.CacheWriteOneHourTokens;
        if (usage.TryGetProperty("cache_creation", out var cacheCreation)
            && cacheCreation.ValueKind == JsonValueKind.Object)
        {
            oneHour = OptionalNonNegativeLong(cacheCreation, "ephemeral_1h_input_tokens") ?? oneHour;
        }

        var reasoning = _usage.ReasoningTokens;
        if (usage.TryGetProperty("output_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            reasoning = OptionalNonNegativeLong(details, "thinking_tokens") ?? reasoning;
        }

        if (reasoning > output || oneHour > cacheWrite)
        {
            throw new InvalidDataException("Anthropic usage contains inconsistent token subsets.");
        }

        _usage = new ModelUsage(input, output, cacheRead, cacheWrite, reasoning, oneHour);
    }

    private int ContentIndex(int blockIndex) => _blocks.Keys.TakeWhile(key => key != blockIndex).Count();

    private void Append(StringBuilder builder, string value)
    {
        _characters += value.Length;
        if (_characters > _maximumCharacters)
        {
            throw new InvalidDataException("The accumulated Anthropic response exceeded the configured size limit.");
        }

        builder.Append(value);
    }

    private static int RequiredIndex(JsonElement root)
    {
        if (!root.TryGetProperty("index", out var value) || !value.TryGetInt32(out var index) || index < 0)
        {
            throw new InvalidDataException("An Anthropic content index must be a non-negative integer.");
        }

        return index;
    }

    private static JsonElement RequiredObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Anthropic event field '{property}' must be an object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property)
        ?? throw new InvalidDataException($"Anthropic event field '{property}' must be a string.");

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Anthropic event field '{property}' must be a string or null.");
        }

        return value.GetString();
    }

    private static long? OptionalNonNegativeLong(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!value.TryGetInt64(out var result) || result < 0)
        {
            throw new InvalidDataException($"Anthropic usage field '{property}' must be a non-negative integer.");
        }

        return result;
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string message)
    {
        if (value.ValueKind != expected)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("The Anthropic stream contains duplicate JSON property names.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private enum BlockKind
    {
        Text,
        Thinking,
        RedactedThinking,
        Tool,
    }

    private sealed class Block
    {
        public Block(int index, BlockKind kind)
        {
            Index = index;
            Kind = kind;
        }

        public int Index { get; }

        public BlockKind Kind { get; }

        public StringBuilder Buffer { get; } = new();

        public StringBuilder Signature { get; } = new();

        public string? InitialInput { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool Ended { get; set; }
    }
}
