using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.MessageGateway;

internal sealed class ProjectedMessageGatewayRequest
{
    public ProjectedMessageGatewayRequest(byte[] payload, bool debug)
    {
        Payload = payload;
        Debug = debug;
    }

    public byte[] Payload { get; }

    public bool Debug { get; }
}

internal static class MessageGatewayWire
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public static ProjectedMessageGatewayRequest ProjectRequest(
        ModelRequest request,
        MessageGatewaySettings settings)
    {
        if (request.Parameters.Deferred)
        {
            throw new NotSupportedException("The message gateway does not support deferred responses.");
        }

        var normalizedMessages = ProviderTranscript.Normalize(
            request.Messages,
            settings.ProviderId,
            settings.ApiId,
            request.Model);
        var context = new Dictionary<string, object?>
        {
            ["messages"] = normalizedMessages.Select(message => ProjectMessage(message, settings, request.Model)).ToArray(),
        };
        if (request.SystemPrompt.Length > 0)
        {
            context["systemPrompt"] = request.SystemPrompt;
        }

        if (request.Tools.Count > 0)
        {
            context["tools"] = request.Tools.Select(ProjectTool).ToArray();
        }

        var options = ProjectOptions(request, settings);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["context"] = context,
            ["options"] = options,
        };
        var bytes = SerializeBounded(payload, settings.MaxRequestBytes);
        return new ProjectedMessageGatewayRequest(bytes, ResolveDebug(request, settings));
    }

    private static object ProjectMessage(
        AgentMessage message,
        MessageGatewaySettings settings,
        string targetModel) => message.Role switch
        {
            AgentRole.Assistant => ProjectAssistant(message, settings, targetModel),
            AgentRole.Tool => ProjectToolResult(message),
            AgentRole.Custom => ProjectUser(message, "[role:" + SanitizeRole(message.CustomRole!) + "]"),
            _ => ProjectUser(message, null),
        };

    private static object ProjectUser(AgentMessage message, string? prefix)
    {
        var content = ProjectTextAndImages(message.Content, allowImages: true).ToList();
        if (prefix is not null)
        {
            content.Insert(0, new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = prefix,
            });
        }

        object wireContent = content.Count == 1
                             && content[0] is Dictionary<string, object?> single
                             && single.Count == 2
                             && string.Equals(single["type"] as string, "text", StringComparison.Ordinal)
            ? single["text"]!
            : content.ToArray();
        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = wireContent,
            ["timestamp"] = message.Timestamp.ToUnixTimeMilliseconds(),
        };
    }

    private static object ProjectAssistant(
        AgentMessage message,
        MessageGatewaySettings settings,
        string targetModel)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = message.Content.Select(ProjectAssistantContent).ToArray(),
            ["api"] = message.Api ?? settings.ApiId,
            ["provider"] = message.Provider ?? settings.ProviderId,
            ["model"] = message.Model ?? targetModel,
            ["usage"] = ProjectUsage(message.Usage ?? new ModelUsage()),
            ["stopReason"] = StopReason(message.StopReason ?? ModelStopReason.Stop),
            ["timestamp"] = message.Timestamp.ToUnixTimeMilliseconds(),
        };
        Add(result, "responseModel", message.ResponseModel);
        Add(result, "responseId", message.ResponseId);
        Add(result, "errorMessage", message.ErrorMessage);
        Add(result, "rawStopReason", message.RawStopReason);
        if (message.EndTurn is { } endTurn)
        {
            result["endTurn"] = endTurn;
        }

        if (message.Deferred is { } deferred)
        {
            result["deferred"] = ProjectDeferred(deferred);
        }

        return result;
    }

    private static object ProjectToolResult(AgentMessage message)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = "toolResult",
            ["toolCallId"] = message.ToolCallId,
            ["toolName"] = message.ToolName,
            ["content"] = ProjectTextAndImages(message.Content, allowImages: true).ToArray(),
            ["isError"] = message.IsError,
            ["timestamp"] = message.Timestamp.ToUnixTimeMilliseconds(),
        };
        if (message.DetailsJson is not null)
        {
            result["details"] = ParseElement(message.DetailsJson);
        }

        if (message.Usage is not null)
        {
            result["usage"] = ProjectUsage(message.Usage);
        }

        if (message.AddedToolNames.Count > 0)
        {
            result["addedToolNames"] = message.AddedToolNames;
        }

        return result;
    }

    private static object ProjectAssistantContent(AgentContent content) => content switch
    {
        TextContent text => ProjectText(text),
        ReasoningContent reasoning => ProjectReasoning(reasoning),
        ToolCallContent call => ProjectToolCall(call),
        JsonContent json => TextPart(json.Json),
        BinaryContent binary => TextPart(UnsupportedAssistantPlaceholder(binary.MediaKind)),
        ResourceContent => TextPart("[resource omitted: unsupported assistant content]"),
        _ => TextPart("[content omitted: unsupported assistant content]"),
    };

    private static IEnumerable<object> ProjectTextAndImages(
        IEnumerable<AgentContent> content,
        bool allowImages)
    {
        foreach (var part in content)
        {
            switch (part)
            {
                case TextContent text:
                    yield return ProjectText(text);
                    break;
                case JsonContent json:
                    yield return TextPart(json.Json);
                    break;
                case BinaryContent binary when allowImages && binary.MediaKind == AgentMediaKind.Image:
                    yield return new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["data"] = binary.Data,
                        ["mimeType"] = binary.MediaType,
                    };
                    break;
                case BinaryContent binary:
                    yield return TextPart(UnsupportedInputPlaceholder(binary.MediaKind));
                    break;
                case ResourceContent:
                    yield return TextPart("[resource omitted: inline data required]");
                    break;
                default:
                    yield return TextPart("[content omitted: unsupported message content]");
                    break;
            }
        }
    }

    private static object ProjectText(TextContent text)
    {
        var result = TextPart(text.Text);
        Add(result, "textSignature", text.Signature);
        return result;
    }

    private static object ProjectReasoning(ReasoningContent reasoning)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = "thinking",
            ["thinking"] = reasoning.Text,
        };
        Add(result, "thinkingSignature", reasoning.Signature);
        if (reasoning.Redacted)
        {
            result["redacted"] = true;
        }

        return result;
    }

    private static object ProjectToolCall(ToolCallContent call)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = "toolCall",
            ["id"] = call.Id,
            ["name"] = call.Name,
            ["arguments"] = ParseElement(call.ArgumentsJson),
        };
        Add(result, "thoughtSignature", call.ThoughtSignature);
        Add(result, "namespace", call.Namespace);
        return result;
    }

    private static object ProjectTool(ToolDefinition tool)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = ParseElement(tool.InputSchemaJson),
        };
        if (tool.ConstrainedSampling is { } constrained)
        {
            result["constrainedSampling"] = constrained.Kind switch
            {
                ToolConstrainedSamplingKind.JsonSchema => new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["strict"] = constrained.Strictness == ToolSchemaStrictness.Require ? "require" : "prefer",
                },
                ToolConstrainedSamplingKind.Grammar => new Dictionary<string, object?>
                {
                    ["type"] = "grammar",
                    ["variants"] = GrammarVariants(constrained),
                },
                _ => throw new InvalidOperationException("The tool uses an unsupported constrained-sampling mode."),
            };
        }

        return result;
    }

    private static object GrammarVariants(ToolConstrainedSampling constrained)
    {
        var variants = new Dictionary<string, string>();
        if (constrained.OpenAiLark is not null)
        {
            variants["openai_lark"] = constrained.OpenAiLark;
        }

        if (constrained.OpenAiRegex is not null)
        {
            variants["openai_regex"] = constrained.OpenAiRegex;
        }

        return variants;
    }

    private static Dictionary<string, object?> ProjectOptions(
        ModelRequest request,
        MessageGatewaySettings settings)
    {
        var result = new Dictionary<string, object?>();
        if (request.Parameters.Temperature is { } temperature)
        {
            if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature is < 0 or > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Temperature must be between zero and two.");
            }

            result["temperature"] = temperature;
        }

        if (request.Parameters.MaxOutputTokens is { } maxTokens)
        {
            if (maxTokens < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Maximum output tokens must be positive.");
            }

            result["maxTokens"] = maxTokens;
        }

        if (request.Parameters.ReasoningLevel is { } reasoning)
        {
            var normalized = reasoning.ToLowerInvariant();
            if (normalized is not ("minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            {
                throw new InvalidOperationException("The message gateway reasoning level is invalid.");
            }

            result["reasoning"] = normalized;
        }

        result["cacheRetention"] = request.Parameters.CacheRetention switch
        {
            ModelCacheRetention.None => "none",
            ModelCacheRetention.Short => "short",
            ModelCacheRetention.Long => "long",
            _ => throw new InvalidOperationException("The message gateway cache-retention value is invalid."),
        };
        Add(result, "sessionId", request.SessionId);
        var toolChoice = ResolveToolChoice(request, settings);
        if (toolChoice is not null)
        {
            result["toolChoice"] = toolChoice;
        }

        return result;
    }

    private static object? ResolveToolChoice(ModelRequest request, MessageGatewaySettings settings)
    {
        if (request.Parameters.Extensions.TryGetValue(MessageGatewayParameterKeys.ToolChoice, out var requested))
        {
            return ParseToolChoice(requested, request.Tools);
        }

        return settings.ToolChoice switch
        {
            null => null,
            MessageGatewayToolChoiceMode.Auto => "auto",
            MessageGatewayToolChoiceMode.None => "none",
            MessageGatewayToolChoiceMode.Required => "required",
            MessageGatewayToolChoiceMode.Function => FunctionToolChoice(
                MessageGatewaySettings.RequireToolName(settings.ToolName, nameof(settings.ToolName)),
                request.Tools),
            _ => throw new InvalidOperationException("The message gateway tool choice is invalid."),
        };
    }

    private static object ParseToolChoice(string value, IReadOnlyList<ToolDefinition> tools)
    {
        if (value is "auto" or "none" or "required")
        {
            return value;
        }

        const string prefix = "function:";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return FunctionToolChoice(
                MessageGatewaySettings.RequireToolName(value.Substring(prefix.Length), nameof(value)),
                tools);
        }

        throw new InvalidOperationException("The message gateway tool-choice extension is invalid.");
    }

    private static object FunctionToolChoice(string name, IReadOnlyList<ToolDefinition> tools)
    {
        if (!tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected message gateway tool is not present in the request.");
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?> { ["name"] = name },
        };
    }

    private static bool ResolveDebug(ModelRequest request, MessageGatewaySettings settings)
    {
        if (!request.Parameters.Extensions.TryGetValue(MessageGatewayParameterKeys.Debug, out var value))
        {
            return settings.Debug;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException("The message gateway debug extension must be true or false.");
    }

    private static object ProjectUsage(ModelUsage usage)
    {
        var result = new Dictionary<string, object?>
        {
            ["input"] = usage.InputTokens,
            ["output"] = usage.OutputTokens,
            ["cacheRead"] = usage.CacheReadTokens,
            ["cacheWrite"] = usage.CacheWriteTokens,
            ["totalTokens"] = usage.TotalTokens,
            ["cost"] = new Dictionary<string, object?>
            {
                ["input"] = usage.Cost.Input,
                ["output"] = usage.Cost.Output,
                ["cacheRead"] = usage.Cost.CacheRead,
                ["cacheWrite"] = usage.Cost.CacheWrite,
                ["total"] = usage.Cost.Total,
            },
        };
        if (usage.CacheWriteOneHourTokens is { } cacheWriteOneHour)
        {
            result["cacheWrite1h"] = cacheWriteOneHour;
        }

        if (usage.ReasoningTokens is { } reasoning)
        {
            result["reasoning"] = reasoning;
        }

        return result;
    }

    private static object ProjectDeferred(DeferredModelHandle deferred)
    {
        var result = new Dictionary<string, object?>
        {
            ["provider"] = deferred.Provider,
            ["modelId"] = deferred.Model,
            ["api"] = deferred.Api,
            ["id"] = deferred.Id,
        };
        if (deferred.ExpiresAt is { } expiresAt)
        {
            result["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        }

        if (deferred.PollAfterMilliseconds is { } pollAfter)
        {
            result["pollAfterMs"] = pollAfter;
        }

        if (deferred.DataJson is not null)
        {
            result["data"] = ParseElement(deferred.DataJson);
        }

        return result;
    }

    private static Dictionary<string, object?> TextPart(string value) => new()
    {
        ["type"] = "text",
        ["text"] = value,
    };

    private static string UnsupportedInputPlaceholder(AgentMediaKind kind) =>
        "[" + kind.ToString().ToLowerInvariant() + " omitted: message gateway supports only text and images]";

    private static string UnsupportedAssistantPlaceholder(AgentMediaKind kind) =>
        "[" + kind.ToString().ToLowerInvariant() + " omitted: unsupported assistant content]";

    private static string SanitizeRole(string role)
    {
        var builder = new StringBuilder(Math.Min(role.Length, 128));
        foreach (var character in role)
        {
            if (builder.Length >= 128)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private static string StopReason(ModelStopReason reason) => reason switch
    {
        ModelStopReason.Pending => "pending",
        ModelStopReason.Stop => "stop",
        ModelStopReason.ToolUse => "toolUse",
        ModelStopReason.Length => "length",
        ModelStopReason.Error => "error",
        ModelStopReason.Aborted => "aborted",
        ModelStopReason.Deferred => "deferred",
        _ => throw new InvalidOperationException("The message stop reason is invalid."),
    };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Add(IDictionary<string, object?> target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static byte[] SerializeBounded(object payload, int maximumBytes)
    {
        using var memory = new MemoryStream();
        using var bounded = new BoundedWriteStream(memory, maximumBytes);
        using (var writer = new Utf8JsonWriter(bounded))
        {
            JsonSerializer.Serialize(writer, payload, JsonOptions);
            writer.Flush();
        }

        return memory.ToArray();
    }

    private sealed class BoundedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _written;

        public BoundedWriteStream(Stream inner, long maximumBytes)
        {
            _inner = inner;
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
            _written += count;
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            _written += count;
        }

        private void EnsureCapacity(int count)
        {
            if (_written + count > _maximumBytes)
            {
                throw new InvalidDataException("The message gateway request exceeded its size limit.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
            }

            base.Dispose(disposing);
        }
    }
}

internal static class MessageGatewayJson
{
    public static JsonDocument Parse(string json, int maximumDepth)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = maximumDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        try
        {
            EnsureUnambiguous(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static string RequiredString(JsonElement value, string property, int maximumCharacters)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } result
            || string.IsNullOrWhiteSpace(result)
            || result.Length > maximumCharacters
            || result.Any(char.IsControl))
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return result;
    }

    public static string? OptionalString(JsonElement value, string property, int maximumCharacters)
    {
        if (!value.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } result
            || result.Length > maximumCharacters
            || result.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return result;
    }

    public static int RequiredIndex(JsonElement value, string property, int maximumExclusive)
    {
        if (!value.TryGetProperty(property, out var element)
            || !element.TryGetInt32(out var result)
            || result < 0
            || result >= maximumExclusive)
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return result;
    }

    public static long RequiredInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || !element.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return result;
    }

    public static bool RequiredBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return element.GetBoolean();
    }

    public static bool? OptionalBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return element.GetBoolean();
    }

    public static double RequiredDouble(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || !element.TryGetDouble(out var result)
            || double.IsNaN(result)
            || double.IsInfinity(result)
            || result < 0)
        {
            throw new InvalidDataException($"The message gateway event field '{property}' is invalid.");
        }

        return result;
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
                    throw new InvalidDataException("A message gateway JSON object contains duplicate property names.");
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
}

internal sealed class MessageGatewayStreamState
{
    private readonly ModelRequest _request;
    private readonly MessageGatewaySettings _settings;
    private readonly MessageGatewaySecretRedactor _redactor;
    private readonly List<AgentContent> _content = new();
    private readonly Dictionary<int, StringBuilder> _text = new();
    private readonly Dictionary<int, StringBuilder> _reasoning = new();
    private readonly Dictionary<int, StringBuilder> _toolArguments = new();
    private bool _started;
    private bool _terminal;
    private int _contentCharacters;
    private int _toolCalls;
    private long _partialSnapshotWork;

    public MessageGatewayStreamState(
        ModelRequest request,
        MessageGatewaySettings settings,
        MessageGatewaySecretRedactor redactor)
    {
        _request = request;
        _settings = settings;
        _redactor = redactor;
    }

    public ModelStreamEvent Apply(string json)
    {
        using var document = MessageGatewayJson.Parse(json, _settings.MaxJsonDepth);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A message gateway event must be a JSON object.");
        }

        var type = MessageGatewayJson.RequiredString(root, "type", 64);
        if (_terminal)
        {
            throw new InvalidDataException("The message gateway emitted an event after its terminal event.");
        }

        return type switch
        {
            "start" => Start(),
            "text_start" => TextStart(root),
            "text_delta" => TextDelta(root),
            "text_end" => TextEnd(root),
            "thinking_start" => ReasoningStart(root),
            "thinking_delta" => ReasoningDelta(root),
            "thinking_end" => ReasoningEnd(root),
            "toolcall_start" => ToolStart(root),
            "toolcall_delta" => ToolDelta(root),
            "toolcall_end" => ToolEnd(root),
            "done" => Terminal(root, isError: false),
            "error" => Terminal(root, isError: true),
            _ => throw new InvalidDataException(
                $"Unknown message gateway event type '{_redactor.Sanitize(type, 64)}'."),
        };
    }

    public void EnsureComplete()
    {
        if (!_terminal)
        {
            throw new InvalidDataException("The message gateway stream ended without a terminal event.");
        }
    }

    private ModelStreamEvent Start()
    {
        if (_started)
        {
            throw new InvalidDataException("The message gateway emitted more than one start event.");
        }

        _started = true;
        return ModelStreamEvent.Update(ModelStreamEventKind.Started, Partial());
    }

    private ModelStreamEvent TextStart(JsonElement root)
    {
        RequireStarted();
        var index = AppendIndex(root);
        _text.Add(index, new StringBuilder());
        _content.Add(new TextContent(string.Empty));
        return ModelStreamEvent.Update(ModelStreamEventKind.TextStarted, Partial(), contentIndex: index);
    }

    private ModelStreamEvent TextDelta(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_text.TryGetValue(index, out var buffer))
        {
            throw new InvalidDataException("A message gateway text delta has no active text block.");
        }

        var delta = MessageGatewayJson.OptionalString(root, "delta", _settings.MaxContentCharacters)
                    ?? throw new InvalidDataException("A message gateway text delta is missing content.");
        AppendContent(buffer, delta);
        AddPartialSnapshotWork(buffer.Length);
        _content[index] = new TextContent(buffer.ToString());
        return ModelStreamEvent.Update(
            ModelStreamEventKind.TextDelta,
            Partial(),
            delta,
            index);
    }

    private ModelStreamEvent TextEnd(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_text.Remove(index, out var buffer))
        {
            throw new InvalidDataException("A message gateway text end has no active text block.");
        }

        var content = MessageGatewayJson.OptionalString(root, "content", _settings.MaxContentCharacters)
                      ?? throw new InvalidDataException("A message gateway text end is missing content.");
        ReconcileFinalContent(buffer, content);
        var signature = MessageGatewayJson.OptionalString(root, "contentSignature", 1_000_000);
        _content[index] = new TextContent(content, signature);
        return ModelStreamEvent.Update(
            ModelStreamEventKind.TextEnded,
            Partial(),
            contentIndex: index,
            content: content);
    }

    private ModelStreamEvent ReasoningStart(JsonElement root)
    {
        RequireStarted();
        var index = AppendIndex(root);
        _reasoning.Add(index, new StringBuilder());
        _content.Add(new ReasoningContent(string.Empty));
        return ModelStreamEvent.Update(ModelStreamEventKind.ReasoningStarted, Partial(), contentIndex: index);
    }

    private ModelStreamEvent ReasoningDelta(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_reasoning.TryGetValue(index, out var buffer))
        {
            throw new InvalidDataException("A message gateway reasoning delta has no active reasoning block.");
        }

        var delta = MessageGatewayJson.OptionalString(root, "delta", _settings.MaxContentCharacters)
                    ?? throw new InvalidDataException("A message gateway reasoning delta is missing content.");
        AppendContent(buffer, delta);
        AddPartialSnapshotWork(buffer.Length);
        _content[index] = new ReasoningContent(buffer.ToString());
        return ModelStreamEvent.Update(
            ModelStreamEventKind.ReasoningDelta,
            Partial(),
            delta,
            index);
    }

    private ModelStreamEvent ReasoningEnd(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_reasoning.Remove(index, out var buffer))
        {
            throw new InvalidDataException("A message gateway reasoning end has no active reasoning block.");
        }

        var content = MessageGatewayJson.OptionalString(root, "content", _settings.MaxContentCharacters)
                      ?? throw new InvalidDataException("A message gateway reasoning end is missing content.");
        ReconcileFinalContent(buffer, content);
        var signature = MessageGatewayJson.OptionalString(root, "contentSignature", 1_000_000);
        var redacted = MessageGatewayJson.OptionalBoolean(root, "redacted") ?? false;
        _content[index] = new ReasoningContent(content, signature, redacted);
        return ModelStreamEvent.Update(
            ModelStreamEventKind.ReasoningEnded,
            Partial(),
            contentIndex: index,
            content: content);
    }

    private ModelStreamEvent ToolStart(JsonElement root)
    {
        RequireStarted();
        if (++_toolCalls > _settings.MaxToolCalls)
        {
            throw new InvalidDataException("The message gateway exceeded its tool-call limit.");
        }

        var index = AppendIndex(root);
        var id = MessageGatewayJson.RequiredString(root, "id", 1_024);
        var name = MessageGatewayJson.RequiredString(root, "toolName", 256);
        _toolArguments.Add(index, new StringBuilder());
        _content.Add(new ToolCallContent(id, name, "{}"));
        return ModelStreamEvent.Update(
            ModelStreamEventKind.ToolCallStarted,
            Partial(),
            contentIndex: index,
            toolCallId: id,
            toolName: name);
    }

    private ModelStreamEvent ToolDelta(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_toolArguments.TryGetValue(index, out var buffer)
            || _content[index] is not ToolCallContent current)
        {
            throw new InvalidDataException("A message gateway tool delta has no active tool call.");
        }

        var delta = MessageGatewayJson.OptionalString(root, "delta", _settings.MaxContentCharacters)
                    ?? throw new InvalidDataException("A message gateway tool delta is missing content.");
        AppendContent(buffer, delta);
        AddPartialSnapshotWork(buffer.Length);
        if (TryCanonicalObject(buffer.ToString(), out var arguments))
        {
            _content[index] = new ToolCallContent(current.Id, current.Name, arguments);
        }

        return ModelStreamEvent.Update(
            ModelStreamEventKind.ToolCallDelta,
            Partial(),
            delta,
            index,
            current.Id,
            current.Name);
    }

    private ModelStreamEvent ToolEnd(JsonElement root)
    {
        RequireStarted();
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (!_toolArguments.Remove(index, out var buffer)
            || _content[index] is not ToolCallContent current)
        {
            throw new InvalidDataException("A message gateway tool end has no active tool call.");
        }

        if (!root.TryGetProperty("toolCall", out var toolCall) || toolCall.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A message gateway tool end is missing its tool call.");
        }

        var id = MessageGatewayJson.RequiredString(toolCall, "id", 1_024);
        var name = MessageGatewayJson.RequiredString(toolCall, "name", 256);
        if (!string.Equals(id, current.Id, StringComparison.Ordinal)
            || !string.Equals(name, current.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A message gateway tool end does not match its start event.");
        }

        if (!toolCall.TryGetProperty("arguments", out var argumentElement)
            || argumentElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A message gateway tool call requires object arguments.");
        }

        var arguments = JsonSerializer.Serialize(argumentElement);
        if (buffer.Length > 0)
        {
            AddPartialSnapshotWork(buffer.Length);
            if (!JsonObjectsEquivalent(buffer.ToString(), argumentElement))
            {
                throw new InvalidDataException("A message gateway tool-call stream does not match its final arguments.");
            }
        }
        else
        {
            AddContentCharacters(arguments.Length);
        }

        var thoughtSignature = MessageGatewayJson.OptionalString(toolCall, "thoughtSignature", 1_000_000);
        var toolNamespace = MessageGatewayJson.OptionalString(toolCall, "namespace", 256);
        if (toolNamespace?.Any(char.IsControl) == true)
        {
            throw new InvalidDataException("A message gateway tool namespace is invalid.");
        }

        var completed = new ToolCallContent(id, name, arguments, thoughtSignature, toolNamespace);
        _content[index] = completed;
        return ModelStreamEvent.Update(
            ModelStreamEventKind.ToolCallEnded,
            Partial(),
            contentIndex: index,
            toolCallId: id,
            toolName: name,
            toolCall: completed);
    }

    private ModelStreamEvent Terminal(JsonElement root, bool isError)
    {
        if (_text.Count > 0 || _reasoning.Count > 0 || _toolArguments.Count > 0)
        {
            throw new InvalidDataException("The message gateway terminated with unfinished content blocks.");
        }

        var rawReason = MessageGatewayJson.RequiredString(root, "reason", 64);
        var stopReason = rawReason switch
        {
            "stop" when !isError => ModelStopReason.Stop,
            "length" when !isError => ModelStopReason.Length,
            "toolUse" when !isError => ModelStopReason.ToolUse,
            "error" when isError => ModelStopReason.Error,
            "aborted" when isError => ModelStopReason.Aborted,
            _ => throw new InvalidDataException("The message gateway terminal reason is invalid."),
        };
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The message gateway terminal event is missing usage.");
        }

        var usage = ParseUsage(usageElement);
        var responseId = MessageGatewayJson.OptionalString(root, "responseId", 1_024);
        if (responseId is not null)
        {
            responseId = _redactor.Sanitize(responseId, 1_024);
        }

        var diagnostics = ParseRewrite(root);
        var errorMessage = isError
            ? MessageGatewayJson.OptionalString(root, "errorMessage", _settings.MaxErrorCharacters)
              ?? (stopReason == ModelStopReason.Aborted
                  ? "The message gateway request was aborted."
                  : "The message gateway reported an error.")
            : null;
        if (errorMessage is not null)
        {
            errorMessage = _redactor.Sanitize(errorMessage, _settings.MaxErrorCharacters);
        }

        if (stopReason == ModelStopReason.Error && !HasMeaningfulOutput(usage))
        {
            throw CreateRetryableStreamFailure(errorMessage!, diagnostics);
        }

        _terminal = true;
        return ModelStreamEvent.Terminal(new ModelResponse(
            _content,
            stopReason,
            usage,
            errorMessage,
            _settings.ProviderId,
            _settings.ApiId,
            _request.Model,
            responseId,
            rawReason,
            diagnostics: diagnostics));
    }

    private ModelUsage ParseUsage(JsonElement usage)
    {
        var input = MessageGatewayJson.RequiredInt64(usage, "input");
        var output = MessageGatewayJson.RequiredInt64(usage, "output");
        var cacheRead = MessageGatewayJson.RequiredInt64(usage, "cacheRead");
        var cacheWrite = MessageGatewayJson.RequiredInt64(usage, "cacheWrite");
        var total = MessageGatewayJson.RequiredInt64(usage, "totalTokens");
        if (input < 0
            || output < 0
            || cacheRead < 0
            || cacheWrite < 0
            || total != checked(input + output + cacheRead + cacheWrite))
        {
            throw new InvalidDataException("The message gateway usage totals are invalid.");
        }

        long? reasoning = null;
        if (usage.TryGetProperty("reasoning", out var reasoningElement))
        {
            if (!reasoningElement.TryGetInt64(out var value))
            {
                throw new InvalidDataException("The message gateway reasoning usage is invalid.");
            }

            reasoning = value;
        }

        long? oneHour = null;
        if (usage.TryGetProperty("cacheWrite1h", out var oneHourElement))
        {
            if (!oneHourElement.TryGetInt64(out var value))
            {
                throw new InvalidDataException("The message gateway cache-write usage is invalid.");
            }

            oneHour = value;
        }

        if (!usage.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The message gateway usage is missing cost data.");
        }

        var modelCost = new ModelCost(
            MessageGatewayJson.RequiredDouble(cost, "input"),
            MessageGatewayJson.RequiredDouble(cost, "output"),
            MessageGatewayJson.RequiredDouble(cost, "cacheRead"),
            MessageGatewayJson.RequiredDouble(cost, "cacheWrite"),
            isKnown: true);
        var reportedCostTotal = MessageGatewayJson.RequiredDouble(cost, "total");
        var computedCostTotal = modelCost.Input + modelCost.Output + modelCost.CacheRead + modelCost.CacheWrite;
        if (double.IsNaN(computedCostTotal) || double.IsInfinity(computedCostTotal))
        {
            throw new InvalidDataException("The message gateway cost total is outside supported bounds.");
        }

        var costTolerance = Math.Max(1e-12, Math.Abs(computedCostTotal) * 1e-9);
        if (Math.Abs(reportedCostTotal - computedCostTotal) > costTolerance)
        {
            throw new InvalidDataException("The message gateway cost totals are invalid.");
        }

        try
        {
            return new ModelUsage(input, output, cacheRead, cacheWrite, reasoning, oneHour, modelCost);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The message gateway usage is outside supported bounds.", exception);
        }
    }

    private static IReadOnlyList<ModelDiagnostic> ParseRewrite(JsonElement root)
    {
        if (!root.TryGetProperty("rewrite", out var rewrite))
        {
            return Array.Empty<ModelDiagnostic>();
        }

        if (rewrite.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The message gateway rewrite summary is invalid.");
        }

        var policyId = MessageGatewayJson.RequiredString(rewrite, "policyId", 256);
        var policyVersion = MessageGatewayJson.RequiredInt64(rewrite, "policyVersion");
        var changed = MessageGatewayJson.RequiredBoolean(rewrite, "changed");
        var tokenCountChange = MessageGatewayJson.RequiredInt64(rewrite, "tokenCountChange");
        var messageCountChange = MessageGatewayJson.RequiredInt64(rewrite, "messageCountChange");
        var systemPromptChanged = MessageGatewayJson.RequiredBoolean(rewrite, "systemPromptChanged");
        if (policyVersion < 0
            || tokenCountChange is < -10_000_000_000 or > 10_000_000_000
            || messageCountChange is < -1_000_000 or > 1_000_000)
        {
            throw new InvalidDataException("The message gateway rewrite summary is outside supported bounds.");
        }

        var data = JsonSerializer.Serialize(new
        {
            policyId,
            policyVersion,
            changed,
            tokenCountChange,
            messageCountChange,
            systemPromptChanged,
        });
        return Array.AsReadOnly(new[]
        {
            new ModelDiagnostic(
                "message_gateway_rewrite",
                changed
                    ? "The message gateway rewrote the request context."
                    : "The message gateway evaluated the request context without changes.",
                ModelDiagnosticSeverity.Information,
                data),
        });
    }

    private int AppendIndex(JsonElement root)
    {
        var index = MessageGatewayJson.RequiredIndex(root, "contentIndex", _settings.MaxContentBlocks);
        if (index != _content.Count || _content.Count >= _settings.MaxContentBlocks)
        {
            throw new InvalidDataException("Message gateway content indices must be contiguous and unique.");
        }

        return index;
    }

    private void RequireStarted()
    {
        if (!_started)
        {
            throw new InvalidDataException("The message gateway emitted content before its start event.");
        }
    }

    private void AppendContent(StringBuilder buffer, string value)
    {
        AddContentCharacters(value.Length);
        buffer.Append(value);
    }

    private void ReconcileFinalContent(StringBuilder streamed, string final)
    {
        if (streamed.Length > 0)
        {
            AddPartialSnapshotWork(streamed.Length);
            if (!string.Equals(streamed.ToString(), final, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Message gateway streamed content does not match its final value.");
            }
        }

        if (streamed.Length == 0)
        {
            AddContentCharacters(final.Length);
        }
    }

    private void AddContentCharacters(int count)
    {
        _contentCharacters = checked(_contentCharacters + count);
        if (_contentCharacters > _settings.MaxContentCharacters)
        {
            throw new InvalidDataException("The message gateway exceeded its content-character limit.");
        }
    }

    private bool TryCanonicalObject(string json, out string canonical)
    {
        try
        {
            using var document = MessageGatewayJson.Parse(json, _settings.MaxJsonDepth);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                canonical = string.Empty;
                return false;
            }

            canonical = JsonSerializer.Serialize(document.RootElement);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    private bool JsonObjectsEquivalent(string json, JsonElement expected)
    {
        try
        {
            using var document = MessageGatewayJson.Parse(json, _settings.MaxJsonDepth);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && JsonEquivalent(document.RootElement, expected);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool JsonEquivalent(JsonElement first, JsonElement second)
    {
        if (first.ValueKind != second.ValueKind)
        {
            if (first.ValueKind == JsonValueKind.Number && second.ValueKind == JsonValueKind.Number)
            {
                return NumbersEquivalent(first, second);
            }

            return false;
        }

        switch (first.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var firstProperties = first.EnumerateObject().ToArray();
                    var secondProperties = second.EnumerateObject().ToArray();
                    if (firstProperties.Length != secondProperties.Length)
                    {
                        return false;
                    }

                    foreach (var property in firstProperties)
                    {
                        if (!second.TryGetProperty(property.Name, out var value)
                            || !JsonEquivalent(property.Value, value))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            case JsonValueKind.Array:
                {
                    var firstItems = first.EnumerateArray().ToArray();
                    var secondItems = second.EnumerateArray().ToArray();
                    return firstItems.Length == secondItems.Length
                           && firstItems.Zip(secondItems, JsonEquivalent).All(equal => equal);
                }
            case JsonValueKind.String:
                return string.Equals(first.GetString(), second.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return NumbersEquivalent(first, second);
            case JsonValueKind.True:
            case JsonValueKind.False:
                return first.GetBoolean() == second.GetBoolean();
            case JsonValueKind.Null:
                return true;
            default:
                return string.Equals(first.GetRawText(), second.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static bool NumbersEquivalent(JsonElement first, JsonElement second)
    {
        if (first.TryGetDecimal(out var firstDecimal) && second.TryGetDecimal(out var secondDecimal))
        {
            return firstDecimal == secondDecimal;
        }

        return first.TryGetDouble(out var firstDouble)
               && second.TryGetDouble(out var secondDouble)
               && !double.IsNaN(firstDouble)
               && !double.IsInfinity(firstDouble)
               && !double.IsNaN(secondDouble)
               && !double.IsInfinity(secondDouble)
               && firstDouble.Equals(secondDouble);
    }

    private bool HasMeaningfulOutput(ModelUsage usage) =>
        _content.Any(content => content switch
        {
            TextContent text => text.Text.Length > 0,
            ReasoningContent reasoning => reasoning.Text.Length > 0,
            ToolCallContent => true,
            _ => false,
        })
        || usage.TotalTokens > 0
        || usage.Cost.Total > 0;

    private ModelProviderException CreateRetryableStreamFailure(
        string errorMessage,
        IReadOnlyList<ModelDiagnostic> diagnostics)
    {
        var combined = diagnostics.Concat(new[]
        {
            new ModelDiagnostic(
                "message_gateway_stream_failure",
                "The message gateway failed before producing meaningful output.",
                ModelDiagnosticSeverity.Error),
        });
        return new ModelProviderException(
            errorMessage,
            combined,
            isTransient: true);
    }

    private void AddPartialSnapshotWork(long units)
    {
        _partialSnapshotWork = checked(_partialSnapshotWork + units);
        if (_partialSnapshotWork > _settings.MaxPartialSnapshotWork)
        {
            throw new InvalidDataException("The message gateway exceeded its partial-snapshot work limit.");
        }
    }

    private ModelResponse Partial()
    {
        AddPartialSnapshotWork(_content.Count);
        return new ModelResponse(
            _content,
            ModelStopReason.Pending,
            provider: _settings.ProviderId,
            api: _settings.ApiId,
            responseModel: _request.Model);
    }
}
