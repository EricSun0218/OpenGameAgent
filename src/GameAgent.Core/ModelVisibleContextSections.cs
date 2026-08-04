using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public static class ModelContextSectionScopes
{
    public const string World = "world";
    public const string Timeline = "timeline";
    public const string Location = "location";
    public const string Faction = "faction";
    public const string Party = "party";
    public const string Actor = "actor";
    public const string Session = "session";
    public const string Run = "run";

    internal static bool IsKnown(string value) =>
        value == World
        || value == Timeline
        || value == Location
        || value == Faction
        || value == Party
        || value == Actor
        || value == Session
        || value == Run;
}

public static class ModelContextDisclosureModes
{
    public const string Full = "full";
    public const string MergePatch = "merge_patch";
    public const string Unchanged = "unchanged";
}

public sealed class ModelContextCaptureRequest
{
    public string WorldId { get; set; } = string.Empty;

    public string? TimelineId { get; set; }

    public string? ActorId { get; set; }

    public string? SessionId { get; set; }

    public string? RunId { get; set; }

    public string ModelCapabilitiesDigest { get; set; } = string.Empty;
}

public sealed class ModelContextSectionSnapshot
{
    public string SectionId { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string? AuthorityId { get; set; }

    public string? TimelineId { get; set; }

    public string? IncarnationId { get; set; }

    public string ModelCapabilitiesDigest { get; set; } = string.Empty;

    public long Revision { get; set; }

    public long? ExpectedBaseRevision { get; set; }

    public bool RetainThroughCompaction { get; set; }

    public JsonElement Content { get; set; }
}

public interface IModelContextSectionContributor
{
    string SectionId { get; }

    ValueTask<ModelContextSectionSnapshot> CaptureAsync(
        ModelContextCaptureRequest request,
        CancellationToken cancellationToken);
}

public sealed class ModelContextSectionBaseline
{
    public string BaselineKey { get; set; } = string.Empty;

    public string ViewKey { get; set; } = string.Empty;

    public string SectionId { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string? AuthorityId { get; set; }

    public string? TimelineId { get; set; }

    public string? IncarnationId { get; set; }

    public string ModelCapabilitiesDigest { get; set; } = string.Empty;

    public long Revision { get; set; }

    public long? ExpectedBaseRevision { get; set; }

    public bool RetainThroughCompaction { get; set; }

    public JsonElement Content { get; set; }

    public string ContentDigest { get; set; } = string.Empty;
}

public interface IModelContextSectionBaselineStore
{
    ValueTask<ModelContextSectionBaseline?> TryGetAsync(
        string baselineKey,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        ModelContextSectionBaseline baseline,
        string? expectedContentDigest,
        CancellationToken cancellationToken);
}

public sealed class ModelContextSectionDisclosure
{
    internal ModelContextSectionDisclosure(
        ModelContextSectionBaseline target,
        string mode,
        JsonElement payload,
        string? baseDigest,
        int payloadUtf8Bytes)
    {
        Target = target;
        Mode = mode;
        Payload = payload;
        BaseDigest = baseDigest;
        PayloadUtf8Bytes = payloadUtf8Bytes;
    }

    public string SectionId => Target.SectionId;

    public string BaselineKey => Target.BaselineKey;

    public string Mode { get; }

    public JsonElement Payload { get; }

    public string? BaseDigest { get; }

    public string TargetDigest => Target.ContentDigest;

    public long TargetRevision => Target.Revision;

    public bool RetainThroughCompaction => Target.RetainThroughCompaction;

    public int PayloadUtf8Bytes { get; }

    internal ModelContextSectionBaseline Target { get; }
}

public sealed class ModelContextSectionOptions
{
    public int MaxContributors { get; set; } = 128;

    public int MaxSectionUtf8Bytes { get; set; } = 262_144;

    public int MaxDisclosureUtf8Bytes { get; set; } = 2 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxContributors is < 1 or > 4_096
            || MaxSectionUtf8Bytes is < 1_024
               or > CanonicalJsonDigest.MaximumUtf8Bytes
            || MaxDisclosureUtf8Bytes < MaxSectionUtf8Bytes
            || MaxDisclosureUtf8Bytes > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ModelContextSectionOptions));
        }
    }
}

public sealed class ModelContextSectionException : Exception
{
    public ModelContextSectionException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Creates bounded provider-neutral full or merge-patch disclosures. A caller
/// commits a disclosure only after that exact payload has been admitted to a
/// model request; preparation alone never advances the durable baseline.
/// </summary>
public sealed class ModelContextSectionCoordinator
{
    private readonly IModelContextSectionBaselineStore _store;
    private readonly ModelContextSectionOptions _options;
    private readonly JsonValueLimits _jsonLimits;

    public ModelContextSectionCoordinator(
        IModelContextSectionBaselineStore store,
        ModelContextSectionOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new ModelContextSectionOptions();
        _options.Validate();
        _jsonLimits = new JsonValueLimits(
            maxUtf8Bytes: _options.MaxSectionUtf8Bytes,
            maxDepth: 64,
            maxNodes: 131_072,
            maxStringUtf8Bytes: _options.MaxSectionUtf8Bytes,
            maxContainerItems: 65_536);
    }

    public async ValueTask<IReadOnlyList<ModelContextSectionDisclosure>>
        PrepareAsync(
            IEnumerable<IModelContextSectionContributor> contributors,
            ModelContextCaptureRequest request,
            ISet<string>? retainedBaselineDigests = null,
            CancellationToken cancellationToken = default)
    {
        if (contributors is null)
        {
            throw new ArgumentNullException(nameof(contributors));
        }

        var admittedRequest = SnapshotAndValidateCaptureRequest(request);
        var viewKey = ResolveViewKey(admittedRequest);
        var captured = new List<IModelContextSectionContributor>();
        foreach (var contributor in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contributor is null || captured.Count >= _options.MaxContributors)
            {
                throw new ModelContextSectionException(
                    "context_section_contributor_limit",
                    "Context section contributors are null or exceed the configured limit.");
            }

            captured.Add(contributor);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModelContextSectionDisclosure>(captured.Count);
        long totalBytes = 0;
        foreach (var contributor in captured)
        {
            var declaredId = RequiredText(
                contributor.SectionId,
                nameof(contributor.SectionId),
                128);
            if (!ids.Add(declaredId))
            {
                throw new ModelContextSectionException(
                    "context_section_duplicate",
                    $"Context section '{declaredId}' is registered more than once.");
            }

            var snapshot = await contributor
                .CaptureAsync(Clone(admittedRequest), cancellationToken)
                .ConfigureAwait(false);
            var target = SnapshotAndValidate(snapshot, declaredId, viewKey);
            var baseline = await _store
                .TryGetAsync(target.BaselineKey, cancellationToken)
                .ConfigureAwait(false);
            var disclosure = CreateDisclosure(
                target,
                baseline,
                retainedBaselineDigests);
            totalBytes = checked(totalBytes + disclosure.PayloadUtf8Bytes);
            if (totalBytes > _options.MaxDisclosureUtf8Bytes)
            {
                throw new ModelContextSectionException(
                    "context_section_disclosure_limit",
                    "Combined context section disclosures exceed the configured byte limit.");
            }

            result.Add(disclosure);
        }

        return new ReadOnlyCollection<ModelContextSectionDisclosure>(result);
    }

    public ValueTask CommitAsync(
        ModelContextSectionDisclosure disclosure,
        CancellationToken cancellationToken = default)
    {
        if (disclosure is null)
        {
            throw new ArgumentNullException(nameof(disclosure));
        }

        if (disclosure.Mode == ModelContextDisclosureModes.Unchanged)
        {
            return default;
        }

        return _store.PutAsync(
            Snapshot(disclosure.Target),
            disclosure.BaseDigest,
            cancellationToken);
    }

    private ModelContextSectionDisclosure CreateDisclosure(
        ModelContextSectionBaseline target,
        ModelContextSectionBaseline? baseline,
        ISet<string>? retainedBaselineDigests)
    {
        if (baseline is not null
            && string.Equals(
                baseline.ContentDigest,
                target.ContentDigest,
                StringComparison.Ordinal))
        {
            return new ModelContextSectionDisclosure(
                target,
                ModelContextDisclosureModes.Unchanged,
                EmptyObject(),
                baseline.ContentDigest,
                2);
        }

        var canUseBaseline = baseline is not null
            && IsCompatible(baseline, target)
            && target.ExpectedBaseRevision == baseline.Revision
            && (!target.RetainThroughCompaction
                || retainedBaselineDigests is null
                || retainedBaselineDigests.Contains(baseline.ContentDigest));
        if (canUseBaseline)
        {
            var patch = JsonMergePatch.Create(
                baseline!.Content,
                target.Content);
            var reconstructed = JsonMergePatch.Apply(
                baseline.Content,
                patch);
            var patchBytes = Encoding.UTF8.GetByteCount(patch.GetRawText());
            var fullBytes = Encoding.UTF8.GetByteCount(target.Content.GetRawText());
            if (string.Equals(
                    CanonicalJsonDigest.ComputeSha256(reconstructed),
                    target.ContentDigest,
                    StringComparison.Ordinal)
                && patchBytes < fullBytes)
            {
                return new ModelContextSectionDisclosure(
                    target,
                    ModelContextDisclosureModes.MergePatch,
                    patch,
                    baseline.ContentDigest,
                    patchBytes);
            }
        }

        return new ModelContextSectionDisclosure(
            target,
            ModelContextDisclosureModes.Full,
            target.Content.Clone(),
            baseline?.ContentDigest,
            Encoding.UTF8.GetByteCount(target.Content.GetRawText()));
    }

    private ModelContextSectionBaseline SnapshotAndValidate(
        ModelContextSectionSnapshot snapshot,
        string declaredId,
        string viewKey)
    {
        if (snapshot is null)
        {
            throw new ModelContextSectionException(
                "context_section_snapshot_missing",
                $"Context section '{declaredId}' returned no snapshot.");
        }

        var sectionId = RequiredText(snapshot.SectionId, nameof(snapshot.SectionId), 128);
        if (!string.Equals(sectionId, declaredId, StringComparison.Ordinal))
        {
            throw new ModelContextSectionException(
                "context_section_identity_mismatch",
                "A context contributor returned a different section identity.");
        }

        var scope = RequiredText(snapshot.Scope, nameof(snapshot.Scope), 32);
        if (!ModelContextSectionScopes.IsKnown(scope)
            || snapshot.Revision < 0
            || snapshot.ExpectedBaseRevision < 0
            || snapshot.ExpectedBaseRevision >= snapshot.Revision)
        {
            throw new ModelContextSectionException(
                "context_section_snapshot_invalid",
                $"Context section '{sectionId}' has invalid scope or revision metadata.");
        }

        var scopeKey = RequiredText(snapshot.ScopeKey, nameof(snapshot.ScopeKey), 256);
        var schemaVersion = RequiredText(
            snapshot.SchemaVersion,
            nameof(snapshot.SchemaVersion),
            64);
        var capabilities = RequiredDigest(
            snapshot.ModelCapabilitiesDigest,
            nameof(snapshot.ModelCapabilitiesDigest));
        var authority = OptionalText(snapshot.AuthorityId, nameof(snapshot.AuthorityId), 128);
        var timeline = OptionalText(snapshot.TimelineId, nameof(snapshot.TimelineId), 128);
        var incarnation = OptionalText(
            snapshot.IncarnationId,
            nameof(snapshot.IncarnationId),
            128);
        if (snapshot.Content.ValueKind == JsonValueKind.Undefined)
        {
            throw new ModelContextSectionException(
                "context_section_content_invalid",
                $"Context section '{sectionId}' returned undefined JSON.");
        }

        JsonValueInspector.ValidateAndMeasure(
            snapshot.Content,
            _jsonLimits,
            nameof(snapshot.Content));
        var content = snapshot.Content.Clone();
        return new ModelContextSectionBaseline
        {
            BaselineKey = BuildBaselineKey(viewKey, scope, scopeKey, sectionId),
            ViewKey = viewKey,
            SectionId = sectionId,
            SchemaVersion = schemaVersion,
            Scope = scope,
            ScopeKey = scopeKey,
            AuthorityId = authority,
            TimelineId = timeline,
            IncarnationId = incarnation,
            ModelCapabilitiesDigest = capabilities,
            Revision = snapshot.Revision,
            ExpectedBaseRevision = snapshot.ExpectedBaseRevision,
            RetainThroughCompaction = snapshot.RetainThroughCompaction,
            Content = content,
            ContentDigest = CanonicalJsonDigest.ComputeSha256(content)
        };
    }

    private static bool IsCompatible(
        ModelContextSectionBaseline baseline,
        ModelContextSectionBaseline target) =>
        string.Equals(baseline.SectionId, target.SectionId, StringComparison.Ordinal)
        && string.Equals(baseline.ViewKey, target.ViewKey, StringComparison.Ordinal)
        && string.Equals(baseline.SchemaVersion, target.SchemaVersion, StringComparison.Ordinal)
        && string.Equals(baseline.Scope, target.Scope, StringComparison.Ordinal)
        && string.Equals(baseline.ScopeKey, target.ScopeKey, StringComparison.Ordinal)
        && string.Equals(baseline.AuthorityId, target.AuthorityId, StringComparison.Ordinal)
        && string.Equals(baseline.TimelineId, target.TimelineId, StringComparison.Ordinal)
        && string.Equals(baseline.IncarnationId, target.IncarnationId, StringComparison.Ordinal)
        && string.Equals(
            baseline.ModelCapabilitiesDigest,
            target.ModelCapabilitiesDigest,
            StringComparison.Ordinal);

    private static string BuildBaselineKey(
        string viewKey,
        string scope,
        string scopeKey,
        string sectionId) =>
        "model-context:" + CanonicalJsonDigest.ComputeSha256(
            JsonArrayBuilder.Object(
                ("viewKey", JsonArrayBuilder.String(viewKey)),
                ("scope", JsonArrayBuilder.String(scope)),
                ("scopeKey", JsonArrayBuilder.String(scopeKey)),
                ("sectionId", JsonArrayBuilder.String(sectionId))));

    private static ModelContextCaptureRequest SnapshotAndValidateCaptureRequest(
        ModelContextCaptureRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return new ModelContextCaptureRequest
        {
            WorldId = RequiredText(request.WorldId, nameof(request.WorldId), 128),
            TimelineId = OptionalText(
                request.TimelineId, nameof(request.TimelineId), 128),
            ActorId = OptionalText(request.ActorId, nameof(request.ActorId), 128),
            SessionId = OptionalText(request.SessionId, nameof(request.SessionId), 128),
            RunId = OptionalText(request.RunId, nameof(request.RunId), 128),
            ModelCapabilitiesDigest = RequiredDigest(
                request.ModelCapabilitiesDigest,
                nameof(request.ModelCapabilitiesDigest))
        };
    }

    private static string ResolveViewKey(ModelContextCaptureRequest request)
    {
        if (request.SessionId is not null)
        {
            return BuildViewKey(request.WorldId, "session", request.SessionId);
        }

        if (request.RunId is not null)
        {
            return BuildViewKey(request.WorldId, "run", request.RunId);
        }

        throw new ModelContextSectionException(
            "context_section_view_missing",
            "Context deltas require a session or run ID that identifies the model-visible history.");
    }

    private static string BuildViewKey(string worldId, string kind, string identifier) =>
        kind + ":" + CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("worldId", JsonArrayBuilder.String(worldId)),
            ("kind", JsonArrayBuilder.String(kind)),
            ("identifier", JsonArrayBuilder.String(identifier))));

    private static ModelContextCaptureRequest Clone(ModelContextCaptureRequest source) =>
        new()
        {
            WorldId = source.WorldId,
            TimelineId = source.TimelineId,
            ActorId = source.ActorId,
            SessionId = source.SessionId,
            RunId = source.RunId,
            ModelCapabilitiesDigest = source.ModelCapabilitiesDigest
        };

    private static ModelContextSectionBaseline Snapshot(
        ModelContextSectionBaseline baseline) =>
        new()
        {
            BaselineKey = baseline.BaselineKey,
            ViewKey = baseline.ViewKey,
            SectionId = baseline.SectionId,
            SchemaVersion = baseline.SchemaVersion,
            Scope = baseline.Scope,
            ScopeKey = baseline.ScopeKey,
            AuthorityId = baseline.AuthorityId,
            TimelineId = baseline.TimelineId,
            IncarnationId = baseline.IncarnationId,
            ModelCapabilitiesDigest = baseline.ModelCapabilitiesDigest,
            Revision = baseline.Revision,
            ExpectedBaseRevision = baseline.ExpectedBaseRevision,
            RetainThroughCompaction = baseline.RetainThroughCompaction,
            Content = baseline.Content.Clone(),
            ContentDigest = baseline.ContentDigest
        };

    private static string RequiredDigest(string value, string name)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ModelContextSectionException(
                "context_section_digest_invalid",
                $"'{name}' must be a lowercase SHA-256 digest.");
        }

        return value;
    }

    private static string RequiredText(string value, string name, int maximum)
    {
        try
        {
            return RuntimeGuard.RequiredUtf8(value, maximum, name);
        }
        catch (ArgumentException exception)
        {
            throw new ModelContextSectionException(
                "context_section_text_invalid",
                exception.Message);
        }
    }

    private static string? OptionalText(string? value, string name, int maximum) =>
        value is null ? null : RequiredText(value, name, maximum);

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed class InMemoryModelContextSectionBaselineStore :
    IModelContextSectionBaselineStore
{
    private readonly int _maximumBaselines;
    private readonly ConcurrentDictionary<string, ModelContextSectionBaseline>
        _baselines = new(StringComparer.Ordinal);

    public InMemoryModelContextSectionBaselineStore(int maximumBaselines = 65_536)
    {
        if (maximumBaselines is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselines));
        }

        _maximumBaselines = maximumBaselines;
    }

    public ValueTask<ModelContextSectionBaseline?> TryGetAsync(
        string baselineKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _baselines.TryGetValue(baselineKey, out var baseline);
        return new ValueTask<ModelContextSectionBaseline?>(
            baseline is null ? null : Snapshot(baseline));
    }

    public ValueTask PutAsync(
        ModelContextSectionBaseline baseline,
        string? expectedContentDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (baseline is null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        var snapshot = Snapshot(baseline);
        while (true)
        {
            if (_baselines.TryGetValue(snapshot.BaselineKey, out var current))
            {
                if (!string.Equals(
                        current.ContentDigest,
                        expectedContentDigest,
                        StringComparison.Ordinal))
                {
                    throw new ModelContextSectionException(
                        "context_section_baseline_conflict",
                        "The context section disclosure baseline changed before commit.");
                }

                if (_baselines.TryUpdate(snapshot.BaselineKey, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (expectedContentDigest is not null)
            {
                throw new ModelContextSectionException(
                    "context_section_baseline_conflict",
                    "The expected context section disclosure baseline is missing.");
            }

            if (_baselines.Count >= _maximumBaselines)
            {
                throw new ModelContextSectionException(
                    "context_section_baseline_capacity",
                    "The context section baseline store is full.");
            }

            if (_baselines.TryAdd(snapshot.BaselineKey, snapshot))
            {
                return default;
            }
        }
    }

    private static ModelContextSectionBaseline Snapshot(
        ModelContextSectionBaseline baseline) =>
        new()
        {
            BaselineKey = baseline.BaselineKey,
            ViewKey = baseline.ViewKey,
            SectionId = baseline.SectionId,
            SchemaVersion = baseline.SchemaVersion,
            Scope = baseline.Scope,
            ScopeKey = baseline.ScopeKey,
            AuthorityId = baseline.AuthorityId,
            TimelineId = baseline.TimelineId,
            IncarnationId = baseline.IncarnationId,
            ModelCapabilitiesDigest = baseline.ModelCapabilitiesDigest,
            Revision = baseline.Revision,
            ExpectedBaseRevision = baseline.ExpectedBaseRevision,
            RetainThroughCompaction = baseline.RetainThroughCompaction,
            Content = baseline.Content.Clone(),
            ContentDigest = baseline.ContentDigest
        };
}

public static class JsonMergePatch
{
    public static JsonElement Create(JsonElement source, JsonElement target)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePatch(writer, source, target);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static JsonElement Apply(JsonElement source, JsonElement patch)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteApplied(writer, source, patch);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WritePatch(
        Utf8JsonWriter writer,
        JsonElement source,
        JsonElement target)
    {
        if (source.ValueKind != JsonValueKind.Object
            || target.ValueKind != JsonValueKind.Object)
        {
            target.WriteTo(writer);
            return;
        }

        var sourceProperties = source.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.Ordinal);
        var targetProperties = target.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.Ordinal);
        writer.WriteStartObject();
        foreach (var name in sourceProperties.Keys
                     .Union(targetProperties.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var hasSource = sourceProperties.TryGetValue(name, out var oldValue);
            var hasTarget = targetProperties.TryGetValue(name, out var newValue);
            if (!hasTarget)
            {
                writer.WriteNull(name);
                continue;
            }

            if (hasSource
                && string.Equals(
                    CanonicalJsonDigest.ComputeSha256(oldValue),
                    CanonicalJsonDigest.ComputeSha256(newValue),
                    StringComparison.Ordinal))
            {
                continue;
            }

            writer.WritePropertyName(name);
            WritePatch(writer, hasSource ? oldValue : EmptyObject(), newValue);
        }

        writer.WriteEndObject();
    }

    private static void WriteApplied(
        Utf8JsonWriter writer,
        JsonElement source,
        JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            patch.WriteTo(writer);
            return;
        }

        var sourceProperties = source.ValueKind == JsonValueKind.Object
            ? source.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value,
                StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var patchProperties = patch.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.Ordinal);
        writer.WriteStartObject();
        foreach (var name in sourceProperties.Keys
                     .Union(patchProperties.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            if (patchProperties.TryGetValue(name, out var patchValue))
            {
                if (patchValue.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                writer.WritePropertyName(name);
                WriteApplied(
                    writer,
                    sourceProperties.TryGetValue(name, out var oldValue)
                        ? oldValue
                        : EmptyObject(),
                    patchValue);
                continue;
            }

            writer.WritePropertyName(name);
            sourceProperties[name].WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
