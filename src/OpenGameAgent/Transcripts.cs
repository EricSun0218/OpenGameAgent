using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public sealed class GameTranscriptCompactionContext
{
    public GameTranscriptCompactionContext(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        int targetMessageCount,
        long? targetEstimatedTokens = null,
        GameTranscriptTokenEstimator? tokenEstimator = null)
    {
        if (targetMessageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMessageCount));
        }

        if (targetEstimatedTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetEstimatedTokens));
        }

        if (targetEstimatedTokens is not null && tokenEstimator is null)
        {
            throw new ArgumentException(
                "A token estimator is required when a token target is configured.",
                nameof(tokenEstimator));
        }

        Session = session.EnsureValid(nameof(session));
        var copiedMessages = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A transcript cannot contain null messages.", nameof(messages));
        }

        Messages = Array.AsReadOnly(copiedMessages);
        TargetMessageCount = targetMessageCount;
        TargetEstimatedTokens = targetEstimatedTokens;
        TokenEstimator = tokenEstimator;
    }

    public GameSessionKey Session { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public int TargetMessageCount { get; }

    public long? TargetEstimatedTokens { get; }

    public GameTranscriptTokenEstimator? TokenEstimator { get; }
}

public delegate long GameTranscriptTokenEstimator(IReadOnlyList<AgentMessage> messages);

public delegate long GameModelRequestTokenEstimator(
    string model,
    string systemPrompt,
    IReadOnlyList<AgentMessage> messages,
    IReadOnlyList<ToolDefinition> tools);

public static class ApproximateGameTokenEstimator
{
    private const long ResourceTokenEstimate = 1_200;

    public static long EstimateRequest(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model name is required.", nameof(model));
        }

        if (systemPrompt is null)
        {
            throw new ArgumentNullException(nameof(systemPrompt));
        }

        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var characters = (long)systemPrompt.Length;
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("Tool collections cannot contain null values.", nameof(tools));
            }

            characters = checked(characters
                + tool.Name.Length
                + tool.Description.Length
                + tool.InputSchemaJson.Length
                + 128);
        }

        return checked(EstimateMessages(messages) + DivideRoundUp(characters, 4));
    }

    public static long EstimateMessages(IReadOnlyList<AgentMessage> messages)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var tokens = 0L;
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Message collections cannot contain null values.", nameof(messages));
            }

            var characters = 64L;
            characters = checked(characters
                + (message.CustomRole?.Length ?? 0)
                + (message.ToolCallId?.Length ?? 0)
                + (message.ToolName?.Length ?? 0)
                + (message.DetailsJson?.Length ?? 0)
                + (message.Model?.Length ?? 0)
                + (message.ErrorMessage?.Length ?? 0));
            foreach (var pair in message.Metadata)
            {
                characters = checked(characters + pair.Key.Length + pair.Value.Length);
            }

            foreach (var content in message.Content)
            {
                switch (content)
                {
                    case TextContent text:
                        characters = checked(characters + text.Text.Length);
                        break;
                    case JsonContent json:
                        characters = checked(characters + json.Json.Length);
                        break;
                    case ReasoningContent reasoning:
                        characters = checked(characters + reasoning.Text.Length + (reasoning.Signature?.Length ?? 0));
                        break;
                    case ToolCallContent call:
                        characters = checked(characters + call.Id.Length + call.Name.Length + call.ArgumentsJson.Length);
                        break;
                    case ResourceContent resource:
                        characters = checked(characters
                            + resource.Uri.Length
                            + resource.MediaType.Length
                            + (resource.Name?.Length ?? 0));
                        tokens = checked(tokens + ResourceTokenEstimate);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported agent content type '{content.GetType().FullName}'.");
                }
            }

            tokens = checked(tokens + DivideRoundUp(characters, 4));
        }

        return tokens;
    }

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);
}

public interface IGameTranscriptCompactor
{
    ValueTask<IReadOnlyList<AgentMessage>> CompactAsync(
        GameTranscriptCompactionContext context,
        CancellationToken cancellationToken);
}

public delegate ValueTask<string> GameTranscriptSummarizer(
    GameSessionKey session,
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

public sealed class SummarizingGameTranscriptCompactor : IGameTranscriptCompactor
{
    private readonly GameTranscriptSummarizer _summarizer;

    public SummarizingGameTranscriptCompactor(GameTranscriptSummarizer summarizer)
    {
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
    }

    public async ValueTask<IReadOnlyList<AgentMessage>> CompactAsync(
        GameTranscriptCompactionContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (Fits(context, context.Messages))
        {
            return context.Messages;
        }

        var keepCount = Math.Max(1, context.TargetMessageCount - 1);
        var start = FindSafeSuffixStart(context, keepCount);
        if (start == 0)
        {
            throw new InvalidOperationException("The transcript cannot be compacted without splitting a tool exchange.");
        }

        if (start < 0)
        {
            // No complete conversational suffix fits. Summarizing the entire
            // transcript is still safe and leaves a single canonical message.
            start = context.Messages.Count;
        }

        var removed = context.Messages.Take(start).ToArray();
        var summary = await _summarizer(context.Session, removed, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("The transcript summarizer returned an empty summary.");
        }

        var summaryMessage = new AgentMessage(
            AgentRole.Custom,
            new AgentContent[] { new TextContent(summary) },
            DateTimeOffset.UtcNow,
            customRole: "transcript_summary",
            metadata: new Dictionary<string, string>
            {
                ["game.compacted_message_count"] = removed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        var result = new[] { summaryMessage }.Concat(context.Messages.Skip(start)).ToArray();
        if (result.Length > context.TargetMessageCount)
        {
            throw new InvalidOperationException("The transcript compactor exceeded its requested target.");
        }

        if (context.TargetEstimatedTokens is { } tokenTarget
            && Estimate(context, result) > tokenTarget)
        {
            throw new InvalidOperationException("The transcript compactor exceeded its requested token target.");
        }

        ValidateToolExchanges(result);
        return result;
    }

    private static int FindSafeSuffixStart(GameTranscriptCompactionContext context, int keepCount)
    {
        var messages = context.Messages;
        var desired = Math.Max(1, messages.Count - keepCount);
        for (var index = desired; index < messages.Count; index++)
        {
            if (messages[index].Role is AgentRole.User or AgentRole.Custom)
            {
                var projectedCount = checked(messages.Count - index + 1);
                if (projectedCount > context.TargetMessageCount)
                {
                    continue;
                }

                if (context.TargetEstimatedTokens is { } tokenTarget)
                {
                    // Leave half of the transcript budget available to the summary. This is
                    // conservative and the completed summary is checked again below.
                    var suffixTarget = Math.Max(1, tokenTarget / 2);
                    if (Estimate(context, messages.Skip(index).ToArray()) > suffixTarget)
                    {
                        continue;
                    }
                }

                return index;
            }
        }

        return -1;
    }

    private static bool Fits(GameTranscriptCompactionContext context, IReadOnlyList<AgentMessage> messages) =>
        messages.Count <= context.TargetMessageCount
        && (context.TargetEstimatedTokens is not { } target || Estimate(context, messages) <= target);

    private static long Estimate(GameTranscriptCompactionContext context, IReadOnlyList<AgentMessage> messages)
    {
        var estimate = context.TokenEstimator!(messages);
        return estimate >= 0
            ? estimate
            : throw new InvalidOperationException("The transcript token estimator returned a negative value.");
    }

    private static void ValidateToolExchanges(IReadOnlyList<AgentMessage> messages)
    {
        var openCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == AgentRole.Assistant)
            {
                foreach (var call in message.Content.OfType<ToolCallContent>())
                {
                    openCalls.Add(call.Id);
                }
            }
            else if (message.Role == AgentRole.Tool && message.ToolCallId is { } callId)
            {
                if (!openCalls.Remove(callId))
                {
                    throw new InvalidOperationException("The compacted transcript contains an orphan tool result.");
                }
            }
        }

        if (openCalls.Count > 0)
        {
            throw new InvalidOperationException("The compacted transcript contains an unresolved tool call.");
        }
    }
}
