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
        StopReason = message.StopReason?.ToString(),
        Usage = message.Usage is null ? null : new UsageDocument
        {
            InputTokens = message.Usage.InputTokens,
            OutputTokens = message.Usage.OutputTokens,
            CacheReadTokens = message.Usage.CacheReadTokens,
            CacheWriteTokens = message.Usage.CacheWriteTokens,
        },
        ErrorMessage = message.ErrorMessage,
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
                document.Usage.CacheWriteTokens);
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
            document.ErrorMessage);
    }

    private static ContentDocument EncodeContent(AgentContent content) => content switch
    {
        TextContent text => new ContentDocument { Kind = "text", Text = text.Text },
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
        ToolCallContent call => new ContentDocument { Kind = "tool_call", Text = call.Name, Reference = call.Id, Json = call.ArgumentsJson },
        _ => throw new InvalidOperationException("Unsupported agent content type."),
    };

    private static AgentContent DecodeContent(ContentDocument document) => document.Kind switch
    {
        "text" => new TextContent(document.Text ?? string.Empty),
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
        "tool_call" => new ToolCallContent(
            document.Reference ?? throw new PersistenceException("Persisted tool call ID is missing."),
            document.Text ?? throw new PersistenceException("Persisted tool call name is missing."),
            document.Json ?? throw new PersistenceException("Persisted tool arguments are missing.")),
        _ => throw new PersistenceException($"Unsupported persisted content kind '{document.Kind}'."),
    };
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

    public string? StopReason { get; set; }

    public UsageDocument? Usage { get; set; }

    public string? ErrorMessage { get; set; }
}

internal sealed class ContentDocument
{
    public string Kind { get; set; } = string.Empty;

    public string? Text { get; set; }

    public string? Json { get; set; }

    public string? Reference { get; set; }

    public string? Detail { get; set; }

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
}
