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
    Deferred,
}

public enum ModelTransport
{
    Auto,
    ServerSentEvents,
    WebSocket,
    CachedWebSocket,
}

public enum ModelCacheRetention
{
    None,
    Short,
    Long,
}

public enum ModelDeferredWindow
{
    FifteenMinutes,
    OneHour,
    TwentyFourHours,
}

public sealed class ModelCost
{
    public ModelCost(
        double input = 0,
        double output = 0,
        double cacheRead = 0,
        double cacheWrite = 0)
    {
        Input = RequireAmount(input, nameof(input));
        Output = RequireAmount(output, nameof(output));
        CacheRead = RequireAmount(cacheRead, nameof(cacheRead));
        CacheWrite = RequireAmount(cacheWrite, nameof(cacheWrite));
    }

    public double Input { get; }

    public double Output { get; }

    public double CacheRead { get; }

    public double CacheWrite { get; }

    public double Total => Input + Output + CacheRead + CacheWrite;

    internal static ModelCost Aggregate(IEnumerable<ModelCost> values)
    {
        var input = 0d;
        var output = 0d;
        var cacheRead = 0d;
        var cacheWrite = 0d;
        foreach (var value in values)
        {
            input += value.Input;
            output += value.Output;
            cacheRead += value.CacheRead;
            cacheWrite += value.CacheWrite;
        }

        return new ModelCost(input, output, cacheRead, cacheWrite);
    }

    private static double RequireAmount(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, "Model costs must be finite and non-negative.");
        }

        return value;
    }
}

public sealed class ModelUsage
{
    private const long MaximumCombinedTokens = 10_000_000_000;

    public ModelUsage(
        long inputTokens = 0,
        long outputTokens = 0,
        long cacheReadTokens = 0,
        long cacheWriteTokens = 0,
        long? reasoningTokens = null,
        long? cacheWriteOneHourTokens = null,
        ModelCost? cost = null)
        : this(
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            reasoningTokens,
            cacheWriteOneHourTokens,
            cost,
            enforceSingleReportLimit: true)
    {
    }

    private ModelUsage(
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long? reasoningTokens,
        long? cacheWriteOneHourTokens,
        ModelCost? cost,
        bool enforceSingleReportLimit)
    {
        if (inputTokens < 0 || outputTokens < 0 || cacheReadTokens < 0 || cacheWriteTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts cannot be negative.");
        }

        if (reasoningTokens is < 0 || reasoningTokens > outputTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningTokens), "Reasoning tokens must be a subset of output tokens.");
        }

        if (cacheWriteOneHourTokens is < 0 || cacheWriteOneHourTokens > cacheWriteTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheWriteOneHourTokens),
                "One-hour cache writes must be a subset of cache-write tokens.");
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
        ReasoningTokens = reasoningTokens;
        CacheWriteOneHourTokens = cacheWriteOneHourTokens;
        Cost = cost ?? new ModelCost();
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long CacheReadTokens { get; }

    public long CacheWriteTokens { get; }

    public long? ReasoningTokens { get; }

    public long? CacheWriteOneHourTokens { get; }

    public ModelCost Cost { get; }

    public long TotalTokens => checked(InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens);

    internal static ModelUsage Aggregate(IEnumerable<ModelUsage> values)
    {
        var input = 0L;
        var output = 0L;
        var cacheRead = 0L;
        var cacheWrite = 0L;
        var reasoning = 0L;
        var cacheWriteOneHour = 0L;
        var hasReasoning = false;
        var hasCacheWriteOneHour = false;
        var costs = new List<ModelCost>();
        foreach (var value in values)
        {
            input = checked(input + value.InputTokens);
            output = checked(output + value.OutputTokens);
            cacheRead = checked(cacheRead + value.CacheReadTokens);
            cacheWrite = checked(cacheWrite + value.CacheWriteTokens);
            if (value.ReasoningTokens is { } reasoningValue)
            {
                reasoning = checked(reasoning + reasoningValue);
                hasReasoning = true;
            }

            if (value.CacheWriteOneHourTokens is { } longWriteValue)
            {
                cacheWriteOneHour = checked(cacheWriteOneHour + longWriteValue);
                hasCacheWriteOneHour = true;
            }

            costs.Add(value.Cost);
        }

        return new ModelUsage(
            input,
            output,
            cacheRead,
            cacheWrite,
            hasReasoning ? reasoning : null,
            hasCacheWriteOneHour ? cacheWriteOneHour : null,
            ModelCost.Aggregate(costs),
            enforceSingleReportLimit: false);
    }
}

public sealed class ModelParameters
{
    public double? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    public string? ReasoningLevel { get; set; }

    public IReadOnlyDictionary<string, int> ReasoningBudgets { get; set; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());

    public string? SamplingParametersJson { get; set; }

    public ModelTransport Transport { get; set; } = ModelTransport.Auto;

    public ModelCacheRetention CacheRetention { get; set; } = ModelCacheRetention.Short;

    public int? WebSocketConnectTimeoutMilliseconds { get; set; }

    public bool Deferred { get; set; }

    public ModelDeferredWindow? DeferredWindow { get; set; }

    public string? MetadataJson { get; set; }

    public IReadOnlyDictionary<string, string> Extensions { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public ModelParameters Clone()
    {
        return new ModelParameters
        {
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ReasoningLevel = ReasoningLevel,
            ReasoningBudgets = new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(ReasoningBudgets ?? new Dictionary<string, int>(), StringComparer.Ordinal)),
            SamplingParametersJson = SamplingParametersJson is null
                ? null
                : JsonValue.RequireObject(SamplingParametersJson, nameof(SamplingParametersJson)),
            Transport = Transport,
            CacheRetention = CacheRetention,
            WebSocketConnectTimeoutMilliseconds = WebSocketConnectTimeoutMilliseconds,
            Deferred = Deferred,
            DeferredWindow = DeferredWindow,
            MetadataJson = MetadataJson is null
                ? null
                : JsonValue.RequireObject(MetadataJson, nameof(MetadataJson)),
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

public enum ModelDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class ModelDiagnostic
{
    public ModelDiagnostic(
        string code,
        string message,
        ModelDiagnosticSeverity severity = ModelDiagnosticSeverity.Information,
        string? dataJson = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A diagnostic code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        if (!Enum.IsDefined(typeof(ModelDiagnosticSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Code = code;
        Message = message;
        Severity = severity;
        DataJson = dataJson is null ? null : JsonValue.RequireValid(dataJson, nameof(dataJson));
    }

    public string Code { get; }

    public string Message { get; }

    public ModelDiagnosticSeverity Severity { get; }

    public string? DataJson { get; }
}

public sealed class DeferredModelHandle
{
    public DeferredModelHandle(
        string provider,
        string model,
        string api,
        string id,
        DateTimeOffset? expiresAt = null,
        int? pollAfterMilliseconds = null,
        string? dataJson = null)
    {
        Provider = RequireIdentifier(provider, nameof(provider));
        Model = RequireIdentifier(model, nameof(model));
        Api = RequireIdentifier(api, nameof(api));
        Id = RequireIdentifier(id, nameof(id));
        if (pollAfterMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pollAfterMilliseconds));
        }

        ExpiresAt = expiresAt;
        PollAfterMilliseconds = pollAfterMilliseconds;
        DataJson = dataJson is null ? null : JsonValue.RequireValid(dataJson, nameof(dataJson));
    }

    public string Provider { get; }

    public string Model { get; }

    public string Api { get; }

    public string Id { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public int? PollAfterMilliseconds { get; }

    public string? DataJson { get; }

    private static string RequireIdentifier(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty identifier is required.", name)
            : value;
}

public sealed class ModelResponse
{
    public ModelResponse(
        IEnumerable<AgentContent> content,
        ModelStopReason stopReason,
        ModelUsage? usage = null,
        string? errorMessage = null,
        string? provider = null,
        string? api = null,
        string? responseModel = null,
        string? responseId = null,
        string? rawStopReason = null,
        bool? endTurn = null,
        IEnumerable<ModelDiagnostic>? diagnostics = null,
        DeferredModelHandle? deferred = null)
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

        if (stopReason == ModelStopReason.Deferred && deferred is null)
        {
            throw new ArgumentException("A deferred response requires a deferred handle.", nameof(deferred));
        }

        if (stopReason != ModelStopReason.Deferred && deferred is not null)
        {
            throw new ArgumentException("Only a deferred response can carry a deferred handle.", nameof(deferred));
        }

        var copiedDiagnostics = diagnostics?.ToArray() ?? Array.Empty<ModelDiagnostic>();
        if (copiedDiagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Response diagnostics cannot contain null values.", nameof(diagnostics));
        }

        Content = Array.AsReadOnly(copied);
        StopReason = stopReason;
        Usage = usage ?? new ModelUsage();
        ErrorMessage = errorMessage;
        Provider = provider;
        Api = api;
        ResponseModel = responseModel;
        ResponseId = responseId;
        RawStopReason = rawStopReason;
        EndTurn = endTurn;
        Diagnostics = Array.AsReadOnly(copiedDiagnostics);
        Deferred = deferred;
    }

    public IReadOnlyList<AgentContent> Content { get; }

    public ModelStopReason StopReason { get; }

    public ModelUsage Usage { get; }

    public string? ErrorMessage { get; }

    public string? Provider { get; }

    public string? Api { get; }

    public string? ResponseModel { get; }

    public string? ResponseId { get; }

    public string? RawStopReason { get; }

    public bool? EndTurn { get; }

    public IReadOnlyList<ModelDiagnostic> Diagnostics { get; }

    public DeferredModelHandle? Deferred { get; }
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
        string? toolName,
        ToolCallContent? toolCall,
        string? content)
    {
        Kind = kind;
        Partial = partial;
        Response = response;
        Delta = delta;
        ContentIndex = contentIndex;
        ToolCallId = toolCallId;
        ToolName = toolName;
        ToolCall = toolCall;
        Content = content;
    }

    public ModelStreamEventKind Kind { get; }

    public ModelResponse? Partial { get; }

    public ModelResponse? Response { get; }

    public string? Delta { get; }

    public int ContentIndex { get; }

    public string? ToolCallId { get; }

    public string? ToolName { get; }

    public ToolCallContent? ToolCall { get; }

    public string? Content { get; }

    public bool IsTerminal => Kind == ModelStreamEventKind.Completed || Kind == ModelStreamEventKind.Failed;

    public static ModelStreamEvent Update(
        ModelStreamEventKind kind,
        ModelResponse partial,
        string? delta = null,
        int contentIndex = 0,
        string? toolCallId = null,
        string? toolName = null,
        ToolCallContent? toolCall = null,
        string? content = null)
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

        if (kind == ModelStreamEventKind.ToolCallEnded)
        {
            if (toolCall is null)
            {
                throw new ArgumentException("A tool-call end event requires the completed tool call.", nameof(toolCall));
            }

            if (toolCallId is not null && !string.Equals(toolCallId, toolCall.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException("The tool-call ID must match the completed tool call.", nameof(toolCallId));
            }

            if (toolName is not null && !string.Equals(toolName, toolCall.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException("The tool name must match the completed tool call.", nameof(toolName));
            }

            toolCallId = toolCall.Id;
            toolName = toolCall.Name;
        }
        else if (toolCall is not null)
        {
            throw new ArgumentException("Only a tool-call end event can carry a completed tool call.", nameof(toolCall));
        }

        if (kind is ModelStreamEventKind.TextEnded or ModelStreamEventKind.ReasoningEnded)
        {
            if (content is null)
            {
                throw new ArgumentException("A text or reasoning end event requires the completed content.", nameof(content));
            }
        }
        else if (content is not null)
        {
            throw new ArgumentException("Only a text or reasoning end event can carry completed content.", nameof(content));
        }

        var expectedContentKind = kind switch
        {
            ModelStreamEventKind.TextStarted or ModelStreamEventKind.TextDelta or ModelStreamEventKind.TextEnded =>
                AgentContentKind.Text,
            ModelStreamEventKind.ReasoningStarted or ModelStreamEventKind.ReasoningDelta or ModelStreamEventKind.ReasoningEnded =>
                AgentContentKind.Reasoning,
            ModelStreamEventKind.ToolCallStarted or ModelStreamEventKind.ToolCallDelta or ModelStreamEventKind.ToolCallEnded =>
                AgentContentKind.ToolCall,
            _ => (AgentContentKind?)null,
        };
        if (expectedContentKind is { } expected)
        {
            if (contentIndex >= partial.Content.Count || partial.Content[contentIndex].Kind != expected)
            {
                throw new ArgumentException(
                    "A content stream event must reference the matching block in the partial response.",
                    nameof(contentIndex));
            }

            if (partial.Content[contentIndex] is ToolCallContent partialCall)
            {
                if (toolCallId is not null && !string.Equals(toolCallId, partialCall.Id, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The tool-call ID must match the partial response.", nameof(toolCallId));
                }

                if (toolName is not null && !string.Equals(toolName, partialCall.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The tool name must match the partial response.", nameof(toolName));
                }

                toolCallId ??= partialCall.Id;
                toolName ??= partialCall.Name;

                if (toolCall is not null && !EquivalentToolCall(toolCall, partialCall))
                {
                    throw new ArgumentException(
                        "The completed tool call must match the partial response.",
                        nameof(toolCall));
                }
            }
            else if (partial.Content[contentIndex] is TextContent text && content is not null && content != text.Text)
            {
                throw new ArgumentException("The completed text must match the partial response.", nameof(content));
            }
            else if (partial.Content[contentIndex] is ReasoningContent reasoning
                     && content is not null
                     && content != reasoning.Text)
            {
                throw new ArgumentException("The completed reasoning must match the partial response.", nameof(content));
            }
        }

        return new ModelStreamEvent(
            kind,
            partial,
            null,
            delta,
            contentIndex,
            toolCallId,
            toolName,
            toolCall,
            content);
    }

    private static bool EquivalentToolCall(ToolCallContent left, ToolCallContent right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.ArgumentsJson, right.ArgumentsJson, StringComparison.Ordinal)
        && string.Equals(left.ThoughtSignature, right.ThoughtSignature, StringComparison.Ordinal)
        && string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal);

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
        return new ModelStreamEvent(kind, null, response, null, 0, null, null, null, null);
    }
}

public interface IModelProvider
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IDeferredModelProvider : IModelProvider
{
    IAsyncEnumerable<ModelStreamEvent> FetchDeferredAsync(
        DeferredModelHandle handle,
        TimeSpan wait,
        CancellationToken cancellationToken);

    ValueTask CancelDeferredAsync(
        DeferredModelHandle handle,
        CancellationToken cancellationToken);
}

public interface IModelProviderCapabilities
{
    IReadOnlyCollection<string> SupportedApis { get; }

    bool SupportsNativeDeferredTools { get; }

    bool SupportsDeferredResponses { get; }
}

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(
        string message,
        bool isTransient,
        TimeSpan? retryAfter = null,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (retryAfter is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        IsTransient = isTransient;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
        Diagnostics = Array.Empty<ModelDiagnostic>();
    }

    public ModelProviderException(
        string message,
        IEnumerable<ModelDiagnostic>? diagnostics,
        Exception? innerException = null)
        : this(message, diagnostics, false, null, null, innerException)
    {
    }

    public ModelProviderException(
        string message,
        IEnumerable<ModelDiagnostic>? diagnostics,
        bool isTransient,
        TimeSpan? retryAfter = null,
        int? statusCode = null,
        Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? "The model provider failed." : message, innerException)
    {
        if (retryAfter is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        var copied = diagnostics?.ToArray() ?? Array.Empty<ModelDiagnostic>();
        if (copied.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Provider failure diagnostics cannot contain null values.", nameof(diagnostics));
        }

        IsTransient = isTransient;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
        Diagnostics = Array.AsReadOnly(copied);
    }

    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }

    public int? StatusCode { get; }

    public IReadOnlyList<ModelDiagnostic> Diagnostics { get; }
}
