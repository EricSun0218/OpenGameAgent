using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void InMemorySkillSourceRequiresPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryGameSkillSource(Array.Empty<GameSkill>(), 0));
    }

    [Fact]
    public void StructuredGameJsonRejectsDuplicatePropertiesAtAnyDepth()
    {
        Assert.Throws<ArgumentException>(() => Input("event", "{\"nested\":{\"value\":1,\"value\":2}}"));
    }

    [Fact]
    public void PublicWorkflowAndRouteContractsRejectAmbiguousOrNullState()
    {
        Assert.Throws<ArgumentException>(() =>
            new GameRouteDecision(GameRouteKind.Agent, "reason", "unexpected"));
        Assert.Throws<ArgumentException>(() =>
            new GameWorkflowStepResult(GameWorkflowStepStatus.Complete, "{}", new AgentMessage[] { null! }));
        Assert.Throws<ArgumentException>(() =>
            new GameWorkflowResult(new AgentMessage[] { null! }, true));
        Assert.Throws<ArgumentException>(() =>
            new GameWorkflowStepResult(GameWorkflowStepStatus.Complete, "{}", error: "unexpected"));
        Assert.Throws<ArgumentException>(() =>
            new GameWorkflowResult(Array.Empty<AgentMessage>(), false));
        Assert.Throws<ArgumentException>(() =>
            new GameWorkflowCheckpoint("instance", "workflow", 0, 0, "{}", completed: false, error: "unexpected"));
    }

    [Fact]
    public async Task CompletedWorkflowCheckpointCannotBeReopenedOrReassigned()
    {
        var store = new InMemoryGameWorkflowCheckpointStore();
        await store.SaveAsync(
            new GameWorkflowCheckpoint("instance", "one", 1, 1, "{}", completed: true),
            0,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(
                new GameWorkflowCheckpoint("instance", "one", 2, 0, "{}"),
                1,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(
                new GameWorkflowCheckpoint("instance", "two", 2, 0, "{}"),
                1,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StructuredInputRetainsNumbersBooleansAndArrays()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var input = Input("state_delta", "{\"health\":12.75,\"alive\":true,\"cells\":[1,2,3]}");

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(provider.Requests);
        var message = request.Messages.Last();
        var json = Assert.IsType<JsonContent>(Assert.Single(message.Content)).Json;
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("Payload");
        Assert.Equal(12.75, payload.GetProperty("health").GetDouble());
        Assert.True(payload.GetProperty("alive").GetBoolean());
        Assert.Equal(3, payload.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public async Task QuickRouteUsesOneModelTurnAndNoTools()
    {
        var provider = new RecordingProvider(_ => Tools(new ToolCallContent("1", "should_not_run", "{}")));
        var options = new GameAgentRuntimeOptions(provider, "test")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[] { ReadTool("should_not_run") }),
            RoutePolicy = new AutomaticGameRoutePolicy(
                new Dictionary<string, GameRouteDecision>
                {
                    ["chat"] = GameRouteDecision.Quick("typed"),
                }),
        };
        var runtime = new GameAgentRuntime(options);

        var result = await runtime.RunAsync(Input("chat", "{\"text\":\"hello\"}"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(Assert.Single(provider.Requests).Tools);
    }

    [Fact]
    public async Task AgentRouteCommitsDurableActionOnceAndDeduplicatesInput()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("place-1", "place_block", "{\"x\":1,\"y\":2.5}"))
            : Text("placed"));
        var journal = new InMemoryGameActionJournal();
        var handler = new TestActionHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var options = new GameAgentRuntimeOptions(provider, "test")
        {
            ToolProvider = (input, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[]
                {
                    GameActionTool.Create(
                        input,
                        "place_block",
                        "Place a block in the game world.",
                        "{\"type\":\"object\",\"required\":[\"x\",\"y\"],\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"additionalProperties\":false}",
                        dispatcher),
                }),
        };
        var runtime = new GameAgentRuntime(options);
        var input = Input("build_request", "{\"request\":\"tree\"}", inputId: "same-input");

        var first = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var duplicate = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal(GameAgentRunStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, handler.ExecuteCount);
        Assert.Equal(2, provider.CallCount);
        var entry = await journal.FindAsync("same-input:1:0", TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, entry!.Receipt!.Status);
    }

    [Fact]
    public async Task AgentRouteRefreshesWorldContextAfterAToolTurn()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("change", "change_world", "{}"))
            : Text("observed"));
        var revision = 0;
        var handler = new TestActionHandler((intent, _) =>
        {
            revision++;
            return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(
                intent,
                JsonSerializer.Serialize(new { revision })));
        });
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var options = new GameAgentRuntimeOptions(provider, "test")
        {
            ContextProvider = new DelegateContextProvider((_, _) =>
                new ValueTask<IReadOnlyList<GameContextSlice>>(new[]
                {
                    new GameContextSlice("world", JsonSerializer.Serialize(new { revision })),
                })),
            ToolProvider = (input, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                GameActionTool.Create(
                    input,
                    "change_world",
                    "Change the world revision.",
                    "{\"type\":\"object\"}",
                    dispatcher),
            }),
        };

        var result = await new GameAgentRuntime(options).RunAsync(
            Input("change", "{}", "refresh"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("\"revision\":0", provider.Requests.ElementAt(0).SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("\"revision\":1", provider.Requests.ElementAt(1).SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableInstructionsAndSkillsPrecedeMutableWorldContext()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            Instructions = "stable-instructions",
            ContextProvider = new DelegateContextProvider((_, _) =>
                new ValueTask<IReadOnlyList<GameContextSlice>>(new[]
                {
                    new GameContextSlice("world", "{\"revision\":7}"),
                })),
            SkillSource = new InMemoryGameSkillSource(new[]
            {
                new GameSkill("stable-skill", "Stable skill", "", "stable-skill-instructions"),
            }),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "stable-prefix"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var prompt = Assert.Single(provider.Requests).SystemPrompt;
        Assert.True(
            prompt.IndexOf("stable-instructions", StringComparison.Ordinal)
                < prompt.IndexOf("stable-skill-instructions", StringComparison.Ordinal));
        Assert.True(
            prompt.IndexOf("stable-skill-instructions", StringComparison.Ordinal)
                < prompt.IndexOf("\"revision\":7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActiveActorCanBeSteeredWithoutAffectingAnotherActor()
    {
        var provider = new BlockingFirstResponseProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        var input = Input("autonomous", "{}", "steer-input");

        var run = runtime.RunAsync(input, TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;

        Assert.False(runtime.TrySteer(
            new GameSessionKey("session", "other-actor"),
            AgentMessage.UserJson("{\"urgent\":true}")));
        Assert.True(runtime.TrySteer(
            new GameSessionKey("session", "actor"),
            AgentMessage.UserJson("{\"urgent\":true}")));

        provider.ReleaseFirstResponse.SetResult();
        var result = await run;

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(
            provider.Requests.ElementAt(1).Messages,
            message => message.Content.OfType<JsonContent>().Any(content => content.Json.Contains("\"urgent\":true", StringComparison.Ordinal)));
        Assert.False(runtime.TryAbort(new GameSessionKey("session", "actor")));
    }

    [Fact]
    public async Task ActiveActorCanBeAbortedBySessionAndActorKey()
    {
        var provider = new BlockingFirstResponseProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });

        var run = runtime.RunAsync(
            Input("autonomous", "{}", "abort-input"),
            TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;

        Assert.True(runtime.TryAbort(new GameSessionKey("session", "actor")));
        var result = await run;

        Assert.False(result.Succeeded);
        Assert.Equal(AgentRunStatus.Aborted, result.AgentResult!.Status);
        Assert.False(runtime.TryAbort(new GameSessionKey("session", "actor")));
    }

    [Fact]
    public async Task ActionOperationRemainsStableWhenProviderChangesToolCallIdAfterSessionConflict()
    {
        var provider = new RecordingProvider(call => call % 2 == 1
            ? Tools(new ToolCallContent("provider-call-" + call, "place_block", "{\"x\":1}"))
            : Text("placed"));
        var journal = new InMemoryGameActionJournal();
        var handler = new TestActionHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var options = new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = new ConflictOnceSessionStore(),
            ToolProvider = (input, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[]
                {
                    GameActionTool.Create(
                        input,
                        "place_block",
                        "Place a block.",
                        "{\"type\":\"object\",\"required\":[\"x\"],\"properties\":{\"x\":{\"type\":\"number\"}}}",
                        dispatcher),
                }),
        };
        var runtime = new GameAgentRuntime(options);
        var input = Input("build", "{}", "stable-input");

        var conflicted = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var retried = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.SessionConflict, conflicted.Status);
        Assert.True(retried.Succeeded);
        Assert.Equal(1, handler.ExecuteCount);
        Assert.NotNull(await journal.FindAsync("stable-input:1:0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PendingWorkCanPromoteAnOtherwiseQuickInputToAgentRoute()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            PendingWorkProvider = (_, _) => new ValueTask<bool>(true),
        });

        var result = await runtime.RunAsync(Input("tick", "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("tools-or-pending-work", result.Route.Reason);
    }

    [Fact]
    public async Task UncertainDurableActionPreventsLaterWritesInTheSameToolBatch()
    {
        var handler = new TestActionHandler((_, _) => throw new InvalidOperationException("connection lost"));
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var input = Input("build", "{}", "uncertain-batch");
        var provider = new RecordingProvider(_ => Tools(
            new ToolCallContent("first", "first_write", "{}"),
            new ToolCallContent("second", "second_write", "{}")));
        var options = new AgentOptions(provider, "model")
        {
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (_, _) => new ValueTask<bool>(true),
            },
        };
        options.Tools.Add(GameActionTool.Create(
            input,
            "first_write",
            "First write",
            "{\"type\":\"object\"}",
            dispatcher));
        options.Tools.Add(GameActionTool.Create(
            input,
            "second_write",
            "Second write",
            "{\"type\":\"object\"}",
            dispatcher));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Stopped, result.Status);
        Assert.Equal(1, handler.ExecuteCount);
        var tools = result.NewMessages.Where(message => message.Role == AgentRole.Tool).ToArray();
        Assert.Contains("\"status\":\"uncertain\"", Assert.IsType<JsonContent>(Assert.Single(tools[0].Content)).Json, StringComparison.Ordinal);
        Assert.Contains("not executed", Assert.IsType<TextContent>(Assert.Single(tools[1].Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryFailureReturnsUncertainReceiptForLaterReconciliation()
    {
        var journal = new InMemoryGameActionJournal();
        var intent = Intent("recover-failure");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        await journal.MarkDispatchedAsync(intent.OperationId, TestContext.Current.CancellationToken);
        var dispatcher = new DurableGameActionDispatcher(
            journal,
            new TestActionHandler(recover: (_, _) => throw new InvalidOperationException("store offline")));

        var receipt = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Uncertain, receipt.Status);
        Assert.Contains("recovery failed", receipt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatcherRejectsJournalThatLosesDispatchClaim()
    {
        var handler = new TestActionHandler();
        var dispatcher = new DurableGameActionDispatcher(new LostDispatchClaimJournal(), handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.ExecuteAsync(Intent("lost-claim"), TestContext.Current.CancellationToken));

        Assert.Contains("rejected dispatch", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.ExecuteCount);
        Assert.Equal(0, handler.RecoverCount);
    }

    [Fact]
    public async Task ConcurrentIdenticalActionIsExecutedOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestActionHandler(async (intent, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return GameActionReceipt.Committed(intent, "{\"ok\":true}");
        });
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var intent = Intent("same-operation");

        var first = dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken).AsTask();
        release.TrySetResult();

        var receipts = await Task.WhenAll(first, second);
        Assert.All(receipts, receipt => Assert.Equal(GameActionStatus.Committed, receipt.Status));
        Assert.Equal(1, handler.ExecuteCount);
    }

    [Fact]
    public async Task FailedDispatchRemainsPendingUntilGameReconcilesIt()
    {
        var recovered = false;
        var handler = new TestActionHandler(
            (_, _) => throw new InvalidOperationException("connection lost"),
            (intent, _) => new ValueTask<GameActionReceipt?>(
                recovered ? GameActionReceipt.Committed(intent, "{\"recovered\":true}") : null));
        var journal = new InMemoryGameActionJournal();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var intent = Intent("uncertain-operation");

        var uncertain = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);
        var pending = await journal.ListPendingAsync(10, TestContext.Current.CancellationToken);
        recovered = true;
        var receipt = await dispatcher.ReconcileAsync(intent.OperationId, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Uncertain, uncertain.Status);
        Assert.Single(pending);
        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        Assert.Empty(await journal.ListPendingAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparedActionIsExecutedAfterDispatcherRestart()
    {
        var journal = new InMemoryGameActionJournal();
        var intent = Intent("prepared-operation");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        var handler = new TestActionHandler();
        var restarted = new DurableGameActionDispatcher(journal, handler);

        var receipt = await restarted.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        Assert.Equal(1, handler.ExecuteCount);
        Assert.Equal(0, handler.RecoverCount);
    }

    [Fact]
    public async Task DispatchedActionIsRecoveredWithoutBlindReplayAfterDispatcherRestart()
    {
        var journal = new InMemoryGameActionJournal();
        var intent = Intent("dispatched-operation");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.True(await journal.MarkDispatchedAsync(intent.OperationId, TestContext.Current.CancellationToken));
        var handler = new TestActionHandler(
            recover: (candidate, _) => new ValueTask<GameActionReceipt?>(
                GameActionReceipt.Committed(candidate, "{\"recovered\":true}")));
        var restarted = new DurableGameActionDispatcher(journal, handler);

        var receipt = await restarted.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        Assert.Equal(0, handler.ExecuteCount);
        Assert.Equal(1, handler.RecoverCount);
    }

    [Fact]
    public async Task MemoryUsesGameTimeAndPreservesFloatingImportance()
    {
        var store = new InMemoryGameMemoryStore();
        await store.AppendAsync(
            new GameMemory("m1", "session", "npc", "personal", GameMemoryKind.Event, "{\"trust\":0.75}", new GameMoment("world", 12), 0.63, "shared food", new[] { "friend" }),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory("m2", "session", "npc", "personal", GameMemoryKind.Event, "{\"trust\":0.9}", new GameMoment("world", 30), 0.9, "future", new[] { "friend" }),
            TestContext.Current.CancellationToken);

        var result = await store.SearchAsync(
            new GameMemoryQuery("session", 5, ownerId: "npc", tags: new[] { "friend" }, atOrBefore: new GameMoment("world", 20)),
            TestContext.Current.CancellationToken);

        var memory = Assert.Single(result);
        Assert.Equal(0.63, memory.Importance);
        Assert.Contains("0.75", memory.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryExpiryUsesGameTimeAndOptionalRankerNeedsNoBundledModel()
    {
        var lexical = new InMemoryGameMemoryStore();
        await lexical.AppendAsync(
            new GameMemory(
                "old",
                "session",
                "actor",
                "personal",
                GameMemoryKind.Fact,
                "{\"value\":1.25}",
                new GameMoment("world", 2),
                searchableText: "orchard",
                expiresAt: new GameMoment("world", 5),
                metadata: new Dictionary<string, string> { ["perspective"] = "actor" }),
            TestContext.Current.CancellationToken);
        await lexical.AppendAsync(
            new GameMemory(
                "current",
                "session",
                "actor",
                "personal",
                GameMemoryKind.Fact,
                "{\"value\":2.75}",
                new GameMoment("world", 3),
                searchableText: "orchard"),
            TestContext.Current.CancellationToken);
        var ranked = new RankedGameMemoryStore(lexical, new ReverseMemoryRanker());

        var beforeExpiry = await ranked.SearchAsync(
            new GameMemoryQuery("session", 2, text: "orchard", atOrBefore: new GameMoment("world", 4)),
            TestContext.Current.CancellationToken);
        var afterExpiry = await ranked.SearchAsync(
            new GameMemoryQuery("session", 2, text: "orchard", atOrBefore: new GameMoment("world", 5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "old", "current" }, beforeExpiry.Select(memory => memory.MemoryId));
        Assert.Equal("actor", beforeExpiry[0].Metadata["perspective"]);
        Assert.Equal("current", Assert.Single(afterExpiry).MemoryId);
    }

    [Fact]
    public async Task MemoryRankerCannotReplaceCanonicalCandidateContent()
    {
        var source = new InMemoryGameMemoryStore();
        var original = new GameMemory(
            "memory",
            "session",
            "npc",
            "personal",
            GameMemoryKind.Fact,
            "{\"trusted\":true}",
            new GameMoment("world", 1));
        await source.AppendAsync(original, TestContext.Current.CancellationToken);
        var ranked = new RankedGameMemoryStore(source, new ReplacingMemoryRanker());

        var result = await ranked.SearchAsync(
            new GameMemoryQuery("session", 1),
            TestContext.Current.CancellationToken);

        Assert.Same(original, Assert.Single(result));
        Assert.Equal("{\"trusted\":true}", result[0].PayloadJson);
    }

    [Fact]
    public async Task RankedMemoryStoreRejectsCrossSessionCandidatesFromCustomStore()
    {
        var ranked = new RankedGameMemoryStore(new LeakingMemoryStore(), new ReverseMemoryRanker());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ranked.SearchAsync(
                new GameMemoryQuery("requested-session", 1),
                TestContext.Current.CancellationToken));

        Assert.Contains("visibility filters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryLexicalSearchMatchesTermsWithoutRequiringExactPhraseOrEmbeddingModel()
    {
        var store = new InMemoryGameMemoryStore();
        await store.AppendAsync(
            new GameMemory(
                "orchard",
                "session",
                "npc",
                "personal",
                GameMemoryKind.Event,
                "{\"place\":\"orchard\"}",
                new GameMoment("world", 1),
                searchableText: "an apple grows beside the old orchard gate"),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory(
                "market",
                "session",
                "npc",
                "personal",
                GameMemoryKind.Event,
                "{\"place\":\"market\"}",
                new GameMoment("world", 2),
                searchableText: "an apple was sold"),
            TestContext.Current.CancellationToken);

        var result = await store.SearchAsync(
            new GameMemoryQuery("session", 5, text: "orchard apple"),
            TestContext.Current.CancellationToken);

        Assert.Equal("orchard", result[0].MemoryId);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GameTimeSchedulerEmitsDeterministicRecurringOccurrences()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger(
            "monthly",
            "session",
            "month_elapsed",
            "{\"economy\":true}",
            new GameMoment("world", 30),
            intervalTicks: 30,
            maximumOccurrences: 3));

        var first = scheduler.Advance("session", new GameMoment("world", 0), new GameMoment("world", 65), 10);
        var second = scheduler.Advance("session", new GameMoment("world", 65), new GameMoment("world", 100), 10);

        Assert.Equal(new long[] { 30, 60 }, first.Select(item => item.Due.Tick));
        Assert.Equal(90, Assert.Single(second).Due.Tick);
        Assert.False(scheduler.Cancel("monthly"));
    }

    [Fact]
    public async Task MultiActorSchedulerSerializesEachActorAndRunsDifferentActorsConcurrently()
    {
        var scheduler = new MultiActorScheduler(2, 4, 4);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        entered["a"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entered["b"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAStarted = false;

        ValueTask<int> Block(string actor, CancellationToken cancellationToken) => BlockCore(actor, cancellationToken);
        async ValueTask<int> BlockCore(string actor, CancellationToken cancellationToken)
        {
            entered[actor].TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return 1;
        }

        var firstA = scheduler.EnqueueAsync("a", token => Block("a", token), TestContext.Current.CancellationToken);
        var firstB = scheduler.EnqueueAsync("b", token => Block("b", token), TestContext.Current.CancellationToken);
        var secondA = scheduler.EnqueueAsync("a", _ =>
        {
            secondAStarted = true;
            return new ValueTask<int>(2);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(
            entered["a"].Task.WaitAsync(TestContext.Current.CancellationToken),
            entered["b"].Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.False(secondAStarted);
        release.TrySetResult();
        Assert.Equal(new[] { 1, 1, 2 }, await Task.WhenAll(firstA, firstB, secondA));
        Assert.True(secondAStarted);
    }

    [Fact]
    public async Task RuntimeActorSchedulingKeysCannotCollideThroughIdentifierDelimiters()
    {
        var provider = new TwoRequestBarrierProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            Limits = new GameRuntimeLimits { MaxConcurrentActors = 2 },
        });
        var first = runtime.RunAsync(
            new GameInput("a\nb", "c", "chat", "{}", new GameMoment("world", 1), "first"),
            TestContext.Current.CancellationToken);
        var second = runtime.RunAsync(
            new GameInput("a", "b\nc", "chat", "{}", new GameMoment("world", 1), "second"),
            TestContext.Current.CancellationToken);

        var timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var started = await Task.WhenAny(provider.TwoRequestsStarted.Task, timeout);
        provider.Release.TrySetResult();

        Assert.Same(provider.TwoRequestsStarted.Task, started);
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.Succeeded));
    }

    [Fact]
    public async Task QueuedActorWorkObservesCancellationWithoutWaitingForTheLane()
    {
        var scheduler = new MultiActorScheduler(1, 2, 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.EnqueueAsync("actor", async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var queued = scheduler.EnqueueAsync("actor", _ => new ValueTask<int>(2), cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queued.WaitAsync(TestContext.Current.CancellationToken));
        release.TrySetResult();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task SkillsAreSelectedByInputTypeToolsAndPriority()
    {
        var source = new InMemoryGameSkillSource(new[]
        {
            new GameSkill("low", "Low", "", "", new[] { "chat" }, priority: 1),
            new GameSkill("build", "Build", "", "", new[] { "build" }, new[] { "place" }, priority: 10),
            new GameSkill("missing-tool", "Missing", "", "", new[] { "build" }, new[] { "destroy" }, priority: 20),
        });

        var selected = await source.SelectAsync(
            new GameSkillQuery(Input("build", "{}"), new[] { "place" }, 5),
            TestContext.Current.CancellationToken);

        Assert.Equal("build", Assert.Single(selected).SkillId);
    }

    [Fact]
    public async Task SessionStoreRejectsStaleRevision()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        var first = await store.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);
        var stale = await store.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);

        Assert.True(first.Saved);
        Assert.False(stale.Saved);
        Assert.Equal(1, stale.Current.Revision);
    }

    [Fact]
    public async Task RuntimeRejectsSessionStoreThatClaimsToSaveDifferentState()
    {
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RecordingProvider(_ => Text("ok")),
            "test")
        {
            SessionStore = new CorruptingSavedSessionStore(),
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(Input("chat", "{}"), TestContext.Current.CancellationToken));

        Assert.Contains("different saved snapshot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeRejectsSessionHistoryBeyondConfiguredDeduplicationCapacity()
    {
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RecordingProvider(_ => Text("unused")),
            "test")
        {
            SessionStore = new OversizedHistorySessionStore(),
            RecentProcessedInputCapacity = 2,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(Input("chat", "{}"), TestContext.Current.CancellationToken));

        Assert.Contains("retention capacity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryingProviderRetriesOnlyBeforeAnyStreamOutput()
    {
        var attempts = 0;
        var inner = new RecordingProvider(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("temporary");
            }

            return Text("ok");
        });
        var provider = new RetryingModelProvider(inner, 2, _ => TimeSpan.Zero);

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, attempts);
        Assert.Equal(ModelStopReason.Stop, events.Last().Response!.StopReason);
    }

    [Fact]
    public async Task RetryingProviderRetriesEmptyStartupAndDisposesEveryAttempt()
    {
        var inner = new EmptyThenResponseProvider();
        var provider = new RetryingModelProvider(inner, 2, _ => TimeSpan.Zero);

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(2, inner.DisposeCount);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(events.Last().Response!.Content)).Text);
    }

    [Fact]
    public async Task RetryingProviderCanRetryFailureAfterOnlyStartMetadata()
    {
        var inner = new StartThenFailureProvider(succeedOnCall: 2);
        var provider = new RetryingModelProvider(inner, 2, _ => TimeSpan.Zero);

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(2, events.Count(item => item.Kind == ModelStreamEventKind.Started));
        Assert.True(events.Last().IsTerminal);
    }

    [Fact]
    public async Task FallbackProviderUsesNextProviderBeforeOutput()
    {
        var first = new RecordingProvider(_ => throw new InvalidOperationException("offline"));
        var second = new RecordingProvider(_ => Text("ok"));
        var provider = new FallbackModelProvider(new IModelProvider[] { first, second });

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(events.Last().Response!.Content)).Text);
    }

    [Fact]
    public async Task FallbackProviderCanSwitchAfterOnlyStartMetadata()
    {
        var first = new StartThenFailureProvider(succeedOnCall: int.MaxValue);
        var second = new RecordingProvider(_ => Text("ok"));
        var provider = new FallbackModelProvider(new IModelProvider[] { first, second });

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        Assert.True(events.Last().IsTerminal);
    }

    [Fact]
    public async Task ModelRouteClassifierAcceptsOnlyKnownStructuredRoutes()
    {
        var valid = new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("{\"route\":\"workflow\",\"workflow\":\"month\",\"reason\":\"scheduled\"}")),
            "model",
            new[] { "month" });
        var invalid = new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("{\"route\":\"workflow\",\"workflow\":\"unknown\"}")),
            "model",
            new[] { "month" });
        var context = new GameRouteContext(Input("tick", "{}"), 0);

        var accepted = await valid.ClassifyAsync(context, TestContext.Current.CancellationToken);
        var rejected = await invalid.ClassifyAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Workflow, accepted!.Route);
        Assert.Equal("month", accepted.Workflow);
        Assert.Null(rejected);
    }

    [Fact]
    public async Task ModelRouteClassifierRejectsAmbiguousTextJson()
    {
        var classifier = new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("{\"route\":\"quick\",\"route\":\"agent\"}")),
            "model");

        var decision = await classifier.ClassifyAsync(
            new GameRouteContext(Input("chat", "{}"), 0),
            TestContext.Current.CancellationToken);

        Assert.Null(decision);
    }

    [Fact]
    public void RouteConfigurationRejectsNullDecisionsAndInvalidWorkflowNames()
    {
        Assert.Throws<ArgumentException>(() => new AutomaticGameRoutePolicy(
            new Dictionary<string, GameRouteDecision> { ["chat"] = null! }));
        Assert.Throws<ArgumentException>(() => new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("{}")),
            "model",
            new[] { " " }));
    }

    [Fact]
    public async Task MediaToolStreamsProgressAndReturnsResourceContent()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("media-call", "generate_portrait", "{\"style\":\"ink\"}"))
            : Text("ready"));
        var generator = new TestMediaGenerator();
        var input = Input("portrait", "{\"character\":\"hero\"}", "media-input");
        var tool = GameMediaGenerationTool.Create(
            input,
            "generate_portrait",
            "Generate a portrait",
            "{\"type\":\"object\",\"properties\":{\"style\":{\"type\":\"string\"}},\"required\":[\"style\"]}",
            generator,
            (_, arguments, execution) => new GameMediaGenerationRequest(
                input.InputId + ":" + execution.Call.Id,
                GameMediaKind.Image,
                input.PayloadJson,
                arguments.GetRawText()));
        var options = new AgentOptions(provider, "model");
        options.Tools.Add(tool);
        var agent = new Agent(options);
        var progress = new List<ToolProgress>();
        agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent.Progress is not null)
            {
                progress.Add(agentEvent.Progress);
            }

            return default;
        });

        var result = await agent.RunAsync(AgentMessage.UserJson("{}"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0.5, Assert.Single(progress).Fraction);
        var toolMessage = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        var resource = Assert.Single(toolMessage.Content.OfType<ResourceContent>());
        Assert.Equal("image/png", resource.MediaType);
        Assert.Contains("ink", generator.ParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscriptCompactionPreservesCompleteToolExchange()
    {
        var call = new ToolCallContent("call", "act", "{}");
        var toolResult = new ToolResult(new AgentContent[] { new TextContent("ok") });
        var messages = new AgentMessage[]
        {
            AgentMessage.User("old"),
            Assistant("old answer"),
            AgentMessage.User("keep"),
            new(AgentRole.Assistant, new AgentContent[] { call }, DateTimeOffset.UnixEpoch, model: "m", stopReason: ModelStopReason.ToolUse),
            AgentMessage.ToolResult(call, toolResult, DateTimeOffset.UnixEpoch),
            Assistant("after tool"),
            AgentMessage.User("latest"),
            Assistant("latest answer"),
        };
        var compactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
            new ValueTask<string>("summary:" + removed.Count));

        var compacted = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), messages, 7),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, compacted.Count);
        Assert.Equal("transcript_summary", compacted[0].CustomRole);
        Assert.Contains(compacted, message => message.Content.OfType<ToolCallContent>().Any(item => item.Id == "call"));
        Assert.Contains(compacted, message => message.Role == AgentRole.Tool && message.ToolCallId == "call");
    }

    [Fact]
    public async Task TranscriptCompactionCanSummarizeTheEntireTranscript()
    {
        var call = new ToolCallContent("call", "act", "{}");
        var messages = new AgentMessage[]
        {
            AgentMessage.User("old"),
            new(AgentRole.Assistant, new AgentContent[] { call }, DateTimeOffset.UnixEpoch, model: "m", stopReason: ModelStopReason.ToolUse),
            AgentMessage.ToolResult(call, new ToolResult(new AgentContent[] { new TextContent("ok") }), DateTimeOffset.UnixEpoch),
            Assistant("finished"),
        };
        IReadOnlyList<AgentMessage>? summarized = null;
        var compactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
        {
            summarized = removed;
            return new ValueTask<string>("complete summary");
        });

        var compacted = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), messages, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(messages, summarized);
        var summary = Assert.Single(compacted);
        Assert.Equal("transcript_summary", summary.CustomRole);
        Assert.Equal("complete summary", Assert.IsType<TextContent>(Assert.Single(summary.Content)).Text);
    }

    [Fact]
    public async Task RuntimeCompactsEarlyEnoughForToolTurnResults()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        var history = new AgentMessage[]
        {
            AgentMessage.User("one"),
            Assistant("one"),
            AgentMessage.User("two"),
            Assistant("two"),
            AgentMessage.User("three"),
            Assistant("three"),
        };
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, history),
            0,
            TestContext.Current.CancellationToken);
        var compacted = false;
        var provider = new RecordingProvider(_ => Text("done"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[] { ReadTool("inspect") }),
            AgentLimits = new AgentLimits
            {
                MaxMessages = 10,
                MaxToolCallsPerTurn = 2,
            },
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
            {
                compacted = true;
                return new ValueTask<string>("summary:" + removed.Count);
            }),
        });

        var result = await runtime.RunAsync(
            Input("build_request", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(compacted);
        Assert.Equal(7, (await store.LoadAsync(key, TestContext.Current.CancellationToken))!.Messages.Count);
    }

    [Fact]
    public async Task RuntimeRejectsNullTranscriptCompactionResultClearly()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(
                key,
                1,
                Enumerable.Range(0, 7).Select(index => AgentMessage.User(index.ToString())).ToArray()),
            0,
            TestContext.Current.CancellationToken);
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("unused")), "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 8 },
            TranscriptCompactor = new NullTranscriptCompactor(),
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(Input("chat", "{}"), TestContext.Current.CancellationToken));

        Assert.Contains("compactor returned null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSelectedSkillsAreRejectedBeforePromptSerialization()
    {
        var provider = new RecordingProvider(_ => Text("should not run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SkillSource = new InMemoryGameSkillSource(
                new[] { new GameSkill("large", "Large", "description", new string('x', 64)) }),
            Limits = new GameRuntimeLimits { MaxSkillCharactersPerRun = 16 },
        });

        var exception = await Assert.ThrowsAsync<GameRuntimeLimitException>(async () =>
            await runtime.RunAsync(Input("chat", "{}"), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(GameRuntimeLimits.MaxSkillCharactersPerRun), exception.Limit);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task DurableWorkflowWaitsAndResumesFromCheckpoint()
    {
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var workflow = new DurableGameWorkflow(
            "evolve",
            new[]
            {
                new GameWorkflowStep("wait_for_world", (context, _) =>
                    new ValueTask<GameWorkflowStepResult>(
                        context.StateJson.Contains("ready", StringComparison.Ordinal)
                            ? GameWorkflowStepResult.Next("{\"advanced\":true}", Assistant("advanced"))
                            : GameWorkflowStepResult.Wait("{\"ready\":true}", Assistant("waiting")))),
                new GameWorkflowStep("finish", (_, _) =>
                    new ValueTask<GameWorkflowStepResult>(GameWorkflowStepResult.Complete("{\"done\":true}", Assistant("done")))),
            },
            checkpoints);
        var metadata = new Dictionary<string, string> { ["agent.workflow_instance"] = "month-12" };
        var session = new GameSessionSnapshot(new GameSessionKey("session", "actor"), 0);
        var firstInput = new GameInput("session", "actor", "month", "{}", new GameMoment("world", 12), "first", metadata);
        var secondInput = new GameInput("session", "actor", "month", "{}", new GameMoment("world", 13), "second", metadata);

        var first = await workflow.RunAsync(
            new GameWorkflowContext(firstInput, Array.Empty<GameContextSlice>(), Array.Empty<AgentTool>(), session),
            TestContext.Current.CancellationToken);
        var second = await workflow.RunAsync(
            new GameWorkflowContext(secondInput, Array.Empty<GameContextSlice>(), Array.Empty<AgentTool>(), session),
            TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal("waiting", Assert.IsType<TextContent>(Assert.Single(Assert.Single(first.Messages).Content)).Text);
        Assert.True(second.Succeeded);
        Assert.Equal(new[] { "advanced", "done" }, second.Messages.Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
    }

    [Fact]
    public async Task DurableWorkflowInstanceKeysCannotCollideThroughIdentifierDelimiters()
    {
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var executions = 0;
        var workflow = new DurableGameWorkflow(
            "flow",
            new[]
            {
                new GameWorkflowStep("finish", (_, _) =>
                {
                    Interlocked.Increment(ref executions);
                    return new ValueTask<GameWorkflowStepResult>(GameWorkflowStepResult.Complete("{}"));
                }),
            },
            checkpoints);
        var metadata = new Dictionary<string, string> { ["agent.workflow_instance"] = "instance" };
        var firstInput = new GameInput("a:b", "c", "event", "{}", new GameMoment("world", 1), "one", metadata);
        var secondInput = new GameInput("a", "b:c", "event", "{}", new GameMoment("world", 1), "two", metadata);

        await workflow.RunAsync(
            new GameWorkflowContext(
                firstInput,
                Array.Empty<GameContextSlice>(),
                Array.Empty<AgentTool>(),
                new GameSessionSnapshot(new GameSessionKey(firstInput.SessionId, firstInput.ActorId), 0)),
            TestContext.Current.CancellationToken);
        await workflow.RunAsync(
            new GameWorkflowContext(
                secondInput,
                Array.Empty<GameContextSlice>(),
                Array.Empty<AgentTool>(),
                new GameSessionSnapshot(new GameSessionKey(secondInput.SessionId, secondInput.ActorId), 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task DurableWorkflowRejectsCheckpointStoreThatClaimsToSaveDifferentState()
    {
        var workflow = new DurableGameWorkflow(
            "flow",
            new[]
            {
                new GameWorkflowStep("finish", (_, _) =>
                    new ValueTask<GameWorkflowStepResult>(GameWorkflowStepResult.Complete("{\"done\":true}"))),
            },
            new CorruptingCheckpointStore());
        var input = Input("event", "{}", "checkpoint-corruption");
        var context = new GameWorkflowContext(
            input,
            Array.Empty<GameContextSlice>(),
            Array.Empty<AgentTool>(),
            new GameSessionSnapshot(new GameSessionKey(input.SessionId, input.ActorId), 0));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflow.RunAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("different saved checkpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableWorkflowRejectsCheckpointLoadedForWrongIdentityOrPosition(bool wrongIdentity)
    {
        var workflow = new DurableGameWorkflow(
            "flow",
            new[]
            {
                new GameWorkflowStep("finish", (_, _) =>
                    new ValueTask<GameWorkflowStepResult>(GameWorkflowStepResult.Complete("{}"))),
            },
            new LoadingCheckpointStore((instanceId) => new GameWorkflowCheckpoint(
                wrongIdentity ? instanceId + "-other" : instanceId,
                "flow",
                1,
                wrongIdentity ? 0 : 2,
                "{}")));
        var input = Input("event", "{}", "invalid-loaded-checkpoint");
        var context = new GameWorkflowContext(
            input,
            Array.Empty<GameContextSlice>(),
            Array.Empty<AgentTool>(),
            new GameSessionSnapshot(new GameSessionKey(input.SessionId, input.ActorId), 0));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflow.RunAsync(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DurableWorkflowDoesNotAdvanceCheckpointWhenOutputIsRejected()
    {
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var workflow = new DurableGameWorkflow(
            "evolve",
            new[]
            {
                new GameWorkflowStep("oversized", (_, _) =>
                    new ValueTask<GameWorkflowStepResult>(
                        GameWorkflowStepResult.Complete("{}", Assistant(new string('x', 32))))),
            },
            checkpoints);
        var options = new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("unused")), "model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["month"] = GameRouteDecision.ToWorkflow("evolve", "fixed"),
            }),
            AgentLimits = new AgentLimits { MaxTextCharactersPerPart = 8 },
        };
        options.Workflows.Add(workflow);
        var runtime = new GameAgentRuntime(options);
        var input = new GameInput(
            "session",
            "actor",
            "month",
            "{}",
            new GameMoment("world", 12),
            "invalid");

        await Assert.ThrowsAsync<AgentLimitException>(async () =>
            await runtime.RunAsync(input, TestContext.Current.CancellationToken));

        Assert.Null(await checkpoints.LoadAsync(
            "session:actor:evolve:invalid",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MailboxUsesOperationalLeaseButRetainsGameMoment()
    {
        var mailbox = new InMemoryGameMailbox();
        var message = new GameMailboxMessage(
            "mail",
            "session",
            "npc-b",
            "npc_message",
            "{\"trust\":0.25}",
            new GameMoment("world", 50),
            senderId: "npc-a");
        await mailbox.EnqueueAsync(message, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UnixEpoch;

        var first = Assert.Single(await mailbox.ClaimAsync(
            "session", "npc-b", 1, now, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(await mailbox.ClaimAsync(
            "session", "npc-b", 1, now.AddSeconds(5), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var retried = Assert.Single(await mailbox.ClaimAsync(
            "session", "npc-b", 1, now.AddSeconds(11), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await mailbox.CompleteAsync("mail", retried.LeaseToken, TestContext.Current.CancellationToken);

        Assert.Equal(50, first.Message.Moment.Tick);
        Assert.Equal(2, retried.Attempt);
        Assert.Empty(await mailbox.ClaimAsync(
            "session", "npc-b", 1, now.AddMinutes(1), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MailboxRejectsOverflowingOperationalLeaseWithoutMutatingDelivery()
    {
        var mailbox = new InMemoryGameMailbox();
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("mail", "session", "npc", "event", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await mailbox.ClaimAsync(
                "session",
                "npc",
                1,
                DateTimeOffset.MaxValue,
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));
        var delivery = Assert.Single(await mailbox.ClaimAsync(
            "session",
            "npc",
            1,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, delivery.Attempt);
    }

    [Fact]
    public void SchedulerCatchesUpAnOverdueTriggerExactlyOnce()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger("past", "session", "event", "{}", new GameMoment("world", 10)));

        var due = scheduler.Advance("session", new GameMoment("world", 20), new GameMoment("world", 30), 10);

        Assert.Equal(10, Assert.Single(due).Due.Tick);
        Assert.Empty(scheduler.Advance("session", new GameMoment("world", 30), new GameMoment("world", 40), 10));
    }

    [Fact]
    public void ZeroOccurrenceAdvanceDoesNotConsumeDueTriggers()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger("due", "session", "event", "{}", new GameMoment("world", 10)));

        Assert.Empty(scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 10),
            maximumOccurrences: 0));
        Assert.Single(scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 10),
            maximumOccurrences: 1));
    }

    [Fact]
    public void SchedulerStateResumesRecurringGameTimeWithoutDuplicateOccurrences()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger(
            "month",
            "session",
            "month_elapsed",
            "{\"season\":1}",
            new GameMoment("world", 10),
            intervalTicks: 10,
            maximumOccurrences: 4));
        var beforeSave = scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 20),
            10);

        var restored = new GameTimeScheduler(scheduler.CaptureState());
        var afterLoad = restored.Advance(
            "session",
            new GameMoment("world", 20),
            new GameMoment("world", 40),
            10);

        Assert.Equal(new long[] { 10, 20 }, beforeSave.Select(item => item.Due.Tick));
        Assert.Equal(new long[] { 30, 40 }, afterLoad.Select(item => item.Due.Tick));
        Assert.Empty(restored.CaptureState());
    }

    [Fact]
    public void SchedulerStateRoundTripsThroughJsonSaveData()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger(
            "monthly",
            "session",
            "month_advance",
            "{\"taxRate\":0.125}",
            new GameMoment("world", 10, "{\"month\":1}"),
            intervalTicks: 10));
        _ = scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 10),
            maximumOccurrences: 1);

        var json = JsonSerializer.Serialize(scheduler.CaptureState());
        var saved = JsonSerializer.Deserialize<ScheduledGameTriggerState[]>(json);
        var restored = new GameTimeScheduler(saved!);
        var resumed = restored.Advance(
            "session",
            new GameMoment("world", 10),
            new GameMoment("world", 20),
            maximumOccurrences: 1);

        var occurrence = Assert.Single(resumed);
        Assert.Equal(2, occurrence.Occurrence);
        Assert.Equal(20, occurrence.Due.Tick);
        Assert.Contains("0.125", occurrence.Trigger.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerPreservesKnownCalendarDataWithoutInventingFutureCalendars()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger(
            "monthly",
            "session",
            "month_advance",
            "{}",
            new GameMoment("world", 10, "{\"month\":1}"),
            intervalTicks: 10));

        var captured = Assert.Single(scheduler.CaptureState());
        Assert.Equal("{\"month\":1}", captured.NextDue.CalendarJson);
        var first = Assert.Single(scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 10, "{\"month\":1}"),
            1));
        Assert.Equal("{\"month\":1}", first.Due.CalendarJson);
        Assert.Null(Assert.Single(scheduler.CaptureState()).NextDue.CalendarJson);

        var second = Assert.Single(scheduler.Advance(
            "session",
            new GameMoment("world", 10),
            new GameMoment("world", 20, "{\"month\":2}"),
            1));
        Assert.Equal("{\"month\":2}", second.Due.CalendarJson);
    }

    [Fact]
    public void ScheduledOccurrenceRejectsPositionsThatDoNotMatchItsTrigger()
    {
        var trigger = new ScheduledGameTrigger(
            "monthly",
            "session",
            "month_advance",
            "{}",
            new GameMoment("world", 10),
            intervalTicks: 10);

        Assert.Throws<ArgumentException>(() =>
            new ScheduledGameOccurrence(trigger, 2, new GameMoment("world", 25)));
    }

    [Fact]
    public async Task RuntimeBoundsCalendarAndAllSkillPromptContent()
    {
        var limits = new GameRuntimeLimits
        {
            MaxCalendarJsonCharacters = 8,
            MaxSkillCharactersPerRun = 16,
        };
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RecordingProvider(_ => Text("unused")),
            "test")
        {
            Limits = limits,
            SkillSource = new InMemoryGameSkillSource(new[]
            {
                new GameSkill("skill", "skill", "", "", metadata: new Dictionary<string, string>
                {
                    ["large"] = new string('x', 32),
                }),
            }),
        });

        await Assert.ThrowsAsync<GameRuntimeLimitException>(() => runtime.RunAsync(
            new GameInput(
                "session",
                "actor",
                "chat",
                "{}",
                new GameMoment("world", 1, "{\"value\":123}")),
            TestContext.Current.CancellationToken));

        var skillOnly = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RecordingProvider(_ => Text("unused")),
            "test")
        {
            Limits = new GameRuntimeLimits { MaxSkillCharactersPerRun = 16 },
            SkillSource = new InMemoryGameSkillSource(new[]
            {
                new GameSkill("skill", "skill", "", "", metadata: new Dictionary<string, string>
                {
                    ["large"] = new string('x', 32),
                }),
            }),
        });
        await Assert.ThrowsAsync<GameRuntimeLimitException>(() => skillOnly.RunAsync(
            Input("chat", "{}", "skill-limit"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SchedulerLimitFailureDoesNotPartiallyAdvanceState()
    {
        var scheduler = new GameTimeScheduler();
        scheduler.Schedule(new ScheduledGameTrigger(
            "monthly",
            "session",
            "month_elapsed",
            "{}",
            new GameMoment("world", 10),
            intervalTicks: 10));

        Assert.Throws<GameRuntimeLimitException>(() => scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 30),
            maximumOccurrences: 2));

        var recovered = scheduler.Advance(
            "session",
            new GameMoment("world", 0),
            new GameMoment("world", 30),
            maximumOccurrences: 3);
        Assert.Equal(new long[] { 10, 20, 30 }, recovered.Select(item => item.Due.Tick));
        Assert.Equal(new[] { 1, 2, 3 }, recovered.Select(item => item.Occurrence));
    }

    private static GameInput Input(string type, string json, string? inputId = null) =>
        new("session", "actor", type, json, new GameMoment("world", 10), inputId);

    private static GameActionIntent Intent(string operationId) =>
        new(operationId, "input", "session", "actor", "act", "{}", new GameMoment("world", 10));

    private static AgentTool ReadTool(string name) =>
        new(
            new ToolDefinition(name, "test", "{\"type\":\"object\"}"),
            (_, _, _) => new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("ok") })));

    private static ModelResponse Text(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop, new ModelUsage(1, 1));

    private static ModelResponse Tools(params ToolCallContent[] calls) =>
        new(calls, ModelStopReason.ToolUse, new ModelUsage(1, 1));

    private static ModelRequest ModelRequest() =>
        new("model", "", Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static AgentMessage Assistant(string text) =>
        new(
            AgentRole.Assistant,
            new AgentContent[] { new TextContent(text) },
            DateTimeOffset.UnixEpoch,
            model: "model",
            stopReason: ModelStopReason.Stop,
            usage: new ModelUsage());

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly Func<int, ModelResponse> _handler;
        private int _calls;

        public RecordingProvider(Func<int, ModelResponse> handler)
        {
            _handler = handler;
        }

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _calls);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(_handler(call));
        }
    }

    private sealed class BlockingFirstResponseProvider : IModelProvider
    {
        private int _calls;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstRequestStarted.SetResult();
                await ReleaseFirstResponse.Task.WaitAsync(cancellationToken);
                yield return ModelStreamEvent.Terminal(Text("working"));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(Text("updated"));
        }
    }

    private sealed class TwoRequestBarrierProvider : IModelProvider
    {
        private int _requests;

        public TaskCompletionSource TwoRequestsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            if (Interlocked.Increment(ref _requests) == 2)
            {
                TwoRequestsStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
            yield return ModelStreamEvent.Terminal(Text("done"));
        }
    }

    private sealed class DelegateContextProvider : IGameContextProvider
    {
        private readonly Func<GameInput, CancellationToken, ValueTask<IReadOnlyList<GameContextSlice>>> _getContext;

        public DelegateContextProvider(
            Func<GameInput, CancellationToken, ValueTask<IReadOnlyList<GameContextSlice>>> getContext)
        {
            _getContext = getContext;
        }

        public ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
            GameInput input,
            CancellationToken cancellationToken) => _getContext(input, cancellationToken);
    }

    private sealed class ConflictOnceSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();
        private int _saveCount;

        public ValueTask<GameSessionSnapshot?> LoadAsync(GameSessionKey key, CancellationToken cancellationToken) =>
            _inner.LoadAsync(key, cancellationToken);

        public async ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saveCount) == 1)
            {
                var competing = await _inner.SaveAsync(
                    new GameSessionSnapshot(snapshot.Key, checked(expectedRevision + 1)),
                    expectedRevision,
                    cancellationToken);
                return new GameSessionSaveResult(false, competing.Current);
            }

            return await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }
    }

    private sealed class CorruptingSavedSessionStore : IGameSessionStore
    {
        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameSessionSnapshot?>((GameSessionSnapshot?)null);
        }

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            _ = expectedRevision;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameSessionSaveResult>(new GameSessionSaveResult(
                true,
                new GameSessionSnapshot(snapshot.Key, snapshot.Revision)));
        }
    }

    private sealed class OversizedHistorySessionStore : IGameSessionStore
    {
        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameSessionSnapshot?>(new GameSessionSnapshot(
                key,
                3,
                processedInputIds: new[] { "one", "two", "three" }));
        }

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Save must not run.");
    }

    private sealed class EmptyThenResponseProvider : IModelProvider
    {
        private int _calls;
        private int _disposeCount;

        public int CallCount => Volatile.Read(ref _calls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            var call = Interlocked.Increment(ref _calls);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                if (call == 1)
                {
                    yield break;
                }

                yield return ModelStreamEvent.Terminal(Text("ok"));
            }
            finally
            {
                Interlocked.Increment(ref _disposeCount);
            }
        }
    }

    private sealed class StartThenFailureProvider : IModelProvider
    {
        private readonly int _succeedOnCall;
        private int _calls;

        public StartThenFailureProvider(int succeedOnCall)
        {
            _succeedOnCall = succeedOnCall;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
            await Task.Yield();
            if (call < _succeedOnCall)
            {
                throw new InvalidOperationException("connection dropped before content");
            }

            yield return ModelStreamEvent.Terminal(Text("ok"));
        }
    }

    private sealed class TestMediaGenerator : IGameMediaGenerator
    {
        public string ParametersJson { get; private set; } = string.Empty;

        public async ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            ParametersJson = request.ParametersJson;
            if (progress is not null)
            {
                await progress(
                    new GameMediaGenerationProgress("rendering", 0.5, "{\"frame\":1}"),
                    cancellationToken);
            }

            return new GameMediaGenerationResult(
                new[] { new ResourceContent("game://generated/portrait.png", "image/png", "portrait") },
                "{\"seed\":7}",
                "provider-request");
        }
    }

    private sealed class ReverseMemoryRanker : IGameMemoryRanker
    {
        public ValueTask<IReadOnlyList<GameMemory>> RankAsync(
            GameMemoryQuery query,
            IReadOnlyList<GameMemory> candidates,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<GameMemory>>(candidates.Reverse().ToArray());
        }
    }

    private sealed class LeakingMemoryStore : IGameMemoryStore
    {
        public ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Append must not run.");

        public ValueTask<IReadOnlyList<GameMemory>> SearchAsync(
            GameMemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GameMemory> leaked = new[]
            {
                new GameMemory(
                    "leaked",
                    "other-session",
                    "npc",
                    "personal",
                    GameMemoryKind.Fact,
                    "{}",
                    new GameMoment("world", 1)),
            };
            return new ValueTask<IReadOnlyList<GameMemory>>(leaked);
        }
    }

    private sealed class ReplacingMemoryRanker : IGameMemoryRanker
    {
        public ValueTask<IReadOnlyList<GameMemory>> RankAsync(
            GameMemoryQuery query,
            IReadOnlyList<GameMemory> candidates,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            var source = Assert.Single(candidates);
            IReadOnlyList<GameMemory> replacement = new[]
            {
                new GameMemory(
                    source.MemoryId,
                    source.SessionId,
                    source.OwnerId,
                    source.Scope,
                    source.Kind,
                    "{\"trusted\":false}",
                    source.Moment),
            };
            return new ValueTask<IReadOnlyList<GameMemory>>(replacement);
        }
    }

    private sealed class TestActionHandler : IGameActionHandler
    {
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>> _execute;
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>> _recover;
        private int _executeCount;
        private int _recoverCount;

        public TestActionHandler(
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>>? execute = null,
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>>? recover = null)
        {
            _execute = execute ?? ((intent, _) => new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{\"ok\":true}")));
            _recover = recover ?? ((_, _) => new ValueTask<GameActionReceipt?>((GameActionReceipt?)null));
        }

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public int RecoverCount => Volatile.Read(ref _recoverCount);

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            return _execute(intent, cancellationToken);
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _recoverCount);
            return _recover(intent, cancellationToken);
        }
    }

    private sealed class LostDispatchClaimJournal : IGameActionJournal
    {
        private GameActionIntent? _intent;

        public ValueTask<GameActionJournalEntry> ReserveAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _intent = intent;
            return new ValueTask<GameActionJournalEntry>(
                new GameActionJournalEntry(intent, null, created: true, dispatched: false));
        }

        public ValueTask<GameActionJournalEntry?> FindAsync(
            string operationId,
            CancellationToken cancellationToken)
        {
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameActionJournalEntry?>(new GameActionJournalEntry(
                _intent ?? throw new InvalidOperationException("Intent was not reserved."),
                null,
                created: false,
                dispatched: false));
        }

        public ValueTask<bool> MarkDispatchedAsync(
            string operationId,
            CancellationToken cancellationToken)
        {
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(false);
        }

        public ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A receipt must not be saved.");

        public ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            _ = limit;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<GameActionIntent>>(Array.Empty<GameActionIntent>());
        }
    }

    private sealed class CorruptingCheckpointStore : IGameWorkflowCheckpointStore
    {
        public ValueTask<GameWorkflowCheckpoint?> LoadAsync(
            string instanceId,
            CancellationToken cancellationToken)
        {
            _ = instanceId;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameWorkflowCheckpoint?>((GameWorkflowCheckpoint?)null);
        }

        public ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
            GameWorkflowCheckpoint checkpoint,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            _ = expectedRevision;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameWorkflowCheckpointSaveResult>(new GameWorkflowCheckpointSaveResult(
                true,
                new GameWorkflowCheckpoint(
                    checkpoint.InstanceId,
                    checkpoint.Workflow,
                    checkpoint.Revision,
                    checkpoint.NextStep,
                    "{}",
                    checkpoint.Completed,
                    checkpoint.Error)));
        }
    }

    private sealed class LoadingCheckpointStore : IGameWorkflowCheckpointStore
    {
        private readonly Func<string, GameWorkflowCheckpoint> _load;

        public LoadingCheckpointStore(Func<string, GameWorkflowCheckpoint> load)
        {
            _load = load;
        }

        public ValueTask<GameWorkflowCheckpoint?> LoadAsync(
            string instanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameWorkflowCheckpoint?>(_load(instanceId));
        }

        public ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
            GameWorkflowCheckpoint checkpoint,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An invalid checkpoint must not be saved.");
    }

    private sealed class NullTranscriptCompactor : IGameTranscriptCompactor
    {
        public ValueTask<IReadOnlyList<AgentMessage>> CompactAsync(
            GameTranscriptCompactionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<AgentMessage>>((IReadOnlyList<AgentMessage>)null!);
        }
    }
}
