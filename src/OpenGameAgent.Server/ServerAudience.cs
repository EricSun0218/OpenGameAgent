using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Server;

public enum GameAgentAudienceKind
{
    Internal = 0,
    Owner = 1,
    Public = 2,
    Recipient = 3,
}

public sealed class GameAgentAudience
{
    private GameAgentAudience(GameAgentAudienceKind kind, string? recipientId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == GameAgentAudienceKind.Recipient)
        {
            RecipientId = RequireViewerId(recipientId, nameof(recipientId));
        }
        else if (recipientId is not null)
        {
            throw new ArgumentException("Only a recipient audience can carry a recipient ID.", nameof(recipientId));
        }

        Kind = kind;
    }

    public static GameAgentAudience Internal { get; } = new(GameAgentAudienceKind.Internal, null);

    public static GameAgentAudience Owner { get; } = new(GameAgentAudienceKind.Owner, null);

    public static GameAgentAudience Public { get; } = new(GameAgentAudienceKind.Public, null);

    public GameAgentAudienceKind Kind { get; }

    public string? RecipientId { get; }

    public static GameAgentAudience Recipient(string recipientId) =>
        new(GameAgentAudienceKind.Recipient, recipientId);

    public bool IsVisibleTo(GameAgentViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.IsInternal
            || Kind == GameAgentAudienceKind.Public
            || Kind == GameAgentAudienceKind.Owner && viewer.IsOwner
            || Kind == GameAgentAudienceKind.Recipient
                && string.Equals(RecipientId, viewer.ViewerId, StringComparison.Ordinal);
    }

    internal static string RequireViewerId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 1024
            || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("A bounded, non-control viewer ID is required.", parameterName);
        }

        return value;
    }
}

public sealed class GameAgentViewer
{
    public GameAgentViewer(string? viewerId, bool isOwner, bool isInternal = false)
    {
        ViewerId = viewerId is null
            ? null
            : GameAgentAudience.RequireViewerId(viewerId, nameof(viewerId));
        if (isOwner && ViewerId is null)
        {
            throw new ArgumentException("An owner viewer requires a viewer ID.", nameof(viewerId));
        }

        IsOwner = isOwner;
        IsInternal = isInternal;
    }

    public string? ViewerId { get; }

    public bool IsOwner { get; }

    public bool IsInternal { get; }
}

public enum GameAgentAudienceOutputKind
{
    Event = 0,
    Message = 1,
}

public sealed class GameAgentAudienceContext
{
    internal GameAgentAudienceContext(
        GameSessionKey key,
        GameAgentAudienceOutputKind outputKind,
        AgentMessage? message,
        AgentEvent? agentEvent)
    {
        Key = new GameSessionKey(key.SessionId, key.ActorId);
        OutputKind = outputKind;
        Message = message;
        AgentEvent = agentEvent;
    }

    public GameSessionKey Key { get; }

    public GameAgentAudienceOutputKind OutputKind { get; }

    public AgentMessage? Message { get; }

    public AgentEvent? AgentEvent { get; }
}

public interface IGameAgentAudiencePolicy
{
    ValueTask<GameAgentViewer> ResolveViewerAsync(
        ClaimsPrincipal principal,
        GameSessionKey key,
        CancellationToken cancellationToken);

    ValueTask<GameAgentAudience> ResolveAudienceAsync(
        GameAgentAudienceContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional host-authored audience annotations that survive the existing session persistence format.
/// The stock metadata policy trusts annotations only on assistant and custom messages. User input and
/// tool output cannot promote themselves by supplying similarly named data.
/// </summary>
public static class GameAgentAudienceMetadata
{
    public const string AudienceKey = "opengameagent.audience";
    public const string RecipientKey = "opengameagent.recipient";

    public static AgentMessage WithAudience(AgentMessage message, GameAgentAudience audience)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(audience);
        if (message.Role is not (AgentRole.Assistant or AgentRole.Custom))
        {
            throw new ArgumentException(
                "Persisted audience annotations are accepted only on host-authored assistant or custom messages.",
                nameof(message));
        }

        var metadata = new Dictionary<string, string>(message.Metadata, StringComparer.Ordinal)
        {
            [AudienceKey] = audience.Kind.ToString().ToLowerInvariant(),
        };
        if (audience.RecipientId is null)
        {
            metadata.Remove(RecipientKey);
        }
        else
        {
            metadata[RecipientKey] = audience.RecipientId;
        }

        return Copy(message, metadata);
    }

    public static bool TryGetAudience(AgentMessage message, out GameAgentAudience audience)
    {
        ArgumentNullException.ThrowIfNull(message);
        audience = GameAgentAudience.Owner;
        if (message.Role is not (AgentRole.Assistant or AgentRole.Custom)
            || !message.Metadata.TryGetValue(AudienceKey, out var value))
        {
            return false;
        }

        switch (value)
        {
            case "internal":
                audience = GameAgentAudience.Internal;
                return true;
            case "owner":
                audience = GameAgentAudience.Owner;
                return true;
            case "public":
                audience = GameAgentAudience.Public;
                return true;
            case "recipient" when message.Metadata.TryGetValue(RecipientKey, out var recipient):
                try
                {
                    audience = GameAgentAudience.Recipient(recipient);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private static AgentMessage Copy(AgentMessage message, IReadOnlyDictionary<string, string> metadata) =>
        new(
            message.Role,
            message.Content,
            message.Timestamp,
            message.CustomRole,
            message.ToolCallId,
            message.ToolName,
            message.IsError,
            message.DetailsJson,
            metadata,
            message.Model,
            message.StopReason,
            message.Usage,
            message.ErrorMessage,
            message.Provider,
            message.Api,
            message.ResponseModel,
            message.ResponseId,
            message.RawStopReason,
            message.EndTurn,
            message.Role == AgentRole.Assistant ? message.Diagnostics : null,
            message.Deferred,
            addedToolNames: null);
}

public delegate ValueTask<GameAgentViewer> GameAgentViewerResolver(
    ClaimsPrincipal principal,
    GameSessionKey key,
    CancellationToken cancellationToken);

public sealed class MetadataGameAgentAudiencePolicy : IGameAgentAudiencePolicy
{
    private readonly GameAgentViewerResolver _viewerResolver;
    private readonly GameAgentAudience _defaultAudience;

    public MetadataGameAgentAudiencePolicy(
        GameAgentViewerResolver viewerResolver,
        GameAgentAudience? defaultAudience = null)
    {
        _viewerResolver = viewerResolver ?? throw new ArgumentNullException(nameof(viewerResolver));
        _defaultAudience = defaultAudience ?? GameAgentAudience.Owner;
    }

    public async ValueTask<GameAgentViewer> ResolveViewerAsync(
        ClaimsPrincipal principal,
        GameSessionKey key,
        CancellationToken cancellationToken) =>
        await _viewerResolver(principal, key, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The game audience viewer resolver returned null.");

    public ValueTask<GameAgentAudience> ResolveAudienceAsync(
        GameAgentAudienceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.AgentEvent?.Kind is AgentEventKind.ToolStarted
            or AgentEventKind.ToolProgressed
            or AgentEventKind.ToolEnded
            || context.Message?.Role == AgentRole.Tool)
        {
            return new ValueTask<GameAgentAudience>(GameAgentAudience.Internal);
        }

        return new ValueTask<GameAgentAudience>(
            context.Message is not null
                && GameAgentAudienceMetadata.TryGetAudience(context.Message, out var audience)
                    ? audience
                    : _defaultAudience);
    }
}

internal sealed class GameAgentAudienceProjection
{
    private readonly IGameAgentAudiencePolicy _policy;
    private readonly GameAgentViewer _viewer;
    private readonly GameSessionKey _key;

    public GameAgentAudienceProjection(
        IGameAgentAudiencePolicy policy,
        GameAgentViewer viewer,
        GameSessionKey key)
    {
        _policy = policy;
        _viewer = viewer;
        _key = key;
    }

    public async ValueTask<string> ProjectResultAsync(
        GameAgentRunResult result,
        CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(GameAgentWire.SerializeResult(result))?.AsObject()
            ?? throw new InvalidOperationException("The game agent result projection was not an object.");
        if (result.AgentResult is null || root["agent"] is not JsonObject agent
            || agent["newMessages"] is not JsonArray serializedMessages)
        {
            return root.ToJsonString(JsonOptions);
        }

        var projected = new JsonArray();
        for (var index = 0; index < result.AgentResult.NewMessages.Count; index++)
        {
            var message = result.AgentResult.NewMessages[index];
            var audience = await _policy.ResolveAudienceAsync(
                new GameAgentAudienceContext(_key, GameAgentAudienceOutputKind.Message, message, null),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game audience policy returned null.");
            if (!audience.IsVisibleTo(_viewer))
            {
                continue;
            }

            var node = serializedMessages[index]?.DeepClone() as JsonObject;
            if (node is not null && (_viewer.IsInternal || SanitizeMessage(node)))
            {
                projected.Add(node);
            }
        }

        agent["newMessages"] = projected;
        return root.ToJsonString(JsonOptions);
    }

    public async ValueTask<string?> ProjectEventAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        var audience = await _policy.ResolveAudienceAsync(
            new GameAgentAudienceContext(
                _key,
                GameAgentAudienceOutputKind.Event,
                agentEvent.Message,
                agentEvent),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The game audience policy returned null.");
        if (!audience.IsVisibleTo(_viewer))
        {
            return null;
        }

        var root = JsonNode.Parse(GameAgentWire.SerializeEvent(agentEvent))?.AsObject()
            ?? throw new InvalidOperationException("The game agent event projection was not an object.");
        if (_viewer.IsInternal)
        {
            return root.ToJsonString(JsonOptions);
        }

        var modelKind = root["modelEvent"]?["kind"]?.GetValue<string>();
        if (modelKind is "ReasoningStarted" or "ReasoningDelta" or "ReasoningEnded")
        {
            return null;
        }

        if (modelKind is "ToolCallStarted" or "ToolCallDelta" or "ToolCallEnded")
        {
            root["modelEvent"] = new JsonObject { ["kind"] = modelKind };
        }

        root["toolCall"] = null;
        root["toolResult"] = null;
        root["progress"] = null;
        if (root["message"] is JsonObject message && !SanitizeMessage(message))
        {
            root["message"] = null;
        }

        return root.ToJsonString(JsonOptions);
    }

    private static bool SanitizeMessage(JsonObject message)
    {
        if (string.Equals(message["role"]?.GetValue<string>(), "Tool", StringComparison.Ordinal))
        {
            return false;
        }

        if (message["content"] is JsonArray content)
        {
            var safe = new JsonArray();
            foreach (var part in content)
            {
                var kind = part?["kind"]?.GetValue<string>();
                if (kind is not ("reasoning" or "tool_call"))
                {
                    var visible = part?.DeepClone();
                    if (visible is JsonObject visibleObject)
                    {
                        visibleObject["signature"] = null;
                    }

                    safe.Add(visible);
                }
            }

            message["content"] = safe;
        }

        message["toolCallId"] = null;
        message["toolName"] = null;
        message["details"] = null;
        message["metadata"] = new JsonObject();
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
