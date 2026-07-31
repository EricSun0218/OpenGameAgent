using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Runtime;

/// <summary>
/// Builds one explicit host-selected runtime profile. Source definitions do
/// not carry tool or skill permissions into the profile; the host must add
/// every provider, context item, tool, skill, and memory service explicitly.
/// </summary>
public sealed class AgentProfileBuilder
{
    private const int MaxProviders = 16;
    private const int MaxContextCandidates = 512;
    private readonly AgentDefinition _baseDefinition;
    private readonly List<IStreamingModelProvider> _providers = new();
    private readonly List<ContextCandidate> _context = new();
    private readonly List<ToolDescriptor> _tools = new();
    private readonly List<SkillManifest> _skills = new();
    private RuntimeMemoryLifecycle? _memoryLifecycle;
    private IRuntimeMemoryPolicy? _memoryPolicy;
    private RuntimeMemoryIntegrationOptions? _memoryOptions;
    private bool _disposeMemoryOnShutdown;

    public AgentProfileBuilder(AgentDefinition definition)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        ProtocolValidator.EnsureValid(definition);
        _baseDefinition = Clone(definition);
        _baseDefinition.Toolsets = new List<string>();
        _baseDefinition.Skills = new List<string>();
    }

    public AgentProfileBuilder AddProvider(
        IStreamingModelProvider provider)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (_providers.Count >= MaxProviders)
        {
            throw new RuntimeContentLimitException(
                nameof(provider),
                "agent_profile_provider_count_exceeded",
                "An agent profile cannot select more than 16 providers.");
        }

        _providers.Add(provider);
        return this;
    }

    public AgentProfileBuilder AddContext(ContextCandidate context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (_context.Count >= MaxContextCandidates)
        {
            throw new RuntimeContentLimitException(
                nameof(context),
                "agent_profile_context_count_exceeded",
                "An agent profile cannot select more than 512 context "
                + "candidates.");
        }

        _context.Add(context.Clone());
        return this;
    }

    public AgentProfileBuilder AddContext(
        IEnumerable<ContextCandidate> context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        foreach (var item in context)
        {
            AddContext(
                item
                ?? throw new ArgumentException(
                    "Context collections cannot contain null.",
                    nameof(context)));
        }

        return this;
    }

    public AgentProfileBuilder AllowTools(
        IEnumerable<ToolDescriptor> tools)
    {
        CopyTools(tools, _tools);
        return this;
    }

    /// <summary>
    /// Selects only the named toolsets from a trusted host catalog. Untrusted
    /// content cannot call this method or manufacture permissions.
    /// </summary>
    public AgentProfileBuilder AllowToolsets(
        IEnumerable<ToolDescriptor> catalog,
        IEnumerable<string> toolsets)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (toolsets is null)
        {
            throw new ArgumentNullException(nameof(toolsets));
        }

        var selectedToolsets = CopyStrings(
            toolsets,
            RegistryLimits.DefaultMaxTools,
            96,
            nameof(toolsets));
        var admitted = new List<ToolDescriptor>();
        var examined = 0;
        foreach (var tool in catalog)
        {
            if (examined >= RegistryLimits.DefaultMaxTools)
            {
                throw new RuntimeContentLimitException(
                    nameof(catalog),
                    "agent_profile_tool_catalog_count_exceeded",
                    "The candidate tool catalog exceeds the profile "
                    + "inspection limit.");
            }

            examined++;
            if (tool is null)
            {
                throw new ArgumentException(
                    "Tool catalogs cannot contain null.",
                    nameof(catalog));
            }

            if (selectedToolsets.Contains(
                    tool.Toolset,
                    StringComparer.Ordinal))
            {
                admitted.Add(tool);
            }
        }

        CopyTools(admitted, _tools);
        return this;
    }

    public AgentProfileBuilder AllowSkills(
        IEnumerable<SkillManifest> skills)
    {
        if (skills is null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        foreach (var skill in skills)
        {
            if (_skills.Count >= RegistryLimits.DefaultMaxSkills)
            {
                throw new RuntimeContentLimitException(
                    nameof(skills),
                    "agent_profile_skill_count_exceeded",
                    "An agent profile cannot select more than 512 skills.");
            }

            if (skill is null)
            {
                throw new ArgumentException(
                    "Skill collections cannot contain null.",
                    nameof(skills));
            }

            _skills.Add(Clone(skill));
        }

        return this;
    }

    public AgentProfileBuilder WithMemory(
        RuntimeMemoryLifecycle lifecycle,
        IRuntimeMemoryPolicy policy,
        RuntimeMemoryIntegrationOptions? options = null,
        bool disposeOnShutdown = false)
    {
        if (_memoryLifecycle is not null)
        {
            throw new InvalidOperationException(
                "Memory is already selected for this profile.");
        }

        var selectedLifecycle = lifecycle
                                ?? throw new ArgumentNullException(
                                    nameof(lifecycle));
        var selectedPolicy = policy
                             ?? throw new ArgumentNullException(
                                 nameof(policy));
        var selectedOptions = SnapshotMemoryOptions(options);
        _memoryLifecycle = selectedLifecycle;
        _memoryPolicy = selectedPolicy;
        _memoryOptions = selectedOptions;
        _disposeMemoryOnShutdown = disposeOnShutdown;
        return this;
    }

    public AgentRuntimeProfile Build()
    {
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException(
                "An agent profile requires an explicit provider "
                + "selection.");
        }

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        var stableProviders =
            new IStreamingModelProvider[_providers.Count];
        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var providerId = Required(
                provider.ProviderId,
                128,
                nameof(_providers));
            if (!providerIds.Add(providerId))
            {
                throw new ArgumentException(
                    "Agent profile provider ids must be unique.",
                    nameof(_providers));
            }

            stableProviders[index] = provider;
        }

        var tools = _tools
            .Select(Clone)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        _ = new ToolCatalogRegistry().Replace(tools);
        var skills = _skills
            .Select(Clone)
            .OrderBy(item => item.SkillId, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
        _ = new SkillCatalogRegistry().Replace(skills);

        var context = _context
            .Select(item => item.Clone())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (context
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Agent profile context ids must be unique.",
                nameof(_context));
        }

        var definition = Clone(_baseDefinition);
        definition.Toolsets = tools
            .Select(item => item.Toolset)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        definition.Skills = skills
            .Select(item => item.SkillId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        ProtocolValidator.EnsureValid(definition);

        IRuntimeMemoryPolicy? stableMemoryPolicy = null;
        if (_memoryLifecycle is not null)
        {
            string policyId;
            string policyVersion;
            try
            {
                policyId = Required(
                    _memoryPolicy!.PolicyId,
                    128,
                    nameof(_memoryPolicy));
                policyVersion = Required(
                    _memoryPolicy.Version,
                    128,
                    nameof(_memoryPolicy));
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                throw new ArgumentException(
                    "The selected memory policy identity is invalid.",
                    nameof(_memoryPolicy),
                    exception);
            }

            stableMemoryPolicy = new ProfileBoundMemoryPolicy(
                _memoryPolicy!,
                policyId,
                policyVersion);
        }

        return new AgentRuntimeProfile(
            definition,
            stableProviders,
            context,
            tools,
            skills,
            _memoryLifecycle,
            stableMemoryPolicy,
            _memoryOptions,
            _disposeMemoryOnShutdown);
    }

    private static RuntimeMemoryIntegrationOptions SnapshotMemoryOptions(
        RuntimeMemoryIntegrationOptions? options)
    {
        var source = options ?? new RuntimeMemoryIntegrationOptions();
        if (source.MaxRecallContextCandidates is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory recall context capacity is invalid.");
        }

        if (source.MaxCommitMutations is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory commit capacity is invalid.");
        }

        if (source.MaxCommitAggregateContentUtf8Bytes
            is < 1 or > 768 * 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory commit byte capacity is invalid.");
        }

        return new RuntimeMemoryIntegrationOptions
        {
            MaxRecallContextCandidates =
                source.MaxRecallContextCandidates,
            MaxCommitMutations = source.MaxCommitMutations,
            MaxCommitAggregateContentUtf8Bytes =
                source.MaxCommitAggregateContentUtf8Bytes
        };
    }

    private static void CopyTools(
        IEnumerable<ToolDescriptor>? tools,
        ICollection<ToolDescriptor> destination)
    {
        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        foreach (var tool in tools)
        {
            if (destination.Count >= RegistryLimits.DefaultMaxTools)
            {
                throw new RuntimeContentLimitException(
                    nameof(tools),
                    "agent_profile_tool_count_exceeded",
                    "An agent profile cannot select more than 512 tools.");
            }

            if (tool is null)
            {
                throw new ArgumentException(
                    "Tool collections cannot contain null.",
                    nameof(tools));
            }

            destination.Add(Clone(tool));
        }
    }

    private static IReadOnlyList<string> CopyStrings(
        IEnumerable<string> values,
        int maximumItems,
        int maximumUtf8Bytes,
        string parameterName)
    {
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (result.Count >= maximumItems)
            {
                throw new RuntimeContentLimitException(
                    parameterName,
                    "agent_profile_reference_count_exceeded",
                    "An agent profile reference collection exceeds its "
                    + "limit.");
            }

            var safe = Required(
                value,
                maximumUtf8Bytes,
                parameterName);
            if (!unique.Add(safe))
            {
                throw new ArgumentException(
                    "Agent profile references must be unique.",
                    parameterName);
            }

            result.Add(safe);
        }

        return result;
    }

    private static string Required(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }

    private static AgentDefinition Clone(AgentDefinition value)
    {
        return ProtocolJson.DeserializeAgentDefinition(
            ProtocolJson.Serialize(value));
    }

    private static ToolDescriptor Clone(ToolDescriptor value)
    {
        return ProtocolJson.DeserializeToolDescriptor(
            ProtocolJson.Serialize(value));
    }

    private static SkillManifest Clone(SkillManifest value)
    {
        return ProtocolJson.DeserializeSkillManifest(
            ProtocolJson.Serialize(value));
    }

    private static UTF8Encoding StrictUtf8 { get; } =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed class ProfileBoundMemoryPolicy : IRuntimeMemoryPolicy
    {
        private readonly IRuntimeMemoryPolicy _inner;

        public ProfileBoundMemoryPolicy(
            IRuntimeMemoryPolicy inner,
            string policyId,
            string version)
        {
            _inner = inner;
            PolicyId = policyId;
            Version = version;
        }

        public string PolicyId { get; }

        public string Version { get; }

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return _inner.PlanRecall(context);
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            return _inner.SelectCommittedMutations(context);
        }
    }
}

public sealed class AgentRuntimeProfile
{
    private readonly AgentDefinition _definition;
    private readonly IReadOnlyList<IStreamingModelProvider> _providers;
    private readonly IReadOnlyList<ContextCandidate> _context;
    private readonly IReadOnlyList<ToolDescriptor> _tools;
    private readonly IReadOnlyList<SkillManifest> _skills;
    private readonly RuntimeMemoryLifecycle? _memoryLifecycle;
    private readonly IRuntimeMemoryPolicy? _memoryPolicy;
    private readonly RuntimeMemoryIntegrationOptions? _memoryOptions;
    private readonly bool _disposeMemoryOnShutdown;

    internal AgentRuntimeProfile(
        AgentDefinition definition,
        IReadOnlyList<IStreamingModelProvider> providers,
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<ToolDescriptor> tools,
        IReadOnlyList<SkillManifest> skills,
        RuntimeMemoryLifecycle? memoryLifecycle,
        IRuntimeMemoryPolicy? memoryPolicy,
        RuntimeMemoryIntegrationOptions? memoryOptions,
        bool disposeMemoryOnShutdown)
    {
        _definition = Clone(definition);
        _providers = providers;
        _context = new ReadOnlyCollection<ContextCandidate>(
            context.Select(item => item.Clone()).ToArray());
        _tools = new ReadOnlyCollection<ToolDescriptor>(
            tools.Select(Clone).ToArray());
        _skills = new ReadOnlyCollection<SkillManifest>(
            skills.Select(Clone).ToArray());
        _memoryLifecycle = memoryLifecycle;
        _memoryPolicy = memoryPolicy;
        _memoryOptions = memoryOptions;
        _disposeMemoryOnShutdown = disposeMemoryOnShutdown;
        ProviderIds = new ReadOnlyCollection<string>(
            providers.Select(item => item.ProviderId).ToArray());
        ProfileDigest = ComputeDigest();
    }

    public AgentDefinition AgentDefinition => Clone(_definition);

    public IReadOnlyList<string> ProviderIds { get; }

    public IReadOnlyList<ContextCandidate> Context =>
        new ReadOnlyCollection<ContextCandidate>(
            _context.Select(item => item.Clone()).ToArray());

    public IReadOnlyList<ToolDescriptor> Tools =>
        new ReadOnlyCollection<ToolDescriptor>(
            _tools.Select(Clone).ToArray());

    public IReadOnlyList<SkillManifest> Skills =>
        new ReadOnlyCollection<SkillManifest>(
            _skills.Select(Clone).ToArray());

    public bool HasMemory => _memoryLifecycle is not null;

    public string ProfileDigest { get; }

    /// <summary>
    /// Applies only the profile's explicitly selected capabilities. Empty tool
    /// and skill selections intentionally clear any implicit catalog.
    /// </summary>
    public GameAgentRuntimeBuilder ApplyTo(GameAgentRuntimeBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        foreach (var provider in _providers)
        {
            builder.AddProvider(provider);
        }

        builder.WithTools(_tools.Select(Clone));
        builder.WithSkills(_skills.Select(Clone));
        if (_memoryLifecycle is not null)
        {
            builder.WithMemory(
                _memoryLifecycle,
                _memoryPolicy!,
                _memoryOptions,
                _disposeMemoryOnShutdown);
        }

        return builder;
    }

    public DurableRunRequest CreateRunRequest(AgentRun run)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        ProtocolValidator.EnsureValid(run);
        return new DurableRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(run)),
            Context = _context.Select(item => item.Clone()).ToArray(),
            ActiveSkills = _skills
                .Select(
                    item => new SkillReference(
                        item.SkillId,
                        item.Version))
                .ToArray()
        };
    }

    private string ComputeDigest()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "definition",
                CanonicalJsonDigest.ComputeSha256(
                    ProtocolJson.ToElement(_definition)));
            writer.WritePropertyName("providers");
            WriteStrings(writer, ProviderIds);
            writer.WritePropertyName("context");
            writer.WriteStartArray();
            foreach (var candidate in _context)
            {
                writer.WriteStartObject();
                writer.WriteString("id", candidate.Id);
                writer.WriteString("category", candidate.Category);
                writer.WriteNumber("priority", candidate.Priority);
                writer.WriteBoolean("required", candidate.Required);
                writer.WriteBoolean("canDefer", candidate.CanDefer);
                if (candidate.EstimatedTokens.HasValue)
                {
                    writer.WriteNumber(
                        "estimatedTokens",
                        candidate.EstimatedTokens.Value);
                }
                else
                {
                    writer.WriteNull("estimatedTokens");
                }

                if (candidate.ExpiresAt.HasValue)
                {
                    writer.WriteString(
                        "expiresAt",
                        candidate.ExpiresAt.Value.ToString(
                            "O",
                            CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNull("expiresAt");
                }

                writer.WriteString("provenance", candidate.Provenance);
                if (candidate.Content.HasValue)
                {
                    writer.WriteString(
                        "contentDigest",
                        CanonicalJsonDigest.ComputeSha256(
                            candidate.Content.Value));
                }
                else
                {
                    writer.WriteString(
                        "resourceDigest",
                        candidate.Resource?.Digest);
                    writer.WriteString(
                        "resourceUri",
                        candidate.Resource?.Uri);
                    writer.WriteString(
                        "resourceMediaType",
                        candidate.Resource?.MediaType);
                    if (candidate.Resource?.SizeBytes is long sizeBytes)
                    {
                        writer.WriteNumber("resourceSizeBytes", sizeBytes);
                    }
                    else
                    {
                        writer.WriteNull("resourceSizeBytes");
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in _tools)
            {
                writer.WriteStringValue(
                    CanonicalJsonDigest.ComputeSha256(
                        ProtocolJson.ToElement(tool)));
            }

            writer.WriteEndArray();
            writer.WritePropertyName("skills");
            writer.WriteStartArray();
            foreach (var skill in _skills)
            {
                writer.WriteStringValue(
                    CanonicalJsonDigest.ComputeSha256(
                        ProtocolJson.ToElement(skill)));
            }

            writer.WriteEndArray();
            writer.WriteBoolean(
                "memoryConfigured",
                _memoryLifecycle is not null);
            writer.WriteBoolean(
                "disposeMemoryOnShutdown",
                _disposeMemoryOnShutdown);
            if (_memoryPolicy is not null)
            {
                writer.WriteString(
                    "memoryPolicyId",
                    _memoryPolicy.PolicyId);
                writer.WriteString(
                    "memoryPolicyVersion",
                    _memoryPolicy.Version);
                writer.WriteNumber(
                    "maxRecallContextCandidates",
                    _memoryOptions!.MaxRecallContextCandidates);
                writer.WriteNumber(
                    "maxCommitMutations",
                    _memoryOptions.MaxCommitMutations);
                writer.WriteNumber(
                    "maxCommitAggregateContentUtf8Bytes",
                    _memoryOptions.MaxCommitAggregateContentUtf8Bytes);
            }

            writer.WriteEndObject();
        }

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(output.ToArray());
        var result = new StringBuilder(64);
        foreach (var item in digest)
        {
            result.Append(
                item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static AgentDefinition Clone(AgentDefinition value)
    {
        return ProtocolJson.DeserializeAgentDefinition(
            ProtocolJson.Serialize(value));
    }

    private static ToolDescriptor Clone(ToolDescriptor value)
    {
        return ProtocolJson.DeserializeToolDescriptor(
            ProtocolJson.Serialize(value));
    }

    private static SkillManifest Clone(SkillManifest value)
    {
        return ProtocolJson.DeserializeSkillManifest(
            ProtocolJson.Serialize(value));
    }
}
