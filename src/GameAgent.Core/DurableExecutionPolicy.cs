using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Identifies the executable tool, skill, provider, and model policy captured
/// at one durable agent-loop admission boundary.
/// </summary>
public sealed class DurableExecutionPolicyIdentity
{
    public DurableExecutionPolicyIdentity(
        string toolCatalogDigest,
        string skillCatalogDigest,
        string providerPolicyDigest,
        string modelPolicyDigest)
    {
        ToolCatalogDigest = RequiredDigest(
            toolCatalogDigest,
            nameof(toolCatalogDigest));
        SkillCatalogDigest = RequiredDigest(
            skillCatalogDigest,
            nameof(skillCatalogDigest));
        ProviderPolicyDigest = RequiredDigest(
            providerPolicyDigest,
            nameof(providerPolicyDigest));
        ModelPolicyDigest = RequiredDigest(
            modelPolicyDigest,
            nameof(modelPolicyDigest));
    }

    public string ToolCatalogDigest { get; }

    public string SkillCatalogDigest { get; }

    public string ProviderPolicyDigest { get; }

    public string ModelPolicyDigest { get; }

    public bool Matches(DurableExecutionPolicyIdentity? other)
    {
        return other is not null
               && string.Equals(
                   ToolCatalogDigest,
                   other.ToolCatalogDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   SkillCatalogDigest,
                   other.SkillCatalogDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ProviderPolicyDigest,
                   other.ProviderPolicyDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ModelPolicyDigest,
                   other.ModelPolicyDigest,
                   StringComparison.Ordinal);
    }

    private static string RequiredDigest(
        string value,
        string parameterName)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "Execution-policy digests must be lowercase SHA-256 values.",
                parameterName);
        }

        return value;
    }
}

/// <summary>
/// Persists an optional caller expectation on an AgentRun. When present,
/// DurableAgentRuntime compares it with the exact execution-policy lease used
/// by the next RunAsync or ResumeAsync loop before provider or tool dispatch.
/// </summary>
public static class DurableExecutionPolicyBinding
{
    public const string ExtensionName = "gameAgent.executionPolicy";

    private const string Contract =
        "game-agent.durable-execution-policy.v1";

    public static void Attach(
        AgentRun run,
        DurableExecutionPolicyIdentity identity)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        run.Extensions[ExtensionName] = ToJson(identity);
    }

    public static JsonElement ToJson(
        DurableExecutionPolicyIdentity identity)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        return JsonArrayBuilder.Object(
            ("contract", JsonArrayBuilder.String(Contract)),
            ("toolCatalogDigest",
                JsonArrayBuilder.String(identity.ToolCatalogDigest)),
            ("skillCatalogDigest",
                JsonArrayBuilder.String(identity.SkillCatalogDigest)),
            ("providerPolicyDigest",
                JsonArrayBuilder.String(identity.ProviderPolicyDigest)),
            ("modelPolicyDigest",
                JsonArrayBuilder.String(identity.ModelPolicyDigest)));
    }

    public static DurableExecutionPolicyIdentity? Read(AgentRun run)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (!run.Extensions.TryGetValue(ExtensionName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 5
            || !TryReadString(value, "contract", out var contract)
            || !string.Equals(contract, Contract, StringComparison.Ordinal)
            || !TryReadString(
                value,
                "toolCatalogDigest",
                out var toolCatalogDigest)
            || !TryReadString(
                value,
                "skillCatalogDigest",
                out var skillCatalogDigest)
            || !TryReadString(
                value,
                "providerPolicyDigest",
                out var providerPolicyDigest)
            || !TryReadString(
                value,
                "modelPolicyDigest",
                out var modelPolicyDigest))
        {
            throw InvalidBinding();
        }

        try
        {
            return new DurableExecutionPolicyIdentity(
                toolCatalogDigest!,
                skillCatalogDigest!,
                providerPolicyDigest!,
                modelPolicyDigest!);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The durable execution-policy binding is invalid.",
                exception);
        }
    }

    private static bool TryReadString(
        JsonElement value,
        string propertyName,
        out string? result)
    {
        if (value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString();
            return result is not null;
        }

        result = null;
        return false;
    }

    private static InvalidDataException InvalidBinding()
    {
        return new InvalidDataException(
            "The durable execution-policy binding is invalid.");
    }
}

public sealed class DurableExecutionPolicyMismatchException :
    InvalidOperationException
{
    public const string ReasonCode = "runtime_execution_policy_mismatch";

    public DurableExecutionPolicyMismatchException()
        : base(
            "The executable runtime policy does not match the durable "
            + "run binding.")
    {
    }
}
