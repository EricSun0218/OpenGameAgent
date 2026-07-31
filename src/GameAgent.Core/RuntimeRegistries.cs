using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RegistryLimits
{
    public const int DefaultMaxTools = 512;

    public const int DefaultMaxSkills = 512;

    public RegistryLimits(
        int maxTools = DefaultMaxTools,
        int maxSkills = DefaultMaxSkills,
        int maxListItems = 256,
        JsonValueLimits? jsonLimits = null)
    {
        if (maxTools < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTools));
        }

        if (maxSkills < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSkills));
        }

        if (maxListItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxListItems));
        }

        MaxTools = maxTools;
        MaxSkills = maxSkills;
        MaxListItems = maxListItems;
        JsonLimits = jsonLimits ?? new JsonValueLimits();
    }

    public int MaxTools { get; }

    public int MaxSkills { get; }

    public int MaxListItems { get; }

    public JsonValueLimits JsonLimits { get; }
}

public sealed class ToolCatalogEntry
{
    internal ToolCatalogEntry(ToolDescriptor descriptor, RegistryLimits limits)
    {
        ProtocolValidator.EnsureValid(descriptor);
        Name = RuntimeGuard.RequiredUtf8(descriptor.Name, 96, nameof(descriptor));
        Version = RuntimeGuard.RequiredUtf8(descriptor.Version, 32, nameof(descriptor));
        Description = RuntimeGuard.RequiredUtf8(descriptor.Description, 2_048, nameof(descriptor));
        JsonValueInspector.ValidateAndMeasure(
            descriptor.ParametersSchema,
            limits.JsonLimits,
            nameof(descriptor.ParametersSchema));
        if (descriptor.ParametersSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool parametersSchema must be a JSON object.", nameof(descriptor));
        }

        ParametersSchema = descriptor.ParametersSchema.Clone();
        if (descriptor.ResultSchema.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                descriptor.ResultSchema.Value,
                limits.JsonLimits,
                nameof(descriptor.ResultSchema));
            if (descriptor.ResultSchema.Value.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Tool resultSchema must be a JSON object.", nameof(descriptor));
            }

            ResultSchema = descriptor.ResultSchema.Value.Clone();
        }

        Effect = descriptor.Effect;
        ConflictScopes = RuntimeGuard.CopyStrings(
            descriptor.ConflictScopes,
            limits.MaxListItems,
            128,
            nameof(descriptor.ConflictScopes),
            sort: true,
            requireUnique: true);
        ThreadAffinity = descriptor.ThreadAffinity;
        TimeoutMs = descriptor.TimeoutMs;
        RetryPolicy = descriptor.RetryPolicy;
        IdempotencyPolicy = descriptor.IdempotencyPolicy;
        Toolset = RuntimeGuard.RequiredUtf8(descriptor.Toolset, 96, nameof(descriptor.Toolset));
        Visibility = descriptor.Visibility;
        Extensions = RuntimeGuard.CopyExtensions(descriptor.Extensions, limits.JsonLimits);
        Digest = ComputeDigest();
    }

    public string Name { get; }

    public string Version { get; }

    public string Description { get; }

    public JsonElement ParametersSchema { get; }

    public JsonElement? ResultSchema { get; }

    public string Effect { get; }

    public IReadOnlyList<string> ConflictScopes { get; }

    public string ThreadAffinity { get; }

    public int TimeoutMs { get; }

    public string RetryPolicy { get; }

    public string IdempotencyPolicy { get; }

    public string Toolset { get; }

    public string Visibility { get; }

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; }

    public string Digest { get; }

    private string ComputeDigest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "tool");
        digest.Add("name", Name);
        digest.Add("version", Version);
        digest.Add("description", Description);
        digest.Add("parametersSchema", ParametersSchema);
        digest.Add("hasResultSchema", ResultSchema.HasValue ? "1" : "0");
        if (ResultSchema.HasValue)
        {
            digest.Add("resultSchema", ResultSchema.Value);
        }

        digest.Add("effect", Effect);
        digest.Add("conflictScopes", ConflictScopes);
        digest.Add("threadAffinity", ThreadAffinity);
        digest.Add("timeoutMs", TimeoutMs);
        digest.Add("retryPolicy", RetryPolicy);
        digest.Add("idempotencyPolicy", IdempotencyPolicy);
        digest.Add("toolset", Toolset);
        digest.Add("visibility", Visibility);
        foreach (var extension in Extensions)
        {
            digest.Add($"extension:{extension.Key}", extension.Value);
        }

        return digest.Finish();
    }
}

public sealed class ToolCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, ToolCatalogEntry> _byName;

    internal ToolCatalogSnapshot(long generation, IReadOnlyList<ToolCatalogEntry> tools)
    {
        Generation = generation;
        Tools = tools;
        DirectTools = new ReadOnlyCollection<ToolCatalogEntry>(
            tools.Where(item => string.Equals(item.Visibility, "direct", StringComparison.Ordinal))
                .ToList());
        DeferredTools = new ReadOnlyCollection<ToolCatalogEntry>(
            tools.Where(item => string.Equals(item.Visibility, "deferred", StringComparison.Ordinal))
                .ToList());
        _byName = new ReadOnlyDictionary<string, ToolCatalogEntry>(
            tools.ToDictionary(item => item.Name, StringComparer.Ordinal));
        Digest = ComputeDigest(tools);
    }

    public long Generation { get; }

    public string Digest { get; }

    public IReadOnlyList<ToolCatalogEntry> Tools { get; }

    public IReadOnlyList<ToolCatalogEntry> DirectTools { get; }

    public IReadOnlyList<ToolCatalogEntry> DeferredTools { get; }

    public bool TryGet(string name, out ToolCatalogEntry? entry)
    {
        if (name is null)
        {
            entry = null;
            return false;
        }

        return _byName.TryGetValue(name, out entry);
    }

    private static string ComputeDigest(IEnumerable<ToolCatalogEntry> tools)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "tool_catalog");
        foreach (var tool in tools)
        {
            digest.Add("toolName", tool.Name);
            digest.Add("toolVersion", tool.Version);
            digest.Add("toolDigest", tool.Digest);
        }

        return digest.Finish();
    }
}

public sealed class ToolCatalogRegistry
{
    private readonly object _sync = new();
    private readonly RegistryLimits _limits;
    private ToolCatalogSnapshot _current;

    public ToolCatalogRegistry(RegistryLimits? limits = null)
    {
        _limits = limits ?? new RegistryLimits();
        _current = new ToolCatalogSnapshot(0, Array.Empty<ToolCatalogEntry>());
    }

    public ToolCatalogSnapshot Current => Volatile.Read(ref _current);

    public ToolCatalogSnapshot Replace(IEnumerable<ToolDescriptor> descriptors)
    {
        if (descriptors is null)
        {
            throw new ArgumentNullException(nameof(descriptors));
        }

        var entries = Materialize(descriptors);
        lock (_sync)
        {
            var current = _current;
            var candidate = new ToolCatalogSnapshot(checked(current.Generation + 1), entries);
            if (string.Equals(candidate.Digest, current.Digest, StringComparison.Ordinal))
            {
                return current;
            }

            Volatile.Write(ref _current, candidate);
            return candidate;
        }
    }

    private IReadOnlyList<ToolCatalogEntry> Materialize(IEnumerable<ToolDescriptor> descriptors)
    {
        var entries = new List<ToolCatalogEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (descriptor is null)
            {
                throw new ArgumentException("A tool descriptor cannot be null.", nameof(descriptors));
            }

            if (entries.Count >= _limits.MaxTools)
            {
                throw new RuntimeContentLimitException(
                    nameof(descriptors),
                    "tool_count_exceeded",
                    $"Tool count exceeds {_limits.MaxTools}.");
            }

            var entry = new ToolCatalogEntry(descriptor, _limits);
            if (ToolDisclosureControlNames.IsReserved(entry.Name)
                || SkillRuntimeControlNames.IsReserved(entry.Name)
                || string.Equals(
                    entry.Name,
                    FinalOutputAdmissionControl.SubmitToolName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Tool name '{entry.Name}' is reserved by the runtime.",
                    nameof(descriptors));
            }

            if (!names.Add(entry.Name))
            {
                throw new ArgumentException(
                    $"Tool name '{entry.Name}' is registered more than once in one snapshot.",
                    nameof(descriptors));
            }

            entries.Add(entry);
        }

        entries.Sort((left, right) =>
        {
            var byName = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return byName != 0
                ? byName
                : StringComparer.Ordinal.Compare(left.Version, right.Version);
        });
        return new ReadOnlyCollection<ToolCatalogEntry>(entries);
    }
}

public sealed class SkillResource
{
    internal SkillResource(ResourceReference value)
    {
        Uri = RuntimeGuard.RequiredUtf8(value.Uri, 2_048, nameof(value.Uri));
        MediaType = RuntimeGuard.RequiredUtf8(value.MediaType, 128, nameof(value.MediaType));
        Digest = value.Digest;
        SizeBytes = value.SizeBytes;
        if (Digest is not null)
        {
            RuntimeGuard.RequiredUtf8(Digest, 256, nameof(value.Digest));
        }

        if (SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.SizeBytes));
        }
    }

    public string Uri { get; }

    public string MediaType { get; }

    public string? Digest { get; }

    public long? SizeBytes { get; }
}

public sealed class SkillCatalogEntry
{
    internal SkillCatalogEntry(SkillManifest manifest, RegistryLimits limits)
    {
        if (!string.Equals(
                manifest.ProtocolVersion,
                ProtocolConstants.ProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.SchemaVersion,
                ProtocolConstants.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Skill manifest protocolVersion and schemaVersion must match the runtime.",
                nameof(manifest));
        }

        SkillId = RuntimeGuard.RequiredId(manifest.SkillId, nameof(manifest.SkillId));
        Version = RuntimeGuard.RequiredUtf8(manifest.Version, 32, nameof(manifest.Version));
        DeclaredDigest = RuntimeGuard.RequiredUtf8(manifest.Digest, 256, nameof(manifest.Digest));
        Description = RuntimeGuard.RequiredUtf8(
            manifest.Description,
            2_048,
            nameof(manifest.Description));
        PromptFragments = RuntimeGuard.CopyStrings(
            manifest.PromptFragments,
            limits.MaxListItems,
            8_192,
            nameof(manifest.PromptFragments),
            sort: false,
            requireUnique: false);
        RequiredToolReferences = RuntimeGuard.CopyStrings(
            manifest.RequiredToolRefs,
            limits.MaxListItems,
            160,
            nameof(manifest.RequiredToolRefs),
            sort: true,
            requireUnique: true);
        OptionalToolReferences = RuntimeGuard.CopyStrings(
            manifest.OptionalToolRefs,
            limits.MaxListItems,
            160,
            nameof(manifest.OptionalToolRefs),
            sort: true,
            requireUnique: true);
        ContextProviderReferences = RuntimeGuard.CopyStrings(
            manifest.ContextProviderRefs,
            limits.MaxListItems,
            160,
            nameof(manifest.ContextProviderRefs),
            sort: true,
            requireUnique: true);

        if (manifest.ResourceRefs.Count > limits.MaxListItems)
        {
            throw new RuntimeContentLimitException(
                nameof(manifest.ResourceRefs),
                "skill_resource_count_exceeded",
                $"Skill resources exceed {limits.MaxListItems}.");
        }

        Resources = new ReadOnlyCollection<SkillResource>(
            manifest.ResourceRefs.Select(item => new SkillResource(item)).ToList());

        JsonValueInspector.ValidateAndMeasure(
            manifest.CapabilityRequirements,
            limits.JsonLimits,
            nameof(manifest.CapabilityRequirements));
        JsonValueInspector.ValidateAndMeasure(
            manifest.ActivationPolicy,
            limits.JsonLimits,
            nameof(manifest.ActivationPolicy));
        if (manifest.CapabilityRequirements.ValueKind != JsonValueKind.Object
            || manifest.ActivationPolicy.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Skill capabilityRequirements and activationPolicy must be JSON objects.",
                nameof(manifest));
        }

        CapabilityRequirements = manifest.CapabilityRequirements.Clone();
        ActivationPolicy = manifest.ActivationPolicy.Clone();
        Trust = RuntimeGuard.RequiredUtf8(manifest.Trust, 32, nameof(manifest.Trust));
        if (Trust is not ("builtin" or "trusted" or "untrusted"))
        {
            throw new ArgumentException(
                "Skill trust must be 'builtin', 'trusted', or 'untrusted'.",
                nameof(manifest));
        }

        Extensions = RuntimeGuard.CopyExtensions(manifest.Extensions, limits.JsonLimits);
        ContentDigest = ComputeDigest();
    }

    public string SkillId { get; }

    public string Version { get; }

    public string Reference => $"{SkillId}@{Version}";

    public string DeclaredDigest { get; }

    public string ContentDigest { get; }

    public string Description { get; }

    public IReadOnlyList<string> PromptFragments { get; }

    public IReadOnlyList<string> RequiredToolReferences { get; }

    public IReadOnlyList<string> OptionalToolReferences { get; }

    public IReadOnlyList<string> ContextProviderReferences { get; }

    public IReadOnlyList<SkillResource> Resources { get; }

    public JsonElement CapabilityRequirements { get; }

    public JsonElement ActivationPolicy { get; }

    public string Trust { get; }

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; }

    private string ComputeDigest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "skill");
        digest.Add("skillId", SkillId);
        digest.Add("version", Version);
        digest.Add("declaredDigest", DeclaredDigest);
        digest.Add("description", Description);
        digest.Add("promptFragments", PromptFragments);
        digest.Add("requiredTools", RequiredToolReferences);
        digest.Add("optionalTools", OptionalToolReferences);
        digest.Add("contextProviders", ContextProviderReferences);
        foreach (var resource in Resources)
        {
            digest.Add("resourceUri", resource.Uri);
            digest.Add("resourceMediaType", resource.MediaType);
            digest.Add("resourceDigest", resource.Digest);
            digest.Add("resourceSize", resource.SizeBytes ?? -1);
        }

        digest.Add("capabilities", CapabilityRequirements);
        digest.Add("activation", ActivationPolicy);
        digest.Add("trust", Trust);
        foreach (var extension in Extensions)
        {
            digest.Add($"extension:{extension.Key}", extension.Value);
        }

        return digest.Finish();
    }
}

public sealed class SkillReference
{
    public SkillReference(string skillId, string version)
    {
        SkillId = RuntimeGuard.RequiredId(skillId, nameof(skillId));
        Version = RuntimeGuard.RequiredUtf8(version, 32, nameof(version));
    }

    public string SkillId { get; }

    public string Version { get; }

    public string Value => $"{SkillId}@{Version}";
}

public sealed class SkillCatalogSummary
{
    internal SkillCatalogSummary(SkillCatalogEntry entry)
    {
        SkillId = entry.SkillId;
        Version = entry.Version;
        Digest = entry.ContentDigest;
        Description = entry.Description;
    }

    public string SkillId { get; }

    public string Version { get; }

    public string Digest { get; }

    public string Description { get; }

    public string Reference => $"{SkillId}@{Version}";
}

public sealed class SkillDisclosureBudget
{
    public SkillDisclosureBudget(
        int maxCatalogItems = 64,
        int maxCatalogUtf8Bytes = 16_384,
        int maxActivatedSkills = 16,
        int maxPromptFragments = 128,
        int maxPromptUtf8Bytes = 65_536,
        int maxReferences = 512)
    {
        if (maxCatalogItems < 0
            || maxCatalogUtf8Bytes < 0
            || maxActivatedSkills < 0
            || maxPromptFragments < 0
            || maxPromptUtf8Bytes < 0
            || maxReferences < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCatalogItems),
                "Skill disclosure limits cannot be negative.");
        }

        MaxCatalogItems = maxCatalogItems;
        MaxCatalogUtf8Bytes = maxCatalogUtf8Bytes;
        MaxActivatedSkills = maxActivatedSkills;
        MaxPromptFragments = maxPromptFragments;
        MaxPromptUtf8Bytes = maxPromptUtf8Bytes;
        MaxReferences = maxReferences;
    }

    public int MaxCatalogItems { get; }

    public int MaxCatalogUtf8Bytes { get; }

    public int MaxActivatedSkills { get; }

    public int MaxPromptFragments { get; }

    public int MaxPromptUtf8Bytes { get; }

    public int MaxReferences { get; }
}

public sealed class SkillDisclosurePlan
{
    internal SkillDisclosurePlan(
        IReadOnlyList<SkillCatalogSummary> catalog,
        IReadOnlyList<SkillCatalogEntry> activated,
        IReadOnlyList<string> deferredReferences,
        int estimatedUtf8Bytes)
    {
        Catalog = catalog;
        Activated = activated;
        DeferredReferences = deferredReferences;
        EstimatedUtf8Bytes = estimatedUtf8Bytes;
    }

    public IReadOnlyList<SkillCatalogSummary> Catalog { get; }

    public IReadOnlyList<SkillCatalogEntry> Activated { get; }

    public IReadOnlyList<string> DeferredReferences { get; }

    public int EstimatedUtf8Bytes { get; }
}

public sealed class SkillCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, SkillCatalogEntry> _byReference;

    internal SkillCatalogSnapshot(long generation, IReadOnlyList<SkillCatalogEntry> skills)
    {
        Generation = generation;
        Skills = skills;
        _byReference = new ReadOnlyDictionary<string, SkillCatalogEntry>(
            skills.ToDictionary(item => item.Reference, StringComparer.Ordinal));
        Digest = ComputeDigest(skills);
    }

    public long Generation { get; }

    public string Digest { get; }

    public IReadOnlyList<SkillCatalogEntry> Skills { get; }

    public bool TryGet(string skillId, string version, out SkillCatalogEntry? skill)
    {
        if (skillId is null || version is null)
        {
            skill = null;
            return false;
        }

        return _byReference.TryGetValue($"{skillId}@{version}", out skill);
    }

    public SkillDisclosurePlan CreateDisclosure(
        IEnumerable<SkillReference> activatedSkills,
        SkillDisclosureBudget? budget = null)
    {
        return CreateDisclosure(
            activatedSkills,
            budget,
            catalogReferences: null);
    }

    internal SkillDisclosurePlan CreateDisclosure(
        IEnumerable<SkillReference> activatedSkills,
        SkillDisclosureBudget? budget,
        IReadOnlyCollection<string>? catalogReferences)
    {
        if (activatedSkills is null)
        {
            throw new ArgumentNullException(nameof(activatedSkills));
        }

        var effectiveBudget = budget ?? new SkillDisclosureBudget();
        var active = ResolveActivated(activatedSkills, effectiveBudget);
        ValidateActivatedBudget(active, effectiveBudget);
        var admittedCatalog = catalogReferences is null
            ? null
            : new HashSet<string>(
                catalogReferences,
                StringComparer.Ordinal);

        var catalog = new List<SkillCatalogSummary>();
        var deferred = new List<string>();
        var bytes = EstimateActivatedBytes(active);
        var catalogBytes = 0;
        var activeReferences = new HashSet<string>(
            active.Select(item => item.Reference),
            StringComparer.Ordinal);
        foreach (var skill in Skills)
        {
            if (admittedCatalog is not null
                && !admittedCatalog.Contains(skill.Reference))
            {
                continue;
            }

            var summary = new SkillCatalogSummary(skill);
            var summaryBytes = EstimateSummaryBytes(summary);
            if (catalog.Count < effectiveBudget.MaxCatalogItems
                && checked(catalogBytes + summaryBytes) <= effectiveBudget.MaxCatalogUtf8Bytes)
            {
                catalog.Add(summary);
                catalogBytes += summaryBytes;
                bytes = checked(bytes + summaryBytes);
            }
            else if (!activeReferences.Contains(skill.Reference))
            {
                deferred.Add(skill.Reference);
            }
        }

        return new SkillDisclosurePlan(
            new ReadOnlyCollection<SkillCatalogSummary>(catalog),
            active,
            new ReadOnlyCollection<string>(deferred),
            bytes);
    }

    private IReadOnlyList<SkillCatalogEntry> ResolveActivated(
        IEnumerable<SkillReference> references,
        SkillDisclosureBudget budget)
    {
        var active = new List<SkillCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (reference is null)
            {
                throw new ArgumentException("An activated skill reference cannot be null.", nameof(references));
            }

            if (!seen.Add(reference.Value))
            {
                throw new ArgumentException(
                    $"Activated skill '{reference.Value}' appears more than once.",
                    nameof(references));
            }

            if (active.Count >= budget.MaxActivatedSkills)
            {
                throw new RuntimeContentLimitException(
                    nameof(references),
                    "activated_skill_count_exceeded",
                    $"Activated skills exceed {budget.MaxActivatedSkills}.");
            }

            if (!_byReference.TryGetValue(reference.Value, out var skill))
            {
                throw new KeyNotFoundException($"Skill '{reference.Value}' is not in this snapshot.");
            }

            active.Add(skill);
        }

        return new ReadOnlyCollection<SkillCatalogEntry>(active);
    }

    private static void ValidateActivatedBudget(
        IEnumerable<SkillCatalogEntry> active,
        SkillDisclosureBudget budget)
    {
        var fragments = 0;
        var promptBytes = 0;
        var references = 0;
        foreach (var skill in active)
        {
            fragments = checked(fragments + skill.PromptFragments.Count);
            foreach (var fragment in skill.PromptFragments)
            {
                promptBytes = checked(promptBytes + Encoding.UTF8.GetByteCount(fragment));
            }

            references = checked(
                references
                + skill.RequiredToolReferences.Count
                + skill.OptionalToolReferences.Count
                + skill.ContextProviderReferences.Count
                + skill.Resources.Count);
        }

        if (fragments > budget.MaxPromptFragments)
        {
            throw new RuntimeContentLimitException(
                nameof(active),
                "skill_prompt_fragment_count_exceeded",
                $"Activated skill prompt fragments exceed {budget.MaxPromptFragments}.");
        }

        if (promptBytes > budget.MaxPromptUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                nameof(active),
                "skill_prompt_bytes_exceeded",
                $"Activated skill prompt text exceeds {budget.MaxPromptUtf8Bytes} UTF-8 bytes.");
        }

        if (references > budget.MaxReferences)
        {
            throw new RuntimeContentLimitException(
                nameof(active),
                "skill_reference_count_exceeded",
                $"Activated skill references exceed {budget.MaxReferences}.");
        }
    }

    private static int EstimateActivatedBytes(IEnumerable<SkillCatalogEntry> active)
    {
        var bytes = 0;
        foreach (var skill in active)
        {
            bytes = checked(
                bytes
                + 128
                + Encoding.UTF8.GetByteCount(skill.SkillId)
                + Encoding.UTF8.GetByteCount(skill.Version)
                + Encoding.UTF8.GetByteCount(skill.DeclaredDigest)
                + Encoding.UTF8.GetByteCount(skill.ContentDigest)
                + Encoding.UTF8.GetByteCount(skill.Description));
            foreach (var fragment in skill.PromptFragments)
            {
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(fragment) + 4);
            }

            foreach (var reference in skill.RequiredToolReferences
                         .Concat(skill.OptionalToolReferences)
                         .Concat(skill.ContextProviderReferences))
            {
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(reference) + 4);
            }

            foreach (var resource in skill.Resources)
            {
                bytes = checked(
                    bytes
                    + 48
                    + Encoding.UTF8.GetByteCount(resource.Uri)
                    + Encoding.UTF8.GetByteCount(resource.MediaType)
                    + (resource.Digest is null
                        ? 0
                        : Encoding.UTF8.GetByteCount(resource.Digest)));
            }

            bytes = checked(
                bytes
                + Encoding.UTF8.GetByteCount(skill.CapabilityRequirements.GetRawText())
                + Encoding.UTF8.GetByteCount(skill.ActivationPolicy.GetRawText())
                + Encoding.UTF8.GetByteCount(skill.Trust));
            foreach (var extension in skill.Extensions)
            {
                bytes = checked(
                    bytes
                    + Encoding.UTF8.GetByteCount(extension.Key)
                    + Encoding.UTF8.GetByteCount(extension.Value.GetRawText())
                    + 4);
            }
        }

        return bytes;
    }

    private static int EstimateSummaryBytes(SkillCatalogSummary summary)
    {
        return checked(
            48
            + Encoding.UTF8.GetByteCount(summary.SkillId)
            + Encoding.UTF8.GetByteCount(summary.Version)
            + Encoding.UTF8.GetByteCount(summary.Digest)
            + Encoding.UTF8.GetByteCount(summary.Description));
    }

    private static string ComputeDigest(IEnumerable<SkillCatalogEntry> skills)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "skill_catalog");
        foreach (var skill in skills)
        {
            digest.Add("skillId", skill.SkillId);
            digest.Add("skillVersion", skill.Version);
            digest.Add("skillDigest", skill.ContentDigest);
        }

        return digest.Finish();
    }
}

public sealed class SkillCatalogRegistry
{
    private readonly object _sync = new();
    private readonly RegistryLimits _limits;
    private SkillCatalogSnapshot _current;

    public SkillCatalogRegistry(RegistryLimits? limits = null)
    {
        _limits = limits ?? new RegistryLimits();
        _current = new SkillCatalogSnapshot(0, Array.Empty<SkillCatalogEntry>());
    }

    public SkillCatalogSnapshot Current => Volatile.Read(ref _current);

    public SkillCatalogSnapshot Replace(IEnumerable<SkillManifest> manifests)
    {
        if (manifests is null)
        {
            throw new ArgumentNullException(nameof(manifests));
        }

        var entries = Materialize(manifests);
        lock (_sync)
        {
            var current = _current;
            var candidate = new SkillCatalogSnapshot(checked(current.Generation + 1), entries);
            if (string.Equals(candidate.Digest, current.Digest, StringComparison.Ordinal))
            {
                return current;
            }

            Volatile.Write(ref _current, candidate);
            return candidate;
        }
    }

    private IReadOnlyList<SkillCatalogEntry> Materialize(IEnumerable<SkillManifest> manifests)
    {
        var entries = new List<SkillCatalogEntry>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            if (manifest is null)
            {
                throw new ArgumentException("A skill manifest cannot be null.", nameof(manifests));
            }

            if (entries.Count >= _limits.MaxSkills)
            {
                throw new RuntimeContentLimitException(
                    nameof(manifests),
                    "skill_count_exceeded",
                    $"Skill count exceeds {_limits.MaxSkills}.");
            }

            var entry = new SkillCatalogEntry(manifest, _limits);
            if (!references.Add(entry.Reference))
            {
                throw new ArgumentException(
                    $"Skill '{entry.Reference}' is registered more than once in one snapshot.",
                    nameof(manifests));
            }

            entries.Add(entry);
        }

        entries.Sort((left, right) =>
        {
            var byId = StringComparer.Ordinal.Compare(left.SkillId, right.SkillId);
            return byId != 0
                ? byId
                : StringComparer.Ordinal.Compare(left.Version, right.Version);
        });
        return new ReadOnlyCollection<SkillCatalogEntry>(entries);
    }
}
