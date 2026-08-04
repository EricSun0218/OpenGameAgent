using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Core;

namespace GameAgent.Persistence;

internal sealed class GroupInteractionFrameRecord
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("storeRevision")]
    public long StoreRevision { get; set; }

    [JsonPropertyName("previousFrameDigest")]
    public string PreviousFrameDigest { get; set; } = string.Empty;

    [JsonPropertyName("session")]
    public PersistedGroupInteractionSession? Session { get; set; }
}

internal sealed class PersistedGroupInteractionSession
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("worldBinding")]
    public PersistedGroupInteractionWorldBinding? WorldBinding
    {
        get;
        set;
    }

    [JsonPropertyName("sharedScope")]
    public JsonElement SharedScope { get; set; }

    [JsonPropertyName("sharedScopeDigest")]
    public string SharedScopeDigest { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("membershipRevision")]
    public long MembershipRevision { get; set; }

    [JsonPropertyName("members")]
    public List<PersistedGroupInteractionMember> Members { get; set; } =
        new();

    [JsonPropertyName("membershipHistory")]
    public List<PersistedGroupInteractionMembershipSnapshot>
        MembershipHistory
    { get; set; } = new();

    [JsonPropertyName("messages")]
    public List<PersistedGroupInteractionMessage> Messages { get; set; } =
        new();

    [JsonPropertyName("operations")]
    public List<PersistedGroupInteractionOperation> Operations { get; set; } =
        new();

    public static PersistedGroupInteractionSession FromSession(
        GroupInteractionSession session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        return new PersistedGroupInteractionSession
        {
            SessionId = session.SessionId,
            GroupId = session.GroupId,
            WorldBinding = session.WorldBinding is null
                ? null
                : PersistedGroupInteractionWorldBinding.FromBinding(
                    session.WorldBinding),
            SharedScope = session.SharedScope.Clone(),
            SharedScopeDigest = session.SharedScopeDigest,
            Status = session.Status,
            Revision = session.Revision,
            MembershipRevision = session.MembershipRevision,
            Members = session.Members
                .Select(PersistedGroupInteractionMember.FromMember)
                .ToList(),
            MembershipHistory = session.MembershipHistory
                .Select(
                    PersistedGroupInteractionMembershipSnapshot
                        .FromSnapshot)
                .ToList(),
            Messages = session.Messages
                .Select(PersistedGroupInteractionMessage.FromMessage)
                .ToList(),
            Operations = session.Operations
                .Select(PersistedGroupInteractionOperation.FromOperation)
                .ToList()
        };
    }

    public GroupInteractionSession Restore(
        GroupInteractionStateMachine stateMachine)
    {
        if (stateMachine is null)
        {
            throw new ArgumentNullException(nameof(stateMachine));
        }

        return stateMachine.Restore(
            SessionId,
            GroupId,
            SharedScope,
            SharedScopeDigest,
            Status,
            Revision,
            MembershipRevision,
            Required(Members, nameof(Members))
                .Select(item => Required(item, nameof(Members)).ToMember()),
            Required(MembershipHistory, nameof(MembershipHistory))
                .Select(
                    item => Required(
                            item,
                            nameof(MembershipHistory))
                        .ToSnapshot()),
            Required(Messages, nameof(Messages))
                .Select(item => Required(item, nameof(Messages)).ToMessage()),
            Required(Operations, nameof(Operations))
                .Select(
                    item => Required(item, nameof(Operations)).ToOperation()),
            WorldBinding?.ToBinding());
    }

    private static T Required<T>(T? value, string name)
        where T : class
    {
        return value ?? throw new JsonException(
            $"Persisted group field '{name}' cannot be null.");
    }
}

internal sealed class PersistedGroupInteractionWorldBinding
{
    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("timelineId")]
    public string TimelineId { get; set; } = string.Empty;

    [JsonPropertyName("timelineEpoch")]
    public long TimelineEpoch { get; set; }

    [JsonPropertyName("saveRevision")]
    public long SaveRevision { get; set; }

    public static PersistedGroupInteractionWorldBinding FromBinding(
        GroupInteractionWorldBinding binding)
    {
        return new PersistedGroupInteractionWorldBinding
        {
            WorldId = binding.WorldId,
            TimelineId = binding.TimelineId,
            TimelineEpoch = binding.TimelineEpoch,
            SaveRevision = binding.SaveRevision
        };
    }

    public GroupInteractionWorldBinding ToBinding()
    {
        return new GroupInteractionWorldBinding(
            WorldId,
            TimelineId,
            TimelineEpoch,
            SaveRevision);
    }
}

internal sealed class PersistedGroupInteractionMember
{
    [JsonPropertyName("actor")]
    public PersistedGameEntityIdentity? Actor { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new();

    public static PersistedGroupInteractionMember FromMember(
        GroupInteractionMember member)
    {
        return new PersistedGroupInteractionMember
        {
            Actor = PersistedGameEntityIdentity.FromIdentity(member.Actor),
            Roles = member.Roles.ToList()
        };
    }

    public GroupInteractionMember ToMember()
    {
        return new GroupInteractionMember(
            (Actor
             ?? throw new JsonException(
                 "A persisted group member requires an actor."))
            .ToIdentity(),
            Roles
            ?? throw new JsonException(
                "Persisted group roles cannot be null."));
    }
}

internal sealed class PersistedGroupInteractionMembershipSnapshot
{
    [JsonPropertyName("membershipRevision")]
    public long MembershipRevision { get; set; }

    [JsonPropertyName("appliedRevision")]
    public long AppliedRevision { get; set; }

    [JsonPropertyName("members")]
    public List<PersistedGroupInteractionMember> Members { get; set; } =
        new();

    public static PersistedGroupInteractionMembershipSnapshot FromSnapshot(
        GroupInteractionMembershipSnapshot snapshot)
    {
        return new PersistedGroupInteractionMembershipSnapshot
        {
            MembershipRevision = snapshot.MembershipRevision,
            AppliedRevision = snapshot.AppliedRevision,
            Members = snapshot.Members
                .Select(PersistedGroupInteractionMember.FromMember)
                .ToList()
        };
    }

    public GroupInteractionMembershipSnapshot ToSnapshot()
    {
        return new GroupInteractionMembershipSnapshot(
            MembershipRevision,
            AppliedRevision,
            (Members
             ?? throw new JsonException(
                 "Persisted membership members cannot be null."))
            .Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted membership cannot contain null "
                             + "members."))
                    .ToMember()));
    }
}

internal sealed class PersistedGroupInteractionMessage
{
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonPropertyName("payloadDigest")]
    public string PayloadDigest { get; set; } = string.Empty;

    [JsonPropertyName("payloadUtf8Bytes")]
    public int PayloadUtf8Bytes { get; set; }

    [JsonPropertyName("audienceMode")]
    public string AudienceMode { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public PersistedGameEntityIdentity? Author { get; set; }

    [JsonPropertyName("audience")]
    public List<PersistedGameEntityIdentity> Audience { get; set; } = new();

    [JsonPropertyName("membershipRevision")]
    public long MembershipRevision { get; set; }

    [JsonPropertyName("appliedRevision")]
    public long AppliedRevision { get; set; }

    [JsonPropertyName("causationId")]
    public string? CausationId { get; set; }

    public static PersistedGroupInteractionMessage FromMessage(
        GroupInteractionMessage message)
    {
        return new PersistedGroupInteractionMessage
        {
            Sequence = message.Sequence,
            MessageId = message.MessageId,
            Kind = message.Kind,
            Payload = message.Payload.Clone(),
            PayloadDigest = message.PayloadDigest,
            PayloadUtf8Bytes = message.PayloadUtf8Bytes,
            AudienceMode = message.AudienceMode,
            Author = message.Author is null
                ? null
                : PersistedGameEntityIdentity.FromIdentity(message.Author),
            Audience = message.Audience
                .Select(PersistedGameEntityIdentity.FromIdentity)
                .ToList(),
            MembershipRevision = message.MembershipRevision,
            AppliedRevision = message.AppliedRevision,
            CausationId = message.CausationId
        };
    }

    public GroupInteractionMessage ToMessage()
    {
        return new GroupInteractionMessage(
            Sequence,
            MessageId,
            Kind,
            Payload,
            PayloadDigest,
            PayloadUtf8Bytes,
            AudienceMode,
            Author?.ToIdentity(),
            (Audience
             ?? throw new JsonException(
                 "Persisted message audience cannot be null."))
            .Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted message audience cannot contain "
                             + "null."))
                    .ToIdentity())
            .ToArray(),
            MembershipRevision,
            AppliedRevision,
            CausationId);
    }
}

internal sealed class PersistedGroupInteractionOperation
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("requestDigest")]
    public string RequestDigest { get; set; } = string.Empty;

    [JsonPropertyName("appliedRevision")]
    public long AppliedRevision { get; set; }

    public static PersistedGroupInteractionOperation FromOperation(
        GroupInteractionOperationRecord operation)
    {
        return new PersistedGroupInteractionOperation
        {
            OperationId = operation.OperationId,
            Kind = operation.Kind,
            RequestDigest = operation.RequestDigest,
            AppliedRevision = operation.AppliedRevision
        };
    }

    public GroupInteractionOperationRecord ToOperation()
    {
        return new GroupInteractionOperationRecord(
            OperationId,
            Kind,
            RequestDigest,
            AppliedRevision);
    }
}

internal sealed class PersistedGameEntityIdentity
{
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = string.Empty;

    [JsonPropertyName("incarnation")]
    public long Incarnation { get; set; }

    public static PersistedGameEntityIdentity FromIdentity(
        GameEntityIdentity identity)
    {
        return new PersistedGameEntityIdentity
        {
            EntityId = identity.EntityId,
            Incarnation = identity.Incarnation
        };
    }

    public GameEntityIdentity ToIdentity()
    {
        return new GameEntityIdentity(EntityId, Incarnation);
    }
}
