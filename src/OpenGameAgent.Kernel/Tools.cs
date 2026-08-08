using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Kernel;

public enum ToolRisk
{
    ReadOnly,
    IdempotentWrite,
    NonIdempotentWrite,
}

public enum ToolExecutionMode
{
    SafeParallel,
    Sequential,
    Parallel,
}

public enum ToolConstrainedSamplingKind
{
    JsonSchema,
    Grammar,
}

public enum ToolSchemaStrictness
{
    Prefer,
    Require,
}

public sealed class ToolConstrainedSampling
{
    private ToolConstrainedSampling(
        ToolConstrainedSamplingKind kind,
        ToolSchemaStrictness? strictness,
        string? openAiLark,
        string? openAiRegex)
    {
        Kind = kind;
        Strictness = strictness;
        OpenAiLark = openAiLark;
        OpenAiRegex = openAiRegex;
    }

    public ToolConstrainedSamplingKind Kind { get; }

    public ToolSchemaStrictness? Strictness { get; }

    public string? OpenAiLark { get; }

    public string? OpenAiRegex { get; }

    public static ToolConstrainedSampling JsonSchema(ToolSchemaStrictness strictness = ToolSchemaStrictness.Prefer)
    {
        if (!Enum.IsDefined(typeof(ToolSchemaStrictness), strictness))
        {
            throw new ArgumentOutOfRangeException(nameof(strictness));
        }

        return new ToolConstrainedSampling(ToolConstrainedSamplingKind.JsonSchema, strictness, null, null);
    }

    public static ToolConstrainedSampling Grammar(string? openAiLark = null, string? openAiRegex = null)
    {
        if (string.IsNullOrWhiteSpace(openAiLark) && string.IsNullOrWhiteSpace(openAiRegex))
        {
            throw new ArgumentException("At least one grammar variant is required.");
        }

        return new ToolConstrainedSampling(ToolConstrainedSamplingKind.Grammar, null, openAiLark, openAiRegex);
    }
}

public sealed class ToolDefinition
{
    public ToolDefinition(
        string name,
        string description,
        string inputSchemaJson,
        ToolConstrainedSampling? constrainedSampling = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A tool description is required.", nameof(description));
        }

        Name = name;
        Description = description;
        InputSchemaJson = JsonValue.RequireObject(inputSchemaJson, nameof(inputSchemaJson));
        ConstrainedSampling = constrainedSampling;
    }

    public string Name { get; }

    public string Description { get; }

    public string InputSchemaJson { get; }

    public ToolConstrainedSampling? ConstrainedSampling { get; }
}

public sealed class ToolProgress
{
    public ToolProgress(
        string? message = null,
        double? fraction = null,
        string? detailsJson = null,
        IEnumerable<AgentContent>? content = null)
    {
        if (fraction is { } value
            && (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "A progress fraction must be between 0 and 1.");
        }

        Message = message;
        Fraction = fraction;
        DetailsJson = detailsJson is null ? null : JsonValue.RequireValid(detailsJson, nameof(detailsJson));
        var copied = content?.ToArray() ?? Array.Empty<AgentContent>();
        if (copied.Any(part => part is null))
        {
            throw new ArgumentException("Tool progress content cannot contain null parts.", nameof(content));
        }

        if (copied.Any(part => part is ReasoningContent or ToolCallContent))
        {
            throw new ArgumentException(
                "Tool progress cannot contain assistant-only reasoning or tool-call parts.",
                nameof(content));
        }

        Content = Array.AsReadOnly(copied);
    }

    public string? Message { get; }

    public double? Fraction { get; }

    public string? DetailsJson { get; }

    public IReadOnlyList<AgentContent> Content { get; }
}

public sealed class ToolResult
{
    public ToolResult(
        IEnumerable<AgentContent> content,
        bool isError = false,
        string? detailsJson = null,
        bool terminate = false,
        ModelUsage? usage = null,
        bool outcomeUncertain = false,
        IEnumerable<string>? addedToolNames = null)
    {
        var copied = content?.ToArray() ?? throw new ArgumentNullException(nameof(content));
        if (copied.Any(part => part is null))
        {
            throw new ArgumentException("Tool result content cannot contain null parts.", nameof(content));
        }

        if (copied.Any(part => part is ReasoningContent or ToolCallContent))
        {
            throw new ArgumentException(
                "Tool results cannot contain assistant-only reasoning or tool-call parts.",
                nameof(content));
        }

        Content = Array.AsReadOnly(copied);
        IsError = isError;
        DetailsJson = detailsJson is null ? null : JsonValue.RequireValid(detailsJson, nameof(detailsJson));
        Terminate = terminate;
        Usage = usage;
        OutcomeUncertain = outcomeUncertain;
        var copiedAddedTools = addedToolNames?.ToArray() ?? Array.Empty<string>();
        if (copiedAddedTools.Any(string.IsNullOrWhiteSpace)
            || copiedAddedTools.Distinct(StringComparer.Ordinal).Count() != copiedAddedTools.Length)
        {
            throw new ArgumentException("Added tool names must be non-empty and unique.", nameof(addedToolNames));
        }

        AddedToolNames = Array.AsReadOnly(copiedAddedTools);
    }

    public IReadOnlyList<AgentContent> Content { get; }

    public bool IsError { get; }

    public string? DetailsJson { get; }

    public bool Terminate { get; }

    public ModelUsage? Usage { get; }

    public bool OutcomeUncertain { get; }

    public IReadOnlyList<string> AddedToolNames { get; }

    public static ToolResult Error(string message) =>
        new(new AgentContent[] { new TextContent(message ?? string.Empty) }, isError: true);
}

public sealed class ToolExecutionContext
{
    private readonly Func<ToolProgress, CancellationToken, ValueTask> _reportProgress;

    internal ToolExecutionContext(
        string runId,
        int turn,
        int toolCallIndex,
        ToolCallContent call,
        Func<ToolProgress, CancellationToken, ValueTask> reportProgress)
    {
        RunId = runId;
        Turn = turn;
        ToolCallIndex = toolCallIndex >= 0
            ? toolCallIndex
            : throw new ArgumentOutOfRangeException(nameof(toolCallIndex));
        Call = call;
        _reportProgress = reportProgress;
    }

    public string RunId { get; }

    public int Turn { get; }

    public int ToolCallIndex { get; }

    public ToolCallContent Call { get; }

    public ValueTask ReportProgressAsync(ToolProgress progress, CancellationToken cancellationToken = default) =>
        _reportProgress(progress ?? throw new ArgumentNullException(nameof(progress)), cancellationToken);
}

public sealed class AgentTool
{
    private readonly Func<JsonElement, ToolExecutionContext, CancellationToken, ValueTask<ToolResult>> _execute;
    private readonly Func<JsonElement, string?>? _validate;
    private readonly Func<JsonElement, string?>? _prepareArguments;

    public AgentTool(
        ToolDefinition definition,
        Func<JsonElement, ToolExecutionContext, CancellationToken, ValueTask<ToolResult>> execute,
        ToolRisk risk = ToolRisk.ReadOnly,
        ToolExecutionMode? executionMode = null,
        Func<JsonElement, string?>? validate = null,
        Func<JsonElement, string?>? conflictKey = null,
        Func<JsonElement, string?>? prepareArguments = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        if (!Enum.IsDefined(typeof(ToolRisk), risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (executionMode is { } configuredMode
            && !Enum.IsDefined(typeof(ToolExecutionMode), configuredMode))
        {
            throw new ArgumentOutOfRangeException(nameof(executionMode));
        }

        Risk = risk;
        ExecutionMode = executionMode;
        _validate = validate;
        _prepareArguments = prepareArguments;
        ConflictKey = conflictKey;
    }

    public ToolDefinition Definition { get; }

    public ToolRisk Risk { get; }

    public ToolExecutionMode? ExecutionMode { get; }

    public Func<JsonElement, string?>? ConflictKey { get; }

    public string? ValidateArguments(string argumentsJson)
    {
        var valid = JsonValue.RequireObject(argumentsJson, nameof(argumentsJson));
        using var document = JsonDocument.Parse(valid);
        return Validate(document.RootElement);
    }

    internal string? Validate(JsonElement arguments)
    {
        var schemaError = JsonSchemaValidator.Validate(Definition.InputSchemaJson, arguments);
        return schemaError ?? _validate?.Invoke(arguments);
    }

    internal string? PrepareArguments(JsonElement arguments) => _prepareArguments?.Invoke(arguments);

    internal ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken) =>
        _execute(arguments, context, cancellationToken);
}

public sealed class ToolCallDecision
{
    private ToolCallDecision(bool blocked, string? reason, string? replacementArgumentsJson, bool terminate)
    {
        Blocked = blocked;
        Reason = reason;
        Terminate = terminate;
        ReplacementArgumentsJson = replacementArgumentsJson is null
            ? null
            : JsonValue.RequireObject(replacementArgumentsJson, nameof(replacementArgumentsJson));
    }

    public bool Blocked { get; }

    public string? Reason { get; }

    public string? ReplacementArgumentsJson { get; }

    /// <summary>
    /// Requests that a blocked result participate in the normal all-results-terminate rule.
    /// </summary>
    public bool Terminate { get; }

    public static ToolCallDecision Allow(string? replacementArgumentsJson = null) =>
        new(false, null, replacementArgumentsJson, false);

    public static ToolCallDecision Block(string reason, bool terminate = false) =>
        new(true, string.IsNullOrWhiteSpace(reason) ? "Tool execution was blocked." : reason, null, terminate);
}
