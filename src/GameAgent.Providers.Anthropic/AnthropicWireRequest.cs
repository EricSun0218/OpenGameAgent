using System.Buffers;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.Anthropic;

internal sealed class AnthropicWireRequest
{
    internal const int MaxTools = 128;
    internal const int MaxToolSchemaUtf8Bytes = 256 * 1024;
    private const int MaxMessages = 4_096;
    private const int MaxParts = 65_536;
    private const int MaxRequestBodyUtf8Bytes = 8 * 1_048_576;
    private const int MaxTextUtf8Bytes = 4 * 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private AnthropicWireRequest(
        string streamAttemptId,
        int maxOutputTokens,
        string? system,
        IReadOnlyList<WireMessage> messages,
        IReadOnlyList<WireTool> tools,
        ModelInferenceOptions? inference)
    {
        StreamAttemptId = streamAttemptId;
        MaxOutputTokens = maxOutputTokens;
        System = system;
        Messages = messages;
        Tools = tools;
        Inference = inference;
    }

    internal string StreamAttemptId { get; }

    private int MaxOutputTokens { get; }

    private string? System { get; }

    private IReadOnlyList<WireMessage> Messages { get; }

    private IReadOnlyList<WireTool> Tools { get; }

    private ModelInferenceOptions? Inference { get; }

    internal static AnthropicWireRequest Create(
        StreamingModelRequest request,
        AnthropicProviderOptions options,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw Validation("The provider request is missing.");
        }

        var streamAttemptId = RequiredString(
            request.StreamAttemptId,
            256,
            "The stream-attempt identifier is invalid.");
        if (request.MaxOutputTokens < 1)
        {
            throw Validation(
                "The Anthropic output-token limit is invalid.");
        }

        if (request.OpaqueContinuationState is not null)
        {
            throw Validation(
                "Anthropic Messages does not support opaque continuation state.");
        }

        var maxOutputTokens = request.MaxOutputTokens.HasValue
            ? Math.Min(
                request.MaxOutputTokens.Value,
                options.MaxOutputTokens)
            : options.MaxOutputTokens;
        IReadOnlyList<NormalizedMessage> sourceMessages;
        IReadOnlyList<ToolDescriptor> sourceTools;
        int messageCount;
        int toolCount;
        try
        {
            sourceMessages = request.Messages;
            sourceTools = request.Tools;
            messageCount = sourceMessages.Count;
            toolCount = sourceTools.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException
                  and not OperationCanceledException)
        {
            throw Validation(
                "The provider request collections are invalid.",
                exception);
        }

        if (messageCount is < 1 or > MaxMessages
            || toolCount is < 0 or > MaxTools)
        {
            throw Validation("The provider request exceeds its item limit.");
        }

        var tools = new WireTool[toolCount];
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < toolCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sourceTools[index];
                if (source is null)
                {
                    throw Validation(
                        "The provider request contains a null tool.");
                }

                var name = RequiredToolName(source.Name);
                if (!toolNames.Add(name))
                {
                    throw Validation(
                        "Anthropic tool names must be unique.");
                }

                var description = OptionalString(
                    source.Description,
                    8_192,
                    "An Anthropic tool description is invalid.");
                var schema = CloneBoundedJson(
                    source.ParametersSchema,
                    MaxToolSchemaUtf8Bytes,
                    requireObject: true,
                    "An Anthropic tool schema is invalid.");
                tools[index] = new WireTool(
                    name,
                    description,
                    schema);
            }
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw Validation(
                "The provider tool collection changed during preparation.",
                exception);
        }

        var systemParts = new List<string>();
        var messages = new List<WireMessage>(messageCount);
        var pendingToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var allToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var nonSystemSeen = false;
        var totalParts = 0;

        try
        {
            for (var messageIndex = 0;
                 messageIndex < messageCount;
                 messageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sourceMessages[messageIndex];
                if (source is null || source.Parts is null)
                {
                    throw Validation(
                        "The provider request contains a null message.");
                }

                var partCount = source.Parts.Count;
                if (partCount < 1 || partCount > MaxParts - totalParts)
                {
                    throw Validation(
                        "The provider request exceeds its content-part limit.");
                }

                totalParts += partCount;
                switch (source.Role)
                {
                    case NormalizedRoles.System:
                        if (nonSystemSeen)
                        {
                            throw Validation(
                                "Anthropic system content must precede all messages.");
                        }

                        ReadSystemParts(source.Parts, systemParts);
                        break;
                    case NormalizedRoles.User:
                        nonSystemSeen = true;
                        if (pendingToolCalls.Count > 0)
                        {
                            throw Validation(
                                "Anthropic tool results must immediately follow every tool use.");
                        }

                        AddOrMerge(
                            messages,
                            "user",
                            ReadUserParts(source.Parts));
                        break;
                    case NormalizedRoles.Assistant:
                        nonSystemSeen = true;
                        if (pendingToolCalls.Count > 0)
                        {
                            throw Validation(
                                "Anthropic tool uses are missing immediate results.");
                        }

                        AddOrMerge(
                            messages,
                            "assistant",
                            ReadAssistantParts(
                                source.Parts,
                                pendingToolCalls,
                                allToolCalls,
                                options.MaxToolArgumentsUtf8Bytes));
                        break;
                    case NormalizedRoles.Tool:
                        nonSystemSeen = true;
                        if (pendingToolCalls.Count == 0)
                        {
                            throw Validation(
                                "An Anthropic tool result has no pending tool use.");
                        }

                        AddOrMerge(
                            messages,
                            "user",
                            ReadToolResults(
                                source.Parts,
                                pendingToolCalls));
                        break;
                    default:
                        throw Validation(
                            "The provider request contains an unsupported role.");
                }
            }
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw Validation(
                "The provider message collection changed during preparation.",
                exception);
        }

        if (messages.Count == 0)
        {
            throw Validation(
                "Anthropic requires at least one user or assistant message.");
        }

        if (pendingToolCalls.Count > 0)
        {
            throw Validation(
                "Anthropic tool uses are missing immediate results.");
        }

        var system = systemParts.Count == 0
            ? null
            : string.Join("\n\n", systemParts);
        if (system is not null)
        {
            _ = OptionalContent(
                system,
                MaxTextUtf8Bytes,
                "The Anthropic system prompt exceeds its limit.");
        }

        return new AnthropicWireRequest(
            streamAttemptId,
            maxOutputTokens,
            system,
            messages,
            tools,
            request.Inference?.CloneValidated());
    }

    internal byte[] Encode(AnthropicProviderOptions options)
    {
        ValidateInference(options);
        using var buffer = new BoundedBufferWriter(
            MaxRequestBodyUtf8Bytes);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options.Model);
            writer.WriteNumber(
                "max_tokens",
                MaxOutputTokens);
            writer.WriteBoolean("stream", true);
            var reasoningDisabled = IsReasoningDisabled(Inference);
            if (string.Equals(
                    options.ThinkingDialect,
                    AnthropicThinkingDialects.ManualBudget,
                    StringComparison.Ordinal)
                && !reasoningDisabled
                && (Inference?.ReasoningEnabled == true
                    || Inference?.ReasoningTokenBudget.HasValue == true
                    || options.DefaultReasoningTokenBudget.HasValue))
            {
                var budget = Inference?.ReasoningTokenBudget
                             ?? options.DefaultReasoningTokenBudget
                             ?? throw Unsupported(
                                 "reasoning without a token budget");
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteNumber("budget_tokens", budget);
                writer.WriteEndObject();
            }
            else if (string.Equals(
                         options.ThinkingDialect,
                         AnthropicThinkingDialects.Adaptive,
                         StringComparison.Ordinal)
                     && reasoningDisabled)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "disabled");
                writer.WriteEndObject();
            }
            else if (string.Equals(
                         options.ThinkingDialect,
                         AnthropicThinkingDialects.Adaptive,
                         StringComparison.Ordinal)
                     && Inference?.ReasoningEnabled == true)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "adaptive");
                writer.WriteEndObject();
            }

            if (Inference?.ReasoningEffort is string effort
                && !string.Equals(
                    effort,
                    ModelReasoningEfforts.None,
                    StringComparison.Ordinal))
            {
                writer.WritePropertyName("output_config");
                writer.WriteStartObject();
                writer.WriteString("effort", effort);
                writer.WriteEndObject();
            }

            if (Inference?.Temperature is double temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (Inference?.TopP is double topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (Inference?.PromptCachingEnabled == true)
            {
                writer.WritePropertyName("cache_control");
                writer.WriteStartObject();
                writer.WriteString("type", "ephemeral");
                if (Inference.PromptCacheRetention is string retention)
                {
                    writer.WriteString("ttl", retention);
                }

                writer.WriteEndObject();
            }
            if (System is not null)
            {
                writer.WriteString("system", System);
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var block in message.Blocks)
                {
                    WriteBlock(writer, block);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    if (!string.IsNullOrEmpty(tool.Description))
                    {
                        writer.WriteString(
                            "description",
                            tool.Description);
                    }

                    writer.WritePropertyName("input_schema");
                    tool.InputSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private void ValidateInference(AnthropicProviderOptions options)
    {
        var manual = string.Equals(
            options.ThinkingDialect,
            AnthropicThinkingDialects.ManualBudget,
            StringComparison.Ordinal);
        var adaptive = string.Equals(
            options.ThinkingDialect,
            AnthropicThinkingDialects.Adaptive,
            StringComparison.Ordinal);
        var reasoningDisabled = IsReasoningDisabled(Inference);
        var budget = manual && !reasoningDisabled
            ? Inference?.ReasoningTokenBudget
              ?? options.DefaultReasoningTokenBudget
            : null;
        var requestedReasoningControl =
            Inference?.ReasoningEnabled.HasValue == true
            || Inference?.ReasoningEffort is not null
            || Inference?.ReasoningTokenBudget.HasValue == true;
        if (!manual && !adaptive && requestedReasoningControl)
        {
            throw Unsupported(
                "reasoning control because this route declares no thinking dialect");
        }

        if (manual
            && Inference?.ReasoningEnabled == true
            && !budget.HasValue)
        {
            throw Unsupported("manual reasoning without a token budget");
        }

        if (manual
            && Inference?.ReasoningEffort is string manualEffort
            && !string.Equals(
                manualEffort,
                ModelReasoningEfforts.None,
                StringComparison.Ordinal))
        {
            throw Unsupported("reasoning effort on a manual-budget route");
        }

        if (adaptive && Inference?.ReasoningTokenBudget.HasValue == true)
        {
            throw Unsupported("a token budget on an adaptive-thinking route");
        }

        if (adaptive
            && reasoningDisabled
            && !options.SupportsThinkingDisable)
        {
            throw Unsupported("explicit thinking disable on this adaptive route");
        }

        if (adaptive
            && Inference?.ReasoningEffort is string effort
            && !string.Equals(
                effort,
                ModelReasoningEfforts.None,
                StringComparison.Ordinal)
            && !options.SupportedReasoningEfforts.Contains(
                effort,
                StringComparer.Ordinal))
        {
            throw Unsupported("the requested adaptive-thinking effort");
        }

        if (budget.HasValue
            && (budget.Value < 1_024 || budget.Value >= MaxOutputTokens))
        {
            throw new ProviderException(
                "provider_reasoning_budget_invalid",
                "validation",
                "The reasoning token budget must be at least 1024 and below the output-token limit.",
                false,
                usageKnownToBeZero: true);
        }

        var adaptiveMayThink = adaptive && !reasoningDisabled;
        if ((budget.HasValue || adaptiveMayThink) && Tools.Count != 0)
        {
            throw Unsupported(
                "reasoning with tool use because signed thinking-block continuation is not configured");
        }

        if (Inference is null)
        {
            return;
        }

        if ((Inference.Temperature.HasValue || Inference.TopP.HasValue)
            && !options.SupportsSamplingControls)
        {
            throw Unsupported("sampling control");
        }

        if (Inference.Seed.HasValue)
        {
            throw Unsupported("seed");
        }

        if (Inference.PromptCachingEnabled == false)
        {
            throw Unsupported("prompt-cache bypass");
        }

        if (Inference.PromptCacheKey is not null)
        {
            throw Unsupported("prompt-cache key");
        }

        if (budget.HasValue && Inference.Temperature.HasValue)
        {
            throw Unsupported(
                "temperature while manual extended thinking is enabled");
        }

        if (budget.HasValue
            && Inference.TopP is double topP
            && topP < 0.95)
        {
            throw Unsupported(
                "top-p below 0.95 while manual extended thinking is enabled");
        }
    }

    private static bool IsReasoningDisabled(ModelInferenceOptions? inference) =>
        inference?.ReasoningEnabled == false
        || string.Equals(
            inference?.ReasoningEffort,
            ModelReasoningEfforts.None,
            StringComparison.Ordinal);

    private static ProviderException Unsupported(string control) =>
        new(
            "provider_inference_control_unsupported",
            "capability",
            $"The selected provider route does not support {control}.",
            ProviderFailureDisposition.Failover,
            usageKnownToBeZero: true);

    private static void ReadSystemParts(
        IReadOnlyList<NormalizedContentPart> source,
        ICollection<string> destination)
    {
        foreach (var part in source)
        {
            if (part is null)
            {
                throw Validation(
                    "The Anthropic system prompt contains a null part.");
            }

            switch (part.Type)
            {
                case NormalizedPartTypes.Text:
                    destination.Add(RequiredContent(part.Text));
                    break;
                case NormalizedPartTypes.Json when part.Json.HasValue:
                    destination.Add(BoundedJsonText(part.Json.Value));
                    break;
                default:
                    throw Validation(
                        "Anthropic system prompts support text content only.");
            }
        }
    }

    private static IReadOnlyList<WireBlock> ReadUserParts(
        IReadOnlyList<NormalizedContentPart> source)
    {
        var blocks = new List<WireBlock>(source.Count);
        foreach (var part in source)
        {
            if (part is null)
            {
                throw Validation(
                    "The Anthropic user message contains a null part.");
            }

            switch (part.Type)
            {
                case NormalizedPartTypes.Text:
                    blocks.Add(WireBlock.FromText(RequiredContent(part.Text)));
                    break;
                case NormalizedPartTypes.Json when part.Json.HasValue:
                    blocks.Add(
                        WireBlock.FromText(BoundedJsonText(part.Json.Value)));
                    break;
                default:
                    throw Validation(
                        "Anthropic user messages support text content only.");
            }
        }

        return blocks;
    }

    private static IReadOnlyList<WireBlock> ReadAssistantParts(
        IReadOnlyList<NormalizedContentPart> source,
        ISet<string> pendingToolCalls,
        ISet<string> allToolCalls,
        int maxToolArgumentsUtf8Bytes)
    {
        var blocks = new List<WireBlock>(source.Count);
        foreach (var part in source)
        {
            if (part is null)
            {
                throw Validation(
                    "The Anthropic assistant message contains a null part.");
            }

            switch (part.Type)
            {
                case NormalizedPartTypes.Text:
                    blocks.Add(WireBlock.FromText(RequiredContent(part.Text)));
                    break;
                case NormalizedPartTypes.ToolCall
                    when part.Json.HasValue:
                    {
                        var id = RequiredString(
                            part.ToolCallId,
                            256,
                            "An Anthropic tool-use identifier is invalid.");
                        var name = RequiredToolName(part.ToolName);
                        if (!allToolCalls.Add(id))
                        {
                            throw Validation(
                                "Anthropic tool-use identifiers must be unique.");
                        }

                        var input = CloneBoundedJson(
                            part.Json.Value,
                            maxToolArgumentsUtf8Bytes,
                            requireObject: true,
                            "An Anthropic tool-use input is invalid.");
                        pendingToolCalls.Add(id);
                        blocks.Add(WireBlock.ToolUse(id, name, input));
                        break;
                    }
                default:
                    throw Validation(
                        "Anthropic assistant messages support text and tool-use content only.");
            }
        }

        return blocks;
    }

    private static IReadOnlyList<WireBlock> ReadToolResults(
        IReadOnlyList<NormalizedContentPart> source,
        ISet<string> pendingToolCalls)
    {
        var blocks = new List<WireBlock>(source.Count);
        foreach (var part in source)
        {
            if (part is null
                || !string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolResult,
                    StringComparison.Ordinal)
                || !part.Json.HasValue)
            {
                throw Validation(
                    "Anthropic tool messages support tool results only.");
            }

            var id = RequiredString(
                part.ToolCallId,
                256,
                "An Anthropic tool-result identifier is invalid.");
            if (!pendingToolCalls.Remove(id))
            {
                throw Validation(
                    "An Anthropic tool result does not match a pending tool use.");
            }

            blocks.Add(
                WireBlock.ToolResult(
                    id,
                    BoundedJsonText(part.Json.Value)));
        }

        return blocks;
    }

    private static void AddOrMerge(
        IList<WireMessage> messages,
        string role,
        IReadOnlyList<WireBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            throw Validation(
                "Anthropic messages cannot have empty content.");
        }

        if (messages.Count > 0
            && string.Equals(
                messages[messages.Count - 1].Role,
                role,
                StringComparison.Ordinal))
        {
            messages[messages.Count - 1].Blocks.AddRange(blocks);
            return;
        }

        messages.Add(new WireMessage(role, blocks));
    }

    private static void WriteBlock(
        Utf8JsonWriter writer,
        WireBlock block)
    {
        writer.WriteStartObject();
        writer.WriteString("type", block.Type);
        switch (block.Type)
        {
            case "text":
                writer.WriteString("text", block.Text);
                break;
            case "tool_use":
                writer.WriteString("id", block.Id);
                writer.WriteString("name", block.Name);
                writer.WritePropertyName("input");
                block.Json!.Value.WriteTo(writer);
                break;
            case "tool_result":
                writer.WriteString("tool_use_id", block.Id);
                writer.WriteString("content", block.Text);
                break;
            default:
                throw new InvalidOperationException(
                    "The Anthropic wire block type is invalid.");
        }

        writer.WriteEndObject();
    }

    private static string RequiredContent(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || GetUtf8ByteCount(
                value,
                "Anthropic text content is invalid.")
               > MaxTextUtf8Bytes)
        {
            throw Validation("Anthropic text content is invalid.");
        }

        return value;
    }

    private static string RequiredToolName(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 64
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'))
        {
            throw Validation("An Anthropic tool name is invalid.");
        }

        return value;
    }

    private static string RequiredString(
        string? value,
        int maximumUtf8Bytes,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || GetUtf8ByteCount(value, message) > maximumUtf8Bytes)
        {
            throw Validation(message);
        }

        return value;
    }

    private static string OptionalString(
        string? value,
        int maximumUtf8Bytes,
        string message)
    {
        value ??= string.Empty;
        if (GetUtf8ByteCount(value, message) > maximumUtf8Bytes)
        {
            throw Validation(message);
        }

        return value;
    }

    private static string OptionalContent(
        string? value,
        int maximumUtf8Bytes,
        string message)
    {
        value ??= string.Empty;
        if (GetUtf8ByteCount(value, message) > maximumUtf8Bytes)
        {
            throw Validation(message);
        }

        return value;
    }

    private static string BoundedJsonText(JsonElement value)
    {
        var clone = CloneBoundedJson(
            value,
            MaxTextUtf8Bytes,
            requireObject: false,
            "Anthropic JSON text content is invalid.");
        return clone.GetRawText();
    }

    private static JsonElement CloneBoundedJson(
        JsonElement value,
        int maximumUtf8Bytes,
        bool requireObject,
        string message)
    {
        string raw;
        try
        {
            raw = value.GetRawText();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                  or ObjectDisposedException)
        {
            throw Validation(message, exception);
        }

        if (GetUtf8ByteCount(raw, message) > maximumUtf8Bytes)
        {
            throw Validation(message);
        }

        try
        {
            using var document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (requireObject
                && document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Validation(message);
            }

            EnsureUniqueJsonProperties(
                document.RootElement,
                message);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw Validation(message, exception);
        }
    }

    private static void EnsureUniqueJsonProperties(
        JsonElement value,
        string message)
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
                            throw Validation(message);
                        }

                        EnsureUniqueJsonProperties(
                            property.Value,
                            message);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    EnsureUniqueJsonProperties(item, message);
                }

                break;
        }
    }

    private static int GetUtf8ByteCount(string value, string message)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw Validation(message, exception);
        }
    }

    private static ProviderException Validation(
        string message,
        Exception? exception = null)
    {
        return new ProviderException(
            "provider_request_invalid",
            "validation",
            message,
            false,
            innerException: exception,
            usageKnownToBeZero: true);
    }

    private sealed class WireMessage
    {
        internal WireMessage(
            string role,
            IReadOnlyList<WireBlock> blocks)
        {
            Role = role;
            Blocks = new List<WireBlock>(blocks);
        }

        internal string Role { get; }

        internal List<WireBlock> Blocks { get; }
    }

    private sealed class WireBlock
    {
        private WireBlock(
            string type,
            string? text,
            string? id,
            string? name,
            JsonElement? json)
        {
            Type = type;
            Text = text;
            Id = id;
            Name = name;
            Json = json;
        }

        internal string Type { get; }

        internal string? Text { get; }

        internal string? Id { get; }

        internal string? Name { get; }

        internal JsonElement? Json { get; }

        internal static WireBlock FromText(string text)
        {
            return new WireBlock("text", text, null, null, null);
        }

        internal static WireBlock ToolUse(
            string id,
            string name,
            JsonElement input)
        {
            return new WireBlock(
                "tool_use",
                null,
                id,
                name,
                input);
        }

        internal static WireBlock ToolResult(
            string id,
            string content)
        {
            return new WireBlock(
                "tool_result",
                content,
                id,
                null,
                null);
        }
    }

    private sealed class WireTool
    {
        internal WireTool(
            string name,
            string description,
            JsonElement inputSchema)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
        }

        internal string Name { get; }

        internal string Description { get; }

        internal JsonElement InputSchema { get; }
    }

    private sealed class BoundedBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int JsonWriterSlackBytes = 4_096;
        private readonly int _maximumBytes;
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        internal BoundedBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(
                Math.Min(16_384, maximumBytes + JsonWriterSlackBytes));
        }

        public void Advance(int count)
        {
            if (_disposed
                || count < 0
                || count > _buffer.Length - _written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _written += count;
            if (_written > _maximumBytes)
            {
                throw Validation(
                    "The Anthropic JSON request body exceeds its limit.");
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        internal byte[] ToArray()
        {
            if (_disposed || _written < 1)
            {
                throw new InvalidOperationException(
                    "The Anthropic request buffer is unavailable.");
            }

            return _buffer.AsSpan(0, _written).ToArray();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Array.Clear(_buffer, 0, _buffer.Length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
            _written = 0;
        }

        private void Ensure(int sizeHint)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedBufferWriter));
            }

            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            sizeHint = Math.Max(sizeHint, 1);
            var maximumCapacity = checked(
                _maximumBytes + JsonWriterSlackBytes);
            if (sizeHint > maximumCapacity - _written)
            {
                throw Validation(
                    "The Anthropic JSON request body exceeds its limit.");
            }

            if (_buffer.Length - _written >= sizeHint)
            {
                return;
            }

            var target = Math.Min(
                maximumCapacity,
                Math.Max(
                    checked(_written + sizeHint),
                    Math.Min(
                        maximumCapacity,
                        checked(_buffer.Length * 2))));
            var replacement = ArrayPool<byte>.Shared.Rent(target);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            Array.Clear(_buffer, 0, _buffer.Length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }
    }
}
