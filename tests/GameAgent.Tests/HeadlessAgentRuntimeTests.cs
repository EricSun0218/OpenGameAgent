using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class HeadlessAgentRuntimeTests
{
    [Fact]
    public async Task RestartedSequentialIdsRemainUniqueAcrossRuns()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var firstRequest = CreateRequest();
        firstRequest.Run.RunId = "headless-run-1";
        var secondRequest = CreateRequest();
        secondRequest.Run.RunId = "headless-run-2";
        var firstRuntime = new HeadlessAgentRuntime(
            new ScriptedModelProvider(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}"""))),
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var secondRuntime = new HeadlessAgentRuntime(
            new ScriptedModelProvider(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}"""))),
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());

        await firstRuntime.RunAsync(firstRequest, cancellationToken: TestContext.Current.CancellationToken);
        await secondRuntime.RunAsync(secondRequest, cancellationToken: TestContext.Current.CancellationToken);

        var firstIds = (await store.ReadRunAsync(
                firstRequest.Run.RunId,
                TestContext.Current.CancellationToken))
            .Select(item => item.EventId)
            .ToArray();
        var secondIds = (await store.ReadRunAsync(
                secondRequest.Run.RunId,
                TestContext.Current.CancellationToken))
            .Select(item => item.EventId)
            .ToArray();
        Assert.NotEmpty(firstIds);
        Assert.Equal(firstIds.Length, firstIds.Distinct().Count());
        Assert.Equal(secondIds.Length, secondIds.Distinct().Count());
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        Assert.All(
            firstIds.Concat(secondIds),
            eventId =>
            {
                Assert.StartsWith(
                    "event:sha256:",
                    eventId,
                    StringComparison.Ordinal);
                Assert.InRange(
                    eventId.Length,
                    1,
                    RuntimeEventIdDerivation.MaximumLength);
                Assert.Matches("^[A-Za-z0-9._:-]+$", eventId);
            });
    }

    [Fact]
    public async Task KnownGameContextFailsClosedBeforeProviderDispatch()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var runtime = Runtime(provider, store, clock);
        var cases = new[]
        {
            ProtocolJson.ParseElement("""{"worldId":"world"}"""),
            GameContextEnvelope.ToJson(
                new GameContextCoordinate(
                    "other-world",
                    "prime",
                    saveRevision: 1))
        };

        foreach (var gameContext in cases)
        {
            var request = CreateRequest();
            request.Run.RunId = "game-context-" + Guid.NewGuid().ToString("N");
            request.Run.Extensions[GameContextEnvelope.ExtensionName] =
                gameContext;

            await Assert.ThrowsAsync<ArgumentException>(
                () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        }

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task ObservationScopeFailsClosedBeforeProviderDispatch()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var runtime = Runtime(provider, store, clock);
        var cases =
            new (Action<ObservationEnvelope> Mutate, string ReasonCode)[]
            {
                (
                    observation => observation.WorldId = "other-world",
                    "observation_world_mismatch"),
                (
                    observation => observation.SessionId = "other-session",
                    "observation_session_mismatch"),
                (
                    observation =>
                        observation.Visibility.AudienceIds =
                            new List<string> { "other-agent" },
                    "observation_audience_mismatch"),
                (
                    observation =>
                    {
                        observation.Visibility.Scope =
                            ObservationVisibilityScopes.Private;
                        observation.Visibility.AudienceIds =
                            new List<string>
                            {
                                "agent-demo",
                                "other-agent"
                            };
                    },
                    "observation_private_audience_invalid")
            };

        foreach (var testCase in cases)
        {
            var request = CreateRequest();
            request.Run.RunId =
                "observation-admission-" + Guid.NewGuid().ToString("N");
            testCase.Mutate(request.Observations[0]);

            var error = await Assert.ThrowsAsync<
                ObservationAdmissionException>(
                () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(testCase.ReasonCode, error.ReasonCode);
            Assert.Equal(0, runtime.ActiveRunCount);
        }

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task ProtocolDtoFieldsAreBoundedBeforeSerialization()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(
                maxInputUtf8Bytes: 4_096,
                inputJsonLimits: new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxStringUtf8Bytes: 4_096)));
        var cases = new (Action<HeadlessRunRequest> Mutate, string LimitCode)[]
        {
            (
                request => request.Run.AgentId = new string('a', 8_192),
                "agent_run_bytes_exceeded"),
            (
                request => request.Observations[0].Source =
                    new string('o', 8_192),
                "observation_bytes_exceeded"),
            (
                request => request.Tools[0].Description =
                    new string('t', 8_192),
                "tool_descriptor_bytes_exceeded")
        };

        foreach (var testCase in cases)
        {
            var request = CreateRequest();
            testCase.Mutate(request);

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(testCase.LimitCode, error.LimitCode);
            Assert.Equal(0, runtime.ActiveRunCount);
            Assert.Empty(store.Events);
        }

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ProtocolDtoCollectionsAreBoundedBeforeValidation()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var runtime = Runtime(provider, store, clock);
        var cases = new (Action<HeadlessRunRequest> Mutate, string LimitCode)[]
        {
            (
                request => request.Run.PendingOperationIds =
                    Enumerable.Repeat("duplicate-operation", 2_049).ToList(),
                "agent_run_items_exceeded"),
            (
                request => request.Observations[0].SubjectIds =
                    Enumerable.Repeat("duplicate-subject", 2_049).ToList(),
                "observation_items_exceeded"),
            (
                request => request.Tools[0].ConflictScopes =
                    Enumerable.Repeat("duplicate-scope", 2_049).ToList(),
                "tool_descriptor_items_exceeded")
        };

        foreach (var testCase in cases)
        {
            var request = CreateRequest();
            testCase.Mutate(request);

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(testCase.LimitCode, error.LimitCode);
            Assert.Equal(0, runtime.ActiveRunCount);
            Assert.Empty(store.Events);
        }

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task MisreportedInfiniteInputIsRejectedWithoutLeakingAdmission()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var runtime = Runtime(
            new ScriptedModelProvider(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}"""))),
            store,
            clock);
        var request = CreateRequest();
        request.Observations =
            new MisreportedInfiniteReadOnlyList<ObservationEnvelope>(
                request.Observations[0]);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("observation_count_exceeded", error.LimitCode);
        Assert.Equal(0, runtime.ActiveRunCount);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task JsonOnlyToolLoopCompletesAndFeedsReceiptBackToTheModel()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-0001",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement("""{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"decision":"eat","resource":"berries"}""")));
        var host = FakeGameHost.Returning(request => SucceededReceipt(request, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.True(outcome.IsTerminal);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, host.CallCount);
        Assert.Equal("eat", outcome.FinalOutput!.Value.GetProperty("decision").GetString());

        var firstInput = provider.Requests[0].Messages[0].Content;
        Assert.Equal(
            70,
            firstInput
                .GetProperty("observations")[0]
                .GetProperty("payload")
                .GetProperty("hunger")
                .GetInt32());

        var secondRequest = provider.Requests[1];
        var toolResult = Assert.Single(secondRequest.Messages, item => item.Role == "tool");
        Assert.Equal("call-0001", toolResult.ToolCallId);
        Assert.Equal(
            ReceiptStatuses.Succeeded,
            toolResult.Content.GetProperty("status").GetString());

        var expectedTrace = JsonDocument.Parse(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "expected-trace.json"));
        var expectedKinds = expectedTrace.RootElement
            .GetProperty("eventKinds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(expectedKinds, store.Events.Select(item => item.Kind).ToArray());
        Assert.Equal(
            Enumerable.Range(0, store.Events.Count).Select(value => (long)value),
            store.Events.Select(item => item.Sequence));
    }

    [Fact]
    public async Task Long_horizon_world_goal_can_inspect_act_observe_and_replan()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "inspect-1",
                Name = "inspect_region",
                Arguments = ProtocolJson.ParseElement(
                    """{"min":[0,0,0],"max":[2,1,0]}""")
            }),
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "place-1",
                Name = "place_cells",
                Arguments = ProtocolJson.ParseElement(
                    """{"material":"stone","cells":[[0,0,0],[1,0,0]]}""")
            }),
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "place-2",
                Name = "place_cells",
                Arguments = ProtocolJson.ParseElement(
                    """{"material":"stone","cells":[[0,0,0],[2,0,0]]}""")
            }),
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "inspect-2",
                Name = "inspect_region",
                Arguments = ProtocolJson.ParseElement(
                    """{"min":[0,0,0],"max":[2,1,0]}""")
            }),
            ModelResponse.Final(ProtocolJson.ParseElement(
                """{"goal":"foundation","status":"completed","completedSteps":2,"remainingSteps":0}""")));
        var cells = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1,0,0"] = "protected"
        };
        Exception? hostError = null;
        var host = FakeGameHost.Returning(request =>
        {
            try
            {
                JsonElement result;
                if (request.ActionName == "inspect_region")
                {
                    result = ProtocolJson.ParseElement(
                        $"{{\"cells\":{{\"0,0,0\":\"{Value("0,0,0")}\",\"1,0,0\":\"{Value("1,0,0")}\",\"2,0,0\":\"{Value("2,0,0")}\"}}}}");
                }
                else
                {
                    var placed = new List<string>();
                    var rejected = new List<string>();
                    foreach (var cell in request.Arguments.GetProperty("cells").EnumerateArray())
                    {
                        var key = string.Join(",", cell.EnumerateArray().Select(value => value.GetInt32()));
                        if (cells.ContainsKey(key))
                        {
                            rejected.Add(key);
                        }
                        else
                        {
                            cells[key] = request.Arguments.GetProperty("material").GetString()!;
                            placed.Add(key);
                        }
                    }

                    result = JsonArrayBuilder.Object(
                        ("placed", JsonArrayBuilder.Strings(placed)),
                        ("rejected", JsonArrayBuilder.Strings(rejected)),
                        ("worldRevision", JsonArrayBuilder.Number(cells.Count)));
                }

                return new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = result,
                    Retryable = false,
                    CommittedAt = clock.UtcNow,
                    ReceivedAt = clock.UtcNow
                };
            }
            catch (Exception exception)
            {
                hostError = exception;
                throw;
            }

            string Value(string key) => cells.TryGetValue(key, out var value) ? value : "air";
        });
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        request.Run.Budget.MaxTurns = 8;
        request.Run.Budget.MaxActions = 8;
        request.Tools = new[]
        {
            WorldTool("inspect_region", ToolEffects.PureRead),
            WorldTool("place_cells", ToolEffects.WorldCommand)
        };

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            outcome.Run.State == RunStates.Completed,
            $"state={outcome.Run.State}; reason={outcome.Run.TerminalReason}; "
            + $"pending={string.Join(',', outcome.Run.PendingOperationIds)}; "
            + $"hostError={hostError}; "
            + $"events={string.Join(';', store.Events.Select(item => item.Kind + ':' + item.Payload.GetRawText()))}");
        Assert.Equal(5, provider.CallCount);
        Assert.Equal(4, host.CallCount);
        Assert.Equal("protected", cells["1,0,0"]);
        Assert.Equal("stone", cells["0,0,0"]);
        Assert.Equal("stone", cells["2,0,0"]);
        Assert.Equal("completed", outcome.FinalOutput!.Value
            .GetProperty("status").GetString());

        static ToolDescriptor WorldTool(string name, string effect) => new()
        {
            Name = name,
            Version = "1",
            Description = "Inspect or change a bounded region owned by the game host.",
            ParametersSchema = ProtocolJson.ParseElement("""{"type":"object"}"""),
            ResultSchema = ProtocolJson.ParseElement("""{"type":"object"}"""),
            Effect = effect,
            ConflictScopes = new List<string> { "world:region" },
            ThreadAffinity = ThreadAffinities.EngineMainThread,
            TimeoutMs = 1_000,
            RetryPolicy = ToolRetryPolicies.Idempotent,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "world_interaction",
            Visibility = ToolVisibilities.Direct
        };
    }

    [Fact]
    public async Task GameDecisionMetadataReachesTheHostAction()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-context",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        request.Run.DecisionKey = "npc decision 12 / forage";
        request.Run.BatchId = "world-tick-12";
        GameContextEnvelope.Attach(
            request.Run,
            new GameContextCoordinate(
                request.Run.WorldId,
                "prime",
                saveRevision: 12,
                stateVersion: "world-v12",
                gameTime: new GameTimePoint(
                    "simulation",
                    "prime",
                    epoch: 1,
                    tick: 12)));

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            string.Equals(
                RunStates.Completed,
                outcome.Run.State,
                StringComparison.Ordinal),
            outcome.Run.TerminalReason);
        var action = Assert.Single(host.Requests);
        Assert.Equal("npc decision 12 / forage", action.DecisionKey);
        Assert.Equal("world-tick-12", action.BatchId);
        Assert.Equal("world-v12", action.BasedOnStateVersion);
        Assert.True(
            action.Extensions.ContainsKey(
                GameContextEnvelope.ExtensionName));
    }

    [Fact]
    public async Task UnknownReceiptStopsForReconciliationWithoutReplayingTheModel()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-unknown",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement("""{"resource":"berries"}""")
            }));
        var host = FakeGameHost.Returning(request => new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 0,
            Status = ReceiptStatuses.Unknown,
            Retryable = false,
            ReceivedAt = clock.UtcNow
        });
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.False(outcome.IsTerminal);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, host.CallCount);
        Assert.Single(outcome.Run.PendingOperationIds);
        Assert.Equal(RuntimeEventKinds.ActionReconciling, store.Events[^1].Kind);
    }

    [Fact]
    public async Task ActionRequestIsJournaledBeforeGameCodeCanObserveIt()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var sawDurableRequest = false;
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-ordering",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement("""{"resource":"berries"}""")
            }),
            ModelResponse.Final(ProtocolJson.ParseElement("""{"done":true}""")));
        var host = FakeGameHost.Returning(request =>
        {
            sawDurableRequest = store.Events.Any(item =>
                item.Kind == RuntimeEventKinds.ActionRequested
                && item.Payload.GetProperty("operationId").GetString() == request.OperationId);
            return SucceededReceipt(request, clock);
        });
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.True(sawDurableRequest);
        Assert.Equal(
            new[] { "agent:agent-demo", "resource:berries" },
            Assert.Single(host.Requests).ExpectedEffects);

        var requestedIndex = FindEventIndex(store.Events, RuntimeEventKinds.ActionRequested);
        var receivedIndex = FindEventIndex(store.Events, RuntimeEventKinds.ActionReceived);
        Assert.True(requestedIndex >= 0);
        Assert.True(receivedIndex > requestedIndex);
    }

    [Fact]
    public async Task RequestIsSnapshottedBeforePersistenceWait()
    {
        var store = new BlockingReadSessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-snapshot",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        var originalRun = request.Run;
        var originalTool = Assert.Single(request.Tools);
        var expectedRunId = originalRun.RunId;
        var expectedAgentId = originalRun.AgentId;
        var expectedWorldId = originalRun.WorldId;
        var expectedToolName = originalTool.Name;
        var expectedToolVersion = originalTool.Version;
        var originalRunJson = ProtocolJson.Serialize(originalRun);
        var originalToolJson = ProtocolJson.Serialize(originalTool);

        var runTask = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.WaitUntilBlockedAsync();

        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));
        Assert.Equal(originalToolJson, ProtocolJson.Serialize(originalTool));

        originalRun.RunId = "caller-mutated-run";
        originalRun.AgentId = "caller-mutated-agent";
        originalRun.WorldId = "caller-mutated-world";
        originalRun.State = RunStates.Failed;
        originalRun.Budget.MaxTurns = 0;
        originalRun.Usage.Turns = 700;
        originalRun.PendingOperationIds.Add("caller-operation");
        originalTool.Name = "caller_mutated_tool";
        originalTool.Version = "99.0.0";
        originalTool.ConflictScopes.Add("caller:mutation");
        request.Tools = Array.Empty<ToolDescriptor>();
        var callerRunJson = ProtocolJson.Serialize(originalRun);
        var callerToolJson = ProtocolJson.Serialize(originalTool);
        var callerTools = request.Tools;

        store.Release();
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotSame(originalRun, outcome.Run);
        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(expectedRunId, outcome.Run.RunId);
        Assert.Equal(expectedAgentId, outcome.Run.AgentId);
        Assert.Equal(expectedWorldId, outcome.Run.WorldId);
        Assert.Equal(2, outcome.Run.Usage.Turns);

        Assert.All(
            provider.Requests,
            modelRequest =>
            {
                Assert.Equal(expectedRunId, modelRequest.RunId);
                var tool = Assert.Single(modelRequest.Tools);
                Assert.NotSame(originalTool, tool);
                Assert.Equal(expectedToolName, tool.Name);
                Assert.Equal(expectedToolVersion, tool.Version);
            });
        var action = Assert.Single(host.Requests);
        Assert.Equal(expectedRunId, action.RunId);
        Assert.Equal(expectedAgentId, action.AgentId);
        Assert.Equal(expectedWorldId, action.WorldId);
        Assert.Equal(expectedToolName, action.ActionName);
        Assert.Equal(expectedToolVersion, action.ActionVersion);

        Assert.All(
            store.Events,
            runtimeEvent => Assert.Equal(expectedRunId, runtimeEvent.RunId));
        var requested = Assert.Single(
            store.Events,
            runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKinds.ActionRequested);
        Assert.Equal(
            expectedToolName,
            requested.Payload.GetProperty("actionName").GetString());

        Assert.Equal(callerRunJson, ProtocolJson.Serialize(originalRun));
        Assert.Equal(callerToolJson, ProtocolJson.Serialize(originalTool));
        Assert.Same(callerTools, request.Tools);
    }

    [Fact]
    public async Task ProviderCannotMutateAuthoritativeToolsOrMessages()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new MutatingModelRequestProvider();
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        var callerRunJson = ProtocolJson.Serialize(request.Run);
        var callerTool = Assert.Single(request.Tools);
        var callerToolJson = ProtocolJson.Serialize(callerTool);
        var expectedEffect = callerTool.Effect;
        var expectedTimeout = callerTool.TimeoutMs;

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal("gather_food", provider.SecondTurnToolName);
        Assert.Equal("user", provider.SecondTurnFirstMessageRole);
        Assert.Equal(70, provider.SecondTurnObservedHunger);
        var action = Assert.Single(host.Requests);
        Assert.Equal("gather_food", action.ActionName);
        Assert.Equal(
            expectedTimeout,
            (int)(action.Deadline!.Value - action.RequestedAt)
                .TotalMilliseconds);
        var toolStarted = Assert.Single(
            store.Events,
            runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKinds.ToolStarted);
        Assert.Equal(
            expectedEffect,
            toolStarted.Payload.GetProperty("effect").GetString());
        Assert.Equal(callerRunJson, ProtocolJson.Serialize(request.Run));
        Assert.Equal(callerToolJson, ProtocolJson.Serialize(callerTool));
    }

    [Fact]
    public async Task ProviderResponseIsSnapshottedBeforeLaterAwait()
    {
        var store = new NthAppendBlockingSessionStore(blockedAppendNumber: 3);
        var clock = new FakeRuntimeClock();
        var provider = new MutableResponseProvider();
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var runTask = runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.WaitUntilBlockedAsync();
        provider.FirstToolCall!.ToolCallId = "call-mutated-late";
        provider.FirstToolCall.Name = "provider_mutated_tool";
        provider.FirstToolCall.Arguments =
            ProtocolJson.ParseElement("""{"resource":"roots"}""");
        provider.FirstResponse!.Usage.InputTokens = 900;
        provider.FirstResponse.Usage.CostUsd = "0.09";

        store.Release();
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(7, outcome.Run.Usage.InputTokens);
        Assert.Equal("0.01", outcome.Run.Usage.CostUsd);
        var action = Assert.Single(host.Requests);
        Assert.Equal("call-response-snapshot", action.ToolCallId);
        Assert.Equal(
            "berries",
            action.Arguments.GetProperty("resource").GetString());
        var toolStarted = Assert.Single(
            store.Events,
            runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKinds.ToolStarted);
        Assert.Equal(
            "call-response-snapshot",
            toolStarted.Payload.GetProperty("toolCallId").GetString());
    }

    [Fact]
    public async Task HostCannotMutateAuthoritativeActionRequest()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-host-mutation",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var host = new MutatingActionHost(clock);
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        var callerRunJson = ProtocolJson.Serialize(request.Run);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal("gather_food", host.ReceivedActionName);
        Assert.NotNull(host.ReceivedOperationId);
        var actionRequested = Assert.Single(
            store.Events,
            runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKinds.ActionRequested);
        Assert.Equal(
            host.ReceivedOperationId,
            actionRequested.Payload.GetProperty("operationId").GetString());
        Assert.Equal(
            "gather_food",
            actionRequested.Payload.GetProperty("actionName").GetString());
        var actionReceived = Assert.Single(
            store.Events,
            runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKinds.ActionReceived);
        Assert.Equal(
            host.ReceivedOperationId,
            actionReceived.Payload.GetProperty("operationId").GetString());
        Assert.Equal(callerRunJson, ProtocolJson.Serialize(request.Run));
    }

    [Fact]
    public async Task ConcurrentCallsWithSameRequestRejectDuplicateRun()
    {
        var store = new BlockingSessionStore(expectedBlockedAppends: 1);
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        request.Run.RunId = "run-shared-request";
        var originalRun = request.Run;
        var originalRunJson = ProtocolJson.Serialize(originalRun);

        var firstTask = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.WaitUntilBlockedAsync();

        Assert.Equal(1, runtime.ActiveRunCount);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));
        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(request.Run.RunId, duplicate.RunId);
        Assert.Equal(
            DuplicateRunException.StableReasonCode,
            duplicate.ReasonCode);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));

        store.Release();
        var outcome = await firstTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.CallCount);
        Assert.NotSame(originalRun, outcome.Run);
        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(1, outcome.Run.Usage.Turns);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));
        Assert.Equal(
            Enumerable.Range(0, 5).Select(value => (long)value),
            store.Events.Select(runtimeEvent => runtimeEvent.Sequence));
        Assert.Equal(0, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task HeadlessCapacityFailsFastAndDuplicateWinsAtCapacity()
    {
        var store = new BlockingReadSessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxActiveRuns: 1));
        var first = CreateRequest();
        first.Run.RunId = "headless-capacity-first";
        var duplicateRequest = CreateRequest();
        duplicateRequest.Run.RunId = first.Run.RunId;
        var overflow = CreateRequest();
        overflow.Run.RunId = "headless-capacity-overflow";

        var active = runtime.RunAsync(first, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.WaitUntilBlockedAsync();

        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(duplicateRequest, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var capacity = await Assert.ThrowsAsync<
            RunWorkloadCapacityExceededException>(
            () => runtime.RunAsync(overflow, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            DuplicateRunException.StableReasonCode,
            duplicate.ReasonCode);
        Assert.Equal(
            RunWorkloadCapacityReasonCodes.MaxActiveRuns,
            capacity.ReasonCode);
        Assert.Equal(1, capacity.Limit);
        Assert.Equal(1, runtime.ActiveRunCount);
        Assert.Equal(0, provider.CallCount);

        store.Release();
        var outcome = await active.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(0, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task HeadlessCancellationBeforeHistoryRestoresCapacity()
    {
        var store = new BlockingReadSessionStore();
        var clock = new FakeRuntimeClock();
        var runtime = new HeadlessAgentRuntime(
            new ConcurrentFinalModelProvider(),
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxActiveRuns: 1));
        var request = CreateRequest();
        request.Run.RunId = "headless-cancelled-admission";
        using var cancellation = new CancellationTokenSource();

        var cancelled = runtime
            .RunAsync(request, cancellation.Token)
            .AsTask();
        await store.WaitUntilBlockedAsync();
        Assert.Equal(1, runtime.ActiveRunCount);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled);
        Assert.Equal(0, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task ConcurrentHeadlessOverflowNeverExceedsConfiguredLimit()
    {
        const int maxActiveRuns = 8;
        var store = new BlockingSessionStore(
            expectedBlockedAppends: maxActiveRuns);
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxActiveRuns));
        var admitted = Enumerable.Range(0, maxActiveRuns)
            .Select(
                index =>
                {
                    var request = CreateRequest();
                    request.Run.RunId = $"headless-admitted-{index}";
                    request.Run.Budget.MaxDurationMs = 60_000;
                    return runtime.RunAsync(request).AsTask();
                })
            .ToArray();
        await store.WaitUntilBlockedAsync();
        Assert.Equal(maxActiveRuns, runtime.ActiveRunCount);

        var rejected = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(
                    async index =>
                    {
                        var request = CreateRequest();
                        request.Run.RunId = $"headless-rejected-{index}";
                        return await Assert.ThrowsAsync<
                            RunWorkloadCapacityExceededException>(
                            () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());
                    }));

        Assert.All(
            rejected,
            exception => Assert.Equal(
                RunWorkloadCapacityReasonCodes.MaxActiveRuns,
                exception.ReasonCode));
        Assert.Equal(maxActiveRuns, runtime.ActiveRunCount);

        store.Release();
        var outcomes = await Task.WhenAll(admitted)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(
            outcomes,
            outcome => Assert.Equal(RunStates.Completed, outcome.Run.State));
        Assert.Equal(0, runtime.ActiveRunCount);
        Assert.Equal(maxActiveRuns, provider.CallCount);
    }

    [Fact]
    public async Task ConcurrentDistinctRunsOwnIndependentEventSequences()
    {
        var store = new BlockingSessionStore(expectedBlockedAppends: 2);
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var firstRequest = CreateRequest();
        firstRequest.Run.RunId = "run-concurrent-first";
        var secondRequest = CreateRequest();
        secondRequest.Run.RunId = "run-concurrent-second";

        var firstTask = runtime.RunAsync(firstRequest, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var secondTask = runtime.RunAsync(secondRequest, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.WaitUntilBlockedAsync();

        store.Release();
        var outcomes = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, provider.CallCount);
        Assert.All(
            outcomes,
            outcome => Assert.Equal(RunStates.Completed, outcome.Run.State));
        foreach (var runId in new[]
                 {
                     firstRequest.Run.RunId,
                     secondRequest.Run.RunId
                 })
        {
            Assert.Equal(
                Enumerable.Range(0, 5).Select(value => (long)value),
                store.Events
                    .Where(runtimeEvent => runtimeEvent.RunId == runId)
                    .Select(runtimeEvent => runtimeEvent.Sequence));
        }
    }

    [Fact]
    public async Task SequentialRunsEachStartEventSequenceAtZero()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var firstRequest = CreateRequest();
        firstRequest.Run.RunId = "run-sequence-first";
        var secondRequest = CreateRequest();
        secondRequest.Run.RunId = "run-sequence-second";

        var firstOutcome = await runtime.RunAsync(firstRequest, cancellationToken: TestContext.Current.CancellationToken);
        var secondOutcome = await runtime.RunAsync(secondRequest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, firstOutcome.Run.State);
        Assert.Equal(RunStates.Completed, secondOutcome.Run.State);
        foreach (var runId in new[]
                 {
                     firstRequest.Run.RunId,
                     secondRequest.Run.RunId
                 })
        {
            Assert.Equal(
                Enumerable.Range(0, 5).Select(value => (long)value),
                store.Events
                    .Where(runtimeEvent => runtimeEvent.RunId == runId)
                    .Select(runtimeEvent => runtimeEvent.Sequence));
        }
    }

    [Theory]
    [InlineData(RunStates.Completed, false)]
    [InlineData(RunStates.Reconciling, true)]
    [InlineData(RunStates.Queued, true)]
    public async Task NewRunRejectsInvalidEntryStateBeforeAnyAdapterCall(
        string state,
        bool includePendingOperation)
    {
        var store = new CountingSessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "No tool call expected."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());
        var request = CreateRequest();
        request.Run.State = state;
        if (includePendingOperation)
        {
            request.Run.PendingOperationIds.Add("operation-existing");
        }

        var originalRunJson = ProtocolJson.Serialize(request.Run);

        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, store.ReadCallCount);
        Assert.Equal(0, store.AppendCallCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, host.CallCount);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(request.Run));
    }

    [Fact]
    public async Task CompletedRunIdCannotBeReused()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var firstRequest = CreateRequest();
        firstRequest.Run.RunId = "run-cannot-reuse";
        var secondRequest = CreateRequest();
        secondRequest.Run.RunId = firstRequest.Run.RunId;
        var secondRunJson = ProtocolJson.Serialize(secondRequest.Run);

        var firstOutcome = await runtime.RunAsync(firstRequest, cancellationToken: TestContext.Current.CancellationToken);
        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(secondRequest, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(RunStates.Completed, firstOutcome.Run.State);
        Assert.Equal(secondRequest.Run.RunId, duplicate.RunId);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(5, store.Events.Count);
        Assert.Equal(secondRunJson, ProtocolJson.Serialize(secondRequest.Run));
    }

    [Fact]
    public async Task FailedHistoryCheckReleasesRunAdmission()
    {
        var store = new FailFirstReadSessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ConcurrentFinalModelProvider();
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
        var firstRequest = CreateRequest();
        firstRequest.Run.RunId = "run-history-retry";
        var secondRequest = CreateRequest();
        secondRequest.Run.RunId = firstRequest.Run.RunId;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunAsync(firstRequest, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, runtime.ActiveRunCount);
        var outcome = await runtime.RunAsync(secondRequest, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task HostFailureAfterWriteAheadRemainsReconciling()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-uncertain",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "host failed after accepting the operation"));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.False(outcome.IsTerminal);
        Assert.Single(outcome.Run.PendingOperationIds);
        Assert.Equal(1, host.CallCount);
        Assert.Equal(
            RuntimeEventKinds.ActionReconciling,
            store.Events[^1].Kind);
        Assert.DoesNotContain(
            store.Events,
            item => item.Kind is RuntimeEventKinds.RunFailed
                or RuntimeEventKinds.RunCancelled);
    }

    [Fact]
    public async Task ProviderTokenUsageOverBudgetEndsBudgetExhausted()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    InputTokens = 1_500,
                    OutputTokens = 600,
                    CostUsd = "0"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxTokens = 2_000;
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_tokens", outcome.Run.TerminalReason);
        Assert.Equal(1_500, outcome.Run.Usage.InputTokens);
        Assert.Equal(600, outcome.Run.Usage.OutputTokens);
        Assert.Null(outcome.FinalOutput);
    }

    [Fact]
    public async Task ProviderCostUsageOverBudgetEndsBudgetExhausted()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    InputTokens = 1,
                    OutputTokens = 1,
                    CostUsd = "0.11"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxCostUsd = "0.10";
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_cost", outcome.Run.TerminalReason);
        Assert.Equal("0.11", outcome.Run.Usage.CostUsd);
    }

    [Fact]
    public async Task ProviderTokenUsageAtExactBudgetCanComplete()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    InputTokens = 1_400,
                    OutputTokens = 600,
                    CostUsd = "0"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxTokens = 2_000;
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(1_400, outcome.Run.Usage.InputTokens);
        Assert.Equal(600, outcome.Run.Usage.OutputTokens);
        Assert.True(outcome.FinalOutput.HasValue);
    }

    [Fact]
    public async Task ProviderCostUsageAtExactBudgetCanComplete()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    CostUsd = "0.10"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxCostUsd = "0.10";
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal("0.1", outcome.Run.Usage.CostUsd);
        Assert.True(outcome.FinalOutput.HasValue);
    }

    [Fact]
    public async Task ClockElapsedDuringProviderEnforcesDurationBudget()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new AdvancingModelProvider(
            clock,
            TimeSpan.FromMilliseconds(20),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 10;
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_duration", outcome.Run.TerminalReason);
        Assert.True(outcome.Run.Usage.DurationMs >= 20);
    }

    [Fact]
    public async Task AlreadyElapsedDeadlineRejectsSynchronousOperations()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 1;
        request.Run.Usage.DurationMs = 1;
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_duration", outcome.Run.TerminalReason);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task HardDeadlineReturnsWhenProviderIgnoresCancellation()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new CancellationIgnoringModelProvider();
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxActiveRuns: 1));

        var runTask = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Equal(1, runtime.ActiveRunCount);
            var overflow = CreateRequest();
            overflow.Run.RunId = "provider-detached-overflow";
            await Assert.ThrowsAsync<RunWorkloadCapacityExceededException>(
                () => runtime.RunAsync(overflow, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            provider.Release.TrySetResult(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"late":true}""")));
        }

        await WaitUntilAsync(
            () => runtime.ActiveRunCount == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HostDeadlineKeepsUncertainSideEffectReconciling()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-deadline",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = new CancellationIgnoringHost(clock);
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 500;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var runTask = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Reconciling, outcome.Run.State);
            Assert.Single(outcome.Run.PendingOperationIds);
            Assert.Equal(
                RuntimeEventKinds.ActionReconciling,
                store.Events[^1].Kind);
        }
        finally
        {
            host.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ToolDeadlineKeepsLateHostReceiptReconciling()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-tool-deadline",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = new CancellationIgnoringHost(clock);
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 5_000;
        request.Tools[0].TimeoutMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var runTask = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Reconciling, outcome.Run.State);
            Assert.Single(outcome.Run.PendingOperationIds);
            Assert.Equal(
                RuntimeEventKinds.ActionReconciling,
                store.Events[^1].Kind);
            Assert.DoesNotContain(
                store.Events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.NotNull(host.LastRequest);
            Assert.Equal(
                25,
                (int)(host.LastRequest!.Deadline!.Value
                      - host.LastRequest.RequestedAt).TotalMilliseconds);
        }
        finally
        {
            host.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task JournalLatencyConsumesTheAbsoluteActionDeadline()
    {
        var clock = new FakeRuntimeClock();
        var store = new ActionRequestAdvancingStore(
            clock,
            TimeSpan.FromMilliseconds(25));
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-journal-deadline",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 5_000;
        request.Tools[0].TimeoutMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(0, host.CallCount);
        var receipt = Assert.Single(
            store.Events,
            item => item.Kind == RuntimeEventKinds.ActionReceived);
        Assert.Equal(
            "action_deadline_expired",
            receipt.Payload.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task TimedOutAppendCannotBeOvertakenByTerminalCheckpoint()
    {
        var clock = new FakeRuntimeClock();
        var store = new CancellationIgnoringActionRequestStore();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-ordered-journal",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = FakeGameHost.Returning(
            request => SucceededReceipt(request, clock));
        var request = CreateRequest();
        request.Run.RunId = "ordered-journal-timeout";
        request.Run.Budget.MaxDurationMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxActiveRuns: 1));

        var run = runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await store.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(75, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        Assert.Equal(1, runtime.ActiveRunCount);
        var overflow = CreateRequest();
        overflow.Run.RunId = "ordered-journal-overflow";
        await Assert.ThrowsAsync<RunWorkloadCapacityExceededException>(
            () => runtime.RunAsync(overflow, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.DoesNotContain(
            store.Events,
            item => item.Kind == RuntimeEventKinds.ActionReconciling);

        store.Release.TrySetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.Equal(0, host.CallCount);
        var kinds = store.Events.Select(item => item.Kind).ToArray();
        Assert.True(
            Array.IndexOf(kinds, RuntimeEventKinds.ActionRequested)
            < Array.IndexOf(kinds, RuntimeEventKinds.ActionReconciling));
        Assert.Equal(
            Enumerable.Range(0, store.Events.Count).Select(value => (long)value),
            store.Events.Select(item => item.Sequence));
        Assert.Equal(0, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task UtcRollbackCannotExtendTheActionDeadline()
    {
        var clock = new FakeRuntimeClock();
        var store = new ActionRequestAdvancingStore(
            clock,
            TimeSpan.FromHours(-1));
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-rollback-deadline",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = new DelayedSuccessfulHost(
            clock,
            TimeSpan.FromMilliseconds(75));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 5_000;
        request.Tools[0].TimeoutMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.Equal(1, host.CallCount);
        Assert.Single(outcome.Run.PendingOperationIds);
        Assert.DoesNotContain(
            store.Events,
            item => item.Kind == RuntimeEventKinds.ActionReceived);
        await WaitUntilAsync(
            () => runtime.InFlightActionCount == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ActionSetupLatencyConsumesTheOriginalMonotonicDeadline()
    {
        var clock = new SlowRollbackActionClock();
        var store = new InMemorySessionStore();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-setup-deadline",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "An expired action must not reach the host."));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 5_000;
        request.Tools[0].TimeoutMs = 10;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new ActionClockArmingIds(clock));

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(0, host.CallCount);
        var receipt = Assert.Single(
            store.Events,
            item => item.Kind == RuntimeEventKinds.ActionReceived);
        Assert.Equal(
            "action_deadline_expired",
            receipt.Payload.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreDispatchClockFailureReleasesActionAndCancellationCapacity(
        int throwOnRead)
    {
        var clock = new ArmedNthReadThrowingClock();
        var store = new ActionRequestClockArmingStore(
            clock,
            throwOnRead);
        var dispatcher = new BoundedCancellationDispatcher(capacity: 4);
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "A failed pre-dispatch check must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            new ScriptedModelProvider(
                ModelResponse.CallTools(new ModelToolCall
                {
                    ToolCallId = "call-clock-failure",
                    Name = "gather_food",
                    Arguments = ProtocolJson.ParseElement(
                        """{"resource":"berries"}""")
                })),
            host,
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxInFlightActions: 1),
            dispatcher);

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.Equal(0, host.CallCount);
        Assert.Equal(0, runtime.InFlightActionCount);
        await WaitUntilAsync(
            () => dispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReceiptObservedAfterAbsoluteDeadlineIsReconciled()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-clock-late",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var host = new ClockAdvancingHost(
            clock,
            TimeSpan.FromMilliseconds(30));
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 5_000;
        request.Tools[0].TimeoutMs = 25;
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, outcome.Run.State);
        Assert.Equal(1, host.CallCount);
        Assert.Single(outcome.Run.PendingOperationIds);
        Assert.DoesNotContain(
            store.Events,
            item => item.Kind == RuntimeEventKinds.ActionReceived);
    }

    [Fact]
    public async Task DetachedHostActionsRemainGloballyBoundedUntilTheyFinish()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new PerRunToolThenFinalProvider();
        var host = new CapacityHoldingHost(clock);
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxInFlightActions: 1));
        var first = CreateRequest();
        first.Run.RunId = "run-detached-first";
        first.Tools[0].TimeoutMs = 25;

        var firstOutcome = await runtime.RunAsync(first, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Reconciling, firstOutcome.Run.State);
        Assert.Equal(1, runtime.InFlightActionCount);
        Assert.Equal(1, host.CallCount);

        var second = CreateRequest();
        second.Run.RunId = "run-detached-second";
        var secondOutcome = await runtime.RunAsync(second, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, secondOutcome.Run.State);
        Assert.Equal(1, host.CallCount);
        Assert.Equal(1, runtime.InFlightActionCount);
        var capacityReceipt = Assert.Single(
            store.Events,
            item => item.RunId == second.Run.RunId
                    && item.Kind == RuntimeEventKinds.ActionReceived);
        Assert.Equal(
            "action_capacity_exceeded",
            capacityReceipt.Payload.GetProperty("errorCode").GetString());

        host.Release.TrySetResult();
        await host.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => runtime.InFlightActionCount == 0,
            TimeSpan.FromSeconds(2));

        var third = CreateRequest();
        third.Run.RunId = "run-detached-third";
        var thirdOutcome = await runtime.RunAsync(third, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Completed, thirdOutcome.Run.State);
        Assert.Equal(2, host.CallCount);
        Assert.Equal(0, runtime.InFlightActionCount);
    }

    [Fact]
    public async Task BlockingCancellationCallbackDoesNotHoldFinishedActionSlot()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var host = new BlockingCancellationCallbackHost(clock);
        var runtime = new HeadlessAgentRuntime(
            new PerRunToolThenFinalProvider(),
            host,
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxInFlightActions: 1));
        var first = CreateRequest();
        first.Run.RunId = "run-blocking-callback-first";
        first.Tools[0].TimeoutMs = 25;

        try
        {
            var firstOutcome = await runtime.RunAsync(first, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await host.CallbackInvoked.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Reconciling, firstOutcome.Run.State);
            Assert.Equal(1, runtime.InFlightActionCount);

            host.ActionRelease.TrySetResult();
            await host.FirstCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => runtime.InFlightActionCount == 0,
                TimeSpan.FromSeconds(2));

            var second = CreateRequest();
            second.Run.RunId = "run-blocking-callback-second";
            var secondOutcome = await runtime.RunAsync(second, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, secondOutcome.Run.State);
            Assert.Equal(2, host.CallCount);
            Assert.Equal(0, runtime.InFlightActionCount);
        }
        finally
        {
            host.CallbackRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task BlockingActionCancellationExhaustsBoundedCapacityBeforeHost()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var host = new BlockingCancellationCallbackHost(clock);
        var dispatcher = new BoundedCancellationDispatcher(capacity: 2);
        var runtime = new HeadlessAgentRuntime(
            new PerRunToolThenFinalProvider(),
            host,
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(maxInFlightActions: 1),
            dispatcher);
        var first = CreateRequest();
        first.Run.RunId = "run-bounded-cancel-first";
        first.Tools[0].TimeoutMs = 25;

        try
        {
            var firstOutcome = await runtime.RunAsync(first, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await host.CallbackInvoked.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(RunStates.Reconciling, firstOutcome.Run.State);
            Assert.Equal(1, dispatcher.ActiveReservations);

            host.ActionRelease.TrySetResult();
            await host.FirstCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => runtime.InFlightActionCount == 0,
                TimeSpan.FromSeconds(2));

            var second = CreateRequest();
            second.Run.RunId = "run-bounded-cancel-second";
            var secondOutcome = await runtime.RunAsync(second, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, secondOutcome.Run.State);
            Assert.Equal(1, host.CallCount);
            Assert.Equal(1, dispatcher.ActiveReservations);
            var capacityReceipt = Assert.Single(
                store.Events,
                item => item.RunId == second.Run.RunId
                        && item.Kind
                        == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                "action_cancellation_capacity_exceeded",
                capacityReceipt.Payload
                    .GetProperty("errorCode")
                    .GetString());
        }
        finally
        {
            host.ActionRelease.TrySetResult();
            host.CallbackRelease.TrySetResult();
        }

        await WaitUntilAsync(
            () => dispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BlockingRunDeadlineCallbackCannotBlockRunCompletion()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new BlockingDeadlineCallbackProvider();
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var runtime = new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched.")),
            store,
            clock,
            new SequentialIdGenerator(),
            new HeadlessAgentRuntimeLimits(),
            dispatcher);
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 25;

        try
        {
            var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            await provider.CallbackInvoked.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Equal(1, runtime.ActiveRunCount);
            Assert.Equal(1, dispatcher.ActiveReservations);
        }
        finally
        {
            provider.Release.TrySetResult();
        }

        await WaitUntilAsync(
            () => dispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, runtime.ActiveRunCount);
    }

    [Fact]
    public async Task MutatedInvalidProviderUsageFailsClosed()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var response = ModelResponse.Final(
            ProtocolJson.ParseElement("""{"done":true}"""));
        response.Usage.CostUsd = "not-a-cost";
        var runtime = Runtime(
            new ScriptedModelProvider(response),
            store,
            clock);

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(RuntimeEventKinds.RunFailed, store.Events[^1].Kind);
        Assert.Equal("0", outcome.Run.Usage.CostUsd);
    }

    [Fact]
    public async Task UnavailableProviderCostIsNotTreatedAsZeroCost()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var runtime = Runtime(
            new ScriptedModelProvider(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}"""),
                    new ProviderUsage
                    {
                        InputTokens = 10,
                        OutputTokens = 2,
                        CostUsd = "0",
                        ProviderTotalTokens = 12,
                        Availability =
                            UsageAvailabilityStates.CostUnavailable
                    })),
            store,
            clock);

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal(
            "provider_cost_unavailable",
            outcome.Run.TerminalReason);
        Assert.Equal(10, outcome.Run.Usage.InputTokens);
        Assert.Equal(2, outcome.Run.Usage.OutputTokens);
        Assert.Equal(1, outcome.Run.Usage.ProviderUsageSamples);
        Assert.Equal(12, outcome.Run.Usage.ProviderTotalTokens);
        Assert.Equal("0", outcome.Run.Usage.CostUsd);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            outcome.Run.Usage.Availability);
        Assert.True(outcome.Run.Usage.HasUnaccountedUsage);
        Assert.Equal(
            1,
            outcome.Run.Usage.UnaccountedProviderAttempts);
    }

    [Fact]
    public async Task InvalidProviderPayloadStillChargesValidatedUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var response = ModelResponse.CallTools(
            new ProviderUsage
            {
                InputTokens = 5,
                OutputTokens = 2,
                CostUsd = "0.02"
            },
            new ModelToolCall
            {
                ToolCallId = "call-duplicate",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            },
            new ModelToolCall
            {
                ToolCallId = "call-duplicate",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"roots"}""")
            });
        var provider = new ScriptedModelProvider(response);
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 5,
            outputTokens: 2,
            costUsd: "0.02");
    }

    [Fact]
    public async Task FinalResponseWithToolCallsFailsAfterChargingUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var response = ModelResponse.CallTools(
            new ProviderUsage
            {
                InputTokens = 2_500,
                OutputTokens = 3,
                CostUsd = "0.03"
            },
            new ModelToolCall
            {
                ToolCallId = "call-final-with-tool",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            });
        var isFinal = typeof(ModelResponse).GetProperty(
            nameof(ModelResponse.IsFinal));
        Assert.NotNull(isFinal);
        isFinal.SetValue(response, true);
        var provider = new ScriptedModelProvider(response);
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 2_500,
            outputTokens: 3,
            costUsd: "0.03");
    }

    [Fact]
    public async Task NonFinalResponseWithoutToolsFailsAfterChargingUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ProviderUsage
            {
                InputTokens = 11,
                OutputTokens = 4,
                CostUsd = "0.04"
            }));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 11,
            outputTokens: 4,
            costUsd: "0.04");
    }

    [Fact]
    public async Task OversizedToolArgumentsFailAfterChargingUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var oversizedArguments = ProtocolJson.ParseElement(
            "{\"value\":\""
            + new string('x', 270_000)
            + "\"}");
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(
                new ProviderUsage
                {
                    InputTokens = 13,
                    OutputTokens = 6,
                    CostUsd = "0.05"
                },
                new ModelToolCall
                {
                    ToolCallId = "call-oversized",
                    Name = "gather_food",
                    Arguments = oversizedArguments
                }));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 13,
            outputTokens: 6,
            costUsd: "0.05");
    }

    [Fact]
    public async Task TooManyToolCallsFailAfterChargingUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var calls = Enumerable.Range(0, 129)
            .Select(index => new ModelToolCall
            {
                ToolCallId = $"call-{index}",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement("""{"count":1}""")
            })
            .ToArray();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(
                new ProviderUsage
                {
                    InputTokens = 17,
                    OutputTokens = 8,
                    CostUsd = "0.06"
                },
                calls));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 17,
            outputTokens: 8,
            costUsd: "0.06");
    }

    [Fact]
    public async Task AggregateToolArgumentsLimitFailsAfterChargingUsage()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var argument = ProtocolJson.ParseElement(
            "{\"values\":[\""
            + new string('a', 60_000)
            + "\",\""
            + new string('b', 60_000)
            + "\",\""
            + new string('c', 60_000)
            + "\",\""
            + new string('d', 60_000)
            + "\"]}");
        var calls = Enumerable.Range(0, 5)
            .Select(index => new ModelToolCall
            {
                ToolCallId = $"call-{index}",
                Name = "gather_food",
                Arguments = argument
            })
            .ToArray();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(
                new ProviderUsage
                {
                    InputTokens = 19,
                    OutputTokens = 9,
                    CostUsd = "0.07"
                },
                calls));
        var host = FakeGameHost.Returning(
            _ => throw new InvalidOperationException(
                "Invalid provider output must not reach the host."));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        AssertFailedUsage(
            outcome,
            store.Events,
            host,
            inputTokens: 19,
            outputTokens: 9,
            costUsd: "0.07");
    }

    [Fact]
    public void ModelResponseFactoryRejectsInvalidProviderUsage()
    {
        Assert.Throws<ArgumentException>(
            () => ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    InputTokens = -1,
                    CostUsd = "0"
                }));
    }

    [Fact]
    public async Task TokenUsageAdditionOverflowSaturatesAndExhaustsBudget()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    InputTokens = 2,
                    CostUsd = "0"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxTokens = int.MaxValue;
        request.Run.Usage.InputTokens = int.MaxValue - 1;
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_tokens", outcome.Run.TerminalReason);
        Assert.Equal(int.MaxValue, outcome.Run.Usage.InputTokens);
    }

    [Fact]
    public async Task CostUsageAdditionOverflowSaturatesAndExhaustsBudget()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}"""),
                new ProviderUsage
                {
                    CostUsd = "2"
                }));
        var request = CreateRequest();
        request.Run.Budget.MaxCostUsd =
            "79228162514264337593543950335";
        request.Run.Usage.CostUsd =
            "79228162514264337593543950334";
        var runtime = Runtime(provider, store, clock);

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_cost", outcome.Run.TerminalReason);
        Assert.Equal(
            "79228162514264337593543950335",
            outcome.Run.Usage.CostUsd);
    }

    [Fact]
    public async Task MaxTurnsUsesBudgetTrackerBeforeAnotherProviderCall()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-last-turn",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement("""{"done":true}""")));
        var request = CreateRequest();
        request.Run.Budget.MaxTurns = 1;
        var host = FakeGameHost.Returning(
            action => SucceededReceipt(action, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_turns", outcome.Run.TerminalReason);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, host.CallCount);
    }

    [Fact]
    public async Task MaxActionsUsesBudgetTrackerBeforeHostDispatch()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-over-action-budget",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }));
        var request = CreateRequest();
        request.Run.Budget.MaxActions = 0;
        var host = FakeGameHost.Returning(
            action => SucceededReceipt(action, clock));
        var runtime = new HeadlessAgentRuntime(
            provider,
            host,
            store,
            clock,
            new SequentialIdGenerator());

        var outcome = await runtime.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_actions", outcome.Run.TerminalReason);
        Assert.Equal(0, host.CallCount);
    }

    private static HeadlessRunRequest CreateRequest()
    {
        return new HeadlessRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                FixtureFiles.Read(
                    "v0.2",
                    "valid",
                    "json-only-tool-loop",
                    "agent-run.json")),
            Observations = new[]
            {
                ProtocolJson.DeserializeObservationEnvelope(
                    FixtureFiles.Read(
                        "v0.2",
                        "valid",
                        "json-only-tool-loop",
                        "observation.json"))
            },
            Tools = new[]
            {
                ProtocolJson.DeserializeToolDescriptor(
                    FixtureFiles.Read(
                        "v0.2",
                        "valid",
                        "json-only-tool-loop",
                        "tool-descriptor.json"))
            }
        };
    }

    private static HeadlessAgentRuntime Runtime(
        IModelProvider provider,
        InMemorySessionStore store,
        FakeRuntimeClock clock)
    {
        return new HeadlessAgentRuntime(
            provider,
            FakeGameHost.Returning(
                _ => throw new InvalidOperationException(
                    "No tool call expected.")),
            store,
            clock,
            new SequentialIdGenerator());
    }

    private static ActionReceipt SucceededReceipt(
        ActionRequest request,
        FakeRuntimeClock clock)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 1,
            Status = ReceiptStatuses.Succeeded,
            Result = ProtocolJson.ParseElement("""{"resource":"berries","gathered":1}"""),
            Retryable = false,
            CommittedAt = clock.UtcNow,
            ReceivedAt = clock.UtcNow
        };
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The expected runtime condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static void AssertFailedUsage(
        HeadlessRunOutcome outcome,
        IReadOnlyList<RuntimeEvent> events,
        FakeGameHost host,
        int inputTokens,
        int outputTokens,
        string costUsd)
    {
        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(inputTokens, outcome.Run.Usage.InputTokens);
        Assert.Equal(outputTokens, outcome.Run.Usage.OutputTokens);
        Assert.Equal(costUsd, outcome.Run.Usage.CostUsd);
        Assert.Equal(0, host.CallCount);
        var failed = Assert.Single(
            events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKinds.RunFailed);
        var persistedUsage = failed.Payload.GetProperty("usage");
        Assert.Equal(
            inputTokens,
            persistedUsage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(
            outputTokens,
            persistedUsage.GetProperty("outputTokens").GetInt32());
        Assert.Equal(
            costUsd,
            persistedUsage.GetProperty("costUsd").GetString());
    }

    private static int FindEventIndex(
        IReadOnlyList<RuntimeEvent> events,
        string kind)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index].Kind == kind)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class CountingSessionStore : ISessionStore
    {
        private int _appendCallCount;
        private int _readCallCount;

        public int AppendCallCount => Volatile.Read(ref _appendCallCount);

        public int ReadCallCount => Volatile.Read(ref _readCallCount);

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            _ = runtimeEvent;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _appendCallCount);
            return default;
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCallCount);
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(
                Array.Empty<RuntimeEvent>());
        }
    }

    private sealed class FailFirstReadSessionStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private int _remainingFailures = 1;

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Injected history lookup failure.");
            }

            return _inner.ReadRunAsync(runId, cancellationToken);
        }
    }

    private sealed class BlockingReadSessionStore : ISessionStore
    {
        private readonly List<RuntimeEvent> _events = new();
        private readonly TaskCompletionSource _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RuntimeEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                _events.Add(runtimeEvent);
            }

            return default;
        }

        public async ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            _blocked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return Array.Empty<RuntimeEvent>();
        }

        public async Task WaitUntilBlockedAsync()
        {
            await _blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class CancellationIgnoringActionRequestStore : ISessionStore
    {
        private readonly List<RuntimeEvent> _events = new();

        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RuntimeEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ActionRequested,
                    StringComparison.Ordinal))
            {
                Blocked.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            lock (_events)
            {
                _events.Add(runtimeEvent);
            }
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(
                Array.Empty<RuntimeEvent>());
        }
    }

    private sealed class BlockingSessionStore : ISessionStore
    {
        private readonly int _expectedBlockedAppends;
        private readonly List<RuntimeEvent> _events = new();
        private readonly TaskCompletionSource _allBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCount;

        public BlockingSessionStore(int expectedBlockedAppends)
        {
            _expectedBlockedAppends = expectedBlockedAppends;
        }

        public IReadOnlyList<RuntimeEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            var appendNumber = Interlocked.Increment(ref _appendCount);
            if (appendNumber <= _expectedBlockedAppends)
            {
                if (appendNumber == _expectedBlockedAppends)
                {
                    _allBlocked.TrySetResult();
                }

                await _release.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_events)
            {
                _events.Add(runtimeEvent);
            }
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                IReadOnlyList<RuntimeEvent> result = _events
                    .Where(runtimeEvent => string.Equals(
                        runtimeEvent.RunId,
                        runId,
                        StringComparison.Ordinal))
                    .ToArray();
                return new ValueTask<IReadOnlyList<RuntimeEvent>>(result);
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            await _allBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class NthAppendBlockingSessionStore : ISessionStore
    {
        private readonly int _blockedAppendNumber;
        private readonly List<RuntimeEvent> _events = new();
        private readonly TaskCompletionSource _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCount;

        public NthAppendBlockingSessionStore(int blockedAppendNumber)
        {
            _blockedAppendNumber = blockedAppendNumber;
        }

        public IReadOnlyList<RuntimeEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _appendCount)
                == _blockedAppendNumber)
            {
                _blocked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_events)
            {
                _events.Add(runtimeEvent);
            }
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                IReadOnlyList<RuntimeEvent> result = _events
                    .Where(runtimeEvent => string.Equals(
                        runtimeEvent.RunId,
                        runId,
                        StringComparison.Ordinal))
                    .ToArray();
                return new ValueTask<IReadOnlyList<RuntimeEvent>>(result);
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            await _blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class MutatingActionHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;

        public MutatingActionHost(FakeRuntimeClock clock)
        {
            _clock = clock;
        }

        public string? ReceivedOperationId { get; private set; }

        public string? ReceivedActionName { get; private set; }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedOperationId = request.OperationId;
            ReceivedActionName = request.ActionName;
            request.OperationId = "host-mutated-operation";
            request.RunId = "host-mutated-run";
            request.ActionName = "host_mutated_action";
            request.Arguments =
                ProtocolJson.ParseElement("""{"mutated":true}""");
            return new ValueTask<ActionReceipt>(new ActionReceipt
            {
                OperationId = ReceivedOperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement("""{"accepted":true}"""),
                Retryable = false,
                CommittedAt = _clock.UtcNow,
                ReceivedAt = _clock.UtcNow
            });
        }
    }

    private sealed class MutableResponseProvider : IModelProvider
    {
        private int _callCount;

        public ModelToolCall? FirstToolCall { get; private set; }

        public ModelResponse? FirstResponse { get; private set; }

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstToolCall = new ModelToolCall
                {
                    ToolCallId = "call-response-snapshot",
                    Name = "gather_food",
                    Arguments = ProtocolJson.ParseElement(
                        """{"resource":"berries"}""")
                };
                FirstResponse = ModelResponse.CallTools(
                    new ProviderUsage
                    {
                        InputTokens = 7,
                        CostUsd = "0.01"
                    },
                    FirstToolCall);
                return new ValueTask<ModelResponse>(FirstResponse);
            }

            return new ValueTask<ModelResponse>(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}""")));
        }
    }

    private sealed class MutatingModelRequestProvider : IModelProvider
    {
        private int _callCount;

        public string? SecondTurnToolName { get; private set; }

        public string? SecondTurnFirstMessageRole { get; private set; }

        public int SecondTurnObservedHunger { get; private set; }

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            var tool = Assert.Single(request.Tools);
            if (call == 1)
            {
                tool.Name = "provider_mutated_tool";
                tool.Effect = ToolEffects.PureRead;
                tool.TimeoutMs = 1;
                tool.ConflictScopes.Clear();
                tool.ParametersSchema = ProtocolJson.ParseElement(
                    """
                    {
                      "type": "object",
                      "properties": {},
                      "additionalProperties": false
                    }
                    """);
                var firstMessage = Assert.Single(request.Messages);
                firstMessage.Role = "system";
                firstMessage.Content =
                    ProtocolJson.ParseElement("""{"mutated":true}""");
                return new ValueTask<ModelResponse>(
                    ModelResponse.CallTools(new ModelToolCall
                    {
                        ToolCallId = "call-provider-mutation",
                        Name = "gather_food",
                        Arguments = ProtocolJson.ParseElement(
                            """{"resource":"berries"}""")
                    }));
            }

            SecondTurnToolName = tool.Name;
            SecondTurnFirstMessageRole = request.Messages[0].Role;
            SecondTurnObservedHunger = request.Messages[0]
                .Content
                .GetProperty("observations")[0]
                .GetProperty("payload")
                .GetProperty("hunger")
                .GetInt32();
            return new ValueTask<ModelResponse>(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}""")));
        }
    }

    private sealed class ConcurrentFinalModelProvider : IModelProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return new ValueTask<ModelResponse>(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"done":true}""")));
        }
    }

    private sealed class AdvancingModelProvider : IModelProvider
    {
        private readonly FakeRuntimeClock _clock;
        private readonly TimeSpan _elapsed;
        private readonly ModelResponse _response;

        public AdvancingModelProvider(
            FakeRuntimeClock clock,
            TimeSpan elapsed,
            ModelResponse response)
        {
            _clock = clock;
            _elapsed = elapsed;
            _response = response;
        }

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            _clock.Advance(_elapsed);
            return new ValueTask<ModelResponse>(_response);
        }
    }

    private sealed class CancellationIgnoringModelProvider : IModelProvider
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ModelResponse> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Entered.TrySetResult();
            return await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class ActionRequestAdvancingStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly FakeRuntimeClock _clock;
        private readonly TimeSpan _elapsed;

        public ActionRequestAdvancingStore(
            FakeRuntimeClock clock,
            TimeSpan elapsed)
        {
            _clock = clock;
            _elapsed = elapsed;
        }

        public IReadOnlyList<RuntimeEvent> Events => _inner.Events;

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            if (runtimeEvent.Kind == RuntimeEventKinds.ActionRequested)
            {
                _clock.Advance(_elapsed);
            }
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }
    }

    private sealed class ActionRequestClockArmingStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly ArmedNthReadThrowingClock _clock;
        private readonly int _throwOnRead;

        public ActionRequestClockArmingStore(
            ArmedNthReadThrowingClock clock,
            int throwOnRead)
        {
            _clock = clock;
            _throwOnRead = throwOnRead;
        }

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            if (runtimeEvent.Kind == RuntimeEventKinds.ActionRequested)
            {
                _clock.Arm(_throwOnRead);
            }
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }
    }

    private sealed class PerRunToolThenFinalProvider : IModelProvider
    {
        private readonly ConcurrentDictionary<string, int> _calls =
            new(StringComparer.Ordinal);

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = _calls.AddOrUpdate(
                request.RunId,
                1,
                (_, current) => checked(current + 1));
            return call == 1
                ? new ValueTask<ModelResponse>(
                    ModelResponse.CallTools(new ModelToolCall
                    {
                        ToolCallId = "call-" + request.RunId,
                        Name = "gather_food",
                        Arguments = ProtocolJson.ParseElement(
                            """{"resource":"berries"}""")
                    }))
                : new ValueTask<ModelResponse>(
                    ModelResponse.Final(
                        ProtocolJson.ParseElement("""{"done":true}""")));
        }
    }

    private sealed class ClockAdvancingHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;
        private readonly TimeSpan _elapsed;
        private int _callCount;

        public ClockAdvancingHost(
            FakeRuntimeClock clock,
            TimeSpan elapsed)
        {
            _clock = clock;
            _elapsed = elapsed;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            _clock.Advance(_elapsed);
            return new ValueTask<ActionReceipt>(
                SucceededReceipt(request, _clock));
        }
    }

    private sealed class DelayedSuccessfulHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;
        private readonly TimeSpan _delay;
        private int _callCount;

        public DelayedSuccessfulHost(
            FakeRuntimeClock clock,
            TimeSpan delay)
        {
            _clock = clock;
            _delay = delay;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            await Task.Delay(_delay).ConfigureAwait(false);
            return SucceededReceipt(request, _clock);
        }
    }

    private sealed class CapacityHoldingHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;
        private int _callCount;

        public CapacityHoldingHost(FakeRuntimeClock clock)
        {
            _clock = clock;
        }

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await Release.Task.ConfigureAwait(false);
                FirstCompleted.TrySetResult();
            }

            return SucceededReceipt(request, _clock);
        }
    }

    private sealed class BlockingCancellationCallbackHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;
        private int _callCount;

        public BlockingCancellationCallbackHost(FakeRuntimeClock clock)
        {
            _clock = clock;
        }

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ActionRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _ = cancellationToken.Register(
                    () =>
                    {
                        CallbackInvoked.TrySetResult();
                        CallbackRelease.Task.GetAwaiter().GetResult();
                    });
                await ActionRelease.Task.ConfigureAwait(false);
                FirstCompleted.TrySetResult();
            }

            return SucceededReceipt(request, _clock);
        }
    }

    private sealed class CancellationIgnoringHost : IGameHost
    {
        private readonly FakeRuntimeClock _clock;

        public CancellationIgnoringHost(FakeRuntimeClock clock)
        {
            _clock = clock;
        }

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActionRequest? LastRequest { get; private set; }

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return SucceededReceipt(request, _clock);
        }
    }

    private sealed class BlockingDeadlineCallbackProvider : IModelProvider
    {
        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    Release.Task.GetAwaiter().GetResult();
                });
            var completion = new TaskCompletionSource<ModelResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask<ModelResponse>(completion.Task);
        }
    }

    private sealed class SlowRollbackActionClock : IRuntimeClock
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = new(
            2026,
            7,
            28,
            9,
            0,
            0,
            TimeSpan.Zero);
        private bool _armed;
        private int _readsAfterArm;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    if (_armed
                        && ++_readsAfterArm == 2)
                    {
                        Thread.Sleep(50);
                        _now = _now.AddHours(-1);
                    }

                    return _now;
                }
            }
        }

        public void Arm()
        {
            lock (_sync)
            {
                _armed = true;
                _readsAfterArm = 0;
            }
        }
    }

    private sealed class ArmedNthReadThrowingClock : IRuntimeClock
    {
        private readonly object _sync = new();
        private int _read;
        private int _throwOnRead;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    if (_throwOnRead > 0
                        && ++_read == _throwOnRead)
                    {
                        throw new InvalidOperationException(
                            "The injected clock failed before host dispatch.");
                    }

                    return new DateTimeOffset(
                        2026,
                        7,
                        28,
                        9,
                        0,
                        0,
                        TimeSpan.Zero);
                }
            }
        }

        public void Arm(int throwOnRead)
        {
            lock (_sync)
            {
                _read = 0;
                _throwOnRead = throwOnRead;
            }
        }
    }

    private sealed class ActionClockArmingIds : IRuntimeIdGenerator
    {
        private readonly SlowRollbackActionClock _clock;
        private int _value;

        public ActionClockArmingIds(SlowRollbackActionClock clock)
        {
            _clock = clock;
        }

        public string NewId(string category)
        {
            if (string.Equals(
                    category,
                    "operation",
                    StringComparison.Ordinal))
            {
                _clock.Arm();
            }

            return category + "-" + Interlocked.Increment(ref _value);
        }
    }

    private sealed class MisreportedInfiniteReadOnlyList<T>
        : IReadOnlyList<T>
    {
        private readonly T _item;

        public MisreportedInfiniteReadOnlyList(T item)
        {
            _item = item;
        }

        public int Count =>
            throw new InvalidOperationException("Count must not be read.");

        public T this[int index] => _item;

        public IEnumerator<T> GetEnumerator()
        {
            while (true)
            {
                yield return _item;
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
