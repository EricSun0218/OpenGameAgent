using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenGameAgent.Kernel;

public enum AgentRole
{
    User,
    Assistant,
    Tool,
    Custom,
}

public sealed class AgentMessage
{
    public AgentMessage(
        AgentRole role,
        IEnumerable<AgentContent> content,
        DateTimeOffset timestamp,
        string? customRole = null,
        string? toolCallId = null,
        string? toolName = null,
        bool isError = false,
        string? detailsJson = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? model = null,
        ModelStopReason? stopReason = null,
        ModelUsage? usage = null,
        string? errorMessage = null,
        string? provider = null,
        string? api = null,
        string? responseModel = null,
        string? responseId = null,
        string? rawStopReason = null,
        bool? endTurn = null,
        IEnumerable<ModelDiagnostic>? diagnostics = null,
        DeferredModelHandle? deferred = null,
        IEnumerable<string>? addedToolNames = null)
    {
        if (!Enum.IsDefined(typeof(AgentRole), role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (stopReason is { } reason && !Enum.IsDefined(typeof(ModelStopReason), reason))
        {
            throw new ArgumentOutOfRangeException(nameof(stopReason));
        }

        if (role == AgentRole.Custom && string.IsNullOrWhiteSpace(customRole))
        {
            throw new ArgumentException("A custom message requires a custom role.", nameof(customRole));
        }

        if (role != AgentRole.Custom && customRole is not null)
        {
            throw new ArgumentException("Only a custom message can carry a custom role.", nameof(customRole));
        }

        if (role == AgentRole.Tool && (string.IsNullOrWhiteSpace(toolCallId) || string.IsNullOrWhiteSpace(toolName)))
        {
            throw new ArgumentException("A tool result message requires a tool call ID and tool name.");
        }

        if (role != AgentRole.Tool
            && (toolCallId is not null
                || toolName is not null
                || isError
                || detailsJson is not null
                || addedToolNames is not null))
        {
            throw new ArgumentException("Only a tool result message can carry tool-result fields.");
        }

        if (role != AgentRole.Assistant
            && (model is not null
                || stopReason is not null
                || errorMessage is not null
                || provider is not null
                || api is not null
                || responseModel is not null
                || responseId is not null
                || rawStopReason is not null
                || endTurn is not null
                || diagnostics is not null
                || deferred is not null))
        {
            throw new ArgumentException("Only an assistant message can carry model response fields.");
        }

        if (role == AgentRole.Assistant
            && stopReason is ModelStopReason.Error or ModelStopReason.Aborted
            && string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("An error or aborted assistant message requires an error message.", nameof(errorMessage));
        }

        if (role == AgentRole.Assistant
            && stopReason is not ModelStopReason.Error and not ModelStopReason.Aborted
            && errorMessage is not null)
        {
            throw new ArgumentException("Only an error or aborted assistant message can carry an error message.", nameof(errorMessage));
        }

        if (role is not AgentRole.Assistant and not AgentRole.Tool && usage is not null)
        {
            throw new ArgumentException("Only an assistant or tool result message can carry usage.", nameof(usage));
        }

        if (role == AgentRole.Assistant && stopReason == ModelStopReason.Deferred && deferred is null)
        {
            throw new ArgumentException("A deferred assistant message requires a deferred handle.", nameof(deferred));
        }

        if (role == AgentRole.Assistant && stopReason != ModelStopReason.Deferred && deferred is not null)
        {
            throw new ArgumentException("Only a deferred assistant message can carry a deferred handle.", nameof(deferred));
        }

        var copiedContent = content?.ToArray() ?? throw new ArgumentNullException(nameof(content));
        if (copiedContent.Any(part => part is null))
        {
            throw new ArgumentException("Message content cannot contain null parts.", nameof(content));
        }

        if (role != AgentRole.Assistant
            && copiedContent.Any(part => part is ToolCallContent or ReasoningContent))
        {
            throw new ArgumentException(
                "Only an assistant message can contain reasoning or tool-call content.",
                nameof(content));
        }

        Role = role;
        Content = Array.AsReadOnly(copiedContent);
        Timestamp = timestamp;
        CustomRole = customRole;
        ToolCallId = toolCallId;
        ToolName = toolName;
        IsError = isError;
        DetailsJson = detailsJson is null ? null : JsonValue.RequireValid(detailsJson, nameof(detailsJson));
        var copiedMetadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        if (copiedMetadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Message metadata requires non-empty keys and non-null values.", nameof(metadata));
        }

        Metadata = new ReadOnlyDictionary<string, string>(copiedMetadata);
        var copiedDiagnostics = diagnostics?.ToArray() ?? Array.Empty<ModelDiagnostic>();
        if (copiedDiagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Message diagnostics cannot contain null values.", nameof(diagnostics));
        }

        var copiedAddedTools = addedToolNames?.ToArray() ?? Array.Empty<string>();
        if (copiedAddedTools.Any(string.IsNullOrWhiteSpace)
            || copiedAddedTools.Distinct(StringComparer.Ordinal).Count() != copiedAddedTools.Length)
        {
            throw new ArgumentException("Added tool names must be non-empty and unique.", nameof(addedToolNames));
        }

        Model = model;
        StopReason = stopReason;
        Usage = usage;
        ErrorMessage = errorMessage;
        Provider = provider;
        Api = api;
        ResponseModel = responseModel;
        ResponseId = responseId;
        RawStopReason = rawStopReason;
        EndTurn = endTurn;
        Diagnostics = Array.AsReadOnly(copiedDiagnostics);
        Deferred = deferred;
        AddedToolNames = Array.AsReadOnly(copiedAddedTools);
    }

    public AgentRole Role { get; }

    public IReadOnlyList<AgentContent> Content { get; }

    public DateTimeOffset Timestamp { get; }

    public string? CustomRole { get; }

    public string? ToolCallId { get; }

    public string? ToolName { get; }

    public bool IsError { get; }

    public string? DetailsJson { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public string? Model { get; }

    public ModelStopReason? StopReason { get; }

    public ModelUsage? Usage { get; }

    public string? ErrorMessage { get; }

    public string? Provider { get; }

    public string? Api { get; }

    public string? ResponseModel { get; }

    public string? ResponseId { get; }

    public string? RawStopReason { get; }

    public bool? EndTurn { get; }

    public IReadOnlyList<ModelDiagnostic> Diagnostics { get; }

    public DeferredModelHandle? Deferred { get; }

    public IReadOnlyList<string> AddedToolNames { get; }

    public static AgentMessage User(
        string text,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            AgentRole.User,
            new AgentContent[] { new TextContent(text) },
            timestamp ?? DateTimeOffset.UtcNow,
            metadata: metadata);

    public static AgentMessage UserJson(
        string json,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            AgentRole.User,
            new AgentContent[] { new JsonContent(json) },
            timestamp ?? DateTimeOffset.UtcNow,
            metadata: metadata);

    public static AgentMessage ToolResult(
        ToolCallContent call,
        ToolResult result,
        DateTimeOffset timestamp)
    {
        if (call is null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return new AgentMessage(
            AgentRole.Tool,
            result.Content,
            timestamp,
            toolCallId: call.Id,
            toolName: call.Name,
            isError: result.IsError,
            detailsJson: result.DetailsJson,
            usage: result.Usage,
            addedToolNames: result.AddedToolNames);
    }
}
