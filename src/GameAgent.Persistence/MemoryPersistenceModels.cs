using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Core;

namespace GameAgent.Persistence;

internal sealed class MemoryFrameRecord
{
    [JsonRequired]
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonRequired]
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonRequired]
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("memoryId")]
    public string? MemoryId { get; set; }

    [JsonPropertyName("record")]
    public PersistedMemoryRecord? Record { get; set; }

    [JsonPropertyName("mutations")]
    public List<MemoryFrameMutation>? Mutations { get; set; }

    [JsonPropertyName("commitId")]
    public string? CommitId { get; set; }

    [JsonPropertyName("payloadDigest")]
    public string? PayloadDigest { get; set; }
}

internal sealed class MemoryFrameMutation
{
    [JsonRequired]
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("memoryId")]
    public string? MemoryId { get; set; }

    [JsonPropertyName("record")]
    public PersistedMemoryRecord? Record { get; set; }
}

internal sealed class PersistedMemoryRecord
{
    [JsonRequired]
    [JsonPropertyName("memoryId")]
    public string MemoryId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    [JsonRequired]
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("importance")]
    public int Importance { get; set; }

    [JsonRequired]
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonRequired]
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("provenance")]
    public PersistedMemoryProvenance? Provenance { get; set; }

    [JsonPropertyName("gameTimeWindow")]
    public PersistedGameTimeWindow? GameTimeWindow { get; set; }

    public static PersistedMemoryRecord FromMemoryRecord(MemoryRecord record)
    {
        return new PersistedMemoryRecord
        {
            MemoryId = record.MemoryId,
            Scope = record.Scope,
            Content = record.Content.Clone(),
            Tags = record.Tags.ToList(),
            Importance = record.Importance,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ExpiresAt = record.ExpiresAt,
            Provenance = record.Provenance is null
                ? null
                : PersistedMemoryProvenance.FromProvenance(
                    record.Provenance),
            GameTimeWindow = record.GameTimeWindow is null
                ? null
                : PersistedGameTimeWindow.FromWindow(
                    record.GameTimeWindow)
        };
    }

    public MemoryRecord ToMemoryRecord()
    {
        if (Tags is null)
        {
            throw new InvalidOperationException(
                "A persisted memory record requires a tags array.");
        }

        return new MemoryRecord(
            MemoryId,
            Scope,
            Content,
            Tags,
            Importance,
            CreatedAt,
            UpdatedAt,
            ExpiresAt,
            Provenance?.ToProvenance(),
            GameTimeWindow?.ToWindow());
    }
}

internal sealed class PersistedMemoryProvenance
{
    [JsonRequired]
    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonRequired]
    [JsonPropertyName("saveRevision")]
    public long SaveRevision { get; set; }

    [JsonRequired]
    [JsonPropertyName("sourceRunId")]
    public string SourceRunId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("sourceEventId")]
    public string SourceEventId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("committed")]
    public bool Committed { get; set; }

    [JsonPropertyName("timelineId")]
    public string? TimelineId { get; set; }

    [JsonPropertyName("timelineEpoch")]
    public long? TimelineEpoch { get; set; }

    [JsonPropertyName("perspective")]
    public PersistedKnowledgePerspective? Perspective { get; set; }

    public static PersistedMemoryProvenance FromProvenance(
        MemoryProvenance provenance)
    {
        return new PersistedMemoryProvenance
        {
            WorldId = provenance.WorldId,
            SessionId = provenance.SessionId,
            SaveRevision = provenance.SaveRevision,
            SourceRunId = provenance.SourceRunId,
            SourceEventId = provenance.SourceEventId,
            Committed = provenance.Committed,
            TimelineId = provenance.TimelineId,
            TimelineEpoch = provenance.TimelineEpoch,
            Perspective = provenance.Perspective is null
                ? null
                : PersistedKnowledgePerspective.FromPerspective(
                    provenance.Perspective)
        };
    }

    public MemoryProvenance ToProvenance()
    {
        return new MemoryProvenance(
            WorldId,
            SessionId,
            SaveRevision,
            SourceRunId,
            SourceEventId,
            Committed,
            TimelineId,
            Perspective?.ToPerspective(),
            TimelineEpoch);
    }
}

internal sealed class PersistedKnowledgePerspective
{
    [JsonRequired]
    [JsonPropertyName("observer")]
    public PersistedEntityIdentity Observer { get; set; } = new();

    [JsonRequired]
    [JsonPropertyName("knowledgeKind")]
    public string KnowledgeKind { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public PersistedEntityIdentity? Source { get; set; }

    public static PersistedKnowledgePerspective FromPerspective(
        GameKnowledgePerspective perspective)
    {
        return new PersistedKnowledgePerspective
        {
            Observer = PersistedEntityIdentity.FromIdentity(
                perspective.Observer),
            KnowledgeKind = perspective.KnowledgeKind,
            Source = perspective.Source is null
                ? null
                : PersistedEntityIdentity.FromIdentity(perspective.Source)
        };
    }

    public GameKnowledgePerspective ToPerspective()
    {
        if (Observer is null)
        {
            throw new InvalidOperationException(
                "A persisted knowledge perspective requires an observer.");
        }

        return new GameKnowledgePerspective(
            Observer.ToIdentity(),
            KnowledgeKind,
            Source?.ToIdentity());
    }
}

internal sealed class PersistedEntityIdentity
{
    [JsonRequired]
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("incarnation")]
    public long Incarnation { get; set; }

    public static PersistedEntityIdentity FromIdentity(
        GameEntityIdentity identity)
    {
        return new PersistedEntityIdentity
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

internal sealed class PersistedGameTimePoint
{
    [JsonRequired]
    [JsonPropertyName("clockId")]
    public string ClockId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("timelineId")]
    public string TimelineId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("epoch")]
    public long Epoch { get; set; }

    [JsonRequired]
    [JsonPropertyName("tick")]
    public long Tick { get; set; }

    public static PersistedGameTimePoint FromPoint(GameTimePoint point)
    {
        return new PersistedGameTimePoint
        {
            ClockId = point.ClockId,
            TimelineId = point.TimelineId,
            Epoch = point.Epoch,
            Tick = point.Tick
        };
    }

    public GameTimePoint ToPoint()
    {
        return new GameTimePoint(ClockId, TimelineId, Epoch, Tick);
    }
}

internal sealed class PersistedGameTimeWindow
{
    [JsonPropertyName("validFrom")]
    public PersistedGameTimePoint? ValidFrom { get; set; }

    [JsonPropertyName("validUntil")]
    public PersistedGameTimePoint? ValidUntil { get; set; }

    public static PersistedGameTimeWindow FromWindow(GameTimeWindow window)
    {
        return new PersistedGameTimeWindow
        {
            ValidFrom = window.ValidFrom is null
                ? null
                : PersistedGameTimePoint.FromPoint(window.ValidFrom),
            ValidUntil = window.ValidUntil is null
                ? null
                : PersistedGameTimePoint.FromPoint(window.ValidUntil)
        };
    }

    public GameTimeWindow ToWindow()
    {
        return new GameTimeWindow(
            ValidFrom?.ToPoint(),
            ValidUntil?.ToPoint());
    }
}
