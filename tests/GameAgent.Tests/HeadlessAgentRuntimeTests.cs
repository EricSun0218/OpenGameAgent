using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class HeadlessAgentRuntimeTests
{
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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.True(sawDurableRequest);

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

        var runTask = runtime.RunAsync(request).AsTask();
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
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2));

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

        var outcome = await runtime.RunAsync(request);

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

        var runTask = runtime.RunAsync(CreateRequest()).AsTask();
        await store.WaitUntilBlockedAsync();
        provider.FirstToolCall!.ToolCallId = "call-mutated-late";
        provider.FirstToolCall.Name = "provider_mutated_tool";
        provider.FirstToolCall.Arguments =
            ProtocolJson.ParseElement("""{"resource":"roots"}""");
        provider.FirstResponse!.Usage.InputTokens = 900;
        provider.FirstResponse.Usage.CostUsd = "0.09";

        store.Release();
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2));

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

        var outcome = await runtime.RunAsync(request);

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

        var firstTask = runtime.RunAsync(request).AsTask();
        await store.WaitUntilBlockedAsync();

        Assert.Equal(1, runtime.ActiveRunCount);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));
        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(request).AsTask());
        Assert.Equal(request.Run.RunId, duplicate.RunId);
        Assert.Equal(
            DuplicateRunException.StableReasonCode,
            duplicate.ReasonCode);
        Assert.Equal(originalRunJson, ProtocolJson.Serialize(originalRun));

        store.Release();
        var outcome = await firstTask.WaitAsync(TimeSpan.FromSeconds(2));

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

        var active = runtime.RunAsync(first).AsTask();
        await store.WaitUntilBlockedAsync();

        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(duplicateRequest).AsTask());
        var capacity = await Assert.ThrowsAsync<
            RunWorkloadCapacityExceededException>(
            () => runtime.RunAsync(overflow).AsTask());

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
        var outcome = await active.WaitAsync(TimeSpan.FromSeconds(2));
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
                            () => runtime.RunAsync(request).AsTask());
                    }));

        Assert.All(
            rejected,
            exception => Assert.Equal(
                RunWorkloadCapacityReasonCodes.MaxActiveRuns,
                exception.ReasonCode));
        Assert.Equal(maxActiveRuns, runtime.ActiveRunCount);

        store.Release();
        var outcomes = await Task.WhenAll(admitted)
            .WaitAsync(TimeSpan.FromSeconds(5));
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

        var firstTask = runtime.RunAsync(firstRequest).AsTask();
        var secondTask = runtime.RunAsync(secondRequest).AsTask();
        await store.WaitUntilBlockedAsync();

        store.Release();
        var outcomes = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(2));

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

        var firstOutcome = await runtime.RunAsync(firstRequest);
        var secondOutcome = await runtime.RunAsync(secondRequest);

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
            () => runtime.RunAsync(request).AsTask());

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

        var firstOutcome = await runtime.RunAsync(firstRequest);
        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => runtime.RunAsync(secondRequest).AsTask());

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
            () => runtime.RunAsync(firstRequest).AsTask());
        Assert.Equal(0, runtime.ActiveRunCount);
        var outcome = await runtime.RunAsync(secondRequest);

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

        Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
        Assert.Equal("max_duration", outcome.Run.TerminalReason);
        Assert.True(outcome.Run.Usage.DurationMs >= 20);
    }

    [Fact]
    public async Task HardDeadlineReturnsWhenProviderIgnoresCancellation()
    {
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new CancellationIgnoringModelProvider();
        var request = CreateRequest();
        request.Run.Budget.MaxDurationMs = 25;
        var runtime = Runtime(provider, store, clock);

        var runTask = runtime.RunAsync(request).AsTask();
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
        }
        finally
        {
            provider.Release.TrySetResult(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"late":true}""")));
        }
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

        var runTask = runtime.RunAsync(request).AsTask();
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(2));

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

        var outcome = await runtime.RunAsync(CreateRequest());

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(RuntimeEventKinds.RunFailed, store.Events[^1].Kind);
        Assert.Equal("0", outcome.Run.Usage.CostUsd);
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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(CreateRequest());

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        var outcome = await runtime.RunAsync(request);

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

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return SucceededReceipt(request, _clock);
        }
    }
}
