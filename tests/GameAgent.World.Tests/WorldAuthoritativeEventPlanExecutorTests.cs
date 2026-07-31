using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldAuthoritativeEventPlanExecutorTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExecutesConflictBatchesInOrderWithFreshCoordinates()
    {
        var prepared = await PreparePlanAsync("effect-a", "effect-b");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var calls = new List<string>();
        var factoryRevisions = new List<long>();
        var registry = Registry(
            ("effect-a", factoryContext =>
            {
                factoryRevisions.Add(
                    factoryContext.ExpectedCoordinate.SaveRevision);
                return Effect(
                    context =>
                    {
                        calls.Add("a");
                        Assert.Equal(
                            "0",
                            context.Source.State
                                .GetProperty("value")
                                .GetString());
                        context.Draft.ReplaceState(
                            Json("""{"value":"1"}"""));
                        return Applied("a");
                    });
            }
        ),
            ("effect-b", factoryContext =>
            {
                factoryRevisions.Add(
                    factoryContext.ExpectedCoordinate.SaveRevision);
                return Effect(
                    context =>
                    {
                        calls.Add("b");
                        Assert.Equal(
                            "1",
                            context.Source.State
                                .GetProperty("value")
                                .GetString());
                        context.Draft.ReplaceState(
                            Json("""{"value":"2"}"""));
                        return Applied("b");
                    });
            }
        ));
        var artifact = new WorldAuthoritativeEventPlan(
            plan,
            prepared.Coordinate);

        var result = await new WorldAuthoritativeEventPlanExecutor(
                store,
                registry)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(artifact),
                default);

        Assert.True(result.Succeeded);
        Assert.Equal(
            WorldAuthoritativePlanExecutionStatus.Completed,
            result.Status);
        Assert.Equal(new[] { "a", "b" }, calls);
        Assert.Equal(new long[] { 0, 1 }, factoryRevisions);
        Assert.Equal(2, result.Executions.Count);
        Assert.Equal(
            new[]
            {
                WorldTransactionExecutionStatus.Committed,
                WorldTransactionExecutionStatus.Committed
            },
            result.Executions.Select(item => item.Result.Status));
        Assert.Equal(2, result.Coordinate.SaveRevision);
        Assert.Equal(2, result.Coordinate.StateVersion);
        var state = await store.ReadAsync(Address(), default);
        Assert.Equal(
            "2",
            state!.State.GetProperty("value").GetString());
        Assert.All(
            plan.Instances,
            instance => Assert.NotNull(
                store.FindInstanceAsync(instance.InstanceId, default)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()));
    }

    [Fact]
    public async Task DurableExecutorRecoversTheCompleteBatchSequence()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-world-plan-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "world.json");
            var prepared = await PreparePlanAsync(
                "effect-a",
                "effect-b");
            var store = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { Snapshot(prepared.Coordinate, 1) });
            var plan = prepared.Plan;
            var registry = Registry(
                ("effect-a", _ => Effect(
                    context =>
                    {
                        context.Draft.ReplaceState(
                            Json("""{"value":"1"}"""));
                        return Applied("a");
                    })),
                ("effect-b", _ => Effect(
                    context =>
                    {
                        context.Draft.ReplaceState(
                            Json("""{"value":"2"}"""));
                        return Applied("b");
                    })));
            var result = await new WorldAuthoritativeEventPlanExecutor(
                    store,
                    registry)
                .ExecuteAsync(
                    new WorldEventPlanExecutionRequest(
                        new WorldAuthoritativeEventPlan(
                            plan,
                            prepared.Coordinate)),
                    default);

            var restarted =
                new FileWorldAuthoritativeTransactionStore(path);
            var state = await restarted.ReadAsync(Address(), default);
            var history = await Task.WhenAll(
                plan.Instances.Select(
                    item => restarted.FindInstanceAsync(
                            item.InstanceId,
                            default)
                        .AsTask()));

            Assert.True(result.Succeeded);
            Assert.Equal(2, state!.Coordinate.SaveRevision);
            Assert.Equal(
                "2",
                state.State.GetProperty("value").GetString());
            Assert.All(history, Assert.NotNull);
            Assert.Equal(
                2,
                result.Executions.Count(
                    item => item.Result.Receipt is not null));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PartialFailureStopsLaterBatchesAndIsNeverReportedSuccess()
    {
        var prepared = await PreparePlanAsync(
            "effect-a",
            "effect-b",
            "effect-c");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var calls = new List<string>();
        var registry = Registry(
            ("effect-a", _ => Effect(
                context =>
                {
                    calls.Add("a");
                    context.Draft.ReplaceState(Json("""{"value":"1"}"""));
                    return Applied("a");
                })),
            ("effect-b", _ => Effect(
                _ =>
                {
                    calls.Add("b");
                    return new WorldEventEffectResult(false, "blocked");
                })),
            ("effect-c", _ => Effect(
                _ =>
                {
                    calls.Add("c");
                    return Applied("c");
                })));

        var result = await new WorldAuthoritativeEventPlanExecutor(
                store,
                registry)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    new WorldAuthoritativeEventPlan(
                        plan,
                        prepared.Coordinate)),
                default);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WorldAuthoritativePlanExecutionStatus.PartiallyCompleted,
            result.Status);
        Assert.Equal(
            WorldAuthoritativePlanReasonCodes.PartialFailure,
            result.ReasonCode);
        Assert.Equal(new[] { "a", "b" }, calls);
        Assert.Equal(2, result.Executions.Count);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(
            WorldTransactionExecutionStatus.Rejected,
            result.Executions[1].Result.Status);
        Assert.NotNull(
            await store.FindInstanceAsync(
                plan.Instances[0].InstanceId,
                default));
        Assert.Null(
            await store.FindInstanceAsync(
                plan.Instances[1].InstanceId,
                default));
        Assert.Null(
            await store.FindInstanceAsync(
                plan.Instances[2].InstanceId,
                default));
    }

    [Theory]
    [InlineData("version", WorldTransactionReasonCodes.StaleVersion)]
    [InlineData("catalog", WorldTransactionReasonCodes.StaleCatalog)]
    [InlineData("incarnation", WorldTransactionReasonCodes.StaleIncarnation)]
    public async Task ExactFenceIsRevalidatedBeforeEffectExecution(
        string mismatch,
        string expectedReason)
    {
        var prepared = await PreparePlanAsync("effect");
        var expected = prepared.Coordinate;
        var actualCoordinate = mismatch == "version"
            ? Coordinate(
                saveRevision: 1,
                stateVersion: 1,
                catalogDigest: expected.CatalogDigest)
            : mismatch == "catalog"
                ? Coordinate(
                    catalogDigest:
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                : expected;
        var actualIncarnation = mismatch == "incarnation" ? 2 : 1;
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            Snapshot(actualCoordinate, actualIncarnation));
        var plan = prepared.Plan;
        var calls = 0;
        var registry = Registry(
            ("effect", _ => Effect(
                _ =>
                {
                    calls++;
                    return Applied("must_not_run");
                })));

        var result = await new WorldAuthoritativeEventPlanExecutor(
                store,
                registry)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    new WorldAuthoritativeEventPlan(plan, expected)),
                default);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WorldAuthoritativePlanExecutionStatus.Rejected,
            result.Status);
        Assert.Equal(expectedReason, result.Executions[0].Result.ReasonCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RestartedPlanReplaysCommittedPrefixThenContinues()
    {
        var prepared = await PreparePlanAsync("effect-a", "effect-b");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var firstInstance = plan.ExecutionBatches[0].Instances[0];
        var firstEffect = Effect(
            context =>
            {
                context.Draft.ReplaceState(Json("""{"value":"1"}"""));
                return Applied("a");
            });
        var first = new WorldEventTransactionExecutionRequest(
            firstInstance,
            prepared.Coordinate,
            "world.command." + firstInstance.InstanceId,
            "world.operation." + firstInstance.InstanceId,
            firstEffect);
        var prefix = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(first, default);
        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            prefix.Status);
        var secondCalls = 0;
        var registry = Registry(
            ("effect-b", _ => Effect(
                context =>
                {
                    secondCalls++;
                    context.Draft.ReplaceState(Json("""{"value":"2"}"""));
                    return Applied("b");
                })));

        var result = await new WorldAuthoritativeEventPlanExecutor(
                store,
                registry)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    new WorldAuthoritativeEventPlan(
                        plan,
                        prepared.Coordinate)),
                default);

        Assert.True(result.Succeeded);
        Assert.Equal(
            WorldTransactionExecutionStatus.Replayed,
            result.Executions[0].Result.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            result.Executions[1].Result.Status);
        Assert.Equal(1, secondCalls);
        Assert.Equal(2, result.Coordinate.SaveRevision);
    }

    [Fact]
    public async Task FacadeUsesTheTypedAuthoritativeExecutionBoundary()
    {
        var prepared = await PreparePlanAsync("effect");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var executor = new WorldAuthoritativeEventPlanExecutor(
            store,
            Registry(
                ("effect", _ => Effect(
                    context =>
                    {
                        context.Draft.ReplaceState(
                            Json("""{"value":"1"}"""));
                        return Applied("facade");
                    }))));
        var facade = new InteractiveWorldFacade(
            new WorldEventPlanner(
                new WorldEventHandlerRegistryBuilder().Build(),
                store),
            executor);

        var result = await facade.ExecuteAuthoritativePlanAsync(
            new WorldAuthoritativeEventPlan(
                plan,
                prepared.Coordinate));

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Succeeded);
        Assert.Equal(1, result.Value.Coordinate.SaveRevision);
        Assert.Equal(
            "1",
            (await store.ReadAsync(Address(), default))!
            .State.GetProperty("value").GetString());
    }

    [Fact]
    public async Task PlanCannotBeReboundAfterTheAdmittedStateAdvances()
    {
        var prepared = await PreparePlanAsync("effect");
        var plan = prepared.Plan;

        var exception = Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeEventPlan(
                plan,
                Coordinate(
                    saveRevision: 1,
                    stateVersion: 1,
                    catalogDigest:
                    prepared.Coordinate.CatalogDigest)));

        Assert.Contains(
            "not admitted",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LooseDefinitionsRemainPlanningOnly()
    {
        var definition = new WorldEventDefinition(
            "event",
            "1",
            "tick",
            0,
            "condition",
            "selector",
            "resolver",
            "effect");
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver())
            .AddEffect("effect", new PlanningEffect())
            .Build();
        var facade = new InteractiveWorldFacade(
            new WorldEventPlanner(
                handlers,
                new InMemoryWorldEventHistory()));
        var result = await facade.PlanTriggerAsync(
            Trigger(),
            new[] { definition },
            Fence());

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.AdmissionFence);
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeEventPlan(
                result.Value,
                Coordinate()));
    }

    [Fact]
    public async Task EventCatalogSnapshotMustMatchTheAdmittedComponent()
    {
        var first = new WorldEventDefinition(
            "event",
            "1",
            "tick",
            0,
            "condition",
            "selector",
            "resolver",
            "effect-a");
        var changed = new WorldEventDefinition(
            "event",
            "1",
            "tick",
            0,
            "condition",
            "selector",
            "resolver",
            "effect-b");
        var firstCatalog = new WorldEventCatalogSnapshot(
            "events",
            1,
            new[] { first });
        var changedCatalog = new WorldEventCatalogSnapshot(
            "events",
            2,
            new[] { changed });
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver())
            .AddEffect("effect-a", new PlanningEffect())
            .AddEffect("effect-b", new PlanningEffect())
            .Build();
        var facade = new InteractiveWorldFacade(
            new WorldEventPlanner(
                handlers,
                new InMemoryWorldEventHistory()));

        var stale = await facade.PlanTriggerAsync(
            Trigger(),
            firstCatalog,
            Fence(
                changedCatalog.Digest,
                changedCatalog.Digest));
        var current = await facade.PlanTriggerAsync(
            Trigger(),
            firstCatalog,
            Fence(firstCatalog.Digest, firstCatalog.Digest));

        Assert.False(stale.Succeeded);
        Assert.Equal(
            InteractiveWorldReasonCodes.StaleCatalog,
            stale.ReasonCode);
        Assert.True(current.Succeeded);
        Assert.NotNull(current.Value!.AdmissionFence);
        _ = new WorldAuthoritativeEventPlan(
            current.Value,
            Coordinate(catalogDigest: firstCatalog.Digest));
    }

    [Fact]
    public async Task PendingUnknownPrefixIsReconciledWithoutRedispatch()
    {
        var prepared = await PreparePlanAsync("effect");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var instance = Assert.Single(plan.Instances);
        var calls = 0;
        var factoryCalls = 0;
        var effect = Effect(
            _ =>
            {
                calls++;
                return Applied("must_not_run");
            });
        var transactionRequest =
            new WorldEventTransactionExecutionRequest(
                instance,
                prepared.Coordinate,
                "world.command." + instance.InstanceId,
                "world.operation." + instance.InstanceId,
                effect);
        var begin = await store.BeginAsync(
            transactionRequest.TransactionRequest,
            default);
        await begin.Transaction!.DisposeAsync();
        var registry = Registry(
            ("effect", _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException(
                    "Recovery must not recreate a pending effect.");
            }
        ));

        var result = await new WorldAuthoritativeEventPlanExecutor(
                store,
                registry)
            .ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    new WorldAuthoritativeEventPlan(
                        plan,
                        prepared.Coordinate)),
                default);

        Assert.Equal(
            WorldAuthoritativePlanExecutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.ReconciliationRequired,
            result.Executions[0].Result.Status);
        Assert.Equal(0, calls);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task GeneralExecutorBoundaryRequiresTypedBoundArtifact()
    {
        var prepared = await PreparePlanAsync("effect");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        IWorldEventPlanExecutor executor =
            new WorldAuthoritativeEventPlanExecutor(
                store,
                Registry(("effect", _ => Effect(_ => Applied("ok")))));

        var rejected = await executor.ExecuteAsync(plan, null, default);

        Assert.Equal(
            WorldAuthoritativePlanReasonCodes.ExecutionContextRequired,
            rejected.OutcomeCode);
        Assert.DoesNotContain(
            plan.Instances,
            instance => store.FindInstanceAsync(
                        instance.InstanceId,
                        default)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                    is not null);
    }

    [Fact]
    public async Task GeneralExecutorBoundaryRunsTheTypedTransactionPipeline()
    {
        var prepared = await PreparePlanAsync("effect");
        var store = Store(prepared.Coordinate);
        var plan = prepared.Plan;
        var artifact = new WorldAuthoritativeEventPlan(
            plan,
            prepared.Coordinate);
        IWorldEventPlanExecutor executor =
            new WorldAuthoritativeEventPlanExecutor(
                store,
                Registry(
                    ("effect", _ => Effect(
                        context =>
                        {
                            context.Draft.ReplaceState(
                                Json("""{"value":"1"}"""));
                            return Applied("changed");
                        }))));

        var result = await executor.ExecuteAsync(
            plan,
            new WorldAuthoritativePlanExecutionContext(artifact),
            default);
        var state = await store.ReadAsync(Address(), default);

        Assert.Equal(
            WorldAuthoritativePlanReasonCodes.Applied,
            result.OutcomeCode);
        Assert.True(result.Evidence.HasValue);
        Assert.Equal(
            "1",
            state!.State.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ExecutionEvidencePreservesLongsBeyondSafeIntegerRange()
    {
        const long firstUnsafeInteger = 9_007_199_254_740_992;
        const long adjacentInteger = firstUnsafeInteger + 1;

        var first = await ExecuteEvidenceAtAsync(firstUnsafeInteger);
        var adjacent = await ExecuteEvidenceAtAsync(adjacentInteger);

        Assert.Equal(
            JsonValueKind.String,
            first.GetProperty("resultingSaveRevision").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            first.GetProperty("resultingStateVersion").ValueKind);
        Assert.Equal(
            (firstUnsafeInteger + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            first.GetProperty("resultingSaveRevision").GetString());
        Assert.NotEqual(first.GetRawText(), adjacent.GetRawText());
        Assert.NotEqual(
            CanonicalJsonDigest.ComputeSha256(first),
            CanonicalJsonDigest.ComputeSha256(adjacent));
    }

    private static async Task<JsonElement> ExecuteEvidenceAtAsync(
        long coordinateValue)
    {
        var prepared = await PreparePlanAtCoordinateAsync(
            coordinateValue,
            coordinateValue,
            coordinateValue,
            "effect");
        var store = Store(prepared.Coordinate);
        var artifact = new WorldAuthoritativeEventPlan(
            prepared.Plan,
            prepared.Coordinate);
        IWorldEventPlanExecutor executor =
            new WorldAuthoritativeEventPlanExecutor(
                store,
                Registry(
                    ("effect", _ => Effect(
                        context =>
                        {
                            context.Draft.ReplaceState(
                                Json("""{"value":"1"}"""));
                            return Applied("changed");
                        }))));

        var result = await executor.ExecuteAsync(
            prepared.Plan,
            new WorldAuthoritativePlanExecutionContext(artifact),
            default);

        Assert.Equal(
            WorldAuthoritativePlanReasonCodes.Applied,
            result.OutcomeCode);
        return result.Evidence!.Value;
    }

    private static IWorldTransactionalEventEffectRegistry Registry(
        params (
            string Id,
            Func<
                WorldTransactionalEffectFactoryContext,
                IWorldTransactionalEventEffect> Create)[] entries)
    {
        var builder = new WorldTransactionalEventEffectRegistryBuilder();
        foreach (var entry in entries)
        {
            builder.Add(entry.Id, new DelegateFactory(entry.Create));
        }

        return builder.Build();
    }

    private static DelegateEffect Effect(
        Func<
            WorldTransactionalEventEffectContext,
            WorldEventEffectResult> apply)
    {
        return new DelegateEffect(apply);
    }

    private static InMemoryWorldAuthoritativeTransactionStore Store(
        WorldAuthoritativeCoordinate coordinate)
    {
        return new InMemoryWorldAuthoritativeTransactionStore(
            Snapshot(coordinate, 1));
    }

    private static WorldAuthoritativeStateSnapshot Snapshot(
        WorldAuthoritativeCoordinate coordinate,
        long incarnation)
    {
        return new WorldAuthoritativeStateSnapshot(
            coordinate,
            Json("""{"value":"0"}"""),
            new Dictionary<string, long> { ["actor"] = incarnation });
    }

    private static async Task<PreparedPlan> PreparePlanAsync(
        params string[] effectIds)
    {
        return await PreparePlanAtCoordinateAsync(
            timelineEpoch: 1,
            saveRevision: 0,
            stateVersion: 0,
            effectIds);
    }

    private static async Task<PreparedPlan> PreparePlanAtCoordinateAsync(
        long timelineEpoch,
        long saveRevision,
        long stateVersion,
        params string[] effectIds)
    {
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver());
        var definitions = new List<WorldEventDefinition>();
        for (var index = 0; index < effectIds.Length; index++)
        {
            handlers.AddEffect(effectIds[index], new PlanningEffect());
            definitions.Add(
                new WorldEventDefinition(
                    "event-" + index,
                    "1",
                    "tick",
                    effectIds.Length - index,
                    "condition",
                    "selector",
                    "resolver",
                    effectIds[index],
                    writeResourceKeys: new[] { "state:value" }));
        }

        var trigger = new WorldEvolutionTrigger(
            "trigger",
            "tick",
            "world",
            "timeline",
            timelineEpoch,
            new GameTimePoint(
                "clock",
                "timeline",
                timelineEpoch,
                10));
        var catalog = new WorldEventCatalogSnapshot(
            "test.events",
            generation: 0,
            definitions);
        var admissionCoordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            timelineEpoch,
            saveRevision,
            stateVersion,
            catalog.Digest);
        var result = await new InteractiveWorldFacade(
                new WorldEventPlanner(
                    handlers.Build(),
                    new InMemoryWorldEventHistory()))
            .PlanTriggerAsync(
                trigger,
                catalog,
                new WorldStateFence(
                    admissionCoordinate.WorldId,
                    admissionCoordinate.TimelineId,
                    admissionCoordinate.TimelineEpoch,
                    admissionCoordinate.SaveRevision,
                    admissionCoordinate.StateVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    admissionCoordinate.CatalogDigest,
                    catalog.Digest));
        return new PreparedPlan(
            result.Value!,
            admissionCoordinate,
            catalog);
    }

    private static WorldEventEffectResult Applied(string outcome)
    {
        return new WorldEventEffectResult(true, outcome);
    }

    private static WorldAuthoritativeCoordinate Coordinate(
        long saveRevision = 0,
        long stateVersion = 0,
        string catalogDigest = CatalogDigest)
    {
        return new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            1,
            saveRevision,
            stateVersion,
            catalogDigest);
    }

    private static WorldTimelineAddress Address()
    {
        return new WorldTimelineAddress("world", "timeline");
    }

    private static WorldEvolutionTrigger Trigger()
    {
        return new WorldEvolutionTrigger(
            "trigger",
            "tick",
            "world",
            "timeline",
            1,
            new GameTimePoint("clock", "timeline", 1, 10));
    }

    private static WorldStateFence Fence(
        string catalogDigest = CatalogDigest,
        string? eventCatalogDigest = null)
    {
        return new WorldStateFence(
            "world",
            "timeline",
            1,
            0,
            "0",
            catalogDigest,
            eventCatalogDigest);
    }

    private sealed class PreparedPlan
    {
        public PreparedPlan(
            WorldEventPlan plan,
            WorldAuthoritativeCoordinate coordinate,
            WorldEventCatalogSnapshot catalog)
        {
            Plan = plan;
            Coordinate = coordinate;
            Catalog = catalog;
        }

        public WorldEventPlan Plan { get; }

        public WorldAuthoritativeCoordinate Coordinate { get; }

        public WorldEventCatalogSnapshot Catalog { get; }
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
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
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventParticipant> participants = new[]
            {
                new WorldEventParticipant("actor", 1, "actor")
            };
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                participants);
        }
    }

    private sealed class Resolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventResolution> resolutions = new[]
            {
                new WorldEventResolution(
                    "resolution",
                    selectedParticipants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                resolutions);
        }
    }

    private sealed class PlanningEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventEffectResult>(Applied("planned"));
        }
    }

    private sealed class DelegateFactory
        : IWorldTransactionalEventEffectFactory
    {
        private readonly Func<
            WorldTransactionalEffectFactoryContext,
            IWorldTransactionalEventEffect> _create;

        public DelegateFactory(
            Func<
                WorldTransactionalEffectFactoryContext,
                IWorldTransactionalEventEffect> create)
        {
            _create = create;
        }

        public ValueTask<IWorldTransactionalEventEffect> CreateAsync(
            WorldTransactionalEffectFactoryContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IWorldTransactionalEventEffect>(
                _create(context));
        }
    }

    private sealed class DelegateEffect : IWorldTransactionalEventEffect
    {
        private readonly Func<
            WorldTransactionalEventEffectContext,
            WorldEventEffectResult> _apply;

        public DelegateEffect(
            Func<
                WorldTransactionalEventEffectContext,
                WorldEventEffectResult> apply)
        {
            _apply = apply;
        }

        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldTransactionalEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(_apply(context));
        }
    }
}
