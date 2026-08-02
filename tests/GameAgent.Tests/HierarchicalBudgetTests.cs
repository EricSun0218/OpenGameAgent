using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class HierarchicalBudgetTests
{
    [Fact]
    public async Task Scope_definition_cannot_inject_unreceipted_usage()
    {
        var ledger = new HierarchicalBudgetLedger(new InMemoryHierarchicalBudgetStore());
        var scope = Scope("world", BudgetScopeKinds.World, null, maxOutputTokens: 100);
        scope.Used.OutputTokens = 10;

        var exception = await Assert.ThrowsAsync<HierarchicalBudgetException>(
            async () => await ledger.DefineScopeAsync(scope));

        Assert.Equal("hierarchical_budget_initial_usage_invalid", exception.ReasonCode);
    }

    [Fact]
    public async Task Sibling_children_share_root_tree_budget()
    {
        var ledger = new HierarchicalBudgetLedger(
            new InMemoryHierarchicalBudgetStore());
        await ledger.DefineScopeAsync(Scope(
            "tree-1",
            BudgetScopeKinds.AgentTree,
            null,
            maxOutputTokens: 100));
        await ledger.DefineScopeAsync(Scope(
            "child-a",
            BudgetScopeKinds.Run,
            "tree-1",
            maxOutputTokens: 100));
        await ledger.DefineScopeAsync(Scope(
            "child-b",
            BudgetScopeKinds.Run,
            "tree-1",
            maxOutputTokens: 100));

        await ledger.ChargeAsync(Charge("charge-a", "child-a", outputTokens: 60));
        var exception = await Assert.ThrowsAsync<HierarchicalBudgetException>(
            async () => await ledger.ChargeAsync(
                Charge("charge-b", "child-b", outputTokens: 50)));

        Assert.Equal("hierarchical_budget_exceeded", exception.ReasonCode);
        Assert.Equal("tree-1", exception.ScopeId);
    }

    [Fact]
    public async Task Replayed_charge_is_idempotent_across_scope_reads()
    {
        var store = new InMemoryHierarchicalBudgetStore();
        var ledger = new HierarchicalBudgetLedger(store);
        await ledger.DefineScopeAsync(Scope(
            "world",
            BudgetScopeKinds.World,
            null,
            maxOutputTokens: 1_000));
        await ledger.DefineScopeAsync(Scope(
            "run",
            BudgetScopeKinds.Run,
            "world",
            maxOutputTokens: 100));
        var charge = Charge("same", "run", outputTokens: 25);

        await ledger.ChargeAsync(charge);
        await ledger.ChargeAsync(charge);
        var state = await store.ReadAsync(default);

        Assert.Single(state.Charges);
        Assert.Equal(25, state.Scopes.Single(scope => scope.ScopeId == "run")
            .Used.OutputTokens);
        Assert.Equal(25, state.Scopes.Single(scope => scope.ScopeId == "world")
            .Used.OutputTokens);
    }

    [Fact]
    public async Task Media_side_effect_and_residency_dimensions_are_enforced()
    {
        var ledger = new HierarchicalBudgetLedger(
            new InMemoryHierarchicalBudgetStore());
        await ledger.DefineScopeAsync(new HierarchicalBudgetScope
        {
            ScopeId = "actor",
            Kind = BudgetScopeKinds.Actor,
            Limit = new HierarchicalBudgetLimit
            {
                MaxHostSideEffects = 1,
                MaxResidentMilliseconds = 100,
                MaxCostUsd = "1.5",
                MaxMediaCostUsd = "0.5"
            }
        });
        await ledger.ChargeAsync(new HierarchicalBudgetCharge
        {
            ChargeId = "media-1",
            ScopeId = "actor",
            Amount = new HierarchicalBudgetAmount
            {
                HostSideEffects = 1,
                ResidentMilliseconds = 100,
                CostUsd = "0.5",
                MediaCostUsd = "0.5"
            }
        });

        var exception = await Assert.ThrowsAsync<HierarchicalBudgetException>(
            async () => await ledger.ChargeAsync(new HierarchicalBudgetCharge
            {
                ChargeId = "media-2",
                ScopeId = "actor",
                Amount = new HierarchicalBudgetAmount
                {
                    HostSideEffects = 1,
                    CostUsd = "0.1",
                    MediaCostUsd = "0.1"
                }
            }));
        Assert.Equal("actor", exception.ScopeId);
    }

    [Fact]
    public async Task Concurrent_charges_cannot_overspend_through_cas_race()
    {
        var ledger = new HierarchicalBudgetLedger(
            new InMemoryHierarchicalBudgetStore());
        await ledger.DefineScopeAsync(Scope(
            "world",
            BudgetScopeKinds.World,
            null,
            maxOutputTokens: 100));
        var tasks = Enumerable.Range(0, 10)
            .Select(async index =>
            {
                try
                {
                    await ledger.ChargeAsync(
                        Charge("charge-" + index, "world", outputTokens: 20));
                    return true;
                }
                catch (HierarchicalBudgetException exception)
                    when (exception.ReasonCode == "hierarchical_budget_exceeded")
                {
                    return false;
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(5, results.Count(value => value));
    }

    private static HierarchicalBudgetScope Scope(
        string id,
        string kind,
        string? parent,
        long maxOutputTokens) => new()
        {
            ScopeId = id,
            Kind = kind,
            ParentScopeId = parent,
            Limit = new HierarchicalBudgetLimit
            {
                MaxOutputTokens = maxOutputTokens
            }
        };

    private static HierarchicalBudgetCharge Charge(
        string id,
        string scope,
        long outputTokens) => new()
        {
            ChargeId = id,
            ScopeId = scope,
            Amount = new HierarchicalBudgetAmount
            {
                OutputTokens = outputTokens
            }
        };
}
