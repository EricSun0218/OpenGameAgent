using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class InteractionReasonCodes
{
    public const string StaleCatalog = "interaction_stale_catalog";

    public const string DefinitionNotFound =
        "interaction_definition_not_found";

    public const string InvalidParameters =
        "interaction_invalid_parameters";

    public const string InvalidTargetCount =
        "interaction_invalid_target_count";

    public const string UnsupportedChannel =
        "interaction_unsupported_channel";

    public const string CapabilityUnavailable =
        "interaction_capability_unavailable";

    public const string InvalidContinuation =
        "interaction_invalid_continuation";
}

public sealed class InteractionCatalogSnapshot
{
    private readonly IReadOnlyList<InteractionDefinition> _definitions;

    public InteractionCatalogSnapshot(
        string catalogId,
        long generation,
        IEnumerable<InteractionDefinition> definitions)
        : this(
            catalogId,
            generation,
            definitions,
            authoritativeCatalogDigest: null)
    {
    }

    internal InteractionCatalogSnapshot(
        string catalogId,
        long generation,
        IEnumerable<InteractionDefinition> definitions,
        string? authoritativeCatalogDigest,
        string? precomputedComponentDigest = null)
    {
        CatalogId = WorldValidation.Required(
            catalogId,
            nameof(catalogId));
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }

        var copy = WorldValidation.MaterializeBounded(
                definitions,
                WorldValidation.MaximumCatalogDefinitions,
                nameof(definitions))
            .Select(
                definition => definition
                              ?? throw new ArgumentException(
                                  "Definitions cannot contain null entries.",
                                  nameof(definitions)))
            .OrderBy(
                definition => definition.InteractionId,
                StringComparer.Ordinal)
            .ThenBy(
                definition => definition.Version,
                StringComparer.Ordinal)
            .ToArray();

        if (copy.Any(definition => definition.Details is null))
        {
            throw new ArgumentException(
                "Catalog interactions require typed definition details.",
                nameof(definitions));
        }

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].InteractionId,
                    copy[index].InteractionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    copy[index - 1].Version,
                    copy[index].Version,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The interaction catalog contains a duplicate "
                    + "interaction ID and version.",
                    nameof(definitions));
            }
        }

        Generation = generation;
        _definitions = new ReadOnlyCollection<InteractionDefinition>(copy);
        if (authoritativeCatalogDigest is not null
            && !CanonicalJsonDigest.IsSha256(authoritativeCatalogDigest))
        {
            throw new ArgumentException(
                "An authoritative catalog digest must be a lowercase "
                + "SHA-256 digest.",
                nameof(authoritativeCatalogDigest));
        }

        if (precomputedComponentDigest is not null
            && !CanonicalJsonDigest.IsSha256(precomputedComponentDigest))
        {
            throw new ArgumentException(
                "A component catalog digest must be a lowercase SHA-256 "
                + "digest.",
                nameof(precomputedComponentDigest));
        }

        ComponentDigest =
            precomputedComponentDigest ?? ComputeDigest();
        Digest = authoritativeCatalogDigest ?? ComponentDigest;
    }

    public string CatalogId { get; }

    public long Generation { get; }

    public IReadOnlyList<InteractionDefinition> Definitions => _definitions;

    public string ComponentDigest { get; }

    public string Digest { get; }

    public InteractionDefinition? Find(string interactionId, string version)
    {
        return _definitions.FirstOrDefault(
            definition => string.Equals(
                              definition.InteractionId,
                              interactionId,
                              StringComparison.Ordinal)
                          && string.Equals(
                              definition.Version,
                              version,
                              StringComparison.Ordinal));
    }

    private string ComputeDigest()
    {
        return WorldCatalogDigest.Compute(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("catalogId", CatalogId);
                writer.WriteString(
                    "generation",
                    Generation.ToString(CultureInfo.InvariantCulture));
                writer.WritePropertyName("definitions");
                writer.WriteStartArray();
                foreach (var definition in _definitions)
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "interactionId",
                        definition.InteractionId);
                    writer.WriteString("version", definition.Version);
                    writer.WriteString(
                        "contentRevision",
                        definition.ContentRevision);
                    writer.WriteString(
                        "contentDigest",
                        definition.ContentDigest);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            "definitions");
    }
}

public sealed class InteractionQueryRequest
{
    private readonly JsonElement? _context;

    public InteractionQueryRequest(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        string stateVersion,
        GameEntityIdentity actor,
        string channelId,
        IEnumerable<GameEntityIdentity>? targets = null,
        JsonElement? context = null,
        IEnumerable<string>? capabilityTags = null,
        IEnumerable<string>? definitionTags = null,
        string? definitionNamespace = null,
        int maximumResults = 64,
        string? continuationCursor = null,
        bool includeUnavailable = true)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        StateVersion = WorldValidation.Required(
            stateVersion,
            nameof(stateVersion));
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        ChannelId = WorldValidation.Required(channelId, nameof(channelId));
        Targets = InteractionIdentityList.Copy(
            targets,
            nameof(targets),
            maximumCount: 64);
        if (context.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                context.Value,
                InteractionJsonLimits.QueryContext,
                nameof(context));
            _context = context.Value.Clone();
        }

        CapabilityTags = WorldValidation.CopyKeys(
            capabilityTags,
            nameof(capabilityTags),
            maximumCount: 128);
        DefinitionTags = WorldValidation.CopyKeys(
            definitionTags,
            nameof(definitionTags),
            maximumCount: 64);
        DefinitionNamespace = WorldValidation.Optional(
            definitionNamespace,
            nameof(definitionNamespace));
        if (maximumResults is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        ContinuationCursor = WorldValidation.Optional(
            continuationCursor,
            nameof(continuationCursor),
            1_024);
        TimelineEpoch = timelineEpoch;
        SaveRevision = saveRevision;
        MaximumResults = maximumResults;
        IncludeUnavailable = includeUnavailable;
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public string StateVersion { get; }

    public GameEntityIdentity Actor { get; }

    public IReadOnlyList<GameEntityIdentity> Targets { get; }

    public string ChannelId { get; }

    public JsonElement? Context => _context?.Clone();

    public IReadOnlyList<string> CapabilityTags { get; }

    public IReadOnlyList<string> DefinitionTags { get; }

    public string? DefinitionNamespace { get; }

    public int MaximumResults { get; }

    public string? ContinuationCursor { get; }

    public bool IncludeUnavailable { get; }
}

public enum InteractionAvailabilityState
{
    Hidden = 0,
    Unavailable = 1,
    Available = 2
}

public sealed class InteractionAdmissionDecision
{
    private readonly JsonElement? _projection;

    public InteractionAdmissionDecision(
        InteractionAvailabilityState state,
        string reasonCode,
        JsonElement? projection = null)
    {
        if (!Enum.IsDefined(typeof(InteractionAvailabilityState), state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        if (projection.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                projection.Value,
                InteractionJsonLimits.QueryContext,
                nameof(projection));
            _projection = projection.Value.Clone();
        }
    }

    public InteractionAvailabilityState State { get; }

    public string ReasonCode { get; }

    /// <summary>
    /// Optional game-filtered cost, cooldown, duration, or presentation data.
    /// </summary>
    public JsonElement? Projection => _projection?.Clone();
}

public sealed class InteractionAdmissionContext
{
    public InteractionAdmissionContext(
        InteractionCatalogSnapshot catalog,
        InteractionDefinition definition,
        InteractionQueryRequest request)
    {
        Catalog = catalog
                  ?? throw new ArgumentNullException(nameof(catalog));
        Definition = definition
                     ?? throw new ArgumentNullException(nameof(definition));
        Request = request
                  ?? throw new ArgumentNullException(nameof(request));
    }

    public InteractionCatalogSnapshot Catalog { get; }

    public InteractionDefinition Definition { get; }

    public InteractionQueryRequest Request { get; }
}

/// <summary>
/// The game supplies presence, reachability, permission, visibility, and
/// capability semantics through this read-only admission boundary.
/// </summary>
public interface IInteractionAdmissionEvaluator
{
    ValueTask<InteractionAdmissionDecision> EvaluateAsync(
        InteractionAdmissionContext context,
        CancellationToken cancellationToken);
}

public sealed class InteractionQueryItem
{
    private readonly JsonElement? _projection;

    internal InteractionQueryItem(
        InteractionDefinition definition,
        InteractionAdmissionDecision decision,
        string availabilityEvidenceDigest)
    {
        InteractionId = definition.InteractionId;
        Version = definition.Version;
        ContentRevision = definition.ContentRevision;
        ContentDigest = definition.ContentDigest;
        InputSchemaId = definition.InputSchemaId;
        ParameterContract = definition.Details?.ParameterContract;
        TargetContract = definition.Details?.TargetContract;
        State = decision.State;
        ReasonCode = decision.ReasonCode;
        _projection = decision.Projection?.Clone();
        AvailabilityEvidenceDigest = availabilityEvidenceDigest;
    }

    public string InteractionId { get; }

    public string Version { get; }

    public string ContentRevision { get; }

    public string ContentDigest { get; }

    public string InputSchemaId { get; }

    public InteractionParameterContract? ParameterContract { get; }

    public InteractionTargetContract? TargetContract { get; }

    public InteractionAvailabilityState State { get; }

    public string ReasonCode { get; }

    public JsonElement? Projection => _projection?.Clone();

    public string AvailabilityEvidenceDigest { get; }
}

public sealed class InteractionQueryResult
{
    internal InteractionQueryResult(
        InteractionCatalogSnapshot catalog,
        InteractionQueryRequest request,
        IReadOnlyList<InteractionQueryItem> items,
        string? nextContinuationCursor)
    {
        CatalogId = catalog.CatalogId;
        CatalogGeneration = catalog.Generation;
        CatalogDigest = catalog.Digest;
        StateVersion = request.StateVersion;
        SaveRevision = request.SaveRevision;
        Items = items;
        NextContinuationCursor = nextContinuationCursor;
    }

    public string CatalogId { get; }

    public long CatalogGeneration { get; }

    public string CatalogDigest { get; }

    public string StateVersion { get; }

    public long SaveRevision { get; }

    public IReadOnlyList<InteractionQueryItem> Items { get; }

    public string? NextContinuationCursor { get; }
}

public sealed class InteractionQueryService
{
    public async ValueTask<InteractionQueryResult> QueryAsync(
        InteractionCatalogSnapshot catalog,
        InteractionQueryRequest request,
        IInteractionAdmissionEvaluator evaluator,
        CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (evaluator is null)
        {
            throw new ArgumentNullException(nameof(evaluator));
        }

        var after = InteractionCursor.Decode(
            request.ContinuationCursor,
            catalog.Digest);
        var items = new List<InteractionQueryItem>(
            request.MaximumResults);
        InteractionDefinition? lastExamined = null;
        var hasMore = false;
        foreach (var definition in catalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAfter(definition, after)
                || !MatchesStaticFilters(definition, request))
            {
                continue;
            }

            if (items.Count >= request.MaximumResults)
            {
                hasMore = true;
                break;
            }

            lastExamined = definition;
            var decision = await evaluator.EvaluateAsync(
                new InteractionAdmissionContext(
                    catalog,
                    definition,
                    request),
                cancellationToken).ConfigureAwait(false);
            if (decision is null)
            {
                throw new InvalidOperationException(
                    "The interaction admission evaluator returned null.");
            }

            if (decision.State == InteractionAvailabilityState.Hidden
                || (decision.State
                    == InteractionAvailabilityState.Unavailable
                    && !request.IncludeUnavailable))
            {
                continue;
            }

            items.Add(
                new InteractionQueryItem(
                    definition,
                    decision,
                    ComputeEvidenceDigest(
                        catalog,
                        request,
                        definition,
                        decision)));
        }

        var nextCursor = hasMore && lastExamined is not null
            ? InteractionCursor.Encode(
                catalog.Digest,
                lastExamined.InteractionId,
                lastExamined.Version)
            : null;
        return new InteractionQueryResult(
            catalog,
            request,
            new ReadOnlyCollection<InteractionQueryItem>(
                items.ToArray()),
            nextCursor);
    }

    private static bool MatchesStaticFilters(
        InteractionDefinition definition,
        InteractionQueryRequest request)
    {
        if (request.DefinitionNamespace is not null
            && !definition.InteractionId.StartsWith(
                request.DefinitionNamespace,
                StringComparison.Ordinal))
        {
            return false;
        }

        var details = definition.Details;
        if (details is null)
        {
            return request.DefinitionTags.Count == 0;
        }

        if (details.ChannelIds.Count > 0
            && !details.ChannelIds.Contains(
                request.ChannelId,
                StringComparer.Ordinal))
        {
            return false;
        }

        if (request.DefinitionTags.Any(
                tag => !details.Tags.Contains(
                    tag,
                    StringComparer.Ordinal)))
        {
            return false;
        }

        return details.RequiredCapabilities.All(
            capability => request.CapabilityTags.Contains(
                capability,
                StringComparer.Ordinal));
    }

    private static bool IsAfter(
        InteractionDefinition definition,
        InteractionCursorValue? after)
    {
        if (after is null)
        {
            return true;
        }

        var idComparison = string.CompareOrdinal(
            definition.InteractionId,
            after.InteractionId);
        return idComparison > 0
               || (idComparison == 0
                   && string.CompareOrdinal(
                       definition.Version,
                       after.Version) > 0);
    }

    private static string ComputeEvidenceDigest(
        InteractionCatalogSnapshot catalog,
        InteractionQueryRequest request,
        InteractionDefinition definition,
        InteractionAdmissionDecision decision)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("catalogDigest", catalog.Digest);
            writer.WriteString("stateVersion", request.StateVersion);
            writer.WriteString(
                "saveRevision",
                request.SaveRevision.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString("worldId", request.WorldId);
            writer.WriteString("timelineId", request.TimelineId);
            writer.WriteString(
                "timelineEpoch",
                request.TimelineEpoch.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString("actorId", request.Actor.EntityId);
            writer.WriteString(
                "actorIncarnation",
                request.Actor.Incarnation.ToString(
                    CultureInfo.InvariantCulture));
            writer.WriteString("channelId", request.ChannelId);
            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in request.Targets)
            {
                writer.WriteStartObject();
                writer.WriteString("entityId", target.EntityId);
                writer.WriteString(
                    "incarnation",
                    target.Incarnation.ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("capabilityTags");
            writer.WriteStartArray();
            foreach (var capability in request.CapabilityTags)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            if (request.Context.HasValue)
            {
                writer.WriteString(
                    "contextDigest",
                    CanonicalJsonDigest.ComputeSha256(
                        request.Context.Value));
            }

            writer.WriteString(
                "definitionDigest",
                definition.ContentDigest);
            writer.WriteNumber("state", (int)decision.State);
            writer.WriteString("reasonCode", decision.ReasonCode);
            if (decision.Projection.HasValue)
            {
                writer.WriteString(
                    "projectionDigest",
                    CanonicalJsonDigest.ComputeSha256(
                        decision.Projection.Value));
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return CanonicalJsonDigest.ComputeSha256(document.RootElement);
    }
}

internal sealed class InteractionCursorValue
{
    public InteractionCursorValue(
        string interactionId,
        string version)
    {
        InteractionId = interactionId;
        Version = version;
    }

    public string InteractionId { get; }

    public string Version { get; }
}

internal static class InteractionCursor
{
    public static string Encode(
        string catalogDigest,
        string interactionId,
        string version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(catalogDigest);
            writer.WriteStringValue(interactionId);
            writer.WriteStringValue(version);
            writer.WriteEndArray();
        }

        return Convert.ToBase64String(buffer.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static InteractionCursorValue? Decode(
        string? cursor,
        string catalogDigest)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            using var document = JsonDocument.Parse(
                Convert.FromBase64String(padded));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() != 3
                || root[0].ValueKind != JsonValueKind.String
                || root[1].ValueKind != JsonValueKind.String
                || root[2].ValueKind != JsonValueKind.String
                || !string.Equals(
                    root[0].GetString(),
                    catalogDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            return new InteractionCursorValue(
                WorldValidation.Required(
                    root[1].GetString(),
                    nameof(cursor)),
                WorldValidation.Required(
                    root[2].GetString(),
                    nameof(cursor),
                    96));
        }
        catch (Exception exception)
            when (exception is FormatException
                  or JsonException
                  or ArgumentException)
        {
            throw InvalidCursor();
        }
    }

    private static ArgumentException InvalidCursor()
    {
        return new ArgumentException(
            InteractionReasonCodes.InvalidContinuation,
            "cursor");
    }
}

internal static class InteractionIdentityList
{
    public static IReadOnlyList<GameEntityIdentity> Copy(
        IEnumerable<GameEntityIdentity>? values,
        string parameterName,
        int maximumCount)
    {
        var copy = WorldValidation.MaterializeBounded(
                values ?? Array.Empty<GameEntityIdentity>(),
                maximumCount,
                parameterName)
            .Select(
                value => value
                         ?? throw new ArgumentException(
                             "Identity lists cannot contain null entries.",
                             parameterName))
            .ToArray();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in copy)
        {
            var key = value.EntityId
                      + "\u001f"
                      + value.Incarnation.ToString(
                          System.Globalization.CultureInfo.InvariantCulture);
            if (!keys.Add(key))
            {
                throw new ArgumentException(
                    "The identity list contains duplicate incarnations.",
                    parameterName);
            }
        }

        return new ReadOnlyCollection<GameEntityIdentity>(copy);
    }
}
