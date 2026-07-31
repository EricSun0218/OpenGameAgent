using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class BudgetDecision
{
    public bool Allowed { get; set; }

    public string? Reason { get; set; }
}

public sealed class BudgetTracker
{
    private readonly AgentBudget _budget;
    private readonly DateTimeOffset _startedAt;

    public BudgetTracker(AgentBudget budget, DateTimeOffset startedAt)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _startedAt = startedAt;
    }

    public BudgetDecision CanStartTurn(AgentUsage usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        if (usage.Turns < 0)
        {
            return Deny("invalid_usage");
        }

        if (usage.Turns >= _budget.MaxTurns)
        {
            return Deny("max_turns");
        }

        return CheckShared(usage, now);
    }

    public BudgetDecision CanDispatchAction(AgentUsage usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        if (usage.Actions < 0)
        {
            return Deny("invalid_usage");
        }

        if (usage.Actions >= _budget.MaxActions)
        {
            return Deny("max_actions");
        }

        return CheckShared(usage, now);
    }

    public BudgetDecision CheckShared(AgentUsage usage, DateTimeOffset now)
    {
        return CheckShared(usage, now, allowExactConsumption: false);
    }

    public BudgetDecision CheckAfterCharge(
        AgentUsage usage,
        DateTimeOffset now)
    {
        return CheckShared(usage, now, allowExactConsumption: true);
    }

    private BudgetDecision CheckShared(
        AgentUsage usage,
        DateTimeOffset now,
        bool allowExactConsumption)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        if (_budget.MaxTurns < 1
            || _budget.MaxDurationMs < 1
            || _budget.MaxTokens < 1
            || _budget.MaxActions < 0)
        {
            return Deny("invalid_budget");
        }

        if (usage.Turns < 0
            || usage.DurationMs < 0
            || usage.InputTokens < 0
            || usage.OutputTokens < 0
            || usage.Actions < 0)
        {
            return Deny("invalid_usage");
        }

        if ((now - _startedAt).TotalMilliseconds >= _budget.MaxDurationMs)
        {
            return Deny("max_duration");
        }

        if (!string.Equals(
                usage.Availability,
                UsageAvailabilityStates.CostAvailable,
                StringComparison.Ordinal))
        {
            return Deny("provider_cost_unavailable");
        }

        var usedTokens = (long)usage.InputTokens + usage.OutputTokens;
        if (allowExactConsumption
                ? usedTokens > _budget.MaxTokens
                : usedTokens >= _budget.MaxTokens)
        {
            return Deny("max_tokens");
        }

        if (!TryParseCost(usage.CostUsd, out var cost)
            || !TryParseCost(_budget.MaxCostUsd, out var maxCost))
        {
            return Deny("invalid_cost");
        }

        if (allowExactConsumption
                ? cost > maxCost
                : cost >= maxCost)
        {
            return Deny("max_cost");
        }

        return new BudgetDecision { Allowed = true };
    }

    private static BudgetDecision Deny(string reason)
    {
        return new BudgetDecision
        {
            Allowed = false,
            Reason = reason
        };
    }

    private static bool TryParseCost(string? value, out decimal cost)
    {
        cost = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character != '.'
                && character is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return decimal.TryParse(
                   value,
                   System.Globalization.NumberStyles.AllowDecimalPoint,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out cost)
               && cost >= 0;
    }
}
