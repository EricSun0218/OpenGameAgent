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
        int targetMessageCount)
    {
        if (targetMessageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMessageCount));
        }

        Session = session.EnsureValid(nameof(session));
        var copiedMessages = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A transcript cannot contain null messages.", nameof(messages));
        }

        Messages = Array.AsReadOnly(copiedMessages);
        TargetMessageCount = targetMessageCount;
    }

    public GameSessionKey Session { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public int TargetMessageCount { get; }
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

        if (context.Messages.Count <= context.TargetMessageCount)
        {
            return context.Messages;
        }

        var keepCount = Math.Max(1, context.TargetMessageCount - 1);
        var start = FindSafeSuffixStart(context.Messages, keepCount);
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

        ValidateToolExchanges(result);
        return result;
    }

    private static int FindSafeSuffixStart(IReadOnlyList<AgentMessage> messages, int keepCount)
    {
        var desired = Math.Max(1, messages.Count - keepCount);
        for (var index = desired; index < messages.Count; index++)
        {
            if (messages[index].Role is AgentRole.User or AgentRole.Custom)
            {
                return index;
            }
        }

        return -1;
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
