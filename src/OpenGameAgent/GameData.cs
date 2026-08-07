using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenGameAgent;

public readonly struct GameMoment : IEquatable<GameMoment>, IComparable<GameMoment>
{
    [JsonConstructor]
    public GameMoment(string timelineId, long tick, string? calendarJson = null)
    {
        TimelineId = RequireId(timelineId, nameof(timelineId));
        Tick = tick;
        CalendarJson = calendarJson is null ? null : GameJson.RequireValid(calendarJson, nameof(calendarJson));
    }

    public string TimelineId { get; }

    public long Tick { get; }

    public string? CalendarJson { get; }

    public int CompareTo(GameMoment other)
    {
        EnsureValid(nameof(GameMoment));
        other.EnsureValid(nameof(other));
        if (!string.Equals(TimelineId, other.TimelineId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Game moments from different timelines cannot be ordered.");
        }

        return Tick.CompareTo(other.Tick);
    }

    public bool Equals(GameMoment other) =>
        Tick == other.Tick
        && string.Equals(TimelineId, other.TimelineId, StringComparison.Ordinal)
        && string.Equals(CalendarJson, other.CalendarJson, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameMoment other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = TimelineId is null ? 0 : StringComparer.Ordinal.GetHashCode(TimelineId);
            hash = (hash * 397) ^ Tick.GetHashCode();
            hash = (hash * 397) ^ (CalendarJson is null ? 0 : StringComparer.Ordinal.GetHashCode(CalendarJson));
            return hash;
        }
    }

    public static bool operator ==(GameMoment left, GameMoment right) => left.Equals(right);

    public static bool operator !=(GameMoment left, GameMoment right) => !left.Equals(right);

    internal GameMoment EnsureValid(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(TimelineId))
        {
            throw new ArgumentException("A valid game moment is required.", parameterName);
        }

        return this;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A timeline ID is required.", parameterName);
        }

        return value;
    }
}

public sealed class GameInput
{
    public GameInput(
        string sessionId,
        string actorId,
        string type,
        string payloadJson,
        GameMoment moment,
        string? inputId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        ActorId = GameJson.RequireId(actorId, nameof(actorId));
        Type = GameJson.RequireId(type, nameof(type));
        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Moment = moment.EnsureValid(nameof(moment));
        InputId = inputId is null ? Guid.NewGuid().ToString("N") : GameJson.RequireId(inputId, nameof(inputId));
        var copiedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                throw new ArgumentException("Metadata keys must be non-empty and values cannot be null.", nameof(metadata));
            }

            copiedMetadata.Add(pair.Key, pair.Value);
        }

        Metadata = new ReadOnlyDictionary<string, string>(copiedMetadata);
    }

    public string InputId { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string Type { get; }

    public string PayloadJson { get; }

    public GameMoment Moment { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}

public sealed class GameContextSlice
{
    public GameContextSlice(string source, string payloadJson, int priority = 0, string? version = null)
    {
        Source = GameJson.RequireId(source, nameof(source));
        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Priority = priority;
        Version = version;
    }

    public string Source { get; }

    public string PayloadJson { get; }

    public int Priority { get; }

    public string? Version { get; }
}

public sealed class GameRuntimeLimits
{
    public int MaxInputJsonCharacters { get; set; } = 1_000_000;

    public int MaxCalendarJsonCharacters { get; set; } = 1_000_000;

    public int MaxContextSlices { get; set; } = 128;

    public int MaxContextJsonCharacters { get; set; } = 4_000_000;

    public int MaxMetadataEntries { get; set; } = 64;

    public int MaxMetadataKeyCharacters { get; set; } = 256;

    public int MaxMetadataValueCharacters { get; set; } = 16_384;

    public int MaxIdentifierCharacters { get; set; } = 512;

    public int MaxConcurrentActors { get; set; } = 16;

    public int MaxQueuedInputsPerActor { get; set; } = 64;

    public int MaxSkillsPerRun { get; set; } = 16;

    public int MaxSkillCharactersPerRun { get; set; } = 1_000_000;

    internal GameRuntimeLimits CopyAndValidate()
    {
        var copy = (GameRuntimeLimits)MemberwiseClone();
        RequireRange(copy.MaxInputJsonCharacters, 2, 100_000_000, nameof(MaxInputJsonCharacters));
        RequireRange(copy.MaxCalendarJsonCharacters, 2, 100_000_000, nameof(MaxCalendarJsonCharacters));
        RequireRange(copy.MaxContextSlices, 0, 100_000, nameof(MaxContextSlices));
        RequireRange(copy.MaxContextJsonCharacters, 2, 100_000_000, nameof(MaxContextJsonCharacters));
        RequireRange(copy.MaxMetadataEntries, 0, 100_000, nameof(MaxMetadataEntries));
        RequireRange(copy.MaxMetadataKeyCharacters, 1, 100_000, nameof(MaxMetadataKeyCharacters));
        RequireRange(copy.MaxMetadataValueCharacters, 0, 100_000_000, nameof(MaxMetadataValueCharacters));
        RequireRange(copy.MaxIdentifierCharacters, 1, 16_384, nameof(MaxIdentifierCharacters));
        RequireRange(copy.MaxConcurrentActors, 1, 4096, nameof(MaxConcurrentActors));
        RequireRange(copy.MaxQueuedInputsPerActor, 1, 100_000, nameof(MaxQueuedInputsPerActor));
        RequireRange(copy.MaxSkillsPerRun, 0, 10_000, nameof(MaxSkillsPerRun));
        RequireRange(copy.MaxSkillCharactersPerRun, 0, 100_000_000, nameof(MaxSkillCharactersPerRun));
        return copy;
    }

    internal void Validate(GameInput input)
    {
        if (input.PayloadJson.Length > MaxInputJsonCharacters)
        {
            throw new GameRuntimeLimitException(nameof(MaxInputJsonCharacters), "The input payload is too large.");
        }

        if ((input.Moment.CalendarJson?.Length ?? 0) > MaxCalendarJsonCharacters)
        {
            throw new GameRuntimeLimitException(nameof(MaxCalendarJsonCharacters), "The input game calendar is too large.");
        }

        if (input.Metadata.Count > MaxMetadataEntries)
        {
            throw new GameRuntimeLimitException(nameof(MaxMetadataEntries), "The input has too many metadata entries.");
        }

        foreach (var value in new[] { input.InputId, input.SessionId, input.ActorId, input.Type, input.Moment.TimelineId })
        {
            if (value.Length > MaxIdentifierCharacters)
            {
                throw new GameRuntimeLimitException(nameof(MaxIdentifierCharacters), "An input identifier is too large.");
            }
        }


        foreach (var pair in input.Metadata)
        {
            if (pair.Key.Length > MaxMetadataKeyCharacters)
            {
                throw new GameRuntimeLimitException(nameof(MaxMetadataKeyCharacters), "An input metadata key is too large.");
            }

            if (pair.Value.Length > MaxMetadataValueCharacters)
            {
                throw new GameRuntimeLimitException(nameof(MaxMetadataValueCharacters), "An input metadata value is too large.");
            }
        }
    }

    internal void Validate(IReadOnlyList<GameContextSlice> slices)
    {
        if (slices.Count > MaxContextSlices)
        {
            throw new GameRuntimeLimitException(nameof(MaxContextSlices), "Too many context slices were returned.");
        }

        var total = 0L;
        foreach (var slice in slices)
        {
            if (slice is null)
            {
                throw new InvalidOperationException("The game context provider returned a null context slice.");
            }

            total += slice.PayloadJson.Length;
            if (slice.Source.Length > MaxIdentifierCharacters || (slice.Version?.Length ?? 0) > MaxIdentifierCharacters)
            {
                throw new GameRuntimeLimitException(nameof(MaxIdentifierCharacters), "A context identifier is too large.");
            }
        }

        if (total > MaxContextJsonCharacters)
        {
            throw new GameRuntimeLimitException(nameof(MaxContextJsonCharacters), "The combined game context is too large.");
        }
    }

    internal void Validate(IReadOnlyList<GameSkill> skills)
    {
        if (skills.Count > MaxSkillsPerRun)
        {
            throw new GameRuntimeLimitException(nameof(MaxSkillsPerRun), "Too many skills were selected.");
        }

        var total = 0L;
        foreach (var skill in skills)
        {
            if (skill is null)
            {
                throw new InvalidOperationException("The game skill source returned a null skill.");
            }

            total += skill.SkillId.Length;
            total += skill.Name.Length;
            total += skill.Description.Length;
            total += skill.Instructions.Length;
            total += skill.InputTypes.Sum(name => (long)name.Length);
            total += skill.ToolNames.Sum(name => (long)name.Length);
            total += skill.Metadata.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
            if (skill.SkillId.Length > MaxIdentifierCharacters
                || skill.Name.Length > MaxIdentifierCharacters
                || skill.InputTypes.Any(value => value.Length > MaxIdentifierCharacters)
                || skill.ToolNames.Any(value => value.Length > MaxIdentifierCharacters)
                || skill.Metadata.Any(pair => pair.Key.Length > MaxMetadataKeyCharacters
                                              || pair.Value.Length > MaxMetadataValueCharacters))
            {
                throw new GameRuntimeLimitException(
                    nameof(MaxIdentifierCharacters),
                    "The selected skill contains an oversized identifier or metadata value.");
            }

            if (total > MaxSkillCharactersPerRun)
            {
                throw new GameRuntimeLimitException(
                    nameof(MaxSkillCharactersPerRun),
                    "The selected skill content is too large.");
            }
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
        }
    }
}

public sealed class GameRuntimeLimitException : Exception
{
    public GameRuntimeLimitException(string limit, string message)
        : base(message)
    {
        Limit = limit;
    }

    public string Limit { get; }
}

internal static class GameJson
{
    public static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }

        return value;
    }

    public static string RequireValid(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            EnsureUnambiguous(document.RootElement, parameterName);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must contain valid JSON.", parameterName, exception);
        }
    }

    public static JsonElement ParseElement(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    public static string JoinIds(params string[] values)
    {
        if (values is null || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Joined identifiers must be non-empty.", nameof(values));
        }

        return string.Join(":", values.Select(Uri.EscapeDataString));
    }

    private static void EnsureUnambiguous(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("JSON objects cannot contain duplicate property names.", parameterName);
                }

                EnsureUnambiguous(property.Value, parameterName);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item, parameterName);
            }
        }
    }
}
