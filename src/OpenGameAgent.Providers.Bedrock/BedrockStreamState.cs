using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Bedrock;

internal sealed class BedrockStreamState
{
    private readonly string _model;
    private readonly string _provider;
    private readonly string _api;
    private readonly int _maximumCharacters;
    private readonly int _maximumToolCalls;
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<int, Block> _byProtocolIndex = new();
    private long _characters;
    private ModelUsage _usage = new();
    private ModelStopReason _stopReason = ModelStopReason.Pending;
    private string? _rawStopReason;
    private string? _errorMessage;
    private bool _messageStarted;
    private bool _messageStopped;

    public BedrockStreamState(string model, string provider, string api, int maximumCharacters, int maximumToolCalls)
    {
        _model = model;
        _provider = provider;
        _api = api;
        _maximumCharacters = maximumCharacters;
        _maximumToolCalls = maximumToolCalls;
    }

    public ModelResponse Partial() => Build(ModelStopReason.Pending, null, final: false);

    public IReadOnlyList<ModelStreamEvent> Apply(BedrockProtocolEvent item)
    {
        var updates = new List<ModelStreamEvent>();
        switch (item.Kind)
        {
            case BedrockProtocolEventKind.MessageStarted:
                if (_messageStarted || !string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Bedrock started an invalid assistant message.");
                }

                _messageStarted = true;
                break;
            case BedrockProtocolEventKind.ContentStarted:
                RequireActiveMessage();
                if (_byProtocolIndex.ContainsKey(item.ContentIndex))
                {
                    throw new InvalidDataException("Bedrock started a duplicate content block.");
                }

                if (!string.IsNullOrWhiteSpace(item.ToolCallId) || !string.IsNullOrWhiteSpace(item.ToolName))
                {
                    if (string.IsNullOrWhiteSpace(item.ToolCallId) || string.IsNullOrWhiteSpace(item.ToolName))
                    {
                        throw new InvalidDataException("A Bedrock tool block requires both ID and name.");
                    }

                    if (_blocks.Count(value => value.Kind == BlockKind.Tool) >= _maximumToolCalls)
                    {
                        throw new InvalidDataException("The Bedrock response exceeded the configured tool-call limit.");
                    }

                    var block = new Block(BlockKind.Tool, item.ContentIndex)
                    {
                        Id = item.ToolCallId,
                        Name = item.ToolName,
                    };
                    _blocks.Add(block);
                    _byProtocolIndex.Add(item.ContentIndex, block);
                    updates.Add(ModelStreamEvent.Update(
                        ModelStreamEventKind.ToolCallStarted,
                        Partial(),
                        contentIndex: _blocks.Count - 1,
                        toolCallId: block.Id,
                        toolName: block.Name));
                }

                break;
            case BedrockProtocolEventKind.ContentDelta:
                RequireActiveMessage();
                ApplyDelta(item, updates);
                break;
            case BedrockProtocolEventKind.ContentStopped:
                RequireActiveMessage();
                StopBlock(item.ContentIndex, updates);
                break;
            case BedrockProtocolEventKind.MessageStopped:
                RequireActiveMessage();
                if (_messageStopped)
                {
                    throw new InvalidDataException("Bedrock stopped the message more than once.");
                }

                _rawStopReason = item.StopReason;
                (_stopReason, _errorMessage) = MapStopReason(item.StopReason!);
                _messageStopped = true;
                break;
            case BedrockProtocolEventKind.Metadata:
                _usage = new ModelUsage(
                    item.InputTokens,
                    item.OutputTokens,
                    item.CacheReadTokens,
                    item.CacheWriteTokens);
                break;
            default:
                throw new InvalidDataException("Bedrock returned an unsupported protocol event.");
        }

        return updates;
    }

    public ModelResponse Complete()
    {
        if (!_messageStarted || !_messageStopped)
        {
            throw new InvalidDataException("The Bedrock stream ended before message_stop.");
        }

        if (_blocks.Any(value => !value.Ended))
        {
            throw new InvalidDataException("The Bedrock stream ended with an incomplete content block.");
        }

        return Build(_stopReason, _errorMessage, final: true);
    }

    private void ApplyDelta(BedrockProtocolEvent item, ICollection<ModelStreamEvent> updates)
    {
        if (item.Text is not null)
        {
            var block = GetOrCreate(item.ContentIndex, BlockKind.Text, updates);
            Append(block.Buffer, item.Text);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                Partial(),
                item.Text,
                _blocks.IndexOf(block)));
            return;
        }

        if (item.ToolArgumentsDelta is not null)
        {
            if (!_byProtocolIndex.TryGetValue(item.ContentIndex, out var block) || block.Kind != BlockKind.Tool)
            {
                throw new InvalidDataException("Bedrock streamed tool arguments for a missing tool block.");
            }

            Append(block.Buffer, item.ToolArgumentsDelta);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallDelta,
                Partial(),
                item.ToolArgumentsDelta,
                _blocks.IndexOf(block),
                block.Id,
                block.Name));
            return;
        }

        if (item.ReasoningText is not null || item.ReasoningSignature is not null)
        {
            var block = GetOrCreate(item.ContentIndex, BlockKind.Reasoning, updates);
            if (item.ReasoningText is { } reasoning)
            {
                Append(block.Buffer, reasoning);
                updates.Add(ModelStreamEvent.Update(
                    ModelStreamEventKind.ReasoningDelta,
                    Partial(),
                    reasoning,
                    _blocks.IndexOf(block)));
            }

            if (item.ReasoningSignature is { } signature)
            {
                Append(block.Signature, signature);
            }
        }
    }

    private Block GetOrCreate(int protocolIndex, BlockKind kind, ICollection<ModelStreamEvent> updates)
    {
        if (_byProtocolIndex.TryGetValue(protocolIndex, out var existing))
        {
            if (existing.Kind != kind)
            {
                throw new InvalidDataException("Bedrock changed a content block's type while streaming.");
            }

            return existing;
        }

        var block = new Block(kind, protocolIndex);
        _blocks.Add(block);
        _byProtocolIndex.Add(protocolIndex, block);
        updates.Add(ModelStreamEvent.Update(
            kind == BlockKind.Text ? ModelStreamEventKind.TextStarted : ModelStreamEventKind.ReasoningStarted,
            Partial(),
            contentIndex: _blocks.Count - 1));
        return block;
    }

    private void StopBlock(int protocolIndex, ICollection<ModelStreamEvent> updates)
    {
        if (!_byProtocolIndex.TryGetValue(protocolIndex, out var block) || block.Ended)
        {
            throw new InvalidDataException("Bedrock stopped a missing or already stopped content block.");
        }

        if (block.Kind == BlockKind.Tool && !IsJsonObject(block.Buffer.ToString()))
        {
            throw new InvalidDataException("A completed Bedrock tool call did not contain a JSON object.");
        }

        block.Ended = true;
        var kind = block.Kind switch
        {
            BlockKind.Text => ModelStreamEventKind.TextEnded,
            BlockKind.Reasoning => ModelStreamEventKind.ReasoningEnded,
            _ => ModelStreamEventKind.ToolCallEnded,
        };
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            contentIndex: _blocks.IndexOf(block),
            toolCallId: block.Id,
            toolName: block.Name));
    }

    private ModelResponse Build(ModelStopReason reason, string? errorMessage, bool final)
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
                content.Add(new ReasoningContent(block.Buffer.ToString(), block.Signature.ToString()));
            }
            else
            {
                var streamedArguments = block.Buffer.ToString();
                var arguments = string.IsNullOrWhiteSpace(streamedArguments)
                    ? "{}"
                    : IsJsonObject(streamedArguments) ? streamedArguments : "{}";
                if (final && arguments == "{}" && !string.IsNullOrWhiteSpace(streamedArguments))
                {
                    throw new InvalidDataException("A completed Bedrock tool call did not contain a JSON object.");
                }

                content.Add(new ToolCallContent(block.Id!, block.Name!, arguments));
            }
        }

        return new ModelResponse(
            content,
            reason,
            _usage,
            errorMessage,
            _provider,
            _api,
            _model,
            rawStopReason: _rawStopReason);
    }

    private void RequireActiveMessage()
    {
        if (!_messageStarted || _messageStopped)
        {
            throw new InvalidDataException("A Bedrock content event arrived outside an active message.");
        }
    }

    private void Append(StringBuilder builder, string value)
    {
        _characters = checked(_characters + value.Length);
        if (_characters > _maximumCharacters)
        {
            throw new InvalidDataException("The Bedrock response exceeded the configured character limit.");
        }

        builder.Append(value);
    }

    private static bool IsJsonObject(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

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

    private static (ModelStopReason Reason, string? Error) MapStopReason(string reason) => reason switch
    {
        "end_turn" or "stop_sequence" => (ModelStopReason.Stop, null),
        "max_tokens" or "model_context_window_exceeded" => (ModelStopReason.Length, null),
        "tool_use" => (ModelStopReason.ToolUse, null),
        _ => (ModelStopReason.Error, "Provider stopped with: " + reason),
    };

    private enum BlockKind
    {
        Text,
        Reasoning,
        Tool,
    }

    private sealed class Block
    {
        public Block(BlockKind kind, int protocolIndex)
        {
            Kind = kind;
            ProtocolIndex = protocolIndex;
        }

        public BlockKind Kind { get; }

        public int ProtocolIndex { get; }

        public StringBuilder Buffer { get; } = new();

        public StringBuilder Signature { get; } = new();

        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool Ended { get; set; }
    }
}
