using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public static class WorldPresentationWriteStatuses
{
    public const string Applied = "applied";

    public const string Idempotent = "idempotent";

    public const string RevisionConflict = "revision_conflict";

    public const string PresentationConflict = "presentation_conflict";
}

public enum WorldPresentationCommitStatus
{
    Applied = 0
}

/// <summary>
/// Bounded resource limits for durable, non-authoritative presentation data.
/// These limits are deliberately independent of a particular engine or UI.
/// </summary>
public sealed class WorldPresentationLimits
{
    public WorldPresentationLimits(
        int maxAudienceMembers = 512,
        int maxMediaCues = 64,
        int maxParentPresentationIds = 64,
        int maxPayloadUtf8Bytes = 262_144,
        int maxMetadataUtf8Bytes = 65_536,
        int maxJsonDepth = 32,
        int maxJsonNodes = 8_192,
        int maxAggregateUtf8Bytes = 8 * 1_048_576,
        int maxAggregateJsonNodes = 65_536)
    {
        if (maxAudienceMembers is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAudienceMembers));
        }

        if (maxMediaCues is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMediaCues));
        }

        if (maxParentPresentationIds is < 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxParentPresentationIds));
        }

        if (maxPayloadUtf8Bytes is < 1_024 or > 4 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPayloadUtf8Bytes));
        }

        if (maxMetadataUtf8Bytes is < 1_024 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMetadataUtf8Bytes));
        }

        if (maxJsonDepth is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJsonDepth));
        }

        if (maxJsonNodes is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJsonNodes));
        }

        if (maxAggregateUtf8Bytes < maxPayloadUtf8Bytes
            || maxAggregateUtf8Bytes < maxMetadataUtf8Bytes
            || maxAggregateUtf8Bytes > 32 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAggregateUtf8Bytes));
        }

        if (maxAggregateJsonNodes < maxJsonNodes
            || maxAggregateJsonNodes > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAggregateJsonNodes));
        }

        MaxAudienceMembers = maxAudienceMembers;
        MaxMediaCues = maxMediaCues;
        MaxParentPresentationIds = maxParentPresentationIds;
        MaxPayloadUtf8Bytes = maxPayloadUtf8Bytes;
        MaxMetadataUtf8Bytes = maxMetadataUtf8Bytes;
        MaxJsonDepth = maxJsonDepth;
        MaxJsonNodes = maxJsonNodes;
        MaxAggregateUtf8Bytes = maxAggregateUtf8Bytes;
        MaxAggregateJsonNodes = maxAggregateJsonNodes;
    }

    public int MaxAudienceMembers { get; }

    public int MaxMediaCues { get; }

    public int MaxParentPresentationIds { get; }

    public int MaxPayloadUtf8Bytes { get; }

    public int MaxMetadataUtf8Bytes { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonNodes { get; }

    public int MaxAggregateUtf8Bytes { get; }

    public int MaxAggregateJsonNodes { get; }
}

/// <summary>
/// Exact authoritative coordinate to which a presentation is attached.
/// A timeline fork, epoch reset, save fork, state advance, or catalog change
/// is a different binding.
/// </summary>
public sealed class WorldPresentationBinding
{
    public WorldPresentationBinding(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        long stateVersion,
        string catalogDigest,
        GameTimePoint? gameTime = null,
        string? committedStateDigest = null)
    {
        WorldId = RuntimeGuard.RequiredUtf8(worldId, 128, nameof(worldId));
        TimelineId = RuntimeGuard.RequiredUtf8(
            timelineId,
            128,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        if (stateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateVersion));
        }

        if (!CanonicalJsonDigest.IsSha256(catalogDigest))
        {
            throw new ArgumentException(
                "The catalog digest must be a lowercase SHA-256 digest.",
                nameof(catalogDigest));
        }

        if (committedStateDigest is not null
            && !CanonicalJsonDigest.IsSha256(committedStateDigest))
        {
            throw new ArgumentException(
                "The committed-state digest must be a lowercase SHA-256 "
                + "digest.",
                nameof(committedStateDigest));
        }

        if (gameTime is not null
            && (!string.Equals(
                    timelineId,
                    gameTime.TimelineId,
                    StringComparison.Ordinal)
                || gameTime.Epoch != timelineEpoch))
        {
            throw new ArgumentException(
                "Game time must use the presentation timeline and epoch.",
                nameof(gameTime));
        }

        TimelineEpoch = timelineEpoch;
        SaveRevision = saveRevision;
        StateVersion = stateVersion;
        CatalogDigest = catalogDigest;
        GameTime = gameTime is null
            ? null
            : WorldPresentationValidation.CloneTime(gameTime);
        CommittedStateDigest = committedStateDigest;
        SemanticDigest =
            WorldPresentationValidation.ComputeSemanticDigest(ToJson());
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public long StateVersion { get; }

    public string CatalogDigest { get; }

    public GameTimePoint? GameTime { get; }

    public string? CommittedStateDigest { get; }

    public string SemanticDigest { get; }

    public bool IsSameAs(WorldPresentationBinding? other)
    {
        return other is not null
               && string.Equals(
                   SemanticDigest,
                   other.SemanticDigest,
                   StringComparison.Ordinal);
    }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("worldId", JsonArrayBuilder.String(WorldId)),
            ("timelineId", JsonArrayBuilder.String(TimelineId)),
            ("timelineEpoch", JsonArrayBuilder.Number(TimelineEpoch)),
            ("saveRevision", JsonArrayBuilder.Number(SaveRevision)),
            ("stateVersion", JsonArrayBuilder.Number(StateVersion)),
            ("catalogDigest", JsonArrayBuilder.String(CatalogDigest)),
            ("gameTime", GameTime is null
                ? JsonArrayBuilder.Null()
                : WorldPresentationValidation.TimeToJson(GameTime)),
            ("committedStateDigest", CommittedStateDigest is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(CommittedStateDigest)));
    }
}

/// <summary>
/// Identifiers carried by the authoritative receipt that caused a
/// presentation. The world receipt is mandatory; an event occurrence or
/// action is optional, because purely systemic commits are also valid.
/// </summary>
public sealed class WorldPresentationSource
{
    public WorldPresentationSource(
        string worldReceiptId,
        string worldReceiptDigest,
        string? occurrenceId = null,
        string? actionId = null,
        string? operationId = null)
    {
        WorldReceiptId = RuntimeGuard.RequiredUtf8(
            worldReceiptId,
            128,
            nameof(worldReceiptId));
        if (!CanonicalJsonDigest.IsSha256(worldReceiptDigest))
        {
            throw new ArgumentException(
                "The world-receipt digest must be a lowercase SHA-256 "
                + "digest.",
                nameof(worldReceiptDigest));
        }

        WorldReceiptDigest = worldReceiptDigest;
        OccurrenceId = WorldPresentationValidation.OptionalId(
            occurrenceId,
            nameof(occurrenceId));
        ActionId = WorldPresentationValidation.OptionalId(
            actionId,
            nameof(actionId));
        OperationId = operationId is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                operationId,
                192,
                nameof(operationId));
        SemanticDigest =
            WorldPresentationValidation.ComputeSemanticDigest(ToJson());
    }

    public string WorldReceiptId { get; }

    public string WorldReceiptDigest { get; }

    public string? OccurrenceId { get; }

    public string? ActionId { get; }

    public string? OperationId { get; }

    public string SemanticDigest { get; }

    public bool IsSameAs(WorldPresentationSource? other)
    {
        return other is not null
               && string.Equals(
                   SemanticDigest,
                   other.SemanticDigest,
                   StringComparison.Ordinal);
    }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("worldReceiptId", JsonArrayBuilder.String(WorldReceiptId)),
            ("worldReceiptDigest", JsonArrayBuilder.String(
                WorldReceiptDigest)),
            ("occurrenceId", OccurrenceId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(OccurrenceId)),
            ("actionId", ActionId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(ActionId)),
            ("operationId", OperationId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(OperationId)));
    }
}

/// <summary>
/// Evidence read from an authoritative world receipt ledger. Implementations
/// should construct this only after the referenced receipt is terminal and
/// applied. Presentation data never becomes authoritative evidence itself.
/// </summary>
public sealed class CommittedWorldPresentationEvidence
{
    public CommittedWorldPresentationEvidence(
        WorldPresentationSource source,
        WorldPresentationBinding binding,
        WorldPresentationCommitStatus commitStatus,
        string outcomeCode,
        JsonElement? receiptEvidence = null)
    {
        Source = WorldPresentationValidation.CloneSource(
            source ?? throw new ArgumentNullException(nameof(source)));
        Binding = WorldPresentationValidation.CloneBinding(
            binding ?? throw new ArgumentNullException(nameof(binding)));
        if (commitStatus != WorldPresentationCommitStatus.Applied)
        {
            throw new ArgumentOutOfRangeException(nameof(commitStatus));
        }

        CommitStatus = commitStatus;
        OutcomeCode = RuntimeGuard.RequiredUtf8(
            outcomeCode,
            128,
            nameof(outcomeCode));
        if (receiptEvidence.HasValue)
        {
            WorldPresentationValidation.ValidateMetadata(
                receiptEvidence.Value,
                new WorldPresentationLimits(),
                nameof(receiptEvidence));
            ReceiptEvidence = receiptEvidence.Value.Clone();
        }

        SemanticDigest = WorldPresentationValidation.ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("source", Source.ToJson()),
                ("binding", Binding.ToJson()),
                ("commitStatus", JsonArrayBuilder.String("applied")),
                ("outcomeCode", JsonArrayBuilder.String(OutcomeCode)),
                ("receiptEvidence", ReceiptEvidence.HasValue
                    ? ReceiptEvidence.Value
                    : JsonArrayBuilder.Null())));
    }

    public WorldPresentationSource Source { get; }

    public WorldPresentationBinding Binding { get; }

    public WorldPresentationCommitStatus CommitStatus { get; }

    public string OutcomeCode { get; }

    public JsonElement? ReceiptEvidence { get; }

    public string SemanticDigest { get; }
}

/// <summary>
/// Trusted boundary used to read committed receipt evidence. Returning
/// <see langword="null"/> means the receipt is missing, unresolved, rejected,
/// or otherwise not safe to present.
/// </summary>
public interface ICommittedWorldPresentationEvidenceSource
{
    ValueTask<CommittedWorldPresentationEvidence?> ReadCommittedAsync(
        string worldReceiptId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Frozen membership snapshot and disclosure classification for one
/// presentation revision.
/// </summary>
public sealed class WorldPresentationAudience
{
    public WorldPresentationAudience(
        string membershipScopeId,
        long membershipRevision,
        IEnumerable<GameEntityIdentity> members,
        string privacyClass,
        string redactionClass,
        WorldPresentationLimits? limits = null)
    {
        var admittedLimits = limits ?? new WorldPresentationLimits();
        MembershipScopeId = RuntimeGuard.RequiredUtf8(
            membershipScopeId,
            128,
            nameof(membershipScopeId));
        if (membershipRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(membershipRevision));
        }

        MembershipRevision = membershipRevision;
        Members = WorldPresentationValidation.CopyIdentities(
            members,
            admittedLimits.MaxAudienceMembers,
            nameof(members));
        if (Members.Count == 0)
        {
            throw new ArgumentException(
                "A durable presentation requires at least one exact "
                + "audience member.",
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
        SemanticDigest =
            WorldPresentationValidation.ComputeSemanticDigest(ToJson());
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
                Members.Select(
                    static member => JsonArrayBuilder.Object(
                        ("entityId", JsonArrayBuilder.String(
                            member.EntityId)),
                        ("incarnation", JsonArrayBuilder.Number(
                            member.Incarnation)))))),
            ("privacyClass", JsonArrayBuilder.String(PrivacyClass)),
            ("redactionClass", JsonArrayBuilder.String(RedactionClass)));
    }
}

public sealed class WorldPresentationLocalization
{
    public WorldPresentationLocalization(
        string key,
        string defaultLocale,
        JsonElement arguments,
        string? fallbackText = null,
        WorldPresentationLimits? limits = null)
    {
        Key = RuntimeGuard.RequiredUtf8(key, 512, nameof(key));
        DefaultLocale = RuntimeGuard.RequiredUtf8(
            defaultLocale,
            64,
            nameof(defaultLocale));
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Localization arguments must be a JSON object.",
                nameof(arguments));
        }

        var admittedLimits = limits ?? new WorldPresentationLimits();
        WorldPresentationValidation.ValidateMetadata(
            arguments,
            admittedLimits,
            nameof(arguments));
        Arguments = arguments.Clone();
        FallbackText = fallbackText is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                fallbackText,
                admittedLimits.MaxPayloadUtf8Bytes,
                nameof(fallbackText));
    }

    public string Key { get; }

    public string DefaultLocale { get; }

    public JsonElement Arguments { get; }

    public string? FallbackText { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("key", JsonArrayBuilder.String(Key)),
            ("defaultLocale", JsonArrayBuilder.String(DefaultLocale)),
            ("arguments", Arguments),
            ("fallbackText", FallbackText is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(FallbackText)));
    }
}

public sealed class WorldPresentationMediaCue
{
    public WorldPresentationMediaCue(
        string cueId,
        string cueKind,
        string resourceId,
        string mediaType,
        JsonElement? parameters = null,
        string? resourceDigest = null,
        WorldPresentationLimits? limits = null)
    {
        CueId = RuntimeGuard.RequiredUtf8(cueId, 128, nameof(cueId));
        CueKind = RuntimeGuard.RequiredUtf8(
            cueKind,
            128,
            nameof(cueKind));
        ResourceId = RuntimeGuard.RequiredUtf8(
            resourceId,
            4_096,
            nameof(resourceId));
        MediaType = RuntimeGuard.RequiredUtf8(
            mediaType,
            128,
            nameof(mediaType));
        if (resourceDigest is not null
            && !CanonicalJsonDigest.IsSha256(resourceDigest))
        {
            throw new ArgumentException(
                "A resource digest must be a lowercase SHA-256 digest.",
                nameof(resourceDigest));
        }

        ResourceDigest = resourceDigest;
        if (parameters.HasValue)
        {
            WorldPresentationValidation.ValidateMetadata(
                parameters.Value,
                limits ?? new WorldPresentationLimits(),
                nameof(parameters));
            Parameters = parameters.Value.Clone();
        }
    }

    public string CueId { get; }

    public string CueKind { get; }

    public string ResourceId { get; }

    public string MediaType { get; }

    public string? ResourceDigest { get; }

    public JsonElement? Parameters { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("cueId", JsonArrayBuilder.String(CueId)),
            ("cueKind", JsonArrayBuilder.String(CueKind)),
            ("resourceId", JsonArrayBuilder.String(ResourceId)),
            ("mediaType", JsonArrayBuilder.String(MediaType)),
            ("resourceDigest", ResourceDigest is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(ResourceDigest)),
            ("parameters", Parameters.HasValue
                ? Parameters.Value
                : JsonArrayBuilder.Null()));
    }
}

/// <summary>
/// Engine-neutral content. Payloads can represent dialogue, notifications,
/// animations, map annotations, choices, or any host-defined presentation.
/// </summary>
public sealed class WorldPresentationContent
{
    public WorldPresentationContent(
        string kind,
        string contentType,
        JsonElement payload,
        WorldPresentationLocalization? localization = null,
        IEnumerable<WorldPresentationMediaCue>? mediaCues = null,
        WorldPresentationLimits? limits = null)
    {
        var admittedLimits = limits ?? new WorldPresentationLimits();
        Kind = RuntimeGuard.RequiredUtf8(kind, 128, nameof(kind));
        ContentType = RuntimeGuard.RequiredUtf8(
            contentType,
            128,
            nameof(contentType));
        WorldPresentationValidation.ValidatePayload(
            payload,
            admittedLimits,
            nameof(payload));
        Payload = payload.Clone();
        Localization = localization is null
            ? null
            : WorldPresentationValidation.CloneLocalization(
                localization,
                admittedLimits);
        MediaCues = new ReadOnlyCollection<WorldPresentationMediaCue>(
            RuntimeInputGuard.CopyBounded(
                mediaCues ?? Array.Empty<WorldPresentationMediaCue>(),
                admittedLimits.MaxMediaCues,
                item => WorldPresentationValidation.CloneCue(
                    item
                    ?? throw new ArgumentException(
                        "Media cue collections cannot contain null.",
                        nameof(mediaCues)),
                    admittedLimits),
                nameof(mediaCues),
                "world_presentation_media_cues_exceeded"));
        var cueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in MediaCues)
        {
            if (!cueIds.Add(cue.CueId))
            {
                throw new ArgumentException(
                    "Media cue identifiers must be unique.",
                    nameof(mediaCues));
            }
        }
    }

    public string Kind { get; }

    public string ContentType { get; }

    public JsonElement Payload { get; }

    public WorldPresentationLocalization? Localization { get; }

    public IReadOnlyList<WorldPresentationMediaCue> MediaCues { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("kind", JsonArrayBuilder.String(Kind)),
            ("contentType", JsonArrayBuilder.String(ContentType)),
            ("payload", Payload),
            ("localization", Localization is null
                ? JsonArrayBuilder.Null()
                : Localization.ToJson()),
            ("mediaCues", JsonArrayBuilder.Array(
                MediaCues.Select(static cue => cue.ToJson()))));
    }
}

public sealed class WorldPresentationProvenance
{
    public WorldPresentationProvenance(
        string producerId,
        string producerVersion,
        string derivationKind,
        IEnumerable<string>? parentPresentationIds = null,
        JsonElement? metadata = null,
        WorldPresentationLimits? limits = null)
    {
        var admittedLimits = limits ?? new WorldPresentationLimits();
        ProducerId = RuntimeGuard.RequiredUtf8(
            producerId,
            128,
            nameof(producerId));
        ProducerVersion = RuntimeGuard.RequiredUtf8(
            producerVersion,
            128,
            nameof(producerVersion));
        DerivationKind = RuntimeGuard.RequiredUtf8(
            derivationKind,
            128,
            nameof(derivationKind));
        ParentPresentationIds = RuntimeGuard.CopyStrings(
            parentPresentationIds ?? Array.Empty<string>(),
            admittedLimits.MaxParentPresentationIds,
            128,
            nameof(parentPresentationIds),
            sort: true,
            requireUnique: true);
        if (metadata.HasValue)
        {
            WorldPresentationValidation.ValidateMetadata(
                metadata.Value,
                admittedLimits,
                nameof(metadata));
            Metadata = metadata.Value.Clone();
        }
    }

    public string ProducerId { get; }

    public string ProducerVersion { get; }

    public string DerivationKind { get; }

    public IReadOnlyList<string> ParentPresentationIds { get; }

    public JsonElement? Metadata { get; }

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("producerId", JsonArrayBuilder.String(ProducerId)),
            ("producerVersion", JsonArrayBuilder.String(ProducerVersion)),
            ("derivationKind", JsonArrayBuilder.String(DerivationKind)),
            ("parentPresentationIds", JsonArrayBuilder.Strings(
                ParentPresentationIds)),
            ("metadata", Metadata.HasValue
                ? Metadata.Value
                : JsonArrayBuilder.Null()));
    }
}

public sealed class WorldPresentationDraft
{
    public WorldPresentationDraft(
        string presentationId,
        long contentRevision,
        WorldPresentationSource source,
        WorldPresentationBinding binding,
        WorldPresentationAudience audience,
        WorldPresentationContent content,
        WorldPresentationProvenance provenance,
        WorldPresentationLimits? limits = null)
    {
        var admittedLimits = limits ?? new WorldPresentationLimits();
        PresentationId = RuntimeGuard.RequiredUtf8(
            presentationId,
            128,
            nameof(presentationId));
        if (contentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentRevision));
        }

        ContentRevision = contentRevision;
        Source = WorldPresentationValidation.CloneSource(
            source ?? throw new ArgumentNullException(nameof(source)));
        Binding = WorldPresentationValidation.CloneBinding(
            binding ?? throw new ArgumentNullException(nameof(binding)));
        Audience = WorldPresentationValidation.CloneAudience(
            audience ?? throw new ArgumentNullException(nameof(audience)),
            admittedLimits);
        Content = WorldPresentationValidation.CloneContent(
            content ?? throw new ArgumentNullException(nameof(content)),
            admittedLimits);
        Provenance = WorldPresentationValidation.CloneProvenance(
            provenance
            ?? throw new ArgumentNullException(nameof(provenance)),
            admittedLimits);
        WorldPresentationValidation.ValidateAggregate(
            JsonArrayBuilder.Object(
                ("presentationId", JsonArrayBuilder.String(
                    PresentationId)),
                ("contentRevision", JsonArrayBuilder.Number(
                    ContentRevision)),
                ("source", Source.ToJson()),
                ("binding", Binding.ToJson()),
                ("audience", Audience.ToJson()),
                ("content", Content.ToJson()),
                ("provenance", Provenance.ToJson())),
            admittedLimits,
            nameof(content));
    }

    public string PresentationId { get; }

    public long ContentRevision { get; }

    public WorldPresentationSource Source { get; }

    public WorldPresentationBinding Binding { get; }

    public WorldPresentationAudience Audience { get; }

    public WorldPresentationContent Content { get; }

    public WorldPresentationProvenance Provenance { get; }
}

/// <summary>
/// A presentation whose source and coordinate were verified against a
/// committed authoritative receipt. The semantic digest binds every field
/// except the append-only store sequence.
/// </summary>
public sealed class VerifiedWorldPresentation
{
    internal VerifiedWorldPresentation(
        long sequence,
        WorldPresentationDraft draft,
        CommittedWorldPresentationEvidence evidence,
        string? expectedSemanticDigest = null)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Sequence = sequence;
        PresentationId = draft.PresentationId;
        ContentRevision = draft.ContentRevision;
        Source = WorldPresentationValidation.CloneSource(draft.Source);
        Binding = WorldPresentationValidation.CloneBinding(draft.Binding);
        Audience = WorldPresentationValidation.CloneAudience(
            draft.Audience,
            WorldPresentationValidation.MaximumLimits);
        Content = WorldPresentationValidation.CloneContent(
            draft.Content,
            WorldPresentationValidation.MaximumLimits);
        Provenance = WorldPresentationValidation.CloneProvenance(
            draft.Provenance,
            WorldPresentationValidation.MaximumLimits);
        ProvenanceDigest =
            WorldPresentationValidation.ComputeSemanticDigest(
                Provenance.ToJson());
        ProjectionUtf8Bytes =
            WorldPresentationValidation.MeasureProjectionUtf8Bytes(
                Content);
        EvidenceDigest = evidence.SemanticDigest;
        var computed = ComputeSemanticDigest();
        if (expectedSemanticDigest is not null
            && !string.Equals(
                expectedSemanticDigest,
                computed,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The persisted presentation semantic digest is invalid.",
                nameof(expectedSemanticDigest));
        }

        SemanticDigest = computed;
    }

    public long Sequence { get; }

    public string PresentationId { get; }

    public long ContentRevision { get; }

    public WorldPresentationSource Source { get; }

    public WorldPresentationBinding Binding { get; }

    public WorldPresentationAudience Audience { get; }

    public WorldPresentationContent Content { get; }

    public WorldPresentationProvenance Provenance { get; }

    public string ProvenanceDigest { get; }

    public int ProjectionUtf8Bytes { get; }

    public string EvidenceDigest { get; }

    public string SemanticDigest { get; }

    internal VerifiedWorldPresentation WithSequence(long sequence)
    {
        return new VerifiedWorldPresentation(
            sequence,
            PresentationId,
            ContentRevision,
            Source,
            Binding,
            Audience,
            Content,
            Provenance,
            EvidenceDigest,
            SemanticDigest);
    }

    internal static VerifiedWorldPresentation Restore(
        long sequence,
        string presentationId,
        long contentRevision,
        WorldPresentationSource source,
        WorldPresentationBinding binding,
        WorldPresentationAudience audience,
        WorldPresentationContent content,
        WorldPresentationProvenance provenance,
        string evidenceDigest,
        string semanticDigest)
    {
        return new VerifiedWorldPresentation(
            sequence,
            presentationId,
            contentRevision,
            source,
            binding,
            audience,
            content,
            provenance,
            evidenceDigest,
            semanticDigest);
    }

    private VerifiedWorldPresentation(
        long sequence,
        string presentationId,
        long contentRevision,
        WorldPresentationSource source,
        WorldPresentationBinding binding,
        WorldPresentationAudience audience,
        WorldPresentationContent content,
        WorldPresentationProvenance provenance,
        string evidenceDigest,
        string semanticDigest)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (!CanonicalJsonDigest.IsSha256(evidenceDigest))
        {
            throw new ArgumentException(
                "The evidence digest must be a lowercase SHA-256 digest.",
                nameof(evidenceDigest));
        }

        Sequence = sequence;
        PresentationId = RuntimeGuard.RequiredUtf8(
            presentationId,
            128,
            nameof(presentationId));
        if (contentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentRevision));
        }

        ContentRevision = contentRevision;
        Source = WorldPresentationValidation.CloneSource(source);
        Binding = WorldPresentationValidation.CloneBinding(binding);
        Audience = WorldPresentationValidation.CloneAudience(
            audience,
            WorldPresentationValidation.MaximumLimits);
        Content = WorldPresentationValidation.CloneContent(
            content,
            WorldPresentationValidation.MaximumLimits);
        Provenance = WorldPresentationValidation.CloneProvenance(
            provenance,
            WorldPresentationValidation.MaximumLimits);
        ProvenanceDigest =
            WorldPresentationValidation.ComputeSemanticDigest(
                Provenance.ToJson());
        ProjectionUtf8Bytes =
            WorldPresentationValidation.MeasureProjectionUtf8Bytes(
                Content);
        EvidenceDigest = evidenceDigest;
        var computed = ComputeSemanticDigest();
        if (!string.Equals(
                semanticDigest,
                computed,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The persisted presentation semantic digest is invalid.",
                nameof(semanticDigest));
        }

        SemanticDigest = semanticDigest;
    }

    private string ComputeSemanticDigest()
    {
        return WorldPresentationValidation.ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("presentationId", JsonArrayBuilder.String(
                    PresentationId)),
                ("contentRevision", JsonArrayBuilder.Number(
                    ContentRevision)),
                ("source", Source.ToJson()),
                ("binding", Binding.ToJson()),
                ("audience", Audience.ToJson()),
                ("content", Content.ToJson()),
                ("provenance", Provenance.ToJson()),
                ("evidenceDigest", JsonArrayBuilder.String(
                    EvidenceDigest))));
    }
}

/// <summary>
/// Viewer-specific projection of one presentation. It intentionally omits the
/// internal audience list, source identifiers, and provenance metadata so
/// content access never grants enumeration of other audience members or
/// hidden causal identities.
/// </summary>
public sealed class WorldPresentationProjection
{
    internal WorldPresentationProjection(
        VerifiedWorldPresentation presentation,
        WorldPresentationReadGrant grant)
    {
        if (!grant.Allows(presentation.Audience))
        {
            throw new ArgumentException(
                "The read grant does not authorize this presentation.",
                nameof(grant));
        }

        StoreSequence = presentation.Sequence;
        PresentationId = presentation.PresentationId;
        ContentRevision = presentation.ContentRevision;
        Binding = WorldPresentationValidation.CloneBinding(
            presentation.Binding);
        Content = WorldPresentationValidation.CloneContent(
            presentation.Content,
            WorldPresentationValidation.MaximumLimits);
        Viewer = WorldPresentationValidation.CloneIdentity(grant.Viewer);
        MembershipScopeId = presentation.Audience.MembershipScopeId;
        MembershipRevision = presentation.Audience.MembershipRevision;
        PrivacyClass = presentation.Audience.PrivacyClass;
        RedactionClass = presentation.Audience.RedactionClass;
        ProjectionUtf8Bytes = presentation.ProjectionUtf8Bytes;
        Cursor = WorldPresentationValidation.ComputeProjectionCursor(
            presentation,
            grant);
        ProjectionDigest =
            WorldPresentationValidation.ComputeSemanticDigest(
                ToDisclosedJson());
    }

    public string PresentationId { get; }

    public long ContentRevision { get; }

    public WorldPresentationBinding Binding { get; }

    public WorldPresentationContent Content { get; }

    public GameEntityIdentity Viewer { get; }

    public string MembershipScopeId { get; }

    public long MembershipRevision { get; }

    public string PrivacyClass { get; }

    public string RedactionClass { get; }

    public int ProjectionUtf8Bytes { get; }

    /// <summary>
    /// Opaque continuation cursor for this authorized projection. It is
    /// derived only from fields already disclosed to this viewer and never
    /// exposes the append-only store sequence.
    /// </summary>
    public string Cursor { get; }

    /// <summary>
    /// Digest of disclosed fields only. It never commits hidden audience
    /// members, causal source identifiers, receipt evidence, or provenance.
    /// </summary>
    public string ProjectionDigest { get; }

    internal long StoreSequence { get; }

    internal JsonElement ToDisclosedJson()
    {
        return JsonArrayBuilder.Object(
            ("presentationId", JsonArrayBuilder.String(PresentationId)),
            ("contentRevision", JsonArrayBuilder.Number(ContentRevision)),
            ("binding", Binding.ToJson()),
            ("content", Content.ToJson()),
            ("viewer", JsonArrayBuilder.Object(
                ("entityId", JsonArrayBuilder.String(Viewer.EntityId)),
                ("incarnation", JsonArrayBuilder.Number(
                    Viewer.Incarnation)))),
            ("membershipScopeId", JsonArrayBuilder.String(
                MembershipScopeId)),
            ("membershipRevision", JsonArrayBuilder.Number(
                MembershipRevision)),
            ("privacyClass", JsonArrayBuilder.String(PrivacyClass)),
            ("redactionClass", JsonArrayBuilder.String(RedactionClass)));
    }
}

public sealed class WorldPresentationEvidenceException
    : InvalidOperationException
{
    public WorldPresentationEvidenceException(
        string worldReceiptId,
        string reasonCode,
        string message)
        : base(message)
    {
        WorldReceiptId = RuntimeGuard.RequiredUtf8(
            worldReceiptId,
            128,
            nameof(worldReceiptId));
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
    }

    public string WorldReceiptId { get; }

    public string ReasonCode { get; }
}

public sealed class WorldPresentationPublishResult
{
    public WorldPresentationPublishResult(
        string status,
        long currentContentRevision,
        VerifiedWorldPresentation? presentation = null)
    {
        Status = RuntimeGuard.RequiredUtf8(status, 64, nameof(status));
        if (currentContentRevision < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentContentRevision));
        }

        CurrentContentRevision = currentContentRevision;
        Presentation = presentation;
    }

    public string Status { get; }

    public long CurrentContentRevision { get; }

    public VerifiedWorldPresentation? Presentation { get; }
}

/// <summary>
/// Caller request presented to the host-owned read authorizer. It is not an
/// authorization grant by itself.
/// </summary>
public sealed class WorldPresentationAccessRequest
{
    public WorldPresentationAccessRequest(
        WorldPresentationBinding binding,
        GameEntityIdentity viewer,
        string membershipScopeId,
        long membershipRevision,
        IEnumerable<string> privacyClasses,
        IEnumerable<string> redactionClasses)
    {
        Binding = WorldPresentationValidation.CloneBinding(
            binding ?? throw new ArgumentNullException(nameof(binding)));
        Viewer = WorldPresentationValidation.CloneIdentity(
            viewer ?? throw new ArgumentNullException(nameof(viewer)));
        MembershipScopeId = RuntimeGuard.RequiredUtf8(
            membershipScopeId,
            128,
            nameof(membershipScopeId));
        if (membershipRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(membershipRevision));
        }

        MembershipRevision = membershipRevision;
        PrivacyClasses = RuntimeGuard.CopyStrings(
            privacyClasses,
            64,
            128,
            nameof(privacyClasses),
            sort: true,
            requireUnique: true);
        RedactionClasses = RuntimeGuard.CopyStrings(
            redactionClasses,
            64,
            128,
            nameof(redactionClasses),
            sort: true,
            requireUnique: true);
        if (PrivacyClasses.Count == 0 || RedactionClasses.Count == 0)
        {
            throw new ArgumentException(
                "Access requests require at least one privacy and "
                + "redaction class.");
        }
    }

    public WorldPresentationBinding Binding { get; }

    public GameEntityIdentity Viewer { get; }

    public string MembershipScopeId { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<string> PrivacyClasses { get; }

    public IReadOnlyList<string> RedactionClasses { get; }
}

/// <summary>
/// Trusted host boundary for session identity, membership, and disclosure
/// policy. The host must validate the request against authoritative current
/// session state; request fields are caller claims.
/// </summary>
public interface IWorldPresentationReadAuthorizer
{
    ValueTask<bool> IsAuthorizedAsync(
        WorldPresentationAccessRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class WorldPresentationReadGrant
{
    internal WorldPresentationReadGrant(
        WorldPresentationAccessRequest request)
        : this(
            request.Viewer,
            request.MembershipScopeId,
            request.MembershipRevision,
            request.PrivacyClasses,
            request.RedactionClasses)
    {
    }

    internal WorldPresentationReadGrant(
        GameEntityIdentity viewer,
        string membershipScopeId,
        long membershipRevision,
        IEnumerable<string> privacyClasses,
        IEnumerable<string> redactionClasses)
    {
        Viewer = WorldPresentationValidation.CloneIdentity(
            viewer ?? throw new ArgumentNullException(nameof(viewer)));
        MembershipScopeId = RuntimeGuard.RequiredUtf8(
            membershipScopeId,
            128,
            nameof(membershipScopeId));
        if (membershipRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(membershipRevision));
        }

        MembershipRevision = membershipRevision;
        PrivacyClasses = RuntimeGuard.CopyStrings(
            privacyClasses,
            64,
            128,
            nameof(privacyClasses),
            sort: true,
            requireUnique: true);
        RedactionClasses = RuntimeGuard.CopyStrings(
            redactionClasses,
            64,
            128,
            nameof(redactionClasses),
            sort: true,
            requireUnique: true);
        if (PrivacyClasses.Count == 0 || RedactionClasses.Count == 0)
        {
            throw new ArgumentException(
                "Read grants require at least one privacy and redaction "
                + "class.");
        }
    }

    public GameEntityIdentity Viewer { get; }

    public string MembershipScopeId { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<string> PrivacyClasses { get; }

    public IReadOnlyList<string> RedactionClasses { get; }

    internal bool Allows(WorldPresentationAudience audience)
    {
        return string.Equals(
                   MembershipScopeId,
                   audience.MembershipScopeId,
                   StringComparison.Ordinal)
               && MembershipRevision == audience.MembershipRevision
               && PrivacyClasses.Contains(
                   audience.PrivacyClass,
                   StringComparer.Ordinal)
               && RedactionClasses.Contains(
                   audience.RedactionClass,
                   StringComparer.Ordinal)
               && audience.Members.Any(
                   member => member.IsSameIncarnation(Viewer));
    }
}

public sealed class WorldPresentationQuery
{
    internal WorldPresentationQuery(
        WorldPresentationBinding binding,
        WorldPresentationReadGrant grant,
        string? afterCursor = null,
        int maxItems = 100,
        int maxProjectedUtf8Bytes = 8 * 1_048_576)
    {
        Binding = WorldPresentationValidation.CloneBinding(
            binding ?? throw new ArgumentNullException(nameof(binding)));
        Grant = WorldPresentationValidation.CloneGrant(
            grant ?? throw new ArgumentNullException(nameof(grant)));
        if (afterCursor is not null
            && !WorldPresentationValidation.IsProjectionCursor(
                afterCursor))
        {
            throw new ArgumentException(
                "A presentation cursor has an invalid opaque-token format.",
                nameof(afterCursor));
        }

        if (maxItems is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        }

        if (maxProjectedUtf8Bytes is < 1_024
            or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProjectedUtf8Bytes));
        }

        AfterCursor = afterCursor;
        MaxItems = maxItems;
        MaxProjectedUtf8Bytes = maxProjectedUtf8Bytes;
    }

    public WorldPresentationBinding Binding { get; }

    public string? AfterCursor { get; }

    public int MaxItems { get; }

    public int MaxProjectedUtf8Bytes { get; }

    public GameEntityIdentity Viewer => Grant.Viewer;

    public string MembershipScopeId => Grant.MembershipScopeId;

    public long MembershipRevision => Grant.MembershipRevision;

    public IReadOnlyList<string> PrivacyClasses => Grant.PrivacyClasses;

    public IReadOnlyList<string> RedactionClasses =>
        Grant.RedactionClasses;

    internal WorldPresentationReadGrant Grant { get; }

    /// <summary>
    /// Helper for custom stores. Projection succeeds only for this query's
    /// exact binding and opaque authorized grant.
    /// </summary>
    public WorldPresentationProjection? Project(
        VerifiedWorldPresentation presentation)
    {
        if (presentation is null)
        {
            throw new ArgumentNullException(nameof(presentation));
        }

        return presentation.Binding.IsSameAs(Binding)
               && Grant.Allows(presentation.Audience)
            ? new WorldPresentationProjection(presentation, Grant)
            : null;
    }
}

public sealed class WorldPresentationPage
{
    public WorldPresentationPage(
        IReadOnlyList<WorldPresentationProjection> items,
        string? continuationCursor,
        bool hasMore)
    {
        Items = new ReadOnlyCollection<WorldPresentationProjection>(
            RuntimeInputGuard.CopyBounded(
                items
                ?? throw new ArgumentNullException(nameof(items)),
                4_096,
                item => item
                        ?? throw new ArgumentException(
                            "Presentation pages cannot contain null.",
                            nameof(items)),
                nameof(items),
                "world_presentation_page_items_exceeded"));
        if (continuationCursor is not null
            && !WorldPresentationValidation.IsProjectionCursor(
                continuationCursor))
        {
            throw new ArgumentException(
                "A presentation cursor has an invalid opaque-token format.",
                nameof(continuationCursor));
        }

        ContinuationCursor = continuationCursor;
        HasMore = hasMore;
        ValidateSequence(Items, continuationCursor, nameof(items));
    }

    public IReadOnlyList<WorldPresentationProjection> Items { get; }

    public string? ContinuationCursor { get; }

    public bool HasMore { get; }

    private static void ValidateSequence(
        IReadOnlyList<WorldPresentationProjection> items,
        string? continuationCursor,
        string parameterName)
    {
        long previous = 0;
        foreach (var item in items)
        {
            if (item.StoreSequence <= previous)
            {
                throw new ArgumentException(
                    "Presentation page sequences must be strictly "
                    + "increasing.",
                    parameterName);
            }

            previous = item.StoreSequence;
        }

        if (items.Count > 0
            && !string.Equals(
                continuationCursor,
                items[^1].Cursor,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The page continuation must identify its final item.",
                nameof(continuationCursor));
        }
    }
}

public sealed class WorldPresentationAccessDeniedException
    : UnauthorizedAccessException
{
    public WorldPresentationAccessDeniedException()
        : base(
            "The host presentation authorizer denied the exact viewer, "
            + "membership, world binding, or disclosure classes.")
    {
        ReasonCode = "world_presentation_access_denied";
    }

    public string ReasonCode { get; }
}

public sealed class WorldPresentationCursorException
    : ArgumentException
{
    public WorldPresentationCursorException()
        : base(
            "The presentation cursor is invalid for the exact authorized "
            + "query.")
    {
        ReasonCode = "world_presentation_cursor_invalid";
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Audience-filtered export. Its digest covers the exact world binding,
/// viewer lifetime, membership revision, classifications, and records.
/// </summary>
public sealed class WorldPresentationExport
{
    public WorldPresentationExport(
        WorldPresentationQuery query,
        IReadOnlyList<WorldPresentationProjection> items,
        string? continuationCursor,
        bool hasMore)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        Binding = WorldPresentationValidation.CloneBinding(query.Binding);
        var grant = WorldPresentationValidation.CloneGrant(query.Grant);
        Viewer = WorldPresentationValidation.CloneIdentity(grant.Viewer);
        MembershipScopeId = grant.MembershipScopeId;
        MembershipRevision = grant.MembershipRevision;
        PrivacyClasses = grant.PrivacyClasses;
        RedactionClasses = grant.RedactionClasses;
        Items = new ReadOnlyCollection<WorldPresentationProjection>(
            RuntimeInputGuard.CopyBounded(
                items
                ?? throw new ArgumentNullException(nameof(items)),
                query.MaxItems,
                item => item
                        ?? throw new ArgumentException(
                            "Presentation exports cannot contain null.",
                            nameof(items)),
                nameof(items),
                "world_presentation_export_items_exceeded"));
        if (continuationCursor is not null
            && !WorldPresentationValidation.IsProjectionCursor(
                continuationCursor))
        {
            throw new ArgumentException(
                "A presentation cursor has an invalid opaque-token format.",
                nameof(continuationCursor));
        }

        ContinuationCursor = continuationCursor;
        HasMore = hasMore;
        long previous = 0;
        foreach (var item in Items)
        {
            if (item.StoreSequence <= previous
                || !item.Binding.IsSameAs(Binding)
                || !item.Viewer.IsSameIncarnation(Viewer)
                || !string.Equals(
                    item.MembershipScopeId,
                    MembershipScopeId,
                    StringComparison.Ordinal)
                || item.MembershipRevision != MembershipRevision
                || !PrivacyClasses.Contains(
                    item.PrivacyClass,
                    StringComparer.Ordinal)
                || !RedactionClasses.Contains(
                    item.RedactionClass,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Every exported presentation must be ordered and "
                    + "authorized by the exact query.",
                    nameof(items));
            }

            previous = item.StoreSequence;
        }

        if (Items.Count > 0
            && !string.Equals(
                ContinuationCursor,
                Items[^1].Cursor,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The export continuation must identify its final item.",
                nameof(continuationCursor));
        }

        SemanticDigest = WorldPresentationValidation.ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("binding", Binding.ToJson()),
                ("viewer", JsonArrayBuilder.Object(
                    ("entityId", JsonArrayBuilder.String(
                        Viewer.EntityId)),
                    ("incarnation", JsonArrayBuilder.Number(
                        Viewer.Incarnation)))),
                ("membershipScopeId", JsonArrayBuilder.String(
                    MembershipScopeId)),
                ("membershipRevision", JsonArrayBuilder.Number(
                    MembershipRevision)),
                ("privacyClasses", JsonArrayBuilder.Strings(
                    PrivacyClasses)),
                ("redactionClasses", JsonArrayBuilder.Strings(
                    RedactionClasses)),
                ("requestedAfterCursor", query.AfterCursor is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(query.AfterCursor)),
                ("maxItems", JsonArrayBuilder.Number(query.MaxItems)),
                ("maxProjectedUtf8Bytes", JsonArrayBuilder.Number(
                    query.MaxProjectedUtf8Bytes)),
                ("continuationCursor", ContinuationCursor is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(ContinuationCursor)),
                ("hasMore", JsonArrayBuilder.Boolean(HasMore)),
                ("presentations", JsonArrayBuilder.Array(
                    Items.Select(
                        static item => JsonArrayBuilder.Object(
                            ("cursor", JsonArrayBuilder.String(
                                item.Cursor)),
                            ("projectionDigest", JsonArrayBuilder.String(
                                item.ProjectionDigest))))))));
    }

    public WorldPresentationBinding Binding { get; }

    public GameEntityIdentity Viewer { get; }

    public string MembershipScopeId { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<string> PrivacyClasses { get; }

    public IReadOnlyList<string> RedactionClasses { get; }

    public IReadOnlyList<WorldPresentationProjection> Items { get; }

    public string? ContinuationCursor { get; }

    public bool HasMore { get; }

    public string SemanticDigest { get; }
}

public interface IWorldPresentationStore
{
    ValueTask<WorldPresentationPublishResult> PublishVerifiedAsync(
        VerifiedWorldPresentation presentation,
        long expectedPreviousContentRevision,
        CancellationToken cancellationToken = default);

    ValueTask<WorldPresentationProjection?> ReadLatestAsync(
        string presentationId,
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<WorldPresentationPage> QueryAsync(
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<WorldPresentationExport> ExportAsync(
        WorldPresentationQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Composition root that proves authoritative receipt evidence before any
/// non-authoritative presentation reaches durable storage.
/// </summary>
public sealed class DurableWorldPresentationPublisher
{
    private readonly ICommittedWorldPresentationEvidenceSource _evidence;
    private readonly IWorldPresentationStore _store;

    public DurableWorldPresentationPublisher(
        ICommittedWorldPresentationEvidenceSource evidence,
        IWorldPresentationStore store)
    {
        _evidence = evidence
                    ?? throw new ArgumentNullException(nameof(evidence));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<WorldPresentationPublishResult> PublishAsync(
        WorldPresentationDraft draft,
        long expectedPreviousContentRevision,
        CancellationToken cancellationToken = default)
    {
        if (draft is null)
        {
            throw new ArgumentNullException(nameof(draft));
        }

        if (expectedPreviousContentRevision < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedPreviousContentRevision));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var evidence = await _evidence.ReadCommittedAsync(
                draft.Source.WorldReceiptId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence is null)
        {
            throw EvidenceFailure(
                draft,
                "world_presentation_receipt_not_committed",
                "The authoritative receipt is missing or is not committed "
                + "as applied.");
        }

        if (evidence.CommitStatus
            != WorldPresentationCommitStatus.Applied)
        {
            throw EvidenceFailure(
                draft,
                "world_presentation_receipt_not_applied",
                "The authoritative receipt is not committed as applied.");
        }

        if (!draft.Source.IsSameAs(evidence.Source))
        {
            throw EvidenceFailure(
                draft,
                "world_presentation_source_mismatch",
                "The presentation source does not match the committed "
                + "receipt.");
        }

        if (!draft.Binding.IsSameAs(evidence.Binding))
        {
            throw EvidenceFailure(
                draft,
                "world_presentation_binding_mismatch",
                "The presentation coordinate does not match the committed "
                + "receipt.");
        }

        var verified = new VerifiedWorldPresentation(
            sequence: 0,
            draft,
            evidence);
        return await _store.PublishVerifiedAsync(
                verified,
                expectedPreviousContentRevision,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static WorldPresentationEvidenceException EvidenceFailure(
        WorldPresentationDraft draft,
        string code,
        string message)
    {
        return new WorldPresentationEvidenceException(
            draft.Source.WorldReceiptId,
            code,
            message);
    }
}

/// <summary>
/// Authorized read composition root. Raw access requests are never accepted
/// by the store until the host-owned authorizer validates them.
/// </summary>
public sealed class DurableWorldPresentationReader
{
    private readonly IWorldPresentationReadAuthorizer _authorizer;
    private readonly IWorldPresentationStore _store;

    public DurableWorldPresentationReader(
        IWorldPresentationReadAuthorizer authorizer,
        IWorldPresentationStore store)
    {
        _authorizer = authorizer
                      ?? throw new ArgumentNullException(nameof(authorizer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<WorldPresentationProjection?> ReadLatestAsync(
        string presentationId,
        WorldPresentationAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await _store.ReadLatestAsync(
                presentationId,
                new WorldPresentationQuery(
                    access.Binding,
                    access.Grant,
                    afterCursor: null,
                    maxItems: 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorldPresentationPage> QueryAsync(
        WorldPresentationAccessRequest request,
        string? afterCursor = null,
        int maxItems = 100,
        int maxProjectedUtf8Bytes = 8 * 1_048_576,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await _store.QueryAsync(
                new WorldPresentationQuery(
                    access.Binding,
                    access.Grant,
                    afterCursor,
                    maxItems,
                    maxProjectedUtf8Bytes),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorldPresentationExport> ExportAsync(
        WorldPresentationAccessRequest request,
        string? afterCursor = null,
        int maxItems = 100,
        int maxProjectedUtf8Bytes = 8 * 1_048_576,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await _store.ExportAsync(
                new WorldPresentationQuery(
                    access.Binding,
                    access.Grant,
                    afterCursor,
                    maxItems,
                    maxProjectedUtf8Bytes),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AuthorizedAccess> AuthorizeAsync(
        WorldPresentationAccessRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var snapshot = WorldPresentationValidation.CloneAccess(request);
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = await _authorizer.IsAuthorizedAsync(
                snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!allowed)
        {
            throw new WorldPresentationAccessDeniedException();
        }

        return new AuthorizedAccess(
            snapshot.Binding,
            new WorldPresentationReadGrant(snapshot));
    }

    private sealed class AuthorizedAccess
    {
        public AuthorizedAccess(
            WorldPresentationBinding binding,
            WorldPresentationReadGrant grant)
        {
            Binding = binding;
            Grant = grant;
        }

        public WorldPresentationBinding Binding { get; }

        public WorldPresentationReadGrant Grant { get; }
    }
}

internal static class WorldPresentationValidation
{
    public static readonly WorldPresentationLimits MaximumLimits = new(
        maxAudienceMembers: 4_096,
        maxMediaCues: 128,
        maxParentPresentationIds: 4_096,
        maxPayloadUtf8Bytes: 4 * 1_048_576,
        maxMetadataUtf8Bytes: 65_536,
        maxJsonDepth: 64,
        maxJsonNodes: 65_536,
        maxAggregateUtf8Bytes: 32 * 1_048_576,
        maxAggregateJsonNodes: 1_000_000);

    private static readonly JsonValueLimits SemanticDigestLimits = new(
        maxUtf8Bytes: 33 * 1_048_576,
        maxDepth: 72,
        maxNodes: 1_001_024,
        maxStringUtf8Bytes: 4 * 1_048_576,
        maxContainerItems: 65_536);

    public static string ComputeSemanticDigest(JsonElement value)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            SemanticDigestLimits,
            nameof(value));
        using var algorithm = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        using var output = new IncrementalHashBufferWriter(algorithm);
        using (var writer = new Utf8JsonWriter(output))
        {
            WriteCanonical(writer, value);
            writer.Flush();
        }

        var digest = algorithm.GetHashAndReset();
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            _ = result.Append(item.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    public static int MeasureProjectionUtf8Bytes(
        WorldPresentationContent content)
    {
        var contentBytes = JsonValueInspector.ValidateAndMeasure(
            content.ToJson(),
            SemanticDigestLimits,
            nameof(content));
        return checked(contentBytes + 4_096);
    }

    internal static string ComputeProjectionCursor(
        VerifiedWorldPresentation presentation,
        WorldPresentationReadGrant grant)
    {
        if (grant is null)
        {
            throw new ArgumentNullException(nameof(grant));
        }

        var recordCursor = ComputeProjectionCursorBase(
            presentation,
            grant.Viewer);
        var queryScope = ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("privacyClasses", JsonArrayBuilder.Strings(
                    grant.PrivacyClasses)),
                ("redactionClasses", JsonArrayBuilder.Strings(
                    grant.RedactionClasses))));
        return string.Concat(recordCursor, ".", queryScope);
    }

    internal static string ComputeProjectionCursorBase(
        VerifiedWorldPresentation presentation,
        GameEntityIdentity viewer)
    {
        if (presentation is null)
        {
            throw new ArgumentNullException(nameof(presentation));
        }

        if (viewer is null)
        {
            throw new ArgumentNullException(nameof(viewer));
        }

        return ComputeSemanticDigest(
            JsonArrayBuilder.Object(
                ("binding", presentation.Binding.ToJson()),
                ("presentationId", JsonArrayBuilder.String(
                    presentation.PresentationId)),
                ("contentRevision", JsonArrayBuilder.Number(
                    presentation.ContentRevision)),
                ("viewer", JsonArrayBuilder.Object(
                    ("entityId", JsonArrayBuilder.String(viewer.EntityId)),
                    ("incarnation", JsonArrayBuilder.Number(
                        viewer.Incarnation)))),
                ("membershipScopeId", JsonArrayBuilder.String(
                    presentation.Audience.MembershipScopeId)),
                ("membershipRevision", JsonArrayBuilder.Number(
                    presentation.Audience.MembershipRevision)),
                ("privacyClass", JsonArrayBuilder.String(
                    presentation.Audience.PrivacyClass)),
                ("redactionClass", JsonArrayBuilder.String(
                    presentation.Audience.RedactionClass))));
    }

    internal static bool IsProjectionCursor(string value)
    {
        return value.Length == 129
               && value[64] == '.'
               && CanonicalJsonDigest.IsSha256(value[..64])
               && CanonicalJsonDigest.IsSha256(value[65..]);
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    value.GetRawText(),
                    skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    "Undefined JSON cannot be hashed.",
                    nameof(value));
        }
    }

    public static string? OptionalId(string? value, string parameterName)
    {
        return value is null
            ? null
            : RuntimeGuard.RequiredUtf8(value, 128, parameterName);
    }

    public static GameEntityIdentity CloneIdentity(
        GameEntityIdentity identity)
    {
        return new GameEntityIdentity(
            identity.EntityId,
            identity.Incarnation);
    }

    public static GameTimePoint CloneTime(GameTimePoint time)
    {
        return new GameTimePoint(
            time.ClockId,
            time.TimelineId,
            time.Epoch,
            time.Tick);
    }

    public static JsonElement TimeToJson(GameTimePoint time)
    {
        return JsonArrayBuilder.Object(
            ("clockId", JsonArrayBuilder.String(time.ClockId)),
            ("timelineId", JsonArrayBuilder.String(time.TimelineId)),
            ("epoch", JsonArrayBuilder.Number(time.Epoch)),
            ("tick", JsonArrayBuilder.Number(time.Tick)));
    }

    public static WorldPresentationBinding CloneBinding(
        WorldPresentationBinding binding)
    {
        return new WorldPresentationBinding(
            binding.WorldId,
            binding.TimelineId,
            binding.TimelineEpoch,
            binding.SaveRevision,
            binding.StateVersion,
            binding.CatalogDigest,
            binding.GameTime,
            binding.CommittedStateDigest);
    }

    public static WorldPresentationSource CloneSource(
        WorldPresentationSource source)
    {
        return new WorldPresentationSource(
            source.WorldReceiptId,
            source.WorldReceiptDigest,
            source.OccurrenceId,
            source.ActionId,
            source.OperationId);
    }

    public static IReadOnlyList<GameEntityIdentity> CopyIdentities(
        IEnumerable<GameEntityIdentity> identities,
        int maximum,
        string parameterName)
    {
        var copied = RuntimeInputGuard.CopyBounded(
            identities
            ?? throw new ArgumentNullException(parameterName),
            maximum,
            item => CloneIdentity(
                item
                ?? throw new ArgumentException(
                    "An audience cannot contain null members.",
                    parameterName)),
            parameterName,
            "world_presentation_audience_exceeded");
        Array.Sort(
            copied,
            static (left, right) =>
            {
                var byId = StringComparer.Ordinal.Compare(
                    left.EntityId,
                    right.EntityId);
                return byId != 0
                    ? byId
                    : left.Incarnation.CompareTo(right.Incarnation);
            });
        for (var index = 1; index < copied.Length; index++)
        {
            if (string.Equals(
                    copied[index - 1].EntityId,
                    copied[index].EntityId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An audience cannot contain multiple incarnations of "
                    + "the same entity.",
                    parameterName);
            }
        }

        return new ReadOnlyCollection<GameEntityIdentity>(copied);
    }

    public static WorldPresentationAudience CloneAudience(
        WorldPresentationAudience audience,
        WorldPresentationLimits limits)
    {
        return new WorldPresentationAudience(
            audience.MembershipScopeId,
            audience.MembershipRevision,
            audience.Members,
            audience.PrivacyClass,
            audience.RedactionClass,
            limits);
    }

    public static WorldPresentationLocalization CloneLocalization(
        WorldPresentationLocalization localization,
        WorldPresentationLimits limits)
    {
        return new WorldPresentationLocalization(
            localization.Key,
            localization.DefaultLocale,
            localization.Arguments,
            localization.FallbackText,
            limits);
    }

    public static WorldPresentationMediaCue CloneCue(
        WorldPresentationMediaCue cue,
        WorldPresentationLimits limits)
    {
        return new WorldPresentationMediaCue(
            cue.CueId,
            cue.CueKind,
            cue.ResourceId,
            cue.MediaType,
            cue.Parameters,
            cue.ResourceDigest,
            limits);
    }

    public static WorldPresentationContent CloneContent(
        WorldPresentationContent content,
        WorldPresentationLimits limits)
    {
        return new WorldPresentationContent(
            content.Kind,
            content.ContentType,
            content.Payload,
            content.Localization,
            content.MediaCues,
            limits);
    }

    public static WorldPresentationProvenance CloneProvenance(
        WorldPresentationProvenance provenance,
        WorldPresentationLimits limits)
    {
        return new WorldPresentationProvenance(
            provenance.ProducerId,
            provenance.ProducerVersion,
            provenance.DerivationKind,
            provenance.ParentPresentationIds,
            provenance.Metadata,
            limits);
    }

    public static WorldPresentationReadGrant CloneGrant(
        WorldPresentationReadGrant grant)
    {
        return new WorldPresentationReadGrant(
            grant.Viewer,
            grant.MembershipScopeId,
            grant.MembershipRevision,
            grant.PrivacyClasses,
            grant.RedactionClasses);
    }

    public static WorldPresentationAccessRequest CloneAccess(
        WorldPresentationAccessRequest request)
    {
        return new WorldPresentationAccessRequest(
            request.Binding,
            request.Viewer,
            request.MembershipScopeId,
            request.MembershipRevision,
            request.PrivacyClasses,
            request.RedactionClasses);
    }

    public static WorldPresentationDraft ToDraft(
        VerifiedWorldPresentation presentation)
    {
        return new WorldPresentationDraft(
            presentation.PresentationId,
            presentation.ContentRevision,
            presentation.Source,
            presentation.Binding,
            presentation.Audience,
            presentation.Content,
            presentation.Provenance);
    }

    public static void ValidatePayload(
        JsonElement value,
        WorldPresentationLimits limits,
        string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "Presentation JSON cannot be undefined.",
                parameterName);
        }

        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                limits.MaxPayloadUtf8Bytes,
                limits.MaxJsonDepth,
                limits.MaxJsonNodes,
                limits.MaxPayloadUtf8Bytes,
                limits.MaxJsonNodes),
            parameterName);
    }

    public static void ValidateMetadata(
        JsonElement value,
        WorldPresentationLimits limits,
        string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "Presentation metadata cannot be undefined.",
                parameterName);
        }

        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                limits.MaxMetadataUtf8Bytes,
                limits.MaxJsonDepth,
                limits.MaxJsonNodes,
                limits.MaxMetadataUtf8Bytes,
                limits.MaxJsonNodes),
            parameterName);
    }

    public static void ValidateAggregate(
        JsonElement value,
        WorldPresentationLimits limits,
        string parameterName)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                limits.MaxAggregateUtf8Bytes,
                checked(limits.MaxJsonDepth + 8),
                limits.MaxAggregateJsonNodes,
                Math.Max(
                    limits.MaxPayloadUtf8Bytes,
                    limits.MaxMetadataUtf8Bytes),
                Math.Max(
                    limits.MaxJsonNodes,
                    Math.Max(
                        limits.MaxAudienceMembers,
                        Math.Max(
                            limits.MaxMediaCues,
                            limits.MaxParentPresentationIds)))),
            parameterName);
    }

    private sealed class IncrementalHashBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int DefaultBufferBytes = 4_096;

        private readonly IncrementalHash _hash;
        private byte[]? _buffer;

        public IncrementalHashBufferWriter(IncrementalHash hash)
        {
            _hash = hash ?? throw new ArgumentNullException(nameof(hash));
        }

        public void Advance(int count)
        {
            if (_buffer is null || count < 0 || count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _hash.AppendData(_buffer, 0, count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }

        private void EnsureBuffer(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            var required = sizeHint == 0
                ? DefaultBufferBytes
                : sizeHint;
            if (_buffer is not null && _buffer.Length >= required)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            var prior = _buffer;
            _buffer = replacement;
            if (prior is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    prior,
                    clearArray: true);
            }
        }
    }
}
