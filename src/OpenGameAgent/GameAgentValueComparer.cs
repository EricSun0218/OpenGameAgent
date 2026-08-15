using System;
using System.Collections.Generic;
using System.Linq;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

/// <summary>
/// Compares durable agent values by contract rather than object identity. Custom persistence
/// implementations can use these helpers when they rehydrate immutable messages and checkpoints.
/// </summary>
public static class GameAgentValueComparer
{
    public static bool ContentEquals(AgentContent? left, AgentContent? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return (left, right) switch
        {
            (TextContent first, TextContent second) =>
                string.Equals(first.Text, second.Text, StringComparison.Ordinal),
            (JsonContent first, JsonContent second) =>
                string.Equals(first.Json, second.Json, StringComparison.Ordinal),
            (ReasoningContent first, ReasoningContent second) =>
                string.Equals(first.Text, second.Text, StringComparison.Ordinal)
                && string.Equals(first.Signature, second.Signature, StringComparison.Ordinal),
            (ResourceContent first, ResourceContent second) =>
                string.Equals(first.Uri, second.Uri, StringComparison.Ordinal)
                && string.Equals(first.MediaType, second.MediaType, StringComparison.Ordinal)
                && string.Equals(first.Name, second.Name, StringComparison.Ordinal),
            (ImageAttachmentContent first, ImageAttachmentContent second) =>
                string.Equals(first.Attachment.AttachmentId, second.Attachment.AttachmentId, StringComparison.Ordinal)
                && string.Equals(first.Attachment.MediaType, second.Attachment.MediaType, StringComparison.Ordinal)
                && first.Attachment.Bytes == second.Attachment.Bytes
                && first.Attachment.Width == second.Attachment.Width
                && first.Attachment.Height == second.Attachment.Height
                && string.Equals(first.Attachment.Name, second.Attachment.Name, StringComparison.Ordinal),
            (ToolCallContent first, ToolCallContent second) =>
                string.Equals(first.Id, second.Id, StringComparison.Ordinal)
                && string.Equals(first.Name, second.Name, StringComparison.Ordinal)
                && string.Equals(first.ArgumentsJson, second.ArgumentsJson, StringComparison.Ordinal),
            _ => false,
        };
    }

    public static bool MessageEquals(AgentMessage? left, AgentMessage? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.Role == right.Role
            && left.Timestamp == right.Timestamp
            && string.Equals(left.CustomRole, right.CustomRole, StringComparison.Ordinal)
            && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal)
            && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            && left.IsError == right.IsError
            && string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
            && left.StopReason == right.StopReason
            && string.Equals(left.ErrorMessage, right.ErrorMessage, StringComparison.Ordinal)
            && UsageEquals(left.Usage, right.Usage)
            && DictionariesEqual(left.Metadata, right.Metadata)
            && MessagesContentEqual(left.Content, right.Content);
    }

    public static bool MessagesEqual(
        IReadOnlyList<AgentMessage>? left,
        IReadOnlyList<AgentMessage>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.Count == right.Count
            && left.Zip(right, MessageEquals).All(equal => equal);
    }

    public static bool WorkflowInvocationEquals(
        GameWorkflowInvocationResult? left,
        GameWorkflowInvocationResult? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
            && left.Complete == right.Complete
            && left.Succeeded == right.Succeeded
            && string.Equals(left.Error, right.Error, StringComparison.Ordinal)
            && MessagesEqual(left.Messages, right.Messages);
    }

    private static bool UsageEquals(ModelUsage? left, ModelUsage? right) =>
        left is null
            ? right is null
            : right is not null
                && left.InputTokens == right.InputTokens
                && left.OutputTokens == right.OutputTokens
                && left.CacheReadTokens == right.CacheReadTokens
                && left.CacheWriteTokens == right.CacheWriteTokens;

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool MessagesContentEqual(
        IReadOnlyList<AgentContent> left,
        IReadOnlyList<AgentContent> right) =>
        left.Count == right.Count
        && left.Zip(right, ContentEquals).All(equal => equal);
}
