using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public static class GameAgentWire
{
    public const int MaximumTranscriptPageUtf8Bytes = 8_000_000;

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
            content = input.Content.Select(ProjectInputContent).ToArray(),
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
            toolRepeat = agentEvent.ToolRepeat is null ? null : new
            {
                consecutiveCount = agentEvent.ToolRepeat.ConsecutiveCount,
                action = agentEvent.ToolRepeat.Action.ToString(),
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

    public static string SerializeTranscriptPage(GameSessionTranscriptPage page)
    {
        if (page is null)
        {
            throw new ArgumentNullException(nameof(page));
        }

        using var stream = new BoundedMemoryStream(MaximumTranscriptPageUtf8Bytes);
        try
        {
            JsonSerializer.Serialize(stream, CreateTranscriptProjection(page), JsonOptions);
        }
        catch (TranscriptPageLimitExceededException)
        {
            throw new GameSessionTranscriptPageTooLargeException();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    internal static bool FitsTranscriptPage(GameSessionTranscriptPage page)
    {
        if (page is null)
        {
            throw new ArgumentNullException(nameof(page));
        }

        using var stream = new BoundedCountingStream(MaximumTranscriptPageUtf8Bytes);
        try
        {
            JsonSerializer.Serialize(stream, CreateTranscriptProjection(page), JsonOptions);
            return true;
        }
        catch (TranscriptPageLimitExceededException)
        {
            return false;
        }
    }

    private static object CreateTranscriptProjection(GameSessionTranscriptPage page) => new
    {
        sessionId = page.Key.SessionId,
        actorId = page.Key.ActorId,
        sessionRevision = page.SessionRevision,
        startIndex = page.StartIndex,
        totalMessages = page.TotalMessages,
        nextCursor = page.NextCursor,
        messages = page.Messages.Select(ProjectMessage),
    };

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

    private static object ProjectInputContent(AgentContent content) => content switch
    {
        TextContent text => new { kind = "text", text = text.Text },
        JsonContent json => new { kind = "json", data = (object)ParseElement(json.Json) },
        ResourceContent resource => new
        {
            kind = "resource",
            uri = resource.Uri,
            mediaType = resource.MediaType,
            name = resource.Name,
        },
        BinaryContent { MediaKind: AgentMediaKind.Image } image => new
        {
            kind = "image",
            data = image.Data,
            mediaType = image.MediaType,
            name = image.Name,
        },
        ImageAttachmentContent => throw new ArgumentException(
            "Durable image references cannot be submitted on the input wire; send the image bytes instead.",
            nameof(content)),
        _ => throw new ArgumentException($"Unsupported game input content type '{content.GetType().FullName}'.", nameof(content)),
    };

    private static object ProjectContent(AgentContent content) => content switch
    {
        TextContent text => new
        {
            kind = "text",
            text = text.Text,
            signature = text.Signature,
            phase = text.Phase?.ToString(),
        },
        ReasoningContent reasoning => new
        {
            kind = "reasoning",
            text = reasoning.Text,
            signature = reasoning.Signature,
            redacted = reasoning.Redacted,
        },
        JsonContent json => new { kind = "json", data = (object)ParseElement(json.Json) },
        ResourceContent resource => new
        {
            kind = "resource",
            uri = resource.Uri,
            mediaType = resource.MediaType,
            name = resource.Name,
        },
        ImageAttachmentContent image => new
        {
            kind = "image",
            attachment = new
            {
                attachmentId = image.Attachment.AttachmentId,
                mediaType = image.Attachment.MediaType,
                bytes = image.Attachment.Bytes,
                width = image.Attachment.Width,
                height = image.Attachment.Height,
                name = image.Attachment.Name,
            },
        },
        BinaryContent => throw new ArgumentException(
            "Inline binary content cannot be projected to a public event; persist it as an attachment first.",
            nameof(content)),
        ToolCallContent call => new
        {
            kind = "tool_call",
            id = call.Id,
            name = call.Name,
            arguments = (object)ParseElement(call.ArgumentsJson),
            thoughtSignature = call.ThoughtSignature,
            toolNamespace = call.Namespace,
        },
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

        public List<InputContentDocument>? Content { get; set; }

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
            (Content ?? new List<InputContentDocument>())
                .Select(part => part.ToContent())
                .ToArray());
    }

    private sealed class InputContentDocument
    {
        public string Kind { get; set; } = string.Empty;

        public string? Text { get; set; }

        public JsonElement Data { get; set; }

        public string Uri { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string? Name { get; set; }

        public AgentContent ToContent()
        {
            switch (Kind)
            {
                case "text":
                    return new TextContent(Text ?? throw new ArgumentException("Text input content requires text."));
                case "json":
                    if (Data.ValueKind == JsonValueKind.Undefined)
                    {
                        throw new ArgumentException("JSON input content requires data.");
                    }

                    return new JsonContent(Data.GetRawText());
                case "resource":
                    return new ResourceContent(Uri, MediaType, Name);
                case "image":
                    if (Data.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("Image input content requires base64 string data.");
                    }

                    return new BinaryContent(
                        AgentMediaKind.Image,
                        Data.GetString() ?? string.Empty,
                        MediaType,
                        Name);
                default:
                    throw new ArgumentException($"Unsupported game input content kind '{Kind}'.");
            }
        }
    }

    private sealed class TranscriptPageLimitExceededException : IOException
    {
    }

    private class BoundedCountingStream : Stream
    {
        private readonly long _maximumBytes;
        private long _length;

        public BoundedCountingStream(long maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count < 0 || _length > _maximumBytes - count)
            {
                throw new TranscriptPageLimitExceededException();
            }

            WriteCore(buffer, offset, count);
            _length += count;
        }

        protected virtual void WriteCore(byte[] buffer, int offset, int count)
        {
        }
    }

    private sealed class BoundedMemoryStream : BoundedCountingStream
    {
        private readonly MemoryStream _stream = new();

        public BoundedMemoryStream(long maximumBytes)
            : base(maximumBytes)
        {
        }

        public byte[] GetBuffer() => _stream.GetBuffer();

        protected override void WriteCore(byte[] buffer, int offset, int count) =>
            _stream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
