using System.Collections;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldEventPlannerTests
{
    [Fact]
    public async Task CustomPeriodTriggerPlansStableOrderAndIdentity()
    {
        var trigger = new TestPeriodTrigger(
            "trigger-12",
            "world",
            Time(11),
            Time(12));
        var definitions = new[]
        {
            Definition("later", priority: 10),
            Definition("first", priority: 20)
        };
        var registry = Registry(
            selector: _ => new[]
            {
                Participant("second"),
                Participant("first")
            },
            resolver: (_, participants) => new[]
            {
                new WorldEventResolution(
                    "b",
                    participants,
                    readResourceKeys: new[] { "state:b" }),
                new WorldEventResolution(
                    "a",
                    participants,
                    writeResourceKeys: new[] { "state:a" })
            });

        var first = await Planner(registry).PlanAsync(
            new WorldEventPlanningRequest(trigger, definitions));
        var second = await Planner(registry).PlanAsync(
            new WorldEventPlanningRequest(
                trigger,
                definitions.Reverse().ToArray()));

        Assert.Equal(
            new[] { "first", "first", "later", "later" },
            first.Instances.Select(item => item.DefinitionId));
        Assert.Equal(
            new[] { "a", "b", "a", "b" },
            first.Instances.Select(item => item.ResolutionKey));
        Assert.Equal(
            first.Instances.Select(item => item.InstanceId),
            second.Instances.Select(item => item.InstanceId));
        Assert.All(
            first.Instances,
            item =>
            {
                Assert.StartsWith("evt_", item.InstanceId);
                Assert.Equal(68, item.InstanceId.Length);
                Assert.Equal(
                    new[] { "first", "second" },
                    item.Participants.Select(participant =>
                        participant.EntityId));
            });
        Assert.Equal(
            WorldAgentInvocationPolicy.OncePerParticipant,
            first.Instances[0].Definition.AgentInvocationPolicy);
    }

    [Fact]
    public async Task PlannerUsesHostHandlersWithoutApplyingEffects()
    {
        var calls = new List<string>();
        var effect = new DelegateEffect(
            _ =>
            {
                calls.Add("effect");
                return new WorldEventEffectResult(true, "applied");
            });
        var registry = Registry(
            condition: _ =>
            {
                calls.Add("condition");
                return true;
            },
            selector: _ =>
            {
                calls.Add("selector");
                return new[] { Participant("actor") };
            },
            resolver: (_, participants) =>
            {
                calls.Add("resolver");
                return new[]
                {
                    new WorldEventResolution("candidate", participants)
                };
            },
            effect: effect);

        var plan = await Planner(registry).PlanAsync(
            Request(Definition("fixed", priority: 1)));

        Assert.Single(plan.Instances);
        Assert.Equal(
            new[] { "condition", "selector", "resolver" },
            calls);
        Assert.DoesNotContain("effect", calls);
    }

    [Fact]
    public async Task HistoryEnforcesCooldownOccurrenceLimitAndReplay()
    {
        var history = new InMemoryWorldEventHistory();
        var definition = Definition(
            "bounded",
            priority: 1,
            cooldown: new WorldEventCooldown(3),
            maximumOccurrences: 2);
        var registry = Registry();
        var planner = new WorldEventPlanner(registry, history);

        var first = await planner.PlanAsync(
            Request(definition, tick: 10, triggerId: "trigger-10"));
        var instance = Assert.Single(first.Instances);
        Assert.Equal(
            WorldEventHistoryAppendResult.Appended,
            await history.TryAppendAsync(
                WorldEventHistoryRecord.FromInstance(instance),
                default));

        var replay = await planner.PlanAsync(
            Request(definition, tick: 10, triggerId: "trigger-10"));
        Assert.Empty(replay.Instances);
        Assert.Equal(
            WorldEventEvaluationStatus.CooldownActive,
            Assert.Single(replay.Evaluations).Status);

        var cooldown = await planner.PlanAsync(
            Request(definition, tick: 12, triggerId: "trigger-12"));
        Assert.Empty(cooldown.Instances);
        Assert.Equal(
            WorldEventEvaluationStatus.CooldownActive,
            Assert.Single(cooldown.Evaluations).Status);

        var second = await planner.PlanAsync(
            Request(definition, tick: 13, triggerId: "trigger-13"));
        var secondInstance = Assert.Single(second.Instances);
        _ = await history.TryAppendAsync(
            WorldEventHistoryRecord.FromInstance(secondInstance),
            default);

        var exhausted = await planner.PlanAsync(
            Request(definition, tick: 20, triggerId: "trigger-20"));
        Assert.Empty(exhausted.Instances);
        Assert.Equal(
            WorldEventEvaluationStatus.MaximumOccurrencesReached,
            Assert.Single(exhausted.Evaluations).Status);
    }

    [Fact]
    public async Task RecordedInstanceIsSuppressedWhenNoCooldownExists()
    {
        var history = new InMemoryWorldEventHistory();
        var definition = Definition("replay", priority: 1);
        var planner = new WorldEventPlanner(Registry(), history);
        var first = await planner.PlanAsync(Request(definition));
        _ = await history.TryAppendAsync(
            WorldEventHistoryRecord.FromInstance(
                Assert.Single(first.Instances)),
            default);

        var replay = await planner.PlanAsync(Request(definition));

        Assert.Empty(replay.Instances);
        Assert.Equal(
            WorldEventEvaluationStatus.AlreadyRecorded,
            Assert.Single(replay.Evaluations).Status);
    }

    [Fact]
    public async Task MissingHandlersAndFabricatedParticipantsFailClosed()
    {
        var incomplete = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new DelegateCondition(_ => true))
            .Build();
        var missing = await Assert.ThrowsAsync<
            WorldEventConfigurationException>(
            () => new WorldEventPlanner(
                    incomplete,
                    new InMemoryWorldEventHistory())
                .PlanAsync(Request(Definition("event", priority: 0)))
                .AsTask());
        Assert.Equal(
            WorldEvolutionReasonCodes.MissingHandler,
            missing.ReasonCode);

        var fabricated = Registry(
            selector: _ => new[] { Participant("selected") },
            resolver: (_, _) => new[]
            {
                new WorldEventResolution(
                    "candidate",
                    new[] { Participant("fabricated") })
            });
        var invalid = await Assert.ThrowsAsync<
            WorldEventConfigurationException>(
            () => Planner(fabricated)
                .PlanAsync(Request(Definition("event", priority: 0)))
                .AsTask());
        Assert.Equal(
            WorldEvolutionReasonCodes.InvalidHandlerResult,
            invalid.ReasonCode);
    }

    [Fact]
    public async Task BoundsCascadeCandidatesParticipantsAndCancellation()
    {
        var definition = Definition("event", priority: 0);
        var cascadePlanner = Planner(
            Registry(),
            new WorldEventPlannerOptions(maxCascadeDepth: 1));
        var cascade = await Assert.ThrowsAsync<WorldEvolutionLimitException>(
            () => cascadePlanner.PlanAsync(
                    new WorldEventPlanningRequest(
                        Trigger(),
                        new[] { definition },
                        cascadeDepth: 2,
                        parentInstanceId: "evt_parent"))
                .AsTask());
        Assert.Equal(
            WorldEvolutionReasonCodes.CascadeLimitExceeded,
            cascade.ReasonCode);

        var candidates = Planner(
            Registry(
                resolver: (_, participants) => Enumerable.Range(0, 3)
                    .Select(index => new WorldEventResolution(
                        "candidate-" + index,
                        participants))
                    .ToArray()),
            new WorldEventPlannerOptions(
                maxCandidates: 2,
                maxCandidatesPerDefinition: 2));
        var candidateLimit =
            await Assert.ThrowsAsync<WorldEvolutionLimitException>(
                () => candidates.PlanAsync(Request(definition)).AsTask());
        Assert.Equal(
            WorldEvolutionReasonCodes.CandidateLimitExceeded,
            candidateLimit.ReasonCode);

        var participants = Planner(
            Registry(
                selector: _ => new[]
                {
                    Participant("one"),
                    Participant("two")
                }),
            new WorldEventPlannerOptions(
                maxParticipantsPerSelection: 1,
                maxParticipantsPerInstance: 1));
        var participantLimit =
            await Assert.ThrowsAsync<WorldEvolutionLimitException>(
                () => participants.PlanAsync(Request(definition)).AsTask());
        Assert.Equal(
            WorldEvolutionReasonCodes.ParticipantLimitExceeded,
            participantLimit.ReasonCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Planner(Registry())
                .PlanAsync(Request(definition), cancellation.Token)
                .AsTask());
    }

    [Fact]
    public async Task PlannerBoundsLyingHandlerListsAtMaxPlusOne()
    {
        var participantProbe =
            new LyingReadOnlyList<WorldEventParticipant>(
                Participant("actor"),
                declaredCount: 1,
                maximumAllowedMoves: 3);
        var participantPlanner = Planner(
            Registry(selector: _ => participantProbe),
            new WorldEventPlannerOptions(
                maxParticipantsPerSelection: 2,
                maxParticipantsPerInstance: 2));

        var participantLimit =
            await Assert.ThrowsAsync<WorldEvolutionLimitException>(
                () => participantPlanner
                    .PlanAsync(Request(Definition("participants", 0)))
                    .AsTask());

        Assert.Equal(
            WorldEvolutionReasonCodes.ParticipantLimitExceeded,
            participantLimit.ReasonCode);
        Assert.Equal(3, participantProbe.MoveNextCalls);

        var resolutionProbe =
            new LyingReadOnlyList<WorldEventResolution>(
                new WorldEventResolution(
                    "candidate",
                    new[] { Participant("actor") }),
                declaredCount: 1,
                maximumAllowedMoves: 3);
        var resolutionPlanner = Planner(
            Registry(resolver: (_, _) => resolutionProbe),
            new WorldEventPlannerOptions(
                maxCandidates: 2,
                maxCandidatesPerDefinition: 2));

        var candidateLimit =
            await Assert.ThrowsAsync<WorldEvolutionLimitException>(
                () => resolutionPlanner
                    .PlanAsync(Request(Definition("resolutions", 0)))
                    .AsTask());

        Assert.Equal(
            WorldEvolutionReasonCodes.CandidateLimitExceeded,
            candidateLimit.ReasonCode);
        Assert.Equal(3, resolutionProbe.MoveNextCalls);
    }

    [Fact]
    public async Task CascadeParentChangesIdentityAndStaysInItsWave()
    {
        var definition = Definition("event", priority: 0);
        var planner = Planner(Registry());
        var first = await planner.PlanAsync(
            new WorldEventPlanningRequest(
                Trigger(),
                new[] { definition },
                cascadeDepth: 1,
                parentInstanceId: "evt_parent_a"));
        var second = await planner.PlanAsync(
            new WorldEventPlanningRequest(
                Trigger(),
                new[] { definition },
                cascadeDepth: 1,
                parentInstanceId: "evt_parent_b"));

        Assert.NotEqual(
            Assert.Single(first.Instances).InstanceId,
            Assert.Single(second.Instances).InstanceId);
        Assert.Equal(1, first.CascadeDepth);
        Assert.Equal("evt_parent_a", first.Instances[0].ParentInstanceId);
    }

    [Fact]
    public async Task TriggerPayloadIsClonedCanonicalAndIdentityBound()
    {
        var firstPayload = Json(
            """{"count":2,"nested":{"enabled":true,"name":"value"}}""");
        var reorderedPayload = Json(
            """{"nested":{"name":"value","enabled":true},"count":2}""");
        var changedPayload = Json(
            """{"count":3,"nested":{"enabled":true,"name":"value"}}""");
        var definition = Definition("event", priority: 0);
        var planner = Planner(Registry());

        var first = await planner.PlanAsync(
            new WorldEventPlanningRequest(
                TriggerWithPayload(firstPayload),
                new[] { definition }));
        var reordered = await planner.PlanAsync(
            new WorldEventPlanningRequest(
                TriggerWithPayload(reorderedPayload),
                new[] { definition }));
        var changed = await planner.PlanAsync(
            new WorldEventPlanningRequest(
                TriggerWithPayload(changedPayload),
                new[] { definition }));

        Assert.Equal(
            first.Trigger.PayloadDigest,
            reordered.Trigger.PayloadDigest);
        Assert.Equal(
            Assert.Single(first.Instances).InstanceId,
            Assert.Single(reordered.Instances).InstanceId);
        Assert.NotEqual(
            first.Trigger.PayloadDigest,
            changed.Trigger.PayloadDigest);
        Assert.NotEqual(
            first.Instances[0].InstanceId,
            changed.Instances[0].InstanceId);
        Assert.Equal(
            2,
            first.Trigger.Payload!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TimelineEpochIsolatesIdentityAndHistory()
    {
        var definition = Definition("event", priority: 0);
        var history = new InMemoryWorldEventHistory();
        var planner = new WorldEventPlanner(Registry(), history);
        var epochOne = Trigger(epoch: 1);
        var first = await planner.PlanAsync(
            new WorldEventPlanningRequest(epochOne, new[] { definition }));
        var firstInstance = Assert.Single(first.Instances);
        _ = await history.TryAppendAsync(
            WorldEventHistoryRecord.FromInstance(firstInstance),
            default);

        var epochTwo = Trigger(epoch: 2);
        var second = await planner.PlanAsync(
            new WorldEventPlanningRequest(epochTwo, new[] { definition }));
        var secondInstance = Assert.Single(second.Instances);

        Assert.NotEqual(firstInstance.InstanceId, secondInstance.InstanceId);
        Assert.Equal(
            0,
            (await history.ReadDefinitionAsync(
                new WorldEventDefinitionKey(
                    "world",
                    "timeline",
                    2,
                    definition.DefinitionId,
                    definition.Version),
                default)).OccurrenceCount);
    }

    [Fact]
    public async Task InteractionUsesGenericAdmissionAndPlanningPipeline()
    {
        var interaction = new InteractionDefinition(
            "interaction",
            "1",
            "schema.input.v1",
            priority: 10,
            availabilityHandlerId: "availability",
            costAdmissionHandlerId: "cost",
            participantSelectorId: "interaction-selector",
            resolverId: "interaction-resolver",
            effectHandlerId: "effect",
            confirmationAdmissionHandlerId: "confirmation",
            readResourceKeys: new[] { "actor:a:state" },
            writeResourceKeys: new[] { "target:b:state" },
            agentInvocationPolicy:
                WorldAgentInvocationPolicy.OncePerInstance);
        var actor = new GameEntityIdentity("a", 1);
        var target = new GameEntityIdentity("b", 2);
        var trigger = new InteractionRequestedTrigger(
            "request-1",
            "world",
            "timeline",
            1,
            actor,
            "schema.input.v1",
            Json("""{"amount":{"units":125,"scale":2}}"""),
            target,
            confirmationToken: "confirmation-1",
            gameTime: Time(12));
        var registry = new WorldEventHandlerRegistryBuilder()
            .AddCondition(
                "availability",
                new DelegateCondition(_ => true))
            .AddAdmission(
                "cost",
                new DelegateAdmission(
                    _ => WorldEventAdmissionDecision.Accept(
                        "cost_available")))
            .AddAdmission(
                "confirmation",
                new DelegateAdmission(
                    _ => WorldEventAdmissionDecision.Reject(
                        "confirmation_required")))
            .AddParticipantSelector(
                "interaction-selector",
                new DelegateSelector(
                    _ => new[]
                    {
                        new WorldEventParticipant("a", 1, "actor"),
                        new WorldEventParticipant("b", 2, "target")
                    }))
            .AddResolver(
                "interaction-resolver",
                new DelegateResolver(
                    (_, participants) => new[]
                    {
                        new WorldEventResolution(
                            "request",
                            participants)
                    }))
            .AddEffect(
                "effect",
                new DelegateEffect(
                    _ => new WorldEventEffectResult(
                        true,
                        "applied",
                        Json("""{"status":"continued"}"""))))
            .Build();

        var plan = await new WorldEventPlanner(
                registry,
                new InMemoryWorldEventHistory())
            .PlanAsync(
                new WorldEventPlanningRequest(
                    trigger,
                    new[] { interaction.ToEventDefinition() }));

        Assert.Empty(plan.Instances);
        var evaluation = Assert.Single(plan.Evaluations);
        Assert.Equal(
            WorldEventEvaluationStatus.AdmissionRejected,
            evaluation.Status);
        Assert.Equal("confirmation_required", evaluation.ReasonCode);
        Assert.Equal(
            125,
            trigger.Input.GetProperty("amount")
                .GetProperty("units")
                .GetInt64());
        Assert.NotNull(trigger.PayloadDigest);
    }

    [Fact]
    public void FixedPointAuthorityDoesNotUseBinaryFloatingPoint()
    {
        var lower = new WorldFixedPointValue(125, 2);
        var higher = new WorldFixedPointValue(126, 2);

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.Throws<InvalidOperationException>(
            () => lower.CompareTo(new WorldFixedPointValue(1250, 3)));
    }

    private static WorldEventPlanner Planner(
        IWorldEventHandlerRegistry registry,
        WorldEventPlannerOptions? options = null)
    {
        return new WorldEventPlanner(
            registry,
            new InMemoryWorldEventHistory(),
            options);
    }

    private static WorldEventPlanningRequest Request(
        WorldEventDefinition definition,
        long tick = 12,
        string triggerId = "trigger-12")
    {
        return new WorldEventPlanningRequest(
            Trigger(tick, triggerId),
            new[] { definition });
    }

    private static WorldEvolutionTrigger Trigger(
        long tick = 12,
        string triggerId = "trigger-12",
        long epoch = 1)
    {
        return new WorldEvolutionTrigger(
            triggerId,
            "period_advanced",
            "world",
            "timeline",
            timelineEpoch: epoch,
            Time(tick, epoch));
    }

    private static GameTimePoint Time(long tick, long epoch = 1)
    {
        return new GameTimePoint(
            "simulation",
            "timeline",
            epoch,
            tick);
    }

    private static WorldEvolutionTrigger TriggerWithPayload(
        JsonElement payload)
    {
        return new WorldEvolutionTrigger(
            "trigger-payload",
            "period_advanced",
            "world",
            "timeline",
            1,
            Time(12),
            payload);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static WorldEventDefinition Definition(
        string id,
        int priority,
        IEnumerable<string>? reads = null,
        IEnumerable<string>? writes = null,
        WorldEventCooldown? cooldown = null,
        int? maximumOccurrences = null)
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
            writes,
            cooldown,
            maximumOccurrences,
            agentInvocationPolicy:
                WorldAgentInvocationPolicy.OncePerParticipant);
    }

    private static WorldEventParticipant Participant(string id)
    {
        return new WorldEventParticipant(id, 1, "participant");
    }

    private static IWorldEventHandlerRegistry Registry(
        Func<WorldEventEvaluationContext, bool>? condition = null,
        Func<WorldEventEvaluationContext,
            IReadOnlyList<WorldEventParticipant>>? selector = null,
        Func<WorldEventEvaluationContext,
            IReadOnlyList<WorldEventParticipant>,
            IReadOnlyList<WorldEventResolution>>? resolver = null,
        IWorldEventEffectHandler? effect = null)
    {
        return new WorldEventHandlerRegistryBuilder()
            .AddCondition(
                "condition",
                new DelegateCondition(condition ?? (_ => true)))
            .AddParticipantSelector(
                "selector",
                new DelegateSelector(
                    selector
                    ?? (_ => new[] { Participant("actor") })))
            .AddResolver(
                "resolver",
                new DelegateResolver(
                    resolver
                    ?? ((_, participants) =>
                        new[]
                        {
                            new WorldEventResolution(
                                "candidate",
                                participants)
                        })))
            .AddEffect(
                "effect",
                effect
                ?? new DelegateEffect(
                    _ => new WorldEventEffectResult(true, "applied")))
            .Build();
    }

    private sealed class TestPeriodTrigger : WorldEvolutionTrigger
    {
        public TestPeriodTrigger(
            string triggerId,
            string worldId,
            GameTimePoint previous,
            GameTimePoint current)
            : base(
                triggerId,
                "period_advanced",
                worldId,
                current.TimelineId,
                current.Epoch,
                current)
        {
            if (!previous.IsComparableTo(current)
                || previous.CompareTo(current) >= 0)
            {
                throw new ArgumentException(
                    "The test trigger must advance one game clock.");
            }

            Previous = previous;
            Current = current;
        }

        public GameTimePoint Previous { get; }

        public GameTimePoint Current { get; }
    }

    private sealed class DelegateCondition : IWorldEventCondition
    {
        private readonly Func<WorldEventEvaluationContext, bool> _callback;

        public DelegateCondition(
            Func<WorldEventEvaluationContext, bool> callback)
        {
            _callback = callback;
        }

        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(_callback(context));
        }
    }

    private sealed class DelegateSelector : IWorldEventParticipantSelector
    {
        private readonly Func<WorldEventEvaluationContext,
            IReadOnlyList<WorldEventParticipant>> _callback;

        public DelegateSelector(
            Func<WorldEventEvaluationContext,
                IReadOnlyList<WorldEventParticipant>> callback)
        {
            _callback = callback;
        }

        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                _callback(context));
        }
    }

    private sealed class DelegateAdmission : IWorldEventAdmissionHandler
    {
        private readonly Func<WorldEventEvaluationContext,
            WorldEventAdmissionDecision> _callback;

        public DelegateAdmission(
            Func<WorldEventEvaluationContext,
                WorldEventAdmissionDecision> callback)
        {
            _callback = callback;
        }

        public ValueTask<WorldEventAdmissionDecision> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventAdmissionDecision>(
                _callback(context));
        }
    }

    private sealed class DelegateResolver : IWorldEventResolver
    {
        private readonly Func<WorldEventEvaluationContext,
            IReadOnlyList<WorldEventParticipant>,
            IReadOnlyList<WorldEventResolution>> _callback;

        public DelegateResolver(
            Func<WorldEventEvaluationContext,
                IReadOnlyList<WorldEventParticipant>,
                IReadOnlyList<WorldEventResolution>> callback)
        {
            _callback = callback;
        }

        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                _callback(context, selectedParticipants));
        }
    }

    private sealed class DelegateEffect : IWorldEventEffectHandler
    {
        private readonly Func<WorldEventEffectContext,
            WorldEventEffectResult> _callback;

        public DelegateEffect(
            Func<WorldEventEffectContext, WorldEventEffectResult> callback)
        {
            _callback = callback;
        }

        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(_callback(context));
        }
    }

    private sealed class LyingReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T _value;
        private readonly int _maximumAllowedMoves;

        public LyingReadOnlyList(
            T value,
            int declaredCount,
            int maximumAllowedMoves)
        {
            _value = value;
            Count = declaredCount;
            _maximumAllowedMoves = maximumAllowedMoves;
        }

        public int Count { get; }

        public int MoveNextCalls { get; private set; }

        public T this[int index] =>
            throw new InvalidOperationException(
                "The planner must enumerate the bounded list.");

        public IEnumerator<T> GetEnumerator()
        {
            while (true)
            {
                MoveNextCalls++;
                if (MoveNextCalls > _maximumAllowedMoves)
                {
                    throw new InvalidOperationException(
                        "The framework enumerated beyond Max+1.");
                }

                yield return _value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
