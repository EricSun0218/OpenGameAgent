using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Reserved model-facing controls used to discover and activate admitted
/// skills. Applications cannot register game tools with these names.
/// </summary>
public static class SkillRuntimeControlNames
{
    public const string Search = "runtime_skill_search";

    public const string Activate = "runtime_skill_activate";

    internal static bool IsReserved(string name) =>
        string.Equals(name, Search, StringComparison.Ordinal)
        || string.Equals(name, Activate, StringComparison.Ordinal);
}

public static class SkillRuntimeReasonCodes
{
    public const string SearchArgumentsInvalid =
        "skill_search_arguments_invalid";
    public const string SearchBudgetExceeded =
        "skill_search_budget_exceeded";
    public const string ActivationArgumentsInvalid =
        "skill_activation_arguments_invalid";
    public const string NotAuthorized = "skill_activation_not_authorized";
    public const string ExactIdentityMismatch =
        "skill_activation_exact_identity_mismatch";
    public const string ActivatedByModel = "skill_activated_by_model";
    public const string ReplacedByContinuation =
        "skill_state_replaced_by_continuation";
    public const string AlreadyActivated = "skill_already_activated";
    public const string CatalogEntryChanged = "skill_catalog_entry_changed";
    public const string ResolverUnavailable =
        "skill_content_resolver_unavailable";
    public const string ResolverError = "skill_content_resolver_error";
    public const string ResolverResultInvalid =
        "skill_content_resolver_result_invalid";
    public const string ResolverTimeout = "skill_content_resolver_timeout";
    public const string ResolverCapacityExceeded =
        "skill_content_resolver_capacity_exceeded";
    public const string ReferenceCountExceeded =
        "skill_content_reference_count_exceeded";
    public const string ReferenceDepthExceeded =
        "skill_content_reference_depth_exceeded";
    public const string ItemLimitExceeded =
        "skill_content_item_limit_exceeded";
    public const string AggregateLimitExceeded =
        "skill_content_aggregate_limit_exceeded";
    public const string DigestMissing = "skill_content_digest_missing";
    public const string DigestInvalid = "skill_content_digest_invalid";
    public const string DigestMismatch = "skill_content_digest_mismatch";
    public const string SizeMissing = "skill_content_size_missing";
    public const string SizeMismatch = "skill_content_size_mismatch";
    public const string Resolved = "skill_content_resolved";
}

public sealed class SkillContentResolutionException : InvalidOperationException
{
    public SkillContentResolutionException(string reasonCode)
        : base("Required skill context could not be resolved safely.")
    {
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Hard limits for model-driven skill discovery and host-controlled content
/// resolution.
/// </summary>
public sealed class SkillRuntimeLimits
{
    public SkillRuntimeLimits(
        int maxSearchResults = 8,
        int maxControlCallsPerTurn = 16,
        int maxSearchQueryUtf8Bytes = 4_096,
        int maxResolvedItems = 64,
        int maxResolvedItemUtf8Bytes = 16_384,
        int maxResolvedUtf8Bytes = 65_536,
        int maxReferenceDepth = 4,
        int maxJsonDepth = 16,
        int maxJsonNodesPerItem = 4_096,
        int resolverTimeoutMilliseconds = 2_000,
        int maxConcurrentResolverCalls = 4,
        int maxSearchTokens = 128,
        int maxSearchComparisons = 262_144)
    {
        if (maxSearchResults is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSearchResults));
        }

        if (maxControlCallsPerTurn is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxControlCallsPerTurn));
        }

        if (maxSearchQueryUtf8Bytes is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSearchQueryUtf8Bytes));
        }

        if (maxResolvedItems is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResolvedItems));
        }

        if (maxResolvedItemUtf8Bytes is < 1 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResolvedItemUtf8Bytes));
        }

        if (maxResolvedUtf8Bytes < maxResolvedItemUtf8Bytes
            || maxResolvedUtf8Bytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResolvedUtf8Bytes));
        }

        if (maxReferenceDepth is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReferenceDepth));
        }

        if (maxJsonDepth is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJsonDepth));
        }

        if (maxJsonNodesPerItem is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxJsonNodesPerItem));
        }

        if (resolverTimeoutMilliseconds is < 10 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolverTimeoutMilliseconds));
        }

        if (maxConcurrentResolverCalls is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentResolverCalls));
        }

        if (maxSearchTokens is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSearchTokens));
        }

        if (maxSearchComparisons is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSearchComparisons));
        }

        MaxSearchResults = maxSearchResults;
        MaxControlCallsPerTurn = maxControlCallsPerTurn;
        MaxSearchQueryUtf8Bytes = maxSearchQueryUtf8Bytes;
        MaxResolvedItems = maxResolvedItems;
        MaxResolvedItemUtf8Bytes = maxResolvedItemUtf8Bytes;
        MaxResolvedUtf8Bytes = maxResolvedUtf8Bytes;
        MaxReferenceDepth = maxReferenceDepth;
        MaxJsonDepth = maxJsonDepth;
        MaxJsonNodesPerItem = maxJsonNodesPerItem;
        ResolverTimeoutMilliseconds = resolverTimeoutMilliseconds;
        MaxConcurrentResolverCalls = maxConcurrentResolverCalls;
        MaxSearchTokens = maxSearchTokens;
        MaxSearchComparisons = maxSearchComparisons;
    }

    public int MaxSearchResults { get; }

    public int MaxControlCallsPerTurn { get; }

    public int MaxSearchQueryUtf8Bytes { get; }

    public int MaxResolvedItems { get; }

    public int MaxResolvedItemUtf8Bytes { get; }

    public int MaxResolvedUtf8Bytes { get; }

    public int MaxReferenceDepth { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonNodesPerItem { get; }

    public int ResolverTimeoutMilliseconds { get; }

    public int MaxConcurrentResolverCalls { get; }

    public int MaxSearchTokens { get; }

    public int MaxSearchComparisons { get; }
}

public static class SkillContentReferenceKinds
{
    public const string ContextProvider = "context_provider";

    public const string Resource = "resource";

    internal static bool IsKnown(string value) =>
        value is ContextProvider or Resource;
}

/// <summary>
/// A closed resolver reference. Root references come from an admitted skill
/// manifest; related references can only be introduced by the host resolver.
/// The runtime itself performs no network or file access.
/// </summary>
public sealed class SkillContentReference
{
    private SkillContentReference(
        string kind,
        string reference,
        string? mediaType,
        string? digest,
        long? sizeBytes)
    {
        Kind = SkillContentReferenceKinds.IsKnown(kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind));
        Reference = RuntimeGuard.RequiredUtf8(
            reference,
            2_048,
            nameof(reference));
        MediaType = mediaType is null
            ? null
            : RuntimeGuard.RequiredUtf8(mediaType, 128, nameof(mediaType));
        Digest = digest is null
            ? null
            : RuntimeGuard.RequiredUtf8(digest, 256, nameof(digest));
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        SizeBytes = sizeBytes;
        if (string.Equals(
                Kind,
                SkillContentReferenceKinds.Resource,
                StringComparison.Ordinal)
            && MediaType is null)
        {
            throw new ArgumentException(
                "A skill resource requires a media type.",
                nameof(mediaType));
        }
    }

    public string Kind { get; }

    public string Reference { get; }

    public string? MediaType { get; }

    public string? Digest { get; }

    public long? SizeBytes { get; }

    public static SkillContentReference ContextProvider(string reference) =>
        new(
            SkillContentReferenceKinds.ContextProvider,
            reference,
            mediaType: null,
            digest: null,
            sizeBytes: null);

    public static SkillContentReference Resource(
        string uri,
        string mediaType,
        string? digest = null,
        long? sizeBytes = null) =>
        new(
            SkillContentReferenceKinds.Resource,
            uri,
            mediaType,
            digest,
            sizeBytes);

    internal string Key =>
        Kind + "\0"
        + Reference + "\0"
        + (MediaType ?? string.Empty) + "\0"
        + (Digest ?? string.Empty) + "\0"
        + (SizeBytes?.ToString(CultureInfo.InvariantCulture)
           ?? string.Empty);

    internal SkillContentReference Snapshot() =>
        new(Kind, Reference, MediaType, Digest, SizeBytes);

    internal static SkillContentReference FromResource(SkillResource value) =>
        Resource(
            value.Uri,
            value.MediaType,
            value.Digest,
            value.SizeBytes);
}

public sealed class SkillContentResolutionRequest
{
    internal SkillContentResolutionRequest(
        AgentRun run,
        string turnId,
        SkillCatalogEntry skill,
        SkillContentReference reference,
        int depth)
    {
        RunId = run.RunId;
        AgentId = run.AgentId;
        WorldId = run.WorldId;
        SessionId = run.SessionId;
        RuntimeGeneration = run.RuntimeGeneration;
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        Skill = skill ?? throw new ArgumentNullException(nameof(skill));
        Reference = reference?.Snapshot()
                    ?? throw new ArgumentNullException(nameof(reference));
        Depth = depth >= 0
            ? depth
            : throw new ArgumentOutOfRangeException(nameof(depth));
    }

    public string RunId { get; }

    public string AgentId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long RuntimeGeneration { get; }

    public string TurnId { get; }

    public SkillCatalogEntry Skill { get; }

    public SkillContentReference Reference { get; }

    public int Depth { get; }
}

/// <summary>
/// A resolver result. Content is always treated as non-authoritative context.
/// Related references remain bound to the same admitted skill and are subject
/// to the runtime's count and depth limits.
/// </summary>
public sealed class SkillContentResolution
{
    private const int MaximumDigestUtf8Bytes = 71;

    public SkillContentResolution(
        JsonElement content,
        IReadOnlyList<SkillContentReference>? relatedReferences = null,
        string? digest = null,
        long? sizeBytes = null)
    {
        Content = content.Clone();
        Digest = digest is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                digest,
                MaximumDigestUtf8Bytes,
                nameof(digest));
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        RelatedReferences = new ReadOnlyCollection<SkillContentReference>(
            (relatedReferences ?? Array.Empty<SkillContentReference>())
            .Select(
                value => value?.Snapshot()
                         ?? throw new ArgumentException(
                             "A related skill-content reference cannot be null.",
                             nameof(relatedReferences)))
            .ToList());
        SizeBytes = sizeBytes;
    }

    public JsonElement Content { get; }

    public IReadOnlyList<SkillContentReference> RelatedReferences { get; }

    public string? Digest { get; }

    public long? SizeBytes { get; }
}

/// <summary>
/// Resolves only references supplied by an admitted skill or by a prior
/// resolver result. Implementations own all I/O and must honor cancellation.
/// </summary>
public interface ISkillContentResolver
{
    ValueTask<SkillContentResolution> ResolveAsync(
        SkillContentResolutionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class SkillActivationStateRecord
{
    public SkillActivationStateRecord(
        string skillId,
        string version,
        string contentDigest)
    {
        SkillId = RuntimeGuard.RequiredId(skillId, nameof(skillId));
        Version = RuntimeGuard.RequiredUtf8(version, 32, nameof(version));
        ContentDigest = RuntimeGuard.RequiredUtf8(
            contentDigest,
            256,
            nameof(contentDigest));
    }

    public string SkillId { get; }

    public string Version { get; }

    public string ContentDigest { get; }

    public string Reference => SkillId + "@" + Version;

    public SkillReference ToReference() => new(SkillId, Version);

    public bool Matches(SkillCatalogEntry entry) =>
        string.Equals(SkillId, entry.SkillId, StringComparison.Ordinal)
        && string.Equals(Version, entry.Version, StringComparison.Ordinal)
        && string.Equals(
            ContentDigest,
            entry.ContentDigest,
            StringComparison.Ordinal);

    public SkillActivationStateRecord Clone() =>
        new(SkillId, Version, ContentDigest);
}

internal static class SkillActivationStateCodec
{
    public const string ExtensionName = "activeSkillState";

    private const string ContentType =
        "application/vnd.game-agent.active-skills+json";

    public static JsonElement Encode(
        IReadOnlyList<SkillActivationStateRecord> records)
    {
        if (records is null)
        {
            throw new ArgumentNullException(nameof(records));
        }

        var ordered = ValidateAndOrder(records);
        var digest = ComputeDigest(ordered);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", ContentType);
            writer.WriteString("stateDigest", digest);
            writer.WritePropertyName("activations");
            writer.WriteStartArray();
            foreach (var record in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("skillId", record.SkillId);
                writer.WriteString("version", record.Version);
                writer.WriteString("skillDigest", record.ContentDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<SkillActivationStateRecord> Decode(
        JsonElement value,
        int maximumActivations)
    {
        if (maximumActivations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActivations));
        }

        if (value.ValueKind != JsonValueKind.Object
            || !TryReadString(value, "contentType", out var contentType)
            || !string.Equals(
                contentType,
                ContentType,
                StringComparison.Ordinal)
            || !TryReadString(value, "stateDigest", out var stateDigest)
            || !value.TryGetProperty(
                "activations",
                out var activations)
            || activations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "A durable active-skill state is malformed.");
        }

        var result = new List<SkillActivationStateRecord>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in activations.EnumerateArray())
        {
            if (result.Count >= maximumActivations
                || item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, "skillId", out var skillId)
                || !TryReadString(item, "version", out var version)
                || !TryReadString(
                    item,
                    "skillDigest",
                    out var skillDigest))
            {
                throw new InvalidDataException(
                    "A durable active-skill entry is malformed or over capacity.");
            }

            var record = new SkillActivationStateRecord(
                skillId,
                version,
                skillDigest);
            if (!references.Add(record.Reference))
            {
                throw new InvalidDataException(
                    "A durable active-skill state contains duplicates.");
            }

            result.Add(record);
        }

        var ordered = ValidateAndOrder(result);
        if (!string.Equals(
                ComputeDigest(ordered),
                stateDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable active-skill state digest does not match.");
        }

        return ordered;
    }

    public static void Attach(
        AgentRun run,
        IReadOnlyList<SkillActivationStateRecord> records)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        run.Extensions[ExtensionName] = Encode(records);
    }

    public static IReadOnlyList<SkillActivationStateRecord>? TryRead(
        AgentRun run,
        int maximumActivations)
    {
        return run.Extensions.TryGetValue(ExtensionName, out var value)
            ? Decode(value, maximumActivations)
            : null;
    }

    private static IReadOnlyList<SkillActivationStateRecord> ValidateAndOrder(
        IEnumerable<SkillActivationStateRecord> records)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SkillActivationStateRecord>();
        foreach (var record in records)
        {
            if (record is null || !references.Add(record.Reference))
            {
                throw new ArgumentException(
                    "Active skill state contains a null or duplicate entry.",
                    nameof(records));
            }

            result.Add(record.Clone());
        }

        return result
            .OrderBy(value => value.SkillId, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeDigest(
        IEnumerable<SkillActivationStateRecord> records)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "active_skill_state");
        foreach (var record in records)
        {
            digest.Add("skillId", record.SkillId);
            digest.Add("version", record.Version);
            digest.Add("skillDigest", record.ContentDigest);
        }

        return digest.Finish();
    }

    private static bool TryReadString(
        JsonElement value,
        string property,
        out string result)
    {
        result = string.Empty;
        return value.TryGetProperty(property, out var element)
               && element.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(
                   result = element.GetString()!);
    }
}

internal sealed class SkillRuntimeSearchHit
{
    public SkillRuntimeSearchHit(SkillCatalogEntry skill, int score)
    {
        Skill = skill;
        Score = score;
    }

    public SkillCatalogEntry Skill { get; }

    public int Score { get; }
}

internal sealed class SkillRuntimePlan
{
    private readonly SkillCatalogSnapshot _snapshot;
    private readonly HashSet<string> _admittedCatalog;
    private readonly HashSet<string> _active;
    private readonly IReadOnlyList<SkillSearchIndexEntry> _searchIndex;
    private readonly IReadOnlyList<SkillCatalogEntry> _searchableSkills;

    public SkillRuntimePlan(
        SkillCatalogSnapshot snapshot,
        IReadOnlyCollection<string> admittedCatalog,
        IReadOnlyList<SkillActivationStateRecord> active,
        SkillRuntimeLimits limits)
    {
        _snapshot = snapshot
                    ?? throw new ArgumentNullException(nameof(snapshot));
        _admittedCatalog = new HashSet<string>(
            admittedCatalog
            ?? throw new ArgumentNullException(nameof(admittedCatalog)),
            StringComparer.Ordinal);
        _active = new HashSet<string>(
            (active ?? throw new ArgumentNullException(nameof(active)))
            .Select(value => value.Reference),
            StringComparer.Ordinal);
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _searchIndex = new ReadOnlyCollection<SkillSearchIndexEntry>(
            _snapshot.Skills
                .Where(
                    value => _admittedCatalog.Contains(value.Reference)
                             && !_active.Contains(value.Reference))
                .OrderBy(value => value.SkillId, StringComparer.Ordinal)
                .ThenBy(value => value.Version, StringComparer.Ordinal)
                .Select(value => new SkillSearchIndexEntry(value))
                .ToArray());
        _searchableSkills = new ReadOnlyCollection<SkillCatalogEntry>(
            _searchIndex.Select(value => value.Skill).ToArray());
    }

    public SkillRuntimeLimits Limits { get; }

    public bool IsControlVisible(string name) =>
        SearchableSkills.Count > 0
        && SkillRuntimeControlNames.IsReserved(name);

    public IReadOnlyList<SkillCatalogEntry> SearchableSkills =>
        _searchableSkills;

    public IReadOnlyList<ToolDescriptor> ControlTools =>
        SearchableSkills.Count == 0
            ? Array.Empty<ToolDescriptor>()
            : new[]
            {
                CreateSearchDescriptor(Limits),
                CreateActivationDescriptor()
            };

    public bool TrySearch(
        JsonElement query,
        int limit,
        out IReadOnlyList<SkillRuntimeSearchHit> hits,
        out string reasonCode)
    {
        var queryText = SearchText(query);
        var queryTokens = Tokenize(queryText, Limits.MaxSearchTokens);
        var boundedLimit = Math.Min(limit, Limits.MaxSearchResults);
        var comparisonsPerSkill = 3L
                                  + (3L * queryTokens.Tokens.Count)
                                  + (3L * boundedLimit);
        if (queryTokens.Exceeded
            || comparisonsPerSkill * _searchIndex.Count
            > Limits.MaxSearchComparisons)
        {
            hits = Array.Empty<SkillRuntimeSearchHit>();
            reasonCode = SkillRuntimeReasonCodes.SearchBudgetExceeded;
            return false;
        }

        var ranked = new List<SkillRuntimeSearchHit>(boundedLimit);
        foreach (var value in _searchIndex)
        {
            var hit = new SkillRuntimeSearchHit(
                value.Skill,
                Score(value, queryText, queryTokens.Tokens));
            if (hit.Score <= 0)
            {
                continue;
            }

            var insertionIndex = 0;
            while (insertionIndex < ranked.Count
                   && CompareSearchHits(
                       hit,
                       ranked[insertionIndex]) >= 0)
            {
                insertionIndex++;
            }

            if (insertionIndex >= boundedLimit)
            {
                continue;
            }

            ranked.Insert(insertionIndex, hit);
            if (ranked.Count > boundedLimit)
            {
                ranked.RemoveAt(boundedLimit);
            }
        }

        hits = ranked.ToArray();
        reasonCode = string.Empty;
        return true;
    }

    public bool TryResolveExact(
        string skillId,
        string version,
        string skillDigest,
        out SkillCatalogEntry? skill,
        out string reasonCode)
    {
        skill = null;
        if (!_snapshot.TryGet(skillId, version, out var candidate)
            || candidate is null)
        {
            reasonCode = SkillRuntimeReasonCodes.NotAuthorized;
            return false;
        }

        if (!string.Equals(
                candidate.ContentDigest,
                skillDigest,
                StringComparison.Ordinal))
        {
            reasonCode = SkillRuntimeReasonCodes.ExactIdentityMismatch;
            return false;
        }

        if (!_admittedCatalog.Contains(candidate.Reference))
        {
            reasonCode = SkillRuntimeReasonCodes.NotAuthorized;
            return false;
        }

        skill = candidate;
        reasonCode = SkillAdmissionReasonCodes.Allowed;
        return true;
    }

    public static IReadOnlyList<ToolDescriptor> MergeProviderTools(
        IReadOnlyList<ToolDescriptor> gameAndToolControls,
        IReadOnlyList<ToolDescriptor> skillControls)
    {
        return gameAndToolControls
            .Concat(skillControls)
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .Select(
                value => ProtocolJson.DeserializeToolDescriptor(
                    ProtocolJson.Serialize(value)))
            .ToArray();
    }

    public static string ComputeProviderToolDigest(
        IEnumerable<ToolDescriptor> tools)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "effective_provider_tools");
        foreach (var tool in tools.OrderBy(
                     value => value.Name,
                     StringComparer.Ordinal))
        {
            digest.Add("name", tool.Name);
            digest.Add("version", tool.Version);
            digest.Add("descriptor", ProtocolJson.ToElement(tool));
        }

        return digest.Finish();
    }

    public static bool TryReadSearch(
        JsonElement arguments,
        SkillRuntimeLimits limits,
        out JsonElement query,
        out int resultLimit,
        out string reasonCode)
    {
        query = default;
        resultLimit = limits.MaxSearchResults;
        reasonCode = SkillRuntimeReasonCodes.SearchArgumentsInvalid;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        var hasQuery = false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "query":
                    if (property.Value.ValueKind
                        is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        return false;
                    }

                    try
                    {
                        JsonValueInspector.ValidateAndMeasure(
                            property.Value,
                            new JsonValueLimits(
                                limits.MaxSearchQueryUtf8Bytes,
                                limits.MaxJsonDepth,
                                limits.MaxJsonNodesPerItem,
                                limits.MaxSearchQueryUtf8Bytes,
                                limits.MaxJsonNodesPerItem),
                            nameof(arguments));
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }

                    query = property.Value.Clone();
                    hasQuery = true;
                    break;
                case "limit":
                    if (!property.Value.TryGetInt32(out resultLimit))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        if (!hasQuery
            || resultLimit is < 1
            || resultLimit > limits.MaxSearchResults)
        {
            return false;
        }

        var queryTokens = Tokenize(
            SearchText(query),
            limits.MaxSearchTokens);
        if (queryTokens.Exceeded)
        {
            reasonCode = SkillRuntimeReasonCodes.SearchBudgetExceeded;
            return false;
        }

        return queryTokens.Tokens.Count > 0;
    }

    public static bool TryReadActivation(
        JsonElement arguments,
        out PreparedSkillActivation? activation)
    {
        activation = null;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? skillId = null;
        string? version = null;
        string? skillDigest = null;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.Add(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (property.Name)
            {
                case "skillId":
                    skillId = property.Value.GetString();
                    break;
                case "version":
                    version = property.Value.GetString();
                    break;
                case "skillDigest":
                    skillDigest = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (!IsBounded(skillId, 128)
            || !IsBounded(version, 32)
            || !IsBounded(skillDigest, 256))
        {
            return false;
        }

        try
        {
            _ = new SkillReference(skillId!, version!);
        }
        catch (ArgumentException)
        {
            return false;
        }

        activation = new PreparedSkillActivation(
            skillId!,
            version!,
            skillDigest!);
        return true;
    }

    private static int Score(
        SkillSearchIndexEntry skill,
        string normalizedQuery,
        IReadOnlyList<string> queryTokens)
    {
        var score = 0;
        if (string.Equals(
                skill.NormalizedId,
                normalizedQuery,
                StringComparison.Ordinal))
        {
            score += 1_000;
        }

        if (skill.NormalizedId.Contains(
                normalizedQuery,
                StringComparison.Ordinal))
        {
            score += 300;
        }

        if (skill.NormalizedDescription.Contains(
                normalizedQuery,
                StringComparison.Ordinal))
        {
            score += 180;
        }

        foreach (var token in queryTokens)
        {
            if (skill.NormalizedId.Contains(token, StringComparison.Ordinal))
            {
                score += 100;
            }

            if (skill.NormalizedDescription.Contains(
                    token,
                    StringComparison.Ordinal))
            {
                score += 40;
            }

            if (skill.NormalizedVersion.Contains(
                    token,
                    StringComparison.Ordinal))
            {
                score += 5;
            }
        }

        return score;
    }

    private static int CompareSearchHits(
        SkillRuntimeSearchHit left,
        SkillRuntimeSearchHit right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        var skillId = string.Compare(
            left.Skill.SkillId,
            right.Skill.SkillId,
            StringComparison.Ordinal);
        return skillId != 0
            ? skillId
            : string.Compare(
                left.Skill.Version,
                right.Skill.Version,
                StringComparison.Ordinal);
    }

    private static string SearchText(JsonElement value)
    {
        var text = new StringBuilder();
        Append(value, text);
        return Normalize(text.ToString());

        static void Append(JsonElement current, StringBuilder output)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        output.Append(' ');
                        output.Append(property.Name);
                        Append(property.Value, output);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                    {
                        Append(item, output);
                    }

                    break;
                case JsonValueKind.String:
                    output.Append(' ');
                    output.Append(current.GetString());
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    output.Append(' ');
                    output.Append(current.GetRawText());
                    break;
            }
        }
    }

    private static string Normalize(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]))
                {
                    sanitized.Append(current);
                    sanitized.Append(value[++index]);
                }
                else
                {
                    sanitized.Append(' ');
                }

                continue;
            }

            sanitized.Append(
                char.IsLowSurrogate(current)
                    ? ' '
                    : current);
        }

        return sanitized.ToString()
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Trim();
    }

    private static SearchTokenization Tokenize(
        string value,
        int maximumTokens)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = new List<SearchScalar>();
        for (var index = 0; index < value.Length; index++)
        {
            var scalar = ReadScalar(value, ref index);
            if (scalar.IsLetterOrDigit)
            {
                current.Add(scalar);
                continue;
            }

            if (!Flush())
            {
                return Snapshot(exceeded: true);
            }
        }

        return Flush()
            ? Snapshot(exceeded: false)
            : Snapshot(exceeded: true);

        bool Flush()
        {
            if (current.Count == 0)
            {
                return true;
            }

            var tokenBuilder = new StringBuilder();
            foreach (var item in current)
            {
                tokenBuilder.Append(item.Text);
            }

            if (!TryAdd(tokenBuilder.ToString()))
            {
                return false;
            }

            if (current.Any(IsCjk))
            {
                for (var index = 0; index < current.Count; index++)
                {
                    if (!TryAdd(current[index].Text))
                    {
                        return false;
                    }

                    if (index + 1 < current.Count
                        && !TryAdd(
                            current[index].Text
                            + current[index + 1].Text))
                    {
                        return false;
                    }
                }
            }

            current.Clear();
            return true;
        }

        bool TryAdd(string token)
        {
            if (result.Contains(token))
            {
                return true;
            }

            if (result.Count >= maximumTokens)
            {
                return false;
            }

            result.Add(token);
            return true;
        }

        SearchTokenization Snapshot(bool exceeded) =>
            new(
                result.ToArray(),
                exceeded);
    }

    private static SearchScalar ReadScalar(
        string value,
        ref int index)
    {
        var first = value[index];
        if (char.IsHighSurrogate(first)
            && index + 1 < value.Length
            && char.IsLowSurrogate(value[index + 1]))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(value, index);
            var scalar = char.ConvertToUtf32(first, value[index + 1]);
            var text = value.Substring(index, 2);
            index++;
            return new SearchScalar(
                scalar,
                text,
                IsLetterOrDigit(category));
        }

        return new SearchScalar(
            first,
            first.ToString(),
            char.IsLetterOrDigit(first));
    }

    private static bool IsLetterOrDigit(UnicodeCategory category) =>
        category is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber;

    private static bool IsCjk(SearchScalar value)
    {
        var scalar = value.Value;
        return scalar is >= 0x3400 and <= 0x4dbf
               or >= 0x4e00 and <= 0x9fff
               or >= 0xf900 and <= 0xfaff
               or >= 0x20000 and <= 0x2ee5f
               or >= 0x2f800 and <= 0x2fa1f
               or >= 0x30000 and <= 0x323af;
    }

    private readonly struct SearchScalar
    {
        public SearchScalar(
            int value,
            string text,
            bool isLetterOrDigit)
        {
            Value = value;
            Text = text;
            IsLetterOrDigit = isLetterOrDigit;
        }

        public int Value { get; }

        public string Text { get; }

        public bool IsLetterOrDigit { get; }
    }

    private sealed class SkillSearchIndexEntry
    {
        public SkillSearchIndexEntry(SkillCatalogEntry skill)
        {
            Skill = skill;
            NormalizedId = Normalize(skill.SkillId);
            NormalizedDescription = Normalize(skill.Description);
            NormalizedVersion = Normalize(skill.Version);
        }

        public SkillCatalogEntry Skill { get; }

        public string NormalizedId { get; }

        public string NormalizedDescription { get; }

        public string NormalizedVersion { get; }
    }

    private sealed class SearchTokenization
    {
        public SearchTokenization(
            IReadOnlyList<string> tokens,
            bool exceeded)
        {
            Tokens = tokens;
            Exceeded = exceeded;
        }

        public IReadOnlyList<string> Tokens { get; }

        public bool Exceeded { get; }
    }

    private static bool IsBounded(string? value, int maximumUtf8Bytes) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes;

    private static ToolDescriptor CreateSearchDescriptor(
        SkillRuntimeLimits limits)
    {
        return new ToolDescriptor
        {
            Name = SkillRuntimeControlNames.Search,
            Version = "1",
            Description =
                "Search the authorized skill catalog with text or structured "
                + "JSON. Activate a result only with its exact identity.",
            ParametersSchema = SearchSchema(limits),
            Effect = ToolEffects.PureRead,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 1_000,
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.None,
            Toolset = "runtime",
            Visibility = ToolVisibilities.Direct
        };
    }

    private static ToolDescriptor CreateActivationDescriptor()
    {
        return new ToolDescriptor
        {
            Name = SkillRuntimeControlNames.Activate,
            Version = "1",
            Description =
                "Activate one exact authorized skill. Admission, trust, "
                + "required-tool, version, and host policy checks still apply. "
                + "The skill becomes effective on the next model turn.",
            ParametersSchema = ActivationSchema(),
            Effect = ToolEffects.AgentLocalWrite,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 1_000,
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "runtime",
            Visibility = ToolVisibilities.Direct
        };
    }

    private static JsonElement SearchSchema(SkillRuntimeLimits limits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName("query");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("limit");
            writer.WriteStartObject();
            writer.WriteString("type", "integer");
            writer.WriteNumber("minimum", 1);
            writer.WriteNumber("maximum", limits.MaxSearchResults);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("query");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement ActivationSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "skillId": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 128
                },
                "version": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 32
                },
                "skillDigest": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 256
                }
              },
              "required": [ "skillId", "version", "skillDigest" ],
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }
}

internal sealed class PreparedSkillActivation
{
    public PreparedSkillActivation(
        string skillId,
        string version,
        string skillDigest)
    {
        SkillId = skillId;
        Version = version;
        SkillDigest = skillDigest;
    }

    public string SkillId { get; }

    public string Version { get; }

    public string SkillDigest { get; }

    public string Reference => SkillId + "@" + Version;
}

internal sealed class SkillContentResolvedItem
{
    public SkillContentResolvedItem(
        SkillCatalogEntry skill,
        SkillContentReference reference,
        int depth,
        string status,
        string reasonCode,
        JsonElement? content,
        string? contentDigest,
        int contentUtf8Bytes)
    {
        Skill = skill;
        Reference = reference;
        Depth = depth;
        Status = status;
        ReasonCode = reasonCode;
        Content = content?.Clone();
        ContentDigest = contentDigest;
        ContentUtf8Bytes = contentUtf8Bytes;
    }

    public SkillCatalogEntry Skill { get; }

    public SkillContentReference Reference { get; }

    public int Depth { get; }

    public string Status { get; }

    public string ReasonCode { get; }

    public JsonElement? Content { get; }

    public string? ContentDigest { get; }

    public int ContentUtf8Bytes { get; }
}

internal sealed class SkillContentResolutionSelection
{
    public SkillContentResolutionSelection(
        IReadOnlyList<SkillContentResolvedItem> items,
        int resolvedUtf8Bytes,
        bool truncated,
        IReadOnlyList<string> reasonCodes)
    {
        Items = items;
        ResolvedUtf8Bytes = resolvedUtf8Bytes;
        Truncated = truncated;
        ReasonCodes = reasonCodes;
        Evidence = CreateEvidence();
    }

    public IReadOnlyList<SkillContentResolvedItem> Items { get; }

    public int ResolvedUtf8Bytes { get; }

    public bool Truncated { get; }

    public IReadOnlyList<string> ReasonCodes { get; }

    public bool HasReferences => Items.Count > 0;

    public JsonElement Evidence { get; }

    private JsonElement CreateEvidence()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("itemCount", Items.Count);
            writer.WriteNumber(
                "resolvedCount",
                Items.Count(
                    value => string.Equals(
                        value.Status,
                        "resolved",
                        StringComparison.Ordinal)));
            writer.WriteNumber(
                "failedCount",
                Items.Count(
                    value => string.Equals(
                        value.Status,
                        "failed",
                        StringComparison.Ordinal)));
            writer.WriteNumber("resolvedUtf8Bytes", ResolvedUtf8Bytes);
            writer.WriteBoolean("truncated", Truncated);
            writer.WritePropertyName("reasonCodes");
            writer.WriteStartArray();
            foreach (var reason in ReasonCodes)
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in Items)
            {
                WriteIdentity(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static void WriteIdentity(
        Utf8JsonWriter writer,
        SkillContentResolvedItem item)
    {
        writer.WriteStartObject();
        writer.WriteString("skillId", item.Skill.SkillId);
        writer.WriteString("version", item.Skill.Version);
        writer.WriteString("skillDigest", item.Skill.ContentDigest);
        writer.WriteString("referenceKind", item.Reference.Kind);
        writer.WriteString(
            "referenceDigest",
            ComputeReferenceDigest(item.Reference));
        var safeMediaType = SafeDisclosedMediaType(item.Reference.MediaType);
        if (safeMediaType is not null)
        {
            writer.WriteString("mediaType", safeMediaType);
        }

        writer.WriteNumber("depth", item.Depth);
        writer.WriteString("status", item.Status);
        writer.WriteString("reasonCode", item.ReasonCode);
        if (item.ContentDigest is not null)
        {
            writer.WriteString("contentDigest", item.ContentDigest);
        }

        writer.WriteNumber("contentUtf8Bytes", item.ContentUtf8Bytes);
    }

    private static string? SafeDisclosedMediaType(string? mediaType)
    {
        if (mediaType is null)
        {
            return null;
        }

        var parameterIndex = mediaType.IndexOf(';');
        var candidate = (parameterIndex < 0
                ? mediaType
                : mediaType.Substring(0, parameterIndex))
            .Trim();
        var separator = candidate.IndexOf('/');
        if (separator <= 0
            || separator == candidate.Length - 1
            || separator != candidate.LastIndexOf('/')
            || !candidate.Take(separator).All(IsMediaTypeToken)
            || !candidate.Skip(separator + 1).All(IsMediaTypeToken))
        {
            return null;
        }

        return candidate.ToLowerInvariant();
    }

    private static bool IsMediaTypeToken(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '!'
        or '#'
        or '$'
        or '&'
        or '^'
        or '_'
        or '.'
        or '+'
        or '-';

    private static string ComputeReferenceDigest(
        SkillContentReference reference)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "skill_content_reference");
        digest.Add("kind", reference.Kind);
        digest.Add("reference", reference.Reference);
        digest.Add(
            "mediaTypePresent",
            reference.MediaType is null ? "0" : "1");
        digest.Add("mediaType", reference.MediaType);
        digest.Add(
            "declaredDigestPresent",
            reference.Digest is null ? "0" : "1");
        digest.Add("declaredDigest", reference.Digest);
        digest.Add(
            "declaredSizePresent",
            reference.SizeBytes.HasValue ? "1" : "0");
        digest.Add(
            "declaredSize",
            reference.SizeBytes?.ToString(CultureInfo.InvariantCulture));
        return digest.Finish();
    }
}

internal sealed class SkillContentRuntime
{
    private readonly ISkillContentResolver? _resolver;
    private readonly SkillRuntimeLimits _limits;
    private readonly JsonValueLimits _itemLimits;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly BoundedCallbackExecutionDispatcher
        _callbackExecutionDispatcher;
    private readonly object _sync = new();
    private readonly HashSet<ResolverCall> _calls = new();
    private TaskCompletionSource<bool>? _drained;
    private bool _stopped;

    public SkillContentRuntime(
        ISkillContentResolver? resolver,
        SkillRuntimeLimits limits,
        BoundedCancellationDispatcher? cancellationDispatcher = null,
        BoundedCallbackExecutionDispatcher? callbackExecutionDispatcher = null)
    {
        _resolver = resolver;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _cancellationDispatcher = cancellationDispatcher
                                  ?? BoundedCancellationDispatcher
                                       .SkillContentResolverShared;
        _callbackExecutionDispatcher = callbackExecutionDispatcher
                                       ?? BoundedCallbackExecutionDispatcher
                                           .SkillResolverShared;
        _itemLimits = new JsonValueLimits(
            limits.MaxResolvedItemUtf8Bytes,
            limits.MaxJsonDepth,
            limits.MaxJsonNodesPerItem,
            limits.MaxResolvedItemUtf8Bytes,
            limits.MaxJsonNodesPerItem);
    }

    internal int DetachedResolverCallCount
    {
        get
        {
            lock (_sync)
            {
                return _calls.Count(call => call.IsDetached);
            }
        }
    }

    public async ValueTask<SkillContentResolutionSelection> ResolveAsync(
        AgentRun run,
        string turnId,
        IReadOnlyList<SkillCatalogEntry> skills,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<PendingReference>();
        foreach (var skill in skills)
        {
            foreach (var reference in skill.ContextProviderReferences)
            {
                queue.Enqueue(
                    new PendingReference(
                        skill,
                        SkillContentReference.ContextProvider(reference),
                        0));
            }

            foreach (var resource in skill.Resources)
            {
                queue.Enqueue(
                    new PendingReference(
                        skill,
                        SkillContentReference.FromResource(resource),
                        0));
            }
        }

        var items = new List<SkillContentResolvedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reasonCodes = new HashSet<string>(StringComparer.Ordinal);
        var resolvedBytes = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count >= _limits.MaxResolvedItems)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ReferenceCountExceeded);
            }

            var pending = queue.Dequeue();
            var key = pending.Skill.Reference + "\0" + pending.Reference.Key;
            if (!seen.Add(key))
            {
                continue;
            }

            if (pending.Depth > _limits.MaxReferenceDepth)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ReferenceDepthExceeded);
            }

            if (_resolver is null)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ResolverUnavailable);
            }

            SkillContentResolution? result;
            try
            {
                result = await ResolveOneAsync(
                        new SkillContentResolutionRequest(
                            run,
                            turnId,
                            pending.Skill,
                            pending.Reference,
                            pending.Depth),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SkillContentResolutionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (result is null)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ResolverResultInvalid);
            }

            string? validationFailure = null;
            int contentBytes = 0;
            string? contentDigest = null;
            try
            {
                _ = JsonValueInspector.ValidateAndMeasure(
                    result.Content,
                    _itemLimits,
                    nameof(result));
                var canonical = new StringBuilder();
                CanonicalJsonDigest.AppendCanonical(
                    canonical,
                    result.Content);
                var canonicalBytes =
                    Encoding.UTF8.GetBytes(canonical.ToString());
                contentBytes = canonicalBytes.Length;
                using var sha = System.Security.Cryptography.SHA256.Create();
                contentDigest = ToHex(sha.ComputeHash(canonicalBytes));
                if (result.Digest is not null)
                {
                    var reported = NormalizeDigest(result.Digest);
                    validationFailure = reported is null
                        ? SkillRuntimeReasonCodes.DigestInvalid
                        : !string.Equals(
                            reported,
                            contentDigest,
                            StringComparison.Ordinal)
                            ? SkillRuntimeReasonCodes.DigestMismatch
                            : null;
                }

                if (result.SizeBytes < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(result.SizeBytes));
                }
            }
            catch (RuntimeContentLimitException)
            {
                validationFailure =
                    SkillRuntimeReasonCodes.ItemLimitExceeded;
            }
            catch (ArgumentException)
            {
                validationFailure =
                    SkillRuntimeReasonCodes.ResolverResultInvalid;
            }

            if (validationFailure is null
                && pending.Reference.Digest is not null)
            {
                var declared = NormalizeDigest(
                    pending.Reference.Digest);
                validationFailure = declared is null
                    ? SkillRuntimeReasonCodes.DigestInvalid
                    : !string.Equals(
                        declared,
                        contentDigest,
                        StringComparison.Ordinal)
                        ? SkillRuntimeReasonCodes.DigestMismatch
                        : null;
            }

            if (validationFailure is null
                && pending.Reference.SizeBytes.HasValue)
            {
                validationFailure =
                    pending.Reference.SizeBytes.Value != contentBytes
                        ? SkillRuntimeReasonCodes.SizeMismatch
                        : null;
            }

            if (validationFailure is null
                && result.SizeBytes.HasValue
                && result.SizeBytes.Value != contentBytes)
            {
                validationFailure =
                    SkillRuntimeReasonCodes.SizeMismatch;
            }

            if (validationFailure is null
                && checked((long)resolvedBytes + contentBytes)
                > _limits.MaxResolvedUtf8Bytes)
            {
                validationFailure =
                    SkillRuntimeReasonCodes.AggregateLimitExceeded;
            }

            if (validationFailure is not null)
            {
                throw Failure(validationFailure);
            }

            resolvedBytes = checked(resolvedBytes + contentBytes);
            items.Add(
                new SkillContentResolvedItem(
                    pending.Skill,
                    pending.Reference,
                    pending.Depth,
                    "resolved",
                    SkillRuntimeReasonCodes.Resolved,
                    result.Content,
                    contentDigest,
                    contentBytes));
            reasonCodes.Add(SkillRuntimeReasonCodes.Resolved);

            if (result.RelatedReferences.Count == 0)
            {
                continue;
            }

            if (pending.Depth >= _limits.MaxReferenceDepth)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ReferenceDepthExceeded);
            }

            var remainingCapacity = _limits.MaxResolvedItems
                                    - items.Count
                                    - queue.Count;
            if (remainingCapacity < 0
                || result.RelatedReferences.Count > remainingCapacity)
            {
                throw Failure(
                    SkillRuntimeReasonCodes.ReferenceCountExceeded);
            }

            foreach (var related in result.RelatedReferences
                         .OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                queue.Enqueue(
                    new PendingReference(
                        pending.Skill,
                        related.Snapshot(),
                        pending.Depth + 1));
            }
        }

        return new SkillContentResolutionSelection(
            new ReadOnlyCollection<SkillContentResolvedItem>(items),
            resolvedBytes,
            truncated: false,
            reasonCodes.OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    public async ValueTask<bool> StopAsync()
    {
        Task drain;
        ResolverCall[] calls;
        lock (_sync)
        {
            _stopped = true;
            calls = _calls.ToArray();
            drain = _calls.Count == 0
                ? Task.CompletedTask
                : (_drained ??= NewCompletion()).Task;
        }

        foreach (var call in calls)
        {
            call.CancelDetached(_cancellationDispatcher);
        }

        if (drain.IsCompleted)
        {
            await ObserveDrainAsync(drain).ConfigureAwait(false);
            return true;
        }

        var timeout = Task.Delay(_limits.ResolverTimeoutMilliseconds);
        var completed = await Task.WhenAny(drain, timeout)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, drain))
        {
            await ObserveDrainAsync(drain).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async ValueTask<SkillContentResolution> ResolveOneAsync(
        SkillContentResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var call = StartCall(request);
        if (call is null)
        {
            throw Failure(
                SkillRuntimeReasonCodes.ResolverCapacityExceeded);
        }

        var timeout = Task.Delay(_limits.ResolverTimeoutMilliseconds);
        var cancelled = NewCompletion();
        using var cancellationRegistration = cancellationToken.Register(
            () => cancelled.TrySetResult(true));
        var completed = await Task.WhenAny(
                call.Task,
                timeout,
                cancelled.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, call.Task))
        {
            call.CompleteWithoutCancellation();
            try
            {
                return await call.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw Failure(SkillRuntimeReasonCodes.ResolverError);
            }
        }

        call.CancelDetached(_cancellationDispatcher);
        cancellationToken.ThrowIfCancellationRequested();
        throw Failure(SkillRuntimeReasonCodes.ResolverTimeout);
    }

    private ResolverCall? StartCall(
        SkillContentResolutionRequest request)
    {
        ResolverCall call;
        lock (_sync)
        {
            if (_stopped
                || _calls.Count >= _limits.MaxConcurrentResolverCalls)
            {
                return null;
            }

            var cancellation = new CancellationTokenSource();
            if (!_callbackExecutionDispatcher.TryExecute(
                        () => _resolver!.ResolveAsync(
                        request,
                        cancellation.Token),
                        out var task))
            {
                cancellation.Dispose();
                return null;
            }

            call = new ResolverCall(task, cancellation);
            _calls.Add(call);
        }

        _ = ObserveCallAsync(call);
        return call;
    }

    private async Task ObserveCallAsync(ResolverCall call)
    {
        try
        {
            try
            {
                await call.Task.ConfigureAwait(false);
            }
            catch
            {
                // The caller receives a bounded reason code; detached faults
                // are observed here so they cannot become unobserved task
                // failures.
            }

            await call.CancellationSettled.ConfigureAwait(false);
        }
        finally
        {
            call.DisposeCancellation();
            TaskCompletionSource<bool>? drained = null;
            lock (_sync)
            {
                _calls.Remove(call);
                if (_calls.Count == 0)
                {
                    drained = _drained;
                    _drained = null;
                }
            }

            drained?.TrySetResult(true);
        }
    }

    private static async Task ObserveDrainAsync(Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch
        {
            // Individual resolver failures are already converted to bounded
            // runtime reason codes.
        }
    }

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SkillContentResolutionException Failure(string reason) =>
        new(reason);

    private static string? NormalizeDigest(string value)
    {
        var candidate = value.StartsWith(
            "sha256:",
            StringComparison.Ordinal)
            ? value.Substring("sha256:".Length)
            : value;
        return CanonicalJsonDigest.IsSha256(candidate)
            ? candidate
            : null;
    }

    private static string ToHex(IEnumerable<byte> value)
    {
        var result = new StringBuilder(64);
        foreach (var item in value)
        {
            result.Append(
                item.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private sealed class ResolverCall
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource<bool> _cancellationSettled =
            NewCompletion();
        private int _cancellationState;

        public ResolverCall(
            Task<SkillContentResolution> task,
            CancellationTokenSource cancellation)
        {
            Task = task;
            _cancellation = cancellation;
        }

        public Task<SkillContentResolution> Task { get; }

        public Task CancellationSettled => _cancellationSettled.Task;

        public bool IsDetached =>
            Volatile.Read(ref _cancellationState) == 1;

        public void CompleteWithoutCancellation()
        {
            if (Interlocked.CompareExchange(
                    ref _cancellationState,
                    2,
                    0) == 0)
            {
                _cancellationSettled.TrySetResult(true);
            }
        }

        public void CancelDetached(
            BoundedCancellationDispatcher cancellationDispatcher)
        {
            if (Interlocked.CompareExchange(
                    ref _cancellationState,
                    1,
                    0) != 0)
            {
                return;
            }

            if (!cancellationDispatcher.TryReserve(out var reservation))
            {
                _cancellationSettled.TrySetResult(true);
                return;
            }

            Task cancellation;
            try
            {
                cancellation = reservation!.DispatchAsync(_cancellation);
            }
            catch
            {
                reservation!.Dispose();
                _cancellationSettled.TrySetResult(true);
                return;
            }

            _ = ObserveCancellationAsync(
                cancellation,
                reservation!,
                _cancellationSettled);
        }

        public void DisposeCancellation()
        {
            _cancellation.Dispose();
        }

        private static async Task ObserveCancellationAsync(
            Task cancellation,
            BoundedCancellationDispatcher.CancellationDispatchReservation
                reservation,
            TaskCompletionSource<bool> settled)
        {
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation callback failures stay isolated from the
                // resolver result and shutdown path.
            }
            finally
            {
                reservation.Dispose();
                settled.TrySetResult(true);
            }
        }
    }

    private sealed class PendingReference
    {
        public PendingReference(
            SkillCatalogEntry skill,
            SkillContentReference reference,
            int depth)
        {
            Skill = skill;
            Reference = reference;
            Depth = depth;
        }

        public SkillCatalogEntry Skill { get; }

        public SkillContentReference Reference { get; }

        public int Depth { get; }
    }
}
