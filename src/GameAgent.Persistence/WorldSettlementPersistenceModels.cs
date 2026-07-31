using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Core;

namespace GameAgent.Persistence;

internal sealed class WorldSettlementFrameRecord
{
    [JsonRequired]
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonRequired]
    [JsonPropertyName("storeRevision")]
    public long StoreRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("previousFrameDigest")]
    public string PreviousFrameDigest { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("record")]
    public PersistedWorldSettlementRecord? Record { get; set; }
}

internal sealed class PersistedWorldSettlementRecord
{
    [JsonRequired]
    [JsonPropertyName("plan")]
    public PersistedWorldSettlementPlan? Plan { get; set; }

    [JsonRequired]
    [JsonPropertyName("planDigest")]
    public string PlanDigest { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonRequired]
    [JsonPropertyName("deliveryStates")]
    public List<PersistedWorldSettlementDeliveryState> DeliveryStates
    {
        get;
        set;
    } = new();

    public static PersistedWorldSettlementRecord FromRecord(
        WorldSettlementRecord record)
    {
        return new PersistedWorldSettlementRecord
        {
            Plan = PersistedWorldSettlementPlan.FromPlan(record.Plan),
            PlanDigest = record.Plan.SemanticDigest,
            Revision = record.Revision,
            DeliveryStates = record.DeliveryStates
                .Select(PersistedWorldSettlementDeliveryState.FromState)
                .ToList()
        };
    }

    public WorldSettlementRecord Restore()
    {
        var plan = (Plan
                    ?? throw new JsonException(
                        "A persisted settlement requires a plan."))
            .Restore();
        if (!string.Equals(
                plan.SemanticDigest,
                PlanDigest,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                "The persisted settlement plan digest is invalid.");
        }

        if (DeliveryStates is null)
        {
            throw new JsonException(
                "Persisted settlement delivery states cannot be null.");
        }

        return new WorldSettlementRecord(
            plan,
            Revision,
            DeliveryStates.Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted settlement states cannot contain "
                             + "null."))
                    .Restore()));
    }
}

internal sealed class PersistedWorldSettlementDeliveryState
{
    [JsonRequired]
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("kind")]
    public int Kind { get; set; }

    [JsonRequired]
    [JsonPropertyName("stage")]
    public int Stage { get; set; }

    [JsonRequired]
    [JsonPropertyName("reasonCode")]
    public string ReasonCode { get; set; } = string.Empty;

    public static PersistedWorldSettlementDeliveryState FromState(
        WorldSettlementDeliveryState state)
    {
        return new PersistedWorldSettlementDeliveryState
        {
            OperationId = state.OperationId,
            Kind = (int)state.Kind,
            Stage = (int)state.Stage,
            ReasonCode = state.ReasonCode
        };
    }

    public WorldSettlementDeliveryState Restore()
    {
        return new WorldSettlementDeliveryState(
            OperationId,
            (WorldSettlementSinkKind)Kind,
            (WorldSettlementStage)Stage,
            ReasonCode);
    }
}

internal sealed class PersistedWorldSettlementPlan
{
    [JsonRequired]
    [JsonPropertyName("settlementId")]
    public string SettlementId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("source")]
    public PersistedWorldPresentationSource? Source { get; set; }

    [JsonRequired]
    [JsonPropertyName("binding")]
    public PersistedWorldPresentationBinding? Binding { get; set; }

    [JsonRequired]
    [JsonPropertyName("evidenceDigest")]
    public string EvidenceDigest { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("evidenceOutcomeCode")]
    public string EvidenceOutcomeCode { get; set; } = string.Empty;

    [JsonPropertyName("receiptEvidence")]
    public JsonElement? ReceiptEvidence { get; set; }

    [JsonRequired]
    [JsonPropertyName("deliveries")]
    public List<PersistedWorldSettlementDelivery> Deliveries { get; set; } =
        new();

    public static PersistedWorldSettlementPlan FromPlan(
        WorldSettlementPlan plan)
    {
        return new PersistedWorldSettlementPlan
        {
            SettlementId = plan.SettlementId,
            Source = PersistedWorldPresentationSource.FromSource(
                plan.Source),
            Binding = PersistedWorldPresentationBinding.FromBinding(
                plan.Binding),
            EvidenceDigest = plan.EvidenceDigest,
            EvidenceOutcomeCode = plan.Evidence.OutcomeCode,
            ReceiptEvidence = plan.Evidence.ReceiptEvidence?.Clone(),
            Deliveries = plan.Deliveries
                .Select(PersistedWorldSettlementDelivery.FromDelivery)
                .ToList()
        };
    }

    public WorldSettlementPlan Restore()
    {
        if (Deliveries is null)
        {
            throw new JsonException(
                "Persisted settlement deliveries cannot be null.");
        }

        var evidence = new CommittedWorldPresentationEvidence(
            (Source
             ?? throw new JsonException(
                 "A persisted settlement requires a source."))
            .Restore(),
            (Binding
             ?? throw new JsonException(
                 "A persisted settlement requires a binding."))
            .Restore(),
            WorldPresentationCommitStatus.Applied,
            EvidenceOutcomeCode,
            ReceiptEvidence);
        if (!string.Equals(
                evidence.SemanticDigest,
                EvidenceDigest,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                "The persisted settlement evidence digest is invalid.");
        }

        return new WorldSettlementPlan(
            SettlementId,
            evidence,
            Deliveries.Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted settlement deliveries cannot "
                             + "contain null."))
                    .Restore()),
            new WorldSettlementLimits(
                maxDeliveries: 4_096,
                maxAudienceMembers: 4_096,
                maxAggregateUtf8Bytes: 32 * 1_048_576,
                maxAggregateJsonNodes: 1_000_000));
    }
}

internal sealed class PersistedWorldSettlementDelivery
{
    [JsonRequired]
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("audience")]
    public PersistedWorldSettlementAudience? Audience { get; set; }

    [JsonPropertyName("memoryMutations")]
    public List<MemoryFrameMutation>? MemoryMutations { get; set; }

    [JsonPropertyName("expectedGroupId")]
    public string? ExpectedGroupId { get; set; }

    [JsonPropertyName("expectedMembers")]
    public List<PersistedGroupInteractionMember>? ExpectedMembers
    {
        get;
        set;
    }

    [JsonPropertyName("groupRequest")]
    public PersistedWorldSettlementGroupRequest? GroupRequest { get; set; }

    [JsonPropertyName("presentation")]
    public PersistedWorldSettlementPresentation? Presentation { get; set; }

    public static PersistedWorldSettlementDelivery FromDelivery(
        WorldSettlementDelivery delivery)
    {
        var persisted = new PersistedWorldSettlementDelivery
        {
            Kind = delivery.Kind switch
            {
                WorldSettlementSinkKind.Memory => "memory",
                WorldSettlementSinkKind.Group => "group",
                WorldSettlementSinkKind.Presentation => "presentation",
                _ => throw new InvalidOperationException(
                    "Unsupported settlement delivery kind.")
            },
            OperationId = delivery.OperationId,
            Audience = PersistedWorldSettlementAudience.FromAudience(
                delivery.Audience)
        };
        switch (delivery)
        {
            case WorldSettlementMemoryDelivery memory:
                persisted.MemoryMutations = memory.Mutations
                    .Select(
                        item => new MemoryFrameMutation
                        {
                            Operation = item.Kind
                                == MemoryMutationKind.Upsert
                                ? "upsert"
                                : "delete",
                            MemoryId = item.MemoryId,
                            Record = item.Record is null
                                ? null
                                : PersistedMemoryRecord.FromMemoryRecord(
                                    item.Record)
                        })
                    .ToList();
                break;
            case WorldSettlementGroupDelivery group:
                persisted.ExpectedGroupId = group.ExpectedGroupId;
                persisted.ExpectedMembers = group.ExpectedMembers
                    .Select(PersistedGroupInteractionMember.FromMember)
                    .ToList();
                persisted.GroupRequest =
                    PersistedWorldSettlementGroupRequest.FromRequest(
                        group.Request);
                break;
            case WorldSettlementPresentationDelivery presentation:
                persisted.Presentation =
                    PersistedWorldSettlementPresentation.FromDelivery(
                        presentation);
                break;
        }

        return persisted;
    }

    public WorldSettlementDelivery Restore()
    {
        var audience = (Audience
                        ?? throw new JsonException(
                            "A persisted settlement delivery requires an "
                            + "audience."))
            .Restore();
        switch (Kind)
        {
            case "memory":
                if (MemoryMutations is null
                    || ExpectedGroupId is not null
                    || ExpectedMembers is not null
                    || GroupRequest is not null
                    || Presentation is not null)
                {
                    throw new JsonException(
                        "Persisted memory settlement fields are invalid.");
                }

                return new WorldSettlementMemoryDelivery(
                    OperationId,
                    audience,
                    MemoryMutations.Select(RestoreMutation).ToArray());
            case "group":
                if (MemoryMutations is not null
                    || ExpectedGroupId is null
                    || ExpectedMembers is null
                    || GroupRequest is null
                    || Presentation is not null)
                {
                    throw new JsonException(
                        "Persisted group settlement fields are invalid.");
                }

                return new WorldSettlementGroupDelivery(
                    OperationId,
                    ExpectedGroupId,
                    ExpectedMembers.Select(
                        item => (item
                                 ?? throw new JsonException(
                                     "Persisted expected group members "
                                     + "cannot contain null."))
                            .ToMember()),
                    GroupRequest.Restore(),
                    audience);
            case "presentation":
                if (MemoryMutations is not null
                    || ExpectedGroupId is not null
                    || ExpectedMembers is not null
                    || GroupRequest is not null
                    || Presentation is null)
                {
                    throw new JsonException(
                        "Persisted presentation settlement fields are "
                        + "invalid.");
                }

                var restoredPresentation =
                    Presentation.Restore(OperationId);
                if (!string.Equals(
                        audience.SemanticDigest,
                        restoredPresentation.Audience.SemanticDigest,
                        StringComparison.Ordinal))
                {
                    throw new JsonException(
                        "The persisted settlement presentation audience "
                        + "does not match its delivery audience.");
                }

                return restoredPresentation;
            default:
                throw new JsonException(
                    "The persisted settlement delivery kind is invalid.");
        }
    }

    private static MemoryMutation RestoreMutation(MemoryFrameMutation value)
    {
        if (value is null)
        {
            throw new JsonException(
                "Persisted memory mutations cannot contain null.");
        }

        return value.Operation switch
        {
            "upsert" when value.Record is not null
                && string.Equals(
                    value.MemoryId,
                    value.Record.MemoryId,
                    StringComparison.Ordinal) =>
                MemoryMutation.Upsert(value.Record.ToMemoryRecord()),
            "delete" when value.Record is null =>
                MemoryMutation.Delete(
                    value.MemoryId
                    ?? throw new JsonException(
                        "A persisted memory delete requires an ID.")),
            _ => throw new JsonException(
                "A persisted settlement memory mutation is invalid.")
        };
    }
}

internal sealed class PersistedWorldSettlementAudience
{
    [JsonRequired]
    [JsonPropertyName("membershipScopeId")]
    public string MembershipScopeId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("membershipRevision")]
    public long MembershipRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("members")]
    public List<PersistedPresentationIdentity> Members { get; set; } =
        new();

    [JsonRequired]
    [JsonPropertyName("privacyClass")]
    public string PrivacyClass { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("redactionClass")]
    public string RedactionClass { get; set; } = string.Empty;

    public static PersistedWorldSettlementAudience FromAudience(
        WorldSettlementAudienceClaim audience)
    {
        return new PersistedWorldSettlementAudience
        {
            MembershipScopeId = audience.MembershipScopeId,
            MembershipRevision = audience.MembershipRevision,
            Members = audience.Members
                .Select(PersistedPresentationIdentity.FromIdentity)
                .ToList(),
            PrivacyClass = audience.PrivacyClass,
            RedactionClass = audience.RedactionClass
        };
    }

    public WorldSettlementAudienceClaim Restore()
    {
        if (Members is null)
        {
            throw new JsonException(
                "Persisted settlement audience members cannot be null.");
        }

        return new WorldSettlementAudienceClaim(
            MembershipScopeId,
            MembershipRevision,
            Members.Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted settlement audiences cannot "
                             + "contain null members."))
                    .Restore()),
            PrivacyClass,
            RedactionClass,
            maxMembers: 4_096);
    }
}

internal sealed class PersistedWorldSettlementGroupRequest
{
    [JsonRequired]
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("expectedRevision")]
    public long ExpectedRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("expectedMembershipRevision")]
    public long ExpectedMembershipRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("messages")]
    public List<PersistedWorldSettlementGroupMessage> Messages { get; set; } =
        new();

    public static PersistedWorldSettlementGroupRequest FromRequest(
        GroupInteractionAppendRequest request)
    {
        return new PersistedWorldSettlementGroupRequest
        {
            OperationId = request.OperationId,
            SessionId = request.SessionId,
            ExpectedRevision = request.ExpectedRevision,
            ExpectedMembershipRevision =
                request.ExpectedMembershipRevision,
            Messages = request.Messages
                .Select(PersistedWorldSettlementGroupMessage.FromMessage)
                .ToList()
        };
    }

    public GroupInteractionAppendRequest Restore()
    {
        if (Messages is null)
        {
            throw new JsonException(
                "Persisted settlement group messages cannot be null.");
        }

        return new GroupInteractionAppendRequest(
            OperationId,
            SessionId,
            ExpectedRevision,
            ExpectedMembershipRevision,
            Messages.Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted settlement group messages cannot "
                             + "contain null."))
                    .Restore()));
    }
}

internal sealed class PersistedWorldSettlementGroupMessage
{
    [JsonRequired]
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonRequired]
    [JsonPropertyName("audienceMode")]
    public string AudienceMode { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public PersistedGameEntityIdentity? Author { get; set; }

    [JsonRequired]
    [JsonPropertyName("audience")]
    public List<PersistedGameEntityIdentity> Audience { get; set; } = new();

    [JsonPropertyName("causationId")]
    public string? CausationId { get; set; }

    public static PersistedWorldSettlementGroupMessage FromMessage(
        GroupInteractionMessageDraft message)
    {
        return new PersistedWorldSettlementGroupMessage
        {
            MessageId = message.MessageId,
            Kind = message.Kind,
            Payload = message.Payload.Clone(),
            AudienceMode = message.AudienceMode,
            Author = message.Author is null
                ? null
                : PersistedGameEntityIdentity.FromIdentity(message.Author),
            Audience = message.Audience
                .Select(PersistedGameEntityIdentity.FromIdentity)
                .ToList(),
            CausationId = message.CausationId
        };
    }

    public GroupInteractionMessageDraft Restore()
    {
        if (Audience is null)
        {
            throw new JsonException(
                "Persisted settlement message audience cannot be null.");
        }

        return new GroupInteractionMessageDraft(
            MessageId,
            Kind,
            Payload,
            AudienceMode,
            Author?.ToIdentity(),
            Audience.Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted settlement message audience "
                             + "cannot contain null."))
                    .ToIdentity()),
            CausationId);
    }
}

internal sealed class PersistedWorldSettlementPresentation
{
    [JsonRequired]
    [JsonPropertyName("expectedPreviousContentRevision")]
    public long ExpectedPreviousContentRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("presentationId")]
    public string PresentationId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("contentRevision")]
    public long ContentRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("source")]
    public PersistedWorldPresentationSource? Source { get; set; }

    [JsonRequired]
    [JsonPropertyName("binding")]
    public PersistedWorldPresentationBinding? Binding { get; set; }

    [JsonRequired]
    [JsonPropertyName("audience")]
    public PersistedWorldPresentationAudience? Audience { get; set; }

    [JsonRequired]
    [JsonPropertyName("content")]
    public PersistedWorldPresentationContent? Content { get; set; }

    [JsonRequired]
    [JsonPropertyName("provenance")]
    public PersistedWorldPresentationProvenance? Provenance { get; set; }

    public static PersistedWorldSettlementPresentation FromDelivery(
        WorldSettlementPresentationDelivery delivery)
    {
        return new PersistedWorldSettlementPresentation
        {
            ExpectedPreviousContentRevision =
                delivery.ExpectedPreviousContentRevision,
            PresentationId = delivery.Draft.PresentationId,
            ContentRevision = delivery.Draft.ContentRevision,
            Source = PersistedWorldPresentationSource.FromSource(
                delivery.Draft.Source),
            Binding = PersistedWorldPresentationBinding.FromBinding(
                delivery.Draft.Binding),
            Audience = PersistedWorldPresentationAudience.FromAudience(
                delivery.Draft.Audience),
            Content = PersistedWorldPresentationContent.FromContent(
                delivery.Draft.Content),
            Provenance =
                PersistedWorldPresentationProvenance.FromProvenance(
                    delivery.Draft.Provenance)
        };
    }

    public WorldSettlementPresentationDelivery Restore(string operationId)
    {
        var draft = new WorldPresentationDraft(
            PresentationId,
            ContentRevision,
            (Source
             ?? throw new JsonException(
                 "A persisted settlement presentation requires a source."))
            .Restore(),
            (Binding
             ?? throw new JsonException(
                 "A persisted settlement presentation requires a binding."))
            .Restore(),
            (Audience
             ?? throw new JsonException(
                 "A persisted settlement presentation requires an "
                 + "audience."))
            .Restore(),
            (Content
             ?? throw new JsonException(
                 "A persisted settlement presentation requires content."))
            .Restore(),
            (Provenance
             ?? throw new JsonException(
                 "A persisted settlement presentation requires "
                 + "provenance."))
            .Restore(),
            WorldPresentationValidation.MaximumLimits);
        return new WorldSettlementPresentationDelivery(
            operationId,
            draft,
            ExpectedPreviousContentRevision);
    }
}
