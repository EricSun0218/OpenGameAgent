using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Identifies one incarnation of a game entity. Incarnation prevents an
/// identifier reused after despawn, respawn, possession, or reincarnation
/// from inheriting another entity lifetime's state.
/// </summary>
public sealed class GameEntityIdentity
{
    public GameEntityIdentity(string entityId, long incarnation)
    {
        EntityId = RuntimeGuard.RequiredUtf8(
            entityId,
            128,
            nameof(entityId));
        if (incarnation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        Incarnation = incarnation;
    }

    public string EntityId { get; }

    public long Incarnation { get; }

    public bool IsSameIncarnation(GameEntityIdentity? other)
    {
        return other is not null
               && string.Equals(
                   EntityId,
                   other.EntityId,
                   StringComparison.Ordinal)
               && Incarnation == other.Incarnation;
    }
}

/// <summary>
/// Names the causal position of an observation or proposed action without
/// defining any game-specific conflict-resolution rule.
/// </summary>
public sealed class GameCausalityStamp
{
    public GameCausalityStamp(
        string eventId,
        string basedOnStateVersion,
        IEnumerable<string>? parentEventIds = null)
    {
        EventId = RuntimeGuard.RequiredUtf8(
            eventId,
            128,
            nameof(eventId));
        BasedOnStateVersion = RuntimeGuard.RequiredUtf8(
            basedOnStateVersion,
            128,
            nameof(basedOnStateVersion));
        ParentEventIds = RuntimeGuard.CopyStrings(
            parentEventIds ?? Array.Empty<string>(),
            64,
            128,
            nameof(parentEventIds),
            sort: true,
            requireUnique: true);
        if (ParentEventIds.Contains(eventId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A causal event cannot be its own parent.",
                nameof(parentEventIds));
        }
    }

    public string EventId { get; }

    public string BasedOnStateVersion { get; }

    public IReadOnlyList<string> ParentEventIds { get; }
}

/// <summary>
/// Marks whose knowledge a fact belongs to and how the game classifies it.
/// The classification is an open game-defined string so games can represent
/// observations, reports, deductions, rumors, dreams, or lies.
/// </summary>
public sealed class GameKnowledgePerspective
{
    public GameKnowledgePerspective(
        GameEntityIdentity observer,
        string knowledgeKind,
        GameEntityIdentity? source = null)
    {
        Observer = observer
                   ?? throw new ArgumentNullException(nameof(observer));
        KnowledgeKind = RuntimeGuard.RequiredUtf8(
            knowledgeKind,
            128,
            nameof(knowledgeKind));
        Source = source;
    }

    public GameEntityIdentity Observer { get; }

    public string KnowledgeKind { get; }

    public GameEntityIdentity? Source { get; }
}

/// <summary>
/// A comparable point on one game-defined clock. Wall-clock time is not
/// implied. Games choose the clock, timeline, epoch, and tick meanings.
/// </summary>
public sealed class GameTimePoint
{
    public GameTimePoint(
        string clockId,
        string timelineId,
        long epoch,
        long tick)
    {
        ClockId = RuntimeGuard.RequiredUtf8(
            clockId,
            128,
            nameof(clockId));
        TimelineId = RuntimeGuard.RequiredUtf8(
            timelineId,
            128,
            nameof(timelineId));
        if (epoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        Epoch = epoch;
        Tick = tick;
    }

    public string ClockId { get; }

    public string TimelineId { get; }

    /// <summary>
    /// Separates resets or rewinds that reuse tick numbers.
    /// </summary>
    public long Epoch { get; }

    public long Tick { get; }

    public bool IsComparableTo(GameTimePoint other)
    {
        return other is not null
               && string.Equals(ClockId, other.ClockId, StringComparison.Ordinal)
               && string.Equals(
                   TimelineId,
                   other.TimelineId,
                   StringComparison.Ordinal)
               && Epoch == other.Epoch;
    }

    public int CompareTo(GameTimePoint other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        if (!IsComparableTo(other))
        {
            throw new InvalidOperationException(
                "Game-time points from different coordinates are not comparable.");
        }

        return Tick.CompareTo(other.Tick);
    }
}

public sealed class GameTimeWindow
{
    public GameTimeWindow(
        GameTimePoint? validFrom = null,
        GameTimePoint? validUntil = null)
    {
        if (validFrom is null && validUntil is null)
        {
            throw new ArgumentException(
                "A game-time window requires at least one bound.");
        }

        if (validFrom is not null
            && validUntil is not null
            && (!validFrom.IsComparableTo(validUntil)
                || validFrom.Tick > validUntil.Tick))
        {
            throw new ArgumentException(
                "Game-time window bounds are incompatible or reversed.");
        }

        ValidFrom = validFrom;
        ValidUntil = validUntil;
    }

    public GameTimePoint? ValidFrom { get; }

    /// <summary>
    /// Exclusive upper bound.
    /// </summary>
    public GameTimePoint? ValidUntil { get; }

    public bool Contains(GameTimePoint point)
    {
        if (point is null)
        {
            throw new ArgumentNullException(nameof(point));
        }

        var coordinate = ValidFrom ?? ValidUntil!;
        if (!coordinate.IsComparableTo(point))
        {
            return false;
        }

        return (ValidFrom is null || point.Tick >= ValidFrom.Tick)
               && (ValidUntil is null || point.Tick < ValidUntil.Tick);
    }
}

/// <summary>
/// Identifies the semantic slice from which game context was observed.
/// Optional fields remain game-owned rather than becoming runtime rules.
/// </summary>
public sealed class GameContextCoordinate
{
    public GameContextCoordinate(
        string worldId,
        string timelineId,
        long saveRevision,
        GameEntityIdentity? observer = null,
        string? sceneId = null,
        string? regionId = null,
        string? stateVersion = null,
        GameTimePoint? gameTime = null,
        GameCausalityStamp? causality = null,
        string? sessionId = null)
    {
        WorldId = RuntimeGuard.RequiredUtf8(
            worldId,
            128,
            nameof(worldId));
        TimelineId = RuntimeGuard.RequiredUtf8(
            timelineId,
            128,
            nameof(timelineId));
        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        SaveRevision = saveRevision;
        Observer = observer;
        SceneId = Optional(sceneId, nameof(sceneId));
        RegionId = Optional(regionId, nameof(regionId));
        StateVersion = Optional(stateVersion, nameof(stateVersion), 128);
        GameTime = gameTime;
        Causality = causality;
        SessionId = Optional(sessionId, nameof(sessionId));
        if (gameTime is not null
            && !string.Equals(
                timelineId,
                gameTime.TimelineId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Context and game-time timelines must match.",
                nameof(gameTime));
        }

        if (causality is not null
            && stateVersion is not null
            && !string.Equals(
                causality.BasedOnStateVersion,
                stateVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Context and causal state versions must match.",
                nameof(causality));
        }
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long SaveRevision { get; }

    public GameEntityIdentity? Observer { get; }

    public string? SceneId { get; }

    public string? RegionId { get; }

    public string? StateVersion { get; }

    public GameTimePoint? GameTime { get; }

    public GameCausalityStamp? Causality { get; }

    /// <summary>
    /// Optional explicit binding to the immutable run session. A missing
    /// value inherits the run session; a supplied value must match it.
    /// </summary>
    public string? SessionId { get; }

    private static string? Optional(
        string? value,
        string name,
        int maxUtf8Bytes = 128)
    {
        return value is null
            ? null
            : RuntimeGuard.RequiredUtf8(value, maxUtf8Bytes, name);
    }
}

/// <summary>
/// Stores and restores a game-semantic coordinate in the protocol extension
/// bag so it survives durable run snapshots without making one game's clock,
/// topology, or causality rules part of the base wire protocol.
/// </summary>
public static class GameContextEnvelope
{
    public const string ExtensionName = "gameContext";

    public static void Attach(AgentRun run, GameContextCoordinate coordinate)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (coordinate is null)
        {
            throw new ArgumentNullException(nameof(coordinate));
        }

        ProtocolValidator.EnsureValid(run);
        if (!string.IsNullOrWhiteSpace(run.WorldId)
            && !string.Equals(
                run.WorldId,
                coordinate.WorldId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Run and game-context worlds must match.",
                nameof(coordinate));
        }

        if (coordinate.SessionId is not null
            && !string.Equals(
                run.SessionId,
                coordinate.SessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Run and game-context sessions must match.",
                nameof(coordinate));
        }

        if (!run.Extensions.ContainsKey(ExtensionName)
            && run.Extensions.Count >= ProtocolLimits.MaxProtocolExtensions)
        {
            throw new RuntimeContentLimitException(
                nameof(run),
                "run_extensions_exceeded",
                "The run has no capacity for game-context metadata.");
        }

        var extension = ToJson(BindSession(coordinate, run.SessionId));
        var hadPrevious = run.Extensions.TryGetValue(
            ExtensionName,
            out var previous);
        run.Extensions[ExtensionName] = extension;
        try
        {
            ProtocolValidator.EnsureValid(run);
        }
        catch
        {
            if (hadPrevious)
            {
                run.Extensions[ExtensionName] = previous;
            }
            else
            {
                run.Extensions.Remove(ExtensionName);
            }
            throw;
        }
    }

    public static bool TryRead(
        AgentRun run,
        out GameContextCoordinate? coordinate)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        coordinate = null;
        return run.Extensions.TryGetValue(ExtensionName, out var value)
               && TryRead(value, out coordinate);
    }

    internal static GameContextCoordinate? ValidateForRun(
        AgentRun run,
        string parameterName)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (!run.Extensions.TryGetValue(ExtensionName, out var value))
        {
            return null;
        }

        if (!TryRead(value, out var coordinate) || coordinate is null)
        {
            throw new ArgumentException(
                "The known game-context extension is malformed.",
                parameterName);
        }

        if (!string.Equals(
                run.WorldId,
                coordinate.WorldId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Run and game-context worlds must match.",
                parameterName);
        }

        if (!string.Equals(
                run.SessionId,
                coordinate.SessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Run and game-context sessions must match.",
                parameterName);
        }

        return coordinate;
    }

    private static GameContextCoordinate BindSession(
        GameContextCoordinate coordinate,
        string? sessionId)
    {
        if (string.Equals(
                coordinate.SessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return coordinate;
        }

        return new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            coordinate.Observer,
            coordinate.SceneId,
            coordinate.RegionId,
            coordinate.StateVersion,
            coordinate.GameTime,
            coordinate.Causality,
            sessionId);
    }

    public static JsonElement ToJson(GameContextCoordinate coordinate)
    {
        if (coordinate is null)
        {
            throw new ArgumentNullException(nameof(coordinate));
        }
        var properties = new List<(string Name, JsonElement Value)>
        {
            ("worldId", JsonArrayBuilder.String(coordinate.WorldId)),
            ("timelineId", JsonArrayBuilder.String(coordinate.TimelineId)),
            ("saveRevision", JsonArrayBuilder.String(
                coordinate.SaveRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)))
        };
        Add(properties, "sceneId", coordinate.SceneId);
        Add(properties, "regionId", coordinate.RegionId);
        Add(properties, "stateVersion", coordinate.StateVersion);
        Add(properties, "sessionId", coordinate.SessionId);
        if (coordinate.Observer is not null)
        {
            properties.Add((
                "observer",
                JsonArrayBuilder.Object(
                    ("entityId", JsonArrayBuilder.String(
                        coordinate.Observer.EntityId)),
                    ("incarnation", JsonArrayBuilder.String(
                        coordinate.Observer.Incarnation.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture))))));
        }

        if (coordinate.GameTime is not null)
        {
            properties.Add((
                "gameTime",
                JsonArrayBuilder.Object(
                    ("clockId", JsonArrayBuilder.String(
                        coordinate.GameTime.ClockId)),
                    ("timelineId", JsonArrayBuilder.String(
                        coordinate.GameTime.TimelineId)),
                    ("epoch", JsonArrayBuilder.String(
                        coordinate.GameTime.Epoch.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture))),
                    ("tick", JsonArrayBuilder.String(
                        coordinate.GameTime.Tick.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture))))));
        }

        if (coordinate.Causality is not null)
        {
            properties.Add((
                "causality",
                JsonArrayBuilder.Object(
                    ("eventId", JsonArrayBuilder.String(
                        coordinate.Causality.EventId)),
                    ("basedOnStateVersion", JsonArrayBuilder.String(
                        coordinate.Causality.BasedOnStateVersion)),
                    ("parentEventIds", JsonArrayBuilder.Array(
                        coordinate.Causality.ParentEventIds.Select(
                            JsonArrayBuilder.String))))));
        }

        return JsonArrayBuilder.Object(properties.ToArray());
    }

    public static bool TryRead(
        JsonElement value,
        out GameContextCoordinate? coordinate)
    {
        coordinate = null;
        try
        {
            if (value.ValueKind != JsonValueKind.Object
                || !RequiredString(value, "worldId", out var worldId)
                || !RequiredString(value, "timelineId", out var timelineId)
                || !TryCanonicalInt64(
                    value,
                    "saveRevision",
                    out var saveRevision))
            {
                return false;
            }

            GameEntityIdentity? observer = null;
            if (value.TryGetProperty("observer", out var observerJson))
            {
                if (observerJson.ValueKind != JsonValueKind.Object
                    || !RequiredString(
                        observerJson,
                        "entityId",
                        out var entityId)
                    || !TryCanonicalInt64(
                        observerJson,
                        "incarnation",
                        out var incarnation))
                {
                    return false;
                }

                observer = new GameEntityIdentity(entityId!, incarnation);
            }

            GameTimePoint? gameTime = null;
            if (value.TryGetProperty("gameTime", out var gameTimeJson))
            {
                if (gameTimeJson.ValueKind != JsonValueKind.Object
                    || !RequiredString(
                        gameTimeJson,
                        "clockId",
                        out var clockId)
                    || !RequiredString(
                        gameTimeJson,
                        "timelineId",
                        out var timeTimelineId)
                    || !TryCanonicalInt64(
                        gameTimeJson,
                        "epoch",
                        out var epoch)
                    || !TryCanonicalInt64(
                        gameTimeJson,
                        "tick",
                        out var tick))
                {
                    return false;
                }

                gameTime = new GameTimePoint(
                    clockId!,
                    timeTimelineId!,
                    epoch,
                    tick);
            }

            GameCausalityStamp? causality = null;
            if (value.TryGetProperty("causality", out var causalityJson))
            {
                if (causalityJson.ValueKind != JsonValueKind.Object
                    || !RequiredString(
                        causalityJson,
                        "eventId",
                        out var eventId)
                    || !RequiredString(
                        causalityJson,
                        "basedOnStateVersion",
                        out var causalVersion)
                    || !ReadStringArray(
                        causalityJson,
                        "parentEventIds",
                        out var parents))
                {
                    return false;
                }

                causality = new GameCausalityStamp(
                    eventId!,
                    causalVersion!,
                    parents);
            }

            if (!TryOptionalString(value, "sceneId", out var sceneId)
                || !TryOptionalString(value, "regionId", out var regionId)
                || !TryOptionalString(
                    value,
                    "stateVersion",
                    out var stateVersion)
                || !TryOptionalString(
                    value,
                    "sessionId",
                    out var sessionId))
            {
                return false;
            }

            coordinate = new GameContextCoordinate(
                worldId!,
                timelineId!,
                saveRevision,
                observer,
                sceneId,
                regionId,
                stateVersion,
                gameTime,
                causality,
                sessionId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Add(
        ICollection<(string Name, JsonElement Value)> properties,
        string name,
        string? value)
    {
        if (value is not null)
        {
            properties.Add((name, JsonArrayBuilder.String(value)));
        }
    }

    private static bool RequiredString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool TryOptionalString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        result = property.GetString();
        return true;
    }

    private static bool TryCanonicalInt64(
        JsonElement value,
        string name,
        out long result)
    {
        result = 0;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        return text is not null
               && long.TryParse(
                   text,
                   System.Globalization.NumberStyles.AllowLeadingSign,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out result)
               && string.Equals(
                   text,
                   result.ToString(
                       System.Globalization.CultureInfo.InvariantCulture),
                   StringComparison.Ordinal);
    }

    private static bool ReadStringArray(
        JsonElement value,
        string name,
        out IReadOnlyList<string> result)
    {
        result = Array.Empty<string>();
        if (!value.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return false;
            }

            items.Add(item.GetString()!);
        }

        result = new ReadOnlyCollection<string>(items);
        return true;
    }
}
