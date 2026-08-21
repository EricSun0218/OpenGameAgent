using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenGameAgent.Kernel;

public enum AgentEventKind
{
    RunStarted,
    TurnStarted,
    MessageStarted,
    MessageUpdated,
    MessageEnded,
    ToolStarted,
    ToolProgressed,
    ToolEnded,
    TurnEnded,
    RunFaulted,
    RunEnded,
    ModelRequestStarted,
}

public enum AgentRunStatus
{
    Completed,
    Stopped,
    Aborted,
    ProviderError,
    LimitExceeded,
    KernelError,
}

public sealed class AgentEvent
{
    internal AgentEvent(
        AgentEventKind kind,
        string runId,
        int turn = 0,
        AgentMessage? message = null,
        ModelStreamEvent? modelEvent = null,
        ModelRequest? modelRequest = null,
        ToolCallContent? toolCall = null,
        ToolProgress? progress = null,
        ToolResult? toolResult = null,
        string? error = null,
        AgentRunStatus? status = null,
        IReadOnlyList<AgentMessage>? messages = null)
    {
        Kind = kind;
        RunId = runId;
        Turn = turn;
        Message = message;
        ModelEvent = modelEvent;
        ModelRequest = modelRequest;
        ToolCall = toolCall;
        Progress = progress;
        ToolResult = toolResult;
        Error = error;
        Status = status;
        Messages = messages is null
            ? Array.Empty<AgentMessage>()
            : Array.AsReadOnly(messages.ToArray());
    }

    public AgentEventKind Kind { get; }

    public string RunId { get; }

    public int Turn { get; }

    public AgentMessage? Message { get; }

    public ModelStreamEvent? ModelEvent { get; }

    public ModelRequest? ModelRequest { get; }

    public ToolCallContent? ToolCall { get; }

    public ToolProgress? Progress { get; }

    public ToolResult? ToolResult { get; }

    public string? Error { get; }

    public AgentRunStatus? Status { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }
}

public sealed class AgentRunResult
{
    internal AgentRunResult(
        string runId,
        AgentRunStatus status,
        IReadOnlyList<AgentMessage> newMessages,
        int turns,
        int toolCalls,
        string? error = null,
        IReadOnlyList<string>? subscriberErrors = null)
    {
        RunId = runId;
        Status = status;
        NewMessages = Array.AsReadOnly(
            (newMessages ?? throw new ArgumentNullException(nameof(newMessages))).ToArray());
        Turns = turns;
        ToolCalls = toolCalls;
        Error = error;
        SubscriberErrors = subscriberErrors is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(subscriberErrors.ToArray());
    }

    public string RunId { get; }

    public AgentRunStatus Status { get; }

    public IReadOnlyList<AgentMessage> NewMessages { get; }

    public int Turns { get; }

    public int ToolCalls { get; }

    public string? Error { get; }

    public IReadOnlyList<string> SubscriberErrors { get; }

    public ModelUsage Usage
    {
        get
        {
            var usage = NewMessages
                .Where(message => message.Role is AgentRole.Assistant or AgentRole.Tool && message.Usage is not null)
                .Select(message => message.Usage!)
                .ToArray();
            return ModelUsage.Aggregate(usage);
        }
    }

    public bool Succeeded =>
        Status is AgentRunStatus.Completed or AgentRunStatus.Stopped
        && SubscriberErrors.Count == 0;

    internal AgentRunResult WithSubscriberErrors(IReadOnlyList<string> errors) =>
        new(
            RunId,
            Status,
            NewMessages,
            Turns,
            ToolCalls,
            Error ?? "One or more agent event subscribers failed.",
            errors);
}
