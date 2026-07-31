using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

/// <summary>
/// A typed, idempotently identified cause for one evolution wave.
/// Timeline epochs keep rewound or replaced histories isolated.
/// </summary>
public class WorldEvolutionTrigger
{
    private static readonly JsonValueLimits PayloadLimits = new(
        maxUtf8Bytes: 65_536,
        maxDepth: 24,
        maxNodes: 4_096,
        maxStringUtf8Bytes: 16_384,
        maxContainerItems: 2_048);

    public WorldEvolutionTrigger(
        string triggerId,
        string kind,
        string worldId,
        string timelineId,
        long timelineEpoch,
        GameTimePoint? gameTime,
        JsonElement? payload = null)
    {
        TriggerId = WorldValidation.Required(
            triggerId,
            nameof(triggerId));
        Kind = WorldValidation.Required(kind, nameof(kind));
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (gameTime is not null
            && (!string.Equals(
                    gameTime.TimelineId,
                    timelineId,
                    StringComparison.Ordinal)
                || gameTime.Epoch != timelineEpoch))
        {
            throw new ArgumentException(
                "Game time must use the trigger timeline and epoch.",
                nameof(gameTime));
        }

        TimelineEpoch = timelineEpoch;
        GameTime = gameTime;
        if (payload.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                payload.Value,
                PayloadLimits,
                nameof(payload));
            Payload = payload.Value.Clone();
            PayloadDigest = CanonicalJsonDigest.ComputeSha256(Payload.Value);
        }
    }

    public string TriggerId { get; }

    public string Kind { get; }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public GameTimePoint? GameTime { get; }

    /// <summary>
    /// Optional bounded structured trigger data. Hosts include every
    /// behavior-affecting portable input here rather than in HostContext.
    /// </summary>
    public JsonElement? Payload { get; }

    public string? PayloadDigest { get; }
}
