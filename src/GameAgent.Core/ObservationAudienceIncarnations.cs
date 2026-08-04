using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Associates one protocol audience ID with the exact game-entity lifetime
/// that is allowed to receive an observation.
/// </summary>
public sealed class ObservationAudienceIncarnationBinding
{
    public ObservationAudienceIncarnationBinding(
        string audienceId,
        GameEntityIdentity entity)
    {
        AudienceId = RuntimeGuard.RequiredId(
            audienceId,
            nameof(audienceId));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
    }

    public string AudienceId { get; }

    public GameEntityIdentity Entity { get; }
}

/// <summary>
/// Stores bounded audience-to-entity-incarnation bindings in an observation
/// extension. The protocol audience remains an agent or group concern while
/// the binding identifies the exact in-game entity lifetime behind it.
/// </summary>
public static class ObservationAudienceIncarnations
{
    public const string ExtensionName = "audienceIncarnations";

    public const int MaxBindings = 2_048;

    public static void Attach(
        ObservationEnvelope observation,
        IEnumerable<ObservationAudienceIncarnationBinding> bindings)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        if (bindings is null)
        {
            throw new ArgumentNullException(nameof(bindings));
        }

        ProtocolValidator.EnsureValid(observation);
        var snapshot = RuntimeInputGuard.CopyBounded(
            bindings,
            MaxBindings,
            binding => binding
                       ?? throw new ArgumentException(
                           "Audience incarnation bindings cannot contain null entries.",
                           nameof(bindings)),
            nameof(bindings),
            "observation_audience_incarnation_count_exceeded");
        var byAudience = new Dictionary<
            string,
            ObservationAudienceIncarnationBinding>(StringComparer.Ordinal);
        foreach (var binding in snapshot)
        {
            if (!byAudience.TryAdd(binding.AudienceId, binding))
            {
                throw new ArgumentException(
                    "Audience incarnation bindings must use unique audience IDs.",
                    nameof(bindings));
            }
        }

        var audienceIds = observation.Visibility.AudienceIds;
        if (audienceIds.Count > MaxBindings
            || byAudience.Count != audienceIds.Count
            || audienceIds.Any(id => !byAudience.ContainsKey(id)))
        {
            throw new ArgumentException(
                "Audience incarnation bindings must exactly cover the observation audience.",
                nameof(bindings));
        }

        if (!observation.Extensions.ContainsKey(ExtensionName)
            && observation.Extensions.Count
            >= ProtocolLimits.MaxProtocolExtensions)
        {
            throw new RuntimeContentLimitException(
                nameof(observation),
                "observation_extensions_exceeded",
                "The observation has no capacity for audience incarnation metadata.");
        }

        var extension = JsonArrayBuilder.Array(
            byAudience.Values
                .OrderBy(item => item.AudienceId, StringComparer.Ordinal)
                .Select(
                    item => JsonArrayBuilder.Object(
                        (
                            "audienceId",
                            JsonArrayBuilder.String(item.AudienceId)),
                        (
                            "entityId",
                            JsonArrayBuilder.String(item.Entity.EntityId)),
                        (
                            "incarnation",
                            JsonArrayBuilder.String(
                                item.Entity.Incarnation.ToString(
                                    System.Globalization.CultureInfo
                                        .InvariantCulture))))));
        var hadPrevious = observation.Extensions.TryGetValue(
            ExtensionName,
            out var previous);
        observation.Extensions[ExtensionName] = extension;
        try
        {
            ProtocolValidator.EnsureValid(observation);
        }
        catch
        {
            if (hadPrevious)
            {
                observation.Extensions[ExtensionName] = previous;
            }
            else
            {
                observation.Extensions.Remove(ExtensionName);
            }
            throw;
        }
    }

    public static bool TryRead(
        ObservationEnvelope observation,
        out IReadOnlyList<ObservationAudienceIncarnationBinding> bindings)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        var result = ReadForAdmission(observation);
        bindings = result.Bindings;
        return result.State == AudienceIncarnationBindingState.Valid;
    }

    internal static AudienceIncarnationReadResult ReadForAdmission(
        ObservationEnvelope observation)
    {
        if (observation.Extensions is null
            || !observation.Extensions.TryGetValue(
                ExtensionName,
                out var extension))
        {
            return AudienceIncarnationReadResult.Missing;
        }

        if (extension.ValueKind != JsonValueKind.Array
            || extension.GetArrayLength() > MaxBindings
            || observation.Visibility is null
            || observation.Visibility.AudienceIds.Count > MaxBindings)
        {
            return AudienceIncarnationReadResult.Invalid;
        }

        var bindings =
            new List<ObservationAudienceIncarnationBinding>(
                extension.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in extension.EnumerateArray())
        {
            if (!TryReadBinding(item, out var binding)
                || binding is null
                || !seen.Add(binding.AudienceId))
            {
                return AudienceIncarnationReadResult.Invalid;
            }

            bindings.Add(binding);
        }

        var audienceIds = observation.Visibility.AudienceIds;
        if (bindings.Count != audienceIds.Count
            || audienceIds.Any(id => !seen.Contains(id)))
        {
            return AudienceIncarnationReadResult.Invalid;
        }

        bindings.Sort(
            (left, right) => StringComparer.Ordinal.Compare(
                left.AudienceId,
                right.AudienceId));
        return new AudienceIncarnationReadResult(
            AudienceIncarnationBindingState.Valid,
            bindings);
    }

    private static bool TryReadBinding(
        JsonElement value,
        out ObservationAudienceIncarnationBinding? binding)
    {
        binding = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Add(property.Name)
                || (property.Name != "audienceId"
                    && property.Name != "entityId"
                    && property.Name != "incarnation"))
            {
                return false;
            }
        }

        if (properties.Count != 3
            || !value.TryGetProperty("audienceId", out var audienceId)
            || audienceId.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("entityId", out var entityId)
            || entityId.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("incarnation", out var incarnation)
            || incarnation.ValueKind != JsonValueKind.String
            || !long.TryParse(
                incarnation.GetString(),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var incarnationValue)
            || !string.Equals(
                incarnation.GetString(),
                incarnationValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || incarnationValue < 0)
        {
            return false;
        }

        try
        {
            binding = new ObservationAudienceIncarnationBinding(
                audienceId.GetString()!,
                new GameEntityIdentity(
                    entityId.GetString()!,
                    incarnationValue));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal enum AudienceIncarnationBindingState
{
    Missing,
    Invalid,
    Valid
}

internal sealed class AudienceIncarnationReadResult
{
    private static readonly IReadOnlyList<
        ObservationAudienceIncarnationBinding> Empty =
        Array.Empty<ObservationAudienceIncarnationBinding>();

    public static AudienceIncarnationReadResult Missing { get; } =
        new(AudienceIncarnationBindingState.Missing, Empty);

    public static AudienceIncarnationReadResult Invalid { get; } =
        new(AudienceIncarnationBindingState.Invalid, Empty);

    public AudienceIncarnationReadResult(
        AudienceIncarnationBindingState state,
        IEnumerable<ObservationAudienceIncarnationBinding> bindings)
    {
        State = state;
        Bindings = new ReadOnlyCollection<
            ObservationAudienceIncarnationBinding>(
                bindings.ToArray());
    }

    public AudienceIncarnationBindingState State { get; }

    public IReadOnlyList<ObservationAudienceIncarnationBinding> Bindings
    {
        get;
    }
}
