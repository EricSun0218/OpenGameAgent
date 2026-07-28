using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.Testing;
using UnityEngine;

namespace GameAgent.Unity.Tests;

public sealed class UnityHostConformanceTests
{
    [Fact]
    public void DispatcherExecutesBackgroundRequestsOnItsCreatingThread()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 8);
        var mainThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;

        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ =>
                {
                    callbackThread = Environment.CurrentManagedThreadId;
                    return new ValueTask<int>(42);
                },
                CancellationToken.None));

        PumpUntilCompleted(dispatcher, pending);

        Assert.Equal(42, pending.GetAwaiter().GetResult());
        Assert.Equal(mainThread, callbackThread);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void DispatcherRejectsOverflowAndCancelsQueuedWorkOnShutdown()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var first = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(1),
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        var overflow = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(2),
                CancellationToken.None));
        Assert.Throws<UnityDispatcherQueueFullException>(
            () => overflow.GetAwaiter().GetResult());

        dispatcher.Shutdown();
        Assert.ThrowsAny<OperationCanceledException>(
            () => first.GetAwaiter().GetResult());
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void DispatcherHonorsCallerCancellationBeforePump()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ =>
                {
                    invoked = true;
                    return new ValueTask<int>(1);
                },
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => pending.GetAwaiter().GetResult());

        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        Assert.False(invoked);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task DispatcherShutdownBetweenDequeueAndClaimCancelsWork()
    {
        UnityMainThreadDispatcher? dispatcher = null;
        dispatcher = new UnityMainThreadDispatcher(
            capacity: 1,
            beforeWorkClaim: () => dispatcher!.Shutdown());
        using (dispatcher)
        {
            var invoked = false;
            var pending = Task.Run(
                async () => await dispatcher.InvokeAsync(
                    _ =>
                    {
                        invoked = true;
                        return new ValueTask<int>(1);
                    },
                    CancellationToken.None));
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher.PendingCount == 1,
                    TimeSpan.FromSeconds(2)));

            Assert.Equal(
                1,
                dispatcher.Pump(maxItems: 1, maxMilliseconds: 10));
            await Assert.ThrowsAsync<
                UnityDispatchCancelledBeforeExecutionException>(
                () => pending);

            Assert.False(invoked);
            Assert.True(dispatcher.IsShutdown);
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.Equal(0, dispatcher.RunningCount);
            Assert.True(
                dispatcher.WaitForRunningWorkAsync(CancellationToken.None)
                    .IsCompleted);
        }
    }

    [Fact]
    public async Task DispatcherShutdownBeforeDirectClaimRejectsWork()
    {
        UnityMainThreadDispatcher? dispatcher = null;
        dispatcher = new UnityMainThreadDispatcher(
            capacity: 1,
            beforeWorkClaim: () => dispatcher!.Shutdown());
        using (dispatcher)
        {
            var invoked = false;

            await Assert.ThrowsAsync<
                UnityDispatchCancelledBeforeExecutionException>(
                () => dispatcher.InvokeAsync(
                        _ =>
                        {
                            invoked = true;
                            return new ValueTask<int>(1);
                        },
                        CancellationToken.None)
                    .AsTask());

            Assert.False(invoked);
            Assert.True(dispatcher.IsShutdown);
            Assert.Equal(0, dispatcher.RunningCount);
        }
    }

    [Fact]
    public async Task DispatcherWaitsForRunningActionAfterCancellation()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                async token =>
                {
                    started.TrySetResult(token);
                    return await release.Task;
                },
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var actionToken = await started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.True(actionToken.IsCancellationRequested);
        Assert.False(pending.IsCompleted);

        release.TrySetResult(9);
        Assert.Equal(9, await pending);
    }

    [Fact]
    public async Task DispatcherReportsThrowingCancellationCallbacksAndShutsDown()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.UnhandledException +=
            exception => observed.TrySetResult(exception);

        var pending = dispatcher.InvokeAsync(
            async cancellationToken =>
                {
                    var delay = Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    using var registration = cancellationToken.Register(
                        () => throw new InvalidOperationException(
                            "Cancellation observer failed."));
                    registered.TrySetResult();
                    await delay;
                    return 1;
                },
                CancellationToken.None)
            .AsTask();

        await registered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending);
        var cancellationFailure = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.IsType<AggregateException>(cancellationFailure);
        Assert.True(dispatcher.IsShutdown);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void KnownPreExecutionFailuresReturnDefinitiveReceipts()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var invoked = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                invoked = true;
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            });
        using var cancellation = new CancellationTokenSource();
        var cancelled = Task.Run(
            async () => await host.SubmitActionAsync(
                UnityActionRequest(),
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var cancelledReceipt = cancelled.GetAwaiter().GetResult();
        Assert.Equal(ReceiptStatuses.Failed, cancelledReceipt.Status);
        Assert.Equal(
            "unity_dispatch_cancelled",
            cancelledReceipt.ErrorCode);
        Assert.False(invoked);
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);

        var occupied = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(1),
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
        var overflow = Task.Run(
                async () => await host.SubmitActionAsync(
                UnityActionRequest(),
                CancellationToken.None))
            .GetAwaiter()
            .GetResult();
        Assert.Equal(ReceiptStatuses.Failed, overflow.Status);
        Assert.Equal("unity_dispatch_queue_full", overflow.ErrorCode);
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        Assert.Equal(1, occupied.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task StartedHandlerCancellationFaultRemainsUnknownToCore()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var mutated = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                mutated = true;
                throw new OperationCanceledException(
                    "handler stopped after mutation");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host
                .SubmitActionAsync(
                    UnityActionRequest(),
                    CancellationToken.None)
                .AsTask());
        Assert.True(mutated);
    }

    [Fact]
    public async Task ExpiredActionDoesNotEnterUnityHandler()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var clock = new FakeRuntimeClock();
        var invoked = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                invoked = true;
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            },
            clock);
        var request = UnityActionRequest();
        request.Deadline = clock.UtcNow.AddMilliseconds(-1);

        var receipt = await host.SubmitActionAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ReceiptStatuses.Failed, receipt.Status);
        Assert.Equal("unity_dispatch_deadline", receipt.ErrorCode);
        Assert.False(invoked);
    }

    [Fact]
    public void ActionExpiringWhileQueuedDoesNotEnterUnityHandler()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var clock = new FakeRuntimeClock();
        var invocationCount = 0;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            },
            clock);
        var request = UnityActionRequest();
        request.Deadline = clock.UtcNow.AddSeconds(1);

        var pending = Task.Run(
            async () => await host.SubmitActionAsync(
                request,
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        clock.Advance(TimeSpan.FromSeconds(1));
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var receipt = pending.GetAwaiter().GetResult();

        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(0, receipt.Revision);
        Assert.Equal(ReceiptStatuses.Failed, receipt.Status);
        Assert.Equal("unity_dispatch_deadline", receipt.ErrorCode);
        Assert.True(receipt.Retryable);
        Assert.Equal(clock.UtcNow, receipt.ReceivedAt);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public void DtoBridgePreservesStructuredJsonWithoutUnitySerialization()
    {
        var input = new UnityObservationData
        {
            observationId = "observation-1",
            worldId = "world-1",
            sessionId = "session-1",
            source = "game.metrics",
            kind = "snapshot",
            contentType = "application/json",
            schemaRef = "game://schemas/metrics/1",
            contentSchemaVersion = "1",
            payloadJson = """{"hunger":70,"nearby":["berries"]}""",
            extensionsJson = """{"trace":{"id":1}}""",
            observedAtUnixMilliseconds = 1_785_196_800_000,
            trust = "authoritative",
            visibilityScope = "agent",
            audienceIds = new[] { "agent-1" }
        };

        var protocol = UnityProtocolBridge.ToProtocol(input);
        var json = UnityProtocolBridge.ToJson(protocol);
        var roundTrip = UnityProtocolBridge.ObservationFromJson(json);

        Assert.Equal(70, roundTrip.Payload!.Value
            .GetProperty("hunger")
            .GetInt32());
        Assert.Equal("berries", roundTrip.Payload.Value
            .GetProperty("nearby")[0]
            .GetString());
        Assert.Equal("1", roundTrip.ContentSchemaVersion);
        Assert.Equal(
            1,
            roundTrip.Extensions["trace"].GetProperty("id").GetInt32());
        Assert.Equal("agent-1", Assert.Single(
            roundTrip.Visibility.AudienceIds));
    }

    [Fact]
    public void FieldDtosPreserveResourcesAuthorityAndExtensions()
    {
        var resource = new UnityObservationData
        {
            observationId = "resource-observation",
            worldId = "world-1",
            source = "game.resource",
            kind = "resource",
            resourceUri = "game://world/map",
            resourceMediaType = "application/json",
            resourceDigest = "sha256:abc",
            resourceSizeBytes = 42,
            observedAtUnixMilliseconds = 1_785_196_800_000
        };
        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "operation-1",
                status = ReceiptStatuses.Succeeded,
                resultJson = """{"ok":true}""",
                authoritativeObservations = new[] { resource },
                extensionsJson = """{"host":"unity"}""",
                receivedAtUnixMilliseconds = 1_785_196_800_000
            });
        var request = new ActionRequest
        {
            OperationId = "operation-1",
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "inspect",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            RequestedAt = DateTimeOffset.UnixEpoch,
            Extensions = new Dictionary<string, JsonElement>
            {
                ["trace"] = ProtocolJson.ParseElement("""{"id":2}""")
            }
        };
        var unityRequest = UnityProtocolBridge.ToUnity(request);

        Assert.Equal(
            42,
            Assert.Single(receipt.AuthoritativeObservations)
                .ResourceRef!
                .SizeBytes);
        Assert.Equal(
            "unity",
            receipt.Extensions["host"].GetString());
        Assert.Contains("\"trace\"", unityRequest.extensionsJson);
        Assert.Equal(
            ProtocolConstants.ProtocolVersion,
            unityRequest.protocolVersion);
        Assert.True(unityRequest.hasRequestedAtUnixMilliseconds);
        Assert.Equal(0, unityRequest.requestedAtUnixMilliseconds);
        Assert.False(unityRequest.hasDeadlineUnixMilliseconds);
    }

    [Fact]
    public void FieldDtosPreserveEpochAndZeroTtl()
    {
        var observation = UnityProtocolBridge.ToProtocol(
            new UnityObservationData
            {
                observationId = "epoch-observation",
                worldId = "world-1",
                source = "game.clock",
                kind = "snapshot",
                payloadJson = "{}",
                hasObservedAtUnixMilliseconds = true,
                observedAtUnixMilliseconds = 0,
                hasTtlMilliseconds = true,
                ttlMilliseconds = 0
            });
        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "epoch-operation",
                status = ReceiptStatuses.Succeeded,
                hasCommittedAtUnixMilliseconds = true,
                committedAtUnixMilliseconds = 0,
                hasReceivedAtUnixMilliseconds = true,
                receivedAtUnixMilliseconds = 0
            });
        var request = UnityProtocolBridge.ToUnity(
            new ActionRequest
            {
                OperationId = "epoch-operation",
                RunId = "run-1",
                TurnId = "turn-1",
                ToolCallId = "call-1",
                AgentId = "agent-1",
                WorldId = "world-1",
                ActionName = "inspect",
                ActionVersion = "1",
                Arguments = ProtocolJson.ParseElement("{}"),
                RequestedAt = DateTimeOffset.UnixEpoch,
                Deadline = DateTimeOffset.UnixEpoch
            });

        Assert.Equal(DateTimeOffset.UnixEpoch, observation.ObservedAt);
        Assert.Equal(0, observation.TtlMs);
        Assert.Equal(DateTimeOffset.UnixEpoch, receipt.CommittedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, receipt.ReceivedAt);
        Assert.True(request.hasRequestedAtUnixMilliseconds);
        Assert.True(request.hasDeadlineUnixMilliseconds);
        Assert.Equal(0, request.requestedAtUnixMilliseconds);
        Assert.Equal(0, request.deadlineUnixMilliseconds);
    }

    [Fact]
    public void StructuredToolLoopMarshalsGameActionToMainThread()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 16);
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-1",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement(
                    """{"decision":"eat","resource":"berries"}""")));
        var mainThread = Environment.CurrentManagedThreadId;
        var handlerThread = 0;
        var facade = new UnityAgentRuntimeFacade(
            provider,
            store,
            dispatcher,
            (request, _) =>
            {
                handlerThread = Environment.CurrentManagedThreadId;
                return new ValueTask<ActionReceipt>(new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"gathered":1}"""),
                    ReceivedAt = clock.UtcNow,
                    CommittedAt = clock.UtcNow
                });
            },
            clock,
            new SequentialIdGenerator(),
            ownsSessionStore: false);

        try
        {
            var completed = Task.Run(
                async () => await facade.RunAsync(
                    CreateRunRequest(clock),
                    CancellationToken.None));
            PumpUntilCompleted(dispatcher, completed);
            var outcome = completed.GetAwaiter().GetResult();

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(mainThread, handlerThread);
            Assert.Equal(
                "eat",
                outcome.FinalOutput!.Value
                    .GetProperty("decision")
                    .GetString());
            Assert.Contains(
                store.Events,
                item => item.Kind == RuntimeEventKinds.ActionRequested);
            Assert.Contains(
                store.Events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
        }
        finally
        {
            facade.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task DurableToolLoopRunsEndToEndThroughUnityHost()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new FileSessionStore(
            Path.Combine(directory, "runtime.journal"));
        var clock = new FakeRuntimeClock();
        var ids = new SequentialIdGenerator();
        var tools = new ToolCatalogRegistry();
        tools.Replace(new[]
        {
            new ToolDescriptor
            {
                Name = "gather_food",
                Version = "1",
                Description = "Gather a visible food resource.",
                ParametersSchema = ProtocolJson.ParseElement(
                    """
                    {
                      "type":"object",
                      "required":["resource"],
                      "properties":{"resource":{"type":"string"}}
                    }
                    """),
                Effect = ToolEffects.WorldCommand,
                ThreadAffinity = ThreadAffinities.EngineMainThread,
                ConflictScopes = new List<string> { "inventory:player" },
                TimeoutMs = 1000,
                RetryPolicy = "idempotent",
                IdempotencyPolicy = "required"
            }
        });
        var host = new GameObject("GameAgentRuntimeDurableLoopTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var mainThread = Environment.CurrentManagedThreadId;
        var actionThread = 0;
        var journal = new JournalCoordinator(store, store, clock, ids);
        var runtime = new DurableAgentRuntime(
            new ProviderAttemptRunner(
                new IStreamingModelProvider[]
                {
                    new DurableLoopProvider()
                },
                new ProviderRetryPolicy
                {
                    MaxAttemptsPerProvider = 1,
                    IdleTimeout = TimeSpan.FromSeconds(2),
                    TotalTimeout = TimeSpan.FromSeconds(5)
                },
                new SystemRuntimeDelay(),
                ids),
            new UnityMainThreadGameHost(
                host.Dispatcher,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionThread = Environment.CurrentManagedThreadId;
                    return new ValueTask<ActionReceipt>(new ActionReceipt
                    {
                        OperationId = request.OperationId,
                        Revision = 0,
                        Status = ReceiptStatuses.Succeeded,
                        Result = ProtocolJson.ParseElement(
                            """{"gathered":1}"""),
                        ReceivedAt = clock.UtcNow,
                        CommittedAt = clock.UtcNow
                    });
                },
                clock),
            journal,
            new RunRecovery(store, store, journal),
            tools,
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "unity-test",
                MaxConcurrentProviderCalls = 1
            });
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);

        try
        {
            var pending = Task.Run(
                async () => await host.RunAsync(
                    CreateDurableRunRequest(clock),
                    CancellationToken.None));
            PumpUntilCompleted(host.Dispatcher, pending);
            var outcome = await pending;

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(mainThread, actionThread);
            Assert.Equal(
                "eat",
                outcome.FinalOutput!.Value
                    .GetProperty("decision")
                    .GetString());
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DurablePlayerGateScenarioProducesAVerifiableMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-player-gate-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var markerPath = Path.Combine(
            directory,
            "durable-loop.pass.json");
        var root = new GameObject("GameAgentUnityPlayerGateTest");
        var host = root.AddComponent<UnityAgentRuntimeHost>();

        try
        {
            var pending = UnityDurableGateScenario.RunAsync(
                host,
                Path.Combine(directory, "runtime.journal"),
                CancellationToken.None);
            PumpUntilCompleted(host.Dispatcher, pending);
            var result = pending.GetAwaiter().GetResult();
            UnityDurableGateScenario.WritePassMarker(
                markerPath,
                "CompileHost",
                result);

            Assert.True(result.Passed);
            Assert.True(File.Exists(markerPath));
            var marker = ProtocolJson.ParseElement(
                File.ReadAllText(markerPath));
            Assert.Equal(
                UnityDurableGateScenario.MarkerSchema,
                marker.GetProperty("schema").GetString());
            Assert.Equal(
                "passed",
                marker.GetProperty("status").GetString());
            Assert.Equal(
                "CompileHost",
                marker.GetProperty("backend").GetString());
            Assert.True(
                marker.GetProperty("mainThreadReceipt").GetBoolean());
            Assert.True(
                marker.GetProperty("actionRequested").GetBoolean());
            Assert.True(
                marker.GetProperty("actionReceived").GetBoolean());
        }
        finally
        {
            host.ShutdownAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FacadeShutdownCancelsTrackedRunBeforeReturning()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 8);
        var clock = new FakeRuntimeClock();
        var facade = new UnityAgentRuntimeFacade(
            new CancellationOnlyProvider(),
            new InMemorySessionStore(),
            dispatcher,
            (_, _) => throw new InvalidOperationException(
                "No action should be dispatched."),
            clock,
            new SequentialIdGenerator(),
            ownsSessionStore: false);

        var run = facade.RunAsync(
            CreateRunRequest(clock),
            CancellationToken.None);
        Assert.Equal(1, facade.ActiveRunCount);

        await facade.ShutdownAsync(CancellationToken.None);
        var outcome = await run;

        Assert.True(facade.IsShutdownRequested);
        Assert.Equal(0, facade.ActiveRunCount);
        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = facade.RunAsync(
                CreateRunRequest(clock),
                CancellationToken.None);
        });
    }

    [Fact]
    public async Task FacadeFlushesAfterThrowingCancellationCallback()
    {
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var backend = new ThrowingCancellationBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: false);

        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run);

        Assert.True(store.FlushStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(0, facade.ActiveRunCount);
        Assert.True(facade.IsShutdownRequested);
    }

    [Fact]
    public async Task FacadeShutdownDoesNotRunCancellationCallbacksInline()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var backend = new BlockingCancellationCallbackBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);

        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        var shutdown = facade
            .ShutdownAsync(CancellationToken.None)
            .AsTask();
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(250),
            "ShutdownAsync synchronously ran a blocking cancellation callback.");
        Assert.False(shutdown.IsCompleted);

        release.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task FacadeAggregatesAllLifecycleFailures()
    {
        var backend = new FailingLifecycleBackend();
        var store = new FailingLifecycleStore();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: true,
            ownsBackend: true);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());

        Assert.Equal(3, failure.InnerExceptions.Count);
        Assert.True(store.FlushAttempted);
        Assert.True(backend.DisposeAttempted);
        Assert.True(store.DisposeAttempted);
    }

    [Fact]
    public async Task FacadeRoutesDurableRunAndResumeThroughInjectedBackend()
    {
        var backend = new RecordingDurableBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var request = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "durable-run",
                State = RunStates.Queued
            }
        };

        var started = await facade.RunAsync(
            request,
            CancellationToken.None);
        var resumed = await facade.ResumeAsync(
            "resumed-run",
            cancellationToken: CancellationToken.None);

        Assert.Same(backend.Controls, facade.DurableControls);
        Assert.Same(request, backend.LastRequest);
        Assert.Equal("durable-run", started.Run.RunId);
        Assert.Equal("resumed-run", backend.LastResumeRunId);
        Assert.Equal("resumed-run", resumed.Run.RunId);
        Assert.Equal(0, facade.ActiveRunCount);

        await facade.DisposeAsync();
    }

    [Fact]
    public async Task HostExposesDurableBackendControlsAndCompletion()
    {
        var backend = new RecordingDurableBackend();
        var host = new GameObject("GameAgentRuntimeDurableTest")
            .AddComponent<UnityAgentRuntimeHost>();
        DurableRunOutcome? observed = null;
        host.DurableRunCompleted += outcome => observed = outcome;
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var request = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "host-durable-run",
                State = RunStates.Queued
            }
        };

        var outcome = await host.RunAsync(
            request,
            CancellationToken.None);
        host.Dispatcher.Pump(maxItems: 8, maxMilliseconds: 10);

        Assert.Same(backend.Controls, host.DurableControls);
        Assert.Same(outcome, observed);

        await host.ShutdownAsync(CancellationToken.None);
        Assert.True(host.Dispatcher.IsShutdown);
    }

    [Fact]
    public async Task HostPublishesRuntimeEventSnapshotsOnMainThread()
    {
        var host = new GameObject("GameAgentRuntimeEventTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var mainThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;
        RuntimeEvent? observed = null;
        host.RuntimeEventPublished += runtimeEvent =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            observed = runtimeEvent;
        };
        var source = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 1,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement("""{"state":"preparing"}""")
        };

        host.EventPublisher.Publish(source);
        source.Kind = "mutated";
        typeof(UnityAgentRuntimeHost)
            .GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host, null);

        Assert.NotNull(observed);
        Assert.Equal(RuntimeEventKinds.RunStarted, observed!.Kind);
        Assert.Equal(mainThread, callbackThread);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RuntimeEventBurstCannotStarveActionDispatcher()
    {
        var host = new GameObject("GameAgentRuntimeEventIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var runtimeEvent = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 1,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Ephemeral,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement("""{"state":"preparing"}""")
        };

        for (var index = 0; index < 2_048; index++)
        {
            runtimeEvent.EventId = "event-" + index;
            host.EventPublisher.Publish(runtimeEvent);
        }

        Assert.Equal(0, host.Dispatcher.PendingCount);
        Assert.True(host.DroppedRuntimeEventCount > 0);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostDoesNotCreateEventPublisherAfterShutdown()
    {
        var host = new GameObject("GameAgentRuntimeNoResurrectionTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var dispatcher = host.Dispatcher;

        await host.ShutdownAsync(CancellationToken.None);

        Assert.Same(dispatcher, host.Dispatcher);
        Assert.True(dispatcher.IsShutdown);
        Assert.Throws<ObjectDisposedException>(
            () => _ = host.EventPublisher);
        Assert.Null(
            typeof(UnityAgentRuntimeHost)
                .GetField(
                    "_runtimeEventDispatcher",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host));
        Assert.Null(
            typeof(UnityAgentRuntimeHost)
                .GetField(
                    "_eventPublisher",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host));
    }

    [Fact]
    public async Task FacadeRejectsRunsBeyondConfiguredCapacity()
    {
        var backend = new BlockingDurableBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            maxActiveRuns: 1);
        var firstRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "first",
                State = RunStates.Queued
            }
        };
        var secondRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "second",
                State = RunStates.Queued
            }
        };

        var first = facade.RunAsync(firstRequest, CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rejected = Assert.IsType<UnityRunCapacityExceededException>(
            Record.Exception(
                () =>
                {
                    _ = facade.RunAsync(
                        secondRequest,
                        CancellationToken.None);
                }));
        Assert.Equal(1, rejected.Capacity);

        backend.Release();
        await first;
        await facade.DisposeAsync();
    }

    [Fact]
    public async Task HostOwnsCompleteBuilderComposition()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-built-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            var host = new GameObject("GameAgentRuntimeBuiltTest")
                .AddComponent<UnityAgentRuntimeHost>();
            var built = new GameAgentRuntimeBuilder(
                    new UnityMainThreadGameHost(
                        host.Dispatcher,
                        (_, _) => throw new InvalidOperationException(
                            "No action should be dispatched.")))
                .UseFileJournal(path)
                .AddProvider(new DurableLoopProvider())
                .Build();

            host.Configure(built);
            Assert.Same(
                built.Runtime.Controls,
                host.DurableControls);

            await host.ShutdownAsync(CancellationToken.None);

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HostDisposesOwnedDurableRuntimeAndStore()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimeOwnershipTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);

        await host.ShutdownAsync(CancellationToken.None);

        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task HostShutdownWaitsForStartedDispatcherWorkBeforeDisposal()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimeDrainTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(
            async () => await host.Dispatcher.InvokeAsync(
                async cancellationToken =>
                {
                    started.TrySetResult(cancellationToken);
                    return await release.Task;
                },
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => host.Dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
        host.Dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var handlerToken = await started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var shutdown = host.ShutdownAsync(
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => host.Dispatcher.IsShutdown,
                TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => handlerToken.IsCancellationRequested,
                TimeSpan.FromSeconds(2)));
        Assert.False(shutdown.IsCompleted);
        Assert.False(runtime.IsDisposed);
        Assert.False(store.IsDisposed);
        Assert.Equal(1, host.Dispatcher.RunningCount);

        release.TrySetResult(7);
        Assert.Equal(7, await running);
        await shutdown;

        Assert.Equal(0, host.Dispatcher.RunningCount);
        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task HostCallerCancellationDoesNotPoisonSharedShutdown()
    {
        var store = new BlockingDurableStore();
        var host = new GameObject("GameAgentRuntimeTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            new CancellationOnlyProvider(),
            store,
            (_, _) => throw new InvalidOperationException(
                "No action should be dispatched."),
            new FakeRuntimeClock(),
            new SequentialIdGenerator(),
            ownsSessionStore: true);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.ShutdownAsync(canceled.Token));
        await store.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(store.FlushToken.IsCancellationRequested);
        Assert.False(store.IsDisposed);

        store.ReleaseFlush();
        await host.ShutdownAsync(CancellationToken.None);

        Assert.True(store.IsDisposed);
        Assert.True(host.Dispatcher.IsShutdown);
    }

    private static HeadlessRunRequest CreateRunRequest(
        FakeRuntimeClock clock)
    {
        return new HeadlessRunRequest
        {
            Run = new AgentRun
            {
                RunId = "run-1",
                AgentId = "agent-1",
                WorldId = "world-1",
                SessionId = "session-1",
                State = RunStates.Queued,
                RuntimeGeneration = 1,
                Budget = new AgentBudget
                {
                    MaxTurns = 4,
                    MaxActions = 2,
                    MaxDurationMs = 5000,
                    MaxTokens = 2000,
                    MaxCostUsd = "0.10"
                },
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            },
            Observations = new[]
            {
                new ObservationEnvelope
                {
                    ObservationId = "observation-1",
                    WorldId = "world-1",
                    SessionId = "session-1",
                    Source = "game.world",
                    Kind = "snapshot",
                    ContentType = "application/json",
                    ContentSchemaVersion = "1",
                    Payload = ProtocolJson.ParseElement(
                        """{"hunger":70}"""),
                    ObservedAt = clock.UtcNow,
                    Trust = "authoritative",
                    Visibility = new VisibilityRule
                    {
                        Scope = "agent",
                        AudienceIds = new List<string> { "agent-1" }
                    }
                }
            },
            Tools = new[]
            {
                new ToolDescriptor
                {
                    Name = "gather_food",
                    Version = "1",
                    Description = "Gather a visible food resource.",
                    ParametersSchema = ProtocolJson.ParseElement(
                        """
                        {
                          "type":"object",
                          "required":["resource"],
                          "properties":{"resource":{"type":"string"}}
                        }
                        """),
                    Effect = ToolEffects.WorldCommand,
                    ThreadAffinity = ThreadAffinities.EngineMainThread,
                    TimeoutMs = 1000,
                    RetryPolicy = "idempotent",
                    IdempotencyPolicy = "required"
                }
            }
        };
    }

    private static ActionRequest UnityActionRequest()
    {
        return new ActionRequest
        {
            OperationId = "operation-1",
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "test_action",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private static DurableRunRequest CreateDurableRunRequest(
        FakeRuntimeClock clock)
    {
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "durable-loop-run",
                AgentId = "agent-1",
                WorldId = "world-1",
                SessionId = "session-1",
                State = RunStates.Queued,
                RuntimeGeneration = 1,
                Budget = new AgentBudget
                {
                    MaxTurns = 4,
                    MaxActions = 2,
                    MaxDurationMs = 5000,
                    MaxTokens = 2000,
                    MaxCostUsd = "0.10"
                },
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            },
            Context = new[]
            {
                new ContextCandidate(
                    "world-state",
                    "world_state",
                    ProtocolJson.ParseElement(
                        """{"hunger":70,"visible":["berries"]}"""),
                    required: true,
                    canDefer: false)
            }
        };
    }

    private static void PumpUntilCompleted(
        UnityMainThreadDispatcher dispatcher,
        Task pending)
    {
        var timeout = Stopwatch.StartNew();
        while (!pending.IsCompleted)
        {
            dispatcher.Pump(maxItems: 64, maxMilliseconds: 10);
            Thread.Yield();
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException(
                    "The Unity host conformance task did not complete.");
            }
        }
    }

    private sealed class CancellationOnlyProvider : IModelProvider
    {
        public async ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class DurableLoopProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "unity-test";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 16_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "call-1",
                    ToolNameDelta = "gather_food",
                    ArgumentsJsonDelta = """{"resource":"berries"}"""
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 0,
                        OutputTokens = 0,
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
                yield break;
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = """{"decision":"eat","resource":"berries"}"""
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            };
            await Task.Yield();
        }
    }

    private sealed class ThrowingCancellationBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started => _started;

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException(
                    "Cancellation observer failed."));
            _started.TrySetResult(true);
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BlockingCancellationCallbackBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly ManualResetEventSlim _release;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCancellationCallbackBackend(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started => _started;

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                () => _release.Wait(TimeSpan.FromSeconds(5)));
            _started.TrySetResult(true);
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailingLifecycleBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>,
          IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("backend dispose failed"));
        }
    }

    private sealed class RecordingDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        public RuntimeControlPlane Controls { get; } = new();

        public DurableRunRequest? LastRequest { get; private set; }

        public string? LastResumeRunId { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastResumeRunId = runId;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = new AgentRun
                    {
                        RunId = runId,
                        State = RunStates.Completed
                    }
                });
        }
    }

    private sealed class BlockingDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeControlPlane Controls { get; } = new();

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new DurableRunOutcome { Run = request.Run };
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class OwnedDurableRuntime
        : IDurableAgentRuntime, IDisposable
    {
        private int _disposed;

        public RuntimeControlPlane Controls { get; } = new();

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class BlockingDurableStore : IDurableSessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly TaskCompletionSource<bool> _flushStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFlush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isDisposed;

        public TaskCompletionSource<bool> FlushStarted => _flushStarted;

        public CancellationToken FlushToken { get; private set; }

        public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

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
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            return new JournalAppendResult(
                sequence: 0,
                revision: expectedRunRevision.GetValueOrDefault() + 1,
                wasDuplicate: false);
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = new List<JournalAppendResult>(
                runtimeEvents.Count);
            var revision = expectedRunRevision.GetValueOrDefault();
            foreach (var runtimeEvent in runtimeEvents)
            {
                await _inner.AppendAsync(
                    runtimeEvent,
                    cancellationToken);
                revision++;
                results.Add(
                    new JournalAppendResult(
                        results.Count,
                        revision,
                        false));
            }

            return results;
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public async ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            FlushToken = cancellationToken;
            _flushStarted.TrySetResult(true);
            await _releaseFlush.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _isDisposed, 1);
            return default;
        }

        public void ReleaseFlush()
        {
            _releaseFlush.TrySetResult(true);
        }
    }

    private sealed class FailingLifecycleStore : IDurableSessionStore
    {
        public bool FlushAttempted { get; private set; }

        public bool DisposeAttempted { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            _ = runtimeEvent;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            _ = runtimeEvent;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    0,
                    expectedRunRevision.GetValueOrDefault() + 1,
                    false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = expectedRunRevision.GetValueOrDefault();
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            index,
                            ++revision,
                            false))
                    .ToArray());
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

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("store flush failed"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("store dispose failed"));
        }
    }
}
