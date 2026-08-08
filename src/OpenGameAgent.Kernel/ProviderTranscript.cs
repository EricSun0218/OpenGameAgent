using System.Collections.ObjectModel;

namespace OpenGameAgent.Kernel;

public delegate string ProviderToolCallIdNormalizer(
    string id,
    string sourceProvider,
    string sourceApi,
    string sourceModel);

/// <summary>
/// Produces a provider-safe transcript without mutating the durable session history.
/// Opaque continuity data is retained only for the exact provider, API, and model that issued it.
/// </summary>
public static class ProviderTranscript
{
    public static IReadOnlyList<AgentMessage> Normalize(
        IEnumerable<AgentMessage> messages,
        string targetProvider,
        string targetApi,
        string targetModel,
        ProviderToolCallIdNormalizer? normalizeForeignToolCallId = null)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (string.IsNullOrWhiteSpace(targetProvider)
            || string.IsNullOrWhiteSpace(targetApi)
            || string.IsNullOrWhiteSpace(targetModel))
        {
            throw new ArgumentException("Target provider, API, and model identifiers are required.");
        }

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var transformed = new List<AgentMessage>();
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Provider transcripts cannot contain null messages.", nameof(messages));
            }

            if (message.Role == AgentRole.Tool)
            {
                var mappedId = idMap.TryGetValue(message.ToolCallId!, out var normalizedId)
                    ? normalizedId
                    : message.ToolCallId!;
                transformed.Add(CloneToolResult(message, mappedId));
                continue;
            }

            if (message.Role != AgentRole.Assistant)
            {
                transformed.Add(message);
                continue;
            }

            if (message.StopReason is ModelStopReason.Error or ModelStopReason.Aborted)
            {
                continue;
            }

            var sourceProvider = message.Provider ?? string.Empty;
            var sourceApi = message.Api ?? string.Empty;
            var sourceModel = message.Model ?? string.Empty;
            var sameModel = string.Equals(sourceProvider, targetProvider, StringComparison.Ordinal)
                            && string.Equals(sourceApi, targetApi, StringComparison.Ordinal)
                            && string.Equals(sourceModel, targetModel, StringComparison.Ordinal);
            var content = new List<AgentContent>();
            foreach (var part in message.Content)
            {
                switch (part)
                {
                    case ReasoningContent reasoning when sameModel:
                        content.Add(reasoning);
                        break;
                    case ReasoningContent reasoning when !reasoning.Redacted && !string.IsNullOrWhiteSpace(reasoning.Text):
                        content.Add(new TextContent(reasoning.Text));
                        break;
                    case ReasoningContent:
                        break;
                    case TextContent text when sameModel:
                        content.Add(text);
                        break;
                    case TextContent text:
                        content.Add(new TextContent(text.Text));
                        break;
                    case ToolCallContent call when sameModel:
                        content.Add(call);
                        break;
                    case ToolCallContent call:
                        var mappedId = normalizeForeignToolCallId is null
                            ? call.Id
                            : normalizeForeignToolCallId(call.Id, sourceProvider, sourceApi, sourceModel);
                        if (string.IsNullOrWhiteSpace(mappedId))
                        {
                            throw new InvalidDataException("A provider tool-call ID normalizer returned an empty ID.");
                        }

                        idMap[call.Id] = mappedId;
                        content.Add(new ToolCallContent(mappedId, call.Name, call.ArgumentsJson));
                        break;
                    default:
                        content.Add(part);
                        break;
                }
            }

            transformed.Add(CloneAssistant(message, content));
        }

        return RepairOrphanedToolCalls(transformed);
    }

    private static IReadOnlyList<AgentMessage> RepairOrphanedToolCalls(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<AgentMessage>();
        IReadOnlyList<ToolCallContent> pending = Array.Empty<ToolCallContent>();
        var results = new HashSet<string>(StringComparer.Ordinal);

        void FlushMissing(DateTimeOffset timestamp)
        {
            foreach (var call in pending)
            {
                if (results.Contains(call.Id))
                {
                    continue;
                }

                result.Add(AgentMessage.ToolResult(
                    call,
                    new ToolResult(
                        new AgentContent[] { new TextContent("No result provided") },
                        isError: true),
                    timestamp));
            }

            pending = Array.Empty<ToolCallContent>();
            results.Clear();
        }

        foreach (var message in messages)
        {
            if (message.Role == AgentRole.Assistant)
            {
                FlushMissing(message.Timestamp);
                pending = message.Content.OfType<ToolCallContent>().ToArray();
                result.Add(message);
            }
            else if (message.Role == AgentRole.Tool)
            {
                results.Add(message.ToolCallId!);
                result.Add(message);
            }
            else
            {
                FlushMissing(message.Timestamp);
                result.Add(message);
            }
        }

        FlushMissing(messages.Count > 0 ? messages[^1].Timestamp : DateTimeOffset.UtcNow);
        return new ReadOnlyCollection<AgentMessage>(result);
    }

    private static AgentMessage CloneToolResult(AgentMessage message, string toolCallId) => new(
        AgentRole.Tool,
        message.Content,
        message.Timestamp,
        toolCallId: toolCallId,
        toolName: message.ToolName,
        isError: message.IsError,
        detailsJson: message.DetailsJson,
        metadata: message.Metadata,
        usage: message.Usage,
        addedToolNames: message.AddedToolNames);

    private static AgentMessage CloneAssistant(AgentMessage message, IEnumerable<AgentContent> content) => new(
        AgentRole.Assistant,
        content,
        message.Timestamp,
        metadata: message.Metadata,
        model: message.Model,
        stopReason: message.StopReason,
        usage: message.Usage,
        errorMessage: message.ErrorMessage,
        provider: message.Provider,
        api: message.Api,
        responseModel: message.ResponseModel,
        responseId: message.ResponseId,
        rawStopReason: message.RawStopReason,
        endTurn: message.EndTurn,
        diagnostics: message.Diagnostics,
        deferred: message.Deferred);
}
