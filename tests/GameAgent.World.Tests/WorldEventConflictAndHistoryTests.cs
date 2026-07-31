namespace GameAgent.World.Tests;

public sealed class WorldEventConflictAndHistoryTests
{
    [Fact]
    public async Task ConflictBatchesSeparateWritesAndKeepReadsParallel()
    {
        var definitions = new[]
        {
            Definition("writer", priority: 30, writes: new[] { "shared" }),
            Definition("reader", priority: 20, reads: new[] { "shared" }),
            Definition("other-reader", priority: 10, reads: new[] { "other" })
        };
        var plan = await Planner().PlanAsync(
            new WorldEventPlanningRequest(Trigger(), definitions));

        Assert.Equal(2, plan.ExecutionBatches.Count);
        Assert.Equal(
            new[] { "writer", "other-reader" },
            plan.ExecutionBatches[0].Instances.Select(
                item => item.DefinitionId));
        Assert.Equal(
            new[] { "reader" },
            plan.ExecutionBatches[1].Instances.Select(
                item => item.DefinitionId));
        Assert.True(
            WorldEventConflictBatchPlanner.HasConflict(
                plan.Instances[0],
                plan.Instances[1]));
        Assert.False(
            WorldEventConflictBatchPlanner.HasConflict(
                plan.Instances[1],
                plan.Instances[2]));
    }

    [Fact]
    public async Task TransitiveConflictPreservesEarlierPrecedence()
    {
        var definitions = new[]
        {
            Definition(
                "a",
                priority: 30,
                writes: new[] { "one" }),
            Definition(
                "b",
                priority: 20,
                reads: new[] { "one" },
                writes: new[] { "two" }),
            Definition(
                "c",
                priority: 10,
                reads: new[] { "two" })
        };

        var plan = await Planner().PlanAsync(
            new WorldEventPlanningRequest(Trigger(), definitions));

        Assert.Equal(3, plan.ExecutionBatches.Count);
        Assert.Equal(
            new[] { "a", "b", "c" },
            plan.ExecutionBatches.Select(
                batch => Assert.Single(batch.Instances).DefinitionId));
    }

    [Fact]
    public async Task BatchSizeIsBoundedEvenWithoutConflicts()
    {
        var definitions = Enumerable.Range(0, 5)
            .Select(index => Definition(
                "event-" + index,
                priority: index,
                reads: new[] { "resource-" + index }))
            .ToArray();
        var planner = Planner(
            new WorldEventPlannerOptions(
                maxEventsPerExecutionBatch: 2));

        var plan = await planner.PlanAsync(
            new WorldEventPlanningRequest(Trigger(), definitions));

        Assert.Equal(new[] { 2, 2, 1 }, plan.ExecutionBatches.Select(
            batch => batch.Instances.Count));
    }

    [Fact]
    public async Task InMemoryHistoryAppendIsAtomicAndIdempotent()
    {
        var plan = await Planner().PlanAsync(
            new WorldEventPlanningRequest(
                Trigger(),
                new[] { Definition("event", priority: 1) }));
        var record = WorldEventHistoryRecord.FromInstance(
            Assert.Single(plan.Instances));
        var history = new InMemoryWorldEventHistory();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => history.TryAppendAsync(record, default).AsTask()));

        Assert.Equal(
            1,
            results.Count(
                result => result
                    == WorldEventHistoryAppendResult.Appended));
        Assert.Equal(
            15,
            results.Count(
                result => result
                    == WorldEventHistoryAppendResult.AlreadyExists));
        var state = await history.ReadDefinitionAsync(
            record.Definition,
            default);
        Assert.Equal(1, state.OccurrenceCount);
    }

    [Fact]
    public async Task HistoryRejectsSameIdentityWithDifferentFingerprint()
    {
        var plan = await Planner().PlanAsync(
            new WorldEventPlanningRequest(
                Trigger(),
                new[] { Definition("event", priority: 1) }));
        var instance = Assert.Single(plan.Instances);
        var original = WorldEventHistoryRecord.FromInstance(instance);
        var conflict = new WorldEventHistoryRecord(
            original.InstanceId,
            original.Definition,
            original.TriggerId,
            original.ResolutionKey,
            new string('f', 64),
            original.OccurredAt,
            original.ParentInstanceId);
        var history = new InMemoryWorldEventHistory();
        _ = await history.TryAppendAsync(original, default);

        var exception =
            await Assert.ThrowsAsync<WorldEventConfigurationException>(
                () => history.TryAppendAsync(conflict, default).AsTask());

        Assert.Equal(
            WorldEvolutionReasonCodes.InvalidHistory,
            exception.ReasonCode);
    }

    private static WorldEventPlanner Planner(
        WorldEventPlannerOptions? options = null)
    {
        var participants = new[]
        {
            new WorldEventParticipant("actor", 1, "participant")
        };
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector(
                "selector",
                new Selector(participants))
            .AddResolver("resolver", new Resolver())
            .AddEffect("effect", new Effect())
            .Build();
        return new WorldEventPlanner(
            handlers,
            new InMemoryWorldEventHistory(),
            options);
    }

    private static WorldEvolutionTrigger Trigger()
    {
        return new WorldEvolutionTrigger(
            "trigger",
            "period_advanced",
            "world",
            "timeline",
            1,
            new GameAgent.Core.GameTimePoint(
                "simulation",
                "timeline",
                1,
                12));
    }

    private static WorldEventDefinition Definition(
        string id,
        int priority,
        IEnumerable<string>? reads = null,
        IEnumerable<string>? writes = null)
    {
        return new WorldEventDefinition(
            id,
            "1",
            "period_advanced",
            priority,
            "condition",
            "selector",
            "resolver",
            "effect",
            reads,
            writes);
    }

    private sealed class Condition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class Selector : IWorldEventParticipantSelector
    {
        private readonly IReadOnlyList<WorldEventParticipant> _participants;

        public Selector(IReadOnlyList<WorldEventParticipant> participants)
        {
            _participants = participants;
        }

        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                _participants);
        }
    }

    private sealed class Resolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventResolution> result = new[]
            {
                new WorldEventResolution("candidate", selectedParticipants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(result);
        }
    }

    private sealed class Effect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "applied"));
        }
    }
}
