using GameAgent.Core;
using GameAgent.Simulation;

namespace GameAgent.Simulation.Tests;

public sealed class LivingWorldPolicyTests
{
    [Fact]
    public void ForegroundRunsBeforeBackgroundAndUsesInteractiveAdmission()
    {
        var policy = Policy(maxActors: 1);
        var plan = policy.Plan(Cycle(), new[]
        {
            Actor("background", pendingTriggers: 2, salience: 0.3),
            Actor("foreground", direct: true)
        });

        var selected = Assert.Single(plan.Runnable);
        Assert.Equal("foreground", selected.ActorId);
        Assert.Equal(ProviderWorkloadClasses.Interactive, selected.WorkloadClass);
        Assert.Equal(LivingWorldDecisionKinds.Coalesce,
            Assert.Single(plan.Decisions, item => item.ActorId == "background").Decision);
    }

    [Fact]
    public void DormantActorsAggregateGameTimeSignalsWithoutModelWork()
    {
        var policy = Policy(maxActors: 8);
        var decision = Assert.Single(policy.Plan(Cycle(), new[]
        {
            Actor("dormant", pendingTriggers: 12, lastTick: 0)
        }).Decisions);

        Assert.Equal(LivingWorldActivationTiers.Dormant, decision.ActivationTier);
        Assert.Equal(LivingWorldDecisionKinds.Aggregate, decision.Decision);
        Assert.Equal(12, decision.CoalescedSignalCount);
    }

    [Fact]
    public void StarvedBackgroundActorIsPromotedDeterministically()
    {
        var policy = Policy(maxActors: 1);
        var decision = Assert.Single(policy.Plan(Cycle(gameTick: 5_000), new[]
        {
            Actor("starved", pendingTriggers: 1, salience: 0.3, lastTick: 0)
        }).Runnable);

        Assert.Equal("starvation_promoted", decision.ReasonCode);
        Assert.Equal(ProviderWorkloadClasses.Background, decision.WorkloadClass);
    }

    [Fact]
    public void InputOrderDoesNotChangeSelection()
    {
        var actors = Enumerable.Range(0, 100)
            .Select(index => Actor($"actor-{index:D3}", pendingTriggers: 1, salience: index / 100d))
            .ToArray();
        var policy = Policy(maxActors: 10);
        var forward = policy.Plan(Cycle(), actors).Runnable.Select(static value => value.ActorId);
        var reverse = policy.Plan(Cycle(), actors.Reverse().ToArray()).Runnable.Select(static value => value.ActorId);

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void RunnableOrderPreservesTierAndPriorityAdmission()
    {
        var policy = Policy(maxActors: 4);
        var runnable = policy.Plan(Cycle(), new[]
        {
            Actor("nearby", pendingTriggers: 1, salience: 0.9),
            Actor("foreground-low", direct: true, salience: 0.1),
            Actor("foreground-high", direct: true, salience: 0.8),
            Actor("background", pendingTriggers: 1, salience: 0.3, lastTick: 9_999)
        }).Runnable;

        Assert.Equal(
            new[] { "foreground-high", "foreground-low", "nearby", "background" },
            runnable.Select(static item => item.ActorId));
        Assert.Equal(new int?[] { 0, 1, 2, 3 }, runnable.Select(static item => item.AdmissionOrder));
    }

    [Fact]
    public void ExcessivePendingSignalsAreRejectedBeforeAggregation()
    {
        var actor = Actor("actor", pendingTriggers: 1_000_001);
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(1).Plan(Cycle(), new[] { actor }));
    }

    [Fact]
    public void LargePopulationStaysInsideRunTokenAndStepBudgets()
    {
        var policy = new LivingWorldPolicy(new LivingWorldPolicyOptions
        {
            MaxActorsPerCycle = 32,
            MaxForegroundActors = 16,
            MaxNearbyActors = 12,
            MaxBackgroundActors = 4,
            MaxEstimatedTokensPerCycle = 4_000,
            MaxEstimatedStepsPerCycle = 64
        });
        var actors = Enumerable.Range(0, 10_000)
            .Select(index => Actor(
                $"actor-{index:D5}",
                pendingTriggers: 1,
                salience: (index % 100) / 100d,
                tokens: 200,
                steps: 2))
            .ToArray();

        var plan = policy.Plan(Cycle(), actors);
        Assert.Equal(10_000, plan.Decisions.Count);
        Assert.True(plan.Runnable.Count <= 20);
        Assert.True(plan.Runnable.Sum(item => actors.Single(actor => actor.ActorId == item.ActorId).EstimatedTokens) <= 4_000);
        Assert.True(plan.Runnable.Sum(item => actors.Single(actor => actor.ActorId == item.ActorId).EstimatedSteps) <= 64);
    }

    private static LivingWorldPolicy Policy(int maxActors) => new(new LivingWorldPolicyOptions
    {
        MaxActorsPerCycle = maxActors,
        MaxForegroundActors = maxActors,
        MaxNearbyActors = maxActors,
        MaxBackgroundActors = maxActors,
        StarvationAfterGameTicks = 2_000,
        DormantAfterGameTicks = 4_000
    });

    private static LivingWorldCycle Cycle(long gameTick = 10_000) => new()
    {
        WorldId = "world",
        GameTick = gameTick
    };

    private static LivingWorldActorSignal Actor(
        string id,
        bool direct = false,
        int pendingTriggers = 0,
        double salience = 0,
        long lastTick = 9_999,
        int tokens = 1,
        int steps = 1) => new()
        {
            ActorId = id,
            HasDirectPlayerInput = direct,
            PendingTriggers = pendingTriggers,
            Salience = salience,
            LastEvaluatedGameTick = lastTick,
            EstimatedTokens = tokens,
            EstimatedSteps = steps
        };
}
