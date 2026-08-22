using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public sealed class GameAgentTraceEntry
{
    public GameAgentTraceEntry(
        long sequence,
        string kind,
        string sessionId,
        string actorId,
        string inputId,
        GameMoment moment,
        DateTimeOffset operationalTimestamp,
        string detailsJson)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Sequence = sequence;
        Kind = Require(kind, nameof(kind));
        SessionId = Require(sessionId, nameof(sessionId));
        ActorId = Require(actorId, nameof(actorId));
        InputId = Require(inputId, nameof(inputId));
        if (string.IsNullOrWhiteSpace(moment.TimelineId) || moment.TimelineId.Length > 1_024)
        {
            throw new ArgumentException("A valid game moment is required.", nameof(moment));
        }

        Moment = moment;
        if (operationalTimestamp == default)
        {
            throw new ArgumentException("An operational timestamp is required.", nameof(operationalTimestamp));
        }

        OperationalTimestamp = operationalTimestamp;
        DetailsJson = RequireJson(detailsJson);
    }

    public long Sequence { get; }

    public string Kind { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string InputId { get; }

    public GameMoment Moment { get; }

    public DateTimeOffset OperationalTimestamp { get; }

    public string DetailsJson { get; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 1_024
            ? throw new ArgumentException("A value of at most 1,024 characters is required.", name)
            : value;

    private static string RequireJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 10_000_000)
        {
            throw new ArgumentException("Trace details must contain at most 10,000,000 characters.", nameof(value));
        }

        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return value;
    }
}

public interface IGameAgentTraceSink
{
    ValueTask WriteAsync(GameAgentTraceEntry entry, CancellationToken cancellationToken);
}

public sealed class InMemoryGameAgentTraceSink : IGameAgentTraceSink
{
    private readonly object _gate = new();
    private readonly Queue<GameAgentTraceEntry> _entries;
    private readonly int _capacity;

    public InMemoryGameAgentTraceSink(int capacity = 10_000)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Queue<GameAgentTraceEntry>(Math.Min(capacity, 1024));
    }

    public ValueTask WriteAsync(GameAgentTraceEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        lock (_gate)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        return default;
    }

    public IReadOnlyList<GameAgentTraceEntry> Snapshot()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<GameAgentTraceEntry>(_entries.ToArray());
        }
    }
}

public sealed class GameAgentTracingOptions
{
    public bool IncludeInputPayload { get; set; }

    public bool IncludeToolArguments { get; set; }

    public int MaximumDetailsCharacters { get; set; } = 65_536;

    public Func<DateTimeOffset> OperationalClock { get; set; } = () => DateTimeOffset.UtcNow;

    internal GameAgentTracingOptions CopyAndValidate()
    {
        var copy = (GameAgentTracingOptions)MemberwiseClone();
        if (copy.MaximumDetailsCharacters < 256 || copy.MaximumDetailsCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDetailsCharacters));
        }

        if (copy.OperationalClock is null)
        {
            throw new ArgumentNullException(nameof(OperationalClock));
        }

        return copy;
    }
}

public sealed class GameAgentTracingExtension : IGameAgentExtension
{
    private static readonly JsonSerializerOptions TraceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly IGameAgentTraceSink _sink;
    private readonly GameAgentTracingOptions _options;
    private long _sequence;

    public GameAgentTracingExtension(IGameAgentTraceSink sink, GameAgentTracingOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = (options ?? new GameAgentTracingOptions()).CopyAndValidate();
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.tracing",
        "1.0.0",
        "Bounded structured traces that keep game time separate from operational time.",
        new[] { "tracing", "observability", "diagnostics" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.Subscribe(ToolApprovalExtension.ApprovalChanged, (value, token) =>
            WriteApprovalAsync(value, token));
        api.On(GameAgentExtensionEvents.InputReceived, (value, context, token) =>
            WriteAsync(
                "input.received",
                context,
                _options.IncludeInputPayload
                    ? (object)new
                    {
                        type = value.Input.Type,
                        payload = Parse(value.Input.PayloadJson),
                        metadataCount = value.Input.Metadata.Count,
                        queueMilliseconds = value.QueueDuration?.TotalMilliseconds,
                        inputPreparationMilliseconds = value.InputPreparationDuration?.TotalMilliseconds,
                        sessionLoadMilliseconds = value.SessionLoadDuration?.TotalMilliseconds,
                    }
                    : new
                    {
                        type = value.Input.Type,
                        payloadOmitted = true,
                        metadataCount = value.Input.Metadata.Count,
                        queueMilliseconds = value.QueueDuration?.TotalMilliseconds,
                        inputPreparationMilliseconds = value.InputPreparationDuration?.TotalMilliseconds,
                        sessionLoadMilliseconds = value.SessionLoadDuration?.TotalMilliseconds,
                    },
                token));
        api.On(GameAgentExtensionEvents.SessionLoaded, (value, context, token) =>
            WriteAsync(
                "session.loaded",
                context,
                new { revision = value.Session.Revision, messages = value.Session.Messages.Count },
                token));
        api.On(GameAgentExtensionEvents.ContextCollected, (value, context, token) =>
            WriteAsync(
                "context.collected",
                context,
                new
                {
                    count = value.Context.Count,
                    sources = value.Context.Select(slice => slice.Source).ToArray(),
                    durationMilliseconds = value.Duration?.TotalMilliseconds,
                },
                token));
        api.On(GameAgentExtensionEvents.ToolsCollected, (value, context, token) =>
            WriteAsync(
                "tools.collected",
                context,
                new
                {
                    count = value.Tools.Count,
                    names = value.Tools.Select(tool => tool.Definition.Name).ToArray(),
                    durationMilliseconds = value.Duration?.TotalMilliseconds,
                },
                token));
        api.On(GameAgentExtensionEvents.RouteSelected, (value, context, token) =>
            WriteAsync(
                "route.selected",
                context,
                new
                {
                    route = value.Decision.Route.ToString(),
                    value.Decision.Reason,
                    value.Decision.Workflow,
                    classificationStatus = value.Decision.Classification is null
                        ? null
                        : value.Decision.Classification.UsedFallback ? "fallback" : "selected",
                    classificationFailure = value.Decision.Classification?.FailureCode,
                    classificationFallbackReason = value.Decision.Classification?.FallbackReason,
                    classificationContentKinds = value.Decision.Classification?.ResponseContentKinds
                        .Select(RouteContentKindName)
                        .ToArray(),
                    classificationVisibleContentCharacters = value.Decision.Classification?.VisibleContentCharacters,
                    classificationReasoningCharacters = value.Decision.Classification?.ReasoningCharacters,
                    classificationProviderStatusCode = value.Decision.Classification?.ProviderStatusCode,
                    classificationProviderFailureCategory = value.Decision.Classification?.ProviderFailureCategory,
                    classificationProviderRequestFields = value.Decision.Classification?.ProviderRequestFields,
                    classificationProviderRequestId = value.Decision.Classification?.ProviderRequestId,
                    durationMilliseconds = value.Duration?.TotalMilliseconds,
                    modelDurationMilliseconds = value.ModelDuration?.TotalMilliseconds,
                },
                token));
        api.On(GameAgentExtensionEvents.SkillsSelected, (value, context, token) =>
            WriteAsync(
                "skills.selected",
                context,
                new
                {
                    count = value.Skills.Count,
                    ids = value.Skills.Select(skill => skill.SkillId).ToArray(),
                    durationMilliseconds = value.Duration?.TotalMilliseconds,
                },
                token));
        api.On(GameAgentExtensionEvents.ImagesProjected, (value, context, token) =>
            WriteAsync(
                "images.projected",
                context,
                new
                {
                    value.Model,
                    value.RunId,
                    value.Turn,
                    images = value.Images.Select(image => new
                    {
                        image.Ordinal,
                        image.SourceAttachmentId,
                        image.RequestAttachmentId,
                        disposition = image.Disposition.ToString(),
                        image.TransformId,
                        image.Width,
                        image.Height,
                        image.Bytes,
                    }).ToArray(),
                },
                token));
        api.On(GameAgentExtensionEvents.KernelEvent, (value, context, token) =>
            WriteAsync(
                value.Value.Kind == AgentEventKind.ModelRequestStarted
                    ? "model.request.started"
                    : "kernel." + value.Value.Kind.ToString().ToLowerInvariant(),
                context,
                KernelDetails(value.Value),
                token));
        api.On(GameAgentExtensionEvents.RunCompleted, (value, context, token) =>
            WriteAsync(
                "run.completed",
                context,
                new
                {
                    status = value.Result.Status.ToString(),
                    route = value.Result.Route.Route.ToString(),
                    revision = value.Result.SessionRevision,
                    succeeded = value.Result.Succeeded,
                    turns = value.Result.AgentResult?.Turns,
                    toolCalls = value.Result.AgentResult?.ToolCalls,
                    usage = ProjectUsageTotals(value.Result.RunUsage.Stats.Total),
                    usageByCause = value.Result.RunUsage.TotalsByCause
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new { cause = pair.Key.ToString(), usage = ProjectUsageTotals(pair.Value) })
                        .ToArray(),
                    responses = value.Result.AgentResult?.NewMessages
                        .Where(message => message.Role == AgentRole.Assistant)
                        .Select(message => new
                        {
                            message.Provider,
                            message.Api,
                            requestedModel = message.Model,
                            responseModel = message.ResponseModel,
                            message.ResponseId,
                            stopReason = message.StopReason?.ToString(),
                            message.RawStopReason,
                        })
                        .ToArray(),
                },
                token));
        api.On(GameAgentExtensionEvents.SessionSaved, (value, context, token) =>
            WriteAsync(
                "session.saved",
                context,
                new
                {
                    revision = value.Session.Revision,
                    messages = value.Session.Messages.Count,
                    usageRecords = value.Session.UsageLedger.TotalRecordCount,
                    usage = ProjectUsage(value.Session.UsageLedger.Stats.Total),
                    usageByCause = value.Session.UsageLedger.TotalsByCause
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new { cause = pair.Key.ToString(), usage = ProjectUsage(pair.Value) })
                        .ToArray(),
                },
                token));
        api.On(GameAgentExtensionEvents.RunFailed, (value, context, token) =>
            WriteAsync(
                "run.failed",
                context,
                new { exception = value.Exception.GetType().FullName, value.Exception.Message },
                token));
    }

    private ValueTask WriteAsync(
        string kind,
        GameAgentExtensionRunContext context,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, TraceJsonOptions);
        if (json.Length > _options.MaximumDetailsCharacters)
        {
            json = JsonSerializer.Serialize(new { truncated = true, originalCharacters = json.Length });
        }

        return _sink.WriteAsync(
            new GameAgentTraceEntry(
                Interlocked.Increment(ref _sequence),
                kind,
                context.Input.SessionId,
                context.Input.ActorId,
                context.Input.InputId,
                context.Input.Moment,
                _options.OperationalClock(),
                json),
            cancellationToken);
    }

    private ValueTask WriteApprovalAsync(
        GameToolApprovalEvent value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                value.ApprovalId,
                value.RunId,
                value.ToolCallId,
                value.ToolName,
                status = value.Status.ToString(),
                waitMilliseconds = value.WaitDuration.TotalMilliseconds,
            },
            TraceJsonOptions);
        return _sink.WriteAsync(
            new GameAgentTraceEntry(
                Interlocked.Increment(ref _sequence),
                value.Status == GameToolApprovalStatus.Pending ? "tool.approval.pending" : "tool.approval.completed",
                value.SessionId,
                value.ActorId,
                value.InputId,
                value.Moment,
                _options.OperationalClock(),
                json),
            cancellationToken);
    }

    private object KernelDetails(AgentEvent value) => new
    {
        value.RunId,
        value.Turn,
        tool = value.ToolCall?.Name,
        toolCallId = value.ToolCall?.Id,
        operation = ProjectFrameworkToolOperation(value.ToolCall),
        arguments = _options.IncludeToolArguments && value.ToolCall is not null
            ? Parse(value.ToolCall.ArgumentsJson)
            : (JsonElement?)null,
        status = value.Status?.ToString(),
        value.Error,
        contentParts = value.Message?.Content.Count,
        requestedModel = value.Message?.Model,
        provider = value.Message?.Provider,
        responseModel = value.Message?.ResponseModel,
        responseId = value.Message?.ResponseId,
        streamEvent = value.ModelEvent?.Kind.ToString(),
        modelRequest = value.ModelRequest is null
            ? null
            : new
            {
                value.ModelRequest.Model,
                messages = value.ModelRequest.Messages.Count,
                tools = value.ModelRequest.Tools.Count,
            },
        providerAttempts = ProjectProviderAttempts(value.Message?.Diagnostics),
        progressMessage = value.Progress?.Message,
        toolRepeatCount = value.ToolRepeat?.ConsecutiveCount,
        toolRepeatAction = value.ToolRepeat?.Action.ToString(),
        toolError = value.ToolResult?.IsError,
        failureCategory = value.ToolResult?.FailureCategory.ToString(),
        outcomeUncertain = value.ToolResult?.OutcomeUncertain,
        action = ProjectActionResult(value.ToolResult?.DetailsJson),
        usage = ProjectUsage(value.Message?.Usage ?? value.ToolResult?.Usage),
    };

    private static string? ProjectFrameworkToolOperation(ToolCallContent? call)
    {
        if (call is null
            || call.Name is not "manage_task_plan" and not "manage_goal")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.TryGetProperty("action", out var action)
                   && action.ValueKind == JsonValueKind.String
                ? action.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ProjectProviderAttempts(IReadOnlyList<ModelDiagnostic>? diagnostics)
    {
        if (diagnostics is null)
        {
            return null;
        }

        var retry = diagnostics.LastOrDefault(value => string.Equals(value.Code, "oga.provider.retry", StringComparison.Ordinal));
        var fallback = diagnostics.LastOrDefault(value => string.Equals(value.Code, "oga.provider.fallback", StringComparison.Ordinal));
        if (retry is null && fallback is null)
        {
            return null;
        }

        return new
        {
            retry = ParseBoundedDiagnostic(retry?.DataJson),
            fallback = ParseBoundedDiagnostic(fallback?.DataJson),
        };
    }

    private static JsonElement? ParseBoundedDiagnostic(string? json)
    {
        if (json is null || json.Length > 4_096)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ProjectActionResult(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("operationId", out var operationId)
                || operationId.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new
            {
                operationId = operationId.GetString(),
                status = status.GetString(),
                dispatch = root.TryGetProperty("dispatch", out var dispatch) && dispatch.ValueKind == JsonValueKind.String
                    ? dispatch.GetString()
                    : null,
                duplicateExecutionPrevented = root.TryGetProperty("duplicateExecutionPrevented", out var duplicate)
                    && duplicate.ValueKind == JsonValueKind.True,
                recovered = root.TryGetProperty("recovered", out var recovered)
                    && recovered.ValueKind == JsonValueKind.True,
                totalMilliseconds = ReadOptionalDouble(root, "totalMilliseconds"),
                hostMilliseconds = ReadOptionalDouble(root, "hostMilliseconds"),
                frameworkMilliseconds = ReadOptionalDouble(root, "frameworkMilliseconds"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadOptionalDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
            ? value
            : null;

    private static object? ProjectUsage(ModelUsage? usage) => usage is null
        ? null
        : new
        {
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens,
            usage.CacheWriteOneHourTokens,
            usage.TotalTokens,
            cost = new
            {
                known = usage.Cost.IsKnown,
                input = usage.Cost.IsKnown ? usage.Cost.Input : (double?)null,
                output = usage.Cost.IsKnown ? usage.Cost.Output : (double?)null,
                cacheRead = usage.Cost.IsKnown ? usage.Cost.CacheRead : (double?)null,
                cacheWrite = usage.Cost.IsKnown ? usage.Cost.CacheWrite : (double?)null,
                total = usage.Cost.TotalIfKnown,
            },
        };

    private static object ProjectUsageTotals(GameSessionUsageTotals usage) => new
    {
        usage.InputTokens,
        usage.OutputTokens,
        usage.CacheReadTokens,
        usage.CacheWriteTokens,
        usage.ReasoningTokens,
        usage.CacheWriteOneHourTokens,
        usage.TotalTokens,
        cost = new
        {
            known = usage.CostKnown,
            input = usage.CostKnown ? usage.InputCost : (double?)null,
            output = usage.CostKnown ? usage.OutputCost : (double?)null,
            cacheRead = usage.CostKnown ? usage.CacheReadCost : (double?)null,
            cacheWrite = usage.CostKnown ? usage.CacheWriteCost : (double?)null,
            total = usage.CostTotalIfKnown,
        },
    };

    private static object ProjectUsage(GameSessionUsageTotals usage) => new
    {
        usage.InputTokens,
        usage.OutputTokens,
        usage.CacheReadTokens,
        usage.CacheWriteTokens,
        usage.ReasoningTokens,
        usage.CacheWriteOneHourTokens,
        usage.TotalTokens,
        cost = new
        {
            known = usage.CostKnown,
            input = usage.CostKnown ? usage.InputCost : (double?)null,
            output = usage.CostKnown ? usage.OutputCost : (double?)null,
            cacheRead = usage.CostKnown ? usage.CacheReadCost : (double?)null,
            cacheWrite = usage.CostKnown ? usage.CacheWriteCost : (double?)null,
            total = usage.CostTotalIfKnown,
        },
    };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static string RouteContentKindName(AgentContentKind kind) => kind switch
    {
        AgentContentKind.Text => "text",
        AgentContentKind.Json => "json",
        AgentContentKind.Resource => "resource",
        AgentContentKind.ImageAttachment => "image-attachment",
        AgentContentKind.Binary => "binary",
        AgentContentKind.Reasoning => "reasoning",
        AgentContentKind.ToolCall => "tool-call",
        _ => "unknown",
    };
}
