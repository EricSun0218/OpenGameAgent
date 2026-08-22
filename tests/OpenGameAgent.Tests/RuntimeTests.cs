using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void GameCoordinatesExposeConsistentValueOperators()
    {
        var first = new GameMoment("world", 1);
        var second = new GameMoment("world", 2);
        var key = new GameSessionKey("session", "actor");

        Assert.True(first < second);
        Assert.True(first <= second);
        Assert.True(second > first);
        Assert.True(second >= first);
        Assert.True(key == new GameSessionKey("session", "actor"));
        Assert.True(key != new GameSessionKey("session", "other"));
        Assert.Throws<InvalidOperationException>(() => first < new GameMoment("fork", 2));
    }

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
    public async Task StructuredGameInputForwardsAttachedModelResources()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var input = new GameInput(
            "session",
            "actor",
            "observation",
            "{\"question\":\"what is visible?\"}",
            new GameMoment("world", 10),
            content: new AgentContent[]
            {
                new ResourceContent("https://assets.example.test/frame.png", "image/png", "camera"),
            });

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var message = Assert.Single(provider.Requests).Messages.Last();
        Assert.IsType<JsonContent>(message.Content[0]);
        var resource = Assert.IsType<ResourceContent>(message.Content[1]);
        Assert.Equal("image/png", resource.MediaType);
        Assert.Equal("https://assets.example.test/frame.png", resource.Uri);
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
        var operationId = GameActionOperationIds.CreateV2(
            input.SessionId,
            input.ActorId,
            input.InputId,
            1,
            0,
            "place_block",
            input.Moment);
        var entry = await journal.FindAsync(operationId, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, entry!.Receipt!.Status);
    }

    [Fact]
    public void DefaultActionOperationIdV2IsStableBoundedAndSeparatesEveryAuthorityDimension()
    {
        static string Create(
            string session = "session",
            string actor = "actor",
            string input = "input",
            int turn = 1,
            int index = 0,
            string action = "act",
            string timeline = "world",
            long tick = 10,
            string? generation = "generation") => GameActionOperationIds.CreateV2(
                session,
                actor,
                input,
                turn,
                index,
                action,
                new GameMoment(timeline, tick),
                generation);

        var baseline = Create();
        Assert.Equal(baseline, Create());
        Assert.True(GameActionOperationIds.IsVersion2(baseline));
        Assert.Equal(GameActionOperationIds.Version2Prefix.Length + 64, baseline.Length);
        Assert.All(
            new[]
            {
                Create(session: "other-session"),
                Create(actor: "other-actor"),
                Create(input: "other-input"),
                Create(turn: 2),
                Create(index: 1),
                Create(action: "other-action"),
                Create(timeline: "other-world"),
                Create(tick: 11),
                Create(generation: "other-generation"),
            },
            candidate => Assert.NotEqual(baseline, candidate));
        Assert.Equal(
            GameActionOperationIds.Version2Prefix.Length + 64,
            Create(session: new string('s', 16_384)).Length);
    }

    [Fact]
    public async Task SettledToolTurnIsCheckpointedBeforeTheInputIsMarkedComplete()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("read-1", "inspect", "{}"))
            : Text("done"));
        var store = new RecordingSessionStore();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = store,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                new[] { ReadTool("inspect") }),
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["inspect"] = GameRouteDecision.Agent("typed"),
            }),
        });

        var result = await runtime.RunAsync(
            Input("inspect", "{}", "checkpoint-input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.SessionRevision);
        Assert.Equal(2, store.SavedSnapshots.Count);
        var checkpoint = store.SavedSnapshots[0];
        Assert.Equal(1, checkpoint.Revision);
        Assert.Empty(checkpoint.ProcessedInputIds);
        Assert.Equal("checkpoint-input", checkpoint.PendingInputId);
        Assert.Equal(3, checkpoint.Messages.Count);
        Assert.Equal(AgentRole.Tool, checkpoint.Messages[^1].Role);
        var final = store.SavedSnapshots[1];
        Assert.Equal(2, final.Revision);
        Assert.Contains("checkpoint-input", final.ProcessedInputIds);
        Assert.Null(final.PendingInputId);
    }

    [Fact]
    public async Task DurableToolCheckpointResumesWithoutAppendingTheInputTwice()
    {
        var store = new FailSecondSaveSessionStore();
        var input = Input("inspect", "{\"target\":\"gate\"}", "resume-input");
        var firstProvider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("read-1", "inspect", "{}"))
            : Text("lost final response"));
        await using (var firstRuntime = CreateCheckpointRuntime(firstProvider, store))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await firstRuntime.RunAsync(input, TestContext.Current.CancellationToken));
        }

        var checkpoint = await store.LoadAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            TestContext.Current.CancellationToken);
        Assert.Equal("resume-input", checkpoint!.PendingInputId);
        Assert.Equal(AgentRole.Tool, checkpoint.Messages[^1].Role);

        var unrelatedProvider = new RecordingProvider(_ => Text("must not run"));
        await using (var blockedRuntime = CreateCheckpointRuntime(unrelatedProvider, store))
        {
            var blocked = await blockedRuntime.RunAsync(
                Input("inspect", "{}", "different-input"),
                TestContext.Current.CancellationToken);
            Assert.Equal(GameAgentRunStatus.SessionConflict, blocked.Status);
            Assert.Equal(0, unrelatedProvider.CallCount);
        }

        var resumeProvider = new RecordingProvider(_ => Text("resumed"));
        await using (var resumeRuntime = CreateCheckpointRuntime(resumeProvider, store))
        {
            var result = await resumeRuntime.RunAsync(input, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var request = Assert.Single(resumeProvider.Requests);
        Assert.Equal(1, request.Messages.Count(message =>
            message.Metadata.TryGetValue("game.input_id", out var value)
            && value == "resume-input"));
        var completed = await store.LoadAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            TestContext.Current.CancellationToken);
        Assert.Null(completed!.PendingInputId);
        Assert.Contains("resume-input", completed.ProcessedInputIds);

        GameAgentRuntime CreateCheckpointRuntime(IModelProvider provider, IGameSessionStore sessionStore) =>
            new(new GameAgentRuntimeOptions(provider, "test")
            {
                SessionStore = sessionStore,
                ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                    new[] { ReadTool("inspect") }),
                RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
                {
                    ["inspect"] = GameRouteDecision.Agent("typed"),
                }),
            });
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
    public async Task CallerCancellationStillDurablySettlesAnAlreadyStartedRun()
    {
        var provider = new BlockingFirstResponseProvider();
        var store = new InMemoryGameSessionStore();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = store,
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        using var cancellation = new CancellationTokenSource();

        var run = runtime.RunAsync(Input("autonomous", "{}", "cancel-input"), cancellation.Token);
        await provider.FirstRequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var canceledRun = await run.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunStatus.Aborted, canceledRun.AgentResult?.Status);
        await runtime.DisposeAsync();

        var saved = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Revision);
        Assert.Contains("cancel-input", saved.ProcessedInputIds);
        var terminal = Assert.IsType<AgentMessage>(saved.Messages.Last());
        Assert.Equal(ModelStopReason.Aborted, terminal.StopReason);
    }

    [Fact]
    public async Task CompletedWorkflowCommitsWithABoundedSettlementAfterCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new InMemoryGameSessionStore();
        var options = new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("unused")), "test")
        {
            SessionStore = store,
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["month"] = GameRouteDecision.ToWorkflow("evolve", "typed"),
            }),
        };
        options.Workflows.Add(new DelegateWorkflow("evolve", (_, _) =>
        {
            cancellation.Cancel();
            return new ValueTask<GameWorkflowResult>(new GameWorkflowResult(
                new[] { Assistant("advanced") },
                succeeded: true));
        }));
        await using var runtime = new GameAgentRuntime(options);

        var result = await runtime.RunAsync(
            Input("month", "{}", "month-input"),
            cancellation.Token).WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var saved = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, saved!.Revision);
        Assert.Contains("month-input", saved.ProcessedInputIds);
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
        Assert.NotNull(await journal.FindAsync(
            GameActionOperationIds.CreateV2(
                input.SessionId,
                input.ActorId,
                input.InputId,
                1,
                0,
                "place_block",
                input.Moment),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PendingWorkCanPromoteAnOtherwiseQuickInputToAgentRoute()
    {
        var routingProvider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new JsonContent("{\"route\":\"quick\"}") },
            ModelStopReason.Stop,
            new ModelUsage(1, 1)));
        var provider = new RecordingProvider(_ => Text("ok"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            PendingWorkProvider = (_, _) => new ValueTask<bool>(true),
            RoutePolicy = new AutomaticGameRoutePolicy(
                classifier: new ModelGameRouteClassifier(routingProvider, "router-model").ClassifyAsync),
        });

        var result = await runtime.RunAsync(Input("tick", "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("pending-work", result.Route.Reason);
        Assert.Equal(0, routingProvider.CallCount);
    }

    [Fact]
    public async Task RuntimeRefreshesDynamicToolsAndDependentSkillsWithinTheActiveRun()
    {
        var unlocked = 0;
        var unlock = new AgentTool(
            new ToolDefinition("unlock", "Unlock another capability.", "{\"type\":\"object\"}"),
            (_, _, _) =>
            {
                Volatile.Write(ref unlocked, 1);
                return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("unlocked") }));
            });
        var advanced = ReadTool("advanced");
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("unlock-call", "unlock", "{}"))
            : Text("done"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                Volatile.Read(ref unlocked) == 0
                    ? new[] { unlock }
                    : new[] { unlock, advanced }),
            SkillSource = new InMemoryGameSkillSource(new[]
            {
                new GameSkill(
                    "advanced-guidance",
                    "advanced-guidance",
                    "Instructions for the unlocked capability.",
                    "Use the advanced capability only after it is unlocked.",
                    toolNames: new[] { "advanced" }),
            }),
        });

        var result = await runtime.RunAsync(
            Input("command", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var requests = provider.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.DoesNotContain(requests[0].Tools, tool => tool.Name == "advanced");
        Assert.Contains(requests[1].Tools, tool => tool.Name == "advanced");
        Assert.DoesNotContain("advanced-guidance", requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("advanced-guidance", requests[1].SystemPrompt, StringComparison.Ordinal);
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
        Assert.Equal(
            GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        var dispatcher = new DurableGameActionDispatcher(
            journal,
            new TestActionHandler(recover: (_, _) => throw new InvalidOperationException("store offline")));

        var receipt = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Uncertain, receipt.Status);
        Assert.Contains("recovery failed", receipt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerDiagnosticsCannotCreateUnboundedUncertainReceipts()
    {
        var dispatcher = new DurableGameActionDispatcher(
            new InMemoryGameActionJournal(),
            new TestActionHandler((_, _) => throw new InvalidOperationException(new string('x', 100_000))));

        var receipt = await dispatcher.ExecuteAsync(Intent("bounded-diagnostic"), TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Uncertain, receipt.Status);
        Assert.Equal(64_000, receipt.Message!.Length);
        Assert.Throws<ArgumentException>(() => new GameActionReceipt(
            receipt.OperationId,
            GameActionStatus.Rejected,
            "{}",
            receipt.Moment,
            code: new string('c', 1_025)));
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
    public async Task FinalActionReceiptIsJournaledEvenWhenCallerCancelsAfterExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var journal = new InMemoryGameActionJournal();
        var handler = new TestActionHandler((intent, _) =>
        {
            cancellation.Cancel();
            return new ValueTask<GameActionReceipt>(
                GameActionReceipt.Committed(intent, "{\"committed\":true}"));
        });
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var intent = Intent("cancel-after-commit");

        var receipt = await dispatcher.ExecuteAsync(intent, cancellation.Token);

        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        var stored = await journal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, stored!.Receipt!.Status);
    }

    [Fact]
    public async Task ReceiptCommitFailureReturnsUncertainInsteadOfEncouragingBlindReplay()
    {
        var durableJournal = new InMemoryGameActionJournal();
        var journal = new FailingReceiptJournal(durableJournal);
        var handler = new TestActionHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var intent = Intent("commit-failure");

        var receipt = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Uncertain, receipt.Status);
        Assert.Contains("journal commit failed", receipt.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.ExecuteCount);
        var stored = await durableJournal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken);
        Assert.True(stored!.Dispatched);
        Assert.Null(stored.Receipt);
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
        Assert.Equal(
            GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
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
    public async Task MemoryIdentifiersAreScopedToTheirGameSessionAndOwner()
    {
        var store = new InMemoryGameMemoryStore();
        await store.AppendAsync(
            new GameMemory("shared-id", "session-a", "npc", "personal", GameMemoryKind.Fact, "{\"value\":1}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory("shared-id", "session-b", "npc", "personal", GameMemoryKind.Fact, "{\"value\":2}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory("shared-id", "session-a", "other-npc", "personal", GameMemoryKind.Fact, "{\"value\":3}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        var first = await store.SearchAsync(
            new GameMemoryQuery("session-a", 1, ownerId: "npc", atOrBefore: new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        var second = await store.SearchAsync(
            new GameMemoryQuery("session-b", 1, atOrBefore: new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        Assert.Contains("1", Assert.Single(first).PayloadJson, StringComparison.Ordinal);
        Assert.Contains("2", Assert.Single(second).PayloadJson, StringComparison.Ordinal);
        Assert.Equal(
            2,
            (await store.SearchAsync(
                new GameMemoryQuery("session-a", 2, atOrBefore: new GameMoment("world", 1)),
                TestContext.Current.CancellationToken)).Count);
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
    public async Task RunningActorWorkCanReturnItsSettledOutcomeAfterCancellation()
    {
        var scheduler = new MultiActorScheduler(1, 1, 1);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = scheduler.EnqueueAsync("actor", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return 42;
        }, cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        release.TrySetResult();

        Assert.Equal(42, await work.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiActorIdleBarrierWaitsForRunningAndQueuedWorkToLeaveAllLanes()
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
        var second = scheduler.EnqueueAsync("actor", _ => new ValueTask<int>(2), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var idle = scheduler.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);
        release.TrySetResult();

        Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
        await idle.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MultiActorIdleBarrierIsSharedUntilTheSchedulerBecomesIdle()
    {
        var scheduler = new MultiActorScheduler(1, 1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = scheduler.EnqueueAsync("actor", async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var first = scheduler.WaitForIdleAsync();
        var second = scheduler.WaitForIdleAsync();

        Assert.Same(first, second);
        release.TrySetResult();
        Assert.Equal(1, await work);
        await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(scheduler.WaitForIdleAsync().IsCompletedSuccessfully);
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
        Assert.Equal(1, events.Count(item => item.Kind == ModelStreamEventKind.Started));
        Assert.True(events.Last().IsTerminal);
    }

    [Fact]
    public async Task MeaningfulStreamStartPreventsRetryAndFallback()
    {
        static async Task<IReadOnlyList<ModelStreamEvent>> CaptureAsync(IModelProvider provider)
        {
            var events = new List<ModelStreamEvent>();
            await foreach (var streamEvent in provider.StreamAsync(
                               ModelRequest(),
                               TestContext.Current.CancellationToken))
            {
                events.Add(streamEvent);
            }

            return events;
        }

        var retrySource = new MeaningfulStartThenFailureProvider();
        var retry = new RetryingModelProvider(retrySource, 2, _ => TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureAsync(retry));

        var fallbackSource = new MeaningfulStartThenFailureProvider();
        var fallbackTarget = new RecordingProvider(_ => Text("must not run"));
        var fallback = new FallbackModelProvider(new IModelProvider[] { fallbackSource, fallbackTarget });
        await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureAsync(fallback));

        Assert.Equal(1, retrySource.CallCount);
        Assert.Equal(1, fallbackSource.CallCount);
        Assert.Equal(0, fallbackTarget.CallCount);
    }

    [Fact]
    public async Task RetryingProviderPreservesStreamOutcomeWhenEnumeratorCleanupFails()
    {
        var inner = new FailureThenTerminalProviderWithFailingCleanup(failuresBeforeSuccess: 1);
        var provider = new RetryingModelProvider(inner, 2, _ => TimeSpan.Zero);

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(2, inner.DisposeCount);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(events.Last().Response!.Content)).Text);
    }

    [Fact]
    public async Task RetryingProviderRespectsTypedFailureClassificationAndServerDelay()
    {
        var attempts = 0;
        var transient = new RecordingProvider(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new ModelProviderException("busy", isTransient: true, retryAfter: TimeSpan.Zero);
            }

            return Text("ok");
        });
        var retried = new RetryingModelProvider(
            transient,
            2,
            _ => throw new InvalidOperationException("the server delay should take precedence"));

        var events = await CollectAsync(retried.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, attempts);
        Assert.Equal(ModelStopReason.Stop, events.Last().Response!.StopReason);

        var rejectedAttempts = 0;
        var nonTransient = new RecordingProvider(_ =>
        {
            Interlocked.Increment(ref rejectedAttempts);
            throw new ModelProviderException("invalid", isTransient: false);
        });
        var notRetried = new RetryingModelProvider(nonTransient, 2, _ => TimeSpan.Zero);

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(notRetried.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, rejectedAttempts);
    }

    [Fact]
    public async Task RetryingProviderCapsUntrustedServerDelay()
    {
        var attempts = 0;
        var inner = new RecordingProvider(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new ModelProviderException("busy", isTransient: true, retryAfter: TimeSpan.FromDays(1));
            }

            return Text("ok");
        });
        var provider = new RetryingModelProvider(
            inner,
            2,
            _ => TimeSpan.FromDays(1),
            maximumDelay: TimeSpan.Zero);

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, attempts);
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
        Assert.InRange(events.Count(item => item.Kind == ModelStreamEventKind.Started), 0, 1);
        Assert.True(events.Last().IsTerminal);
    }

    [Fact]
    public async Task FallbackProviderPreservesFailureAndTerminalAcrossCleanupFailures()
    {
        var first = new FailureThenTerminalProviderWithFailingCleanup(failuresBeforeSuccess: int.MaxValue);
        var second = new FailureThenTerminalProviderWithFailingCleanup(failuresBeforeSuccess: 0);
        var provider = new FallbackModelProvider(new IModelProvider[] { first, second });

        var events = await CollectAsync(provider.StreamAsync(ModelRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.CallCount);
        Assert.Equal(1, second.DisposeCount);
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

    [Theory]
    [InlineData("Route: {\"route\":\"quick\"}")]
    [InlineData("```json\n{\"route\":\"quick\"}\n```\n```json\n{\"route\":\"agent\"}\n```")]
    [InlineData("```yaml\nroute: quick\n```")]
    [InlineData("{\"route\":\"quick\",\"confidence\":1}")]
    public async Task ModelRouteClassifierRejectsProseMultipleFencesAndUnknownFields(string output)
    {
        var classifier = new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text(output)),
            "model");

        var decision = await classifier.ClassifyAsync(
            new GameRouteContext(Input("chat", "{}"), 1),
            TestContext.Current.CancellationToken);

        Assert.Null(decision);
    }

    [Fact]
    public async Task ModelRouteClassifierAcceptsASingleJsonFenceFromOpenAiCompatibleProviders()
    {
        var classifier = new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("```json\n{\"route\":\"quick\",\"reason\":\"read-only-question\"}\n```")),
            "model");

        var decision = await classifier.ClassifyAsync(
            new GameRouteContext(Input("chat", "{}"), availableToolCount: 3),
            TestContext.Current.CancellationToken);

        Assert.NotNull(decision);
        Assert.Equal(GameRouteKind.QuickResponse, decision.Route);
        Assert.Equal("read-only-question", decision.Reason);
    }

    [Fact]
    public async Task ExplicitAutoRouteDefersToTheConfiguredAutomaticPolicy()
    {
        var provider = new RecordingProvider(_ => Text("done"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["command"] = GameRouteDecision.Agent("typed-command"),
            }),
        });
        var input = new GameInput(
            "session",
            "actor",
            "command",
            "{}",
            new GameMoment("world", 10),
            "explicit-auto",
            new Dictionary<string, string> { ["agent.route"] = "auto" });

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("typed-command", result.Route.Reason);
    }

    [Fact]
    public async Task AutomaticRouteCanClassifyOrdinaryConversationAsQuickWhenToolsAreAvailable()
    {
        var routingProvider = new RecordingProvider(_ => Text("```json\n{\"route\":\"quick\"}\n```"));
        var answerProvider = new RecordingProvider(_ => Text("done"));
        var classifier = new ModelGameRouteClassifier(routingProvider, "router-model");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(answerProvider, "answer-model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { ReadTool("inspect") }),
        });

        var result = await runtime.RunAsync(
            Input("command", "{}", "structural-route"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal("model-classifier", result.Route.Reason);
        Assert.Equal(1, routingProvider.CallCount);
        Assert.Equal(1, answerProvider.CallCount);
        Assert.Empty(Assert.Single(routingProvider.Requests).Tools);
        Assert.Empty(Assert.Single(answerProvider.Requests).Tools);
    }

    [Fact]
    public async Task ModelRouteUsageSharesTheInputBudgetAndPersistsByCause()
    {
        var routingProvider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new JsonContent("{\"route\":\"quick\",\"reason\":\"simple\"}") },
            ModelStopReason.Stop,
            new ModelUsage(3, 2),
            provider: "router-provider",
            responseModel: "router-model",
            responseId: "router-response"));
        var responseProvider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("hello") },
            ModelStopReason.Stop,
            new ModelUsage(2, 1),
            provider: "answer-provider",
            responseModel: "answer-model"));
        var classifier = new ModelGameRouteClassifier(routingProvider, "router-model");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(responseProvider, "answer-model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "routed-usage"),
            TestContext.Current.CancellationToken);
        var usage = await runtime.ReadUsageAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.NotNull(usage);
        Assert.Equal(5, usage!.Ledger.TotalsByCause[GameSessionUsageCause.Routing].TotalTokens);
        Assert.Equal(3, usage.Ledger.TotalsByCause[GameSessionUsageCause.Assistant].TotalTokens);
        Assert.Equal(5, result.RunUsage.TotalsByCause[GameSessionUsageCause.Routing].TotalTokens);
        Assert.Equal(3, result.RunUsage.TotalsByCause[GameSessionUsageCause.Assistant].TotalTokens);
        var routing = Assert.Single(usage.Ledger.Records, record => record.Cause == GameSessionUsageCause.Routing);
        Assert.Contains("route-classification", routing.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("router-response", routing.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRouteClassificationStillAccountsUsageBeforeConservativeFallback()
    {
        var routingProvider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("not-json") },
            ModelStopReason.Stop,
            new ModelUsage(4, 1)));
        var responseProvider = new RecordingProvider(_ => Text("fallback"));
        var classifier = new ModelGameRouteClassifier(routingProvider, "router-model");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(responseProvider, "answer-model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "invalid-routing"),
            TestContext.Current.CancellationToken);
        var usage = await runtime.ReadUsageAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal("classifier-invalid-json-fallback-no-tools-needed", result.Route.Reason);
        Assert.Equal(GameRouteClassificationFailure.InvalidJson, result.Route.Classification!.Failure);
        Assert.True(result.Route.Classification.UsedFallback);
        Assert.Equal("no-tools-needed", result.Route.Classification.FallbackReason);
        Assert.Equal(5, usage!.Ledger.TotalsByCause[GameSessionUsageCause.Routing].TotalTokens);
        Assert.Contains("invalid-json", Assert.Single(
            usage.Ledger.Records,
            record => record.Cause == GameSessionUsageCause.Routing).DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRouteClassificationFallsBackToAgentWithToolsWithoutGivingTheRouterToolAuthority()
    {
        var routingProvider = new RecordingProvider(_ => Text("```json\n{\"route\":\"unknown\"}\n```"));
        var responseProvider = new RecordingProvider(_ => Text("safe fallback"));
        var classifier = new ModelGameRouteClassifier(routingProvider, "router-model");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(responseProvider, "answer-model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { ReadTool("inspect") }),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "invalid-route-with-tools"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("classifier-invalid-route-fallback-tools-available", result.Route.Reason);
        Assert.Equal(GameRouteClassificationFailure.InvalidRoute, result.Route.Classification!.Failure);
        Assert.True(result.Route.Classification.UsedFallback);
        Assert.Equal("tools-available", result.Route.Classification.FallbackReason);
        Assert.Empty(Assert.Single(routingProvider.Requests).Tools);
        Assert.Single(Assert.Single(responseProvider.Requests).Tools);
    }

    [Fact]
    public async Task ProviderFailureAndTimeoutHaveDistinctSafeRouteFallbacks()
    {
        var providerFailure = new RecordingProvider(_ => new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Error,
            new ModelUsage(2, 0),
            errorMessage: "provider unavailable"));
        var failedRuntime = new GameAgentRuntime(new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("fallback")), "answer")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(
                classifier: new ModelGameRouteClassifier(providerFailure, "router").ClassifyAsync),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { ReadTool("inspect") }),
        });

        var failed = await failedRuntime.RunAsync(
            Input("chat", "{}", "provider-route-failure"),
            TestContext.Current.CancellationToken);

        Assert.True(failed.Succeeded);
        Assert.Equal(GameRouteClassificationFailure.Provider, failed.Route.Classification!.Failure);
        Assert.Equal("classifier-provider-fallback-tools-available", failed.Route.Reason);

        var timeoutProvider = new NeverCompletingProvider();
        var timeoutRuntime = new GameAgentRuntime(new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("fallback")), "answer")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(
                classifier: new ModelGameRouteClassifier(
                    timeoutProvider,
                    "router",
                    options: new ModelGameRouteClassifierOptions { TimeoutMilliseconds = 25 }).ClassifyAsync),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { ReadTool("inspect") }),
        });

        var timedOut = await timeoutRuntime.RunAsync(
            Input("chat", "{}", "provider-route-timeout"),
            TestContext.Current.CancellationToken);

        Assert.True(timedOut.Succeeded);
        Assert.Equal(GameRouteClassificationFailure.Timeout, timedOut.Route.Classification!.Failure);
        Assert.Equal("classifier-timeout-fallback-tools-available", timedOut.Route.Reason);
    }

    [Fact]
    public async Task EmptyRouteClassificationHasAnExplicitFallbackCategory()
    {
        var emptyProvider = new RecordingProvider(_ => new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Stop,
            new ModelUsage(2, 0)));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new RecordingProvider(_ => Text("fallback")), "answer")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(
                classifier: new ModelGameRouteClassifier(emptyProvider, "router").ClassifyAsync),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "empty-route-classification"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal(GameRouteClassificationFailure.Empty, result.Route.Classification!.Failure);
        Assert.Equal("classifier-empty-fallback-no-tools-needed", result.Route.Reason);
    }

    [Fact]
    public async Task RoutingCannotConsumeTheAnswerBudgetAndThenStartAnotherModelCall()
    {
        var routingProvider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new JsonContent("{\"route\":\"quick\"}") },
            ModelStopReason.Stop,
            new ModelUsage(6, 5)));
        var responseProvider = new RecordingProvider(_ => Text("must not run"));
        var classifier = new ModelGameRouteClassifier(routingProvider, "router-model");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(responseProvider, "answer-model")
        {
            AgentLimits = new AgentLimits { MaxTotalTokens = 10 },
            RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "routing-budget"),
            TestContext.Current.CancellationToken);
        var usage = await runtime.ReadUsageAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Equal(0, responseProvider.CallCount);
        Assert.Equal(11, usage!.Ledger.Stats.TotalTokens);
        Assert.Equal(GameSessionUsageCause.Routing, Assert.Single(usage.Ledger.Records).Cause);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelGameRouteClassifier(
            new RecordingProvider(_ => Text("{}")),
            "model",
            options: new ModelGameRouteClassifierOptions { TimeoutMilliseconds = 0 }));
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
        var update = Assert.Single(progress);
        Assert.Equal(0.5, update.Fraction);
        Assert.Equal(
            "cHJldmlldw==",
            Assert.Single(update.Content.OfType<BinaryContent>()).Data);
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
            new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("summary:" + removed.Count)));

        var compacted = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), messages, 7),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, compacted.Messages.Count);
        Assert.Equal("transcript_summary", compacted.Messages[0].CustomRole);
        Assert.Contains(compacted.Messages, message => message.Content.OfType<ToolCallContent>().Any(item => item.Id == "call"));
        Assert.Contains(compacted.Messages, message => message.Role == AgentRole.Tool && message.ToolCallId == "call");
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
            return new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("complete summary"));
        });

        var compacted = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), messages, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(messages, summarized);
        var summary = Assert.Single(compacted.Messages);
        Assert.Equal("transcript_summary", summary.CustomRole);
        Assert.Equal("complete summary", Assert.IsType<TextContent>(Assert.Single(summary.Content)).Text);
    }

    [Fact]
    public async Task TranscriptCompactionHonorsATokenTargetEvenWhenMessageCountFits()
    {
        var messages = new AgentMessage[]
        {
            AgentMessage.User(new string('a', 200)),
            Assistant(new string('b', 200)),
            AgentMessage.User("recent"),
            Assistant("recent answer"),
        };
        var compactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
            new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("short summary:" + removed.Count)));

        var compacted = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("session", "actor"),
                messages,
                targetMessageCount: 10,
                targetEstimatedTokens: 100,
                tokenEstimator: ApproximateGameTokenEstimator.EstimateMessages),
            TestContext.Current.CancellationToken);

        Assert.True(compacted.Messages.Count < messages.Length);
        Assert.Equal("transcript_summary", compacted.Messages[0].CustomRole);
        Assert.True(ApproximateGameTokenEstimator.EstimateMessages(compacted.Messages) <= 100);
    }

    [Fact]
    public async Task TranscriptCompactionReturnsSummaryUsageAndTypedDetails()
    {
        var messages = new AgentMessage[]
        {
            AgentMessage.User("one"),
            Assistant("one"),
            AgentMessage.User("two"),
            Assistant("two"),
        };
        var usage = new ModelUsage(
            7,
            3,
            reasoningTokens: 2,
            cost: new ModelCost(input: 0.07, output: 0.06));
        var compactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
            new ValueTask<GameTranscriptSummaryResult>(
                new GameTranscriptSummaryResult("complete summary", usage, "{\"provider\":\"summary\"}")));

        var result = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("session", "actor"),
                messages,
                targetMessageCount: 1,
                targetEstimatedTokens: 100,
                tokenEstimator: ApproximateGameTokenEstimator.EstimateMessages),
            TestContext.Current.CancellationToken);

        Assert.Same(usage, result.Usage);
        Assert.Equal(4, result.Details.OriginalMessageCount);
        Assert.Equal(4, result.Details.CompactedMessageCount);
        Assert.Equal(0, result.Details.RetainedMessageCount);
        Assert.NotNull(result.Details.EstimatedTokensBefore);
        Assert.Equal("{\"provider\":\"summary\"}", result.Details.SummaryDetailsJson);
        Assert.Equal("transcript_summary", Assert.Single(result.Messages).CustomRole);
    }

    [Fact]
    public async Task RepeatedTranscriptCompactionUpdatesThePriorSummaryWithOnlyNewHistory()
    {
        var requests = new List<GameTranscriptSummaryContext>();
        var compactor = new SummarizingGameTranscriptCompactor((request, _) =>
        {
            requests.Add(request);
            var text = request.PreviousSummary is null ? "first summary" : "updated summary";
            return new ValueTask<GameTranscriptSummaryAttemptResult>(
                GameTranscriptSummaryAttemptResult.Success(text, new ModelUsage(1, 1)));
        });
        var key = new GameSessionKey("session", "actor");
        var first = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                key,
                new[]
                {
                    AgentMessage.User("one"),
                    Assistant("one"),
                    AgentMessage.User("two"),
                    Assistant("two"),
                },
                targetMessageCount: 3),
            TestContext.Current.CancellationToken);
        var secondSource = first.Messages
            .Concat(new[] { AgentMessage.User("three"), Assistant("three") })
            .ToArray();

        var second = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(key, secondSource, targetMessageCount: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, requests.Count);
        Assert.Null(requests[0].PreviousSummary);
        Assert.Equal(new[] { "one", "one" }, requests[0].Messages.Select(MessageText));
        Assert.Equal("first summary", requests[1].PreviousSummary);
        Assert.Equal(new[] { "two", "two" }, requests[1].Messages.Select(MessageText));
        Assert.Equal(3, requests[1].SourceMessages.Count);
        Assert.DoesNotContain(requests[1].Messages, message => message.CustomRole == "transcript_summary");
        Assert.True(second.Details.PreviousSummaryUsed);
        Assert.Equal(2, second.Details.IncrementalMessageCount);
        Assert.Equal("updated summary", MessageText(second.Messages[0]));
    }

    [Fact]
    public async Task TranscriptCompactionDoesNotTreatCustomMessagesInsideToolExchangesAsTurnBoundaries()
    {
        var call = new ToolCallContent("safe-call", "act", "{}");
        var source = new AgentMessage[]
        {
            AgentMessage.User("old"),
            Assistant("old"),
            AgentMessage.User("build"),
            new(AgentRole.Assistant, new AgentContent[] { call }, DateTimeOffset.UnixEpoch, model: "m", stopReason: ModelStopReason.ToolUse),
            new(AgentRole.Custom, new AgentContent[] { new TextContent("progress") }, DateTimeOffset.UnixEpoch, customRole: "world_event"),
            AgentMessage.ToolResult(call, new ToolResult(new AgentContent[] { new TextContent("built") }), DateTimeOffset.UnixEpoch),
            Assistant("finished"),
            AgentMessage.User("latest"),
            Assistant("latest"),
        };
        IReadOnlyList<AgentMessage>? summarized = null;
        var compactor = new SummarizingGameTranscriptCompactor((_, messages, _) =>
        {
            summarized = messages;
            return new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("safe summary"));
        });

        var result = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), source, 6),
            TestContext.Current.CancellationToken);

        Assert.NotNull(summarized);
        Assert.Contains(summarized, message => message.Content.OfType<ToolCallContent>().Any(item => item.Id == call.Id));
        Assert.Contains(summarized, message => message.Role == AgentRole.Tool && message.ToolCallId == call.Id);
        Assert.DoesNotContain(result.Messages, message => message.ToolCallId == call.Id);
        Assert.Equal(7, result.Details.CutMessageIndex);
        Assert.Equal(1, result.Details.RetainedTurnCount);
    }

    [Fact]
    public async Task TranscriptSummaryRetriesAggregateEveryAttemptUsageAndExposeAttemptDetails()
    {
        var seenPreviousErrors = new List<string?>();
        var compactor = new SummarizingGameTranscriptCompactor((request, _) =>
        {
            seenPreviousErrors.Add(request.PreviousError);
            return request.Attempt == 1
                ? new ValueTask<GameTranscriptSummaryAttemptResult>(
                    GameTranscriptSummaryAttemptResult.Failure(
                        "temporary provider failure",
                        new ModelUsage(3, 1, cost: new ModelCost(input: 0.3, output: 0.1)),
                        retryable: true,
                        detailsJson: "{\"attempt\":1}"))
                : new ValueTask<GameTranscriptSummaryAttemptResult>(
                    GameTranscriptSummaryAttemptResult.Success(
                        "summary",
                        new ModelUsage(2, 1, cost: new ModelCost(input: 0.2, output: 0.1)),
                        "{\"attempt\":2}"));
        }, maxSummaryAttempts: 2);

        var result = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("session", "actor"),
                new[] { AgentMessage.User("one"), Assistant("one") },
                targetMessageCount: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "temporary provider failure" }, seenPreviousErrors);
        Assert.Equal(7, result.Usage.TotalTokens);
        Assert.Equal(0.7, result.Usage.Cost.Total, precision: 10);
        Assert.Equal(2, result.Details.SummaryAttemptCount);
        Assert.Equal(1, result.Details.FailedSummaryAttemptCount);
        Assert.False(result.Details.SummaryAttempts[0].Succeeded);
        Assert.True(result.Details.SummaryAttempts[0].Retryable);
        Assert.True(result.Details.SummaryAttempts[1].Succeeded);
        Assert.Equal(GameTranscriptCompactionTrigger.MessageLimit, result.Details.Trigger);
    }

    [Fact]
    public async Task OversizedTranscriptSummaryIsRetriedAndChargesBothModelCalls()
    {
        var previousErrors = new List<string?>();
        var compactor = new SummarizingGameTranscriptCompactor((request, _) =>
        {
            previousErrors.Add(request.PreviousError);
            var summary = request.Attempt == 1 ? new string('x', 1_000) : "ok";
            return new ValueTask<GameTranscriptSummaryAttemptResult>(
                GameTranscriptSummaryAttemptResult.Success(summary, new ModelUsage(1, 1)));
        }, maxSummaryAttempts: 2);

        var result = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("session", "actor"),
                new[] { AgentMessage.User(new string('u', 100)), Assistant(new string('a', 100)) },
                targetMessageCount: 1,
                targetEstimatedTokens: 80,
                tokenEstimator: ApproximateGameTokenEstimator.EstimateMessages),
            TestContext.Current.CancellationToken);

        Assert.Null(previousErrors[0]);
        Assert.Contains("token target", previousErrors[1], StringComparison.Ordinal);
        Assert.Equal(4, result.Usage.TotalTokens);
        Assert.Equal(2, result.Details.SummaryAttemptCount);
        Assert.False(result.Details.SummaryAttempts[0].Succeeded);
        Assert.True(result.Details.SummaryAttempts[1].Succeeded);
        Assert.Equal("ok", MessageText(Assert.Single(result.Messages)));
    }

    [Fact]
    public async Task FailedSummaryDoesNotStartAnotherRetryAfterItsRunUsageBudgetIsExhausted()
    {
        var attempts = 0;
        var compactor = new SummarizingGameTranscriptCompactor((_, _) =>
        {
            attempts++;
            return new ValueTask<GameTranscriptSummaryAttemptResult>(
                GameTranscriptSummaryAttemptResult.Failure(
                    "temporary failure",
                    new ModelUsage(2, 1),
                    retryable: true));
        }, maxSummaryAttempts: 3);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await compactor.CompactAsync(
                new GameTranscriptCompactionContext(
                    new GameSessionKey("session", "actor"),
                    new[] { AgentMessage.User("one"), Assistant("one") },
                    targetMessageCount: 1,
                    maximumSummaryUsageTokens: 3),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal("summary_usage_limit_exceeded", exception.ErrorCode);
        Assert.Equal(3, exception.Usage.TotalTokens);
        Assert.Single(exception.Details.SummaryAttempts);
        Assert.False(exception.Details.Applied);
        Assert.Equal(exception.ErrorCode, exception.Details.FailureCode);
    }

    [Fact]
    public async Task FailedTranscriptSummaryPersistsAllRetryUsageWithoutProcessingTheInput()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        var original = new[]
        {
            AgentMessage.User("one"),
            Assistant("one"),
            AgentMessage.User("two"),
            Assistant("two"),
        };
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, original),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => Text("must not run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 5 },
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((request, _) =>
                new ValueTask<GameTranscriptSummaryAttemptResult>(
                    GameTranscriptSummaryAttemptResult.Failure(
                        "summary service unavailable",
                        new ModelUsage(2, 1, cost: new ModelCost(input: 0.2, output: 0.1)),
                        retryable: true,
                        detailsJson: "{\"attempt\":" + request.Attempt + "}")),
                maxSummaryAttempts: 2),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "failed-summary-usage"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Contains("summary service unavailable", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Revision);
        Assert.Equal(original, saved.Messages);
        Assert.DoesNotContain("failed-summary-usage", saved.ProcessedInputIds);
        Assert.Equal(6, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        var usageRecord = Assert.Single(saved.UsageLedger.Records);
        Assert.Contains("\"SummaryAttemptCount\":2", usageRecord.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"Applied\":false", usageRecord.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"FailureCode\":\"summary_failed\"", usageRecord.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliedUsageOnlyCasConflictDoesNotDuplicateFailedSummaryCharges()
    {
        var store = new AppliedButReportedConflictOnSecondSaveSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new[]
            {
                AgentMessage.User("one"),
                Assistant("one"),
                AgentMessage.User("two"),
                Assistant("two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RecordingProvider(_ => Text("must not run")),
            "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 5 },
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _) =>
                new ValueTask<GameTranscriptSummaryAttemptResult>(
                    GameTranscriptSummaryAttemptResult.Failure(
                        "failed",
                        new ModelUsage(2, 1),
                        retryable: false))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "failed-summary-cas"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Equal(2, store.SaveCalls);
        Assert.NotNull(saved);
        Assert.Single(saved.UsageLedger.Records);
        Assert.Equal(3, saved.UsageLedger.Stats.TotalTokens);
    }

    [Fact]
    public async Task BranchSummaryHelperSelectsACompleteRecentTurnWithoutSessionTreeState()
    {
        var call = new ToolCallContent("branch-call", "act", "{}");
        var source = new AgentMessage[]
        {
            AgentMessage.User("old"),
            Assistant("old"),
            AgentMessage.User("branch action"),
            new(AgentRole.Assistant, new AgentContent[] { call }, DateTimeOffset.UnixEpoch, model: "m", stopReason: ModelStopReason.ToolUse),
            AgentMessage.ToolResult(call, new ToolResult(new AgentContent[] { new TextContent("done") }), DateTimeOffset.UnixEpoch),
            Assistant("branch finished"),
        };
        GameTranscriptSummaryContext? request = null;
        var summarizer = new GameBranchSummarizer((context, _) =>
        {
            request = context;
            return new ValueTask<GameTranscriptSummaryAttemptResult>(
                GameTranscriptSummaryAttemptResult.Success("branch summary", new ModelUsage(2, 1)));
        });

        var result = await summarizer.SummarizeAsync(
            new GameSessionKey("session", "actor"),
            source,
            targetEstimatedTokens: 40,
            messages => messages.Count * 10L,
            TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal(GameTranscriptSummaryPurpose.Branch, request.Purpose);
        Assert.Equal(4, request.Messages.Count);
        Assert.Contains(request.Messages, message => message.Content.OfType<ToolCallContent>().Any(item => item.Id == call.Id));
        Assert.Contains(request.Messages, message => message.Role == AgentRole.Tool && message.ToolCallId == call.Id);
        Assert.Equal(2, result.Details.OmittedMessageCount);
        Assert.Equal(3, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task TranscriptSummaryUsageCountsTowardTheRunTokenLimit()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("one"),
                Assistant("one"),
                AgentMessage.User("two"),
                Assistant("two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => Text("answer"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 5, MaxTotalTokens = 10 },
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(
                    new GameTranscriptSummaryResult("summary", new ModelUsage(6, 3)))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "usage-budget"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Contains("including transcript compaction", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(11, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(9, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        Assert.Equal(2, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
    }

    [Fact]
    public async Task OverBudgetTranscriptSummaryPreventsTheNextModelRequest()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("one"),
                Assistant("one"),
                AgentMessage.User("two"),
                Assistant("two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => Text("must not run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 5, MaxTotalTokens = 5 },
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(
                    new GameTranscriptSummaryResult("summary", new ModelUsage(4, 2)))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}", "summary-over-budget"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Equal(0, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Single(saved.UsageLedger.Records, record => record.Cause == GameSessionUsageCause.Compaction);
        Assert.Equal(6, saved.UsageLedger.Stats.TotalTokens);
    }

    [Fact]
    public async Task UsageLedgerSurvivesRepeatedCompactionAndRuntimeRestart()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        var provider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("answer") },
            ModelStopReason.Stop,
            new ModelUsage(2, 1, cost: new ModelCost(input: 0.2, output: 0.1))));
        var compactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
            new ValueTask<GameTranscriptSummaryResult>(
                new GameTranscriptSummaryResult(
                    "summary",
                    new ModelUsage(3, 2, cost: new ModelCost(input: 0.3, output: 0.2)))));

        GameAgentRuntime CreateRuntime() => new(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            AgentLimits = new AgentLimits { MaxMessages = 3 },
            TranscriptCompactor = compactor,
        });

        await using (var runtime = CreateRuntime())
        {
            Assert.True((await runtime.RunAsync(
                Input("chat", "{}", "usage-one"),
                TestContext.Current.CancellationToken)).Succeeded);
            Assert.True((await runtime.RunAsync(
                Input("chat", "{}", "usage-two"),
                TestContext.Current.CancellationToken)).Succeeded);
        }

        await using (var restarted = CreateRuntime())
        {
            Assert.True((await restarted.RunAsync(
                Input("chat", "{}", "usage-three"),
                TestContext.Current.CancellationToken)).Succeeded);
        }

        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(3, saved.Messages.Count);
        Assert.Equal(5, saved.UsageLedger.Records.Count);
        Assert.Equal(19, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(10, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        Assert.Equal(9, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
        Assert.Equal(1.9, saved.UsageLedger.Stats.CostTotal, precision: 10);
    }

    [Fact]
    public async Task AppliedCasConflictRetryDoesNotDuplicateUsage()
    {
        var store = new AppliedButReportedConflictSessionStore();
        var provider = new RecordingProvider(_ => Text("answer"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
        });
        var input = Input("chat", "{}", "cas-usage");

        var first = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var retry = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.SessionConflict, first.Status);
        Assert.Equal(GameAgentRunStatus.Duplicate, retry.Status);
        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Single(saved.UsageLedger.Records);
        Assert.Equal(2, saved.UsageLedger.Stats.TotalTokens);
    }

    [Fact]
    public async Task LosingCasAttemptSettlesUsageOnceBeforeARealRetry()
    {
        var store = new ConflictOnceSessionStore();
        var provider = new RecordingProvider(_ => Text("answer"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
        });
        var input = Input("chat", "{}", "losing-cas-usage");

        var conflicted = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var retried = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.SessionConflict, conflicted.Status);
        Assert.True(retried.Succeeded);
        Assert.Equal(2, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.UsageLedger.Records.Count);
        Assert.Equal(4, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(3, saved.Revision);
    }

    [Fact]
    public void UsageLedgerAppendIsIdempotentAndRejectsRecordIdentityReuse()
    {
        var record = new GameSessionUsageRecord(
            "usage-record",
            GameSessionUsageCause.Assistant,
            new ModelUsage(2, 1),
            "run",
            "input");
        var ledger = new GameSessionUsageLedger(new[] { record });

        var replayed = ledger.Append(new[] { record });

        Assert.Same(ledger, replayed);
        Assert.Single(replayed.Records);
        Assert.False(replayed.Stats.Total.CostKnown);
        Assert.Null(replayed.Stats.Total.CostTotalIfKnown);
        Assert.Throws<InvalidOperationException>(() => replayed.Append(new[]
        {
            new GameSessionUsageRecord(
                record.RecordId,
                record.Cause,
                new ModelUsage(8, 1),
                record.RunId,
                record.InputId),
        }));
    }

    [Fact]
    public async Task UsageLedgerBoundsRecentRecordsWithoutLosingCumulativeStats()
    {
        var records = Enumerable.Range(0, 10)
            .Select(index => new GameSessionUsageRecord(
                "bounded-" + index,
                GameSessionUsageCause.Assistant,
                new ModelUsage(1, 1, cost: new ModelCost(input: 0.01, output: 0.02))))
            .ToArray();
        var ledger = new GameSessionUsageLedger(records, recentRecordCapacity: 3);
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("bounded-session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, usageLedger: ledger),
            0,
            TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.UsageLedger.Records.Count);
        Assert.Equal(10, loaded.UsageLedger.TotalRecordCount);
        Assert.Equal(20, loaded.UsageLedger.Stats.TotalTokens);
        Assert.Equal(0.3, loaded.UsageLedger.Stats.CostTotal, precision: 10);
        Assert.Equal(new[] { "bounded-7", "bounded-8", "bounded-9" },
            loaded.UsageLedger.Records.Select(record => record.RecordId));
    }

    [Fact]
    public async Task ToolModelUsageIsIncludedInTheSessionLedger()
    {
        var store = new InMemoryGameSessionStore();
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("usage-tool", "inspect", "{}"))
            : Text("done"));
        var tool = new AgentTool(
            new ToolDefinition("inspect", "inspect", "{\"type\":\"object\"}"),
            (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                new AgentContent[] { new TextContent("inspected") },
                usage: new ModelUsage(3, 1, cost: new ModelCost(input: 0.03, output: 0.02)))));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        });

        var result = await runtime.RunAsync(
            Input("inspect", "{}", "tool-usage"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(saved);
        Assert.Equal(3, saved.UsageLedger.Records.Count);
        Assert.Equal(8, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(4, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
        Assert.Equal(4, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Tool).TotalTokens);
        Assert.Equal(0.05, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Tool).CostTotal, precision: 10);
    }

    [Fact]
    public async Task RuntimeCompactsBeforeAnEstimatedContextWindowOverflow()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        var history = new AgentMessage[]
        {
            AgentMessage.User(new string('a', 1_200)),
            Assistant(new string('b', 1_200)),
            AgentMessage.User(new string('c', 1_200)),
            Assistant(new string('d', 1_200)),
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
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
            {
                compacted = true;
                return new ValueTask<GameTranscriptSummaryResult>(
                    new GameTranscriptSummaryResult("summary:" + removed.Count));
            }),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(compacted);
        Assert.Equal(1, provider.CallCount);
        var request = Assert.Single(provider.Requests);
        Assert.True(ApproximateGameTokenEstimator.EstimateRequest(
            request.Model,
            request.SystemPrompt,
            request.Messages,
            request.Tools) <= 900);
    }

    [Fact]
    public async Task RuntimeRecoversOnceFromAProviderReportedContextOverflowBeforeOutput()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(call => call == 1
            ? new ModelResponse(
                Array.Empty<AgentContent>(),
                ModelStopReason.Length,
                new ModelUsage(inputTokens: 990))
            : Text("recovered"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult(
                    "older history",
                    new ModelUsage(inputTokens: 2, outputTokens: 1)))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{\"text\":\"keep this exact input\"}"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
        var requests = provider.Requests.ToArray();
        Assert.Equal(5, requests[0].Messages.Count);
        Assert.True(requests[1].Messages.Count < requests[0].Messages.Count);
        Assert.Equal(
            Assert.IsType<JsonContent>(requests[0].Messages[^1].Content[0]).Json,
            Assert.IsType<JsonContent>(requests[1].Messages[^1].Content[0]).Json);
        Assert.NotNull(saved);
        Assert.Equal(995, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(992, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
        Assert.Equal(3, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        Assert.Contains(saved.UsageLedger.Records, record =>
            record.DetailsJson?.Contains("context_overflow_recovery", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RuntimeRecoversFromStructuredRequestTooLargeBeforeAStreamStarts()
    {
        var store = new InMemoryGameSessionStore();
        await store.SaveAsync(
            new GameSessionSnapshot(new GameSessionKey("session", "actor"), 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RequestTooLargeThenResponseProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("older history"))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RuntimeRecoversFromAStructuredContextOverflowDiagnostic()
    {
        var store = new InMemoryGameSessionStore();
        await store.SaveAsync(
            new GameSessionSnapshot(new GameSessionKey("session", "actor"), 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(call => call == 1
            ? new ModelResponse(
                Array.Empty<AgentContent>(),
                ModelStopReason.Error,
                errorMessage: "request rejected",
                diagnostics: new[]
                {
                    new ModelDiagnostic(
                        "provider_failure",
                        "structured provider error",
                        ModelDiagnosticSeverity.Error,
                        "{\"status\":400,\"errorCode\":\"model_context_window_exceeded\"}"),
                })
            : Text("recovered"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("older history"))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RuntimeNeverReplaysAnOverflowAfterMeaningfulOutputWasExposed()
    {
        var compacted = false;
        var provider = new MeaningfulOutputThenOverflowProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
            {
                compacted = true;
                return new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("must not run"));
            }),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, provider.CallCount);
        Assert.False(compacted);
    }

    [Fact]
    public async Task RuntimeNeverReplaysAfterAToolMayHaveChangedTheGame()
    {
        var compacted = false;
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("call", "inspect", "{}"))
            : new ModelResponse(
                Array.Empty<AgentContent>(),
                ModelStopReason.Length,
                new ModelUsage(inputTokens: 990)));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { ReadTool("inspect") }),
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
            {
                compacted = true;
                return new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("must not run"));
            }),
        });

        var result = await runtime.RunAsync(
            Input("inspect", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
        Assert.False(compacted);
    }

    [Fact]
    public async Task RuntimeDoesNotMisclassifyRateLimitsAsContextOverflow()
    {
        var provider = new RateLimitedProvider();
        var compacted = false;
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
            {
                compacted = true;
                return new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult("must not run"));
            }),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, provider.CallCount);
        Assert.False(compacted);
    }

    [Fact]
    public async Task FailedOverflowCompactionIsChargedWithoutReplayingTheProvider()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Length,
            new ModelUsage(inputTokens: 990)));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor(
                (_, _) => new ValueTask<GameTranscriptSummaryAttemptResult>(
                    GameTranscriptSummaryAttemptResult.Failure(
                        "summary unavailable",
                        new ModelUsage(inputTokens: 5, outputTokens: 2),
                        retryable: false))),
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(997, saved.UsageLedger.Stats.TotalTokens);
        Assert.Equal(990, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
        Assert.Equal(7, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
    }

    [Fact]
    public async Task AppliedCasConflictDoesNotDuplicateOverflowRecoveryUsage()
    {
        var inner = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await inner.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var store = new AppliedButReportedConflictWrapper(inner);
        var provider = new RecordingProvider(call => call == 1
            ? new ModelResponse(
                Array.Empty<AgentContent>(),
                ModelStopReason.Length,
                new ModelUsage(inputTokens: 990))
            : Text("recovered"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, _, _) =>
                new ValueTask<GameTranscriptSummaryResult>(new GameTranscriptSummaryResult(
                    "older history",
                    new ModelUsage(inputTokens: 2, outputTokens: 1)))),
        });
        var input = Input("chat", "{}", "overflow-cas");

        var conflicted = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var duplicate = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal(GameAgentRunStatus.SessionConflict, conflicted.Status);
        Assert.Equal(GameAgentRunStatus.Duplicate, duplicate.Status);
        Assert.Equal(2, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(3, saved.UsageLedger.Records.Count);
        Assert.Equal(995, saved.UsageLedger.Stats.TotalTokens);
    }

    [Fact]
    public async Task CancellingOverflowCompactionDoesNotReplayAndStillChargesReportedProviderUsage()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new AgentMessage[]
            {
                AgentMessage.User("old one"),
                Assistant("old one"),
                AgentMessage.User("old two"),
                Assistant("old two"),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Length,
            new ModelUsage(inputTokens: 990)));
        var compactionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            TranscriptCompactor = new SummarizingGameTranscriptCompactor(async (_, cancellationToken) =>
            {
                compactionStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return GameTranscriptSummaryAttemptResult.Success("unreachable");
            }),
        });
        using var cancellation = new CancellationTokenSource();

        var run = runtime.RunAsync(Input("chat", "{}"), cancellation.Token);
        await compactionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await run;
        var saved = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentRunStatus.Aborted, result.AgentResult!.Status);
        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Revision);
        Assert.Equal(990, saved.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
    }

    [Fact]
    public async Task RuntimeRejectsAnEstimatedContextOverflowBeforeCallingTheProvider()
    {
        var store = new InMemoryGameSessionStore();
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new[]
            {
                AgentMessage.User(new string('a', 2_000)),
                Assistant(new string('b', 2_000)),
            }),
            0,
            TestContext.Current.CancellationToken);
        var provider = new RecordingProvider(_ => Text("must not run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            SessionStore = store,
            ContextWindowTokens = 800,
            ContextWindowReserveTokens = 100,
        });

        var exception = await Assert.ThrowsAsync<GameRuntimeLimitException>(async () =>
            await runtime.RunAsync(Input("chat", "{}"), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(GameAgentRuntimeOptions.ContextWindowTokens), exception.Limit);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RuntimeRejectsContextGrowthFromTheFinalRequestHookBeforeCallingTheProvider()
    {
        var provider = new RecordingProvider(_ => Text("must not run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ContextWindowTokens = 800,
            ContextWindowReserveTokens = 100,
            AgentHooks = new AgentHooks
            {
                BeforeModelRequestAsync = (request, _) => new ValueTask<ModelRequest>(new ModelRequest(
                    request.Model,
                    new string('x', 4_000),
                    request.Messages,
                    request.Tools,
                    request.Parameters,
                    request.SessionId,
                    request.RunId,
                    request.Turn)),
            },
        });

        var result = await runtime.RunAsync(
            Input("chat", "{}"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(GameAgentRunStatus.Failed, result.Status);
        Assert.Equal(AgentRunStatus.KernelError, result.AgentResult!.Status);
        Assert.Contains("context window", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RuntimeCompactsAgainAfterALargeToolResultBeforeTheNextModelTurn()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("call", "inspect", "{}"))
            : Text("done"));
        var tool = new AgentTool(
            new ToolDefinition("inspect", "Inspect a bounded area.", "{\"type\":\"object\"}"),
            (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                new AgentContent[] { new TextContent(new string('x', 5_000)) })));
        var compacted = false;
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ContextWindowTokens = 1_000,
            ContextWindowReserveTokens = 100,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
            TranscriptCompactor = new SummarizingGameTranscriptCompactor((_, removed, _) =>
            {
                compacted = true;
                return new ValueTask<GameTranscriptSummaryResult>(
                    new GameTranscriptSummaryResult("tool turn summary:" + removed.Count));
            }),
        });

        var result = await runtime.RunAsync(
            Input("inspect", "{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(compacted);
        Assert.Equal(2, provider.CallCount);
        var second = provider.Requests.Last();
        Assert.Equal("transcript_summary", Assert.Single(second.Messages).CustomRole);
        Assert.True(ApproximateGameTokenEstimator.EstimateRequest(
            second.Model,
            second.SystemPrompt,
            second.Messages,
            second.Tools) <= 900);
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
                return new ValueTask<GameTranscriptSummaryResult>(
                    new GameTranscriptSummaryResult("summary:" + removed.Count));
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
        var committedSession = new GameSessionSnapshot(
            session.Key,
            1,
            processedInputIds: new[] { firstInput.InputId });
        var second = await workflow.RunAsync(
            new GameWorkflowContext(secondInput, Array.Empty<GameContextSlice>(), Array.Empty<AgentTool>(), committedSession),
            TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal("waiting", Assert.IsType<TextContent>(Assert.Single(Assert.Single(first.Messages).Content)).Text);
        Assert.True(second.Succeeded);
        Assert.Equal(new[] { "advanced", "done" }, second.Messages.Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
    }

    [Fact]
    public async Task DurableWorkflowReplaysCompletedInvocationUntilItsInputIsCommitted()
    {
        var executions = 0;
        var workflow = new DurableGameWorkflow(
            "evolve",
            new[]
            {
                new GameWorkflowStep("finish", (_, _) =>
                {
                    Interlocked.Increment(ref executions);
                    return new ValueTask<GameWorkflowStepResult>(
                        GameWorkflowStepResult.Complete("{\"done\":true}", Assistant("replay me")));
                }),
            },
            new InMemoryGameWorkflowCheckpointStore());
        var input = Input("month", "{}", "workflow-replay");
        var session = new GameSessionSnapshot(new GameSessionKey(input.SessionId, input.ActorId), 0);
        var context = new GameWorkflowContext(
            input,
            Array.Empty<GameContextSlice>(),
            Array.Empty<AgentTool>(),
            session);

        var first = await workflow.RunAsync(context, TestContext.Current.CancellationToken);
        var replay = await workflow.RunAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(1, executions);
        Assert.Equal(
            Assert.IsType<TextContent>(Assert.Single(Assert.Single(first.Messages).Content)).Text,
            Assert.IsType<TextContent>(Assert.Single(Assert.Single(replay.Messages).Content)).Text);
    }

    [Fact]
    public async Task DurableWorkflowAcceptsEquivalentValuesRehydratedByACustomStore()
    {
        var workflow = new DurableGameWorkflow(
            "evolve",
            new[]
            {
                new GameWorkflowStep("finish", (_, _) =>
                    new ValueTask<GameWorkflowStepResult>(
                        GameWorkflowStepResult.Complete("{\"done\":true}", Assistant("persisted")))),
            },
            new RehydratingCheckpointStore());
        var input = Input("month", "{}", "workflow-rehydrated");
        var context = new GameWorkflowContext(
            input,
            Array.Empty<GameContextSlice>(),
            Array.Empty<AgentTool>(),
            new GameSessionSnapshot(new GameSessionKey(input.SessionId, input.ActorId), 0));

        var result = await workflow.RunAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("persisted", Assert.IsType<TextContent>(Assert.Single(Assert.Single(result.Messages).Content)).Text);
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
    public async Task MailboxPendingStatusIsReadOnlyAndTracksLeaseLifecycle()
    {
        var mailbox = new InMemoryGameMailbox();
        var recipient = new GameMailboxRecipientKey("session", "npc");
        var missing = new GameMailboxRecipientKey("session", "missing");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await mailbox.EnqueueAsync(
            new GameMailboxMessage(
                "mail",
                recipient.SessionId,
                recipient.RecipientId,
                "event",
                "{\"private\":\"not returned\"}",
                new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        var requested = new[] { recipient, missing, recipient };
        var initial = await mailbox.GetPendingStatusAsync(
            requested,
            now,
            TestContext.Current.CancellationToken);
        var repeated = await mailbox.GetPendingStatusAsync(
            requested,
            now,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, initial.Count);
        Assert.Equal(1, initial[0].ReadyCount);
        Assert.Equal(0, initial[0].LeasedCount);
        Assert.Equal(1, initial[0].IncompleteCount);
        Assert.Equal(0, initial[1].IncompleteCount);
        Assert.Equal(initial[0].IncompleteCount, initial[2].IncompleteCount);
        Assert.Equal(initial.Select(StatusTuple), repeated.Select(StatusTuple));

        var delivery = Assert.Single(await mailbox.ClaimAsync(
            recipient.SessionId,
            recipient.RecipientId,
            1,
            now,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, delivery.Attempt);

        var leased = Assert.Single(await mailbox.GetPendingStatusAsync(
            new[] { recipient },
            now.AddSeconds(30),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, leased.ReadyCount);
        Assert.Equal(1, leased.LeasedCount);
        Assert.Equal(1, leased.IncompleteCount);

        var expired = Assert.Single(await mailbox.GetPendingStatusAsync(
            new[] { recipient },
            now.AddMinutes(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, expired.ReadyCount);
        Assert.Equal(0, expired.LeasedCount);

        await mailbox.AbandonAsync(
            delivery.Message.MessageId,
            delivery.LeaseToken,
            TestContext.Current.CancellationToken);
        var abandoned = Assert.Single(await mailbox.GetPendingStatusAsync(
            new[] { recipient },
            now,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, abandoned.ReadyCount);
        Assert.Equal(0, abandoned.LeasedCount);

        var reclaimed = Assert.Single(await mailbox.ClaimAsync(
            recipient.SessionId,
            recipient.RecipientId,
            1,
            now,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, reclaimed.Attempt);
        await mailbox.CompleteAsync(
            reclaimed.Message.MessageId,
            reclaimed.LeaseToken,
            TestContext.Current.CancellationToken);

        var completed = Assert.Single(await mailbox.GetPendingStatusAsync(
            new[] { recipient },
            now,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, completed.IncompleteCount);

        static (GameMailboxRecipientKey Recipient, int Ready, int Leased, int Incomplete) StatusTuple(
            GameMailboxPendingStatus status) =>
            (status.Recipient, status.ReadyCount, status.LeasedCount, status.IncompleteCount);
    }

    [Fact]
    public async Task MailboxPendingStatusValidatesBoundsAndCancellation()
    {
        var mailbox = new InMemoryGameMailbox();
        var recipients = Enumerable.Range(0, 4_097)
            .Select(index => new GameMailboxRecipientKey("session", "npc-" + index))
            .ToArray();

        var limit = await Assert.ThrowsAsync<GameRuntimeLimitException>(async () =>
            await mailbox.GetPendingStatusAsync(
                recipients,
                DateTimeOffset.UnixEpoch,
                TestContext.Current.CancellationToken));
        Assert.Equal("MaximumRecipients", limit.Limit);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await mailbox.GetPendingStatusAsync(
                new[] { new GameMailboxRecipientKey("session", "npc") },
                DateTimeOffset.UnixEpoch,
                cancellation.Token));
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

    private static string MessageText(AgentMessage message) =>
        string.Join("\n", message.Content.OfType<TextContent>().Select(content => content.Text));

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

    private sealed class NeverCompletingProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class DelegateWorkflow : IGameWorkflow
    {
        private readonly Func<GameWorkflowContext, CancellationToken, ValueTask<GameWorkflowResult>> _run;

        public DelegateWorkflow(
            string name,
            Func<GameWorkflowContext, CancellationToken, ValueTask<GameWorkflowResult>> run)
        {
            Name = name;
            _run = run;
        }

        public string Name { get; }

        public ValueTask<GameWorkflowResult> RunAsync(
            GameWorkflowContext context,
            CancellationToken cancellationToken) => _run(context, cancellationToken);
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

    private sealed class RecordingSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();

        public List<GameSessionSnapshot> SavedSnapshots { get; } = new();

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) => _inner.LoadAsync(key, cancellationToken);

        public async ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            SavedSnapshots.Add(snapshot);
            return await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }
    }

    private sealed class FailSecondSaveSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();
        private int _saves;

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) => _inner.LoadAsync(key, cancellationToken);

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saves) == 2)
            {
                throw new InvalidOperationException("simulated process failure after the tool checkpoint");
            }

            return _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }
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

    private sealed class AppliedButReportedConflictSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();
        private int _reportedConflict;

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) => _inner.LoadAsync(key, cancellationToken);

        public async ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var saved = await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
            if (saved.Saved && Interlocked.Exchange(ref _reportedConflict, 1) == 0)
            {
                return new GameSessionSaveResult(saved: false, saved.Current);
            }

            return saved;
        }
    }

    private sealed class AppliedButReportedConflictWrapper : IGameSessionStore
    {
        private readonly IGameSessionStore _inner;
        private int _reportedConflict;

        public AppliedButReportedConflictWrapper(IGameSessionStore inner)
        {
            _inner = inner;
        }

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) => _inner.LoadAsync(key, cancellationToken);

        public async ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var saved = await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
            if (saved.Saved && Interlocked.Exchange(ref _reportedConflict, 1) == 0)
            {
                return new GameSessionSaveResult(saved: false, saved.Current);
            }

            return saved;
        }
    }

    private sealed class AppliedButReportedConflictOnSecondSaveSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();
        private int _saveCalls;

        public int SaveCalls => Volatile.Read(ref _saveCalls);

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) => _inner.LoadAsync(key, cancellationToken);

        public async ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _saveCalls);
            var saved = await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
            return call == 2 && saved.Saved
                ? new GameSessionSaveResult(saved: false, saved.Current)
                : saved;
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

    private sealed class MeaningfulStartThenFailureProvider : IModelProvider
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                new ModelResponse(
                    new AgentContent[] { new TextContent("already visible") },
                    ModelStopReason.Pending,
                    new ModelUsage(1)));
            await Task.Yield();
            throw new InvalidOperationException("connection dropped after visible output");
        }
    }

    private sealed class RequestTooLargeThenResponseProvider : IModelProvider
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            await Task.Yield();
            if (call == 1)
            {
                throw new ModelProviderException(
                    "The request body is too large.",
                    isTransient: false,
                    statusCode: 413);
            }

            yield return ModelStreamEvent.Terminal(Text("recovered"));
        }
    }

    private sealed class MeaningfulOutputThenOverflowProvider : IModelProvider
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextStarted,
                new ModelResponse(
                    new AgentContent[] { new TextContent(string.Empty) },
                    ModelStopReason.Pending));
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                new ModelResponse(
                    new AgentContent[] { new TextContent("already visible") },
                    ModelStopReason.Pending),
                delta: "already visible");
            await Task.Yield();
            throw new ModelProviderException(
                "maximum context length exceeded",
                isTransient: false,
                statusCode: 400);
        }
    }

    private sealed class RateLimitedProvider : IModelProvider
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            throw new ModelProviderException(
                "rate limit: maximum context length metric unavailable",
                isTransient: true,
                statusCode: 429);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FailureThenTerminalProviderWithFailingCleanup : IModelProvider
    {
        private readonly int _failuresBeforeSuccess;
        private int _calls;
        private int _disposeCount;

        public FailureThenTerminalProviderWithFailingCleanup(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            return new Stream(
                call <= _failuresBeforeSuccess,
                () => Interlocked.Increment(ref _disposeCount));
        }

        private sealed class Stream : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly bool _fail;
            private readonly Action _onDispose;
            private bool _moved;

            public Stream(bool fail, Action onDispose)
            {
                _fail = fail;
                _onDispose = onDispose;
            }

            public ModelStreamEvent Current { get; private set; } = null!;

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return this;
            }

            public ValueTask<bool> MoveNextAsync()
            {
                if (_moved)
                {
                    return new ValueTask<bool>(false);
                }

                _moved = true;
                if (_fail)
                {
                    return ValueTask.FromException<bool>(new ModelProviderException("offline", isTransient: true));
                }

                Current = ModelStreamEvent.Terminal(Text("ok"));
                return new ValueTask<bool>(true);
            }

            public ValueTask DisposeAsync()
            {
                _onDispose();
                return ValueTask.FromException(new InvalidOperationException("cleanup failed"));
            }
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
                    new GameMediaGenerationProgress(
                        "rendering",
                        0.5,
                        "{\"frame\":1}",
                        new ResourceContent(
                            "data:image/png;base64,cHJldmlldw==",
                            "image/png",
                            "preview")),
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

    private sealed class FailingReceiptJournal : IGameActionJournal
    {
        private readonly IGameActionJournal _inner;

        public FailingReceiptJournal(IGameActionJournal inner)
        {
            _inner = inner;
        }

        public ValueTask<GameActionJournalEntry> ReserveAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken) =>
            _inner.ReserveAsync(intent, cancellationToken);

        public ValueTask<GameActionJournalEntry?> FindAsync(
            string operationId,
            CancellationToken cancellationToken) =>
            _inner.FindAsync(operationId, cancellationToken);

        public ValueTask<bool> MarkDispatchedAsync(string operationId, CancellationToken cancellationToken) =>
            _inner.MarkDispatchedAsync(operationId, cancellationToken);

        public ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken)
        {
            _ = receipt;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("simulated journal outage");
        }

        public ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(limit, cancellationToken);
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

    private sealed class RehydratingCheckpointStore : IGameWorkflowCheckpointStore
    {
        private readonly InMemoryGameWorkflowCheckpointStore _inner = new();

        public async ValueTask<GameWorkflowCheckpoint?> LoadAsync(
            string instanceId,
            CancellationToken cancellationToken)
        {
            var loaded = await _inner.LoadAsync(instanceId, cancellationToken);
            return loaded is null ? null : Rehydrate(loaded);
        }

        public async ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
            GameWorkflowCheckpoint checkpoint,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var saved = await _inner.SaveAsync(checkpoint, expectedRevision, cancellationToken);
            return new GameWorkflowCheckpointSaveResult(saved.Saved, Rehydrate(saved.Current));
        }

        private static GameWorkflowCheckpoint Rehydrate(GameWorkflowCheckpoint checkpoint)
        {
            var invocation = checkpoint.Invocation is null
                ? null
                : new GameWorkflowInvocationResult(
                    checkpoint.Invocation.InputId,
                    checkpoint.Invocation.Messages.Select(Rehydrate).ToArray(),
                    checkpoint.Invocation.Complete,
                    checkpoint.Invocation.Succeeded,
                    checkpoint.Invocation.Error);
            return new GameWorkflowCheckpoint(
                checkpoint.InstanceId,
                checkpoint.Workflow,
                checkpoint.Revision,
                checkpoint.NextStep,
                checkpoint.StateJson,
                checkpoint.Completed,
                checkpoint.Error,
                invocation);
        }

        private static AgentMessage Rehydrate(AgentMessage message) =>
            new(
                message.Role,
                message.Content,
                message.Timestamp,
                message.CustomRole,
                message.ToolCallId,
                message.ToolName,
                message.IsError,
                message.DetailsJson,
                message.Metadata,
                message.Model,
                message.StopReason,
                message.Usage,
                message.ErrorMessage);
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
        public ValueTask<GameTranscriptCompactionResult> CompactAsync(
            GameTranscriptCompactionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameTranscriptCompactionResult>((GameTranscriptCompactionResult)null!);
        }
    }
}
