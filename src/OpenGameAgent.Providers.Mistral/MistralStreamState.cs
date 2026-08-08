using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Mistral;

internal sealed class MistralStreamState
{
    private readonly string _requestModel;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly int _maximumCharacters;
    private readonly int _maximumToolCalls;
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<int, Block> _tools = new();
    private long _characters;
    private Block? _currentText;
    private string? _responseId;
    private string? _responseModel;
    private ModelStopReason _stopReason = ModelStopReason.Pending;
    private string? _rawStopReason;
    private string? _errorMessage;
    private ModelUsage _usage = new();

    public MistralStreamState(
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

    public ModelResponse Partial() => BuildResponse(ModelStopReason.Pending, null, final: false);

    public IReadOnlyList<ModelStreamEvent> Apply(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            RequireObject(root, "A Mistral stream chunk must be an object.");
            EnsureUnambiguous(root);
            var id = OptionalString(root, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                _responseId ??= id;
            }

            var model = OptionalString(root, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                _responseModel ??= model;
            }

            if (TryProperty(root, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                ReadUsage(usage);
            }

            var updates = new List<ModelStreamEvent>();
            if (!TryProperty(root, "choices", out var choices))
            {
                return updates;
            }

            if (choices.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Mistral choices must be an array.");
            }

            if (choices.GetArrayLength() == 0)
            {
                return updates;
            }

            var choice = choices[0];
            RequireObject(choice, "A Mistral choice must be an object.");
            var finishReason = OptionalString(choice, "finish_reason", "finishReason");
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                _rawStopReason = finishReason;
                (_stopReason, _errorMessage) = MapStopReason(finishReason!);
            }

            if (TryProperty(choice, "delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                ApplyDelta(delta, updates);
            }

            return updates;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mistral stream contained invalid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Mistral stream did not match the expected response shape.", exception);
        }
    }

    public IReadOnlyList<ModelStreamEvent> CloseOpenBlocks()
    {
        var updates = new List<ModelStreamEvent>();
        CloseCurrent(updates);
        foreach (var block in _blocks.Where(value => value.Kind == BlockKind.Tool && !value.Ended))
        {
            block.Ended = true;
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallEnded,
                Partial(),
                contentIndex: _blocks.IndexOf(block),
                toolCallId: block.Id,
                toolName: block.Name));
        }

        return updates;
    }

    public ModelResponse Complete()
    {
        if (_currentText is not null || _blocks.Any(value => value.Kind == BlockKind.Tool && !value.Ended))
        {
            throw new InvalidDataException("The Mistral stream completed before its content blocks were closed.");
        }

        if (_stopReason == ModelStopReason.Pending)
        {
            throw new InvalidDataException("The Mistral stream ended without a finish reason.");
        }

        return BuildResponse(_stopReason, _errorMessage, final: true);
    }

    private void ApplyDelta(JsonElement delta, ICollection<ModelStreamEvent> updates)
    {
        if (TryProperty(delta, "content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                AppendText(BlockKind.Text, content.GetString() ?? string.Empty, updates);
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    ApplyContentItem(item, updates);
                }
            }
            else
            {
                throw new InvalidDataException("Mistral delta content must be a string or array.");
            }
        }

        if (TryProperty(delta, "tool_calls", "toolCalls", out var toolCalls)
            && toolCalls.ValueKind != JsonValueKind.Null)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Mistral tool calls must be an array.");
            }

            foreach (var call in toolCalls.EnumerateArray())
            {
                ApplyToolCall(call, updates);
            }
        }
    }

    private void ApplyContentItem(JsonElement item, ICollection<ModelStreamEvent> updates)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            AppendText(BlockKind.Text, item.GetString() ?? string.Empty, updates);
            return;
        }

        RequireObject(item, "A Mistral content item must be an object.");
        var type = RequiredString(item, "type");
        if (type == "text")
        {
            AppendText(BlockKind.Text, OptionalString(item, "text") ?? string.Empty, updates);
        }
        else if (type == "thinking")
        {
            var builder = new StringBuilder();
            if (TryProperty(item, "thinking", out var thinking) && thinking.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in thinking.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object)
                    {
                        builder.Append(OptionalString(part, "text") ?? string.Empty);
                    }
                }
            }

            if (builder.Length > 0)
            {
                AppendText(BlockKind.Reasoning, builder.ToString(), updates);
            }
        }
    }

    private void AppendText(BlockKind kind, string text, ICollection<ModelStreamEvent> updates)
    {
        if (_currentText is null || _currentText.Kind != kind)
        {
            CloseCurrent(updates);
            _currentText = new Block(kind);
            _blocks.Add(_currentText);
            updates.Add(ModelStreamEvent.Update(
                kind == BlockKind.Reasoning ? ModelStreamEventKind.ReasoningStarted : ModelStreamEventKind.TextStarted,
                Partial(),
                contentIndex: _blocks.Count - 1));
        }

        AddCharacters(text.Length);
        _currentText.Buffer.Append(text);
        updates.Add(ModelStreamEvent.Update(
            kind == BlockKind.Reasoning ? ModelStreamEventKind.ReasoningDelta : ModelStreamEventKind.TextDelta,
            Partial(),
            text,
            _blocks.Count - 1));
    }

    private void ApplyToolCall(JsonElement call, ICollection<ModelStreamEvent> updates)
    {
        RequireObject(call, "A Mistral tool call must be an object.");
        CloseCurrent(updates);
        var index = OptionalInt32(call, "index") ?? 0;
        var id = OptionalString(call, "id");
        if (!_tools.TryGetValue(index, out var block))
        {
            if (_tools.Count >= _maximumToolCalls)
            {
                throw new InvalidDataException("The Mistral response exceeded the configured tool-call limit.");
            }

            block = new Block(BlockKind.Tool)
            {
                Id = !string.IsNullOrWhiteSpace(id) && id != "null" ? id : MistralToolCallIds.From("toolcall:" + index),
            };
            _tools.Add(index, block);
            _blocks.Add(block);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallStarted,
                Partial(),
                contentIndex: _blocks.Count - 1,
                toolCallId: block.Id));
        }

        if (!TryProperty(call, "function", out var function) || function.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var name = OptionalString(function, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            block.Name = name;
        }

        var arguments = string.Empty;
        if (TryProperty(function, "arguments", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            arguments = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }

        if (arguments.Length > 0)
        {
            AddCharacters(arguments.Length);
            block.Buffer.Append(arguments);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallDelta,
                Partial(),
                arguments,
                _blocks.IndexOf(block),
                block.Id,
                block.Name));
        }
    }

    private void CloseCurrent(ICollection<ModelStreamEvent> updates)
    {
        if (_currentText is null)
        {
            return;
        }

        var block = _currentText;
        updates.Add(ModelStreamEvent.Update(
            block.Kind == BlockKind.Reasoning ? ModelStreamEventKind.ReasoningEnded : ModelStreamEventKind.TextEnded,
            Partial(),
            contentIndex: _blocks.IndexOf(block)));
        _currentText = null;
    }

    private ModelResponse BuildResponse(ModelStopReason reason, string? errorMessage, bool final)
    {
        var content = new List<AgentContent>();
        foreach (var block in _blocks)
        {
            if (block.Kind == BlockKind.Text)
            {
                content.Add(new TextContent(block.Buffer.ToString()));
            }
            else if (block.Kind == BlockKind.Reasoning)
            {
                content.Add(new ReasoningContent(block.Buffer.ToString()));
            }
            else
            {
                var arguments = TryJsonObject(block.Buffer.ToString(), out var normalized) ? normalized : "{}";
                if (final && !TryJsonObject(block.Buffer.ToString(), out arguments) && reason != ModelStopReason.Length)
                {
                    throw new InvalidDataException("A completed Mistral tool call did not contain a JSON object.");
                }

                content.Add(new ToolCallContent(
                    block.Id!,
                    string.IsNullOrWhiteSpace(block.Name) ? "unknown_tool" : block.Name!,
                    arguments));
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

    private void ReadUsage(JsonElement usage)
    {
        var prompt = OptionalInt64(usage, "prompt_tokens", "promptTokens");
        var completion = OptionalInt64(usage, "completion_tokens", "completionTokens");
        var cached = ReadCachedTokens(usage);
        _usage = new ModelUsage(Math.Max(0, prompt - cached), completion, cached);
    }

    private static long ReadCachedTokens(JsonElement usage)
    {
        foreach (var name in new[] { "num_cached_tokens", "numCachedTokens" })
        {
            if (TryProperty(usage, name, out var direct) && direct.TryGetInt64(out var value))
            {
                return Math.Max(0, value);
            }
        }

        foreach (var name in new[] { "prompt_tokens_details", "promptTokensDetails", "prompt_token_details", "promptTokenDetails" })
        {
            if (TryProperty(usage, name, out var details) && details.ValueKind == JsonValueKind.Object)
            {
                var value = OptionalInt64(details, "cached_tokens", "cachedTokens");
                return Math.Max(0, value);
            }
        }

        return 0;
    }

    private void AddCharacters(int count)
    {
        _characters = checked(_characters + count);
        if (_characters > _maximumCharacters)
        {
            throw new InvalidDataException("The Mistral response exceeded the configured character limit.");
        }
    }

    private static (ModelStopReason Reason, string? Error) MapStopReason(string reason) => reason switch
    {
        "stop" => (ModelStopReason.Stop, null),
        "length" or "model_length" => (ModelStopReason.Length, null),
        "tool_calls" => (ModelStopReason.ToolUse, null),
        _ => (ModelStopReason.Error, "Provider stopped with: " + reason),
    };

    private static bool TryJsonObject(string json, out string normalized)
    {
        normalized = "{}";
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            normalized = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
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
                    throw new InvalidDataException("Mistral JSON objects cannot contain duplicate property names.");
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

    private static void RequireObject(JsonElement value, string message)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var result = OptionalString(value, name);
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidDataException("Mistral field '" + name + "' must be a non-empty string.")
            : result!;
    }

    private static string? OptionalString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(value, name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : throw new InvalidDataException("Mistral field '" + name + "' must be a string.");
        }

        return null;
    }

    private static int? OptionalInt32(JsonElement value, string name)
    {
        if (!TryProperty(value, name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result) && result >= 0
            ? result
            : throw new InvalidDataException("Mistral field '" + name + "' must be a non-negative integer.");
    }

    private static long OptionalInt64(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(value, name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result) && result >= 0
                ? result
                : throw new InvalidDataException("Mistral field '" + name + "' must be a non-negative integer.");
        }

        return 0;
    }

    private static bool TryProperty(JsonElement value, string name, out JsonElement property) =>
        value.TryGetProperty(name, out property);

    private static bool TryProperty(JsonElement value, string first, string second, out JsonElement property) =>
        value.TryGetProperty(first, out property) || value.TryGetProperty(second, out property);

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

        public StringBuilder Buffer { get; } = new();

        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool Ended { get; set; }
    }
}
