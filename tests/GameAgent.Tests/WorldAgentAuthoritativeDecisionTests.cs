using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.World;

namespace GameAgent.Tests;

public sealed class WorldAgentAuthoritativeDecisionTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string EnterEffectDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string WaitEffectDigest =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task RealDurableRuntimeProposalSurvivesReloadAndCommitsOnce()
    {
        var directory = TempDirectory();
        try
        {
            var coordinate = Coordinate();
            var store = Store(coordinate);
            var occurrence = await OccurrenceAsync(
                store,
                "decision-trigger");
            var enter = Option(
                "enter",
                occurrence,
                coordinate,
                "entered",
                EnterEffectDigest);
            var wait = Option(
                "wait",
                occurrence,
                coordinate,
                "waiting",
                WaitEffectDigest);
            var draft = new WorldAgentDecisionDraft(
                "draft-1",
                occurrence,
                coordinate,
                new[] { wait, enter });
            var job = Job(
                draft,
                "run-decision-1",
                WorldAgentFailurePolicy.Fault);
            var provider = new JsonFinalProvider(
                """{"optionId":"enter"}""");

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .EnableWorldAgentJobs()
                .Build();
            var bridge = new WorldAgentRuntimeBridge(
                built.Runtime,
                new FixedInputFactory());
            var coordinator =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    bridge,
                    store);

            var proposed = await coordinator.ProposeAsync(draft, job);

            Assert.True(
                proposed.Succeeded,
                ProposalFailure(proposed));
            Assert.Equal(1, provider.CallCount);
            var resumed = await coordinator.ResumeProposalAsync(draft, job);
            Assert.True(
                resumed.Succeeded,
                ProposalFailure(resumed));
            Assert.Equal(
                proposed.Proposal!.ProposalDigest,
                resumed.Proposal!.ProposalDigest);
            Assert.Equal(1, provider.CallCount);
            var restored =
                WorldAgentAuthoritativeProposal.FromEnvelope(
                    proposed.Proposal!.ToEnvelope());
            var restarted =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    bridge,
                    store);
            var committed = await restarted.CommitAsync(
                draft,
                job,
                restored);
            var replayed = await restarted.CommitAsync(
                draft,
                job,
                restored);
            var state = await store.ReadAsync(
                coordinate.Address,
                default);

            Assert.True(
                committed.Status
                == WorldAgentDecisionCommitStatus.Committed,
                committed.Status + ":" + committed.ReasonCode + ":"
                + committed.Execution?.Status);
            Assert.Equal(
                WorldAgentDecisionCommitStatus.Replayed,
                replayed.Status);
            Assert.Equal(
                "entered",
                state!.State
                    .GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("route")
                    .GetString());
            Assert.Equal(1, state.Coordinate.SaveRevision);
            Assert.Equal(1, state.Coordinate.StateVersion);
            Assert.NotNull(committed.Execution!.Receipt);
            Assert.Equal(
                committed.Execution.Receipt!.ReceiptId,
                replayed.Execution!.Receipt!.ReceiptId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealDurableRuntimeProposalCannotCommitAfterWorldAdvances()
    {
        var directory = TempDirectory();
        try
        {
            var coordinate = Coordinate();
            var store = Store(coordinate);
            var occurrence = await OccurrenceAsync(
                store,
                "decision-trigger");
            var enter = Option(
                "enter",
                occurrence,
                coordinate,
                "entered",
                EnterEffectDigest);
            var wait = Option(
                "wait",
                occurrence,
                coordinate,
                "waiting",
                WaitEffectDigest);
            var draft = new WorldAgentDecisionDraft(
                "draft-stale",
                occurrence,
                coordinate,
                new[] { enter, wait });
            var job = Job(
                draft,
                "run-stale-1",
                WorldAgentFailurePolicy.Fault);
            var provider = new JsonFinalProvider(
                """{"optionId":"enter"}""");

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .EnableWorldAgentJobs()
                .Build();
            var bridge = new WorldAgentRuntimeBridge(
                built.Runtime,
                new FixedInputFactory());
            var coordinator =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    bridge,
                    store);
            var proposed = await coordinator.ProposeAsync(draft, job);
            Assert.True(
                proposed.Succeeded,
                ProposalFailure(proposed));
            Assert.Equal(1, provider.CallCount);
            await AdvanceWorldAsync(store, coordinate);

            var committed = await coordinator.CommitAsync(
                draft,
                job,
                proposed.Proposal!);
            var state = await store.ReadAsync(
                coordinate.Address,
                default);

            Assert.False(committed.Succeeded);
            Assert.Equal(
                WorldAgentDecisionCommitStatus.Rejected,
                committed.Status);
            Assert.Equal(
                WorldTransactionReasonCodes.StaleVersion,
                committed.ReasonCode);
            Assert.Equal(
                "external",
                state!.State
                    .GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("route")
                    .GetString());
            Assert.Equal(1, state.Coordinate.StateVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnderstandingClassificationUsesTheSameClosedOptionPath()
    {
        var directory = TempDirectory();
        try
        {
            var coordinate = Coordinate();
            var store = Store(coordinate);
            var occurrence = await OccurrenceAsync(
                store,
                "understanding-trigger");
            var recognized = Option(
                "recognized",
                occurrence,
                coordinate,
                "understood",
                EnterEffectDigest);
            var draft = new WorldAgentDecisionDraft(
                "draft-understanding",
                occurrence,
                coordinate,
                new[] { recognized });
            var job = Job(
                draft,
                "run-understanding-1",
                WorldAgentFailurePolicy.Fault,
                kind: WorldAgentJobKind.Understanding);
            var provider = new JsonFinalProvider(
                """{"optionId":"recognized"}""");

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .EnableWorldAgentJobs()
                .Build();
            var coordinator =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    new WorldAgentRuntimeBridge(
                        built.Runtime,
                        new FixedInputFactory()),
                    store);

            var proposed = await coordinator.ProposeAsync(draft, job);
            Assert.True(
                proposed.Succeeded,
                ProposalFailure(proposed));
            var committed = await coordinator.CommitAsync(
                draft,
                job,
                proposed.Proposal!);
            var state = await store.ReadAsync(
                coordinate.Address,
                default);

            Assert.True(committed.Succeeded, committed.ReasonCode);
            Assert.Equal(
                "understood",
                state!.State
                    .GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("route")
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PendingUnknownNeverDispatchesOrSwitchesOption()
    {
        var directory = TempDirectory();
        try
        {
            var coordinate = Coordinate();
            var store = Store(coordinate);
            var occurrence = await OccurrenceAsync(
                store,
                "decision-trigger");
            var enter = Option(
                "enter",
                occurrence,
                coordinate,
                "entered",
                EnterEffectDigest);
            var wait = Option(
                "wait",
                occurrence,
                coordinate,
                "waiting",
                WaitEffectDigest);
            var draft = new WorldAgentDecisionDraft(
                "draft-pending",
                occurrence,
                coordinate,
                new[] { enter, wait });
            var job = Job(
                draft,
                "run-pending-1",
                WorldAgentFailurePolicy.Fault);
            var provider = new JsonFinalProvider(
                """{"optionId":"enter"}""");

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .EnableWorldAgentJobs()
                .Build();
            var bridge = new WorldAgentRuntimeBridge(
                built.Runtime,
                new FixedInputFactory());
            var coordinator =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    bridge,
                    store);
            var proposed = await coordinator.ProposeAsync(draft, job);
            Assert.True(
                proposed.Succeeded,
                ProposalFailure(proposed));
            var request = new WorldEventTransactionExecutionRequest(
                occurrence,
                coordinate,
                draft.CommandId,
                draft.OperationId,
                enter.Effect);
            var pending = await store.BeginAsync(
                request.TransactionRequest,
                default);
            await pending.Transaction!.DisposeAsync();

            var commit = await coordinator.CommitAsync(
                draft,
                job,
                proposed.Proposal!);
            var state = await store.ReadAsync(
                coordinate.Address,
                default);

            Assert.Equal(
                WorldAgentDecisionCommitStatus.ReconciliationRequired,
                commit.Status);
            Assert.Equal(
                "idle",
                state!.State
                    .GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("route")
                    .GetString());
            Assert.Equal(0, state.Coordinate.StateVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacingFallbackOrDraftAfterProposalFailsClosed()
    {
        var directory = TempDirectory();
        try
        {
            var coordinate = Coordinate();
            var store = Store(coordinate);
            var occurrence = await OccurrenceAsync(
                store,
                "decision-trigger");
            var enter = Option(
                "enter",
                occurrence,
                coordinate,
                "entered",
                EnterEffectDigest);
            var wait = Option(
                "wait",
                occurrence,
                coordinate,
                "waiting",
                WaitEffectDigest);
            var draft = new WorldAgentDecisionDraft(
                "draft-replacement",
                occurrence,
                coordinate,
                new[] { enter, wait });
            var original = Job(
                draft,
                "run-replacement-1",
                WorldAgentFailurePolicy.UseFallback,
                """{"optionId":"wait"}""");
            var provider = new JsonFinalProvider(
                """{"optionId":"enter"}""");

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .EnableWorldAgentJobs()
                .Build();
            var bridge = new WorldAgentRuntimeBridge(
                built.Runtime,
                new FixedInputFactory());
            var coordinator =
                new WorldAgentAuthoritativeDecisionCoordinator(
                    bridge,
                    store);
            var proposed = await coordinator.ProposeAsync(draft, original);
            Assert.True(
                proposed.Succeeded,
                ProposalFailure(proposed));
            var replacement = Job(
                draft,
                "run-replacement-1",
                WorldAgentFailurePolicy.UseFallback,
                """{"optionId":"enter"}""");

            var fallbackResult = await coordinator.CommitAsync(
                draft,
                replacement,
                proposed.Proposal!);
            var alteredEnter = Option(
                "enter",
                occurrence,
                coordinate,
                "altered",
                EnterEffectDigest);
            var alteredDraft = new WorldAgentDecisionDraft(
                "draft-replacement",
                occurrence,
                coordinate,
                new[] { alteredEnter, wait });
            var draftResult = await coordinator.CommitAsync(
                alteredDraft,
                original,
                proposed.Proposal!);
            var state = await store.ReadAsync(
                coordinate.Address,
                default);

            Assert.Equal(
                WorldAgentDecisionReasonCodes.ProposalBindingMismatch,
                fallbackResult.ReasonCode);
            Assert.Equal(
                WorldAgentDecisionReasonCodes.DraftBindingMismatch,
                draftResult.ReasonCode);
            Assert.Equal(0, state!.Coordinate.StateVersion);
            Assert.Equal(
                "idle",
                state.State
                    .GetProperty("entities")
                    .GetProperty("actor")
                    .GetProperty("route")
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NarrationCannotEnterAuthoritativeDecisionPath()
    {
        var coordinate = Coordinate();
        var store = Store(coordinate);
        var occurrence = await OccurrenceAsync(
            store,
            "decision-trigger");
        var option = Option(
            "enter",
            occurrence,
            coordinate,
            "entered",
            EnterEffectDigest);
        var draft = new WorldAgentDecisionDraft(
            "draft-narration",
            occurrence,
            coordinate,
            new[] { option });
        var context = GameCoordinate(coordinate);

        Assert.Throws<ArgumentException>(
            () => new WorldAgentJob(
                "narration-job",
                "narration-run",
                "narrator",
                occurrence.InstanceId,
                WorldAgentJobKind.Narration,
                context,
                Json("""{"topic":"gate"}"""),
                "narration",
                "1",
                WorldAgentOutputSchemas.Narration(),
                WorldAgentFailurePolicy.Skip,
                CatalogDigest,
                authoritativeBinding: draft.Binding));
    }

    [Fact]
    public void JobSemanticDigestIncludesTheAuthoritativeTimelineEpoch()
    {
        var coordinate = Coordinate();
        var stateVersion = coordinate.StateVersion.ToString(
            CultureInfo.InvariantCulture);
        var gameCoordinate = new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            new GameEntityIdentity("actor", 1),
            stateVersion: stateVersion,
            causality: new GameCausalityStamp(
                "decision-cause",
                stateVersion));
        var first = new WorldAgentJob(
            "decision",
            "run",
            "agent",
            "occurrence",
            WorldAgentJobKind.Understanding,
            gameCoordinate,
            Json("""{"input":"value"}"""),
            "classification",
            "1",
            WorldAgentOutputSchemas.Selection(new[] { "known" }),
            WorldAgentFailurePolicy.Fault,
            CatalogDigest,
            authoritativeBinding: new WorldAgentAuthoritativeBinding(
                "draft",
                new string('e', 64),
                "occurrence",
                coordinate));
        var second = new WorldAgentJob(
            "decision",
            "run",
            "agent",
            "occurrence",
            WorldAgentJobKind.Understanding,
            gameCoordinate,
            Json("""{"input":"value"}"""),
            "classification",
            "1",
            WorldAgentOutputSchemas.Selection(new[] { "known" }),
            WorldAgentFailurePolicy.Fault,
            CatalogDigest,
            authoritativeBinding: new WorldAgentAuthoritativeBinding(
                "draft",
                new string('e', 64),
                "occurrence",
                new WorldAuthoritativeCoordinate(
                    coordinate.WorldId,
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch + 1,
                    coordinate.SaveRevision,
                    coordinate.StateVersion,
                    coordinate.CatalogDigest)));

        Assert.NotEqual(first.SemanticDigest, second.SemanticDigest);
        Assert.Equal(
            coordinate.TimelineEpoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            first.ToEnvelope()
                .GetProperty("authoritativeBinding")
                .GetProperty("expectedCoordinate")
                .GetProperty("timelineEpoch")
                .GetString());
    }

    [Fact]
    public void JobEnvelopePreservesPortableLongsBeyondSafeIntegerRange()
    {
        const long firstUnsafeInteger = 9_007_199_254_740_992;
        const long adjacentInteger = firstUnsafeInteger + 1;
        var firstCoordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            firstUnsafeInteger,
            firstUnsafeInteger,
            firstUnsafeInteger,
            CatalogDigest);
        var adjacentCoordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            adjacentInteger,
            adjacentInteger,
            adjacentInteger,
            CatalogDigest);

        var first = PortableJob(firstCoordinate);
        var adjacent = PortableJob(adjacentCoordinate);
        var envelope = first.ToEnvelope();
        var coordinate = envelope.GetProperty("coordinate");
        var binding = envelope.GetProperty("authoritativeBinding")
            .GetProperty("expectedCoordinate");

        Assert.Equal(
            JsonValueKind.String,
            coordinate.GetProperty("saveRevision").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            coordinate.GetProperty("observer")
                .GetProperty("incarnation")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            binding.GetProperty("timelineEpoch").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            binding.GetProperty("saveRevision").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            binding.GetProperty("stateVersion").ValueKind);
        Assert.NotEqual(first.SemanticDigest, adjacent.SemanticDigest);
        Assert.NotEqual(
            envelope.GetRawText(),
            adjacent.ToEnvelope().GetRawText());
    }

    [Fact]
    public void OptionSchemaStopsEnumeratingAtItsHardLimit()
    {
        var yielded = 0;

        var exception = Assert.Throws<ArgumentException>(
            () => WorldAgentOutputSchemas.Selection(Options()));

        Assert.Equal("optionIds", exception.ParamName);
        Assert.Equal(257, yielded);

        IEnumerable<string> Options()
        {
            while (true)
            {
                yielded++;
                yield return "option-" + yielded;
            }
        }
    }

    private static WorldAgentJob PortableJob(
        WorldAuthoritativeCoordinate coordinate)
    {
        var stateVersion = coordinate.StateVersion.ToString(
            CultureInfo.InvariantCulture);
        return new WorldAgentJob(
            "portable-job",
            "portable-run",
            "portable-agent",
            "portable-occurrence",
            WorldAgentJobKind.Selection,
            new GameContextCoordinate(
                coordinate.WorldId,
                coordinate.TimelineId,
                coordinate.SaveRevision,
                new GameEntityIdentity(
                    "actor",
                    coordinate.SaveRevision),
                stateVersion: stateVersion,
                gameTime: new GameTimePoint(
                    "calendar",
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch,
                    coordinate.SaveRevision)),
            Json("""{"question":"choose"}"""),
            "selection",
            "1",
            WorldAgentOutputSchemas.Selection(new[] { "option" }),
            WorldAgentFailurePolicy.Fault,
            coordinate.CatalogDigest,
            authoritativeBinding: new WorldAgentAuthoritativeBinding(
                "draft",
                new string('e', 64),
                "portable-occurrence",
                coordinate));
    }

    private static WorldAgentJob Job(
        WorldAgentDecisionDraft draft,
        string runId,
        WorldAgentFailurePolicy failurePolicy,
        string? fallback = null,
        WorldAgentJobKind kind = WorldAgentJobKind.Selection)
    {
        return new WorldAgentJob(
            "decision-" + draft.DraftId,
            runId,
            "actor-agent",
            draft.OccurrenceId,
            kind,
            GameCoordinate(draft.ExpectedCoordinate),
            Json("""{"question":"Choose a declared route."}"""),
            "route-selection",
            "1",
            WorldAgentOutputSchemas.Selection(draft.OptionIds),
            failurePolicy,
            draft.ExpectedCoordinate.CatalogDigest,
            fallbackOutput: fallback is null ? null : Json(fallback),
            authoritativeBinding: draft.Binding);
    }

    private static WorldAgentMutationOption Option(
        string optionId,
        WorldEventInstance occurrence,
        WorldAuthoritativeCoordinate coordinate,
        string value,
        string effectDefinitionDigest)
    {
        var identity = new GameEntityIdentity("actor", 1);
        var intent = new WorldValueMutationIntent(
            "set-route-" + optionId,
            identity,
            "/route",
            "actor:route",
            WorldValueMutationKind.Set,
            Json("\"" + value + "\""));
        var mutation = new WorldAtomicMutationSet(
            "world.agent.command." + occurrence.InstanceId,
            "world.agent.operation." + occurrence.InstanceId,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            coordinate.CatalogDigest,
            new[] { intent });
        var effect = new WorldAtomicMutationEffect(
            mutation,
            Array.Empty<WorldNumericSchema>(),
            new WorldEntityMutationPathResolver(
                "/entities",
                "/relationships"));
        return new WorldAgentMutationOption(
            optionId,
            effect,
            effectDefinitionDigest);
    }

    private static async Task AdvanceWorldAsync(
        InMemoryWorldAuthoritativeTransactionStore store,
        WorldAuthoritativeCoordinate coordinate)
    {
        var occurrence = await OccurrenceAsync(
            store,
            "external-trigger");
        var option = Option(
            "external",
            occurrence,
            coordinate,
            "external",
            new string('d', 64));
        var request = new WorldEventTransactionExecutionRequest(
            occurrence,
            coordinate,
            option.Effect.MutationSet.CommandId,
            option.Effect.MutationSet.OperationId,
            option.Effect);
        var result = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(request, default);
        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            result.Status);
    }

    private static InMemoryWorldAuthoritativeTransactionStore Store(
        WorldAuthoritativeCoordinate coordinate)
    {
        return new InMemoryWorldAuthoritativeTransactionStore(
            new WorldAuthoritativeStateSnapshot(
                coordinate,
                Json(
                    """
                    {
                      "entities": {
                        "actor": {
                          "route": "idle"
                        }
                      },
                      "relationships": {}
                    }
                    """),
                new Dictionary<string, long>
                {
                    ["actor"] = 1
                }));
    }

    private static async Task<WorldEventInstance> OccurrenceAsync(
        IWorldEventHistory history,
        string triggerId)
    {
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver())
            .AddEffect("planning-effect", new PlanningEffect())
            .Build();
        var definition = new WorldEventDefinition(
            "decision-event",
            "1",
            "decision",
            100,
            "condition",
            "selector",
            "resolver",
            "planning-effect",
            writeResourceKeys: new[] { "actor:route" },
            agentInvocationPolicy:
            WorldAgentInvocationPolicy.OncePerInstance);
        var plan = await new WorldEventPlanner(handlers, history)
            .PlanAsync(
                new WorldEventPlanningRequest(
                    new WorldEvolutionTrigger(
                        triggerId,
                        "decision",
                        "world",
                        "timeline",
                        3,
                        new GameTimePoint(
                            "clock",
                            "timeline",
                            3,
                            20)),
                    new[] { definition }));
        return Assert.Single(plan.Instances);
    }

    private static WorldAuthoritativeCoordinate Coordinate()
    {
        return new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            3,
            0,
            0,
            CatalogDigest);
    }

    private static GameContextCoordinate GameCoordinate(
        WorldAuthoritativeCoordinate coordinate)
    {
        var stateVersion = coordinate.StateVersion.ToString(
            CultureInfo.InvariantCulture);
        return new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            new GameEntityIdentity("actor", 1),
            stateVersion: stateVersion,
            gameTime: new GameTimePoint(
                "clock",
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                20),
            causality: new GameCausalityStamp(
                "decision-cause",
                stateVersion));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string ProposalFailure(
        WorldAgentDecisionProposalResult result)
    {
        return string.Concat(
            result.Status.ToString(),
            ":",
            result.ReasonCode,
            ":",
            result.AgentResult?.Status.ToString() ?? "none",
            ":",
            result.AgentResult?.RunState ?? "none");
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-world-decision-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FixedInputFactory : IWorldAgentRunInputFactory
    {
        public ValueTask<WorldAgentRunInput> CreateAsync(
            WorldAgentJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldAgentRunInput>(
                new WorldAgentRunInput(
                    DateTimeOffset.UtcNow,
                    new AgentBudget
                    {
                        MaxTurns = 4,
                        MaxDurationMs = 30_000,
                        MaxTokens = 100_000,
                        MaxCostUsd = "1",
                        MaxActions = 4
                    }));
        }
    }

    private sealed class JsonFinalProvider : IStreamingModelProvider
    {
        private readonly string _json;
        private int _callCount;

        public JsonFinalProvider(string json)
        {
            _json = json;
        }

        public string ProviderId => "world-decision-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = "submit-" + call,
                ToolNameDelta =
                    FinalOutputAdmissionControl.SubmitToolName,
                ArgumentsJsonDelta =
                    "{\"output\":" + _json + ",\"evidence\":[]}"
            };
            await Task.Yield();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 10,
                    OutputTokens = 5,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "tool_calls"
            };
        }
    }

    private sealed class RejectingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No tool call expected.");
        }
    }

    private sealed class Condition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(true);
        }
    }

    private sealed class Selector : IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventResolution> resolutions = new[]
            {
                new WorldEventResolution(
                    "decision-resolution",
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
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "planned"));
        }
    }
}
