using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public static class GroupInteractionStatuses
{
    public const string Open = "open";

    public const string Closed = "closed";
}

public static class GroupInteractionAudienceModes
{
    public const string AllMembers = "all_members";

    public const string Explicit = "explicit";
}

public static class GroupInteractionOperationKinds
{
    public const string Create = "create";

    public const string ReplaceMembers = "replace_members";

    public const string AppendMessages = "append_messages";

    public const string Close = "close";
}

public static class GroupInteractionWriteStatuses
{
    public const string Applied = "applied";

    public const string Idempotent = "idempotent";

    public const string NotFound = "not_found";

    public const string SessionAlreadyExists = "session_already_exists";

    public const string RevisionConflict = "revision_conflict";

    public const string MembershipRevisionConflict =
        "membership_revision_conflict";

    public const string OperationConflict = "operation_conflict";

    public const string SessionClosed = "session_closed";

    public const string WorldBindingMismatch = "world_binding_mismatch";

    public const string CapacityExceeded = "capacity_exceeded";
}

/// <summary>
/// Exact authoritative timeline lifetime to which a shared interaction
/// belongs. Save revision is the latest authoritative revision visible when
/// the session was created or explicitly rebound.
/// </summary>
public sealed class GroupInteractionWorldBinding
{
    public GroupInteractionWorldBinding(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision)
    {
        WorldId = RuntimeGuard.RequiredUtf8(
            worldId,
            128,
            nameof(worldId));
        TimelineId = RuntimeGuard.RequiredUtf8(
            timelineId,
            128,
            nameof(timelineId));
        TimelineEpoch = GroupInteractionValidation.NonNegative(
            timelineEpoch,
            nameof(timelineEpoch));
        SaveRevision = GroupInteractionValidation.NonNegative(
            saveRevision,
            nameof(saveRevision));
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public bool IsSameTimelineAs(GroupInteractionWorldBinding other)
    {
        return other is not null
               && string.Equals(
                   WorldId,
                   other.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   TimelineId,
                   other.TimelineId,
                   StringComparison.Ordinal)
               && TimelineEpoch == other.TimelineEpoch;
    }
}

/// <summary>
/// Bounds one shared interaction session. A session is a coordination
/// primitive, not a chat UI: message payloads are arbitrary JSON and all
/// visibility is explicit.
/// </summary>
public sealed class GroupInteractionLimits
{
    public GroupInteractionLimits(
        int maxMembers = 128,
        int maxMessages = 4_096,
        int maxOperations = 8_192,
        int maxMessagesPerAppend = 64,
        int maxPayloadUtf8Bytes = 65_536,
        int maxTotalPayloadUtf8Bytes = 16 * 1_048_576,
        int maxSharedScopeUtf8Bytes = 131_072,
        int maxJsonDepth = 32,
        int maxJsonNodesPerValue = 8_192,
        int maxMembershipHistoryMembers = 65_536)
    {
        if (maxMembers is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMembers));
        }

        if (maxMessages is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages));
        }

        if (maxOperations is < 2 or > 131_072)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOperations));
        }

        if (maxMessagesPerAppend is < 1 or > 4_096
            || maxMessagesPerAppend > maxMessages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMessagesPerAppend));
        }

        if (maxPayloadUtf8Bytes is < 1_024 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPayloadUtf8Bytes));
        }

        if (maxTotalPayloadUtf8Bytes < maxPayloadUtf8Bytes
            || maxTotalPayloadUtf8Bytes > 256 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalPayloadUtf8Bytes));
        }

        if (maxSharedScopeUtf8Bytes is < 1_024 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSharedScopeUtf8Bytes));
        }

        if (maxJsonDepth is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJsonDepth));
        }

        if (maxJsonNodesPerValue is < 1 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxJsonNodesPerValue));
        }

        if (maxMembershipHistoryMembers < maxMembers
            || maxMembershipHistoryMembers > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMembershipHistoryMembers));
        }

        MaxMembers = maxMembers;
        MaxMessages = maxMessages;
        MaxOperations = maxOperations;
        MaxMessagesPerAppend = maxMessagesPerAppend;
        MaxPayloadUtf8Bytes = maxPayloadUtf8Bytes;
        MaxTotalPayloadUtf8Bytes = maxTotalPayloadUtf8Bytes;
        MaxSharedScopeUtf8Bytes = maxSharedScopeUtf8Bytes;
        MaxJsonDepth = maxJsonDepth;
        MaxJsonNodesPerValue = maxJsonNodesPerValue;
        MaxMembershipHistoryMembers = maxMembershipHistoryMembers;
    }

    public int MaxMembers { get; }

    public int MaxMessages { get; }

    public int MaxOperations { get; }

    public int MaxMessagesPerAppend { get; }

    public int MaxPayloadUtf8Bytes { get; }

    public int MaxTotalPayloadUtf8Bytes { get; }

    public int MaxSharedScopeUtf8Bytes { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonNodesPerValue { get; }

    public int MaxMembershipHistoryMembers { get; }
}

/// <summary>
/// One exact entity lifetime participating in a shared interaction.
/// Reusing an entity ID with a new incarnation never inherits the old
/// participant's directed transcript.
/// </summary>
public sealed class GroupInteractionMember
{
    public GroupInteractionMember(
        GameEntityIdentity actor,
        IEnumerable<string>? roles = null)
    {
        Actor = GroupInteractionValidation.CloneIdentity(
            actor ?? throw new ArgumentNullException(nameof(actor)));
        Roles = RuntimeGuard.CopyStrings(
            roles ?? Array.Empty<string>(),
            64,
            128,
            nameof(roles),
            sort: true,
            requireUnique: true);
    }

    public GameEntityIdentity Actor { get; }

    /// <summary>
    /// Open host-defined role labels. They are context, not framework-level
    /// permissions; the host still decides who may speak or act.
    /// </summary>
    public IReadOnlyList<string> Roles { get; }
}

/// <summary>
/// Exact membership evidence retained for each membership revision. Durable
/// restore uses this history to prove that historical authors and audiences
/// belonged to the session at the revision where a message committed.
/// </summary>
public sealed class GroupInteractionMembershipSnapshot
{
    public GroupInteractionMembershipSnapshot(
        long membershipRevision,
        long appliedRevision,
        IEnumerable<GroupInteractionMember> members)
    {
        MembershipRevision = GroupInteractionValidation.NonNegative(
            membershipRevision,
            nameof(membershipRevision));
        AppliedRevision = GroupInteractionValidation.NonNegative(
            appliedRevision,
            nameof(appliedRevision));
        Members = new ReadOnlyCollection<GroupInteractionMember>(
            RuntimeInputGuard.CopyBounded(
                members
                ?? throw new ArgumentNullException(nameof(members)),
                4_096,
                item => GroupInteractionValidation.CloneMember(
                    item
                    ?? throw new ArgumentException(
                        "Membership history cannot contain null members.",
                        nameof(members))),
                nameof(members),
                "group_interaction_member_hard_limit_exceeded"));
    }

    public long MembershipRevision { get; }

    public long AppliedRevision { get; }

    public IReadOnlyList<GroupInteractionMember> Members { get; }
}

/// <summary>
/// A caller-owned message proposal. The payload may be text-shaped JSON,
/// a command, a UI selection, a world event, or any other structured value.
/// </summary>
public sealed class GroupInteractionMessageDraft
{
    public GroupInteractionMessageDraft(
        string messageId,
        string kind,
        JsonElement payload,
        string audienceMode,
        GameEntityIdentity? author = null,
        IEnumerable<GameEntityIdentity>? audience = null,
        string? causationId = null)
    {
        MessageId = RuntimeGuard.RequiredId(messageId, nameof(messageId));
        Kind = RuntimeGuard.RequiredUtf8(kind, 128, nameof(kind));
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A group interaction payload cannot be undefined.",
                nameof(payload));
        }

        Payload = payload.Clone();
        AudienceMode = GroupInteractionValidation.RequiredAudienceMode(
            audienceMode,
            nameof(audienceMode));
        Author = author is null
            ? null
            : GroupInteractionValidation.CloneIdentity(author);
        Audience = new ReadOnlyCollection<GameEntityIdentity>(
            RuntimeInputGuard.CopyBounded(
                audience ?? Array.Empty<GameEntityIdentity>(),
                4_096,
                item => GroupInteractionValidation.CloneIdentity(
                    item
                    ?? throw new ArgumentException(
                        "A group audience cannot contain null.",
                        nameof(audience))),
                nameof(audience),
                "group_interaction_audience_hard_limit_exceeded"));
        CausationId = string.IsNullOrWhiteSpace(causationId)
            ? null
            : RuntimeGuard.RequiredUtf8(
                causationId,
                128,
                nameof(causationId));
    }

    public string MessageId { get; }

    public string Kind { get; }

    public JsonElement Payload { get; }

    public string AudienceMode { get; }

    public GameEntityIdentity? Author { get; }

    /// <summary>
    /// Used only for <c>explicit</c> visibility. <c>all_members</c> resolves
    /// to an exact membership snapshot when the append commits.
    /// </summary>
    public IReadOnlyList<GameEntityIdentity> Audience { get; }

    public string? CausationId { get; }
}

public sealed class GroupInteractionMessage
{
    public GroupInteractionMessage(
        long sequence,
        string messageId,
        string kind,
        JsonElement payload,
        string payloadDigest,
        int payloadUtf8Bytes,
        string audienceMode,
        GameEntityIdentity? author,
        IReadOnlyList<GameEntityIdentity> audience,
        long membershipRevision,
        long appliedRevision,
        string? causationId)
    {
        Sequence = GroupInteractionValidation.NonNegative(
            sequence,
            nameof(sequence));
        MessageId = RuntimeGuard.RequiredId(
            messageId,
            nameof(messageId));
        Kind = RuntimeGuard.RequiredUtf8(kind, 128, nameof(kind));
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A group interaction payload cannot be undefined.",
                nameof(payload));
        }

        Payload = payload.Clone();
        if (!CanonicalJsonDigest.IsSha256(payloadDigest))
        {
            throw new ArgumentException(
                "A group payload digest must be a lowercase SHA-256 value.",
                nameof(payloadDigest));
        }

        PayloadDigest = payloadDigest;
        if (payloadUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadUtf8Bytes));
        }

        PayloadUtf8Bytes = payloadUtf8Bytes;
        AudienceMode = GroupInteractionValidation.RequiredAudienceMode(
            audienceMode,
            nameof(audienceMode));
        Author = author is null
            ? null
            : GroupInteractionValidation.CloneIdentity(author);
        Audience = new ReadOnlyCollection<GameEntityIdentity>(
            RuntimeInputGuard.CopyBounded(
                audience
                ?? throw new ArgumentNullException(nameof(audience)),
                4_096,
                item => GroupInteractionValidation.CloneIdentity(
                    item
                    ?? throw new ArgumentException(
                        "A committed audience cannot contain null.",
                        nameof(audience))),
                nameof(audience),
                "group_interaction_audience_hard_limit_exceeded"));
        if (Audience.Count == 0)
        {
            throw new ArgumentException(
                "A committed group message must have an audience.",
                nameof(audience));
        }

        MembershipRevision = GroupInteractionValidation.NonNegative(
            membershipRevision,
            nameof(membershipRevision));
        AppliedRevision = GroupInteractionValidation.NonNegative(
            appliedRevision,
            nameof(appliedRevision));
        CausationId = string.IsNullOrWhiteSpace(causationId)
            ? null
            : RuntimeGuard.RequiredUtf8(
                causationId,
                128,
                nameof(causationId));
    }

    public long Sequence { get; }

    public string MessageId { get; }

    public string Kind { get; }

    public JsonElement Payload { get; }

    public string PayloadDigest { get; }

    public int PayloadUtf8Bytes { get; }

    public string AudienceMode { get; }

    public GameEntityIdentity? Author { get; }

    /// <summary>
    /// Exact entity incarnations allowed to consume this message.
    /// </summary>
    public IReadOnlyList<GameEntityIdentity> Audience { get; }

    public long MembershipRevision { get; }

    public long AppliedRevision { get; }

    public string? CausationId { get; }
}

public sealed class GroupInteractionOperationRecord
{
    public GroupInteractionOperationRecord(
        string operationId,
        string kind,
        string requestDigest,
        long appliedRevision)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        Kind = GroupInteractionValidation.RequiredOperationKind(
            kind,
            nameof(kind));
        if (!CanonicalJsonDigest.IsSha256(requestDigest))
        {
            throw new ArgumentException(
                "A group operation digest must be a lowercase SHA-256 value.",
                nameof(requestDigest));
        }

        RequestDigest = requestDigest;
        AppliedRevision = GroupInteractionValidation.NonNegative(
            appliedRevision,
            nameof(appliedRevision));
    }

    public string OperationId { get; }

    public string Kind { get; }

    public string RequestDigest { get; }

    public long AppliedRevision { get; }
}

/// <summary>
/// Immutable state that a durable store can persist as one value. Revisions
/// are session-local and are intentionally unrelated to wall-clock time.
/// </summary>
public sealed class GroupInteractionSession
{
    internal GroupInteractionSession(
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        string sharedScopeDigest,
        string status,
        long revision,
        long membershipRevision,
        IReadOnlyList<GroupInteractionMember> members,
        IReadOnlyList<GroupInteractionMembershipSnapshot> membershipHistory,
        IReadOnlyList<GroupInteractionMessage> messages,
        IReadOnlyList<GroupInteractionOperationRecord> operations,
        int totalPayloadUtf8Bytes,
        GroupInteractionWorldBinding? worldBinding)
    {
        SessionId = sessionId;
        GroupId = groupId;
        SharedScope = sharedScope.Clone();
        SharedScopeDigest = sharedScopeDigest;
        Status = status;
        Revision = revision;
        MembershipRevision = membershipRevision;
        Members = new ReadOnlyCollection<GroupInteractionMember>(
            members
                .Select(GroupInteractionValidation.CloneMember)
                .ToArray());
        MembershipHistory =
            new ReadOnlyCollection<GroupInteractionMembershipSnapshot>(
                membershipHistory
                    .Select(
                        GroupInteractionValidation
                            .CloneMembershipSnapshot)
                    .ToArray());
        TotalMembershipHistoryMembers = MembershipHistory.Sum(
            item => item.Members.Count);
        Messages = new ReadOnlyCollection<GroupInteractionMessage>(
            messages
                .Select(GroupInteractionValidation.CloneMessage)
                .ToArray());
        Operations = new ReadOnlyCollection<GroupInteractionOperationRecord>(
            operations
                .Select(
                    item => new GroupInteractionOperationRecord(
                        item.OperationId,
                        item.Kind,
                        item.RequestDigest,
                        item.AppliedRevision))
                .ToArray());
        TotalPayloadUtf8Bytes = totalPayloadUtf8Bytes;
        WorldBinding = GroupInteractionValidation.CloneWorldBinding(
            worldBinding);
    }

    public string SessionId { get; }

    public string GroupId { get; }

    /// <summary>
    /// Explicit game-defined shared scope. Private memory is never inferred
    /// from or copied into this value by the framework.
    /// </summary>
    public JsonElement SharedScope { get; }

    public string SharedScopeDigest { get; }

    public string Status { get; }

    public long Revision { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<GroupInteractionMember> Members { get; }

    public IReadOnlyList<GroupInteractionMembershipSnapshot>
        MembershipHistory
    { get; }

    public int TotalMembershipHistoryMembers { get; }

    public IReadOnlyList<GroupInteractionMessage> Messages { get; }

    public IReadOnlyList<GroupInteractionOperationRecord> Operations { get; }

    public int TotalPayloadUtf8Bytes { get; }

    /// <summary>
    /// Exact world/timeline lifetime for bundle and settlement admission.
    /// Legacy unbound sessions remain usable by the generic group store but
    /// fail closed at those authoritative boundaries.
    /// </summary>
    public GroupInteractionWorldBinding? WorldBinding { get; }
}

public sealed class GroupInteractionProjection
{
    internal GroupInteractionProjection(
        string sessionId,
        string groupId,
        string status,
        long revision,
        long membershipRevision,
        GameEntityIdentity viewer,
        JsonElement sharedScope,
        string sharedScopeDigest,
        IReadOnlyList<GroupInteractionMember> members,
        IReadOnlyList<GroupInteractionMessage> messages,
        GroupInteractionWorldBinding? worldBinding)
    {
        SessionId = sessionId;
        GroupId = groupId;
        Status = status;
        Revision = revision;
        MembershipRevision = membershipRevision;
        Viewer = GroupInteractionValidation.CloneIdentity(viewer);
        SharedScope = sharedScope.Clone();
        SharedScopeDigest = sharedScopeDigest;
        Members = new ReadOnlyCollection<GroupInteractionMember>(
            members
                .Select(GroupInteractionValidation.CloneMember)
                .ToArray());
        Messages = new ReadOnlyCollection<GroupInteractionMessage>(
            messages
                .Select(GroupInteractionValidation.CloneMessage)
                .ToArray());
        WorldBinding = GroupInteractionValidation.CloneWorldBinding(
            worldBinding);
    }

    public string SessionId { get; }

    public string GroupId { get; }

    public string Status { get; }

    public long Revision { get; }

    public long MembershipRevision { get; }

    public GameEntityIdentity Viewer { get; }

    public JsonElement SharedScope { get; }

    public string SharedScopeDigest { get; }

    public IReadOnlyList<GroupInteractionMember> Members { get; }

    /// <summary>
    /// Contains only messages whose exact committed audience includes the
    /// viewer's current entity incarnation.
    /// </summary>
    public IReadOnlyList<GroupInteractionMessage> Messages { get; }

    public GroupInteractionWorldBinding? WorldBinding { get; }
}

public sealed class GroupInteractionWriteResult
{
    /// <summary>
    /// Creates a store result. This constructor is public so an external
    /// durable implementation of <see cref="IGroupInteractionStore"/> can
    /// report every interface-defined outcome.
    /// </summary>
    public GroupInteractionWriteResult(
        string status,
        GroupInteractionSession? session,
        long? appliedRevision = null)
    {
        Status = GroupInteractionValidation.RequiredWriteStatus(
            status,
            nameof(status));
        var succeeded = string.Equals(
                Status,
                GroupInteractionWriteStatuses.Applied,
                StringComparison.Ordinal)
            || string.Equals(
                Status,
                GroupInteractionWriteStatuses.Idempotent,
                StringComparison.Ordinal);
        var notFound = string.Equals(
            Status,
            GroupInteractionWriteStatuses.NotFound,
            StringComparison.Ordinal);
        if (notFound)
        {
            if (session is not null || appliedRevision.HasValue)
            {
                throw new ArgumentException(
                    "A not-found result cannot carry session state.",
                    nameof(session));
            }
        }
        else if (session is null)
        {
            throw new ArgumentNullException(
                nameof(session),
                "A group write result must carry the current session.");
        }

        if (succeeded)
        {
            if (!appliedRevision.HasValue
                || appliedRevision.Value < 0
                || appliedRevision.Value > session!.Revision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(appliedRevision),
                    "A successful result requires a valid applied revision.");
            }
        }
        else if (appliedRevision.HasValue)
        {
            throw new ArgumentException(
                "A rejected result cannot carry an applied revision.",
                nameof(appliedRevision));
        }

        Session = session;
        AppliedRevision = appliedRevision;
    }

    public string Status { get; }

    public GroupInteractionSession? Session { get; }

    public long? AppliedRevision { get; }

    public bool Succeeded =>
        string.Equals(
            Status,
            GroupInteractionWriteStatuses.Applied,
            StringComparison.Ordinal)
        || string.Equals(
            Status,
            GroupInteractionWriteStatuses.Idempotent,
            StringComparison.Ordinal);
}

public sealed class GroupInteractionCreateRequest
{
    public GroupInteractionCreateRequest(
        string operationId,
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        IEnumerable<GroupInteractionMember> members)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        SessionId = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        GroupId = RuntimeGuard.RequiredId(groupId, nameof(groupId));
        if (sharedScope.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A shared scope cannot be undefined.",
                nameof(sharedScope));
        }

        SharedScope = sharedScope.Clone();
        Members = new ReadOnlyCollection<GroupInteractionMember>(
            RuntimeInputGuard.CopyBounded(
                members
                ?? throw new ArgumentNullException(nameof(members)),
                4_096,
                item => GroupInteractionValidation.CloneMember(
                    item
                    ?? throw new ArgumentException(
                        "Group members cannot contain null.",
                        nameof(members))),
                nameof(members),
                "group_interaction_member_hard_limit_exceeded"));
    }

    public GroupInteractionCreateRequest(
        string operationId,
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        IEnumerable<GroupInteractionMember> members,
        GroupInteractionWorldBinding worldBinding)
        : this(
            operationId,
            sessionId,
            groupId,
            sharedScope,
            members)
    {
        WorldBinding = GroupInteractionValidation.CloneWorldBinding(
            worldBinding
            ?? throw new ArgumentNullException(nameof(worldBinding)));
    }

    public string OperationId { get; }

    public string SessionId { get; }

    public string GroupId { get; }

    public JsonElement SharedScope { get; }

    public IReadOnlyList<GroupInteractionMember> Members { get; }

    public GroupInteractionWorldBinding? WorldBinding { get; }
}

public sealed class GroupInteractionMembershipRequest
{
    public GroupInteractionMembershipRequest(
        string operationId,
        string sessionId,
        long expectedRevision,
        long expectedMembershipRevision,
        IEnumerable<GroupInteractionMember> members)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        SessionId = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        ExpectedRevision = GroupInteractionValidation.NonNegative(
            expectedRevision,
            nameof(expectedRevision));
        ExpectedMembershipRevision = GroupInteractionValidation.NonNegative(
            expectedMembershipRevision,
            nameof(expectedMembershipRevision));
        Members = new ReadOnlyCollection<GroupInteractionMember>(
            RuntimeInputGuard.CopyBounded(
                members
                ?? throw new ArgumentNullException(nameof(members)),
                4_096,
                item => GroupInteractionValidation.CloneMember(
                    item
                    ?? throw new ArgumentException(
                        "Group members cannot contain null.",
                        nameof(members))),
                nameof(members),
                "group_interaction_member_hard_limit_exceeded"));
    }

    public string OperationId { get; }

    public string SessionId { get; }

    public long ExpectedRevision { get; }

    public long ExpectedMembershipRevision { get; }

    public IReadOnlyList<GroupInteractionMember> Members { get; }
}

public sealed class GroupInteractionAppendRequest
{
    public GroupInteractionAppendRequest(
        string operationId,
        string sessionId,
        long expectedRevision,
        long expectedMembershipRevision,
        IEnumerable<GroupInteractionMessageDraft> messages)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        SessionId = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        ExpectedRevision = GroupInteractionValidation.NonNegative(
            expectedRevision,
            nameof(expectedRevision));
        ExpectedMembershipRevision = GroupInteractionValidation.NonNegative(
            expectedMembershipRevision,
            nameof(expectedMembershipRevision));
        Messages = new ReadOnlyCollection<GroupInteractionMessageDraft>(
            RuntimeInputGuard.CopyBounded(
                messages
                ?? throw new ArgumentNullException(nameof(messages)),
                4_096,
                item => GroupInteractionValidation.CloneDraft(
                    item
                    ?? throw new ArgumentException(
                        "Group messages cannot contain null.",
                        nameof(messages))),
                nameof(messages),
                "group_interaction_append_hard_limit_exceeded"));
        if (Messages.Count == 0)
        {
            throw new ArgumentException(
                "A group append must contain at least one message.",
                nameof(messages));
        }
    }

    public string OperationId { get; }

    public string SessionId { get; }

    public long ExpectedRevision { get; }

    public long ExpectedMembershipRevision { get; }

    public IReadOnlyList<GroupInteractionMessageDraft> Messages { get; }
}

public sealed class GroupInteractionCloseRequest
{
    public GroupInteractionCloseRequest(
        string operationId,
        string sessionId,
        long expectedRevision,
        long expectedMembershipRevision)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        SessionId = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        ExpectedRevision = GroupInteractionValidation.NonNegative(
            expectedRevision,
            nameof(expectedRevision));
        ExpectedMembershipRevision = GroupInteractionValidation.NonNegative(
            expectedMembershipRevision,
            nameof(expectedMembershipRevision));
    }

    public string OperationId { get; }

    public string SessionId { get; }

    public long ExpectedRevision { get; }

    public long ExpectedMembershipRevision { get; }
}

/// <summary>
/// Pure deterministic transitions used by stores. A durable implementation
/// can persist the returned immutable session with its own atomic replace.
/// </summary>
public sealed class GroupInteractionStateMachine
{
    private readonly GroupInteractionLimits _limits;
    private readonly JsonValueLimits _payloadLimits;
    private readonly JsonValueLimits _scopeLimits;

    public GroupInteractionStateMachine(
        GroupInteractionLimits? limits = null)
    {
        _limits = limits ?? new GroupInteractionLimits();
        _payloadLimits = new JsonValueLimits(
            _limits.MaxPayloadUtf8Bytes,
            _limits.MaxJsonDepth,
            _limits.MaxJsonNodesPerValue,
            _limits.MaxPayloadUtf8Bytes,
            _limits.MaxJsonNodesPerValue);
        _scopeLimits = new JsonValueLimits(
            _limits.MaxSharedScopeUtf8Bytes,
            _limits.MaxJsonDepth,
            _limits.MaxJsonNodesPerValue,
            _limits.MaxSharedScopeUtf8Bytes,
            _limits.MaxJsonNodesPerValue);
    }

    public GroupInteractionWriteResult Create(
        GroupInteractionCreateRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var members = ValidateMembers(request.Members);
        _ = JsonValueInspector.ValidateAndMeasure(
            request.SharedScope,
            _scopeLimits,
            nameof(request.SharedScope));
        var scopeDigest = ComputeAdmittedJsonDigest(request.SharedScope);
        var digest = DigestCreate(request, scopeDigest, members);
        var operation = new GroupInteractionOperationRecord(
            request.OperationId,
            GroupInteractionOperationKinds.Create,
            digest,
            appliedRevision: 0);
        var membership = new GroupInteractionMembershipSnapshot(
            membershipRevision: 0,
            appliedRevision: 0,
            members);
        var session = new GroupInteractionSession(
            request.SessionId,
            request.GroupId,
            request.SharedScope,
            scopeDigest,
            GroupInteractionStatuses.Open,
            revision: 0,
            membershipRevision: 0,
            members,
            new[] { membership },
            Array.Empty<GroupInteractionMessage>(),
            new[] { operation },
            totalPayloadUtf8Bytes: 0,
            request.WorldBinding);
        return new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.Applied,
            session,
            appliedRevision: 0);
    }

    /// <summary>
    /// Rebuilds and validates a session loaded by a durable store. Persisted
    /// data is never trusted based on collection counts or caller-provided
    /// digests alone.
    /// </summary>
    public GroupInteractionSession Restore(
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        string sharedScopeDigest,
        string status,
        long revision,
        long membershipRevision,
        IEnumerable<GroupInteractionMember> members,
        IEnumerable<GroupInteractionMembershipSnapshot> membershipHistory,
        IEnumerable<GroupInteractionMessage> messages,
        IEnumerable<GroupInteractionOperationRecord> operations)
    {
        return Restore(
            sessionId,
            groupId,
            sharedScope,
            sharedScopeDigest,
            status,
            revision,
            membershipRevision,
            members,
            membershipHistory,
            messages,
            operations,
            worldBinding: null);
    }

    public GroupInteractionSession Restore(
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        string sharedScopeDigest,
        string status,
        long revision,
        long membershipRevision,
        IEnumerable<GroupInteractionMember> members,
        IEnumerable<GroupInteractionMembershipSnapshot> membershipHistory,
        IEnumerable<GroupInteractionMessage> messages,
        IEnumerable<GroupInteractionOperationRecord> operations,
        GroupInteractionWorldBinding? worldBinding)
    {
        var admittedSessionId = RuntimeGuard.RequiredId(
            sessionId,
            nameof(sessionId));
        var admittedGroupId = RuntimeGuard.RequiredId(
            groupId,
            nameof(groupId));
        if (!string.Equals(
                status,
                GroupInteractionStatuses.Open,
                StringComparison.Ordinal)
            && !string.Equals(
                status,
                GroupInteractionStatuses.Closed,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Unknown group interaction status.",
                nameof(status));
        }

        var admittedRevision = GroupInteractionValidation.NonNegative(
            revision,
            nameof(revision));
        var admittedMembershipRevision =
            GroupInteractionValidation.NonNegative(
                membershipRevision,
                nameof(membershipRevision));
        if (admittedRevision >= _limits.MaxOperations)
        {
            throw new RuntimeContentLimitException(
                nameof(revision),
                "group_interaction_operation_limit_exceeded",
                "The restored revision exceeds the operation-history limit.");
        }

        if (admittedMembershipRevision > admittedRevision)
        {
            throw new ArgumentException(
                "Membership revision cannot exceed session revision.",
                nameof(membershipRevision));
        }

        var memberSnapshots = RuntimeInputGuard.CopyBounded(
            members ?? throw new ArgumentNullException(nameof(members)),
            4_096,
            item => GroupInteractionValidation.CloneMember(
                item
                ?? throw new ArgumentException(
                    "Restored members cannot contain null.",
                    nameof(members))),
            nameof(members),
            "group_interaction_member_hard_limit_exceeded");
        var admittedMembers = ValidateMembers(memberSnapshots);
        var membershipSnapshots = RuntimeInputGuard.CopyBounded(
            membershipHistory
            ?? throw new ArgumentNullException(nameof(membershipHistory)),
            _limits.MaxOperations,
            item => GroupInteractionValidation.CloneMembershipSnapshot(
                item
                ?? throw new ArgumentException(
                    "Restored membership history cannot contain null.",
                    nameof(membershipHistory))),
            nameof(membershipHistory),
            "group_interaction_membership_history_limit_exceeded");
        Array.Sort(
            membershipSnapshots,
            (left, right) =>
                left.MembershipRevision.CompareTo(
                    right.MembershipRevision));
        if (membershipSnapshots.LongLength
            != admittedMembershipRevision + 1)
        {
            throw new ArgumentException(
                "Membership history must contain every revision from zero.",
                nameof(membershipHistory));
        }

        var totalMembershipHistoryMembers = 0L;
        var priorAppliedRevision = -1L;
        for (var index = 0; index < membershipSnapshots.Length; index++)
        {
            var snapshot = membershipSnapshots[index];
            if (snapshot.MembershipRevision != index
                || snapshot.AppliedRevision > admittedRevision
                || index == 0 && snapshot.AppliedRevision != 0
                || index > 0
                && snapshot.AppliedRevision <= priorAppliedRevision)
            {
                throw new ArgumentException(
                    "Membership history revisions are not contiguous and ordered.",
                    nameof(membershipHistory));
            }

            var validated = ValidateMembers(snapshot.Members);
            membershipSnapshots[index] =
                new GroupInteractionMembershipSnapshot(
                    snapshot.MembershipRevision,
                    snapshot.AppliedRevision,
                    validated);
            totalMembershipHistoryMembers = checked(
                totalMembershipHistoryMembers + validated.Count);
            if (totalMembershipHistoryMembers
                > _limits.MaxMembershipHistoryMembers)
            {
                throw new RuntimeContentLimitException(
                    nameof(membershipHistory),
                    "group_interaction_membership_history_limit_exceeded",
                    "Restored membership history exceeds its configured limit.");
            }

            priorAppliedRevision = snapshot.AppliedRevision;
        }

        if (!MembersEqual(
                admittedMembers,
                membershipSnapshots[^1].Members))
        {
            throw new ArgumentException(
                "Current members must match the latest membership snapshot.",
                nameof(members));
        }

        var messageSnapshots = RuntimeInputGuard.CopyBounded(
            messages ?? throw new ArgumentNullException(nameof(messages)),
            65_536,
            item => GroupInteractionValidation.CloneMessage(
                item
                ?? throw new ArgumentException(
                    "Restored messages cannot contain null.",
                    nameof(messages))),
            nameof(messages),
            "group_interaction_message_hard_limit_exceeded");
        if (messageSnapshots.Length > _limits.MaxMessages)
        {
            throw new RuntimeContentLimitException(
                nameof(messages),
                "group_interaction_message_limit_exceeded",
                "Restored messages exceed the configured limit.");
        }

        Array.Sort(
            messageSnapshots,
            (left, right) => left.Sequence.CompareTo(right.Sequence));
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        var payloadBytes = 0L;
        for (var index = 0; index < messageSnapshots.Length; index++)
        {
            var message = messageSnapshots[index];
            if (message.Sequence != index)
            {
                throw new ArgumentException(
                    "Restored message sequences must be contiguous from zero.",
                    nameof(messages));
            }

            if (!messageIds.Add(message.MessageId))
            {
                throw new ArgumentException(
                    "Restored message IDs must be unique.",
                    nameof(messages));
            }

            if (message.MembershipRevision
                > admittedMembershipRevision)
            {
                throw new ArgumentException(
                    "A restored message cannot come from a future membership revision.",
                    nameof(messages));
            }

            if (message.AppliedRevision is < 1
                || message.AppliedRevision > admittedRevision)
            {
                throw new ArgumentException(
                    "A restored message has an invalid applied revision.",
                    nameof(messages));
            }

            var messageMembership = MembershipAtOperationRevision(
                membershipSnapshots,
                message.AppliedRevision);
            if (messageMembership.MembershipRevision
                != message.MembershipRevision)
            {
                throw new ArgumentException(
                    "A restored message is bound to the wrong membership revision.",
                    nameof(messages));
            }

            if (message.Author is not null
                && !ContainsIdentity(
                    messageMembership.Members,
                    message.Author))
            {
                throw new ArgumentException(
                    "A restored message author was not a member when it committed.",
                    nameof(messages));
            }

            var measured = JsonValueInspector.ValidateAndMeasure(
                message.Payload,
                _payloadLimits,
                nameof(messages));
            if (measured != message.PayloadUtf8Bytes
                || !string.Equals(
                    ComputeAdmittedJsonDigest(message.Payload),
                    message.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A restored message payload does not match its evidence.",
                    nameof(messages));
            }

            var audience = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in message.Audience)
            {
                if (!ContainsIdentity(
                        messageMembership.Members,
                        identity))
                {
                    throw new ArgumentException(
                        "A restored audience member did not belong to the session.",
                        nameof(messages));
                }

                if (!audience.Add(
                        GroupInteractionValidation.IdentityKey(identity)))
                {
                    throw new ArgumentException(
                        "A restored message audience cannot contain duplicates.",
                        nameof(messages));
                }
            }

            if (message.Audience.Count > _limits.MaxMembers)
            {
                throw new RuntimeContentLimitException(
                    nameof(messages),
                    "group_interaction_audience_limit_exceeded",
                    "A restored audience exceeds the configured member limit.");
            }

            if (string.Equals(
                    message.AudienceMode,
                    GroupInteractionAudienceModes.AllMembers,
                    StringComparison.Ordinal)
                && !AudienceMatchesMembers(
                    message.Audience,
                    messageMembership.Members))
            {
                throw new ArgumentException(
                    "An all-members message must retain its exact membership audience.",
                    nameof(messages));
            }

            payloadBytes = checked(
                payloadBytes + message.PayloadUtf8Bytes);
            if (payloadBytes > _limits.MaxTotalPayloadUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(messages),
                    "group_interaction_total_payload_limit_exceeded",
                    "Restored payloads exceed the configured aggregate limit.");
            }
        }

        var operationSnapshots = RuntimeInputGuard.CopyBounded(
            operations
            ?? throw new ArgumentNullException(nameof(operations)),
            131_072,
            item => new GroupInteractionOperationRecord(
                item?.OperationId
                ?? throw new ArgumentException(
                    "Restored operations cannot contain null.",
                    nameof(operations)),
                item.Kind,
                item.RequestDigest,
                item.AppliedRevision),
            nameof(operations),
            "group_interaction_operation_hard_limit_exceeded");
        if (operationSnapshots.Length > _limits.MaxOperations)
        {
            throw new RuntimeContentLimitException(
                nameof(operations),
                "group_interaction_operation_limit_exceeded",
                "Restored operations exceed the configured limit.");
        }

        Array.Sort(
            operationSnapshots,
            (left, right) =>
                left.AppliedRevision.CompareTo(right.AppliedRevision));
        if (operationSnapshots.LongLength != admittedRevision + 1)
        {
            throw new ArgumentException(
                "A restored session must retain one operation per revision.",
                nameof(operations));
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < operationSnapshots.Length; index++)
        {
            var operation = operationSnapshots[index];
            if (operation.AppliedRevision != index
                || !operationIds.Add(operation.OperationId))
            {
                throw new ArgumentException(
                    "Restored operations must have unique IDs and contiguous revisions.",
                    nameof(operations));
            }
        }

        if (string.Equals(
                status,
                GroupInteractionStatuses.Open,
                StringComparison.Ordinal)
            && operationSnapshots.Length >= _limits.MaxOperations)
        {
            throw new ArgumentException(
                "An open session must retain capacity for a close operation.",
                nameof(operations));
        }

        _ = JsonValueInspector.ValidateAndMeasure(
            sharedScope,
            _scopeLimits,
            nameof(sharedScope));
        var actualScopeDigest =
            ComputeAdmittedJsonDigest(sharedScope);
        if (!string.Equals(
                actualScopeDigest,
                sharedScopeDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The restored shared scope does not match its digest.",
                nameof(sharedScopeDigest));
        }

        ValidateOperationEvidence(
            admittedSessionId,
            admittedGroupId,
            sharedScope,
            actualScopeDigest,
            status,
            admittedRevision,
            membershipSnapshots,
            messageSnapshots,
            operationSnapshots,
            worldBinding);

        return new GroupInteractionSession(
            admittedSessionId,
            admittedGroupId,
            sharedScope,
            actualScopeDigest,
            status,
            admittedRevision,
            admittedMembershipRevision,
            admittedMembers,
            membershipSnapshots,
            messageSnapshots,
            operationSnapshots,
            checked((int)payloadBytes),
            worldBinding);
    }

    /// <summary>
    /// Derives the same immutable shared history for a new authoritative
    /// timeline lifetime. The create-operation evidence is recomputed so the
    /// resulting session remains self-verifying; all later revisions and
    /// audience snapshots are preserved exactly.
    /// </summary>
    public GroupInteractionSession RebindWorld(
        GroupInteractionSession session,
        GroupInteractionWorldBinding worldBinding)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        var admittedBinding =
            GroupInteractionValidation.CloneWorldBinding(
                worldBinding
                ?? throw new ArgumentNullException(nameof(worldBinding)))!;
        var create = session.Operations[0];
        var createRequest = new GroupInteractionCreateRequest(
            create.OperationId,
            session.SessionId,
            session.GroupId,
            session.SharedScope,
            session.MembershipHistory[0].Members,
            admittedBinding);
        var reboundOperations = session.Operations
            .Select(
                (operation, index) => index == 0
                    ? new GroupInteractionOperationRecord(
                        operation.OperationId,
                        operation.Kind,
                        DigestCreate(
                            createRequest,
                            session.SharedScopeDigest,
                            session.MembershipHistory[0].Members),
                        operation.AppliedRevision)
                    : new GroupInteractionOperationRecord(
                        operation.OperationId,
                        operation.Kind,
                        operation.RequestDigest,
                        operation.AppliedRevision))
            .ToArray();
        return Restore(
            session.SessionId,
            session.GroupId,
            session.SharedScope,
            session.SharedScopeDigest,
            session.Status,
            session.Revision,
            session.MembershipRevision,
            session.Members,
            session.MembershipHistory,
            session.Messages,
            reboundOperations,
            admittedBinding);
    }

    public GroupInteractionWriteResult ReplaceMembers(
        GroupInteractionSession current,
        GroupInteractionMembershipRequest request)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        EnsureSession(current, request.SessionId);
        var members = ValidateMembers(request.Members);
        var digest = DigestMembership(request, members);
        var replay = Replay(current, request.OperationId, digest);
        if (replay is not null)
        {
            return replay;
        }

        var conflict = CheckWritable(
            current,
            request.ExpectedRevision,
            request.ExpectedMembershipRevision);
        if (conflict is not null)
        {
            return conflict;
        }

        if (current.Operations.Count >= _limits.MaxOperations - 1
            || members.Count
            > _limits.MaxMembershipHistoryMembers
              - current.TotalMembershipHistoryMembers)
        {
            return Capacity(current);
        }

        var nextRevision = checked(current.Revision + 1);
        var nextMembershipRevision =
            checked(current.MembershipRevision + 1);
        var operations = AppendOperation(
            current,
            request.OperationId,
            GroupInteractionOperationKinds.ReplaceMembers,
            digest,
            nextRevision);
        var membershipHistory = current.MembershipHistory
            .Concat(
                new[]
                {
                    new GroupInteractionMembershipSnapshot(
                        nextMembershipRevision,
                        nextRevision,
                        members)
                })
            .ToArray();
        var session = CopySession(
            current,
            revision: nextRevision,
            membershipRevision: nextMembershipRevision,
            members: members,
            membershipHistory: membershipHistory,
            operations: operations);
        return Applied(session, nextRevision);
    }

    public GroupInteractionWriteResult Append(
        GroupInteractionSession current,
        GroupInteractionAppendRequest request)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        EnsureSession(current, request.SessionId);
        if (request.Messages.Count > _limits.MaxMessagesPerAppend)
        {
            throw new RuntimeContentLimitException(
                nameof(request.Messages),
                "group_interaction_append_limit_exceeded",
                "A group append exceeds the configured message limit.");
        }

        var prepared = PrepareDrafts(request.Messages);
        var digest = DigestAppend(request, prepared);
        var replay = Replay(current, request.OperationId, digest);
        if (replay is not null)
        {
            return replay;
        }

        var conflict = CheckWritable(
            current,
            request.ExpectedRevision,
            request.ExpectedMembershipRevision);
        if (conflict is not null)
        {
            return conflict;
        }

        if (current.Operations.Count >= _limits.MaxOperations - 1
            || prepared.Count > _limits.MaxMessages - current.Messages.Count)
        {
            return Capacity(current);
        }

        var knownMessageIds = new HashSet<string>(
            current.Messages.Select(item => item.MessageId),
            StringComparer.Ordinal);
        var appended = new GroupInteractionMessage[prepared.Count];
        var addedBytes = 0L;
        var nextRevision = checked(current.Revision + 1);
        for (var index = 0; index < prepared.Count; index++)
        {
            var item = prepared[index];
            if (!knownMessageIds.Add(item.Draft.MessageId))
            {
                throw new ArgumentException(
                    "Group message IDs must be unique within a session.",
                    nameof(request));
            }

            if (item.Draft.Author is not null
                && !ContainsIdentity(current.Members, item.Draft.Author))
            {
                throw new ArgumentException(
                    "A group message author must be an exact current member.",
                    nameof(request));
            }

            var audience = ResolveAudience(current, item.Draft);
            addedBytes = checked(addedBytes + item.PayloadUtf8Bytes);
            if (addedBytes
                > _limits.MaxTotalPayloadUtf8Bytes
                  - current.TotalPayloadUtf8Bytes)
            {
                return Capacity(current);
            }

            appended[index] = new GroupInteractionMessage(
                checked(current.Messages.Count + (long)index),
                item.Draft.MessageId,
                item.Draft.Kind,
                item.Draft.Payload,
                item.PayloadDigest,
                item.PayloadUtf8Bytes,
                item.Draft.AudienceMode,
                item.Draft.Author,
                audience,
                current.MembershipRevision,
                nextRevision,
                item.Draft.CausationId);
        }

        var messages = current.Messages.Concat(appended).ToArray();
        var operations = AppendOperation(
            current,
            request.OperationId,
            GroupInteractionOperationKinds.AppendMessages,
            digest,
            nextRevision);
        var session = CopySession(
            current,
            revision: nextRevision,
            messages: messages,
            operations: operations,
            totalPayloadUtf8Bytes:
                checked(current.TotalPayloadUtf8Bytes + (int)addedBytes));
        return Applied(session, nextRevision);
    }

    public GroupInteractionWriteResult Close(
        GroupInteractionSession current,
        GroupInteractionCloseRequest request)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        EnsureSession(current, request.SessionId);
        var digest = DigestClose(request);
        var replay = Replay(current, request.OperationId, digest);
        if (replay is not null)
        {
            return replay;
        }

        var conflict = CheckWritable(
            current,
            request.ExpectedRevision,
            request.ExpectedMembershipRevision);
        if (conflict is not null)
        {
            return conflict;
        }

        if (current.Operations.Count >= _limits.MaxOperations)
        {
            return Capacity(current);
        }

        var nextRevision = checked(current.Revision + 1);
        var operations = AppendOperation(
            current,
            request.OperationId,
            GroupInteractionOperationKinds.Close,
            digest,
            nextRevision);
        var session = CopySession(
            current,
            status: GroupInteractionStatuses.Closed,
            revision: nextRevision,
            operations: operations);
        return Applied(session, nextRevision);
    }

    public GroupInteractionProjection Project(
        GroupInteractionSession session,
        GameEntityIdentity viewer)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (viewer is null)
        {
            throw new ArgumentNullException(nameof(viewer));
        }

        var member = session.Members.FirstOrDefault(
            item => item.Actor.IsSameIncarnation(viewer));
        if (member is null)
        {
            throw new InvalidOperationException(
                "Only an exact current group member can read a projection.");
        }

        var visible = session.Messages
            .Where(
                message => message.Audience.Any(
                    item => item.IsSameIncarnation(viewer)))
            .ToArray();
        return new GroupInteractionProjection(
            session.SessionId,
            session.GroupId,
            session.Status,
            session.Revision,
            session.MembershipRevision,
            member.Actor,
            session.SharedScope,
            session.SharedScopeDigest,
            session.Members,
            visible,
            session.WorldBinding);
    }

    private IReadOnlyList<GroupInteractionMember> ValidateMembers(
        IReadOnlyList<GroupInteractionMember> source)
    {
        if (source.Count is < 1)
        {
            throw new ArgumentException(
                "A group interaction requires at least one member.",
                nameof(source));
        }

        if (source.Count > _limits.MaxMembers)
        {
            throw new RuntimeContentLimitException(
                nameof(source),
                "group_interaction_member_limit_exceeded",
                "Group membership exceeds the configured limit.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GroupInteractionMember>(source.Count);
        foreach (var member in source)
        {
            var snapshot = GroupInteractionValidation.CloneMember(
                member
                ?? throw new ArgumentException(
                    "Group members cannot contain null.",
                    nameof(source)));
            if (!ids.Add(snapshot.Actor.EntityId))
            {
                throw new ArgumentException(
                    "A group cannot contain two incarnations of one entity ID.",
                    nameof(source));
            }

            result.Add(snapshot);
        }

        result.Sort(GroupInteractionValidation.CompareMembers);
        return new ReadOnlyCollection<GroupInteractionMember>(
            result.ToArray());
    }

    private IReadOnlyList<PreparedDraft> PrepareDrafts(
        IReadOnlyList<GroupInteractionMessageDraft> drafts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PreparedDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            var snapshot = GroupInteractionValidation.CloneDraft(
                draft
                ?? throw new ArgumentException(
                    "Group messages cannot contain null.",
                    nameof(drafts)));
            if (!ids.Add(snapshot.MessageId))
            {
                throw new ArgumentException(
                    "One append cannot contain duplicate message IDs.",
                    nameof(drafts));
            }

            var bytes = JsonValueInspector.ValidateAndMeasure(
                snapshot.Payload,
                _payloadLimits,
                nameof(drafts));
            result.Add(
                new PreparedDraft(
                    snapshot,
                    ComputeAdmittedJsonDigest(snapshot.Payload),
                    bytes));
        }

        return new ReadOnlyCollection<PreparedDraft>(result.ToArray());
    }

    private IReadOnlyList<GameEntityIdentity> ResolveAudience(
        GroupInteractionSession current,
        GroupInteractionMessageDraft draft)
    {
        if (string.Equals(
                draft.AudienceMode,
                GroupInteractionAudienceModes.AllMembers,
                StringComparison.Ordinal))
        {
            if (draft.Audience.Count != 0)
            {
                throw new ArgumentException(
                    "all_members messages cannot also declare an audience.",
                    nameof(draft));
            }

            return new ReadOnlyCollection<GameEntityIdentity>(
                current.Members
                    .Select(
                        item => GroupInteractionValidation.CloneIdentity(
                            item.Actor))
                    .ToArray());
        }

        if (draft.Audience.Count == 0)
        {
            throw new ArgumentException(
                "An explicit group audience cannot be empty.",
                nameof(draft));
        }

        if (draft.Audience.Count > _limits.MaxMembers)
        {
            throw new RuntimeContentLimitException(
                nameof(draft),
                "group_interaction_audience_limit_exceeded",
                "A group audience exceeds the configured member limit.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GameEntityIdentity>(draft.Audience.Count);
        foreach (var identity in draft.Audience)
        {
            if (!ContainsIdentity(current.Members, identity))
            {
                throw new ArgumentException(
                    "Every explicit audience member must be an exact current member.",
                    nameof(draft));
            }

            var key = GroupInteractionValidation.IdentityKey(identity);
            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    "A group audience cannot contain duplicates.",
                    nameof(draft));
            }

            result.Add(
                GroupInteractionValidation.CloneIdentity(identity));
        }

        result.Sort(GroupInteractionValidation.CompareIdentities);
        return new ReadOnlyCollection<GameEntityIdentity>(result.ToArray());
    }

    private static bool ContainsIdentity(
        IReadOnlyList<GroupInteractionMember> members,
        GameEntityIdentity identity)
    {
        return members.Any(
            item => item.Actor.IsSameIncarnation(identity));
    }

    private static GroupInteractionMembershipSnapshot
        MembershipAtOperationRevision(
            IReadOnlyList<GroupInteractionMembershipSnapshot> history,
            long operationRevision)
    {
        GroupInteractionMembershipSnapshot? result = null;
        foreach (var snapshot in history)
        {
            if (snapshot.AppliedRevision >= operationRevision)
            {
                break;
            }

            result = snapshot;
        }

        return result
               ?? throw new ArgumentException(
                   "No membership snapshot precedes an operation revision.",
                   nameof(history));
    }

    private static bool AudienceMatchesMembers(
        IReadOnlyList<GameEntityIdentity> audience,
        IReadOnlyList<GroupInteractionMember> members)
    {
        if (audience.Count != members.Count)
        {
            return false;
        }

        var keys = new HashSet<string>(
            audience.Select(GroupInteractionValidation.IdentityKey),
            StringComparer.Ordinal);
        return members.All(
            item => keys.Contains(
                GroupInteractionValidation.IdentityKey(item.Actor)));
    }

    private static bool MembersEqual(
        IReadOnlyList<GroupInteractionMember> left,
        IReadOnlyList<GroupInteractionMember> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].Actor.IsSameIncarnation(
                    right[index].Actor)
                || !left[index].Roles.SequenceEqual(
                    right[index].Roles,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateOperationEvidence(
        string sessionId,
        string groupId,
        JsonElement sharedScope,
        string sharedScopeDigest,
        string status,
        long revision,
        IReadOnlyList<GroupInteractionMembershipSnapshot>
            membershipHistory,
        IReadOnlyList<GroupInteractionMessage> messages,
        IReadOnlyList<GroupInteractionOperationRecord> operations,
        GroupInteractionWorldBinding? worldBinding)
    {
        var create = operations[0];
        if (!string.Equals(
                create.Kind,
                GroupInteractionOperationKinds.Create,
                StringComparison.Ordinal))
        {
            throw InvalidOperationEvidence();
        }

        var createRequest = worldBinding is null
            ? new GroupInteractionCreateRequest(
                create.OperationId,
                sessionId,
                groupId,
                sharedScope,
                membershipHistory[0].Members)
            : new GroupInteractionCreateRequest(
                create.OperationId,
                sessionId,
                groupId,
                sharedScope,
                membershipHistory[0].Members,
                worldBinding);
        RequireOperationDigest(
            create,
            DigestCreate(
                createRequest,
                sharedScopeDigest,
                membershipHistory[0].Members));

        var membershipByAppliedRevision = membershipHistory
            .Skip(1)
            .ToDictionary(
                item => item.AppliedRevision,
                item => item);
        var messagesByAppliedRevision = messages
            .GroupBy(item => item.AppliedRevision)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Sequence)
                    .ToArray());
        var sawClose = false;
        for (var operationRevision = 1L;
             operationRevision <= revision;
             operationRevision++)
        {
            var operation = operations[checked((int)operationRevision)];
            membershipByAppliedRevision.TryGetValue(
                operationRevision,
                out var membership);
            messagesByAppliedRevision.TryGetValue(
                operationRevision,
                out var appendedMessages);
            var activeMembership = MembershipAtOperationRevision(
                membershipHistory,
                operationRevision);

            string expectedDigest;
            if (string.Equals(
                    operation.Kind,
                    GroupInteractionOperationKinds.ReplaceMembers,
                    StringComparison.Ordinal))
            {
                if (membership is null
                    || appendedMessages is not null
                    || sawClose
                    || membership.MembershipRevision
                    != activeMembership.MembershipRevision + 1)
                {
                    throw InvalidOperationEvidence();
                }

                var request = new GroupInteractionMembershipRequest(
                    operation.OperationId,
                    sessionId,
                    operationRevision - 1,
                    activeMembership.MembershipRevision,
                    membership.Members);
                expectedDigest = DigestMembership(
                    request,
                    membership.Members);
            }
            else if (string.Equals(
                         operation.Kind,
                         GroupInteractionOperationKinds.AppendMessages,
                         StringComparison.Ordinal))
            {
                if (membership is not null
                    || appendedMessages is null
                    || appendedMessages.Length == 0
                    || sawClose
                    || appendedMessages.Any(
                        item => item.MembershipRevision
                                != activeMembership
                                    .MembershipRevision))
                {
                    throw InvalidOperationEvidence();
                }

                var drafts = appendedMessages.Select(
                        item => new GroupInteractionMessageDraft(
                            item.MessageId,
                            item.Kind,
                            item.Payload,
                            item.AudienceMode,
                            item.Author,
                            string.Equals(
                                item.AudienceMode,
                                GroupInteractionAudienceModes.Explicit,
                                StringComparison.Ordinal)
                                ? item.Audience
                                : Array.Empty<GameEntityIdentity>(),
                            item.CausationId))
                    .ToArray();
                var request = new GroupInteractionAppendRequest(
                    operation.OperationId,
                    sessionId,
                    operationRevision - 1,
                    activeMembership.MembershipRevision,
                    drafts);
                expectedDigest = DigestAppend(
                    request,
                    PrepareDrafts(request.Messages));
            }
            else if (string.Equals(
                         operation.Kind,
                         GroupInteractionOperationKinds.Close,
                         StringComparison.Ordinal))
            {
                if (membership is not null
                    || appendedMessages is not null
                    || sawClose
                    || !string.Equals(
                        status,
                        GroupInteractionStatuses.Closed,
                        StringComparison.Ordinal)
                    || operationRevision != revision)
                {
                    throw InvalidOperationEvidence();
                }

                var request = new GroupInteractionCloseRequest(
                    operation.OperationId,
                    sessionId,
                    operationRevision - 1,
                    activeMembership.MembershipRevision);
                expectedDigest = DigestClose(request);
                sawClose = true;
            }
            else
            {
                throw InvalidOperationEvidence();
            }

            RequireOperationDigest(operation, expectedDigest);
        }

        if (membershipByAppliedRevision.Count
                != membershipHistory.Count - 1
            || messagesByAppliedRevision.Keys.Any(
                key => key < 1 || key > revision)
            || sawClose
                != string.Equals(
                    status,
                    GroupInteractionStatuses.Closed,
                    StringComparison.Ordinal))
        {
            throw InvalidOperationEvidence();
        }
    }

    private static void RequireOperationDigest(
        GroupInteractionOperationRecord operation,
        string expectedDigest)
    {
        if (!string.Equals(
                operation.RequestDigest,
                expectedDigest,
                StringComparison.Ordinal))
        {
            throw InvalidOperationEvidence();
        }
    }

    private static ArgumentException InvalidOperationEvidence()
    {
        return new ArgumentException(
            "Restored operation history does not match committed state evidence.",
            "operations");
    }

    private static GroupInteractionWriteResult? CheckWritable(
        GroupInteractionSession current,
        long expectedRevision,
        long expectedMembershipRevision)
    {
        if (!string.Equals(
                current.Status,
                GroupInteractionStatuses.Open,
                StringComparison.Ordinal))
        {
            return new GroupInteractionWriteResult(
                GroupInteractionWriteStatuses.SessionClosed,
                current);
        }

        if (current.Revision != expectedRevision)
        {
            return new GroupInteractionWriteResult(
                GroupInteractionWriteStatuses.RevisionConflict,
                current);
        }

        if (current.MembershipRevision != expectedMembershipRevision)
        {
            return new GroupInteractionWriteResult(
                GroupInteractionWriteStatuses.MembershipRevisionConflict,
                current);
        }

        return null;
    }

    private static GroupInteractionWriteResult? Replay(
        GroupInteractionSession current,
        string operationId,
        string requestDigest)
    {
        var existing = current.Operations.FirstOrDefault(
            item => string.Equals(
                item.OperationId,
                operationId,
                StringComparison.Ordinal));
        if (existing is null)
        {
            return null;
        }

        return string.Equals(
            existing.RequestDigest,
            requestDigest,
            StringComparison.Ordinal)
            ? new GroupInteractionWriteResult(
                GroupInteractionWriteStatuses.Idempotent,
                current,
                existing.AppliedRevision)
            : new GroupInteractionWriteResult(
                GroupInteractionWriteStatuses.OperationConflict,
                current);
    }

    private static IReadOnlyList<GroupInteractionOperationRecord>
        AppendOperation(
            GroupInteractionSession current,
            string operationId,
            string kind,
            string digest,
            long appliedRevision)
    {
        return new ReadOnlyCollection<GroupInteractionOperationRecord>(
            current.Operations
                .Concat(
                    new[]
                    {
                        new GroupInteractionOperationRecord(
                            operationId,
                            kind,
                            digest,
                            appliedRevision)
                    })
                .ToArray());
    }

    private static GroupInteractionSession CopySession(
        GroupInteractionSession current,
        string? status = null,
        long? revision = null,
        long? membershipRevision = null,
        IReadOnlyList<GroupInteractionMember>? members = null,
        IReadOnlyList<GroupInteractionMembershipSnapshot>?
            membershipHistory = null,
        IReadOnlyList<GroupInteractionMessage>? messages = null,
        IReadOnlyList<GroupInteractionOperationRecord>? operations = null,
        int? totalPayloadUtf8Bytes = null)
    {
        return new GroupInteractionSession(
            current.SessionId,
            current.GroupId,
            current.SharedScope,
            current.SharedScopeDigest,
            status ?? current.Status,
            revision ?? current.Revision,
            membershipRevision ?? current.MembershipRevision,
            members ?? current.Members,
            membershipHistory ?? current.MembershipHistory,
            messages ?? current.Messages,
            operations ?? current.Operations,
            totalPayloadUtf8Bytes ?? current.TotalPayloadUtf8Bytes,
            current.WorldBinding);
    }

    private static GroupInteractionWriteResult Applied(
        GroupInteractionSession session,
        long revision)
    {
        return new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.Applied,
            session,
            revision);
    }

    private static GroupInteractionWriteResult Capacity(
        GroupInteractionSession current)
    {
        return new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.CapacityExceeded,
            current);
    }

    private static void EnsureSession(
        GroupInteractionSession current,
        string requestedSessionId)
    {
        if (!string.Equals(
                current.SessionId,
                requestedSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The request targets a different group interaction session.",
                nameof(requestedSessionId));
        }
    }

    private static string DigestCreate(
        GroupInteractionCreateRequest request,
        string scopeDigest,
        IReadOnlyList<GroupInteractionMember> members)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", "group.create");
        digest.Add("sessionId", request.SessionId);
        digest.Add("groupId", request.GroupId);
        digest.Add("sharedScopeDigest", scopeDigest);
        if (request.WorldBinding is not null)
        {
            digest.Add("worldId", request.WorldBinding.WorldId);
            digest.Add(
                "timelineId",
                request.WorldBinding.TimelineId);
            digest.Add(
                "timelineEpoch",
                request.WorldBinding.TimelineEpoch);
            digest.Add(
                "saveRevision",
                request.WorldBinding.SaveRevision);
        }
        AddMembers(digest, members);
        return digest.Finish();
    }

    private static string DigestMembership(
        GroupInteractionMembershipRequest request,
        IReadOnlyList<GroupInteractionMember> members)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", "group.members.replace");
        digest.Add("sessionId", request.SessionId);
        digest.Add("expectedRevision", request.ExpectedRevision);
        digest.Add(
            "expectedMembershipRevision",
            request.ExpectedMembershipRevision);
        AddMembers(digest, members);
        return digest.Finish();
    }

    private static string DigestAppend(
        GroupInteractionAppendRequest request,
        IReadOnlyList<PreparedDraft> drafts)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", "group.messages.append");
        digest.Add("sessionId", request.SessionId);
        digest.Add("expectedRevision", request.ExpectedRevision);
        digest.Add(
            "expectedMembershipRevision",
            request.ExpectedMembershipRevision);
        digest.Add("count", drafts.Count);
        for (var index = 0; index < drafts.Count; index++)
        {
            var item = drafts[index];
            var prefix = "message." + index.ToString(
                CultureInfo.InvariantCulture);
            digest.Add(prefix + ".id", item.Draft.MessageId);
            digest.Add(prefix + ".kind", item.Draft.Kind);
            digest.Add(prefix + ".payload", item.PayloadDigest);
            digest.Add(
                prefix + ".author",
                item.Draft.Author is null
                    ? null
                    : GroupInteractionValidation.IdentityKey(
                        item.Draft.Author));
            digest.Add(
                prefix + ".audienceMode",
                item.Draft.AudienceMode);
            digest.Add(
                prefix + ".audience",
                item.Draft.Audience
                    .Select(GroupInteractionValidation.IdentityKey)
                    .OrderBy(value => value, StringComparer.Ordinal));
            digest.Add(prefix + ".causationId", item.Draft.CausationId);
        }

        return digest.Finish();
    }

    private static string DigestClose(
        GroupInteractionCloseRequest request)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("kind", "group.close");
        digest.Add("sessionId", request.SessionId);
        digest.Add("expectedRevision", request.ExpectedRevision);
        digest.Add(
            "expectedMembershipRevision",
            request.ExpectedMembershipRevision);
        return digest.Finish();
    }

    private static void AddMembers(
        CanonicalDigestBuilder digest,
        IReadOnlyList<GroupInteractionMember> members)
    {
        digest.Add("memberCount", members.Count);
        for (var index = 0; index < members.Count; index++)
        {
            var prefix = "member." + index.ToString(
                CultureInfo.InvariantCulture);
            digest.Add(
                prefix + ".identity",
                GroupInteractionValidation.IdentityKey(
                    members[index].Actor));
            digest.Add(prefix + ".roles", members[index].Roles);
        }
    }

    private static string ComputeAdmittedJsonDigest(JsonElement value)
    {
        var canonical = new StringBuilder();
        CanonicalJsonDigest.AppendCanonical(canonical, value);
        using var sha = SHA256.Create();
        var bytes = StrictUtf8Encoding.GetBytes(canonical.ToString());
        var hash = sha.ComputeHash(bytes);
        var result = new StringBuilder(hash.Length * 2);
        foreach (var item in hash)
        {
            result.Append(
                item.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private sealed class PreparedDraft
    {
        public PreparedDraft(
            GroupInteractionMessageDraft draft,
            string payloadDigest,
            int payloadUtf8Bytes)
        {
            Draft = draft;
            PayloadDigest = payloadDigest;
            PayloadUtf8Bytes = payloadUtf8Bytes;
        }

        public GroupInteractionMessageDraft Draft { get; }

        public string PayloadDigest { get; }

        public int PayloadUtf8Bytes { get; }
    }
}

/// <summary>
/// Atomic reference store for embedded and test hosts. Production hosts may
/// implement the same interface with a durable transaction, or store the
/// immutable session inside their authoritative world state.
/// </summary>
public interface IGroupInteractionStore
{
    ValueTask<GroupInteractionSession?> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<GroupInteractionWriteResult> CreateAsync(
        GroupInteractionCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GroupInteractionWriteResult> ReplaceMembersAsync(
        GroupInteractionMembershipRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GroupInteractionWriteResult> AppendAsync(
        GroupInteractionAppendRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GroupInteractionWriteResult> CloseAsync(
        GroupInteractionCloseRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GroupInteractionProjection?> ProjectAsync(
        string sessionId,
        GameEntityIdentity viewer,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryGroupInteractionStore : IGroupInteractionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GroupInteractionSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly GroupInteractionStateMachine _stateMachine;

    public InMemoryGroupInteractionStore(
        GroupInteractionLimits? limits = null)
    {
        _stateMachine = new GroupInteractionStateMachine(limits);
    }

    public ValueTask<GroupInteractionSession?> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var id = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new ValueTask<GroupInteractionSession?>(
                _sessions.TryGetValue(id, out var session)
                    ? session
                    : null);
        }
    }

    public ValueTask<GroupInteractionWriteResult> CreateAsync(
        GroupInteractionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_sessions.TryGetValue(
                    request.SessionId,
                    out var current))
            {
                var candidate = _stateMachine.Create(request);
                var replay = current.Operations.FirstOrDefault(
                    item => string.Equals(
                        item.OperationId,
                        request.OperationId,
                        StringComparison.Ordinal));
                var status = replay is null
                    ? GroupInteractionWriteStatuses.SessionAlreadyExists
                    : string.Equals(
                        replay.RequestDigest,
                        candidate.Session!.Operations[0].RequestDigest,
                        StringComparison.Ordinal)
                        ? GroupInteractionWriteStatuses.Idempotent
                        : GroupInteractionWriteStatuses.OperationConflict;
                return new ValueTask<GroupInteractionWriteResult>(
                    new GroupInteractionWriteResult(
                        status,
                        current,
                        string.Equals(
                            status,
                            GroupInteractionWriteStatuses.Idempotent,
                            StringComparison.Ordinal)
                            ? replay?.AppliedRevision
                            : null));
            }

            var result = _stateMachine.Create(request);
            _sessions.Add(request.SessionId, result.Session!);
            return new ValueTask<GroupInteractionWriteResult>(result);
        }
    }

    public ValueTask<GroupInteractionWriteResult> ReplaceMembersAsync(
        GroupInteractionMembershipRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.ReplaceMembers(session, request),
            cancellationToken);
    }

    public ValueTask<GroupInteractionWriteResult> AppendAsync(
        GroupInteractionAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.Append(session, request),
            cancellationToken);
    }

    public ValueTask<GroupInteractionWriteResult> CloseAsync(
        GroupInteractionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MutateAsync(
            request.SessionId,
            session => _stateMachine.Close(session, request),
            cancellationToken);
    }

    public ValueTask<GroupInteractionProjection?> ProjectAsync(
        string sessionId,
        GameEntityIdentity viewer,
        CancellationToken cancellationToken = default)
    {
        var id = RuntimeGuard.RequiredId(sessionId, nameof(sessionId));
        if (viewer is null)
        {
            throw new ArgumentNullException(nameof(viewer));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new ValueTask<GroupInteractionProjection?>(
                _sessions.TryGetValue(id, out var session)
                    ? _stateMachine.Project(session, viewer)
                    : null);
        }
    }

    private ValueTask<GroupInteractionWriteResult> MutateAsync(
        string sessionId,
        Func<GroupInteractionSession, GroupInteractionWriteResult> transition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var current))
            {
                return new ValueTask<GroupInteractionWriteResult>(
                    new GroupInteractionWriteResult(
                        GroupInteractionWriteStatuses.NotFound,
                        session: null));
            }

            var result = transition(current);
            if (string.Equals(
                    result.Status,
                    GroupInteractionWriteStatuses.Applied,
                    StringComparison.Ordinal))
            {
                _sessions[sessionId] = result.Session!;
            }

            return new ValueTask<GroupInteractionWriteResult>(result);
        }
    }
}

internal static class GroupInteractionValidation
{
    public static long NonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static string RequiredAudienceMode(
        string value,
        string parameterName)
    {
        var result = RuntimeGuard.RequiredUtf8(
            value,
            32,
            parameterName);
        if (!string.Equals(
                result,
                GroupInteractionAudienceModes.AllMembers,
                StringComparison.Ordinal)
            && !string.Equals(
                result,
                GroupInteractionAudienceModes.Explicit,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Unknown group interaction audience mode.",
                parameterName);
        }

        return result;
    }

    public static string RequiredOperationKind(
        string value,
        string parameterName)
    {
        var result = RuntimeGuard.RequiredUtf8(
            value,
            32,
            parameterName);
        if (!string.Equals(
                result,
                GroupInteractionOperationKinds.Create,
                StringComparison.Ordinal)
            && !string.Equals(
                result,
                GroupInteractionOperationKinds.ReplaceMembers,
                StringComparison.Ordinal)
            && !string.Equals(
                result,
                GroupInteractionOperationKinds.AppendMessages,
                StringComparison.Ordinal)
            && !string.Equals(
                result,
                GroupInteractionOperationKinds.Close,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Unknown group interaction operation kind.",
                parameterName);
        }

        return result;
    }

    public static string RequiredWriteStatus(
        string value,
        string parameterName)
    {
        var result = RuntimeGuard.RequiredUtf8(
            value,
            64,
            parameterName);
        if (!new[]
            {
                GroupInteractionWriteStatuses.Applied,
                GroupInteractionWriteStatuses.Idempotent,
                GroupInteractionWriteStatuses.NotFound,
                GroupInteractionWriteStatuses.SessionAlreadyExists,
                GroupInteractionWriteStatuses.RevisionConflict,
                GroupInteractionWriteStatuses.MembershipRevisionConflict,
                GroupInteractionWriteStatuses.OperationConflict,
                GroupInteractionWriteStatuses.SessionClosed,
                GroupInteractionWriteStatuses.WorldBindingMismatch,
                GroupInteractionWriteStatuses.CapacityExceeded
            }.Contains(result, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Unknown group interaction write status.",
                parameterName);
        }

        return result;
    }

    public static GameEntityIdentity CloneIdentity(
        GameEntityIdentity identity)
    {
        return new GameEntityIdentity(
            identity.EntityId,
            identity.Incarnation);
    }

    public static GroupInteractionWorldBinding? CloneWorldBinding(
        GroupInteractionWorldBinding? binding)
    {
        return binding is null
            ? null
            : new GroupInteractionWorldBinding(
                binding.WorldId,
                binding.TimelineId,
                binding.TimelineEpoch,
                binding.SaveRevision);
    }

    public static GroupInteractionMember CloneMember(
        GroupInteractionMember member)
    {
        return new GroupInteractionMember(member.Actor, member.Roles);
    }

    public static GroupInteractionMembershipSnapshot
        CloneMembershipSnapshot(
            GroupInteractionMembershipSnapshot snapshot)
    {
        return new GroupInteractionMembershipSnapshot(
            snapshot.MembershipRevision,
            snapshot.AppliedRevision,
            snapshot.Members);
    }

    public static GroupInteractionMessageDraft CloneDraft(
        GroupInteractionMessageDraft draft)
    {
        return new GroupInteractionMessageDraft(
            draft.MessageId,
            draft.Kind,
            draft.Payload,
            draft.AudienceMode,
            draft.Author,
            draft.Audience,
            draft.CausationId);
    }

    public static GroupInteractionMessage CloneMessage(
        GroupInteractionMessage message)
    {
        return new GroupInteractionMessage(
            message.Sequence,
            message.MessageId,
            message.Kind,
            message.Payload,
            message.PayloadDigest,
            message.PayloadUtf8Bytes,
            message.AudienceMode,
            message.Author,
            message.Audience,
            message.MembershipRevision,
            message.AppliedRevision,
            message.CausationId);
    }

    public static string IdentityKey(GameEntityIdentity identity)
    {
        return identity.EntityId
               + "\0"
               + identity.Incarnation.ToString(
                   CultureInfo.InvariantCulture);
    }

    public static int CompareIdentities(
        GameEntityIdentity left,
        GameEntityIdentity right)
    {
        var byId = string.Compare(
            left.EntityId,
            right.EntityId,
            StringComparison.Ordinal);
        return byId != 0
            ? byId
            : left.Incarnation.CompareTo(right.Incarnation);
    }

    public static int CompareMembers(
        GroupInteractionMember left,
        GroupInteractionMember right)
    {
        return CompareIdentities(left.Actor, right.Actor);
    }
}
