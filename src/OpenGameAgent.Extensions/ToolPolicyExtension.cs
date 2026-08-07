using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public enum GameToolPolicyOutcome
{
    NotApplicable,
    Allow,
    Deny,
    Rewrite,
}

public sealed class GameToolPolicyDecision
{
    private GameToolPolicyDecision(
        GameToolPolicyOutcome outcome,
        string reason,
        string? replacementArgumentsJson)
    {
        if (!Enum.IsDefined(typeof(GameToolPolicyOutcome), outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (outcome == GameToolPolicyOutcome.Deny && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A denial requires a reason.", nameof(reason));
        }

        if ((reason?.Length ?? 0) > 65_536)
        {
            throw new ArgumentException("A policy reason cannot exceed 65536 characters.", nameof(reason));
        }

        if (outcome == GameToolPolicyOutcome.Rewrite && string.IsNullOrWhiteSpace(replacementArgumentsJson))
        {
            throw new ArgumentException("A rewrite requires replacement arguments.", nameof(replacementArgumentsJson));
        }

        if (replacementArgumentsJson is not null)
        {
            if (replacementArgumentsJson.Length > 1_000_000)
            {
                throw new ArgumentException("Replacement tool arguments cannot exceed 1000000 characters.", nameof(replacementArgumentsJson));
            }

            using var document = JsonDocument.Parse(replacementArgumentsJson, new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Replacement tool arguments must be a JSON object.", nameof(replacementArgumentsJson));
            }
        }

        Outcome = outcome;
        Reason = reason ?? string.Empty;
        ReplacementArgumentsJson = replacementArgumentsJson;
    }

    public GameToolPolicyOutcome Outcome { get; }

    public string Reason { get; }

    public string? ReplacementArgumentsJson { get; }

    public static GameToolPolicyDecision NotApplicable() =>
        new(GameToolPolicyOutcome.NotApplicable, string.Empty, null);

    public static GameToolPolicyDecision Allow(string? reason = null) =>
        new(GameToolPolicyOutcome.Allow, reason ?? string.Empty, null);

    public static GameToolPolicyDecision Deny(string reason) =>
        new(GameToolPolicyOutcome.Deny, reason, null);

    public static GameToolPolicyDecision Rewrite(string replacementArgumentsJson, string? reason = null) =>
        new(GameToolPolicyOutcome.Rewrite, reason ?? string.Empty, replacementArgumentsJson);
}

public sealed class GameToolPolicyContext
{
    public GameToolPolicyContext(GameInput input, ToolCallContent call, AgentContext agentContext)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Call = call ?? throw new ArgumentNullException(nameof(call));
        AgentContext = agentContext ?? throw new ArgumentNullException(nameof(agentContext));
    }

    public GameInput Input { get; }

    public ToolCallContent Call { get; }

    public AgentContext AgentContext { get; }
}

public interface IGameToolPolicy
{
    string Id { get; }

    ValueTask<GameToolPolicyDecision> EvaluateAsync(
        GameToolPolicyContext context,
        CancellationToken cancellationToken);
}

public sealed class GameToolPolicyAudit
{
    public GameToolPolicyAudit(
        string policyId,
        string toolName,
        GameToolPolicyOutcome outcome,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(policyId) || policyId.Length > 256)
        {
            throw new ArgumentException("A bounded policy ID is required.", nameof(policyId));
        }

        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 128)
        {
            throw new ArgumentException("A bounded tool name is required.", nameof(toolName));
        }

        if (!Enum.IsDefined(typeof(GameToolPolicyOutcome), outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        PolicyId = policyId;
        ToolName = toolName;
        Outcome = outcome;
        Reason = reason is null
            ? string.Empty
            : reason.Length <= 65_536 ? reason : reason.Substring(0, 65_536);
    }

    public string PolicyId { get; }

    public string ToolName { get; }

    public GameToolPolicyOutcome Outcome { get; }

    public string Reason { get; }
}

public sealed class ToolPolicyExtension : IGameAgentExtension
{
    private readonly IReadOnlyList<IGameToolPolicy> _policies;
    private readonly bool _denyWhenNoPolicyApplies;
    private readonly bool _failClosed;

    public ToolPolicyExtension(
        IEnumerable<IGameToolPolicy> policies,
        bool denyWhenNoPolicyApplies = false,
        bool failClosed = true)
    {
        var copied = (policies ?? throw new ArgumentNullException(nameof(policies))).ToArray();
        if (copied.Any(value => value is null || string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 256))
        {
            throw new ArgumentException("Policies require non-empty IDs.", nameof(policies));
        }

        var duplicate = copied.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate policy ID '{duplicate.Key}'.", nameof(policies));
        }

        _policies = new ReadOnlyCollection<IGameToolPolicy>(copied);
        _denyWhenNoPolicyApplies = denyWhenNoPolicyApplies;
        _failClosed = failClosed;
    }

    public static GameAgentExtensionChannel<GameToolPolicyAudit> DecisionRecorded { get; } =
        new("policy.decision");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.tool-policy",
        "1.0.0",
        "Composable tool-call policy gates with auditable fail-closed behavior.",
        new[] { "tool-policy", "audit" });

    public void Configure(GameAgentExtensionApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        api.RegisterAgentHooks(
            "tool-policy-gate",
            runContext => new AgentHooks
            {
                BeforeToolCallAsync = async (call, agentContext, cancellationToken) =>
                {
                    var current = call;
                    string? replacement = null;
                    var applied = false;
                    foreach (var policy in _policies)
                    {
                        GameToolPolicyDecision decision;
                        try
                        {
                            decision = await policy.EvaluateAsync(
                                new GameToolPolicyContext(runContext.Input, current, agentContext),
                                cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException($"Policy '{policy.Id}' returned null.");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            if (!_failClosed)
                            {
                                await api.PublishAsync(
                                    DecisionRecorded,
                                    new GameToolPolicyAudit(policy.Id, current.Name, GameToolPolicyOutcome.NotApplicable, exception.Message),
                                    cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            var reason = $"Policy '{policy.Id}' failed closed: {exception.Message}";
                            await api.PublishAsync(
                                DecisionRecorded,
                                new GameToolPolicyAudit(policy.Id, current.Name, GameToolPolicyOutcome.Deny, reason),
                                cancellationToken).ConfigureAwait(false);
                            return ToolCallDecision.Block(reason);
                        }

                        await api.PublishAsync(
                            DecisionRecorded,
                            new GameToolPolicyAudit(policy.Id, current.Name, decision.Outcome, decision.Reason),
                            cancellationToken).ConfigureAwait(false);
                        switch (decision.Outcome)
                        {
                            case GameToolPolicyOutcome.NotApplicable:
                                continue;
                            case GameToolPolicyOutcome.Allow:
                                applied = true;
                                continue;
                            case GameToolPolicyOutcome.Deny:
                                return ToolCallDecision.Block(decision.Reason);
                            case GameToolPolicyOutcome.Rewrite:
                                applied = true;
                                replacement = decision.ReplacementArgumentsJson;
                                current = new ToolCallContent(current.Id, current.Name, replacement!);
                                continue;
                            default:
                                throw new InvalidOperationException("Unsupported tool policy outcome.");
                        }
                    }

                    if (!applied && _denyWhenNoPolicyApplies)
                    {
                        const string reason = "No registered policy allowed this tool call.";
                        await api.PublishAsync(
                            DecisionRecorded,
                            new GameToolPolicyAudit("default", current.Name, GameToolPolicyOutcome.Deny, reason),
                            cancellationToken).ConfigureAwait(false);
                        return ToolCallDecision.Block(reason);
                    }

                    return replacement is null ? null : ToolCallDecision.Allow(replacement);
                },
            },
            priority: 1_000);
    }
}
