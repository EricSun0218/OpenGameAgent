using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenGameAgent.Kernel;

internal static class AgentValidator
{
    public static void ValidateOptions(
        string model,
        string? sessionId,
        ModelParameters parameters,
        AgentLimits limits,
        Func<DateTimeOffset> clock,
        Func<string> runIdFactory)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > limits.MaxModelNameCharacters)
        {
            throw new ArgumentException($"Model must contain 1 to {limits.MaxModelNameCharacters} characters.", nameof(model));
        }

        if (sessionId is not null && sessionId.Length > limits.MaxSessionIdCharacters)
        {
            throw new ArgumentException($"SessionId exceeds {limits.MaxSessionIdCharacters} characters.", nameof(sessionId));
        }

        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (parameters.Temperature is { } temperature
            && (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature < 0 || temperature > 10))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Temperature must be between 0 and 10.");
        }

        if (parameters.MaxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "MaxOutputTokens must be positive.");
        }

        if ((parameters.ReasoningLevel?.Length ?? 0) > limits.MaxMetadataValueCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxMetadataValueCharacters), "The reasoning level is too large.");
        }

        var extensions = parameters.Extensions ?? new Dictionary<string, string>();
        if (extensions.Count > limits.MaxMetadataEntriesPerMessage)
        {
            throw new AgentLimitException(nameof(limits.MaxMetadataEntriesPerMessage), "Too many model extensions are configured.");
        }

        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension.Key) || extension.Value is null)
            {
                throw new ArgumentException("Model extension keys must be non-empty and values cannot be null.", nameof(parameters));
            }

            if (extension.Key.Length > limits.MaxMetadataKeyCharacters
                || extension.Value.Length > limits.MaxJsonCharactersPerPart)
            {
                var limit = extension.Key.Length > limits.MaxMetadataKeyCharacters
                    ? nameof(limits.MaxMetadataKeyCharacters)
                    : nameof(limits.MaxJsonCharactersPerPart);
                throw new AgentLimitException(limit, "A model extension is too large.");
            }
        }

        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        if (runIdFactory is null)
        {
            throw new ArgumentNullException(nameof(runIdFactory));
        }
    }

    public static void ValidateContext(AgentContext context, AgentLimits limits)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (context.SystemPrompt.Length > limits.MaxSystemPromptCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxSystemPromptCharacters), "System prompt is too large.");
        }

        if (context.Messages.Count > limits.MaxMessages)
        {
            throw new AgentLimitException(nameof(limits.MaxMessages), "The canonical transcript contains too many messages.");
        }

        if (context.Tools.Count > limits.MaxTools)
        {
            throw new AgentLimitException(nameof(limits.MaxTools), "Too many tools are active.");
        }

        foreach (var message in context.Messages)
        {
            ValidateMessage(message, limits);
        }

        ValidateTranscript(context.Messages);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in context.Tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("The tool list cannot contain null values.", nameof(context));
            }

            var definition = tool.Definition;
            if (definition.Name.Length > limits.MaxToolNameCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxToolNameCharacters), $"Tool name '{definition.Name}' is too large.");
            }

            if (definition.Description.Length > limits.MaxToolDescriptionCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxToolDescriptionCharacters), $"Tool description '{definition.Name}' is too large.");
            }

            if (definition.InputSchemaJson.Length > limits.MaxToolSchemaCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxToolSchemaCharacters), $"Tool schema '{definition.Name}' is too large.");
            }

            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Duplicate tool name '{definition.Name}'.", nameof(context));
            }
        }
    }

    public static void ValidateMessages(IReadOnlyList<AgentMessage> messages, AgentLimits limits)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        foreach (var message in messages)
        {
            ValidateMessage(message, limits);
        }
    }

    public static void ValidateMessage(AgentMessage message, AgentLimits limits)
    {
        if (message is null)
        {
            throw new ArgumentException("A message cannot be null.", nameof(message));
        }

        if (message.Content.Count > limits.MaxContentPartsPerMessage)
        {
            throw new AgentLimitException(nameof(limits.MaxContentPartsPerMessage), "A message contains too many content parts.");
        }

        if (message.Metadata.Count > limits.MaxMetadataEntriesPerMessage)
        {
            throw new AgentLimitException(nameof(limits.MaxMetadataEntriesPerMessage), "A message contains too many metadata entries.");
        }

        foreach (var pair in message.Metadata)
        {
            if (pair.Key.Length > limits.MaxMetadataKeyCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxMetadataKeyCharacters), "A message metadata key is too large.");
            }

            if (pair.Value.Length > limits.MaxMetadataValueCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxMetadataValueCharacters), "A message metadata value is too large.");
            }
        }

        foreach (var content in message.Content)
        {
            ValidateContent(content, limits);
        }

        if (message.DetailsJson is { } details && details.Length > limits.MaxJsonCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxJsonCharactersPerPart), "Tool result details are too large.");
        }

        if ((message.CustomRole?.Length ?? 0) > limits.MaxToolNameCharacters
            || (message.ToolName?.Length ?? 0) > limits.MaxToolNameCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxToolNameCharacters), "A message role or tool name is too large.");
        }

        if ((message.ToolCallId?.Length ?? 0) > limits.MaxToolCallIdCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxToolCallIdCharacters), "A tool call ID is too large.");
        }

        if ((message.Model?.Length ?? 0) > limits.MaxModelNameCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxModelNameCharacters), "A message model name is too large.");
        }

        if ((message.ErrorMessage?.Length ?? 0) > limits.MaxTextCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "A message error is too large.");
        }


    }

    public static void ValidateResponse(ModelResponse response, AgentLimits limits)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (response.Content.Count > limits.MaxContentPartsPerMessage)
        {
            throw new AgentLimitException(nameof(limits.MaxContentPartsPerMessage), "The model response contains too many content parts.");
        }

        foreach (var content in response.Content)
        {
            ValidateContent(content, limits);
        }

        var calls = response.Content.Count(part => part is ToolCallContent);
        if (calls > limits.MaxToolCallsPerTurn)
        {
            throw new AgentLimitException(nameof(limits.MaxToolCallsPerTurn), "The model response contains too many tool calls.");
        }

        if (response.StopReason == ModelStopReason.ToolUse && calls == 0)
        {
            throw new ArgumentException("A tool-use response must contain at least one tool call.", nameof(response));
        }

        if (calls > 0 && response.StopReason is ModelStopReason.Stop)
        {
            throw new ArgumentException("A stopped response cannot contain tool calls.", nameof(response));
        }

        if ((response.ErrorMessage?.Length ?? 0) > limits.MaxTextCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "The model response error is too large.");
        }



        var callIds = new HashSet<string>(StringComparer.Ordinal);
        if (response.Content.OfType<ToolCallContent>().Any(call => !callIds.Add(call.Id)))
        {
            throw new ArgumentException("The model response contains duplicate tool call IDs.", nameof(response));
        }
    }

    public static void ValidateToolCall(ToolCallContent call, AgentLimits limits)
    {
        if (call is null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        ValidateContent(call, limits);
    }

    public static void ValidateToolResult(ToolResult result, AgentLimits limits)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (result.Content.Count > limits.MaxContentPartsPerMessage)
        {
            throw new AgentLimitException(nameof(limits.MaxContentPartsPerMessage), "A tool result contains too many content parts.");
        }

        foreach (var content in result.Content)
        {
            ValidateContent(content, limits);
        }

        if (result.DetailsJson is { } details && details.Length > limits.MaxJsonCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxJsonCharactersPerPart), "Tool result details are too large.");
        }


    }

    public static void ValidateProgress(ToolProgress progress, AgentLimits limits)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        if ((progress.Message?.Length ?? 0) > limits.MaxTextCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "Tool progress text is too large.");
        }

        if ((progress.DetailsJson?.Length ?? 0) > limits.MaxJsonCharactersPerPart)
        {
            throw new AgentLimitException(nameof(limits.MaxJsonCharactersPerPart), "Tool progress details are too large.");
        }
    }

    public static void ValidateRequest(
        ModelRequest request,
        AgentLimits limits,
        Func<DateTimeOffset> clock,
        Func<string> runIdFactory)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateOptions(request.Model, request.SessionId, request.Parameters, limits, clock, runIdFactory);
        if (request.RunId.Length > limits.MaxSessionIdCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxSessionIdCharacters), "The provider run ID is too large.");
        }

        if (request.SystemPrompt.Length > limits.MaxSystemPromptCharacters)
        {
            throw new AgentLimitException(nameof(limits.MaxSystemPromptCharacters), "The provider system prompt is too large.");
        }

        ValidateMessages(request.Messages, limits);
        ValidateTranscript(request.Messages);
        if (request.Messages.Count > limits.MaxMessages || request.Tools.Count > limits.MaxTools)
        {
            throw new AgentLimitException(nameof(limits.MaxMessages), "The provider request exceeds configured collection limits.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in request.Tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("The provider tool list cannot contain null values.", nameof(request));
            }

            if (tool.Name.Length > limits.MaxToolNameCharacters
                || tool.Description.Length > limits.MaxToolDescriptionCharacters
                || tool.InputSchemaJson.Length > limits.MaxToolSchemaCharacters)
            {
                throw new AgentLimitException(nameof(limits.MaxTools), "A provider tool definition exceeds configured limits.");
            }

            if (!names.Add(tool.Name))
            {
                throw new ArgumentException($"Duplicate provider tool name '{tool.Name}'.", nameof(request));
            }
        }
    }

    private static void ValidateContent(AgentContent content, AgentLimits limits)
    {
        switch (content)
        {
            case null:
                throw new ArgumentException("Content cannot be null.", nameof(content));
            case TextContent text when text.Text.Length > limits.MaxTextCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "A text content part is too large.");
            case ReasoningContent reasoning when reasoning.Text.Length > limits.MaxTextCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "A reasoning content part is too large.");
            case ReasoningContent reasoning when (reasoning.Signature?.Length ?? 0) > limits.MaxTextCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "A reasoning signature is too large.");
            case JsonContent json when json.Json.Length > limits.MaxJsonCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxJsonCharactersPerPart), "A JSON content part is too large.");
            case ResourceContent resource when resource.Uri.Length > limits.MaxResourceUriCharacters:
                throw new AgentLimitException(nameof(limits.MaxResourceUriCharacters), "A resource URI is too large.");
            case ResourceContent resource when resource.MediaType.Length > limits.MaxMetadataValueCharacters:
                throw new AgentLimitException(nameof(limits.MaxMetadataValueCharacters), "A resource media type is too large.");
            case ResourceContent resource when (resource.Name?.Length ?? 0) > limits.MaxTextCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxTextCharactersPerPart), "A resource name is too large.");
            case ToolCallContent call when call.Id.Length > limits.MaxToolCallIdCharacters:
                throw new AgentLimitException(nameof(limits.MaxToolCallIdCharacters), "A tool call ID is too large.");
            case ToolCallContent call when call.Name.Length > limits.MaxToolNameCharacters:
                throw new AgentLimitException(nameof(limits.MaxToolNameCharacters), "A tool call name is too large.");
            case ToolCallContent call when call.ArgumentsJson.Length > limits.MaxJsonCharactersPerPart:
                throw new AgentLimitException(nameof(limits.MaxJsonCharactersPerPart), "Tool call arguments are too large.");
            case TextContent or ReasoningContent or JsonContent or ResourceContent or ToolCallContent:
                break;
            default:
                throw new ArgumentException($"Unsupported content type '{content.GetType().FullName}'.", nameof(content));
        }
    }

    public static void ValidateTranscript(IReadOnlyList<AgentMessage> messages)
    {
        var openCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (openCalls.Count > 0 && message.Role != AgentRole.Tool)
            {
                throw new ArgumentException("Every assistant tool call must receive a tool result before the next non-tool message.", nameof(messages));
            }

            if (message.Role == AgentRole.Assistant)
            {
                var calls = message.Content.OfType<ToolCallContent>().ToArray();
                if (message.StopReason == ModelStopReason.Pending)
                {
                    throw new ArgumentException("A canonical transcript cannot contain a pending assistant response.", nameof(messages));
                }

                if (message.StopReason == ModelStopReason.ToolUse && calls.Length == 0)
                {
                    throw new ArgumentException("A tool-use assistant message must contain a tool call.", nameof(messages));
                }

                if (calls.Length > 0 && message.StopReason == ModelStopReason.Stop)
                {
                    throw new ArgumentException("A stopped assistant message cannot contain tool calls.", nameof(messages));
                }

                foreach (var call in calls)
                {
                    if (!openCalls.TryAdd(call.Id, call.Name))
                    {
                        throw new ArgumentException("An assistant message cannot contain duplicate tool call IDs.", nameof(messages));
                    }
                }
            }
            else if (message.Role == AgentRole.Tool)
            {
                if (message.ToolCallId is null
                    || !openCalls.TryGetValue(message.ToolCallId, out var expectedName))
                {
                    throw new ArgumentException("The transcript contains a tool result without a matching assistant tool call.", nameof(messages));
                }

                if (!string.Equals(message.ToolName, expectedName, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A tool result name does not match its assistant tool call.", nameof(messages));
                }

                openCalls.Remove(message.ToolCallId);
            }
        }

        if (openCalls.Count > 0)
        {
            throw new ArgumentException("The transcript ends with unresolved assistant tool calls.", nameof(messages));
        }
    }

}

public static class AgentValidation
{
    public static void ValidateMessages(IEnumerable<AgentMessage> messages, AgentLimits limits)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var validatedLimits = (limits ?? throw new ArgumentNullException(nameof(limits))).Copy();
        var copied = messages.ToArray();
        if (copied.Length > validatedLimits.MaxMessages)
        {
            throw new AgentLimitException(
                nameof(validatedLimits.MaxMessages),
                "The canonical transcript contains too many messages.");
        }

        AgentValidator.ValidateMessages(copied, validatedLimits);
    }

    public static void ValidateTranscript(IEnumerable<AgentMessage> messages, AgentLimits limits)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var validatedLimits = (limits ?? throw new ArgumentNullException(nameof(limits))).Copy();
        var copied = messages.ToArray();
        if (copied.Length > validatedLimits.MaxMessages)
        {
            throw new AgentLimitException(
                nameof(validatedLimits.MaxMessages),
                "The canonical transcript contains too many messages.");
        }

        AgentValidator.ValidateMessages(copied, validatedLimits);
        AgentValidator.ValidateTranscript(copied);
    }
}

public sealed class AgentLimitException : Exception
{
    public AgentLimitException(string limit, string message)
        : base(message)
    {
        Limit = limit;
    }

    public string Limit { get; }
}
