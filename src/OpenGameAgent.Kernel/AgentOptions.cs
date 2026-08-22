using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Kernel;

public enum QueueMode
{
    All,
    OneAtATime,
}

public sealed class AgentLimits
{
    public int MaxSystemPromptCharacters { get; set; } = 1_000_000;

    public int MaxModelNameCharacters { get; set; } = 512;

    public int MaxSessionIdCharacters { get; set; } = 1024;

    public int MaxTurns { get; set; } = 32;

    public long MaxTotalTokens { get; set; } = 1_000_000;

    public int MaxMessages { get; set; } = 1024;

    public int MaxContentPartsPerMessage { get; set; } = 128;

    public int MaxTextCharactersPerPart { get; set; } = 1_000_000;

    public int MaxJsonCharactersPerPart { get; set; } = 1_000_000;

    public int MaxResourceUriCharacters { get; set; } = 16_384;

    public int MaxBinaryDataCharactersPerPart { get; set; } = 16_000_000;

    public int MaxImagesPerMessage { get; set; } = 20;

    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxImageBytesPerMessage { get; set; } = 100 * 1024 * 1024;

    public long MaxImagePixels { get; set; } = 40_000_000;

    public int MaxToolCallsPerTurn { get; set; } = 32;

    public int MaxTools { get; set; } = 256;

    public int MaxToolNameCharacters { get; set; } = 128;

    public int MaxToolCallIdCharacters { get; set; } = 1024;

    public int MaxToolDescriptionCharacters { get; set; } = 100_000;

    public int MaxToolSchemaCharacters { get; set; } = 1_000_000;

    public int MaxMetadataEntriesPerMessage { get; set; } = 128;

    public int MaxMetadataKeyCharacters { get; set; } = 256;

    public int MaxMetadataValueCharacters { get; set; } = 16_384;

    public int MaxDiagnosticsPerMessage { get; set; } = 64;

    public int MaxAddedToolNamesPerResult { get; set; } = 256;

    public int MaxQueuedMessages { get; set; } = 64;

    public int MaxConcurrentTools { get; set; } = 8;

    /// <summary>
    /// Emits a bounded policy advisory after this many consecutive calls to the same tracked tool
    /// with the same prepared arguments. Set to zero to disable advisories.
    /// </summary>
    public int ExactToolRepeatAdvisoryThreshold { get; set; } = 3;

    /// <summary>
    /// Stops the model/tool loop before dispatching this consecutive exact repeat. Set to zero to
    /// disable repeat termination while retaining the normal turn and tool-call limits.
    /// </summary>
    public int ExactToolRepeatTerminationThreshold { get; set; } = 8;

    public int ToolTimeoutMilliseconds { get; set; } = 120_000;

    public int ModelTimeoutMilliseconds { get; set; } = 120_000;

    public int MaxProgressEventsPerTool { get; set; } = 256;

    public int MaxSubscribers { get; set; } = 32;

    internal AgentLimits Copy()
    {
        var copy = (AgentLimits)MemberwiseClone();
        copy.Validate();
        return copy;
    }

    internal void Validate()
    {
        RequireRange(MaxSystemPromptCharacters, 0, 100_000_000, nameof(MaxSystemPromptCharacters));
        RequireRange(MaxModelNameCharacters, 1, 16_384, nameof(MaxModelNameCharacters));
        RequireRange(MaxSessionIdCharacters, 1, 1_000_000, nameof(MaxSessionIdCharacters));
        RequireRange(MaxTurns, 1, 10_000, nameof(MaxTurns));
        RequireRange(MaxTotalTokens, 1, 10_000_000_000, nameof(MaxTotalTokens));
        RequireRange(MaxMessages, 1, 1_000_000, nameof(MaxMessages));
        RequireRange(MaxContentPartsPerMessage, 1, 100_000, nameof(MaxContentPartsPerMessage));
        RequireRange(MaxTextCharactersPerPart, 1, 100_000_000, nameof(MaxTextCharactersPerPart));
        RequireRange(MaxJsonCharactersPerPart, 1, 100_000_000, nameof(MaxJsonCharactersPerPart));
        RequireRange(MaxResourceUriCharacters, 1, 1_000_000, nameof(MaxResourceUriCharacters));
        RequireRange(MaxBinaryDataCharactersPerPart, 1, 100_000_000, nameof(MaxBinaryDataCharactersPerPart));
        RequireRange(MaxImagesPerMessage, 1, 1_024, nameof(MaxImagesPerMessage));
        RequireRange(MaxImageBytes, 1, 512 * 1024 * 1024, nameof(MaxImageBytes));
        RequireRange(MaxImageBytesPerMessage, MaxImageBytes, 1024 * 1024 * 1024, nameof(MaxImageBytesPerMessage));
        RequireRange(MaxImagePixels, 1, 1_000_000_000, nameof(MaxImagePixels));
        RequireRange(MaxToolCallsPerTurn, 1, 10_000, nameof(MaxToolCallsPerTurn));
        RequireRange(MaxTools, 0, 100_000, nameof(MaxTools));
        RequireRange(MaxToolNameCharacters, 1, 4096, nameof(MaxToolNameCharacters));
        RequireRange(MaxToolCallIdCharacters, 1, 1_000_000, nameof(MaxToolCallIdCharacters));
        RequireRange(MaxToolDescriptionCharacters, 1, 100_000_000, nameof(MaxToolDescriptionCharacters));
        RequireRange(MaxToolSchemaCharacters, 2, 100_000_000, nameof(MaxToolSchemaCharacters));
        RequireRange(MaxMetadataEntriesPerMessage, 0, 100_000, nameof(MaxMetadataEntriesPerMessage));
        RequireRange(MaxMetadataKeyCharacters, 1, 100_000, nameof(MaxMetadataKeyCharacters));
        RequireRange(MaxMetadataValueCharacters, 0, 100_000_000, nameof(MaxMetadataValueCharacters));
        RequireRange(MaxDiagnosticsPerMessage, 0, 10_000, nameof(MaxDiagnosticsPerMessage));
        RequireRange(MaxAddedToolNamesPerResult, 0, 100_000, nameof(MaxAddedToolNamesPerResult));
        RequireRange(MaxQueuedMessages, 1, 100_000, nameof(MaxQueuedMessages));
        RequireRange(MaxConcurrentTools, 1, 1024, nameof(MaxConcurrentTools));
        RequireRange(ExactToolRepeatAdvisoryThreshold, 0, 10_000, nameof(ExactToolRepeatAdvisoryThreshold));
        RequireRange(ExactToolRepeatTerminationThreshold, 0, 10_000, nameof(ExactToolRepeatTerminationThreshold));
        if (ExactToolRepeatAdvisoryThreshold > 0
            && ExactToolRepeatTerminationThreshold > 0
            && ExactToolRepeatTerminationThreshold <= ExactToolRepeatAdvisoryThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExactToolRepeatTerminationThreshold),
                ExactToolRepeatTerminationThreshold,
                "The exact-repeat termination threshold must be greater than the advisory threshold.");
        }
        RequireRange(ToolTimeoutMilliseconds, 1, 86_400_000, nameof(ToolTimeoutMilliseconds));
        RequireRange(ModelTimeoutMilliseconds, 1, 86_400_000, nameof(ModelTimeoutMilliseconds));
        RequireRange(MaxProgressEventsPerTool, 0, 1_000_000, nameof(MaxProgressEventsPerTool));
        RequireRange(MaxSubscribers, 0, 10_000, nameof(MaxSubscribers));
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
        }
    }

    private static void RequireRange(long value, long minimum, long maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
        }
    }
}

public sealed class AgentContext
{
    public AgentContext(
        string systemPrompt,
        IEnumerable<AgentMessage>? messages = null,
        IEnumerable<AgentTool>? tools = null)
    {
        SystemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        var copiedMessages = messages?.ToArray() ?? Array.Empty<AgentMessage>();
        var copiedTools = tools?.ToArray() ?? Array.Empty<AgentTool>();
        if (copiedMessages.Any(message => message is null) || copiedTools.Any(tool => tool is null))
        {
            throw new ArgumentException("Agent context collections cannot contain null values.");
        }

        Messages = Array.AsReadOnly(copiedMessages);
        Tools = Array.AsReadOnly(copiedTools);
    }

    public string SystemPrompt { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public IReadOnlyList<AgentTool> Tools { get; }
}

public sealed class AfterTurnContext
{
    internal AfterTurnContext(
        string runId,
        int turn,
        AgentMessage response,
        IReadOnlyList<AgentMessage> toolResults,
        AgentContext context,
        IReadOnlyList<AgentMessage> newMessages)
    {
        RunId = runId;
        Turn = turn;
        Response = response;
        ToolResults = Array.AsReadOnly(
            (toolResults ?? throw new ArgumentNullException(nameof(toolResults))).ToArray());
        Context = context ?? throw new ArgumentNullException(nameof(context));
        NewMessages = Array.AsReadOnly(
            (newMessages ?? throw new ArgumentNullException(nameof(newMessages))).ToArray());
    }

    public string RunId { get; }

    public int Turn { get; }

    public AgentMessage Response { get; }

    public IReadOnlyList<AgentMessage> ToolResults { get; }

    public AgentContext Context { get; }

    public IReadOnlyList<AgentMessage> NewMessages { get; }
}

public sealed class NextTurnUpdate
{
    public AgentContext? Context { get; set; }

    public IModelProvider? Provider { get; set; }

    public string? Model { get; set; }

    public ModelParameters? Parameters { get; set; }
}

public sealed class BeforeToolCallContext
{
    public BeforeToolCallContext(
        string runId,
        int turn,
        AgentMessage assistantMessage,
        ToolCallContent toolCall,
        System.Text.Json.JsonElement arguments,
        AgentContext context)
    {
        RunId = runId;
        Turn = turn;
        AssistantMessage = assistantMessage ?? throw new ArgumentNullException(nameof(assistantMessage));
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Arguments = arguments.Clone();
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public string RunId { get; }

    public int Turn { get; }

    public AgentMessage AssistantMessage { get; }

    public ToolCallContent ToolCall { get; }

    public System.Text.Json.JsonElement Arguments { get; }

    public AgentContext Context { get; }
}

/// <summary>
/// Immutable, final authorization context for a fully prepared tool call. This hook runs after
/// argument preparation, policy rewrites, schema validation, and conflict-key resolution, and
/// immediately before the call becomes executable. Final authorizers cannot rewrite arguments.
/// </summary>
public sealed class AuthorizeToolCallContext
{
    public AuthorizeToolCallContext(
        string runId,
        int turn,
        AgentMessage assistantMessage,
        ToolCallContent toolCall,
        System.Text.Json.JsonElement arguments,
        string? conflictKey,
        AgentContext context)
    {
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("A run ID is required.", nameof(runId))
            : runId;
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        AssistantMessage = assistantMessage ?? throw new ArgumentNullException(nameof(assistantMessage));
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Arguments = arguments.Clone();
        ConflictKey = conflictKey;
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public string RunId { get; }

    public int Turn { get; }

    public AgentMessage AssistantMessage { get; }

    public ToolCallContent ToolCall { get; }

    public System.Text.Json.JsonElement Arguments { get; }

    public string? ConflictKey { get; }

    public AgentContext Context { get; }
}

public sealed class AfterToolCallContext
{
    public AfterToolCallContext(
        string runId,
        int turn,
        AgentMessage assistantMessage,
        ToolCallContent toolCall,
        System.Text.Json.JsonElement arguments,
        ToolResult result,
        AgentContext context)
    {
        RunId = runId;
        Turn = turn;
        AssistantMessage = assistantMessage ?? throw new ArgumentNullException(nameof(assistantMessage));
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Arguments = arguments.Clone();
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public string RunId { get; }

    public int Turn { get; }

    public AgentMessage AssistantMessage { get; }

    public ToolCallContent ToolCall { get; }

    public System.Text.Json.JsonElement Arguments { get; }

    public ToolResult Result { get; }

    public AgentContext Context { get; }
}

public enum ToolExecutionDecisionKind
{
    Execute,
    ReplayResult,
    Recover,
}

/// <summary>
/// Final execution-lifecycle context. It runs after validation and authorization, immediately
/// before the tool executor is dispatched.
/// </summary>
public sealed class BeforeToolExecutionContext
{
    public BeforeToolExecutionContext(
        string runId,
        int turn,
        int toolCallIndex,
        ToolCallContent toolCall,
        System.Text.Json.JsonElement arguments,
        string? conflictKey,
        ToolRisk risk,
        ToolReplayPolicy replayPolicy,
        AgentContext context)
    {
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("A run ID is required.", nameof(runId))
            : runId;
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        ToolCallIndex = toolCallIndex >= 0 ? toolCallIndex : throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Arguments = arguments.Clone();
        ConflictKey = conflictKey;
        if (!Enum.IsDefined(typeof(ToolRisk), risk) || !Enum.IsDefined(typeof(ToolReplayPolicy), replayPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        Risk = risk;
        ReplayPolicy = replayPolicy;
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public string RunId { get; }
    public int Turn { get; }
    public int ToolCallIndex { get; }
    public ToolCallContent ToolCall { get; }
    public System.Text.Json.JsonElement Arguments { get; }
    public string? ConflictKey { get; }
    public ToolRisk Risk { get; }
    public ToolReplayPolicy ReplayPolicy { get; }
    public AgentContext Context { get; }
}

public sealed class ToolExecutionDecision
{
    private ToolExecutionDecision(ToolExecutionDecisionKind kind, ToolResult? result)
    {
        Kind = kind;
        Result = result;
    }

    public ToolExecutionDecisionKind Kind { get; }
    public ToolResult? Result { get; }

    public static ToolExecutionDecision Execute() => new(ToolExecutionDecisionKind.Execute, null);
    public static ToolExecutionDecision Recover() => new(ToolExecutionDecisionKind.Recover, null);
    public static ToolExecutionDecision Replay(ToolResult result) =>
        new(ToolExecutionDecisionKind.ReplayResult, result ?? throw new ArgumentNullException(nameof(result)));
}

public sealed class AgentHooks
{
    public Func<IReadOnlyList<AgentMessage>, CancellationToken, ValueTask<IReadOnlyList<AgentMessage>>>? TransformContextAsync { get; set; }

    public Func<ModelRequest, CancellationToken, ValueTask<ModelRequest>>? BeforeModelRequestAsync { get; set; }

    public Func<AfterTurnContext, CancellationToken, ValueTask<bool>>? ShouldStopAfterTurnAsync { get; set; }

    public Func<AfterTurnContext, CancellationToken, ValueTask<NextTurnUpdate?>>? PrepareNextTurnAsync { get; set; }

    public Func<BeforeToolCallContext, CancellationToken, ValueTask<ToolCallDecision?>>? BeforeToolCallAsync { get; set; }

    /// <summary>
    /// Final, non-rewriting authorization boundary for prepared tool calls. Every registered
    /// authorizer must allow the call. A blocked call never reaches the tool executor.
    /// </summary>
    public Func<AuthorizeToolCallContext, CancellationToken, ValueTask<ToolCallDecision?>>? AuthorizeToolCallAsync { get; set; }

    /// <summary>
    /// Optional durable execution boundary. It may replay a previously persisted result or request
    /// the tool's recovery callback, but cannot rewrite validated arguments.
    /// </summary>
    public Func<BeforeToolExecutionContext, CancellationToken, ValueTask<ToolExecutionDecision?>>? BeforeToolExecutionAsync { get; set; }

    public Func<AfterToolCallContext, CancellationToken, ValueTask<ToolResult?>>? AfterToolCallAsync { get; set; }
}

public sealed class AgentOptions
{
    public AgentOptions(IModelProvider provider, string model)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("A model name is required.", nameof(model))
            : model;
    }

    public IModelProvider Provider { get; }

    public string Model { get; set; }

    public string SystemPrompt { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public IList<AgentMessage> InitialMessages { get; } = new List<AgentMessage>();

    public IList<AgentTool> Tools { get; } = new List<AgentTool>();

    public ModelParameters Parameters { get; set; } = new();

    public AgentLimits Limits { get; set; } = new();

    public AgentHooks Hooks { get; set; } = new();

    public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;

    public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;

    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.SafeParallel;

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public Func<string> RunIdFactory { get; set; } = () => Guid.NewGuid().ToString("N");
}

public sealed class AgentState
{
    internal AgentState(
        string systemPrompt,
        IModelProvider provider,
        string model,
        string? sessionId,
        ModelParameters parameters,
        IReadOnlyList<AgentTool> tools,
        IReadOnlyList<AgentMessage> messages,
        bool isRunning,
        AgentMessage? streamingMessage,
        ModelStreamEvent? streamingEvent,
        IReadOnlyCollection<string> pendingToolCallIds,
        string? error)
    {
        SystemPrompt = systemPrompt;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Model = model;
        SessionId = sessionId;
        Parameters = parameters?.Copy() ?? throw new ArgumentNullException(nameof(parameters));
        Tools = Array.AsReadOnly(tools.ToArray());
        Messages = Array.AsReadOnly(messages.ToArray());
        IsRunning = isRunning;
        StreamingMessage = streamingMessage;
        StreamingEvent = streamingEvent;
        PendingToolCallIds = Array.AsReadOnly(pendingToolCallIds.ToArray());
        Error = error;
    }

    public string SystemPrompt { get; }

    public IModelProvider Provider { get; }

    public string Model { get; }

    public string? SessionId { get; }

    public ModelParameters Parameters { get; }

    public IReadOnlyList<AgentTool> Tools { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public bool IsRunning { get; }

    public AgentMessage? StreamingMessage { get; }

    public ModelStreamEvent? StreamingEvent { get; }

    public IReadOnlyCollection<string> PendingToolCallIds { get; }

    public string? Error { get; }
}

public sealed class AgentLoopOptions
{
    public AgentLoopOptions(IModelProvider provider, string model)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("A model name is required.", nameof(model))
            : model;
    }

    public IModelProvider Provider { get; }

    public string Model { get; set; }

    public string? SessionId { get; set; }

    public ModelParameters Parameters { get; set; } = new();

    public AgentLimits Limits { get; set; } = new();

    public AgentHooks Hooks { get; set; } = new();

    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.SafeParallel;

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public Func<string> RunIdFactory { get; set; } = () => Guid.NewGuid().ToString("N");

    public Func<CancellationToken, ValueTask<IReadOnlyList<AgentMessage>>>? GetSteeringMessagesAsync { get; set; }

    public Func<CancellationToken, ValueTask<IReadOnlyList<AgentMessage>>>? GetFollowUpMessagesAsync { get; set; }

    internal Func<IReadOnlyList<AgentMessage>>? FinalizePendingMessages { get; set; }

    internal Action? NotifyRunFinishing { get; set; }
}
