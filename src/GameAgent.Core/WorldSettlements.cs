using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public static class WorldSettlementPrivacyClasses
{
    public const string Private = "private";
}

public static class WorldSettlementReasonCodes
{
    public const string Applied = "world_settlement_applied";
    public const string DispatchIntentCommitted =
        "world_settlement_dispatch_intent_committed";
    public const string AuthorityDenied =
        "world_settlement_authority_denied";
    public const string SinkNotConfigured =
        "world_settlement_sink_not_configured";
    public const string SinkRejected = "world_settlement_sink_rejected";
    public const string StoreConflict = "world_settlement_store_conflict";
    public const string EvidenceMissing =
        "world_settlement_evidence_missing";
    public const string EvidenceMismatch =
        "world_settlement_evidence_mismatch";
    public const string PlanConflict = "world_settlement_plan_conflict";
    public const string TransitionLimitExceeded =
        "world_settlement_transition_limit_exceeded";
}

public enum WorldSettlementSinkKind
{
    Memory = 0,
    Group = 1,
    Presentation = 2
}

public enum WorldSettlementStage
{
    Pending = 0,
    Applied = 1,
    Rejected = 2,
    Reconciliation = 3
}

public enum WorldSettlementBeginStatus
{
    Created = 0,
    Existing = 1,
    Conflict = 2,
    CapacityExceeded = 3
}

public enum WorldSettlementTransitionStatus
{
    Applied = 0,
    Conflict = 1,
    NotFound = 2
}

/// <summary>
/// Resource limits for one caller-authored settlement plan.
/// </summary>
public sealed class WorldSettlementLimits
{
    public WorldSettlementLimits(
        int maxDeliveries = 256,
        int maxAudienceMembers = 512,
        int maxAggregateUtf8Bytes = 32 * 1_048_576,
        int maxAggregateJsonNodes = 500_000)
    {
        if (maxDeliveries is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDeliveries));
        }

        if (maxAudienceMembers is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAudienceMembers));
        }

        if (maxAggregateUtf8Bytes is < 1_024
            or > 32 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAggregateUtf8Bytes));
        }

        if (maxAggregateJsonNodes is < 128 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAggregateJsonNodes));
        }

        MaxDeliveries = maxDeliveries;
        MaxAudienceMembers = maxAudienceMembers;
        MaxAggregateUtf8Bytes = maxAggregateUtf8Bytes;
        MaxAggregateJsonNodes = maxAggregateJsonNodes;
    }

    public int MaxDeliveries { get; }

    public int MaxAudienceMembers { get; }

    public int MaxAggregateUtf8Bytes { get; }

    public int MaxAggregateJsonNodes { get; }
}

/// <summary>
/// Exact audience lifetime claimed by one outbox delivery. The host-owned
/// authority guard decides whether the claim is still current and allowed.
/// </summary>
public sealed class WorldSettlementAudienceClaim
{
    public WorldSettlementAudienceClaim(
        string membershipScopeId,
        long membershipRevision,
        IEnumerable<GameEntityIdentity> members,
        string privacyClass,
        string redactionClass,
        int maxMembers = 512)
    {
        MembershipScopeId = RuntimeGuard.RequiredUtf8(
            membershipScopeId,
            128,
            nameof(membershipScopeId));
        if (membershipRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(membershipRevision));
        }

        if (maxMembers is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMembers));
        }

        MembershipRevision = membershipRevision;
        Members = WorldPresentationValidation.CopyIdentities(
            members,
            maxMembers,
            nameof(members));
        if (Members.Count == 0)
        {
            throw new ArgumentException(
                "A settlement audience requires at least one exact entity "
                + "incarnation.",
                nameof(members));
        }

        PrivacyClass = RuntimeGuard.RequiredUtf8(
            privacyClass,
            128,
            nameof(privacyClass));
        RedactionClass = RuntimeGuard.RequiredUtf8(
            redactionClass,
            128,
            nameof(redactionClass));
        SemanticDigest = WorldPresentationValidation.ComputeSemanticDigest(
            ToJson());
    }

    public string MembershipScopeId { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<GameEntityIdentity> Members { get; }

    public string PrivacyClass { get; }

    public string RedactionClass { get; }

    public string SemanticDigest { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("membershipScopeId", JsonArrayBuilder.String(
                MembershipScopeId)),
            ("membershipRevision", JsonArrayBuilder.Number(
                MembershipRevision)),
            ("members", JsonArrayBuilder.Array(
                Members.Select(WorldSettlementValidation.IdentityToJson))),
            ("privacyClass", JsonArrayBuilder.String(PrivacyClass)),
            ("redactionClass", JsonArrayBuilder.String(RedactionClass)));
    }
}

public abstract class WorldSettlementDelivery
{
    protected WorldSettlementDelivery(
        string operationId,
        WorldSettlementSinkKind kind,
        WorldSettlementAudienceClaim audience)
    {
        if (!Enum.IsDefined(typeof(WorldSettlementSinkKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        Kind = kind;
        Audience = WorldSettlementValidation.CloneAudience(
            audience ?? throw new ArgumentNullException(nameof(audience)));
    }

    public string OperationId { get; }

    public WorldSettlementSinkKind Kind { get; }

    public WorldSettlementAudienceClaim Audience { get; }

    /// <summary>
    /// Deterministic semantic identity for exact claim comparison. This is
    /// not an authenticity proof, signature, or authorization decision.
    /// </summary>
    public string SemanticDigest =>
        WorldPresentationValidation.ComputeSemanticDigest(ToJson());

    internal abstract JsonElement ToJson();
}

/// <summary>
/// One private, atomic and idempotent memory delivery. Upserts must carry
/// committed provenance for the exact private observer.
/// </summary>
public sealed class WorldSettlementMemoryDelivery
    : WorldSettlementDelivery
{
    public WorldSettlementMemoryDelivery(
        string operationId,
        WorldSettlementAudienceClaim audience,
        IReadOnlyList<MemoryMutation> mutations)
        : base(operationId, WorldSettlementSinkKind.Memory, audience)
    {
        if (!string.Equals(
                Audience.PrivacyClass,
                WorldSettlementPrivacyClasses.Private,
                StringComparison.Ordinal)
            || Audience.Members.Count != 1)
        {
            throw new ArgumentException(
                "A memory settlement delivery must identify exactly one "
                + "private entity incarnation.",
                nameof(audience));
        }

        Mutations = new ReadOnlyCollection<MemoryMutation>(
            MemoryBatchValidator.Snapshot(mutations, default)
                .Select(WorldSettlementValidation.CloneMutation)
                .ToArray());
        if (Mutations.Any(
                item => item.Kind == MemoryMutationKind.Delete))
        {
            throw new ArgumentException(
                "Settlement memory delivery does not accept unscoped "
                + "delete-by-ID mutations. Use a host-owned memory "
                + "lifecycle with an ownership-aware compare-and-swap "
                + "contract.",
                nameof(mutations));
        }
    }

    public IReadOnlyList<MemoryMutation> Mutations { get; }

    internal override JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("kind", JsonArrayBuilder.String("memory")),
            ("operationId", JsonArrayBuilder.String(OperationId)),
            ("audience", Audience.ToJson()),
            ("mutations", JsonArrayBuilder.Array(
                Mutations.Select(WorldSettlementValidation.MutationToJson))));
    }
}

/// <summary>
/// One append to an existing group session. The expected member set freezes
/// exact entity incarnations and host-defined roles at the claimed revision.
/// </summary>
public sealed class WorldSettlementGroupDelivery
    : WorldSettlementDelivery
{
    public WorldSettlementGroupDelivery(
        string operationId,
        string expectedGroupId,
        IEnumerable<GroupInteractionMember> expectedMembers,
        GroupInteractionAppendRequest request,
        string privacyClass = "group",
        string redactionClass = "none")
        : this(
            operationId,
            expectedGroupId,
            request,
            WorldSettlementValidation.SnapshotGroupDelivery(
                expectedMembers,
                request,
                privacyClass,
                redactionClass))
    {
    }

    private WorldSettlementGroupDelivery(
        string operationId,
        string expectedGroupId,
        GroupInteractionAppendRequest request,
        WorldSettlementValidation.GroupDeliverySnapshot snapshot)
        : this(
            operationId,
            expectedGroupId,
            snapshot.Members,
            request,
            snapshot.Audience)
    {
    }

    internal WorldSettlementGroupDelivery(
        string operationId,
        string expectedGroupId,
        IEnumerable<GroupInteractionMember> expectedMembers,
        GroupInteractionAppendRequest request,
        WorldSettlementAudienceClaim audience)
        : base(operationId, WorldSettlementSinkKind.Group, audience)
    {
        ExpectedGroupId = RuntimeGuard.RequiredId(
            expectedGroupId,
            nameof(expectedGroupId));
        Request = WorldSettlementValidation.CloneGroupRequest(
            request ?? throw new ArgumentNullException(nameof(request)));
        if (!string.Equals(
                operationId,
                Request.OperationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The settlement and group operation identities must match.",
                nameof(operationId));
        }

        ExpectedMembers = new ReadOnlyCollection<GroupInteractionMember>(
            WorldSettlementValidation.CopyGroupMembers(
                expectedMembers,
                nameof(expectedMembers)));
        if (ExpectedMembers.Count == 0
            || !string.Equals(
                Audience.MembershipScopeId,
                Request.SessionId,
                StringComparison.Ordinal)
            || Audience.MembershipRevision
            != Request.ExpectedMembershipRevision
            || !WorldSettlementValidation.SameIdentities(
                Audience.Members,
                ExpectedMembers.Select(item => item.Actor)))
        {
            throw new ArgumentException(
                "The group audience must exactly match the expected "
                + "session membership.",
                nameof(audience));
        }
    }

    public string ExpectedGroupId { get; }

    public IReadOnlyList<GroupInteractionMember> ExpectedMembers { get; }

    public GroupInteractionAppendRequest Request { get; }

    internal override JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("kind", JsonArrayBuilder.String("group")),
            ("operationId", JsonArrayBuilder.String(OperationId)),
            ("audience", Audience.ToJson()),
            ("expectedGroupId", JsonArrayBuilder.String(ExpectedGroupId)),
            ("expectedMembers", JsonArrayBuilder.Array(
                ExpectedMembers.Select(
                    WorldSettlementValidation.GroupMemberToJson))),
            ("request", WorldSettlementValidation.GroupRequestToJson(
                Request)));
    }
}

public sealed class WorldSettlementPresentationDelivery
    : WorldSettlementDelivery
{
    public WorldSettlementPresentationDelivery(
        string operationId,
        WorldPresentationDraft draft,
        long expectedPreviousContentRevision)
        : base(
            operationId,
            WorldSettlementSinkKind.Presentation,
            WorldSettlementValidation.AudienceFromPresentation(
                draft
                ?? throw new ArgumentNullException(nameof(draft))))
    {
        if (expectedPreviousContentRevision < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedPreviousContentRevision));
        }

        Draft = WorldSettlementValidation.ClonePresentationDraft(draft);
        ExpectedPreviousContentRevision =
            expectedPreviousContentRevision;
    }

    public WorldPresentationDraft Draft { get; }

    public long ExpectedPreviousContentRevision { get; }

    internal override JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("kind", JsonArrayBuilder.String("presentation")),
            ("operationId", JsonArrayBuilder.String(OperationId)),
            ("audience", Audience.ToJson()),
            ("expectedPreviousContentRevision", JsonArrayBuilder.Number(
                ExpectedPreviousContentRevision)),
            ("draft", WorldSettlementValidation.PresentationToJson(
                Draft)));
    }
}

/// <summary>
/// Immutable caller-authored outbox payload. It contains no business-rule
/// inference: each delivery is explicit and independently audience-bound.
/// </summary>
public sealed class WorldSettlementPlan
{
    public WorldSettlementPlan(
        string settlementId,
        CommittedWorldPresentationEvidence evidence,
        IEnumerable<WorldSettlementDelivery> deliveries,
        WorldSettlementLimits? limits = null)
    {
        SettlementId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        Evidence = WorldSettlementValidation.CloneEvidence(
            evidence ?? throw new ArgumentNullException(nameof(evidence)));
        Source = WorldPresentationValidation.CloneSource(Evidence.Source);
        Binding = WorldPresentationValidation.CloneBinding(Evidence.Binding);
        EvidenceDigest = Evidence.SemanticDigest;
        var admittedLimits = limits ?? new WorldSettlementLimits();
        var copied = RuntimeInputGuard.CopyBounded(
            deliveries
            ?? throw new ArgumentNullException(nameof(deliveries)),
            admittedLimits.MaxDeliveries,
            WorldSettlementValidation.CloneDelivery,
            nameof(deliveries),
            "world_settlement_delivery_count_exceeded");
        if (copied.Length == 0)
        {
            throw new ArgumentException(
                "A settlement plan requires at least one delivery.",
                nameof(deliveries));
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var delivery in copied)
        {
            if (!operationIds.Add(delivery.OperationId))
            {
                throw new ArgumentException(
                    "Settlement operation identities must be unique.",
                    nameof(deliveries));
            }

            WorldSettlementValidation.ValidateDeliveryBinding(
                delivery,
                Source,
                Binding);
            if (delivery.Audience.Members.Count
                > admittedLimits.MaxAudienceMembers)
            {
                throw new RuntimeContentLimitException(
                    nameof(deliveries),
                    "world_settlement_audience_count_exceeded",
                    "A settlement audience exceeds the configured limit.");
            }
        }

        Deliveries = new ReadOnlyCollection<WorldSettlementDelivery>(
            copied);
        var json = ToJson();
        JsonValueInspector.ValidateAndMeasure(
            json,
            new JsonValueLimits(
                admittedLimits.MaxAggregateUtf8Bytes,
                maxDepth: 64,
                admittedLimits.MaxAggregateJsonNodes,
                maxStringUtf8Bytes: 4 * 1_048_576,
                maxContainerItems: 65_536),
            nameof(deliveries));
        SemanticDigest =
            WorldPresentationValidation.ComputeSemanticDigest(json);
    }

    public string SettlementId { get; }

    public WorldPresentationSource Source { get; }

    public WorldPresentationBinding Binding { get; }

    public CommittedWorldPresentationEvidence Evidence { get; }

    public string EvidenceDigest { get; }

    public IReadOnlyList<WorldSettlementDelivery> Deliveries { get; }

    public string SemanticDigest { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("settlementId", JsonArrayBuilder.String(SettlementId)),
            ("source", Source.ToJson()),
            ("binding", Binding.ToJson()),
            ("evidence", WorldSettlementValidation.EvidenceToJson(
                Evidence)),
            ("deliveries", JsonArrayBuilder.Array(
                Deliveries.Select(item => item.ToJson()))));
    }
}

public sealed class WorldSettlementDeliveryState
{
    public WorldSettlementDeliveryState(
        string operationId,
        WorldSettlementSinkKind kind,
        WorldSettlementStage stage,
        string reasonCode)
    {
        if (!Enum.IsDefined(typeof(WorldSettlementSinkKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(typeof(WorldSettlementStage), stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        Kind = kind;
        Stage = stage;
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
    }

    public string OperationId { get; }

    public WorldSettlementSinkKind Kind { get; }

    public WorldSettlementStage Stage { get; }

    public string ReasonCode { get; }
}

public sealed class WorldSettlementRecord
{
    internal WorldSettlementRecord(
        WorldSettlementPlan plan,
        long revision,
        IEnumerable<WorldSettlementDeliveryState> deliveryStates)
    {
        Plan = WorldSettlementValidation.ClonePlan(
            plan ?? throw new ArgumentNullException(nameof(plan)));
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Revision = revision;
        var states = RuntimeInputGuard.CopyBounded(
            deliveryStates
            ?? throw new ArgumentNullException(nameof(deliveryStates)),
            Plan.Deliveries.Count,
            item => new WorldSettlementDeliveryState(
                item.OperationId,
                item.Kind,
                item.Stage,
                item.ReasonCode),
            nameof(deliveryStates),
            "world_settlement_state_count_exceeded");
        if (states.Length != Plan.Deliveries.Count)
        {
            throw new ArgumentException(
                "Every settlement delivery requires exactly one state.",
                nameof(deliveryStates));
        }

        for (var index = 0; index < states.Length; index++)
        {
            var delivery = Plan.Deliveries[index];
            if (!string.Equals(
                    states[index].OperationId,
                    delivery.OperationId,
                    StringComparison.Ordinal)
                || states[index].Kind != delivery.Kind)
            {
                throw new ArgumentException(
                    "Settlement delivery state order or identity is invalid.",
                    nameof(deliveryStates));
            }
        }

        DeliveryStates =
            new ReadOnlyCollection<WorldSettlementDeliveryState>(states);
        Stage = ComputeStage(states);
    }

    public WorldSettlementPlan Plan { get; }

    public long Revision { get; }

    public IReadOnlyList<WorldSettlementDeliveryState> DeliveryStates
    {
        get;
    }

    public WorldSettlementStage Stage { get; }

    internal WorldSettlementRecord Transition(
        int deliveryIndex,
        WorldSettlementStage expectedStage,
        WorldSettlementStage nextStage,
        string reasonCode)
    {
        if (deliveryIndex < 0 || deliveryIndex >= DeliveryStates.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryIndex));
        }

        var current = DeliveryStates[deliveryIndex];
        if (current.Stage != expectedStage)
        {
            throw new InvalidOperationException(
                "The settlement delivery stage changed.");
        }

        if (!WorldSettlementValidation.IsAllowedTransition(
                expectedStage,
                nextStage))
        {
            throw new ArgumentException(
                "The settlement delivery transition is invalid.",
                nameof(nextStage));
        }

        var states = DeliveryStates
            .Select(
                (item, index) => index == deliveryIndex
                    ? new WorldSettlementDeliveryState(
                        item.OperationId,
                        item.Kind,
                        nextStage,
                        reasonCode)
                    : item)
            .ToArray();
        return new WorldSettlementRecord(
            Plan,
            checked(Revision + 1),
            states);
    }

    private static WorldSettlementStage ComputeStage(
        IReadOnlyList<WorldSettlementDeliveryState> states)
    {
        if (states.All(
                item => item.Stage == WorldSettlementStage.Applied))
        {
            return WorldSettlementStage.Applied;
        }

        if (states.Any(
                item => item.Stage
                        == WorldSettlementStage.Reconciliation))
        {
            return WorldSettlementStage.Reconciliation;
        }

        if (states.Any(
                item => item.Stage == WorldSettlementStage.Rejected))
        {
            return WorldSettlementStage.Rejected;
        }

        return WorldSettlementStage.Pending;
    }
}

public sealed class WorldSettlementBeginResult
{
    public WorldSettlementBeginResult(
        WorldSettlementBeginStatus status,
        WorldSettlementRecord? record)
    {
        if (!Enum.IsDefined(typeof(WorldSettlementBeginStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Record = record;
        if (status is WorldSettlementBeginStatus.Created
            or WorldSettlementBeginStatus.Existing
            && record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }
    }

    public WorldSettlementBeginStatus Status { get; }

    public WorldSettlementRecord? Record { get; }
}

public sealed class WorldSettlementTransition
{
    public WorldSettlementTransition(
        string settlementId,
        string planDigest,
        long expectedRecordRevision,
        string operationId,
        WorldSettlementStage expectedStage,
        WorldSettlementStage nextStage,
        string reasonCode)
    {
        SettlementId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        if (!CanonicalJsonDigest.IsSha256(planDigest))
        {
            throw new ArgumentException(
                "A plan digest must be a lowercase SHA-256 digest.",
                nameof(planDigest));
        }

        if (expectedRecordRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRecordRevision));
        }

        PlanDigest = planDigest;
        ExpectedRecordRevision = expectedRecordRevision;
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        ExpectedStage = expectedStage;
        NextStage = nextStage;
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
        if (!WorldSettlementValidation.IsAllowedTransition(
                expectedStage,
                nextStage))
        {
            throw new ArgumentException(
                "The settlement delivery transition is invalid.",
                nameof(nextStage));
        }
    }

    public string SettlementId { get; }

    public string PlanDigest { get; }

    public long ExpectedRecordRevision { get; }

    public string OperationId { get; }

    public WorldSettlementStage ExpectedStage { get; }

    public WorldSettlementStage NextStage { get; }

    public string ReasonCode { get; }
}

public sealed class WorldSettlementTransitionResult
{
    public WorldSettlementTransitionResult(
        WorldSettlementTransitionStatus status,
        WorldSettlementRecord? record)
    {
        if (!Enum.IsDefined(
                typeof(WorldSettlementTransitionStatus),
                status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Record = record;
        if (status != WorldSettlementTransitionStatus.NotFound
            && record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }
    }

    public WorldSettlementTransitionStatus Status { get; }

    public WorldSettlementRecord? Record { get; }
}

/// <summary>
/// Bounded keyset query for unsettled outbox records. Continuation cursors are
/// opaque and scoped to the deterministic settlement-ID ordering.
/// </summary>
public sealed class WorldSettlementListRequest
{
    public WorldSettlementListRequest(
        int maxResults = 100,
        string? continuationCursor = null)
    {
        WorldSettlementValidation.ValidateEnumerationLimit(maxResults);
        if (continuationCursor is not null)
        {
            _ = WorldSettlementValidation.DecodeCursor(
                continuationCursor);
        }

        MaxResults = maxResults;
        ContinuationCursor = continuationCursor;
    }

    public int MaxResults { get; }

    public string? ContinuationCursor { get; }
}

/// <summary>
/// Payload-free recovery index entry. A lifecycle owner explicitly calls
/// <see cref="IWorldSettlementStore.ReadAsync"/> before resuming a settlement,
/// so scheduler enumeration never discloses or clones private sink payloads.
/// </summary>
public sealed class WorldSettlementSummary
{
    public WorldSettlementSummary(
        string settlementId,
        string planDigest,
        long revision,
        WorldSettlementStage stage)
    {
        SettlementId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        if (!CanonicalJsonDigest.IsSha256(planDigest))
        {
            throw new ArgumentException(
                "A settlement summary requires a plan SHA-256 digest.",
                nameof(planDigest));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(typeof(WorldSettlementStage), stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        PlanDigest = planDigest;
        Revision = revision;
        Stage = stage;
    }

    public string SettlementId { get; }

    public string PlanDigest { get; }

    public long Revision { get; }

    public WorldSettlementStage Stage { get; }
}

public sealed class WorldSettlementPage
{
    public WorldSettlementPage(
        IReadOnlyList<WorldSettlementSummary> items,
        string? continuationCursor,
        bool hasMore)
    {
        Items = new ReadOnlyCollection<WorldSettlementSummary>(
            RuntimeInputGuard.CopyBounded(
                items ?? throw new ArgumentNullException(nameof(items)),
                4_096,
                item => item
                        ?? throw new ArgumentException(
                            "Settlement pages cannot contain null summaries.",
                            nameof(items)),
                nameof(items),
                "world_settlement_page_count_exceeded"));
        if (Items.Count == 0)
        {
            if (continuationCursor is not null || hasMore)
            {
                throw new ArgumentException(
                    "An empty settlement page cannot have a continuation.",
                    nameof(continuationCursor));
            }
        }
        else
        {
            var expectedCursor = WorldSettlementValidation.EncodeCursor(
                Items[^1].SettlementId);
            if (!string.Equals(
                    expectedCursor,
                    continuationCursor,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A settlement page cursor must identify its final "
                    + "record.",
                    nameof(continuationCursor));
            }

            for (var index = 1; index < Items.Count; index++)
            {
                if (StringComparer.Ordinal.Compare(
                        Items[index - 1].SettlementId,
                        Items[index].SettlementId)
                    >= 0)
                {
                    throw new ArgumentException(
                        "Settlement page records must be strictly ordered.",
                        nameof(items));
                }
            }
        }

        ContinuationCursor = continuationCursor;
        HasMore = hasMore;
    }

    public IReadOnlyList<WorldSettlementSummary> Items { get; }

    public string? ContinuationCursor { get; }

    public bool HasMore { get; }
}

public interface IWorldSettlementStore
{
    ValueTask<WorldSettlementRecord?> ReadAsync(
        string settlementId,
        CancellationToken cancellationToken = default);

    ValueTask<WorldSettlementBeginResult> BeginAsync(
        WorldSettlementPlan plan,
        CancellationToken cancellationToken = default);

    ValueTask<WorldSettlementTransitionResult> TryTransitionAsync(
        WorldSettlementTransition transition,
        CancellationToken cancellationToken = default);

    ValueTask<WorldSettlementPage> ListUnsettledAsync(
        WorldSettlementListRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A store-level fence used to capture a complete settled-world artifact.
/// Implementations must acquire the fence atomically with verifying that
/// the outbox contains no pending or reconciliation record. While the
/// returned lease is held, no begin or transition operation may enter the
/// store. Returning <see langword="null"/> means that the outbox is not
/// settled at the acquisition point.
/// </summary>
public interface IWorldSettlementQuiescenceSource
{
    ValueTask<IWorldSettlementQuiescenceLease?>
        TryAcquireSettledQuiescenceAsync(
            CancellationToken cancellationToken = default);
}

/// <summary>
/// Exclusive settled-outbox fence. Disposing the lease admits settlement
/// mutations again.
/// </summary>
public interface IWorldSettlementQuiescenceLease : IAsyncDisposable
{
    long StoreRevision { get; }
}

public sealed class InMemoryWorldSettlementStore
    : IWorldSettlementStore,
      IWorldSettlementQuiescenceSource
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _quiescenceGate = new(1, 1);
    private readonly int _maxRecords;
    private readonly Dictionary<string, WorldSettlementRecord> _records =
        new(StringComparer.Ordinal);
    private readonly SortedSet<string> _unsettledIds =
        new(StringComparer.Ordinal);
    private long _storeRevision;

    public InMemoryWorldSettlementStore(int maxRecords = 100_000)
    {
        if (maxRecords is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecords));
        }

        _maxRecords = maxRecords;
    }

    public async ValueTask<WorldSettlementRecord?> ReadAsync(
        string settlementId,
        CancellationToken cancellationToken = default)
    {
        var id = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        await _quiescenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return
                _records.TryGetValue(id, out var record)
                    ? WorldSettlementValidation.CloneRecord(record)
                    : null;
            }
        }
        finally
        {
            _quiescenceGate.Release();
        }
    }

    public async ValueTask<WorldSettlementBeginResult> BeginAsync(
        WorldSettlementPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        await _quiescenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_records.TryGetValue(
                        plan.SettlementId,
                        out var existing))
                {
                    return new WorldSettlementBeginResult(
                        string.Equals(
                            existing.Plan.SemanticDigest,
                            plan.SemanticDigest,
                            StringComparison.Ordinal)
                            ? WorldSettlementBeginStatus.Existing
                            : WorldSettlementBeginStatus.Conflict,
                        WorldSettlementValidation.CloneRecord(existing));
                }

                if (_records.Count >= _maxRecords)
                {
                    return new WorldSettlementBeginResult(
                        WorldSettlementBeginStatus.CapacityExceeded,
                        record: null);
                }

                var record = WorldSettlementValidation.NewRecord(plan);
                _records.Add(plan.SettlementId, record);
                _unsettledIds.Add(plan.SettlementId);
                _storeRevision = checked(_storeRevision + 1);
                return new WorldSettlementBeginResult(
                    WorldSettlementBeginStatus.Created,
                    WorldSettlementValidation.CloneRecord(record));
            }
        }
        finally
        {
            _quiescenceGate.Release();
        }
    }

    public async ValueTask<WorldSettlementTransitionResult>
        TryTransitionAsync(
        WorldSettlementTransition transition,
        CancellationToken cancellationToken = default)
    {
        if (transition is null)
        {
            throw new ArgumentNullException(nameof(transition));
        }

        await _quiescenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_records.TryGetValue(
                        transition.SettlementId,
                        out var current))
                {
                    return new WorldSettlementTransitionResult(
                        WorldSettlementTransitionStatus.NotFound,
                        record: null);
                }

                var index = WorldSettlementValidation.FindDeliveryIndex(
                    current,
                    transition.OperationId);
                if (current.Revision
                        != transition.ExpectedRecordRevision
                    || !string.Equals(
                        current.Plan.SemanticDigest,
                        transition.PlanDigest,
                        StringComparison.Ordinal)
                    || index < 0
                    || current.DeliveryStates[index].Stage
                    != transition.ExpectedStage)
                {
                    return new WorldSettlementTransitionResult(
                        WorldSettlementTransitionStatus.Conflict,
                        WorldSettlementValidation.CloneRecord(current));
                }

                var updated = current.Transition(
                    index,
                    transition.ExpectedStage,
                    transition.NextStage,
                    transition.ReasonCode);
                _records[transition.SettlementId] = updated;
                if (updated.Stage is WorldSettlementStage.Applied
                    or WorldSettlementStage.Rejected)
                {
                    _unsettledIds.Remove(transition.SettlementId);
                }
                else
                {
                    _unsettledIds.Add(transition.SettlementId);
                }

                _storeRevision = checked(_storeRevision + 1);
                return new WorldSettlementTransitionResult(
                    WorldSettlementTransitionStatus.Applied,
                    WorldSettlementValidation.CloneRecord(updated));
            }
        }
        finally
        {
            _quiescenceGate.Release();
        }
    }

    public async ValueTask<WorldSettlementPage>
        ListUnsettledAsync(
            WorldSettlementListRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var afterId = request.ContinuationCursor is null
            ? null
            : WorldSettlementValidation.DecodeCursor(
                request.ContinuationCursor);
        await _quiescenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var items = new List<WorldSettlementSummary>(
                    request.MaxResults);
                var hasMore = false;
                if (_unsettledIds.Count > 0
                    && (afterId is null
                        || StringComparer.Ordinal.Compare(
                            afterId,
                            _unsettledIds.Max!)
                        < 0))
                {
                    var candidates = afterId is null
                        ? _unsettledIds
                        : _unsettledIds.GetViewBetween(
                            afterId,
                            _unsettledIds.Max!);
                    foreach (var settlementId in candidates)
                    {
                        if (afterId is not null
                            && StringComparer.Ordinal.Compare(
                                settlementId,
                                afterId)
                            <= 0)
                        {
                            continue;
                        }

                        if (items.Count == request.MaxResults)
                        {
                            hasMore = true;
                            break;
                        }

                        items.Add(WorldSettlementValidation.Summarize(
                            _records[settlementId]));
                    }
                }

                return new WorldSettlementPage(
                    items,
                    items.Count == 0
                        ? null
                        : WorldSettlementValidation.EncodeCursor(
                            items[^1].SettlementId),
                    hasMore);
            }
        }
        finally
        {
            _quiescenceGate.Release();
        }
    }

    public async ValueTask<IWorldSettlementQuiescenceLease?>
        TryAcquireSettledQuiescenceAsync(
            CancellationToken cancellationToken = default)
    {
        await _quiescenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var release = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_unsettledIds.Count != 0)
                {
                    return null;
                }

                release = false;
                return new InMemoryQuiescenceLease(
                    _quiescenceGate,
                    _storeRevision);
            }
        }
        finally
        {
            if (release)
            {
                _quiescenceGate.Release();
            }
        }
    }

    private sealed class InMemoryQuiescenceLease
        : IWorldSettlementQuiescenceLease
    {
        private SemaphoreSlim? _gate;

        public InMemoryQuiescenceLease(
            SemaphoreSlim gate,
            long storeRevision)
        {
            _gate = gate;
            StoreRevision = storeRevision;
        }

        public long StoreRevision { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return default;
        }
    }
}

public sealed class WorldSettlementAuthorityRequest
{
    public WorldSettlementAuthorityRequest(WorldSettlementPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        Plan = WorldSettlementValidation.ClonePlan(plan);
        SettlementId = Plan.SettlementId;
        Source = WorldPresentationValidation.CloneSource(Plan.Source);
        Binding = WorldPresentationValidation.CloneBinding(Plan.Binding);
        EvidenceDigest = Plan.EvidenceDigest;
        PlanDigest = Plan.SemanticDigest;
    }

    public WorldSettlementPlan Plan { get; }

    public string SettlementId { get; }

    public WorldPresentationSource Source { get; }

    public WorldPresentationBinding Binding { get; }

    public string EvidenceDigest { get; }

    public string PlanDigest { get; }
}

public sealed class WorldSettlementDeliveryClaim
{
    internal WorldSettlementDeliveryClaim(
        WorldSettlementDelivery delivery)
    {
        OperationId = delivery.OperationId;
        Kind = delivery.Kind;
        Delivery = WorldSettlementValidation.CloneDelivery(delivery);
        DeliveryDigest =
            WorldPresentationValidation.ComputeSemanticDigest(
                Delivery.ToJson());
        Audience = WorldSettlementValidation.CloneAudience(
            Delivery.Audience);
    }

    public string OperationId { get; }

    public WorldSettlementSinkKind Kind { get; }

    /// <summary>
    /// Complete immutable delivery proposed to the sink. The host can inspect
    /// typed memory, group, or presentation data when applying ownership and
    /// disclosure policy.
    /// </summary>
    public WorldSettlementDelivery Delivery { get; }

    public string DeliveryDigest { get; }

    public WorldSettlementAudienceClaim Audience { get; }
}

public sealed class WorldSettlementAuthorityDecision
{
    public WorldSettlementAuthorityDecision(
        bool accepted,
        string reasonCode)
    {
        Accepted = accepted;
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
    }

    public bool Accepted { get; }

    public string ReasonCode { get; }

    public static WorldSettlementAuthorityDecision Allow()
    {
        return new WorldSettlementAuthorityDecision(
            accepted: true,
            WorldSettlementReasonCodes.Applied);
    }

    public static WorldSettlementAuthorityDecision Deny(string reasonCode)
    {
        return new WorldSettlementAuthorityDecision(
            accepted: false,
            reasonCode);
    }
}

/// <summary>
/// Host-owned guard for current authoritative state, membership, and
/// incarnation checks. An acquired lease that allows any delivery must keep
/// the exact world binding and all admitted audience lifetimes stable until
/// the lease is disposed. A process-spanning host must provide an equivalent
/// distributed lease; a check-then-return implementation is not sufficient.
/// </summary>
public interface IWorldSettlementAuthorityGuard
{
    ValueTask<IWorldSettlementAuthorityLease?> AcquireAsync(
        WorldSettlementAuthorityRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorldSettlementAuthorityLease : IAsyncDisposable
{
    ValueTask<WorldSettlementAuthorityDecision> ValidateAsync(
        WorldSettlementDeliveryClaim claim,
        CancellationToken cancellationToken = default);
}

public sealed class WorldSettlementEvidenceException
    : InvalidOperationException
{
    public WorldSettlementEvidenceException(
        string settlementId,
        string reasonCode,
        string message)
        : base(message)
    {
        SettlementId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
    }

    public string SettlementId { get; }

    public string ReasonCode { get; }
}

public sealed class WorldSettlementStoreConflictException
    : InvalidOperationException
{
    public WorldSettlementStoreConflictException(
        string settlementId,
        string reasonCode)
        : base(
            "A settlement identity was reused with a different plan or "
            + "the outbox could not admit another record.")
    {
        SettlementId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
    }

    public string SettlementId { get; }

    public string ReasonCode { get; }
}

public sealed class WorldSettlementCoordinatorOptions
{
    public WorldSettlementCoordinatorOptions(
        int maxTransitionsPerInvocation = 4_096)
    {
        if (maxTransitionsPerInvocation is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTransitionsPerInvocation));
        }

        MaxTransitionsPerInvocation = maxTransitionsPerInvocation;
    }

    public int MaxTransitionsPerInvocation { get; }
}

/// <summary>
/// Opaque identity for one exact settlement outbox and its sink stores.
/// Coordinators that use the same complete store set share one topology.
/// Partially overlapping store sets are rejected so a capture cannot replace
/// an active outbox while retaining any of its sidecars.
/// </summary>
public sealed class WorldSettlementTopology
{
    private static readonly object RegistryGate = new();
    private static readonly ConditionalWeakTable<object, Registration>
        Registry = new();

    private WorldSettlementTopology(
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory,
        IGroupInteractionStore? groups,
        IWorldPresentationStore? presentations)
    {
        SettlementStore = store;
        MemoryStore = memory;
        GroupStore = groups;
        PresentationStore = presentations;
    }

    internal IWorldSettlementStore SettlementStore { get; }

    internal IIdempotentAtomicMemoryBatchStore? MemoryStore { get; }

    internal IGroupInteractionStore? GroupStore { get; }

    internal IWorldPresentationStore? PresentationStore { get; }

    internal static WorldSettlementTopology GetOrCreate(
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory,
        IGroupInteractionStore? groups,
        IWorldPresentationStore? presentations)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        var components = DistinctComponents(
            store,
            memory,
            groups,
            presentations);
        lock (RegistryGate)
        {
            WorldSettlementTopology? existing = null;
            foreach (var component in components)
            {
                if (!Registry.TryGetValue(
                        component,
                        out var registration))
                {
                    continue;
                }

                if (existing is not null
                    && !ReferenceEquals(
                        existing,
                        registration.Topology))
                {
                    throw Overlap();
                }

                existing = registration.Topology;
            }

            if (existing is not null)
            {
                if (!existing.Matches(
                        store,
                        memory,
                        groups,
                        presentations))
                {
                    throw Overlap();
                }

                return existing;
            }

            var created = new WorldSettlementTopology(
                store,
                memory,
                groups,
                presentations);
            var claimed = new Registration(created);
            foreach (var component in components)
            {
                Registry.Add(component, claimed);
            }

            return created;
        }
    }

    private bool Matches(
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory,
        IGroupInteractionStore? groups,
        IWorldPresentationStore? presentations)
    {
        return ReferenceEquals(SettlementStore, store)
               && ReferenceEquals(MemoryStore, memory)
               && ReferenceEquals(GroupStore, groups)
               && ReferenceEquals(PresentationStore, presentations);
    }

    private static IReadOnlyList<object> DistinctComponents(
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory,
        IGroupInteractionStore? groups,
        IWorldPresentationStore? presentations)
    {
        var result = new List<object>(4);
        AddDistinct(result, store);
        AddDistinct(result, memory);
        AddDistinct(result, groups);
        AddDistinct(result, presentations);
        return result;
    }

    private static void AddDistinct(
        ICollection<object> target,
        object? component)
    {
        if (component is null
            || target.Any(item => ReferenceEquals(item, component)))
        {
            return;
        }

        target.Add(component);
    }

    private static InvalidOperationException Overlap()
    {
        return new InvalidOperationException(
            "A settlement store or sink already belongs to a different "
            + "settlement topology.");
    }

    private sealed class Registration
    {
        public Registration(WorldSettlementTopology topology)
        {
            Topology = topology;
        }

        public WorldSettlementTopology Topology { get; }
    }
}

/// <summary>
/// Durable, game-neutral delivery coordinator. It never executes the world
/// action or model that produced a receipt, and it never derives one sink's
/// payload from another sink's payload.
/// </summary>
public sealed class WorldSettlementCoordinator
{
    private readonly ICommittedWorldPresentationEvidenceSource _evidence;
    private readonly IWorldSettlementAuthorityGuard _authority;
    private readonly IWorldSettlementStore _store;
    private readonly IIdempotentAtomicMemoryBatchStore? _memory;
    private readonly IGroupInteractionStore? _groups;
    private readonly IWorldPresentationStore? _presentations;
    private readonly WorldSettlementCoordinatorOptions _options;

    public WorldSettlementCoordinator(
        ICommittedWorldPresentationEvidenceSource evidence,
        IWorldSettlementAuthorityGuard authority,
        IWorldSettlementStore store,
        IIdempotentAtomicMemoryBatchStore? memory = null,
        IGroupInteractionStore? groups = null,
        IWorldPresentationStore? presentations = null,
        WorldSettlementCoordinatorOptions? options = null)
    {
        _evidence = evidence
                    ?? throw new ArgumentNullException(nameof(evidence));
        _authority = authority
                     ?? throw new ArgumentNullException(nameof(authority));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _memory = memory;
        _groups = groups;
        _presentations = presentations;
        _options = options ?? new WorldSettlementCoordinatorOptions();
        Topology = WorldSettlementTopology.GetOrCreate(
            _store,
            _memory,
            _groups,
            _presentations);
    }

    public WorldSettlementTopology Topology { get; }

    public async ValueTask<WorldSettlementRecord> SettleAsync(
        WorldSettlementPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var evidence = await RequireExactEvidenceAsync(
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        var begin = await _store.BeginAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        if (begin.Status is WorldSettlementBeginStatus.Conflict
            or WorldSettlementBeginStatus.CapacityExceeded)
        {
            throw new WorldSettlementStoreConflictException(
                plan.SettlementId,
                begin.Status == WorldSettlementBeginStatus.Conflict
                    ? WorldSettlementReasonCodes.PlanConflict
                    : WorldSettlementReasonCodes.StoreConflict);
        }

        RequireSameRecord(plan, begin.Record!);
        return await ProcessAsync(
                begin.Record!,
                evidence,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorldSettlementRecord> ResumeAsync(
        string settlementId,
        CancellationToken cancellationToken = default)
    {
        var expectedId = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        var record = await _store.ReadAsync(
                expectedId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            throw new KeyNotFoundException(
                "The settlement outbox record does not exist.");
        }

        if (!string.Equals(
                expectedId,
                record.Plan.SettlementId,
                StringComparison.Ordinal))
        {
            throw new WorldSettlementStoreConflictException(
                expectedId,
                WorldSettlementReasonCodes.StoreConflict);
        }

        return await ProcessAsync(
                record,
                record.Plan.Evidence,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorldSettlementRecord> ProcessAsync(
        WorldSettlementRecord initial,
        CommittedWorldPresentationEvidence verifiedEvidence,
        CancellationToken cancellationToken)
    {
        var record = initial;
        if (record.Stage is WorldSettlementStage.Applied
            or WorldSettlementStage.Rejected)
        {
            return record;
        }

        var evidence = WorldSettlementValidation.CloneEvidence(
            verifiedEvidence);

        await using var lease = await _authority.AcquireAsync(
                new WorldSettlementAuthorityRequest(record.Plan),
                cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            if (record.DeliveryStates.Any(
                    item => item.Stage
                            == WorldSettlementStage.Reconciliation))
            {
                return record;
            }

            return await DenyFirstPendingAsync(
                    record,
                    WorldSettlementReasonCodes.AuthorityDenied,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var transitions = 0;
        while (record.Stage is not WorldSettlementStage.Applied
               and not WorldSettlementStage.Rejected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progressed = false;
            for (var index = 0;
                 index < record.DeliveryStates.Count;
                 index++)
            {
                var state = record.DeliveryStates[index];
                if (state.Stage == WorldSettlementStage.Applied)
                {
                    continue;
                }

                if (state.Stage == WorldSettlementStage.Rejected)
                {
                    continue;
                }

                var delivery = record.Plan.Deliveries[index];
                if (!HasSink(delivery.Kind))
                {
                    if (state.Stage
                        == WorldSettlementStage.Reconciliation)
                    {
                        return record;
                    }

                    record = await TransitionAsync(
                            record,
                            delivery.OperationId,
                            WorldSettlementStage.Pending,
                            WorldSettlementStage.Rejected,
                            WorldSettlementReasonCodes.SinkNotConfigured,
                            cancellationToken)
                        .ConfigureAwait(false);
                    transitions++;
                    progressed = true;
                    break;
                }

                if (state.Stage
                        == WorldSettlementStage.Reconciliation
                    && delivery is WorldSettlementGroupDelivery group
                    && await TryConfirmGroupReplayAsync(
                            group,
                            record,
                            cancellationToken)
                        .ConfigureAwait(false) is { } replayed)
                {
                    record = replayed;
                    transitions++;
                    progressed = true;
                    break;
                }

                var decision = await lease.ValidateAsync(
                        new WorldSettlementDeliveryClaim(delivery),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decision is null)
                {
                    throw new InvalidOperationException(
                        "A settlement authority lease returned no decision.");
                }

                if (!decision.Accepted)
                {
                    if (state.Stage
                        == WorldSettlementStage.Reconciliation)
                    {
                        return record;
                    }

                    record = await TransitionAsync(
                            record,
                            delivery.OperationId,
                            WorldSettlementStage.Pending,
                            WorldSettlementStage.Rejected,
                            decision.ReasonCode,
                            cancellationToken)
                        .ConfigureAwait(false);
                    transitions++;
                    progressed = true;
                    break;
                }

                if (state.Stage == WorldSettlementStage.Pending)
                {
                    record = await TransitionAsync(
                            record,
                            delivery.OperationId,
                            WorldSettlementStage.Pending,
                            WorldSettlementStage.Reconciliation,
                            WorldSettlementReasonCodes
                                .DispatchIntentCommitted,
                            cancellationToken)
                        .ConfigureAwait(false);
                    transitions++;
                    progressed = true;
                    var currentIndex =
                        WorldSettlementValidation.FindDeliveryIndex(
                            record,
                            delivery.OperationId);
                    if (currentIndex < 0
                        || record.DeliveryStates[currentIndex].Stage
                        != WorldSettlementStage.Reconciliation)
                    {
                        break;
                    }
                }

                var outcome = await DeliverAsync(
                        delivery,
                        record,
                        evidence,
                        cancellationToken)
                    .ConfigureAwait(false);
                record = await TransitionAsync(
                        record,
                        delivery.OperationId,
                        WorldSettlementStage.Reconciliation,
                        outcome.Applied
                            ? WorldSettlementStage.Applied
                            : WorldSettlementStage.Rejected,
                        outcome.ReasonCode,
                        cancellationToken)
                    .ConfigureAwait(false);
                transitions++;
                progressed = true;
                break;
            }

            if (transitions >= _options.MaxTransitionsPerInvocation
                && record.Stage is not WorldSettlementStage.Applied
                    and not WorldSettlementStage.Rejected)
            {
                throw new RuntimeContentLimitException(
                    nameof(record),
                    WorldSettlementReasonCodes.TransitionLimitExceeded,
                    "A settlement invocation exceeded its transition "
                    + "budget.");
            }

            if (!progressed)
            {
                return record;
            }
        }

        return record;
    }

    private async ValueTask<WorldSettlementRecord?>
        TryConfirmGroupReplayAsync(
            WorldSettlementGroupDelivery delivery,
            WorldSettlementRecord record,
            CancellationToken cancellationToken)
    {
        var current = await _groups!.ReadAsync(
                delivery.Request.SessionId,
                cancellationToken)
            .ConfigureAwait(false);
        var request = WorldSettlementValidation.GroupRequestForDispatch(
            record.Plan,
            delivery);
        if (current is null
            || !current.Operations.Any(
                item => string.Equals(
                    item.OperationId,
                    request.OperationId,
                    StringComparison.Ordinal)))
        {
            return null;
        }

        if (!GroupBindingMatches(current, record.Plan.Binding))
        {
            return await TransitionAsync(
                    record,
                    delivery.OperationId,
                    WorldSettlementStage.Reconciliation,
                    WorldSettlementStage.Rejected,
                    GroupInteractionWriteStatuses.WorldBindingMismatch,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(
                current.GroupId,
                delivery.ExpectedGroupId,
                StringComparison.Ordinal))
        {
            return await TransitionAsync(
                    record,
                    delivery.OperationId,
                    WorldSettlementStage.Reconciliation,
                    WorldSettlementStage.Rejected,
                    GroupInteractionWriteStatuses.OperationConflict,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await _groups.AppendAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return await TransitionAsync(
                record,
                delivery.OperationId,
                WorldSettlementStage.Reconciliation,
                result.Succeeded
                    ? WorldSettlementStage.Applied
                    : WorldSettlementStage.Rejected,
                result.Succeeded
                    ? WorldSettlementReasonCodes.Applied
                    : result.Status,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<DeliveryOutcome> DeliverAsync(
        WorldSettlementDelivery delivery,
        WorldSettlementRecord record,
        CommittedWorldPresentationEvidence evidence,
        CancellationToken cancellationToken)
    {
        switch (delivery)
        {
            case WorldSettlementMemoryDelivery memory:
                try
                {
                    _ = await _memory!
                        .ApplyIdempotentAtomicBatchAsync(
                            WorldSettlementValidation.MemoryCommitId(
                                record.Plan,
                                memory),
                            WorldSettlementValidation
                                .MemoryMutationsForDispatch(memory),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return DeliveryOutcome.Success();
                }
                catch (MemoryBatchIdempotencyConflictException)
                {
                    return DeliveryOutcome.Reject(
                        MemoryBatchReasonCodes.IdempotencyConflict);
                }

            case WorldSettlementGroupDelivery group:
                {
                    var request =
                        WorldSettlementValidation.GroupRequestForDispatch(
                            record.Plan,
                            group);
                    var session = await _groups!.ReadAsync(
                            request.SessionId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (session is null)
                    {
                        return DeliveryOutcome.Reject(
                            GroupInteractionWriteStatuses.NotFound);
                    }

                    if (!GroupBindingMatches(
                            session,
                            record.Plan.Binding))
                    {
                        return DeliveryOutcome.Reject(
                            GroupInteractionWriteStatuses
                                .WorldBindingMismatch);
                    }

                    var existingOperation = session.Operations.Any(
                        item => string.Equals(
                            item.OperationId,
                            request.OperationId,
                            StringComparison.Ordinal));
                    if (!existingOperation
                        && (!string.Equals(
                                session.GroupId,
                                group.ExpectedGroupId,
                                StringComparison.Ordinal)
                            || !string.Equals(
                                session.Status,
                                GroupInteractionStatuses.Open,
                                StringComparison.Ordinal)
                            || session.Revision
                            != request.ExpectedRevision
                            || session.MembershipRevision
                            != request.ExpectedMembershipRevision
                            || !WorldSettlementValidation.SameMembers(
                                session.Members,
                                group.ExpectedMembers)))
                    {
                        return DeliveryOutcome.Reject(
                            GroupInteractionWriteStatuses
                                .MembershipRevisionConflict);
                    }

                    var result = await _groups.AppendAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return result.Succeeded
                        ? DeliveryOutcome.Success()
                        : DeliveryOutcome.Reject(result.Status);
                }

            case WorldSettlementPresentationDelivery presentation:
                {
                    if (!presentation.Draft.Source.IsSameAs(evidence.Source)
                        || !presentation.Draft.Binding.IsSameAs(
                            evidence.Binding))
                    {
                        throw new WorldSettlementEvidenceException(
                            record.Plan.SettlementId,
                            WorldSettlementReasonCodes.EvidenceMismatch,
                            "The presentation delivery does not match the "
                            + "captured committed receipt evidence.");
                    }

                    var verified = new VerifiedWorldPresentation(
                        sequence: 0,
                        presentation.Draft,
                        evidence);
                    var result = await _presentations!
                        .PublishVerifiedAsync(
                            verified,
                            presentation.ExpectedPreviousContentRevision,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return string.Equals(
                               result.Status,
                               WorldPresentationWriteStatuses.Applied,
                               StringComparison.Ordinal)
                           || string.Equals(
                               result.Status,
                               WorldPresentationWriteStatuses.Idempotent,
                               StringComparison.Ordinal)
                        ? DeliveryOutcome.Success()
                        : DeliveryOutcome.Reject(result.Status);
                }

            default:
                throw new InvalidOperationException(
                    "The settlement delivery kind is unsupported.");
        }
    }

    private async ValueTask<WorldSettlementRecord> DenyFirstPendingAsync(
        WorldSettlementRecord record,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var pending = record.DeliveryStates.FirstOrDefault(
            item => item.Stage == WorldSettlementStage.Pending);
        if (pending is null)
        {
            return record;
        }

        return await TransitionAsync(
                record,
                pending.OperationId,
                WorldSettlementStage.Pending,
                WorldSettlementStage.Rejected,
                reasonCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorldSettlementRecord> TransitionAsync(
        WorldSettlementRecord record,
        string operationId,
        WorldSettlementStage expected,
        WorldSettlementStage next,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var result = await _store.TryTransitionAsync(
                new WorldSettlementTransition(
                    record.Plan.SettlementId,
                    record.Plan.SemanticDigest,
                    record.Revision,
                    operationId,
                    expected,
                    next,
                    reasonCode),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == WorldSettlementTransitionStatus.NotFound)
        {
            throw new WorldSettlementStoreConflictException(
                record.Plan.SettlementId,
                WorldSettlementReasonCodes.StoreConflict);
        }

        RequireSameRecord(record.Plan, result.Record!);
        return result.Record!;
    }

    private bool HasSink(WorldSettlementSinkKind kind)
    {
        return kind switch
        {
            WorldSettlementSinkKind.Memory => _memory is not null,
            WorldSettlementSinkKind.Group => _groups is not null,
            WorldSettlementSinkKind.Presentation =>
                _presentations is not null,
            _ => false
        };
    }

    private static bool GroupBindingMatches(
        GroupInteractionSession session,
        WorldPresentationBinding binding)
    {
        var groupBinding = session.WorldBinding;
        return groupBinding is not null
               && string.Equals(
                   groupBinding.WorldId,
                   binding.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   groupBinding.TimelineId,
                   binding.TimelineId,
                   StringComparison.Ordinal)
               && groupBinding.TimelineEpoch
               == binding.TimelineEpoch
               && groupBinding.SaveRevision <= binding.SaveRevision;
    }

    private async ValueTask<CommittedWorldPresentationEvidence>
        RequireExactEvidenceAsync(
        WorldSettlementPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = await _evidence.ReadCommittedAsync(
                plan.Source.WorldReceiptId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence is null)
        {
            throw new WorldSettlementEvidenceException(
                plan.SettlementId,
                WorldSettlementReasonCodes.EvidenceMissing,
                "The settlement receipt is missing or is not committed "
                + "as applied.");
        }

        if (!plan.Source.IsSameAs(evidence.Source)
            || !plan.Binding.IsSameAs(evidence.Binding)
            || !string.Equals(
                plan.EvidenceDigest,
                evidence.SemanticDigest,
                StringComparison.Ordinal))
        {
            throw new WorldSettlementEvidenceException(
                plan.SettlementId,
                WorldSettlementReasonCodes.EvidenceMismatch,
                "The settlement source, coordinate, or receipt evidence "
                + "does not exactly match the committed receipt.");
        }

        return evidence;
    }

    private static void RequireSameRecord(
        WorldSettlementPlan expected,
        WorldSettlementRecord record)
    {
        if (!string.Equals(
                expected.SettlementId,
                record.Plan.SettlementId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.SemanticDigest,
                record.Plan.SemanticDigest,
                StringComparison.Ordinal))
        {
            throw new WorldSettlementStoreConflictException(
                expected.SettlementId,
                WorldSettlementReasonCodes.StoreConflict);
        }
    }

    private readonly struct DeliveryOutcome
    {
        private DeliveryOutcome(bool applied, string reasonCode)
        {
            Applied = applied;
            ReasonCode = reasonCode;
        }

        public bool Applied { get; }

        public string ReasonCode { get; }

        public static DeliveryOutcome Success()
        {
            return new DeliveryOutcome(
                applied: true,
                WorldSettlementReasonCodes.Applied);
        }

        public static DeliveryOutcome Reject(string reasonCode)
        {
            return new DeliveryOutcome(applied: false, reasonCode);
        }
    }
}

internal static class WorldSettlementValidation
{
    private static readonly WorldSettlementLimits MaximumLimits = new(
        maxDeliveries: 4_096,
        maxAudienceMembers: 4_096,
        maxAggregateUtf8Bytes: 32 * 1_048_576,
        maxAggregateJsonNodes: 1_000_000);

    public static WorldSettlementAudienceClaim CloneAudience(
        WorldSettlementAudienceClaim audience)
    {
        return new WorldSettlementAudienceClaim(
            audience.MembershipScopeId,
            audience.MembershipRevision,
            audience.Members,
            audience.PrivacyClass,
            audience.RedactionClass,
            maxMembers: 4_096);
    }

    public static CommittedWorldPresentationEvidence CloneEvidence(
        CommittedWorldPresentationEvidence evidence)
    {
        return new CommittedWorldPresentationEvidence(
            evidence.Source,
            evidence.Binding,
            evidence.CommitStatus,
            evidence.OutcomeCode,
            evidence.ReceiptEvidence);
    }

    public static JsonElement EvidenceToJson(
        CommittedWorldPresentationEvidence evidence)
    {
        return JsonArrayBuilder.Object(
            ("source", evidence.Source.ToJson()),
            ("binding", evidence.Binding.ToJson()),
            ("commitStatus", JsonArrayBuilder.String("applied")),
            ("outcomeCode", JsonArrayBuilder.String(
                evidence.OutcomeCode)),
            ("receiptEvidence", evidence.ReceiptEvidence.HasValue
                ? evidence.ReceiptEvidence.Value
                : JsonArrayBuilder.Null()));
    }

    public static string MemoryCommitId(
        WorldSettlementPlan plan,
        WorldSettlementMemoryDelivery delivery)
    {
        var digest = WorldPresentationValidation.ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("settlementId", JsonArrayBuilder.String(
                    plan.SettlementId)),
                ("operationId", JsonArrayBuilder.String(
                    delivery.OperationId)),
                ("planDigest", JsonArrayBuilder.String(
                    plan.SemanticDigest)),
                ("delivery", delivery.ToJson())));
        return $"world-settlement-memory-{digest}";
    }

    public static IReadOnlyList<MemoryMutation> MemoryMutationsForDispatch(
        WorldSettlementMemoryDelivery delivery)
    {
        var owner = delivery.Audience.Members[0];
        return delivery.Mutations
            .Select(
                mutation =>
                {
                    var record = mutation.Record!;
                    var provenance = record.Provenance!;
                    var scopedId =
                        WorldPresentationValidation.ComputeSemanticDigest(
                            JsonArrayBuilder.Object(
                                ("worldId", JsonArrayBuilder.String(
                                    provenance.WorldId)),
                                ("sessionId", provenance.SessionId is null
                                    ? JsonArrayBuilder.Null()
                                    : JsonArrayBuilder.String(
                                        provenance.SessionId)),
                                ("timelineId", JsonArrayBuilder.String(
                                    provenance.TimelineId!)),
                                ("timelineEpoch", JsonArrayBuilder.Number(
                                    provenance.TimelineEpoch!.Value)),
                                ("scope", JsonArrayBuilder.String(
                                    record.Scope)),
                                ("entityId", JsonArrayBuilder.String(
                                    owner.EntityId)),
                                ("incarnation", JsonArrayBuilder.Number(
                                    owner.Incarnation)),
                                ("memoryId", JsonArrayBuilder.String(
                                    record.MemoryId))));
                    return MemoryMutation.Upsert(
                        CloneMemoryRecord(
                            record,
                            $"private-memory-{scopedId}"));
                })
            .ToArray();
    }

    public static GroupInteractionAppendRequest GroupRequestForDispatch(
        WorldSettlementPlan plan,
        WorldSettlementGroupDelivery delivery)
    {
        var digest = WorldPresentationValidation.ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("settlementId", JsonArrayBuilder.String(
                    plan.SettlementId)),
                ("operationId", JsonArrayBuilder.String(
                    delivery.OperationId)),
                ("planDigest", JsonArrayBuilder.String(
                    plan.SemanticDigest)),
                ("delivery", delivery.ToJson())));
        return new GroupInteractionAppendRequest(
            $"world-settlement-group-{digest}",
            delivery.Request.SessionId,
            delivery.Request.ExpectedRevision,
            delivery.Request.ExpectedMembershipRevision,
            delivery.Request.Messages);
    }

    public static WorldSettlementSummary Summarize(
        WorldSettlementRecord record)
    {
        return new WorldSettlementSummary(
            record.Plan.SettlementId,
            record.Plan.SemanticDigest,
            record.Revision,
            record.Stage);
    }

    public static WorldSettlementDelivery CloneDelivery(
        WorldSettlementDelivery delivery)
    {
        if (delivery is null)
        {
            throw new ArgumentException(
                "Settlement deliveries cannot contain null.",
                nameof(delivery));
        }

        return delivery switch
        {
            WorldSettlementMemoryDelivery memory =>
                new WorldSettlementMemoryDelivery(
                    memory.OperationId,
                    memory.Audience,
                    memory.Mutations),
            WorldSettlementGroupDelivery group =>
                new WorldSettlementGroupDelivery(
                    group.OperationId,
                    group.ExpectedGroupId,
                    group.ExpectedMembers,
                    group.Request,
                    group.Audience),
            WorldSettlementPresentationDelivery presentation =>
                new WorldSettlementPresentationDelivery(
                    presentation.OperationId,
                    presentation.Draft,
                    presentation.ExpectedPreviousContentRevision),
            _ => throw new ArgumentException(
                "The settlement delivery kind is unsupported.",
                nameof(delivery))
        };
    }

    public static WorldSettlementPlan ClonePlan(WorldSettlementPlan plan)
    {
        var clone = new WorldSettlementPlan(
            plan.SettlementId,
            plan.Evidence,
            plan.Deliveries,
            MaximumLimits);
        if (!string.Equals(
                clone.SemanticDigest,
                plan.SemanticDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The settlement plan semantic digest is invalid.",
                nameof(plan));
        }

        return clone;
    }

    public static WorldSettlementRecord CloneRecord(
        WorldSettlementRecord record)
    {
        return new WorldSettlementRecord(
            record.Plan,
            record.Revision,
            record.DeliveryStates);
    }

    public static WorldSettlementRecord NewRecord(WorldSettlementPlan plan)
    {
        return new WorldSettlementRecord(
            plan,
            revision: 0,
            plan.Deliveries.Select(
                item => new WorldSettlementDeliveryState(
                    item.OperationId,
                    item.Kind,
                    WorldSettlementStage.Pending,
                    "world_settlement_pending")));
    }

    public static int FindDeliveryIndex(
        WorldSettlementRecord record,
        string operationId)
    {
        for (var index = 0;
             index < record.DeliveryStates.Count;
             index++)
        {
            if (string.Equals(
                    record.DeliveryStates[index].OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public static bool IsAllowedTransition(
        WorldSettlementStage current,
        WorldSettlementStage next)
    {
        return current == WorldSettlementStage.Pending
                   && next is WorldSettlementStage.Reconciliation
                       or WorldSettlementStage.Rejected
               || current == WorldSettlementStage.Reconciliation
                   && next is WorldSettlementStage.Applied
                       or WorldSettlementStage.Rejected;
    }

    public static void ValidateEnumerationLimit(int maxResults)
    {
        if (maxResults is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }
    }

    public static string EncodeCursor(string settlementId)
    {
        var id = RuntimeGuard.RequiredId(
            settlementId,
            nameof(settlementId));
        var encoded = Convert.ToBase64String(
                StrictUtf8Encoding.GetBytes(id))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var digest = CanonicalJsonDigest.ComputeSha256(
            JsonArrayBuilder.String(id));
        return string.Concat(encoded, ".", digest);
    }

    public static string DecodeCursor(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)
            || cursor.Length > 256)
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor));
        }

        var separator = cursor.LastIndexOf('.');
        if (separator < 1
            || separator != cursor.Length - 65
            || !CanonicalJsonDigest.IsSha256(cursor[(separator + 1)..]))
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor));
        }

        var encoded = cursor[..separator]
            .Replace('-', '+')
            .Replace('_', '/');
        var remainder = encoded.Length % 4;
        if (remainder == 1)
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor));
        }

        if (remainder != 0)
        {
            encoded = encoded.PadRight(
                encoded.Length + 4 - remainder,
                '=');
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor),
                exception);
        }

        string id;
        try
        {
            id = RuntimeGuard.RequiredId(decoded, nameof(cursor));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor),
                exception);
        }

        if (!string.Equals(
                EncodeCursor(id),
                cursor,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A settlement continuation cursor is invalid.",
                nameof(cursor));
        }

        return id;
    }

    public static MemoryMutation CloneMutation(MemoryMutation mutation)
    {
        return mutation.Kind == MemoryMutationKind.Delete
            ? MemoryMutation.Delete(mutation.MemoryId)
            : MemoryMutation.Upsert(CloneMemoryRecord(mutation.Record!));
    }

    public static MemoryRecord CloneMemoryRecord(
        MemoryRecord record,
        string? memoryId = null)
    {
        var provenance = record.Provenance is null
            ? null
            : new MemoryProvenance(
                record.Provenance.WorldId,
                record.Provenance.SessionId,
                record.Provenance.SaveRevision,
                record.Provenance.SourceRunId,
                record.Provenance.SourceEventId,
                record.Provenance.Committed,
                record.Provenance.TimelineId,
                record.Provenance.Perspective is null
                    ? null
                    : new GameKnowledgePerspective(
                        CloneIdentity(
                            record.Provenance.Perspective.Observer),
                        record.Provenance.Perspective.KnowledgeKind,
                        record.Provenance.Perspective.Source is null
                            ? null
                            : CloneIdentity(
                                record.Provenance.Perspective.Source)),
                record.Provenance.TimelineEpoch);
        var window = record.GameTimeWindow is null
            ? null
            : new GameTimeWindow(
                record.GameTimeWindow.ValidFrom is null
                    ? null
                    : CloneTime(record.GameTimeWindow.ValidFrom),
                record.GameTimeWindow.ValidUntil is null
                    ? null
                    : CloneTime(record.GameTimeWindow.ValidUntil));
        return new MemoryRecord(
            memoryId ?? record.MemoryId,
            record.Scope,
            record.Content,
            record.Tags,
            record.Importance,
            record.CreatedAt,
            record.UpdatedAt,
            record.ExpiresAt,
            provenance,
            window);
    }

    public static GroupInteractionAppendRequest CloneGroupRequest(
        GroupInteractionAppendRequest request)
    {
        return new GroupInteractionAppendRequest(
            request.OperationId,
            request.SessionId,
            request.ExpectedRevision,
            request.ExpectedMembershipRevision,
            request.Messages.Select(CloneGroupDraft));
    }

    public static GroupInteractionMessageDraft CloneGroupDraft(
        GroupInteractionMessageDraft draft)
    {
        return new GroupInteractionMessageDraft(
            draft.MessageId,
            draft.Kind,
            draft.Payload,
            draft.AudienceMode,
            draft.Author is null ? null : CloneIdentity(draft.Author),
            draft.Audience.Select(CloneIdentity),
            draft.CausationId);
    }

    public static GroupInteractionMember[] CopyGroupMembers(
        IEnumerable<GroupInteractionMember> members,
        string parameterName)
    {
        var copied = RuntimeInputGuard.CopyBounded(
            members ?? throw new ArgumentNullException(parameterName),
            4_096,
            item => new GroupInteractionMember(
                item?.Actor
                ?? throw new ArgumentException(
                    "Group members cannot contain null.",
                    parameterName),
                item.Roles),
            parameterName,
            "world_settlement_group_members_exceeded");
        Array.Sort(
            copied,
            static (left, right) =>
            {
                var byId = StringComparer.Ordinal.Compare(
                    left.Actor.EntityId,
                    right.Actor.EntityId);
                return byId != 0
                    ? byId
                    : left.Actor.Incarnation.CompareTo(
                        right.Actor.Incarnation);
            });
        for (var index = 1; index < copied.Length; index++)
        {
            if (string.Equals(
                    copied[index - 1].Actor.EntityId,
                    copied[index].Actor.EntityId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Expected group members cannot reuse an entity ID.",
                    parameterName);
            }
        }

        return copied;
    }

    public static GroupDeliverySnapshot SnapshotGroupDelivery(
        IEnumerable<GroupInteractionMember> expectedMembers,
        GroupInteractionAppendRequest request,
        string privacyClass,
        string redactionClass)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var members = CopyGroupMembers(
            expectedMembers,
            nameof(expectedMembers));
        return new GroupDeliverySnapshot(
            members,
            new WorldSettlementAudienceClaim(
                request.SessionId,
                request.ExpectedMembershipRevision,
                members.Select(item => item.Actor),
                privacyClass,
                redactionClass,
                maxMembers: 4_096));
    }

    public static bool SameMembers(
        IEnumerable<GroupInteractionMember> left,
        IEnumerable<GroupInteractionMember> right)
    {
        var leftArray = CopyGroupMembers(left, nameof(left));
        var rightArray = CopyGroupMembers(right, nameof(right));
        if (leftArray.Length != rightArray.Length)
        {
            return false;
        }

        for (var index = 0; index < leftArray.Length; index++)
        {
            if (!leftArray[index].Actor.IsSameIncarnation(
                    rightArray[index].Actor)
                || !leftArray[index].Roles.SequenceEqual(
                    rightArray[index].Roles,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool SameIdentities(
        IEnumerable<GameEntityIdentity> left,
        IEnumerable<GameEntityIdentity> right)
    {
        var leftArray = left
            .OrderBy(item => item.EntityId, StringComparer.Ordinal)
            .ThenBy(item => item.Incarnation)
            .ToArray();
        var rightArray = right
            .OrderBy(item => item.EntityId, StringComparer.Ordinal)
            .ThenBy(item => item.Incarnation)
            .ToArray();
        return leftArray.Length == rightArray.Length
               && leftArray.Zip(
                       rightArray,
                       static (first, second) =>
                           first.IsSameIncarnation(second))
                   .All(static item => item);
    }

    public static WorldPresentationDraft ClonePresentationDraft(
        WorldPresentationDraft draft)
    {
        return new WorldPresentationDraft(
            draft.PresentationId,
            draft.ContentRevision,
            draft.Source,
            draft.Binding,
            draft.Audience,
            draft.Content,
            draft.Provenance,
            WorldPresentationValidation.MaximumLimits);
    }

    public static WorldSettlementAudienceClaim AudienceFromPresentation(
        WorldPresentationDraft draft)
    {
        return new WorldSettlementAudienceClaim(
            draft.Audience.MembershipScopeId,
            draft.Audience.MembershipRevision,
            draft.Audience.Members,
            draft.Audience.PrivacyClass,
            draft.Audience.RedactionClass,
            maxMembers: 4_096);
    }

    public static void ValidateDeliveryBinding(
        WorldSettlementDelivery delivery,
        WorldPresentationSource source,
        WorldPresentationBinding binding)
    {
        switch (delivery)
        {
            case WorldSettlementMemoryDelivery memory:
                ValidateMemoryBinding(memory, source, binding);
                break;
            case WorldSettlementGroupDelivery group:
                foreach (var message in group.Request.Messages)
                {
                    if (!string.Equals(
                            message.CausationId,
                            source.WorldReceiptId,
                            StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "Every group settlement message must identify "
                            + "the exact world receipt as its causation.",
                            nameof(delivery));
                    }
                }

                break;
            case WorldSettlementPresentationDelivery presentation:
                if (!presentation.Draft.Source.IsSameAs(source)
                    || !presentation.Draft.Binding.IsSameAs(binding))
                {
                    throw new ArgumentException(
                        "A presentation settlement delivery must use the "
                        + "plan's exact source and world binding.",
                        nameof(delivery));
                }

                break;
            default:
                throw new ArgumentException(
                    "The settlement delivery kind is unsupported.",
                    nameof(delivery));
        }
    }

    private static void ValidateMemoryBinding(
        WorldSettlementMemoryDelivery delivery,
        WorldPresentationSource source,
        WorldPresentationBinding binding)
    {
        var owner = delivery.Audience.Members[0];
        foreach (var mutation in delivery.Mutations)
        {
            if (mutation.Kind == MemoryMutationKind.Delete)
            {
                continue;
            }

            var record = mutation.Record!;
            var provenance = record.Provenance;
            if (provenance is null
                || !provenance.Committed
                || !string.Equals(
                    provenance.WorldId,
                    binding.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    provenance.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || provenance.TimelineEpoch != binding.TimelineEpoch
                || provenance.SaveRevision != binding.SaveRevision
                || !string.Equals(
                    provenance.SourceEventId,
                    source.WorldReceiptId,
                    StringComparison.Ordinal)
                || provenance.Perspective is null
                || !provenance.Perspective.Observer.IsSameIncarnation(owner))
            {
                throw new ArgumentException(
                    "A private memory upsert must carry committed "
                    + "provenance for the exact receipt, coordinate, and "
                    + "observer incarnation.",
                    nameof(delivery));
            }

            if (binding.GameTime is not null
                && record.GameTimeWindow is null)
            {
                throw new ArgumentException(
                    "A timed settlement memory upsert requires a game-time "
                    + "window containing the committed receipt time.",
                    nameof(delivery));
            }

            if (record.GameTimeWindow is not null)
            {
                ValidateTime(record.GameTimeWindow.ValidFrom, binding);
                ValidateTime(record.GameTimeWindow.ValidUntil, binding);
                if (binding.GameTime is not null
                    && !record.GameTimeWindow.Contains(binding.GameTime))
                {
                    throw new ArgumentException(
                        "A settlement memory game-time window must contain "
                        + "the committed receipt time.",
                        nameof(delivery));
                }
            }
        }
    }

    private static void ValidateTime(
        GameTimePoint? time,
        WorldPresentationBinding binding)
    {
        if (time is not null
            && (!string.Equals(
                    time.TimelineId,
                    binding.TimelineId,
                    StringComparison.Ordinal)
                || time.Epoch != binding.TimelineEpoch))
        {
            throw new ArgumentException(
                "Settlement memory game time must use the exact world "
                + "timeline and epoch.",
                nameof(time));
        }
    }

    public static JsonElement IdentityToJson(GameEntityIdentity identity)
    {
        return JsonArrayBuilder.Object(
            ("entityId", JsonArrayBuilder.String(identity.EntityId)),
            ("incarnation", JsonArrayBuilder.Number(identity.Incarnation)));
    }

    public static JsonElement GroupMemberToJson(
        GroupInteractionMember member)
    {
        return JsonArrayBuilder.Object(
            ("actor", IdentityToJson(member.Actor)),
            ("roles", JsonArrayBuilder.Strings(member.Roles)));
    }

    public static JsonElement MutationToJson(MemoryMutation mutation)
    {
        return JsonArrayBuilder.Object(
            ("kind", JsonArrayBuilder.String(
                mutation.Kind == MemoryMutationKind.Upsert
                    ? "upsert"
                    : "delete")),
            ("memoryId", JsonArrayBuilder.String(mutation.MemoryId)),
            ("record", mutation.Record is null
                ? JsonArrayBuilder.Null()
                : MemoryRecordToJson(mutation.Record)));
    }

    public static JsonElement MemoryRecordToJson(MemoryRecord record)
    {
        return JsonArrayBuilder.Object(
            ("memoryId", JsonArrayBuilder.String(record.MemoryId)),
            ("scope", JsonArrayBuilder.String(record.Scope)),
            ("content", record.Content),
            ("tags", JsonArrayBuilder.Strings(record.Tags)),
            ("importance", JsonArrayBuilder.Number(record.Importance)),
            ("createdAt", JsonArrayBuilder.String(
                record.CreatedAt.ToString("O", CultureInfo.InvariantCulture))),
            ("updatedAt", JsonArrayBuilder.String(
                record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))),
            ("expiresAt", record.ExpiresAt.HasValue
                ? JsonArrayBuilder.String(
                    record.ExpiresAt.Value.ToString(
                        "O",
                        CultureInfo.InvariantCulture))
                : JsonArrayBuilder.Null()),
            ("provenance", record.Provenance is null
                ? JsonArrayBuilder.Null()
                : MemoryProvenanceToJson(record.Provenance)),
            ("gameTimeWindow", record.GameTimeWindow is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.Object(
                    ("validFrom", record.GameTimeWindow.ValidFrom is null
                        ? JsonArrayBuilder.Null()
                        : TimeToJson(record.GameTimeWindow.ValidFrom)),
                    ("validUntil", record.GameTimeWindow.ValidUntil is null
                        ? JsonArrayBuilder.Null()
                        : TimeToJson(record.GameTimeWindow.ValidUntil)))));
    }

    private static JsonElement MemoryProvenanceToJson(
        MemoryProvenance provenance)
    {
        return JsonArrayBuilder.Object(
            ("worldId", JsonArrayBuilder.String(provenance.WorldId)),
            ("sessionId", provenance.SessionId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(provenance.SessionId)),
            ("saveRevision", JsonArrayBuilder.Number(
                provenance.SaveRevision)),
            ("sourceRunId", JsonArrayBuilder.String(
                provenance.SourceRunId)),
            ("sourceEventId", JsonArrayBuilder.String(
                provenance.SourceEventId)),
            ("committed", JsonArrayBuilder.Boolean(provenance.Committed)),
            ("timelineId", provenance.TimelineId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(provenance.TimelineId)),
            ("timelineEpoch", provenance.TimelineEpoch.HasValue
                ? JsonArrayBuilder.Number(
                    provenance.TimelineEpoch.Value)
                : JsonArrayBuilder.Null()),
            ("perspective", provenance.Perspective is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.Object(
                    ("observer", IdentityToJson(
                        provenance.Perspective.Observer)),
                    ("knowledgeKind", JsonArrayBuilder.String(
                        provenance.Perspective.KnowledgeKind)),
                    ("source", provenance.Perspective.Source is null
                        ? JsonArrayBuilder.Null()
                        : IdentityToJson(
                            provenance.Perspective.Source)))));
    }

    public static JsonElement GroupRequestToJson(
        GroupInteractionAppendRequest request)
    {
        return JsonArrayBuilder.Object(
            ("operationId", JsonArrayBuilder.String(request.OperationId)),
            ("sessionId", JsonArrayBuilder.String(request.SessionId)),
            ("expectedRevision", JsonArrayBuilder.Number(
                request.ExpectedRevision)),
            ("expectedMembershipRevision", JsonArrayBuilder.Number(
                request.ExpectedMembershipRevision)),
            ("messages", JsonArrayBuilder.Array(
                request.Messages.Select(
                    static message => JsonArrayBuilder.Object(
                        ("messageId", JsonArrayBuilder.String(
                            message.MessageId)),
                        ("kind", JsonArrayBuilder.String(message.Kind)),
                        ("payload", message.Payload),
                        ("audienceMode", JsonArrayBuilder.String(
                            message.AudienceMode)),
                        ("author", message.Author is null
                            ? JsonArrayBuilder.Null()
                            : IdentityToJson(message.Author)),
                        ("audience", JsonArrayBuilder.Array(
                            message.Audience.Select(IdentityToJson))),
                        ("causationId", message.CausationId is null
                            ? JsonArrayBuilder.Null()
                            : JsonArrayBuilder.String(
                                message.CausationId)))))));
    }

    public static JsonElement PresentationToJson(
        WorldPresentationDraft draft)
    {
        return JsonArrayBuilder.Object(
            ("presentationId", JsonArrayBuilder.String(
                draft.PresentationId)),
            ("contentRevision", JsonArrayBuilder.Number(
                draft.ContentRevision)),
            ("source", draft.Source.ToJson()),
            ("binding", draft.Binding.ToJson()),
            ("audience", draft.Audience.ToJson()),
            ("content", draft.Content.ToJson()),
            ("provenance", draft.Provenance.ToJson()));
    }

    private static JsonElement TimeToJson(GameTimePoint time)
    {
        return JsonArrayBuilder.Object(
            ("clockId", JsonArrayBuilder.String(time.ClockId)),
            ("timelineId", JsonArrayBuilder.String(time.TimelineId)),
            ("epoch", JsonArrayBuilder.Number(time.Epoch)),
            ("tick", JsonArrayBuilder.Number(time.Tick)));
    }

    private static GameEntityIdentity CloneIdentity(
        GameEntityIdentity identity)
    {
        return new GameEntityIdentity(
            identity.EntityId,
            identity.Incarnation);
    }

    private static GameTimePoint CloneTime(GameTimePoint time)
    {
        return new GameTimePoint(
            time.ClockId,
            time.TimelineId,
            time.Epoch,
            time.Tick);
    }

    internal sealed class GroupDeliverySnapshot
    {
        public GroupDeliverySnapshot(
            GroupInteractionMember[] members,
            WorldSettlementAudienceClaim audience)
        {
            Members = members;
            Audience = audience;
        }

        public GroupInteractionMember[] Members { get; }

        public WorldSettlementAudienceClaim Audience { get; }
    }
}
