using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class SkillAdmissionPurposes
{
    public const string Catalog = "catalog";
    public const string Activation = "activation";
}

public static class SkillAdmissionReasonCodes
{
    public const string Allowed = "skill_admitted";
    public const string Untrusted = "skill_trust_untrusted";
    public const string CapabilityRequirementsUnsupported =
        "skill_capability_requirements_unsupported";
    public const string ActivationPolicyUnsupported =
        "skill_activation_policy_unsupported";
    public const string RequiredToolReferenceInvalid =
        "skill_required_tool_ref_invalid";
    public const string RequiredToolMissing = "skill_required_tool_missing";
    public const string RequiredToolVersionMismatch =
        "skill_required_tool_version_mismatch";
    public const string RequiredToolNotDisclosable =
        "skill_required_tool_not_disclosable";
    public const string RequiredToolDisclosureDenied =
        "skill_required_tool_disclosure_denied";
    public const string RequiredToolDescriptorMismatch =
        "skill_required_tool_descriptor_mismatch";
    public const string RequiredToolDisclosureCapacityExceeded =
        "skill_required_tool_disclosure_capacity_exceeded";
    public const string CatalogEntryChanged =
        SkillRuntimeReasonCodes.CatalogEntryChanged;
    public const string PolicyError = "skill_admission_policy_error";
    public const string PolicyDecisionInvalid =
        "skill_admission_policy_decision_invalid";
}

public sealed class SkillAdmissionRequest
{
    internal SkillAdmissionRequest(
        AgentRun run,
        string turnId,
        SkillCatalogEntry skill,
        ToolCatalogSnapshot tools,
        string purpose)
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
        Skill = skill ?? throw new ArgumentNullException(nameof(skill));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Purpose = purpose is SkillAdmissionPurposes.Catalog
            or SkillAdmissionPurposes.Activation
            ? purpose
            : throw new ArgumentOutOfRangeException(nameof(purpose));
    }

    public string RunId { get; }

    public string AgentId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long RuntimeGeneration { get; }

    public string TurnId { get; }

    public SkillCatalogEntry Skill { get; }

    public ToolCatalogSnapshot Tools { get; }

    public string Purpose { get; }

    public bool IsExplicitActivation =>
        string.Equals(
            Purpose,
            SkillAdmissionPurposes.Activation,
            StringComparison.Ordinal);
}

public sealed class SkillAdmissionDecision
{
    private SkillAdmissionDecision(bool allowed, string reasonCode)
    {
        Allowed = allowed;
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
    }

    public bool Allowed { get; }

    public string ReasonCode { get; }

    public static SkillAdmissionDecision Allow(
        string reasonCode = SkillAdmissionReasonCodes.Allowed) =>
        new(true, reasonCode);

    public static SkillAdmissionDecision Deny(string reasonCode) =>
        new(false, reasonCode);
}

public interface ISkillAdmissionPolicy
{
    string PolicyId { get; }

    string Version { get; }

    SkillAdmissionDecision Evaluate(SkillAdmissionRequest request);
}

public sealed class DefaultSkillAdmissionPolicy : ISkillAdmissionPolicy
{
    public static DefaultSkillAdmissionPolicy Instance { get; } = new();

    private DefaultSkillAdmissionPolicy()
    {
    }

    public string PolicyId => "default-skill-admission";

    public string Version => "1.0.0";

    public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.Equals(
                request.Skill.Trust,
                "untrusted",
                StringComparison.Ordinal))
        {
            return SkillAdmissionDecision.Deny(
                SkillAdmissionReasonCodes.Untrusted);
        }

        if (HasProperties(request.Skill.CapabilityRequirements))
        {
            return SkillAdmissionDecision.Deny(
                SkillAdmissionReasonCodes.CapabilityRequirementsUnsupported);
        }

        if (HasProperties(request.Skill.ActivationPolicy))
        {
            return SkillAdmissionDecision.Deny(
                SkillAdmissionReasonCodes.ActivationPolicyUnsupported);
        }

        return SkillAdmissionDecision.Allow();
    }

    private static bool HasProperties(JsonElement value)
    {
        using var enumerator = value.EnumerateObject();
        return enumerator.MoveNext();
    }
}

public sealed class SkillAdmissionException : InvalidOperationException
{
    public SkillAdmissionException(string reasonCode)
        : base("The requested skill was not admitted for this turn.")
    {
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
    }

    public string ReasonCode { get; }
}

internal sealed class SkillAdmissionPlan
{
    public SkillAdmissionPlan(
        string policyId,
        string policyVersion,
        IReadOnlyList<SkillAdmissionRecord> admitted,
        IReadOnlyList<SkillAdmissionRecord> catalogAdmissions)
    {
        PolicyId = policyId;
        PolicyVersion = policyVersion;
        Admitted = admitted;
        CatalogAdmissions = catalogAdmissions;
        CatalogReferences = new ReadOnlyCollection<string>(
            catalogAdmissions
                .Select(item => item.Reference)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList());
        DisclosureDigest = ComputeDisclosureDigest();
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public IReadOnlyList<SkillAdmissionRecord> Admitted { get; }

    public IReadOnlyCollection<string> CatalogReferences { get; }

    private IReadOnlyList<SkillAdmissionRecord> CatalogAdmissions { get; }

    public string DisclosureDigest { get; }

    public JsonElement ToSnapshotExtension()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("policyId", PolicyId);
            writer.WriteString("policyVersion", PolicyVersion);
            writer.WriteString("admissionDigest", DisclosureDigest);
            writer.WritePropertyName("decisions");
            writer.WriteStartArray();
            foreach (var item in Admitted)
            {
                writer.WriteStartObject();
                writer.WriteString("policyId", PolicyId);
                writer.WriteString("policyVersion", PolicyVersion);
                writer.WriteString("skillId", item.SkillId);
                writer.WriteString("skillVersion", item.SkillVersion);
                writer.WriteString("skillDigest", item.SkillDigest);
                writer.WriteString("reasonCode", item.ReasonCode);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private string ComputeDisclosureDigest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "skill_admission");
        digest.Add("policyId", PolicyId);
        digest.Add("policyVersion", PolicyVersion);
        foreach (var item in CatalogAdmissions.OrderBy(
                     item => item.Reference,
                     StringComparer.Ordinal))
        {
            digest.Add("catalogSkillId", item.SkillId);
            digest.Add("catalogSkillVersion", item.SkillVersion);
            digest.Add("catalogSkillDigest", item.SkillDigest);
            digest.Add("catalogReasonCode", item.ReasonCode);
        }

        foreach (var item in Admitted)
        {
            digest.Add("skillId", item.SkillId);
            digest.Add("skillVersion", item.SkillVersion);
            digest.Add("skillDigest", item.SkillDigest);
            digest.Add("reasonCode", item.ReasonCode);
        }

        return digest.Finish();
    }
}

internal sealed class SkillAdmissionRecord
{
    public SkillAdmissionRecord(
        SkillCatalogEntry skill,
        SkillAdmissionDecision decision)
    {
        SkillId = skill.SkillId;
        SkillVersion = skill.Version;
        SkillDigest = skill.ContentDigest;
        ReasonCode = decision.ReasonCode;
    }

    public string SkillId { get; }

    public string SkillVersion { get; }

    public string SkillDigest { get; }

    public string ReasonCode { get; }

    public string Reference => $"{SkillId}@{SkillVersion}";
}

internal sealed class SkillAdmissionEvaluator
{
    private readonly ISkillAdmissionPolicy _policy;
    private readonly string _policyId;
    private readonly string _policyVersion;

    public SkillAdmissionEvaluator(ISkillAdmissionPolicy? policy)
    {
        _policy = policy ?? DefaultSkillAdmissionPolicy.Instance;
        _policyId = RuntimeGuard.RequiredId(
            _policy.PolicyId,
            nameof(ISkillAdmissionPolicy.PolicyId));
        _policyVersion = RuntimeGuard.RequiredUtf8(
            _policy.Version,
            32,
            nameof(ISkillAdmissionPolicy.Version));
    }

    public SkillAdmissionPlan Evaluate(
        AgentRun run,
        string turnId,
        SkillCatalogSnapshot skills,
        ToolCatalogSnapshot tools,
        ToolDisclosurePlan toolDisclosure,
        IReadOnlyList<SkillReference> explicitlyActivated,
        SkillDisclosureBudget budget)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        RuntimeGuard.RequiredId(turnId, nameof(turnId));
        if (skills is null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        if (toolDisclosure is null)
        {
            throw new ArgumentNullException(nameof(toolDisclosure));
        }

        if (explicitlyActivated is null)
        {
            throw new ArgumentNullException(nameof(explicitlyActivated));
        }

        if (budget is null)
        {
            throw new ArgumentNullException(nameof(budget));
        }

        var admitted = new List<SkillAdmissionRecord>();
        var catalogAdmissions = new List<SkillAdmissionRecord>();
        var activeReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in explicitlyActivated)
        {
            if (reference is null)
            {
                throw new ArgumentException(
                    "An activated skill reference cannot be null.",
                    nameof(explicitlyActivated));
            }

            if (!activeReferences.Add(reference.Value))
            {
                throw new ArgumentException(
                    $"Activated skill '{reference.Value}' appears more than once.",
                    nameof(explicitlyActivated));
            }

            if (admitted.Count >= budget.MaxActivatedSkills)
            {
                throw new RuntimeContentLimitException(
                    nameof(explicitlyActivated),
                    "activated_skill_count_exceeded",
                    $"Activated skills exceed {budget.MaxActivatedSkills}.");
            }

            if (!skills.TryGet(
                    reference.SkillId,
                    reference.Version,
                    out var skill)
                || skill is null)
            {
                throw new KeyNotFoundException(
                    $"Skill '{reference.Value}' is not in this snapshot.");
            }

            var decision = EvaluateOne(
                run,
                turnId,
                skill,
                tools,
                toolDisclosure,
                SkillAdmissionPurposes.Activation);
            if (!decision.Allowed)
            {
                throw new SkillAdmissionException(decision.ReasonCode);
            }

            var record = new SkillAdmissionRecord(skill, decision);
            admitted.Add(record);
            catalogAdmissions.Add(record);
        }

        foreach (var skill in skills.Skills)
        {
            if (activeReferences.Contains(skill.Reference))
            {
                continue;
            }

            var decision = EvaluateOne(
                run,
                turnId,
                skill,
                tools,
                toolDisclosure,
                SkillAdmissionPurposes.Catalog);
            if (decision.Allowed)
            {
                catalogAdmissions.Add(
                    new SkillAdmissionRecord(skill, decision));
            }
        }

        return new SkillAdmissionPlan(
            _policyId,
            _policyVersion,
            new ReadOnlyCollection<SkillAdmissionRecord>(admitted),
            new ReadOnlyCollection<SkillAdmissionRecord>(
                catalogAdmissions));
    }

    private SkillAdmissionDecision EvaluateOne(
        AgentRun run,
        string turnId,
        SkillCatalogEntry skill,
        ToolCatalogSnapshot tools,
        ToolDisclosurePlan toolDisclosure,
        string purpose)
    {
        var requiredToolDecision = ResolveRequiredTools(
            skill,
            tools,
            out var requiredTools);
        if (requiredToolDecision is not null)
        {
            return requiredToolDecision;
        }

        var disclosureReason = toolDisclosure.ValidateRequiredTools(
            requiredTools,
            skill.Reference,
            activate: false);
        if (!string.Equals(
                disclosureReason,
                ToolDisclosureReasonCodes.Allowed,
                StringComparison.Ordinal))
        {
            return RequiredToolDisclosureDecision(disclosureReason);
        }

        SkillAdmissionDecision decision;
        try
        {
            decision = _policy.Evaluate(
                           new SkillAdmissionRequest(
                               run,
                               turnId,
                               skill,
                               tools,
                               purpose))
                       ?? SkillAdmissionDecision.Deny(
                           SkillAdmissionReasonCodes.PolicyDecisionInvalid);
        }
        catch
        {
            return SkillAdmissionDecision.Deny(
                SkillAdmissionReasonCodes.PolicyError);
        }

        if (!decision.Allowed
            || !string.Equals(
                purpose,
                SkillAdmissionPurposes.Activation,
                StringComparison.Ordinal))
        {
            return decision;
        }

        disclosureReason = toolDisclosure.ValidateRequiredTools(
            requiredTools,
            skill.Reference,
            activate: true);
        return string.Equals(
            disclosureReason,
            ToolDisclosureReasonCodes.Allowed,
            StringComparison.Ordinal)
            ? decision
            : RequiredToolDisclosureDecision(disclosureReason);
    }

    private static SkillAdmissionDecision? ResolveRequiredTools(
        SkillCatalogEntry skill,
        ToolCatalogSnapshot tools,
        out IReadOnlyList<ToolCatalogEntry> requiredTools)
    {
        var resolved = new List<ToolCatalogEntry>(
            skill.RequiredToolReferences.Count);
        foreach (var reference in skill.RequiredToolReferences)
        {
            var separator = reference.LastIndexOf('@');
            if (separator <= 0 || separator == reference.Length - 1)
            {
                requiredTools = Array.Empty<ToolCatalogEntry>();
                return SkillAdmissionDecision.Deny(
                    SkillAdmissionReasonCodes.RequiredToolReferenceInvalid);
            }

            var name = reference[..separator];
            var version = reference[(separator + 1)..];
            if (!tools.TryGet(name, out var tool) || tool is null)
            {
                requiredTools = Array.Empty<ToolCatalogEntry>();
                return SkillAdmissionDecision.Deny(
                    SkillAdmissionReasonCodes.RequiredToolMissing);
            }

            if (!string.Equals(
                    tool.Version,
                    version,
                    StringComparison.Ordinal))
            {
                requiredTools = Array.Empty<ToolCatalogEntry>();
                return SkillAdmissionDecision.Deny(
                    SkillAdmissionReasonCodes.RequiredToolVersionMismatch);
            }

            resolved.Add(tool);
        }

        requiredTools = new ReadOnlyCollection<ToolCatalogEntry>(resolved);
        return null;
    }

    private static SkillAdmissionDecision RequiredToolDisclosureDecision(
        string disclosureReason)
    {
        var reason = disclosureReason switch
        {
            ToolDisclosureReasonCodes.NotDeferred =>
                SkillAdmissionReasonCodes.RequiredToolNotDisclosable,
            ToolDisclosureReasonCodes.NotAuthorized =>
                SkillAdmissionReasonCodes.RequiredToolDisclosureDenied,
            ToolDisclosureReasonCodes.ExactIdentityMismatch =>
                SkillAdmissionReasonCodes.RequiredToolDescriptorMismatch,
            ToolDisclosureReasonCodes.CapacityExceeded =>
                SkillAdmissionReasonCodes
                    .RequiredToolDisclosureCapacityExceeded,
            _ => SkillAdmissionReasonCodes.RequiredToolDisclosureDenied
        };
        return SkillAdmissionDecision.Deny(reason);
    }
}
