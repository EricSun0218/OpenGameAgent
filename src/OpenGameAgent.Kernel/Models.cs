using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace OpenGameAgent.Kernel;

public enum ModelStopReason
{
    Pending,
    Stop,
    ToolUse,
    Length,
    Error,
    Aborted,
}

public sealed class ModelUsage
{
    private const long MaximumCombinedTokens = 10_000_000_000;

    public ModelUsage(long inputTokens = 0, long outputTokens = 0, long cacheReadTokens = 0, long cacheWriteTokens = 0)
        : this(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, enforceSingleReportLimit: true)
    {
    }

    private ModelUsage(
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        bool enforceSingleReportLimit)
    {
        if (inputTokens < 0 || outputTokens < 0 || cacheReadTokens < 0 || cacheWriteTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts cannot be negative.");
        }

        try
        {
            var total = checked(inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens);
            if (enforceSingleReportLimit && total > MaximumCombinedTokens)
            {
                throw new ArgumentOutOfRangeException(nameof(inputTokens), "The combined token count is too large.");
            }
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "The combined token count is too large.");
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long CacheReadTokens { get; }

    public long CacheWriteTokens { get; }

    public long TotalTokens => checked(InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens);

    internal static ModelUsage Aggregate(IEnumerable<ModelUsage> values)
    {
        var input = 0L;
        var output = 0L;
        var cacheRead = 0L;
        var cacheWrite = 0L;
        foreach (var value in values)
        {
            input = checked(input + value.InputTokens);
            output = checked(output + value.OutputTokens);
            cacheRead = checked(cacheRead + value.CacheReadTokens);
            cacheWrite = checked(cacheWrite + value.CacheWriteTokens);
        }

        return new ModelUsage(input, output, cacheRead, cacheWrite, enforceSingleReportLimit: false);
    }
}

public sealed class ModelParameters
{
    public double? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    public string? ReasoningLevel { get; set; }

    public IReadOnlyDictionary<string, string> Extensions { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public ModelParameters Clone()
    {
        return new ModelParameters
        {
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ReasoningLevel = ReasoningLevel,
            Extensions = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(Extensions ?? new Dictionary<string, string>(), StringComparer.Ordinal)),
        };
    }

    internal ModelParameters Copy() => Clone();
}

public sealed class ModelRequest
{
    public ModelRequest(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters,
        string? sessionId,
        string runId,
        int turn)
    {
        Model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("A model name is required.", nameof(model))
            : model;
        SystemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        var copiedMessages = messages?.ToArray() ?? throw new ArgumentNullException(nameof(messages));
        var copiedTools = tools?.ToArray() ?? throw new ArgumentNullException(nameof(tools));
        if (copiedMessages.Any(message => message is null) || copiedTools.Any(tool => tool is null))
        {
            throw new ArgumentException("Model request collections cannot contain null values.");
        }

        Messages = Array.AsReadOnly(copiedMessages);
        Tools = Array.AsReadOnly(copiedTools);
        Parameters = parameters?.Copy() ?? throw new ArgumentNullException(nameof(parameters));
        SessionId = sessionId;
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("A run ID is required.", nameof(runId))
            : runId;
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
    }

    public string Model { get; }

    public string SystemPrompt { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public ModelParameters Parameters { get; }

    public string? SessionId { get; }

    public string RunId { get; }

    public int Turn { get; }
}

public sealed class ModelResponse
{
    public ModelResponse(
        IEnumerable<AgentContent> content,
        ModelStopReason stopReason,
        ModelUsage? usage = null,
        string? errorMessage = null)
    {
        if (!Enum.IsDefined(typeof(ModelStopReason), stopReason))
        {
            throw new ArgumentOutOfRangeException(nameof(stopReason));
        }

        var copied = content?.ToArray() ?? throw new ArgumentNullException(nameof(content));
        if (copied.Any(part => part is null))
        {
            throw new ArgumentException("Model response content cannot contain null parts.", nameof(content));
        }

        var toolCallCount = copied.Count(part => part is ToolCallContent);
        if (stopReason == ModelStopReason.ToolUse && toolCallCount == 0)
        {
            throw new ArgumentException("A tool-use response must contain at least one tool call.", nameof(content));
        }

        if (stopReason == ModelStopReason.Stop && toolCallCount > 0)
        {
            throw new ArgumentException("A stopped response cannot contain tool calls.", nameof(content));
        }

        if ((stopReason == ModelStopReason.Error || stopReason == ModelStopReason.Aborted)
            && string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("An error or aborted response requires an error message.", nameof(errorMessage));
        }

        if (stopReason is not ModelStopReason.Error and not ModelStopReason.Aborted && errorMessage is not null)
        {
            throw new ArgumentException("Only an error or aborted response can carry an error message.", nameof(errorMessage));
        }

        Content = Array.AsReadOnly(copied);
        StopReason = stopReason;
        Usage = usage ?? new ModelUsage();
        ErrorMessage = errorMessage;
    }

    public IReadOnlyList<AgentContent> Content { get; }

    public ModelStopReason StopReason { get; }

    public ModelUsage Usage { get; }

    public string? ErrorMessage { get; }
}

public enum ModelStreamEventKind
{
    Started,
    TextStarted,
    TextDelta,
    TextEnded,
    ReasoningStarted,
    ReasoningDelta,
    ReasoningEnded,
    ToolCallStarted,
    ToolCallDelta,
    ToolCallEnded,
    Completed,
    Failed,
}

public sealed class ModelStreamEvent
{
    private ModelStreamEvent(
        ModelStreamEventKind kind,
        ModelResponse? partial,
        ModelResponse? response,
        string? delta,
        int contentIndex,
        string? toolCallId,
        string? toolName)
    {
        Kind = kind;
        Partial = partial;
        Response = response;
        Delta = delta;
        ContentIndex = contentIndex;
        ToolCallId = toolCallId;
        ToolName = toolName;
    }

    public ModelStreamEventKind Kind { get; }

    public ModelResponse? Partial { get; }

    public ModelResponse? Response { get; }

    public string? Delta { get; }

    public int ContentIndex { get; }

    public string? ToolCallId { get; }

    public string? ToolName { get; }

    public bool IsTerminal => Kind == ModelStreamEventKind.Completed || Kind == ModelStreamEventKind.Failed;

    public static ModelStreamEvent Update(
        ModelStreamEventKind kind,
        ModelResponse partial,
        string? delta = null,
        int contentIndex = 0,
        string? toolCallId = null,
        string? toolName = null)
    {
        if (kind == ModelStreamEventKind.Completed || kind == ModelStreamEventKind.Failed)
        {
            throw new ArgumentException("Use Terminal for a terminal stream event.", nameof(kind));
        }

        if (!Enum.IsDefined(typeof(ModelStreamEventKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (contentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentIndex));
        }

        if (partial is null)
        {
            throw new ArgumentNullException(nameof(partial));
        }

        if (partial.StopReason != ModelStopReason.Pending)
        {
            throw new ArgumentException("A partial stream response must have a pending stop reason.", nameof(partial));
        }

        if (kind is ModelStreamEventKind.TextDelta
                or ModelStreamEventKind.ReasoningDelta
                or ModelStreamEventKind.ToolCallDelta
            && delta is null)
        {
            throw new ArgumentException("A delta stream event requires delta content.", nameof(delta));
        }

        return new ModelStreamEvent(
            kind,
            partial,
            null,
            delta,
            contentIndex,
            toolCallId,
            toolName);
    }

    public static ModelStreamEvent Terminal(ModelResponse response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (response.StopReason == ModelStopReason.Pending)
        {
            throw new ArgumentException("A terminal stream response cannot have a pending stop reason.", nameof(response));
        }

        var kind = response.StopReason == ModelStopReason.Error || response.StopReason == ModelStopReason.Aborted
            ? ModelStreamEventKind.Failed
            : ModelStreamEventKind.Completed;
        return new ModelStreamEvent(kind, null, response, null, 0, null, null);
    }
}

public interface IModelProvider
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}
