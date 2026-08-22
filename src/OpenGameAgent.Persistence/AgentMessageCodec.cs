using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenGameAgent.Attachments;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Persistence;

internal static class AgentMessageCodec
{
    public static MessageDocument Encode(AgentMessage message) => new()
    {
        Role = message.Role.ToString(),
        CustomRole = message.CustomRole,
        Content = message.Content.Select(EncodeContent).ToList(),
        Timestamp = message.Timestamp,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        IsError = message.IsError,
        DetailsJson = message.DetailsJson,
        Metadata = new Dictionary<string, string>(message.Metadata, StringComparer.Ordinal),
        Model = message.Model,
        Provider = message.Provider,
        Api = message.Api,
        ResponseModel = message.ResponseModel,
        ResponseId = message.ResponseId,
        RawStopReason = message.RawStopReason,
        EndTurn = message.EndTurn,
        StopReason = message.StopReason?.ToString(),
        Usage = message.Usage is null ? null : new UsageDocument
        {
            InputTokens = message.Usage.InputTokens,
            OutputTokens = message.Usage.OutputTokens,
            CacheReadTokens = message.Usage.CacheReadTokens,
            CacheWriteTokens = message.Usage.CacheWriteTokens,
            ReasoningTokens = message.Usage.ReasoningTokens,
            CacheWriteOneHourTokens = message.Usage.CacheWriteOneHourTokens,
            InputCost = message.Usage.Cost.Input,
            OutputCost = message.Usage.Cost.Output,
            CacheReadCost = message.Usage.Cost.CacheRead,
            CacheWriteCost = message.Usage.Cost.CacheWrite,
            CostKnown = message.Usage.Cost.IsKnown,
        },
        ErrorMessage = message.ErrorMessage,
        Diagnostics = message.Diagnostics.Select(diagnostic => new DiagnosticDocument
        {
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            Severity = diagnostic.Severity.ToString(),
            DataJson = diagnostic.DataJson,
        }).ToList(),
        Deferred = message.Deferred is null ? null : new DeferredDocument
        {
            Provider = message.Deferred.Provider,
            Model = message.Deferred.Model,
            Api = message.Deferred.Api,
            Id = message.Deferred.Id,
            ExpiresAt = message.Deferred.ExpiresAt,
            PollAfterMilliseconds = message.Deferred.PollAfterMilliseconds,
            DataJson = message.Deferred.DataJson,
        },
        AddedToolNames = message.AddedToolNames.ToList(),
    };

    public static AgentMessage Decode(MessageDocument document)
    {
        if (!Enum.TryParse<AgentRole>(document.Role, out var role)
            || !Enum.IsDefined(typeof(AgentRole), role))
        {
            throw new PersistenceException("The persisted message role is invalid.");
        }

        ModelStopReason? stopReason = null;
        if (document.StopReason is not null)
        {
            if (!Enum.TryParse<ModelStopReason>(document.StopReason, out var parsed)
                || !Enum.IsDefined(typeof(ModelStopReason), parsed))
            {
                throw new PersistenceException("The persisted model stop reason is invalid.");
            }

            stopReason = parsed;
        }

        var usage = document.Usage is null
            ? null
            : new ModelUsage(
                document.Usage.InputTokens,
                document.Usage.OutputTokens,
                document.Usage.CacheReadTokens,
                document.Usage.CacheWriteTokens,
                document.Usage.ReasoningTokens,
                document.Usage.CacheWriteOneHourTokens,
                new ModelCost(
                    document.Usage.InputCost,
                    document.Usage.OutputCost,
                    document.Usage.CacheReadCost,
                    document.Usage.CacheWriteCost,
                    document.Usage.CostKnown));
        var diagnostics = document.Diagnostics?.Select(DecodeDiagnostic).ToArray();
        var deferred = document.Deferred is null
            ? null
            : new DeferredModelHandle(
                document.Deferred.Provider ?? throw new PersistenceException("Persisted deferred provider is missing."),
                document.Deferred.Model ?? throw new PersistenceException("Persisted deferred model is missing."),
                document.Deferred.Api ?? throw new PersistenceException("Persisted deferred API is missing."),
                document.Deferred.Id ?? throw new PersistenceException("Persisted deferred ID is missing."),
                document.Deferred.ExpiresAt,
                document.Deferred.PollAfterMilliseconds,
                document.Deferred.DataJson);
        return new AgentMessage(
            role,
            (document.Content ?? throw new PersistenceException("Persisted message content is missing.")).Select(DecodeContent),
            document.Timestamp,
            document.CustomRole,
            document.ToolCallId,
            document.ToolName,
            document.IsError,
            document.DetailsJson,
            document.Metadata,
            document.Model,
            stopReason,
            usage,
            document.ErrorMessage,
            document.Provider,
            document.Api,
            document.ResponseModel,
            document.ResponseId,
            document.RawStopReason,
            document.EndTurn,
            role == AgentRole.Assistant ? diagnostics : null,
            deferred,
            role == AgentRole.Tool ? document.AddedToolNames : null);
    }

    private static ContentDocument EncodeContent(AgentContent content) => content switch
    {
        TextContent text => new ContentDocument
        {
            Kind = "text",
            Text = text.Text,
            Detail = text.Signature,
            TextPhase = text.Phase?.ToString(),
        },
        JsonContent json => new ContentDocument { Kind = "json", Json = json.Json },
        ReasoningContent reasoning => new ContentDocument
        {
            Kind = "reasoning",
            Text = reasoning.Text,
            Detail = reasoning.Signature,
            Redacted = reasoning.Redacted,
        },
        ResourceContent resource => new ContentDocument { Kind = "resource", Text = resource.Name, Reference = resource.Uri, Detail = resource.MediaType },
        ImageAttachmentContent image => new ContentDocument
        {
            Kind = "image",
            Text = image.Attachment.Name,
            Reference = image.Attachment.AttachmentId,
            Detail = image.Attachment.MediaType,
            Bytes = image.Attachment.Bytes,
            Width = image.Attachment.Width,
            Height = image.Attachment.Height,
        },
        BinaryContent binary => new ContentDocument
        {
            Kind = "binary",
            Text = binary.Name,
            Json = binary.Data,
            Detail = binary.MediaType,
            MediaKind = binary.MediaKind.ToString(),
        },
        ToolCallContent call => new ContentDocument
        {
            Kind = "tool_call",
            Text = call.Name,
            Reference = call.Id,
            Json = call.ArgumentsJson,
            Detail = call.ThoughtSignature,
            ToolNamespace = call.Namespace,
        },
        _ => throw new InvalidOperationException("Unsupported agent content type."),
    };

    private static AgentContent DecodeContent(ContentDocument document) => document.Kind switch
    {
        "text" => new TextContent(
            document.Text ?? string.Empty,
            document.Detail,
            ParseOptionalEnum<AgentTextPhase>(document.TextPhase, "text phase")),
        "json" => new JsonContent(document.Json ?? throw new PersistenceException("Persisted JSON content is missing.")),
        "reasoning" => new ReasoningContent(document.Text ?? string.Empty, document.Detail, document.Redacted),
        "resource" => new ResourceContent(
            document.Reference ?? throw new PersistenceException("Persisted resource URI is missing."),
            document.Detail ?? throw new PersistenceException("Persisted resource media type is missing."),
            document.Text),
        "image" => new ImageAttachmentContent(new GameImageAttachment(
            document.Reference ?? throw new PersistenceException("Persisted image attachment ID is missing."),
            document.Detail ?? throw new PersistenceException("Persisted image media type is missing."),
            document.Bytes,
            document.Width,
            document.Height,
            document.Text)),
        "binary" => new BinaryContent(
            ParseRequiredEnum<AgentMediaKind>(document.MediaKind, "media kind"),
            document.Json ?? throw new PersistenceException("Persisted binary data is missing."),
            document.Detail ?? throw new PersistenceException("Persisted binary media type is missing."),
            document.Text),
        "tool_call" => new ToolCallContent(
            document.Reference ?? throw new PersistenceException("Persisted tool call ID is missing."),
            document.Text ?? throw new PersistenceException("Persisted tool call name is missing."),
            document.Json ?? throw new PersistenceException("Persisted tool arguments are missing."),
            document.Detail,
            document.ToolNamespace),
        _ => throw new PersistenceException($"Unsupported persisted content kind '{document.Kind}'."),
    };

    private static ModelDiagnostic DecodeDiagnostic(DiagnosticDocument document) => new(
        document.Code ?? throw new PersistenceException("Persisted diagnostic code is missing."),
        document.Message ?? throw new PersistenceException("Persisted diagnostic message is missing."),
        ParseRequiredEnum<ModelDiagnosticSeverity>(document.Severity, "diagnostic severity"),
        document.DataJson);

    private static TEnum ParseRequiredEnum<TEnum>(string? value, string description)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, out var parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
        {
            throw new PersistenceException($"The persisted {description} is invalid.");
        }

        return parsed;
    }

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string description)
        where TEnum : struct, Enum => value is null ? null : ParseRequiredEnum<TEnum>(value, description);
}

internal sealed class MessageDocument
{
    public string Role { get; set; } = string.Empty;

    public string? CustomRole { get; set; }

    public List<ContentDocument>? Content { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public bool IsError { get; set; }

    public string? DetailsJson { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public string? Model { get; set; }

    public string? Provider { get; set; }

    public string? Api { get; set; }

    public string? ResponseModel { get; set; }

    public string? ResponseId { get; set; }

    public string? RawStopReason { get; set; }

    public bool? EndTurn { get; set; }

    public string? StopReason { get; set; }

    public UsageDocument? Usage { get; set; }

    public string? ErrorMessage { get; set; }

    public List<DiagnosticDocument>? Diagnostics { get; set; }

    public DeferredDocument? Deferred { get; set; }

    public List<string>? AddedToolNames { get; set; }
}

internal sealed class ContentDocument
{
    public string Kind { get; set; } = string.Empty;

    public string? Text { get; set; }

    public string? Json { get; set; }

    public string? Reference { get; set; }

    public string? Detail { get; set; }

    public string? TextPhase { get; set; }

    public string? MediaKind { get; set; }

    public string? ToolNamespace { get; set; }

    public bool Redacted { get; set; }

    public int Bytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

internal sealed class UsageDocument
{
    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWriteTokens { get; set; }

    public long? ReasoningTokens { get; set; }

    public long? CacheWriteOneHourTokens { get; set; }

    public double InputCost { get; set; }

    public double OutputCost { get; set; }

    public double CacheReadCost { get; set; }

    public double CacheWriteCost { get; set; }

    public bool? CostKnown { get; set; }
}

internal sealed class DiagnosticDocument
{
    public string? Code { get; set; }

    public string? Message { get; set; }

    public string? Severity { get; set; }

    public string? DataJson { get; set; }
}

internal sealed class DeferredDocument
{
    public string? Provider { get; set; }

    public string? Model { get; set; }

    public string? Api { get; set; }

    public string? Id { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public int? PollAfterMilliseconds { get; set; }

    public string? DataJson { get; set; }
}
