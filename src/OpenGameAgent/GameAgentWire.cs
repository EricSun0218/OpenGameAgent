using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public static class GameAgentWire
{
    public static string SerializeInput(GameInput input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        return JsonSerializer.Serialize(new
        {
            inputId = input.InputId,
            sessionId = input.SessionId,
            actorId = input.ActorId,
            type = input.Type,
            payload = ParseElement(input.PayloadJson),
            timelineId = input.Moment.TimelineId,
            tick = input.Moment.Tick,
            calendar = input.Moment.CalendarJson is null
                ? (JsonElement?)null
                : ParseElement(input.Moment.CalendarJson),
            metadata = input.Metadata,
            resources = input.Resources.Select(resource => new
            {
                uri = resource.Uri,
                mediaType = resource.MediaType,
                name = resource.Name,
            }).ToArray(),
        }, JsonOptions);
    }

    public static GameInput ParseInput(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        EnsureRequestIsUnambiguous(document.RootElement, nameof(json));
        var request = JsonSerializer.Deserialize<InputDocument>(document.RootElement.GetRawText(), JsonOptions)
            ?? throw new ArgumentException("The input JSON is empty.", nameof(json));
        return request.ToInput();
    }

    internal static void EnsureRequestIsUnambiguous(JsonElement root, string parameterName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The request must contain a JSON object.", parameterName);
        }

        EnsureObject(root, StringComparer.OrdinalIgnoreCase, parameterName);
    }

    private static void EnsureNestedIsUnambiguous(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            EnsureObject(value, StringComparer.Ordinal, parameterName);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureNestedIsUnambiguous(item, parameterName);
            }
        }
    }

    private static void EnsureObject(JsonElement value, StringComparer comparer, string parameterName)
    {
        var names = new HashSet<string>(comparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ArgumentException("The request cannot contain duplicate JSON properties.", parameterName);
            }

            EnsureNestedIsUnambiguous(property.Value, parameterName);
        }
    }

    public static string SerializeEvent(AgentEvent agentEvent)
    {
        if (agentEvent is null)
        {
            throw new ArgumentNullException(nameof(agentEvent));
        }

        return JsonSerializer.Serialize(new
        {
            kind = agentEvent.Kind.ToString(),
            runId = agentEvent.RunId,
            turn = agentEvent.Turn,
            modelEvent = agentEvent.ModelEvent is null ? null : new
            {
                kind = agentEvent.ModelEvent.Kind.ToString(),
                delta = agentEvent.ModelEvent.Delta,
                contentIndex = agentEvent.ModelEvent.ContentIndex,
                toolCallId = agentEvent.ModelEvent.ToolCallId,
                toolName = agentEvent.ModelEvent.ToolName,
            },
            message = agentEvent.Message is null || IsDeltaOnlyUpdate(agentEvent)
                ? null
                : ProjectMessage(agentEvent.Message),
            toolCall = agentEvent.ToolCall is null ? null : new
            {
                id = agentEvent.ToolCall.Id,
                name = agentEvent.ToolCall.Name,
                arguments = ParseElement(agentEvent.ToolCall.ArgumentsJson),
            },
            toolResult = agentEvent.ToolResult is null ? null : new
            {
                isError = agentEvent.ToolResult.IsError,
                terminate = agentEvent.ToolResult.Terminate,
                outcomeUncertain = agentEvent.ToolResult.OutcomeUncertain,
                details = agentEvent.ToolResult.DetailsJson is null
                    ? (JsonElement?)null
                    : ParseElement(agentEvent.ToolResult.DetailsJson),
                usage = ProjectUsage(agentEvent.ToolResult.Usage),
                content = agentEvent.ToolResult.Content.Select(ProjectContent).ToArray(),
            },
            progress = agentEvent.Progress is null ? null : new
            {
                message = agentEvent.Progress.Message,
                fraction = agentEvent.Progress.Fraction,
                details = agentEvent.Progress.DetailsJson is null
                    ? (JsonElement?)null
                    : ParseElement(agentEvent.Progress.DetailsJson),
            },
            error = agentEvent.Error,
            status = agentEvent.Status?.ToString(),
        }, JsonOptions);
    }

    public static string SerializeResult(GameAgentRunResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return JsonSerializer.Serialize(new
        {
            status = result.Status.ToString(),
            route = result.Route.Route.ToString(),
            routeReason = result.Route.Reason,
            workflow = result.Route.Workflow,
            sessionRevision = result.SessionRevision,
            agent = result.AgentResult is null ? null : new
            {
                runId = result.AgentResult.RunId,
                status = result.AgentResult.Status.ToString(),
                turns = result.AgentResult.Turns,
                toolCalls = result.AgentResult.ToolCalls,
                newMessages = result.AgentResult.NewMessages.Select(ProjectMessage).ToArray(),
                subscriberErrors = result.AgentResult.SubscriberErrors,
                error = result.AgentResult.Error,
                usage = ProjectUsage(result.AgentResult.Usage),
            },
            error = result.Error,
        }, JsonOptions);
    }

    public static string SerializeUsage(GameSessionUsageSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return JsonSerializer.Serialize(new
        {
            sessionId = snapshot.Key.SessionId,
            actorId = snapshot.Key.ActorId,
            sessionRevision = snapshot.SessionRevision,
            totalRecordCount = snapshot.Ledger.TotalRecordCount,
            recentRecordCapacity = snapshot.Ledger.RecentRecordCapacity,
            total = ProjectTotals(snapshot.Ledger.Stats.Total),
            byCause = snapshot.Ledger.TotalsByCause
                .OrderBy(pair => pair.Key)
                .Select(pair => new
                {
                    cause = pair.Key.ToString(),
                    usage = ProjectTotals(pair.Value),
                })
                .ToArray(),
            recentRecords = snapshot.Ledger.Records.Select(record => new
            {
                recordId = record.RecordId,
                cause = record.Cause.ToString(),
                runId = record.RunId,
                inputId = record.InputId,
                usage = ProjectUsage(record.Usage),
            }).ToArray(),
        }, JsonOptions);
    }

    private static object ProjectMessage(AgentMessage message) => new
    {
        role = message.Role.ToString(),
        customRole = message.CustomRole,
        content = message.Content.Select(ProjectContent).ToArray(),
        timestamp = message.Timestamp,
        toolCallId = message.ToolCallId,
        toolName = message.ToolName,
        isError = message.IsError,
        details = message.DetailsJson is null ? (JsonElement?)null : ParseElement(message.DetailsJson),
        metadata = message.Metadata,
        model = message.Model,
        provider = message.Provider,
        api = message.Api,
        responseModel = message.ResponseModel,
        responseId = message.ResponseId,
        rawStopReason = message.RawStopReason,
        stopReason = message.StopReason?.ToString(),
        usage = ProjectUsage(message.Usage),
        error = message.ErrorMessage,
    };

    private static object? ProjectUsage(ModelUsage? usage) => usage is null ? null : new
    {
        inputTokens = usage.InputTokens,
        outputTokens = usage.OutputTokens,
        cacheReadTokens = usage.CacheReadTokens,
        cacheWriteTokens = usage.CacheWriteTokens,
        reasoningTokens = usage.ReasoningTokens,
        cacheWriteOneHourTokens = usage.CacheWriteOneHourTokens,
        totalTokens = usage.TotalTokens,
        cost = ProjectCost(usage.Cost),
    };

    private static object ProjectTotals(GameSessionUsageTotals totals) => new
    {
        inputTokens = totals.InputTokens,
        outputTokens = totals.OutputTokens,
        cacheReadTokens = totals.CacheReadTokens,
        cacheWriteTokens = totals.CacheWriteTokens,
        reasoningTokens = totals.ReasoningTokens,
        cacheWriteOneHourTokens = totals.CacheWriteOneHourTokens,
        totalTokens = totals.TotalTokens,
        cost = new
        {
            known = totals.CostKnown,
            input = totals.CostKnown ? totals.InputCost : (double?)null,
            output = totals.CostKnown ? totals.OutputCost : (double?)null,
            cacheRead = totals.CostKnown ? totals.CacheReadCost : (double?)null,
            cacheWrite = totals.CostKnown ? totals.CacheWriteCost : (double?)null,
            total = totals.CostTotalIfKnown,
        },
    };

    private static object ProjectCost(ModelCost cost) => new
    {
        known = cost.IsKnown,
        input = cost.IsKnown ? cost.Input : (double?)null,
        output = cost.IsKnown ? cost.Output : (double?)null,
        cacheRead = cost.IsKnown ? cost.CacheRead : (double?)null,
        cacheWrite = cost.IsKnown ? cost.CacheWrite : (double?)null,
        total = cost.TotalIfKnown,
    };

    private static bool IsDeltaOnlyUpdate(AgentEvent agentEvent) =>
        agentEvent.Kind == AgentEventKind.MessageUpdated
        && agentEvent.ModelEvent?.Kind is ModelStreamEventKind.TextDelta
            or ModelStreamEventKind.ReasoningDelta
            or ModelStreamEventKind.ToolCallDelta;

    private static object ProjectContent(AgentContent content) => content switch
    {
        TextContent text => new ContentDocument("text", text.Text, null, null, null),
        ReasoningContent reasoning => new ContentDocument("reasoning", reasoning.Text, null, null, reasoning.Signature),
        JsonContent json => new ContentDocument("json", null, ParseElement(json.Json), null, null),
        ResourceContent resource => new ContentDocument("resource", resource.Name, null, resource.Uri, resource.MediaType),
        ToolCallContent call => new ContentDocument("tool_call", call.Name, ParseElement(call.ArgumentsJson), call.Id, null),
        _ => throw new ArgumentException($"Unsupported agent content type '{content.GetType().FullName}'.", nameof(content)),
    };

    private static JsonElement ParseElement(string json) => GameJson.ParseElement(json);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class InputDocument
    {
        public string? InputId { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public JsonElement Payload { get; set; }

        public string TimelineId { get; set; } = "default";

        public long Tick { get; set; }

        public JsonElement? Calendar { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }

        public List<InputResourceDocument>? Resources { get; set; }

        public GameInput ToInput() => new(
            SessionId,
            ActorId,
            Type,
            Payload.ValueKind == JsonValueKind.Undefined ? "{}" : Payload.GetRawText(),
            new GameMoment(
                TimelineId,
                Tick,
                Calendar is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } calendar
                    ? calendar.GetRawText()
                    : null),
            InputId,
            Metadata,
            (Resources ?? new List<InputResourceDocument>())
                .Select(resource => resource.ToResource())
                .ToArray());
    }

    private sealed class InputResourceDocument
    {
        public string Uri { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string? Name { get; set; }

        public ResourceContent ToResource() => new(Uri, MediaType, Name);
    }

    private sealed class ContentDocument
    {
        public ContentDocument(string kind, string? text, JsonElement? data, string? reference, string? detail)
        {
            Kind = kind;
            Text = text;
            Data = data;
            Reference = reference;
            Detail = detail;
        }

        public string Kind { get; }

        public string? Text { get; }

        public JsonElement? Data { get; }

        public string? Reference { get; }

        public string? Detail { get; }
    }
}
