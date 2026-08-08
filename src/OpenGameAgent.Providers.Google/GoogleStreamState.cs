using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Google;

internal sealed class GoogleStreamState
{
    private readonly string _requestModel;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly int _maximumCharacters;
    private readonly int _maximumToolCalls;
    private readonly List<Block> _blocks = new();
    private readonly HashSet<string> _toolCallIds = new(StringComparer.Ordinal);
    private long _characters;
    private Block? _currentTextBlock;
    private string? _responseId;
    private ModelStopReason _stopReason = ModelStopReason.Pending;
    private string? _rawStopReason;
    private string? _errorMessage;
    private ModelUsage _usage = new();
    private long _generatedToolCallId;

    public GoogleStreamState(
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

    public ModelResponse Partial() => BuildResponse(ModelStopReason.Pending, null);

    public IReadOnlyList<ModelStreamEvent> Apply(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "A Google stream chunk must be a JSON object.");
            EnsureUnambiguous(root);
            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidDataException("Google stream error: " + ReadError(error));
            }

            var updates = new List<ModelStreamEvent>();
            var responseId = OptionalString(root, "responseId");
            if (!string.IsNullOrWhiteSpace(responseId))
            {
                _responseId ??= responseId;
            }

            if (root.TryGetProperty("candidates", out var candidates))
            {
                RequireKind(candidates, JsonValueKind.Array, "Google candidates must be an array.");
                if (candidates.GetArrayLength() > 0)
                {
                    ApplyCandidate(candidates[0], updates);
                }
            }

            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                RequireKind(usage, JsonValueKind.Object, "Google usageMetadata must be an object.");
                ReadUsage(usage);
            }

            return updates;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Google stream contained invalid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Google stream did not match the expected response shape.", exception);
        }
    }

    public IReadOnlyList<ModelStreamEvent> CloseOpenBlock()
    {
        var updates = new List<ModelStreamEvent>();
        CloseCurrent(updates);
        return updates;
    }

    public ModelResponse Complete()
    {
        if (_currentTextBlock is not null)
        {
            throw new InvalidDataException("The Google stream completed before its final content block was closed.");
        }

        if (_stopReason == ModelStopReason.Pending)
        {
            throw new InvalidDataException("The Google stream ended without a finish reason.");
        }

        return BuildResponse(_stopReason, _errorMessage);
    }

    private void ApplyCandidate(JsonElement candidate, ICollection<ModelStreamEvent> updates)
    {
        RequireKind(candidate, JsonValueKind.Object, "A Google candidate must be an object.");
        if (candidate.TryGetProperty("content", out var content)
            && content.ValueKind != JsonValueKind.Null
            && content.TryGetProperty("parts", out var parts))
        {
            RequireKind(parts, JsonValueKind.Array, "Google candidate parts must be an array.");
            foreach (var part in parts.EnumerateArray())
            {
                ApplyPart(part, updates);
            }
        }

        var finishReason = OptionalString(candidate, "finishReason");
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            _rawStopReason = finishReason;
            _stopReason = MapStopReason(finishReason!);
            if (_blocks.Any(block => block.Kind == BlockKind.Tool))
            {
                _stopReason = ModelStopReason.ToolUse;
            }

            if (_stopReason == ModelStopReason.Error)
            {
                _errorMessage = "Provider stopped with: " + finishReason;
            }
        }
    }

    private void ApplyPart(JsonElement part, ICollection<ModelStreamEvent> updates)
    {
        RequireKind(part, JsonValueKind.Object, "A Google content part must be an object.");
        if (part.TryGetProperty("text", out var textValue))
        {
            if (textValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Google text content must be a string.");
            }

            var isReasoning = OptionalBoolean(part, "thought") == true;
            var kind = isReasoning ? BlockKind.Reasoning : BlockKind.Text;
            if (_currentTextBlock is null || _currentTextBlock.Kind != kind)
            {
                CloseCurrent(updates);
                _currentTextBlock = new Block(kind);
                _blocks.Add(_currentTextBlock);
                updates.Add(ModelStreamEvent.Update(
                    isReasoning ? ModelStreamEventKind.ReasoningStarted : ModelStreamEventKind.TextStarted,
                    Partial(),
                    contentIndex: _blocks.Count - 1));
            }

            var text = textValue.GetString() ?? string.Empty;
            Append(_currentTextBlock.Text, text);
            var signature = OptionalString(part, "thoughtSignature");
            if (!string.IsNullOrEmpty(signature))
            {
                _currentTextBlock.Signature = signature;
            }

            updates.Add(ModelStreamEvent.Update(
                isReasoning ? ModelStreamEventKind.ReasoningDelta : ModelStreamEventKind.TextDelta,
                Partial(),
                text,
                _blocks.Count - 1));
        }

        if (part.TryGetProperty("functionCall", out var functionCall))
        {
            CloseCurrent(updates);
            RequireKind(functionCall, JsonValueKind.Object, "A Google functionCall must be an object.");
            if (_blocks.Count(block => block.Kind == BlockKind.Tool) >= _maximumToolCalls)
            {
                throw new InvalidDataException("The Google response exceeded the configured tool-call limit.");
            }

            var name = RequiredString(functionCall, "name");
            var id = OptionalString(functionCall, "id");
            if (string.IsNullOrWhiteSpace(id) || !_toolCallIds.Add(id!))
            {
                do
                {
                    id = SanitizeToolCallId(name) + "_" + (++_generatedToolCallId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (!_toolCallIds.Add(id));
            }

            var arguments = "{}";
            if (functionCall.TryGetProperty("args", out var args))
            {
                RequireKind(args, JsonValueKind.Object, "Google function-call arguments must be an object.");
                arguments = args.GetRawText();
                AddCharacters(arguments.Length);
            }

            var block = new Block(BlockKind.Tool)
            {
                Id = id,
                Name = name,
                ArgumentsJson = arguments,
                Signature = OptionalString(part, "thoughtSignature"),
            };
            _blocks.Add(block);
            var contentIndex = _blocks.Count - 1;
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallStarted,
                Partial(),
                contentIndex: contentIndex,
                toolCallId: id,
                toolName: name));
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallDelta,
                Partial(),
                arguments,
                contentIndex,
                id,
                name));
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallEnded,
                Partial(),
                contentIndex: contentIndex,
                toolCallId: id,
                toolName: name));
        }
    }

    private void CloseCurrent(ICollection<ModelStreamEvent> updates)
    {
        if (_currentTextBlock is null)
        {
            return;
        }

        var block = _currentTextBlock;
        var contentIndex = _blocks.IndexOf(block);
        updates.Add(ModelStreamEvent.Update(
            block.Kind == BlockKind.Reasoning
                ? ModelStreamEventKind.ReasoningEnded
                : ModelStreamEventKind.TextEnded,
            Partial(),
            contentIndex: contentIndex));
        _currentTextBlock = null;
    }

    private ModelResponse BuildResponse(ModelStopReason reason, string? errorMessage)
    {
        var content = new List<AgentContent>(_blocks.Count);
        foreach (var block in _blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Text:
                    content.Add(new TextContent(block.Text.ToString(), block.Signature));
                    break;
                case BlockKind.Reasoning:
                    content.Add(new ReasoningContent(block.Text.ToString(), block.Signature));
                    break;
                case BlockKind.Tool:
                    content.Add(new ToolCallContent(
                        block.Id!,
                        block.Name!,
                        block.ArgumentsJson!,
                        block.Signature));
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
            _requestModel,
            _responseId,
            _rawStopReason);
    }

    private void ReadUsage(JsonElement usage)
    {
        var prompt = OptionalInt64(usage, "promptTokenCount");
        var cached = OptionalInt64(usage, "cachedContentTokenCount");
        var candidates = OptionalInt64(usage, "candidatesTokenCount");
        var thoughts = OptionalInt64(usage, "thoughtsTokenCount");
        var input = Math.Max(0, prompt - cached);
        var output = checked(candidates + thoughts);
        _usage = new ModelUsage(input, output, cached, reasoningTokens: thoughts);
    }

    private void Append(StringBuilder builder, string value)
    {
        AddCharacters(value.Length);
        builder.Append(value);
    }

    private void AddCharacters(int count)
    {
        _characters = checked(_characters + count);
        if (_characters > _maximumCharacters)
        {
            throw new InvalidDataException("The Google response exceeded the configured character limit.");
        }
    }

    private static ModelStopReason MapStopReason(string reason) => reason switch
    {
        "STOP" => ModelStopReason.Stop,
        "MAX_TOKENS" => ModelStopReason.Length,
        _ => ModelStopReason.Error,
    };

    private static string ReadError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? "Unknown error";
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            return OptionalString(error, "message") ?? error.GetRawText();
        }

        return error.GetRawText();
    }

    private static string SanitizeToolCallId(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 48));
        foreach (var character in value)
        {
            if (builder.Length >= 48)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "call" : builder.ToString();
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
                    throw new InvalidDataException("Google JSON objects cannot contain duplicate property names.");
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

    private static void RequireKind(JsonElement value, JsonValueKind kind, string message)
    {
        if (value.ValueKind != kind)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string RequiredString(JsonElement value, string property)
    {
        var result = OptionalString(value, property);
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidDataException("Google field '" + property + "' must be a non-empty string.")
            : result!;
    }

    private static string? OptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : throw new InvalidDataException("Google field '" + property + "' must be a string.");
    }

    private static bool? OptionalBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean()
            : throw new InvalidDataException("Google field '" + property + "' must be a boolean.");
    }

    private static long OptionalInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Number || !result.TryGetInt64(out var number) || number < 0)
        {
            throw new InvalidDataException("Google field '" + property + "' must be a non-negative integer.");
        }

        return number;
    }

    private enum BlockKind
    {
        Text,
        Reasoning,
        Tool,
    }

    private sealed class Block
    {
        public Block(BlockKind kind)
        {
            Kind = kind;
        }

        public BlockKind Kind { get; }

        public StringBuilder Text { get; } = new();

        public string? Signature { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? ArgumentsJson { get; set; }
    }
}
