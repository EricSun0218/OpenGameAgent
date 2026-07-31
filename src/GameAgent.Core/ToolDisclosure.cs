using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Reserved model-facing controls used to discover and activate deferred tools.
/// Applications cannot register game tools with these names.
/// </summary>
public static class ToolDisclosureControlNames
{
    public const string Search = "runtime_tool_search";
    public const string Activate = "runtime_tool_activate";

    internal static bool IsReserved(string name) =>
        string.Equals(name, Search, StringComparison.Ordinal)
        || string.Equals(name, Activate, StringComparison.Ordinal);
}

public static class ToolDisclosurePurposes
{
    public const string Search = "search";
    public const string ModelActivation = "model_activation";
    public const string SkillActivation = "skill_activation";
    public const string Revalidation = "revalidation";

    internal static bool IsKnown(string purpose) =>
        purpose is Search or ModelActivation or SkillActivation or Revalidation;
}

public static class ToolDisclosureReasonCodes
{
    public const string Allowed = "tool_disclosure_allowed";
    public const string PolicyError = "tool_disclosure_policy_error";
    public const string PolicyDecisionInvalid =
        "tool_disclosure_policy_decision_invalid";
    public const string NotAuthorized = "tool_disclosure_not_authorized";
    public const string NotDeferred = "tool_disclosure_not_deferred";
    public const string ExactIdentityMismatch =
        "tool_disclosure_exact_identity_mismatch";
    public const string CapacityExceeded =
        "tool_disclosure_capacity_exceeded";
    public const string ActivatedByModel = "tool_activated_by_model";
    public const string ActivatedBySkill = "tool_activated_by_skill";
    public const string AlreadyActivated = "tool_already_activated";
    public const string SkillActivationRemoved =
        "tool_skill_activation_removed";
    public const string CatalogEntryUnavailable =
        "tool_catalog_entry_unavailable";
    public const string CatalogEntryChanged = "tool_catalog_entry_changed";
    public const string RevalidationDenied =
        "tool_disclosure_revalidation_denied";
}

/// <summary>
/// Hard limits for the bounded deferred-tool disclosure surface.
/// </summary>
public sealed class ToolDisclosureLimits
{
    public ToolDisclosureLimits(
        int maxActivatedDeferredTools = 32,
        int maxSearchResults = 8,
        int maxControlCallsPerTurn = 16,
        int maxSearchQueryUtf8Bytes = 1_024)
    {
        if (maxActivatedDeferredTools is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActivatedDeferredTools));
        }

        if (maxSearchResults is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSearchResults));
        }

        if (maxControlCallsPerTurn is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxControlCallsPerTurn));
        }

        if (maxSearchQueryUtf8Bytes is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSearchQueryUtf8Bytes));
        }

        MaxActivatedDeferredTools = maxActivatedDeferredTools;
        MaxSearchResults = maxSearchResults;
        MaxControlCallsPerTurn = maxControlCallsPerTurn;
        MaxSearchQueryUtf8Bytes = maxSearchQueryUtf8Bytes;
    }

    public int MaxActivatedDeferredTools { get; }

    public int MaxSearchResults { get; }

    public int MaxControlCallsPerTurn { get; }

    public int MaxSearchQueryUtf8Bytes { get; }
}

/// <summary>
/// Immutable input supplied to an application disclosure policy.
/// </summary>
public sealed class ToolDisclosureRequest
{
    internal ToolDisclosureRequest(
        AgentRun run,
        string turnId,
        ToolCatalogEntry tool,
        string purpose,
        string? origin)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        RunId = run.RunId;
        AgentId = run.AgentId;
        WorldId = run.WorldId;
        SessionId = run.SessionId;
        RuntimeGeneration = run.RuntimeGeneration;
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Purpose = ToolDisclosurePurposes.IsKnown(purpose)
            ? purpose
            : throw new ArgumentOutOfRangeException(nameof(purpose));
        Origin = origin is null
            ? null
            : RuntimeGuard.RequiredUtf8(origin, 256, nameof(origin));
    }

    public string RunId { get; }

    public string AgentId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long RuntimeGeneration { get; }

    public string TurnId { get; }

    public ToolCatalogEntry Tool { get; }

    public string Purpose { get; }

    public string? Origin { get; }
}

public sealed class ToolDisclosureDecision
{
    private ToolDisclosureDecision(bool allowed, string reasonCode)
    {
        Allowed = allowed;
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
    }

    public bool Allowed { get; }

    public string ReasonCode { get; }

    public static ToolDisclosureDecision Allow(
        string reasonCode = ToolDisclosureReasonCodes.Allowed) =>
        new(true, reasonCode);

    public static ToolDisclosureDecision Deny(string reasonCode) =>
        new(false, reasonCode);
}

/// <summary>
/// Decides which application-registered deferred tools may be searched,
/// activated, and retained. Implementations must be deterministic, bounded,
/// synchronous, and must not mutate the supplied immutable catalog entry.
/// </summary>
public interface IToolDisclosurePolicy
{
    string PolicyId { get; }

    string Version { get; }

    ToolDisclosureDecision Evaluate(ToolDisclosureRequest request);
}

/// <summary>
/// The default policy allows every application-registered deferred tool.
/// Internal tools never reach this policy or the model-facing catalog.
/// </summary>
public sealed class DefaultToolDisclosurePolicy : IToolDisclosurePolicy
{
    public static DefaultToolDisclosurePolicy Instance { get; } = new();

    private DefaultToolDisclosurePolicy()
    {
    }

    public string PolicyId => "default-tool-disclosure";

    public string Version => "1.0.0";

    public ToolDisclosureDecision Evaluate(ToolDisclosureRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return string.Equals(
            request.Tool.Visibility,
            ToolVisibilities.Deferred,
            StringComparison.Ordinal)
            ? ToolDisclosureDecision.Allow()
            : ToolDisclosureDecision.Deny(
                ToolDisclosureReasonCodes.NotDeferred);
    }
}

/// <summary>
/// Durable exact activation request. The descriptor digest and source prevent
/// a catalog replacement from silently redirecting an earlier activation.
/// </summary>
public sealed class ToolActivationRecord
{
    internal ToolActivationRecord(
        string name,
        string version,
        string descriptorDigest,
        string source,
        string origin)
    {
        Name = RuntimeGuard.RequiredUtf8(name, 96, nameof(name));
        Version = RuntimeGuard.RequiredUtf8(version, 32, nameof(version));
        DescriptorDigest = RuntimeGuard.RequiredUtf8(
            descriptorDigest,
            256,
            nameof(descriptorDigest));
        Source = RuntimeGuard.RequiredUtf8(source, 96, nameof(source));
        Origin = RuntimeGuard.RequiredUtf8(origin, 256, nameof(origin));
    }

    public string Name { get; }

    public string Version { get; }

    public string DescriptorDigest { get; }

    public string Source { get; }

    public string Origin { get; }

    public string Reference => Name + "@" + Version;

    internal ToolActivationRecord Clone() =>
        new(Name, Version, DescriptorDigest, Source, Origin);

    internal bool Matches(ToolCatalogEntry tool) =>
        string.Equals(Name, tool.Name, StringComparison.Ordinal)
        && string.Equals(Version, tool.Version, StringComparison.Ordinal)
        && string.Equals(
            DescriptorDigest,
            tool.Digest,
            StringComparison.Ordinal)
        && string.Equals(Source, tool.Toolset, StringComparison.Ordinal)
        && string.Equals(
            tool.Visibility,
            ToolVisibilities.Deferred,
            StringComparison.Ordinal);
}

internal sealed class ToolDisclosurePolicyResult
{
    public ToolDisclosurePolicyResult(
        ToolCatalogEntry tool,
        ToolDisclosureDecision search,
        ToolDisclosureDecision modelActivation,
        ToolDisclosureDecision skillActivation,
        ToolDisclosureDecision revalidation)
    {
        Tool = tool;
        Search = search;
        ModelActivation = modelActivation;
        SkillActivation = skillActivation;
        Revalidation = revalidation;
    }

    public ToolCatalogEntry Tool { get; }

    public ToolDisclosureDecision Search { get; }

    public ToolDisclosureDecision ModelActivation { get; }

    public ToolDisclosureDecision SkillActivation { get; }

    public ToolDisclosureDecision Revalidation { get; }
}

internal sealed class ToolDisclosureSearchHit
{
    public ToolDisclosureSearchHit(ToolCatalogEntry tool, int score)
    {
        Tool = tool;
        Score = score;
    }

    public ToolCatalogEntry Tool { get; }

    public int Score { get; }
}

internal sealed class ToolDisclosurePlan
{
    private const string ModelOrigin = "model";
    private const string SkillOriginPrefix = "skill:";
    private const int MaxSearchDocuments = 4_096;
    private const int MaxSearchQueryTerms = 256;
    private const int MaxParameterNames = 512;
    private const int MaxParameterNameUtf8Bytes = 32_768;
    private const int MaxParameterSchemaNodes = 8_192;
    private static readonly DeterministicUnicodeTokenizer
        ToolDocumentTokenizer = new(
            new DeterministicUnicodeTokenizerLimits(
                maxInputUtf8Bytes: 262_144,
                maxTextSegments: 1_024,
                maxTerms: 16_384,
                maxUniqueTerms: 8_192,
                maxTermUtf8Bytes: 1_024));
    private static readonly DeterministicBm25Scorer ToolSearchScorer =
        new(k1: 1.2, scoreScale: 1_000, maxFieldsPerTerm: 4);
    private readonly ToolCatalogSnapshot _snapshot;
    private readonly ToolDisclosureLimits _limits;
    private readonly IReadOnlyDictionary<string, ToolDisclosurePolicyResult>
        _policyByName;
    private readonly Dictionary<string, ToolActivationRecord> _requestedByName;
    private readonly Dictionary<string, ToolCatalogEntry> _effectiveByName;
    private readonly Dictionary<string, string> _invalidatedByName =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedSkillTools =
        new(StringComparer.Ordinal);
    private readonly List<string> _stateReasonCodes = new();
    private bool _stateChanged;

    public ToolDisclosurePlan(
        string policyId,
        string policyVersion,
        ToolCatalogSnapshot snapshot,
        ToolDisclosureLimits limits,
        IReadOnlyDictionary<string, ToolDisclosurePolicyResult> policyByName,
        IReadOnlyList<ToolActivationRecord> requested)
    {
        PolicyId = policyId;
        PolicyVersion = policyVersion;
        _snapshot = snapshot;
        _limits = limits;
        _policyByName = policyByName;
        _requestedByName = new Dictionary<string, ToolActivationRecord>(
            StringComparer.Ordinal);
        foreach (var item in requested
                     .OrderBy(value => value.Name, StringComparer.Ordinal)
                     .ThenBy(value => value.Version, StringComparer.Ordinal))
        {
            if (_requestedByName.Count >= limits.MaxActivatedDeferredTools)
            {
                throw new InvalidDataException(
                    "The durable deferred-tool activation state exceeds "
                    + "the configured capacity.");
            }

            if (!_requestedByName.TryAdd(item.Name, item.Clone()))
            {
                throw new InvalidDataException(
                    "The durable deferred-tool activation state contains "
                    + "duplicate tool names.");
            }
        }

        _effectiveByName = ResolveEffective();
        DecisionDigest = ComputeDecisionDigest();
        ReasonDigest = ComputeReasonDigest();
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public ToolDisclosureLimits Limits => _limits;

    public string DecisionDigest { get; private set; }

    public string ReasonDigest { get; private set; }

    public bool StateChanged => _stateChanged;

    public IReadOnlyList<string> StateReasonCodes =>
        _stateReasonCodes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ToolActivationRecord> RequestedActivations =>
        _requestedByName.Values
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal)
            .Select(value => value.Clone())
            .ToArray();

    public IReadOnlyList<ToolCatalogEntry> EffectiveGameTools =>
        _snapshot.DirectTools
            .Concat(
                _effectiveByName.Values.OrderBy(
                    value => value.Name,
                    StringComparer.Ordinal))
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ToolCatalogEntry> EffectiveActivatedDeferred =>
        _effectiveByName.Values
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ToolCatalogEntry> SearchableHiddenTools =>
        _snapshot.DeferredTools
            .Where(
                tool => !_effectiveByName.ContainsKey(tool.Name)
                        && _policyByName.TryGetValue(tool.Name, out var policy)
                        && policy.Search.Allowed
                        && policy.ModelActivation.Allowed
                        && policy.Revalidation.Allowed)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ToolCatalogEntry> AuthorizedHiddenTools =>
        _snapshot.DeferredTools
            .Where(
                tool => !_effectiveByName.ContainsKey(tool.Name)
                        && _policyByName.TryGetValue(tool.Name, out var policy)
                        && (policy.Search.Allowed
                            || policy.ModelActivation.Allowed
                            || policy.SkillActivation.Allowed))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ToolDescriptor> EffectiveProviderTools
    {
        get
        {
            var tools = EffectiveGameTools
                .Select(RuntimePromptBuilder.ToDescriptor)
                .ToList();
            if (SearchableHiddenTools.Count > 0)
            {
                tools.Add(CreateSearchDescriptor(_limits));
                tools.Add(CreateActivationDescriptor());
            }

            return tools
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(CloneDescriptor)
                .ToArray();
        }
    }

    public string EffectiveDirectDigest =>
        ComputeDescriptorDigest("effective_direct_tools", EffectiveProviderTools);

    public string BaseDirectDigest =>
        ComputeEntryDigest("base_direct_tools", _snapshot.DirectTools);

    public string DeferredOnlyDigest =>
        ComputeEntryDigest("authorized_hidden_tools", AuthorizedHiddenTools);

    public string StateDigest =>
        ComputeStateDigest(RequestedActivations);

    public bool IsControlVisible(string name) =>
        SearchableHiddenTools.Count > 0
        && ToolDisclosureControlNames.IsReserved(name);

    public bool TryGetEffectiveTool(
        string name,
        out ToolCatalogEntry? tool)
    {
        tool = EffectiveGameTools.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.Ordinal));
        return tool is not null;
    }

    public IReadOnlyList<ToolDisclosureSearchHit> Search(
        string query,
        int limit)
    {
        var normalizedQuery = Normalize(query);
        var queryTokenizer = new DeterministicUnicodeTokenizer(
            new DeterministicUnicodeTokenizerLimits(
                _limits.MaxSearchQueryUtf8Bytes,
                maxTextSegments: 1,
                maxTerms: 32_768,
                maxUniqueTerms: 16_384,
                maxTermUtf8Bytes: 1_024));
        IReadOnlyList<string> tokenOccurrences;
        try
        {
            tokenOccurrences = queryTokenizer.Tokenize(normalizedQuery);
        }
        catch (LexicalSearchLimitException)
        {
            // Exact and substring boosts remain available for a valid,
            // byte-bounded query containing an overlong lexical term.
            tokenOccurrences = Array.Empty<string>();
        }

        var queryTerms = tokenOccurrences
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSearchQueryTerms)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var documents = SearchableHiddenTools
            .Take(MaxSearchDocuments)
            .Select(CreateSearchDocument)
            .ToArray();
        if (documents.Length == 0)
        {
            return Array.Empty<ToolDisclosureSearchHit>();
        }

        var documentFrequencies = new Dictionary<string, int>(
            StringComparer.Ordinal);
        long totalNameTerms = 0;
        long totalDescriptionTerms = 0;
        long totalToolsetTerms = 0;
        long totalParameterTerms = 0;
        foreach (var document in documents)
        {
            totalNameTerms += document.Name.TermCount;
            totalDescriptionTerms += document.Description.TermCount;
            totalToolsetTerms += document.Toolset.TermCount;
            totalParameterTerms += document.Parameters.TermCount;
            var unique = new HashSet<string>(
                document.Name.Frequencies.Keys,
                StringComparer.Ordinal);
            unique.UnionWith(document.Description.Frequencies.Keys);
            unique.UnionWith(document.Toolset.Frequencies.Keys);
            unique.UnionWith(document.Parameters.Frequencies.Keys);
            foreach (var term in unique)
            {
                documentFrequencies[term] =
                    documentFrequencies.TryGetValue(
                        term,
                        out var frequency)
                        ? checked(frequency + 1)
                        : 1;
            }
        }

        var averages = new[]
        {
            Math.Max(1.0, totalNameTerms / (double)documents.Length),
            Math.Max(
                1.0,
                totalDescriptionTerms / (double)documents.Length),
            Math.Max(1.0, totalToolsetTerms / (double)documents.Length),
            Math.Max(1.0, totalParameterTerms / (double)documents.Length)
        };
        return documents
            .Select(
                document => new ToolDisclosureSearchHit(
                    document.Tool,
                    Score(
                        document,
                        normalizedQuery,
                        queryTerms,
                        documentFrequencies,
                        averages,
                        documents.Length)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Tool.Name, StringComparer.Ordinal)
            .ThenBy(hit => hit.Tool.Version, StringComparer.Ordinal)
            .Take(Math.Min(limit, _limits.MaxSearchResults))
            .ToArray();
    }

    public string ValidateRequiredTools(
        IReadOnlyList<ToolCatalogEntry> tools,
        string skillReference,
        bool activate)
    {
        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var deferred = new Dictionary<string, ToolCatalogEntry>(
            StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException(
                    "A required tool cannot be null.",
                    nameof(tools));
            }

            if (string.Equals(
                    tool.Visibility,
                    ToolVisibilities.Direct,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(
                    tool.Visibility,
                    ToolVisibilities.Deferred,
                    StringComparison.Ordinal))
            {
                return ToolDisclosureReasonCodes.NotDeferred;
            }

            if (_invalidatedByName.TryGetValue(
                    tool.Name,
                    out var invalidationReason))
            {
                return string.Equals(
                    invalidationReason,
                    ToolDisclosureReasonCodes.RevalidationDenied,
                    StringComparison.Ordinal)
                    ? ToolDisclosureReasonCodes.NotAuthorized
                    : ToolDisclosureReasonCodes.ExactIdentityMismatch;
            }

            if (_requestedByName.TryGetValue(tool.Name, out var requested)
                && !requested.Matches(tool))
            {
                return ToolDisclosureReasonCodes.ExactIdentityMismatch;
            }

            if (!_policyByName.TryGetValue(tool.Name, out var policy)
                || !policy.SkillActivation.Allowed
                || !policy.Revalidation.Allowed)
            {
                return ToolDisclosureReasonCodes.NotAuthorized;
            }

            deferred[tool.Name] = tool;
        }

        var newCount = deferred.Keys.Count(
            name => !_requestedByName.ContainsKey(name));
        if ((long)_requestedByName.Count + newCount
            > _limits.MaxActivatedDeferredTools)
        {
            return ToolDisclosureReasonCodes.CapacityExceeded;
        }

        if (!activate)
        {
            return ToolDisclosureReasonCodes.Allowed;
        }

        var origin = SkillOriginPrefix
                     + RuntimeGuard.RequiredUtf8(
                         skillReference,
                         224,
                         nameof(skillReference));
        foreach (var tool in deferred.Values.OrderBy(
                     value => value.Name,
                     StringComparer.Ordinal))
        {
            _claimedSkillTools.Add(tool.Name);
            if (_requestedByName.ContainsKey(tool.Name))
            {
                continue;
            }

            _requestedByName.Add(tool.Name, FromTool(tool, origin));
            _effectiveByName[tool.Name] = tool;
            MarkStateChanged(ToolDisclosureReasonCodes.ActivatedBySkill);
        }

        return ToolDisclosureReasonCodes.Allowed;
    }

    public void FinalizeSkillActivations()
    {
        foreach (var pair in _requestedByName
                     .Where(
                         pair => pair.Value.Origin.StartsWith(
                                     SkillOriginPrefix,
                                     StringComparison.Ordinal)
                                 && !_claimedSkillTools.Contains(pair.Key))
                     .ToArray())
        {
            _requestedByName.Remove(pair.Key);
            _effectiveByName.Remove(pair.Key);
            MarkStateChanged(
                ToolDisclosureReasonCodes.SkillActivationRemoved);
        }
    }

    public string ActivateFromModel(
        string name,
        string version,
        string descriptorDigest)
    {
        if (!_snapshot.TryGet(name, out var tool)
            || tool is null
            || !string.Equals(
                tool.Visibility,
                ToolVisibilities.Deferred,
                StringComparison.Ordinal))
        {
            return ToolDisclosureReasonCodes.NotDeferred;
        }

        if (!string.Equals(tool.Version, version, StringComparison.Ordinal)
            || !string.Equals(
                tool.Digest,
                descriptorDigest,
                StringComparison.Ordinal))
        {
            return ToolDisclosureReasonCodes.ExactIdentityMismatch;
        }

        if (!_policyByName.TryGetValue(tool.Name, out var policy)
            || !policy.Search.Allowed
            || !policy.ModelActivation.Allowed
            || !policy.Revalidation.Allowed)
        {
            return ToolDisclosureReasonCodes.NotAuthorized;
        }

        if (_requestedByName.TryGetValue(tool.Name, out var existing)
            && existing.Matches(tool)
            && string.Equals(
                existing.Origin,
                ModelOrigin,
                StringComparison.Ordinal))
        {
            return ToolDisclosureReasonCodes.AlreadyActivated;
        }

        if (!_requestedByName.ContainsKey(tool.Name)
            && _requestedByName.Count >= _limits.MaxActivatedDeferredTools)
        {
            return ToolDisclosureReasonCodes.CapacityExceeded;
        }

        _invalidatedByName.Remove(tool.Name);
        _requestedByName[tool.Name] = FromTool(tool, ModelOrigin);
        _effectiveByName[tool.Name] = tool;
        MarkStateChanged(ToolDisclosureReasonCodes.ActivatedByModel);
        return ToolDisclosureReasonCodes.ActivatedByModel;
    }

    public JsonElement ToSnapshotExtension()
    {
        var effectiveTools = EffectiveProviderTools;
        var hiddenDigest = DeferredOnlyDigest;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("policyId", PolicyId);
            writer.WriteString("policyVersion", PolicyVersion);
            writer.WriteString("catalogDigest", _snapshot.Digest);
            writer.WriteNumber("catalogGeneration", _snapshot.Generation);
            writer.WriteString("baseDirectDigest", BaseDirectDigest);
            writer.WriteString(
                "effectiveDirectDigest",
                ComputeDescriptorDigest(
                    "effective_direct_tools",
                    effectiveTools));
            writer.WriteString("deferredCatalogDigest", hiddenDigest);
            writer.WriteString("stateDigest", StateDigest);
            writer.WriteString("decisionDigest", DecisionDigest);
            writer.WriteString("reasonDigest", ReasonDigest);
            writer.WritePropertyName("baseDirect");
            WriteEntries(writer, _snapshot.DirectTools);
            writer.WritePropertyName("activatedDeferred");
            writer.WriteStartArray();
            foreach (var tool in EffectiveActivatedDeferred)
            {
                var record = _requestedByName[tool.Name];
                WriteActivation(writer, record);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("requestedActivations");
            writer.WriteStartArray();
            foreach (var record in RequestedActivations)
            {
                WriteActivation(writer, record);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("controlTools");
            writer.WriteStartArray();
            foreach (var tool in effectiveTools.Where(
                         tool => ToolDisclosureControlNames.IsReserved(
                             tool.Name)))
            {
                writer.WriteStringValue(tool.Name);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("reasonCodes");
            writer.WriteStartArray();
            foreach (var reason in CurrentReasonCodes())
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public ToolDisclosureJournalRecord ToJournalRecord(
        IEnumerable<string> reasonCodes)
    {
        return new ToolDisclosureJournalRecord(
            PolicyId,
            PolicyVersion,
            DecisionDigest,
            reasonCodes
                .Concat(StateReasonCodes)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            RequestedActivations);
    }

    private Dictionary<string, ToolCatalogEntry> ResolveEffective()
    {
        var effective = new Dictionary<string, ToolCatalogEntry>(
            StringComparer.Ordinal);
        foreach (var record in _requestedByName.Values.ToArray())
        {
            if (!_snapshot.TryGet(record.Name, out var tool)
                || tool is null)
            {
                _stateReasonCodes.Add(
                    ToolDisclosureReasonCodes.CatalogEntryUnavailable);
                _invalidatedByName[record.Name] =
                    ToolDisclosureReasonCodes.CatalogEntryUnavailable;
                continue;
            }

            if (!record.Matches(tool))
            {
                _stateReasonCodes.Add(
                    ToolDisclosureReasonCodes.CatalogEntryChanged);
                _invalidatedByName[record.Name] =
                    ToolDisclosureReasonCodes.CatalogEntryChanged;
                continue;
            }

            if (!_policyByName.TryGetValue(record.Name, out var policy)
                || !policy.Revalidation.Allowed
                || (string.Equals(
                        record.Origin,
                        ModelOrigin,
                        StringComparison.Ordinal)
                    && !policy.ModelActivation.Allowed)
                || (record.Origin.StartsWith(
                        SkillOriginPrefix,
                        StringComparison.Ordinal)
                    && !policy.SkillActivation.Allowed))
            {
                _stateReasonCodes.Add(
                    ToolDisclosureReasonCodes.RevalidationDenied);
                _invalidatedByName[record.Name] =
                    ToolDisclosureReasonCodes.RevalidationDenied;
                continue;
            }

            effective.Add(record.Name, tool);
        }

        foreach (var name in _invalidatedByName.Keys)
        {
            _requestedByName.Remove(name);
        }

        if (_invalidatedByName.Count > 0)
        {
            _stateChanged = true;
        }

        return effective;
    }

    private void MarkStateChanged(string reasonCode)
    {
        _stateChanged = true;
        _stateReasonCodes.Add(reasonCode);
        DecisionDigest = ComputeDecisionDigest();
        ReasonDigest = ComputeReasonDigest();
    }

    private string ComputeDecisionDigest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "tool_disclosure_decisions");
        digest.Add("policyId", PolicyId);
        digest.Add("policyVersion", PolicyVersion);
        foreach (var pair in _policyByName.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            digest.Add("toolName", pair.Value.Tool.Name);
            digest.Add("toolVersion", pair.Value.Tool.Version);
            digest.Add("toolDigest", pair.Value.Tool.Digest);
            digest.Add(
                "searchAllowed",
                pair.Value.Search.Allowed ? "1" : "0");
            digest.Add(
                "modelActivationAllowed",
                pair.Value.ModelActivation.Allowed ? "1" : "0");
            digest.Add(
                "skillActivationAllowed",
                pair.Value.SkillActivation.Allowed ? "1" : "0");
            digest.Add(
                "revalidationAllowed",
                pair.Value.Revalidation.Allowed ? "1" : "0");
        }

        foreach (var record in RequestedActivations)
        {
            digest.Add("requestedName", record.Name);
            digest.Add("requestedVersion", record.Version);
            digest.Add("requestedDigest", record.DescriptorDigest);
            digest.Add("requestedSource", record.Source);
            digest.Add("requestedOrigin", record.Origin);
        }

        return digest.Finish();
    }

    private string ComputeReasonDigest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "tool_disclosure_reasons");
        foreach (var pair in _policyByName.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            digest.Add("toolName", pair.Value.Tool.Name);
            digest.Add("searchReason", pair.Value.Search.ReasonCode);
            digest.Add(
                "modelActivationReason",
                pair.Value.ModelActivation.ReasonCode);
            digest.Add(
                "skillActivationReason",
                pair.Value.SkillActivation.ReasonCode);
            digest.Add(
                "revalidationReason",
                pair.Value.Revalidation.ReasonCode);
        }

        foreach (var reason in CurrentReasonCodes())
        {
            digest.Add("reason", reason);
        }

        return digest.Finish();
    }

    private IReadOnlyList<string> CurrentReasonCodes()
    {
        return _policyByName.Values
            .SelectMany(
                value => new[]
                {
                    value.Search.ReasonCode,
                    value.ModelActivation.ReasonCode,
                    value.SkillActivation.ReasonCode,
                    value.Revalidation.ReasonCode
                })
            .Concat(_stateReasonCodes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static ToolActivationRecord FromTool(
        ToolCatalogEntry tool,
        string origin)
    {
        return new ToolActivationRecord(
            tool.Name,
            tool.Version,
            tool.Digest,
            tool.Toolset,
            origin);
    }

    private static int Score(
        ToolSearchDocument document,
        string normalizedQuery,
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, int> documentFrequencies,
        IReadOnlyList<double> averageFieldLengths,
        int documentCount)
    {
        var tool = document.Tool;
        var reference = Normalize(tool.Name + "@" + tool.Version);
        var name = Normalize(tool.Name);
        var description = Normalize(tool.Description);
        var source = Normalize(tool.Toolset);
        var score = 0L;
        if (string.Equals(
                normalizedQuery,
                reference,
                StringComparison.Ordinal)
            || string.Equals(
                normalizedQuery,
                name,
                StringComparison.Ordinal))
        {
            score += 10_000_000;
        }

        if (name.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 2_000_000;
        }

        if (description.Contains(
                normalizedQuery,
                StringComparison.Ordinal))
        {
            score += 800_000;
        }

        if (source.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 400_000;
        }

        foreach (var term in queryTerms)
        {
            var nameFrequency = Frequency(document.Name, term);
            var descriptionFrequency = Frequency(
                document.Description,
                term);
            var toolsetFrequency = Frequency(document.Toolset, term);
            var parameterFrequency = Frequency(document.Parameters, term);
            if (nameFrequency == 0
                && descriptionFrequency == 0
                && toolsetFrequency == 0
                && parameterFrequency == 0)
            {
                continue;
            }

            score += ToolSearchScorer.ScoreTerm(
                documentCount,
                documentFrequencies[term],
                new[]
                {
                    new Bm25FieldMatch(
                        nameFrequency,
                        document.Name.TermCount,
                        averageFieldLengths[0],
                        weight: 8,
                        lengthNormalization: 0.2),
                    new Bm25FieldMatch(
                        descriptionFrequency,
                        document.Description.TermCount,
                        averageFieldLengths[1],
                        weight: 1,
                        lengthNormalization: 0.75),
                    new Bm25FieldMatch(
                        toolsetFrequency,
                        document.Toolset.TermCount,
                        averageFieldLengths[2],
                        weight: 3,
                        lengthNormalization: 0.25),
                    new Bm25FieldMatch(
                        parameterFrequency,
                        document.Parameters.TermCount,
                        averageFieldLengths[3],
                        weight: 2,
                        lengthNormalization: 0.5)
                });
        }

        return score >= int.MaxValue
            ? int.MaxValue
            : checked((int)score);
    }

    private static string Normalize(string value)
    {
        return value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private static int Frequency(TokenizedTerms field, string term)
    {
        return field.Frequencies.TryGetValue(term, out var frequency)
            ? frequency
            : 0;
    }

    private static ToolSearchDocument CreateSearchDocument(
        ToolCatalogEntry tool)
    {
        return new ToolSearchDocument(
            tool,
            TokenizeToolField(new[] { tool.Name }),
            TokenizeToolField(new[] { tool.Description }),
            TokenizeToolField(new[] { tool.Toolset }),
            TokenizeToolField(ParameterNames(tool.ParametersSchema)));
    }

    private static TokenizedTerms TokenizeToolField(
        IEnumerable<string> values)
    {
        try
        {
            return ToolDocumentTokenizer.TokenizeTextSegments(
                values,
                "tool");
        }
        catch (LexicalSearchLimitException)
        {
            return new TokenizedTerms(
                new Dictionary<string, int>(StringComparer.Ordinal),
                termCount: 0,
                inputUtf8Bytes: 0);
        }
    }

    private static IReadOnlyList<string> ParameterNames(JsonElement schema)
    {
        var names = new List<string>();
        var utf8Bytes = 0;
        var nodes = 0;
        CollectParameterNames(
            schema,
            names,
            ref utf8Bytes,
            ref nodes,
            depth: 0);
        return names;
    }

    private static void CollectParameterNames(
        JsonElement value,
        ICollection<string> names,
        ref int utf8Bytes,
        ref int nodes,
        int depth)
    {
        if (depth > 64
            || nodes >= MaxParameterSchemaNodes
            || names.Count >= MaxParameterNames)
        {
            return;
        }

        nodes++;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (names.Count >= MaxParameterNames
                    || nodes >= MaxParameterSchemaNodes)
                {
                    return;
                }

                if (string.Equals(
                        property.Name,
                        "properties",
                        StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var parameter in
                             property.Value.EnumerateObject())
                    {
                        if (!TryAddParameterName(
                                parameter.Name,
                                names,
                                ref utf8Bytes))
                        {
                            return;
                        }

                        var expanded = ExpandIdentifier(
                            parameter.Name);
                        if (!string.Equals(
                                expanded,
                                parameter.Name,
                                StringComparison.Ordinal)
                            && !TryAddParameterName(
                                expanded,
                                names,
                                ref utf8Bytes))
                        {
                            return;
                        }

                        CollectParameterNames(
                            parameter.Value,
                            names,
                            ref utf8Bytes,
                            ref nodes,
                            depth + 1);
                    }

                    continue;
                }

                CollectParameterNames(
                    property.Value,
                    names,
                    ref utf8Bytes,
                    ref nodes,
                    depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectParameterNames(
                    item,
                    names,
                    ref utf8Bytes,
                    ref nodes,
                    depth + 1);
            }
        }
    }

    private static bool TryAddParameterName(
        string value,
        ICollection<string> names,
        ref int utf8Bytes)
    {
        if (names.Count >= MaxParameterNames)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if (utf8Bytes > MaxParameterNameUtf8Bytes - bytes)
        {
            return false;
        }

        names.Add(value);
        utf8Bytes += bytes;
        return true;
    }

    private static string ExpandIdentifier(string value)
    {
        var expanded = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0)
            {
                var previous = value[index - 1];
                var nextIsLower =
                    index + 1 < value.Length
                    && char.IsLower(value[index + 1]);
                var caseBoundary =
                    char.IsUpper(current)
                    && (char.IsLower(previous)
                        || char.IsUpper(previous) && nextIsLower);
                var digitBoundary =
                    char.IsDigit(current) != char.IsDigit(previous)
                    && (char.IsLetterOrDigit(current)
                        && char.IsLetterOrDigit(previous));
                if (caseBoundary || digitBoundary)
                {
                    expanded.Append(' ');
                }
            }

            expanded.Append(current);
        }

        return expanded.ToString();
    }

    private sealed class ToolSearchDocument
    {
        public ToolSearchDocument(
            ToolCatalogEntry tool,
            TokenizedTerms name,
            TokenizedTerms description,
            TokenizedTerms toolset,
            TokenizedTerms parameters)
        {
            Tool = tool;
            Name = name;
            Description = description;
            Toolset = toolset;
            Parameters = parameters;
        }

        public ToolCatalogEntry Tool { get; }

        public TokenizedTerms Name { get; }

        public TokenizedTerms Description { get; }

        public TokenizedTerms Toolset { get; }

        public TokenizedTerms Parameters { get; }
    }

    private static ToolDescriptor CreateSearchDescriptor(
        ToolDisclosureLimits limits)
    {
        return new ToolDescriptor
        {
            Name = ToolDisclosureControlNames.Search,
            Version = "1",
            Description =
                "Search the authorized deferred game-tool catalog. "
                + "Queries may use any language. Use the exact identity "
                + "returned here with runtime_tool_activate.",
            ParametersSchema = SearchParametersSchema(limits),
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
            Name = ToolDisclosureControlNames.Activate,
            Version = "1",
            Description =
                "Activate one exact deferred game tool. The tool becomes "
                + "callable from the next model turn and then follows the "
                + "normal game-tool safety and action pipeline.",
            ParametersSchema = ActivationParametersSchema(),
            Effect = ToolEffects.AgentLocalWrite,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 1_000,
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "runtime",
            Visibility = ToolVisibilities.Direct
        };
    }

    private static JsonElement SearchParametersSchema(
        ToolDisclosureLimits limits)
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
            writer.WriteString("type", "string");
            writer.WriteNumber("minLength", 1);
            writer.WriteNumber(
                "maxLength",
                limits.MaxSearchQueryUtf8Bytes);
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

    private static JsonElement ActivationParametersSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "minLength": 1, "maxLength": 96 },
                "version": { "type": "string", "minLength": 1, "maxLength": 32 },
                "descriptorDigest": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 256
                }
              },
              "required": [ "name", "version", "descriptorDigest" ],
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }

    private static ToolDescriptor CloneDescriptor(ToolDescriptor descriptor)
    {
        return ProtocolJson.DeserializeToolDescriptor(
            ProtocolJson.Serialize(descriptor));
    }

    private static string ComputeDescriptorDigest(
        string type,
        IEnumerable<ToolDescriptor> tools)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", type);
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

    private static string ComputeEntryDigest(
        string type,
        IEnumerable<ToolCatalogEntry> tools)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", type);
        foreach (var tool in tools.OrderBy(
                     value => value.Name,
                     StringComparer.Ordinal))
        {
            digest.Add("name", tool.Name);
            digest.Add("version", tool.Version);
            digest.Add("digest", tool.Digest);
        }

        return digest.Finish();
    }

    internal static string ComputeStateDigest(
        IEnumerable<ToolActivationRecord> records)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "tool_activation_state");
        foreach (var record in records
                     .OrderBy(value => value.Name, StringComparer.Ordinal)
                     .ThenBy(value => value.Version, StringComparer.Ordinal))
        {
            digest.Add("name", record.Name);
            digest.Add("version", record.Version);
            digest.Add("descriptorDigest", record.DescriptorDigest);
            digest.Add("source", record.Source);
            digest.Add("origin", record.Origin);
        }

        return digest.Finish();
    }

    private static void WriteEntries(
        Utf8JsonWriter writer,
        IEnumerable<ToolCatalogEntry> tools)
    {
        writer.WriteStartArray();
        foreach (var tool in tools.OrderBy(
                     value => value.Name,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("version", tool.Version);
            writer.WriteString("descriptorDigest", tool.Digest);
            writer.WriteString("source", tool.Toolset);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static void WriteActivation(
        Utf8JsonWriter writer,
        ToolActivationRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("name", record.Name);
        writer.WriteString("version", record.Version);
        writer.WriteString(
            "descriptorDigest",
            record.DescriptorDigest);
        writer.WriteString("source", record.Source);
        writer.WriteString("origin", record.Origin);
        writer.WriteEndObject();
    }
}

internal sealed class ToolDisclosureEvaluator
{
    private readonly IToolDisclosurePolicy _policy;
    private readonly string _policyId;
    private readonly string _policyVersion;

    public ToolDisclosureEvaluator(IToolDisclosurePolicy? policy)
    {
        _policy = policy ?? DefaultToolDisclosurePolicy.Instance;
        _policyId = RuntimeGuard.RequiredId(
            _policy.PolicyId,
            nameof(IToolDisclosurePolicy.PolicyId));
        _policyVersion = RuntimeGuard.RequiredUtf8(
            _policy.Version,
            32,
            nameof(IToolDisclosurePolicy.Version));
    }

    public ToolDisclosurePlan Evaluate(
        AgentRun run,
        string turnId,
        ToolCatalogSnapshot snapshot,
        IReadOnlyList<ToolActivationRecord> requested,
        ToolDisclosureLimits limits)
    {
        var decisions =
            new Dictionary<string, ToolDisclosurePolicyResult>(
                StringComparer.Ordinal);
        foreach (var tool in snapshot.DeferredTools)
        {
            decisions.Add(
                tool.Name,
                new ToolDisclosurePolicyResult(
                    tool,
                    EvaluateOne(
                        run,
                        turnId,
                        tool,
                        ToolDisclosurePurposes.Search,
                        origin: null),
                    EvaluateOne(
                        run,
                        turnId,
                        tool,
                        ToolDisclosurePurposes.ModelActivation,
                        "model"),
                    EvaluateOne(
                        run,
                        turnId,
                        tool,
                        ToolDisclosurePurposes.SkillActivation,
                        "skill"),
                    EvaluateOne(
                        run,
                        turnId,
                        tool,
                        ToolDisclosurePurposes.Revalidation,
                        "durable")));
        }

        return new ToolDisclosurePlan(
            _policyId,
            _policyVersion,
            snapshot,
            limits,
            new ReadOnlyDictionary<string, ToolDisclosurePolicyResult>(
                decisions),
            requested);
    }

    private ToolDisclosureDecision EvaluateOne(
        AgentRun run,
        string turnId,
        ToolCatalogEntry tool,
        string purpose,
        string? origin)
    {
        try
        {
            return _policy.Evaluate(
                       new ToolDisclosureRequest(
                           run,
                           turnId,
                           tool,
                           purpose,
                           origin))
                   ?? ToolDisclosureDecision.Deny(
                       ToolDisclosureReasonCodes.PolicyDecisionInvalid);
        }
        catch
        {
            return ToolDisclosureDecision.Deny(
                ToolDisclosureReasonCodes.PolicyError);
        }
    }
}

internal sealed class ToolDisclosureJournalRecord
{
    public ToolDisclosureJournalRecord(
        string policyId,
        string policyVersion,
        string decisionDigest,
        IReadOnlyList<string> reasonCodes,
        IReadOnlyList<ToolActivationRecord> activations)
    {
        PolicyId = RuntimeGuard.RequiredId(policyId, nameof(policyId));
        PolicyVersion = RuntimeGuard.RequiredUtf8(
            policyVersion,
            32,
            nameof(policyVersion));
        DecisionDigest = RuntimeGuard.RequiredUtf8(
            decisionDigest,
            256,
            nameof(decisionDigest));
        ReasonCodes = new ReadOnlyCollection<string>(
            RuntimeGuard.CopyStrings(
                    reasonCodes,
                    64,
                    128,
                    nameof(reasonCodes),
                    sort: true,
                    requireUnique: true)
                .ToList());
        Activations = new ReadOnlyCollection<ToolActivationRecord>(
            activations.Select(value => value.Clone()).ToList());
        StateDigest = ToolDisclosurePlan.ComputeStateDigest(Activations);
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public string DecisionDigest { get; }

    public string StateDigest { get; }

    public IReadOnlyList<string> ReasonCodes { get; }

    public IReadOnlyList<ToolActivationRecord> Activations { get; }
}

internal static class ToolDisclosureJournalCodec
{
    private const string ContentType =
        "application/vnd.game-agent.tool-disclosure+json";

    public static JsonElement Encode(ToolDisclosureJournalRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", ContentType);
            writer.WriteString("policyId", record.PolicyId);
            writer.WriteString("policyVersion", record.PolicyVersion);
            writer.WriteString("decisionDigest", record.DecisionDigest);
            writer.WriteString("stateDigest", record.StateDigest);
            writer.WritePropertyName("reasonCodes");
            writer.WriteStartArray();
            foreach (var reason in record.ReasonCodes)
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("activations");
            writer.WriteStartArray();
            foreach (var activation in record.Activations)
            {
                ToolDisclosurePlan.WriteActivation(writer, activation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static ToolDisclosureJournalRecord Decode(
        JsonElement payload,
        int maximumActivations)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !TryReadString(payload, "contentType", out var contentType)
            || !string.Equals(
                contentType,
                ContentType,
                StringComparison.Ordinal)
            || !TryReadString(payload, "policyId", out var policyId)
            || !TryReadString(
                payload,
                "policyVersion",
                out var policyVersion)
            || !TryReadString(
                payload,
                "decisionDigest",
                out var decisionDigest)
            || !TryReadString(
                payload,
                "stateDigest",
                out var stateDigest)
            || !payload.TryGetProperty(
                "reasonCodes",
                out var reasonsElement)
            || reasonsElement.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty(
                "activations",
                out var activationsElement)
            || activationsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "A durable tool-disclosure state is malformed.");
        }

        var reasons = new List<string>();
        foreach (var item in reasonsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A durable tool-disclosure reason is malformed.");
            }

            reasons.Add(item.GetString()!);
        }

        var activations = new List<ToolActivationRecord>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in activationsElement.EnumerateArray())
        {
            if (activations.Count >= maximumActivations
                || item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, "name", out var name)
                || !TryReadString(item, "version", out var version)
                || !TryReadString(
                    item,
                    "descriptorDigest",
                    out var descriptorDigest)
                || !TryReadString(item, "source", out var source)
                || !TryReadString(item, "origin", out var origin)
                || !names.Add(name))
            {
                throw new InvalidDataException(
                    "A durable tool activation is malformed or over capacity.");
            }

            activations.Add(
                new ToolActivationRecord(
                    name,
                    version,
                    descriptorDigest,
                    source,
                    origin));
        }

        var record = new ToolDisclosureJournalRecord(
            policyId,
            policyVersion,
            decisionDigest,
            reasons,
            activations);
        if (!string.Equals(
                record.StateDigest,
                stateDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable tool-disclosure state digest does not match.");
        }

        return record;
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
