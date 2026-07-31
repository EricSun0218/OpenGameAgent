using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Core;

namespace GameAgent.Persistence;

internal sealed class WorldPresentationFrameRecord
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("storeRevision")]
    public long StoreRevision { get; set; }

    [JsonPropertyName("previousFrameDigest")]
    public string PreviousFrameDigest { get; set; } = string.Empty;

    [JsonPropertyName("presentation")]
    public PersistedWorldPresentation? Presentation { get; set; }
}

internal sealed class PersistedWorldPresentation
{
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("presentationId")]
    public string PresentationId { get; set; } = string.Empty;

    [JsonPropertyName("contentRevision")]
    public long ContentRevision { get; set; }

    [JsonPropertyName("source")]
    public PersistedWorldPresentationSource? Source { get; set; }

    [JsonPropertyName("binding")]
    public PersistedWorldPresentationBinding? Binding { get; set; }

    [JsonPropertyName("audience")]
    public PersistedWorldPresentationAudience? Audience { get; set; }

    [JsonPropertyName("content")]
    public PersistedWorldPresentationContent? Content { get; set; }

    [JsonPropertyName("provenance")]
    public PersistedWorldPresentationProvenance? Provenance { get; set; }

    [JsonPropertyName("evidenceDigest")]
    public string EvidenceDigest { get; set; } = string.Empty;

    [JsonPropertyName("semanticDigest")]
    public string SemanticDigest { get; set; } = string.Empty;

    public static PersistedWorldPresentation FromPresentation(
        VerifiedWorldPresentation presentation)
    {
        return new PersistedWorldPresentation
        {
            Sequence = presentation.Sequence,
            PresentationId = presentation.PresentationId,
            ContentRevision = presentation.ContentRevision,
            Source = PersistedWorldPresentationSource.FromSource(
                presentation.Source),
            Binding = PersistedWorldPresentationBinding.FromBinding(
                presentation.Binding),
            Audience = PersistedWorldPresentationAudience.FromAudience(
                presentation.Audience),
            Content = PersistedWorldPresentationContent.FromContent(
                presentation.Content),
            Provenance =
                PersistedWorldPresentationProvenance.FromProvenance(
                    presentation.Provenance),
            EvidenceDigest = presentation.EvidenceDigest,
            SemanticDigest = presentation.SemanticDigest
        };
    }

    public VerifiedWorldPresentation Restore()
    {
        return VerifiedWorldPresentation.Restore(
            Sequence,
            PresentationId,
            ContentRevision,
            Required(Source, nameof(Source)).Restore(),
            Required(Binding, nameof(Binding)).Restore(),
            Required(Audience, nameof(Audience)).Restore(),
            Required(Content, nameof(Content)).Restore(),
            Required(Provenance, nameof(Provenance)).Restore(),
            EvidenceDigest,
            SemanticDigest);
    }

    private static T Required<T>(T? value, string name)
        where T : class
    {
        return value ?? throw new JsonException(
            $"Persisted presentation field '{name}' cannot be null.");
    }
}

internal sealed class PersistedWorldPresentationSource
{
    [JsonPropertyName("worldReceiptId")]
    public string WorldReceiptId { get; set; } = string.Empty;

    [JsonPropertyName("worldReceiptDigest")]
    public string WorldReceiptDigest { get; set; } = string.Empty;

    [JsonPropertyName("occurrenceId")]
    public string? OccurrenceId { get; set; }

    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; set; }

    public static PersistedWorldPresentationSource FromSource(
        WorldPresentationSource source)
    {
        return new PersistedWorldPresentationSource
        {
            WorldReceiptId = source.WorldReceiptId,
            WorldReceiptDigest = source.WorldReceiptDigest,
            OccurrenceId = source.OccurrenceId,
            ActionId = source.ActionId,
            OperationId = source.OperationId
        };
    }

    public WorldPresentationSource Restore()
    {
        return new WorldPresentationSource(
            WorldReceiptId,
            WorldReceiptDigest,
            OccurrenceId,
            ActionId,
            OperationId);
    }
}

internal sealed class PersistedWorldPresentationBinding
{
    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("timelineId")]
    public string TimelineId { get; set; } = string.Empty;

    [JsonPropertyName("timelineEpoch")]
    public long TimelineEpoch { get; set; }

    [JsonPropertyName("saveRevision")]
    public long SaveRevision { get; set; }

    [JsonPropertyName("stateVersion")]
    public long StateVersion { get; set; }

    [JsonPropertyName("catalogDigest")]
    public string CatalogDigest { get; set; } = string.Empty;

    [JsonPropertyName("gameTime")]
    public PersistedWorldPresentationGameTimePoint? GameTime { get; set; }

    [JsonPropertyName("committedStateDigest")]
    public string? CommittedStateDigest { get; set; }

    public static PersistedWorldPresentationBinding FromBinding(
        WorldPresentationBinding binding)
    {
        return new PersistedWorldPresentationBinding
        {
            WorldId = binding.WorldId,
            TimelineId = binding.TimelineId,
            TimelineEpoch = binding.TimelineEpoch,
            SaveRevision = binding.SaveRevision,
            StateVersion = binding.StateVersion,
            CatalogDigest = binding.CatalogDigest,
            GameTime = binding.GameTime is null
                ? null
                : PersistedWorldPresentationGameTimePoint.FromTime(
                    binding.GameTime),
            CommittedStateDigest = binding.CommittedStateDigest
        };
    }

    public WorldPresentationBinding Restore()
    {
        return new WorldPresentationBinding(
            WorldId,
            TimelineId,
            TimelineEpoch,
            SaveRevision,
            StateVersion,
            CatalogDigest,
            GameTime?.Restore(),
            CommittedStateDigest);
    }
}

internal sealed class PersistedWorldPresentationGameTimePoint
{
    [JsonPropertyName("clockId")]
    public string ClockId { get; set; } = string.Empty;

    [JsonPropertyName("timelineId")]
    public string TimelineId { get; set; } = string.Empty;

    [JsonPropertyName("epoch")]
    public long Epoch { get; set; }

    [JsonPropertyName("tick")]
    public long Tick { get; set; }

    public static PersistedWorldPresentationGameTimePoint FromTime(
        GameTimePoint time)
    {
        return new PersistedWorldPresentationGameTimePoint
        {
            ClockId = time.ClockId,
            TimelineId = time.TimelineId,
            Epoch = time.Epoch,
            Tick = time.Tick
        };
    }

    public GameTimePoint Restore()
    {
        return new GameTimePoint(ClockId, TimelineId, Epoch, Tick);
    }
}

internal sealed class PersistedWorldPresentationAudience
{
    [JsonPropertyName("membershipScopeId")]
    public string MembershipScopeId { get; set; } = string.Empty;

    [JsonPropertyName("membershipRevision")]
    public long MembershipRevision { get; set; }

    [JsonPropertyName("members")]
    public List<PersistedPresentationIdentity> Members { get; set; } =
        new();

    [JsonPropertyName("privacyClass")]
    public string PrivacyClass { get; set; } = string.Empty;

    [JsonPropertyName("redactionClass")]
    public string RedactionClass { get; set; } = string.Empty;

    public static PersistedWorldPresentationAudience FromAudience(
        WorldPresentationAudience audience)
    {
        return new PersistedWorldPresentationAudience
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

    public WorldPresentationAudience Restore()
    {
        return new WorldPresentationAudience(
            MembershipScopeId,
            MembershipRevision,
            (Members
             ?? throw new JsonException(
                 "Persisted presentation members cannot be null."))
            .Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted presentation members cannot "
                             + "contain null."))
                    .Restore()),
            PrivacyClass,
            RedactionClass,
            WorldPresentationValidation.MaximumLimits);
    }
}

internal sealed class PersistedPresentationIdentity
{
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = string.Empty;

    [JsonPropertyName("incarnation")]
    public long Incarnation { get; set; }

    public static PersistedPresentationIdentity FromIdentity(
        GameEntityIdentity identity)
    {
        return new PersistedPresentationIdentity
        {
            EntityId = identity.EntityId,
            Incarnation = identity.Incarnation
        };
    }

    public GameEntityIdentity Restore()
    {
        return new GameEntityIdentity(EntityId, Incarnation);
    }
}

internal sealed class PersistedWorldPresentationContent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonPropertyName("localization")]
    public PersistedWorldPresentationLocalization? Localization
    {
        get;
        set;
    }

    [JsonPropertyName("mediaCues")]
    public List<PersistedWorldPresentationMediaCue> MediaCues
    {
        get;
        set;
    } = new();

    public static PersistedWorldPresentationContent FromContent(
        WorldPresentationContent content)
    {
        return new PersistedWorldPresentationContent
        {
            Kind = content.Kind,
            ContentType = content.ContentType,
            Payload = content.Payload.Clone(),
            Localization = content.Localization is null
                ? null
                : PersistedWorldPresentationLocalization
                    .FromLocalization(content.Localization),
            MediaCues = content.MediaCues
                .Select(PersistedWorldPresentationMediaCue.FromCue)
                .ToList()
        };
    }

    public WorldPresentationContent Restore()
    {
        return new WorldPresentationContent(
            Kind,
            ContentType,
            Payload,
            Localization?.Restore(),
            (MediaCues
             ?? throw new JsonException(
                 "Persisted presentation cues cannot be null."))
            .Select(
                item => (item
                         ?? throw new JsonException(
                             "Persisted presentation cues cannot "
                             + "contain null."))
                    .Restore()),
            WorldPresentationValidation.MaximumLimits);
    }
}

internal sealed class PersistedWorldPresentationLocalization
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("defaultLocale")]
    public string DefaultLocale { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }

    [JsonPropertyName("fallbackText")]
    public string? FallbackText { get; set; }

    public static PersistedWorldPresentationLocalization FromLocalization(
        WorldPresentationLocalization localization)
    {
        return new PersistedWorldPresentationLocalization
        {
            Key = localization.Key,
            DefaultLocale = localization.DefaultLocale,
            Arguments = localization.Arguments.Clone(),
            FallbackText = localization.FallbackText
        };
    }

    public WorldPresentationLocalization Restore()
    {
        return new WorldPresentationLocalization(
            Key,
            DefaultLocale,
            Arguments,
            FallbackText,
            WorldPresentationValidation.MaximumLimits);
    }
}

internal sealed class PersistedWorldPresentationMediaCue
{
    [JsonPropertyName("cueId")]
    public string CueId { get; set; } = string.Empty;

    [JsonPropertyName("cueKind")]
    public string CueKind { get; set; } = string.Empty;

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("resourceDigest")]
    public string? ResourceDigest { get; set; }

    public static PersistedWorldPresentationMediaCue FromCue(
        WorldPresentationMediaCue cue)
    {
        return new PersistedWorldPresentationMediaCue
        {
            CueId = cue.CueId,
            CueKind = cue.CueKind,
            ResourceId = cue.ResourceId,
            MediaType = cue.MediaType,
            Parameters = cue.Parameters?.Clone(),
            ResourceDigest = cue.ResourceDigest
        };
    }

    public WorldPresentationMediaCue Restore()
    {
        return new WorldPresentationMediaCue(
            CueId,
            CueKind,
            ResourceId,
            MediaType,
            Parameters,
            ResourceDigest,
            WorldPresentationValidation.MaximumLimits);
    }
}

internal sealed class PersistedWorldPresentationProvenance
{
    [JsonPropertyName("producerId")]
    public string ProducerId { get; set; } = string.Empty;

    [JsonPropertyName("producerVersion")]
    public string ProducerVersion { get; set; } = string.Empty;

    [JsonPropertyName("derivationKind")]
    public string DerivationKind { get; set; } = string.Empty;

    [JsonPropertyName("parentPresentationIds")]
    public List<string> ParentPresentationIds { get; set; } = new();

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    public static PersistedWorldPresentationProvenance FromProvenance(
        WorldPresentationProvenance provenance)
    {
        return new PersistedWorldPresentationProvenance
        {
            ProducerId = provenance.ProducerId,
            ProducerVersion = provenance.ProducerVersion,
            DerivationKind = provenance.DerivationKind,
            ParentPresentationIds =
                provenance.ParentPresentationIds.ToList(),
            Metadata = provenance.Metadata?.Clone()
        };
    }

    public WorldPresentationProvenance Restore()
    {
        return new WorldPresentationProvenance(
            ProducerId,
            ProducerVersion,
            DerivationKind,
            ParentPresentationIds
            ?? throw new JsonException(
                "Persisted presentation parents cannot be null."),
            Metadata,
            WorldPresentationValidation.MaximumLimits);
    }
}
