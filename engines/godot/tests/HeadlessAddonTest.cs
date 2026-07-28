using System.Collections.Concurrent;
using System.Diagnostics;
using GameAgent.Core;
using GameAgent.Godot.Samples;
using GameAgent.Protocol;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot.Tests;

public partial class HeadlessAddonTest : global::Godot.Node
{
    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<GodotDictionary>> _completions =
        new(StringComparer.Ordinal);
    private readonly object _runtimeEventGate = new();
    private readonly List<string> _runtimeEventKinds = new();
    private int _runtimeEventCount;

    public override async void _Ready()
    {
        try
        {
            await ToSignal(GetTree(), global::Godot.SceneTree.SignalName.ProcessFrame);
            await RunAssertionsAsync();
            global::Godot.GD.Print(
                $"GODOT_TEST_PASS events={_runtimeEventCount}");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            global::Godot.GD.PushError(
                $"GODOT_TEST_FAIL {exception}");
            GetTree().Quit(1);
        }
    }

    private async Task RunAssertionsAsync()
    {
        var runtime = GetNode<GameAgentRuntimeNode>("/root/GameAgentRuntime");
        Assert(runtime.IsInsideTree(), "Autoload did not enter the SceneTree.");
        VerifyBoundSurface(runtime);

        await VerifyDispatcherBoundsAsync(runtime);
        await VerifyNodeWaitsForStartedActionHandlersAsync();
        await VerifyFacadeErrorAsync(runtime);
        VerifyEventPumpBounds();
        await VerifyDurableShutdownOrderAsync();
        await VerifyTypedContinuationSnapshotAsync();
        await VerifyBackendWaitsForRuntimeBeforeFlushAsync();
        await VerifyBackendWaitCancellationDoesNotPoisonShutdownAsync();
        await VerifyNodeWaitCancellationDoesNotPoisonShutdownAsync();
        await VerifyNodeSurfacesShutdownFailureAsync();
        await VerifyNodeContinuesShutdownAfterCancellationFailureAsync();
        await VerifyNodeDoesNotRunCancellationCallbacksInlineAsync();

        var fixture = SampleRuntimeFactory.Configure(runtime);
        VerifyGodotJsonNumberCompatibility(fixture);
        runtime.RuntimeEventPublished += OnRuntimeEvent;
        runtime.RunCompleted += OnRunCompleted;
        runtime.RunFailed += OnRunFailed;

        await VerifyDurableToolLoopAsync(runtime, fixture);
        await VerifyTypedResumeAsync(runtime, fixture);
        await VerifyCancelAsync(runtime, fixture);
        await VerifyInterruptAsync(runtime, fixture);
        await VerifySteerAsync(runtime, fixture);
        await VerifyFollowUpAsync(runtime, fixture);
        await VerifyLegacyHeadlessCompatibilityAsync();

        await runtime.Typed.StopAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert(
            fixture.Store.FlushCount == 1,
            "Graceful shutdown did not flush the durable store exactly once.");
        Assert(
            fixture.Store.IsDisposed,
            "Owned durable store was not disposed during shutdown.");
    }

    private static void VerifyBoundSurface(GameAgentRuntimeNode runtime)
    {
        var methods = new[]
        {
            "start_run",
            "start_agent_run",
            "resume_agent_run",
            "cancel_run",
            "interrupt_run",
            "steer_run",
            "follow_up_run"
        };
        foreach (var method in methods)
        {
            Assert(
                runtime.HasMethod(method),
                $"Variant-compatible method '{method}' was not bound.");
        }

        Assert(
            runtime.HasSignal(GameAgentRuntimeNode.SignalName.RunCompleted),
            "run_completed signal was not bound.");
    }

    private async Task VerifyDurableToolLoopAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        var requestId = runtime.start_agent_run(
            GodotProtocolVariantMapper.ToDictionary(fixture.Request.Run),
            GodotProtocolVariantMapper.ToArray(fixture.Observations));
        Assert(
            !string.IsNullOrWhiteSpace(requestId),
            "start_agent_run rejected valid durable input.");

        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        var run = outcome["run"].AsGodotDictionary();
        Assert(
            run["state"].AsString() == RunStates.Completed,
            $"Expected completed run, got '{run["state"].AsString()}'.");
        Assert(
            outcome["final_output"]
                    .AsGodotDictionary()["decision"]
                    .AsString()
                == "eat",
            "Final JSON output was not preserved as a Variant Dictionary.");
        Assert(
            !outcome["reconciliation_required"].AsBool(),
            "A completed tool loop unexpectedly requested reconciliation.");

        var requests = fixture.Provider.RequestsFor(fixture.Request.Run.RunId);
        Assert(requests.Count == 2, "The durable tool loop did not use two turns.");
        Assert(
            requests[1].Messages.Any(
                message => message.Role == NormalizedRoles.Tool
                           && message.Parts.Any(
                               part => part.Json?.GetRawText().Contains(
                                   ReceiptStatuses.Succeeded,
                                   StringComparison.Ordinal) == true)),
            "The action receipt was not returned to the model transcript.");
        Assert(
            OrderedRuntimeEvent(
                RuntimeEventKinds.ActionRequested,
                RuntimeEventKinds.ActionReceived),
            "ActionRequested was not published before ActionReceived.");
        Assert(
            fixture.MainThreadProbe.HandlerRan,
            "The game action handler did not run.");
        Assert(
            fixture.MainThreadProbe.HandlerRanOnMainThread,
            "The game action handler ran outside the Godot main thread.");
        Assert(
            fixture.MainThreadProbe.ProviderRanOffMainThread,
            "The durable provider did not execute off the Godot main thread.");
    }

    private async Task VerifyTypedResumeAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        var requestId = runtime.Typed.ResumeRun(fixture.Request.Run.RunId);
        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            outcome["run"].AsGodotDictionary()["state"].AsString()
                == RunStates.Completed,
            "Typed resume did not return the durable terminal outcome.");
        Assert(
            fixture.Provider.RequestsFor(fixture.Request.Run.RunId).Count == 2,
            "Resuming a terminal run called the provider again.");
    }

    private async Task VerifyCancelAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        const string runId = "control-cancel-1";
        var requestId = StartControlRun(runtime, runId);
        await fixture.Provider.WaitForAttemptAsync(
            runId,
            1,
            TimeSpan.FromSeconds(5));
        Assert(
            runtime.cancel_run(runId),
            "The GDScript cancel control was not delivered.");
        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            outcome["run"].AsGodotDictionary()["state"].AsString()
                == RunStates.Cancelled,
            "Cancel did not produce a cancelled durable run.");
    }

    private async Task VerifyInterruptAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        const string runId = "control-interrupt-1";
        var requestId = StartControlRun(runtime, runId);
        await fixture.Provider.WaitForAttemptAsync(
            runId,
            1,
            TimeSpan.FromSeconds(5));
        Assert(
            runtime.interrupt_run(runId),
            "The GDScript interrupt control was not delivered.");
        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            outcome["run"].AsGodotDictionary()["state"].AsString()
                == RunStates.Interrupted,
            "Interrupt did not produce an interrupted durable run.");
    }

    private async Task VerifySteerAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        const string runId = "control-steer-1";
        const string worldId = "control-world";
        var requestId = StartControlRun(runtime, runId, worldId);
        await fixture.Provider.WaitForAttemptAsync(
            runId,
            1,
            TimeSpan.FromSeconds(5));
        var observation = SampleRuntimeFactory.CreateObservation(
            "steer-observation",
            worldId,
            """{"priorityTarget":"gate-3"}""",
            DateTimeOffset.UtcNow);
        Assert(
            runtime.steer_run(
                runId,
                GodotProtocolVariantMapper.ToDictionary(observation)),
            "The GDScript steer control was not delivered.");

        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        var state = outcome["run"].AsGodotDictionary()["state"].AsString();
        Assert(
            state == RunStates.Completed,
            $"Steer did not restart and complete the provider turn; got '{state}'.");
        Assert(
            outcome["final_output"].AsString() == "steered-final",
            "Steer returned an unexpected final output.");
        var requests = fixture.Provider.RequestsFor(runId);
        Assert(requests.Count == 2, "Steer did not replace the stale turn.");
        Assert(
            requests[1].Messages.Any(
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "gate-3",
                        StringComparison.Ordinal) == true)),
            "Steer observation was not compiled into the replacement turn.");
    }

    private async Task VerifyFollowUpAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        const string runId = "control-follow-up-1";
        const string worldId = "control-world";
        var requestId = StartControlRun(runtime, runId, worldId);
        await fixture.Provider.WaitForAttemptAsync(
            runId,
            1,
            TimeSpan.FromSeconds(5));
        var observation = SampleRuntimeFactory.CreateObservation(
            "follow-up-observation",
            worldId,
            """{"questionCode":"next-step"}""",
            DateTimeOffset.UtcNow);
        Assert(
            runtime.follow_up_run(
                runId,
                GodotProtocolVariantMapper.ToDictionary(observation)),
            "The GDScript follow-up control was not delivered.");
        fixture.Provider.Release(runId, 1);

        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            outcome["run"].AsGodotDictionary()["state"].AsString()
                == RunStates.Completed,
            "Follow-up did not complete a second turn.");
        Assert(
            outcome["final_output"].AsString() == "follow-up-final",
            "Follow-up returned an unexpected final output.");
        var requests = fixture.Provider.RequestsFor(runId);
        Assert(requests.Count == 2, "Follow-up did not start a second turn.");
        Assert(
            requests[1].Messages.Any(
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "next-step",
                        StringComparison.Ordinal) == true)),
            "Follow-up observation was not compiled into the next turn.");
    }

    private string StartControlRun(
        GameAgentRuntimeNode runtime,
        string runId,
        string worldId = "control-world")
    {
        var requestId = runtime.start_agent_run(
            GodotProtocolVariantMapper.ToDictionary(
                SampleRuntimeFactory.CreateRun(
                    runId,
                    worldId,
                    DateTimeOffset.UtcNow)),
            new GodotArray());
        Assert(
            !string.IsNullOrWhiteSpace(requestId),
            $"Unable to start control test run '{runId}'.");
        return requestId;
    }

    private static async Task VerifyLegacyHeadlessCompatibilityAsync()
    {
        var tree = ((global::Godot.SceneTree)global::Godot.Engine
            .GetMainLoop());
        var legacy = new GameAgentRuntimeNode
        {
            Name = "LegacyHeadlessRuntime"
        };
        tree.Root.AddChild(legacy);
        await legacy.ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);

        var completion = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        legacy.RunCompleted += outcome =>
            completion.TrySetResult(outcome);
        legacy.RunFailed += error => completion.TrySetException(
            new InvalidOperationException(
                $"Legacy run failed: {error["code"].AsString()}"));
        legacy.Typed.ConfigureHeadless(
            new LegacyProvider(),
            new LegacyHost(),
            new LegacyStore(),
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator());
        var requestId = legacy.start_run(
            GodotProtocolVariantMapper.ToDictionary(
                SampleRuntimeFactory.CreateRun(
                    "legacy-headless-run",
                    "legacy-world",
                    DateTimeOffset.UtcNow)),
            new GodotArray(),
            new GodotArray());
        Assert(
            !string.IsNullOrWhiteSpace(requestId),
            "The legacy start_run facade rejected a valid request.");

        var outcome = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            outcome["run"].AsGodotDictionary()["state"].AsString()
                == RunStates.Completed,
            "The legacy Headless runtime compatibility path did not complete.");
        await legacy.Typed.StopAsync(TimeSpan.FromSeconds(2));
        legacy.QueueFree();
        await tree.Root.ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private static async Task VerifyDispatcherBoundsAsync(
        GameAgentRuntimeNode runtime)
    {
        var dispatcher = new GodotMainThreadDispatcher(1);
        var first = dispatcher.InvokeAsync(
            () => global::Godot.OS.GetThreadCallerId(),
            "dispatcher-first");
        var overflow = dispatcher.InvokeAsync(
            () => 2,
            "dispatcher-overflow");

        await AssertThrowsAsync<GodotDispatcherQueueFullException>(
            overflow.AsTask(),
            "A full dispatcher queue accepted an extra command.");
        Assert(dispatcher.PendingCount == 1, "Dispatcher pending count drifted.");

        dispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        var threadId = await first;
        Assert(
            threadId == global::Godot.OS.GetMainThreadId(),
            "Dispatcher callback did not run on the main thread.");

        var expired = dispatcher.InvokeAsync(
            () => 3,
            "dispatcher-expired",
            DateTimeOffset.UtcNow.AddMilliseconds(-1));
        dispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        await AssertThrowsAsync<TimeoutException>(
            expired.AsTask(),
            "An expired dispatcher command executed.");

        var boundaryClock = new MutableClock(
            new DateTimeOffset(
                2026,
                7,
                28,
                0,
                0,
                0,
                TimeSpan.Zero));
        var boundaryDispatcher =
            new GodotMainThreadDispatcher(1, boundaryClock);
        var boundary = boundaryDispatcher.InvokeAsync(
            static () => 4,
            "dispatcher-deadline-boundary",
            boundaryClock.UtcNow);
        boundaryDispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        await AssertThrowsAsync<TimeoutException>(
            boundary.AsTask(),
            "A command executed at its exact deadline.");
        boundaryDispatcher.StopAccepting();

        var shutdownDispatcher = new GodotMainThreadDispatcher(1);
        var shutdownCallbackCount = 0;
        var queuedAtShutdown = shutdownDispatcher.InvokeAsync(
            () =>
            {
                Interlocked.Increment(ref shutdownCallbackCount);
                return 5;
            },
            "dispatcher-queued-at-shutdown");
        await Task.Run(shutdownDispatcher.StopAccepting);
        shutdownDispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        await AssertThrowsAsync<
            GodotDispatchCancelledBeforeExecutionException>(
            queuedAtShutdown.AsTask(),
            "Shutdown allowed queued dispatcher work to execute.");
        Assert(
            Volatile.Read(ref shutdownCallbackCount) == 0,
            "A queued callback started after dispatcher shutdown.");

        var asyncDispatcher = new GodotMainThreadDispatcher(1);
        var asyncHost = new GodotMainThreadGameHost(
            asyncDispatcher,
            new SystemRuntimeClock());
        var releaseHandler = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = false;
        var handlerStartedOnMainThread = false;
        asyncHost.Register(
            "async_action",
            async (request, cancellationToken) =>
            {
                handlerStarted = true;
                handlerStartedOnMainThread =
                    global::Godot.OS.GetThreadCallerId()
                    == global::Godot.OS.GetMainThreadId();
                await releaseHandler.Task.WaitAsync(cancellationToken);
                return new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    ReceivedAt = DateTimeOffset.UtcNow
                };
            });
        var asyncReceipt = asyncHost
            .SubmitActionAsync(
                new ActionRequest
                {
                    OperationId = "async-operation",
                    RunId = "async-run",
                    TurnId = "async-turn",
                    ToolCallId = "async-call",
                    AgentId = "async-agent",
                    WorldId = "async-world",
                    ActionName = "async_action",
                    ActionVersion = "1",
                    Arguments = ProtocolJson.ParseElement("{}"),
                    RequestedAt = DateTimeOffset.UtcNow
                },
                CancellationToken.None)
            .AsTask();
        asyncDispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        Assert(handlerStarted, "An async action handler did not start.");
        Assert(
            handlerStartedOnMainThread,
            "An async action handler did not start on the Godot main thread.");
        Assert(
            !asyncReceipt.IsCompleted,
            "An async action handler was forced to complete in one frame.");
        Assert(
            asyncDispatcher.RunningCount == 1,
            "The dispatcher did not track a started async action handler.");
        var asyncDrain = asyncDispatcher
            .WaitForRunningWorkAsync(CancellationToken.None)
            .AsTask();
        Assert(
            !asyncDrain.IsCompleted,
            "The dispatcher reported a running async handler as drained.");
        releaseHandler.TrySetResult(true);
        Assert(
            (await asyncReceipt).Status == ReceiptStatuses.Succeeded,
            "An async action handler did not return its receipt.");
        await asyncDrain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            asyncDispatcher.RunningCount == 0,
            "The dispatcher retained a completed async action handler.");
        asyncDispatcher.StopAccepting();

        using var cancellation = new CancellationTokenSource();
        var running = dispatcher.InvokeAsync(
            () =>
            {
                cancellation.Cancel();
                return 7;
            },
            "dispatcher-running-cancellation",
            cancellationToken: cancellation.Token);
        dispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
        Assert(
            await running == 7,
            "Cancellation after execution began hid a completed mutation.");
        dispatcher.StopAccepting();

        Assert(
            runtime.Dispatcher.IsMainThread,
            "Autoload dispatcher did not capture the Godot main thread.");
    }

    private async Task VerifyNodeWaitsForStartedActionHandlersAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var node = new GameAgentRuntimeNode
        {
            Name = "StartedActionDrainRuntime"
        };
        var releaseHandler = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);

        try
        {
            node.Typed.ConfigureDurable(
                new LifecycleRuntime(order),
                new LifecycleStore(order, failFlush: false),
                disposeRuntimeOnShutdown: true,
                disposeStoreOnShutdown: true);
            var host = new GodotMainThreadGameHost(
                node.Dispatcher,
                new SystemRuntimeClock());
            var handlerStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            host.Register(
                "slow_action",
                async (request, cancellationToken) =>
                {
                    _ = cancellationToken;
                    handlerStarted.TrySetResult(true);
                    await releaseHandler.Task.ConfigureAwait(false);
                    return new ActionReceipt
                    {
                        OperationId = request.OperationId,
                        Revision = 1,
                        Status = ReceiptStatuses.Succeeded,
                        ReceivedAt = DateTimeOffset.UtcNow
                    };
                });
            var receipt = host.SubmitActionAsync(
                    new ActionRequest
                    {
                        OperationId = "slow-operation",
                        RunId = "slow-run",
                        TurnId = "slow-turn",
                        ToolCallId = "slow-call",
                        AgentId = "slow-agent",
                        WorldId = "slow-world",
                        ActionName = "slow_action",
                        ActionVersion = "1",
                        Arguments = ProtocolJson.ParseElement("{}"),
                        RequestedAt = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None)
                .AsTask();
            node.Dispatcher.Drain(1, TimeSpan.FromMilliseconds(10));
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await AssertThrowsAsync<TimeoutException>(
                node.Typed
                    .StopAsync(
                        TimeSpan.FromMilliseconds(100),
                        CancellationToken.None)
                    .AsTask(),
                "A shutdown waiter exceeded its timeout.");
            Assert(
                order.Count == 0,
                "Shutdown disposed runtime state before the action handler drained.");

            var stop = node.Typed
                .StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .AsTask();
            Assert(
                !stop.IsCompleted,
                "A timed-out waiter poisoned the shared shutdown task.");
            releaseHandler.TrySetResult(true);
            Assert(
                (await receipt.WaitAsync(TimeSpan.FromSeconds(2))).Status
                    == ReceiptStatuses.Succeeded,
                "The started action handler did not finish.");
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(
                order.SequenceEqual(
                    new[] { "runtime_dispose", "flush", "store_dispose" }),
                "Shutdown did not drain the action handler before durable cleanup.");
        }
        finally
        {
            releaseHandler.TrySetResult(true);
            node.QueueFree();
            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void VerifyEventPumpBounds()
    {
        var pump = new GodotEventPump(2);
        Assert(
            pump.TryPublish(new GodotEventMessage
            {
                Kind = GodotEventKinds.RuntimeStarted
            }),
            "Event pump rejected its first event.");
        Assert(
            pump.TryPublish(new GodotEventMessage
            {
                Kind = GodotEventKinds.RuntimeStarted
            }),
            "Event pump rejected an event below capacity.");
        Assert(
            !pump.TryPublish(new GodotEventMessage
            {
                Kind = GodotEventKinds.RuntimeStarted
            }),
            "Event pump exceeded its configured capacity.");

        var messages = new List<GodotEventMessage>();
        pump.Drain(3, TimeSpan.FromMilliseconds(10), messages.Add);
        Assert(messages.Count == 3, "Event pump did not report and drain overflow.");
        Assert(
            messages[0].Kind == GodotEventKinds.PumpOverflow
            && messages[0].Count == 1,
            "Event pump did not coalesce overflow diagnostics.");
        pump.StopAccepting();
    }

    private static async Task VerifyDurableShutdownOrderAsync()
    {
        var order = new List<string>();
        var store = new LifecycleStore(order, failFlush: true);
        var backend = new GodotDurableRuntimeBackend(
            new LifecycleRuntime(order),
            store,
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        await AssertThrowsAsync<InvalidOperationException>(
            backend.StopAsync(CancellationToken.None).AsTask(),
            "A durable flush failure was swallowed.");
        Assert(
            order.SequenceEqual(
                new[] { "runtime_dispose", "flush", "store_dispose" }),
            "Durable shutdown did not stop the runtime before flushing and disposing the store.");

        await AssertThrowsAsync<InvalidOperationException>(
            backend.StopAsync(CancellationToken.None).AsTask(),
            "Repeated durable stop did not preserve its first result.");
        Assert(
            order.Count == 3,
            "Repeated durable stop executed lifecycle cleanup more than once.");

        var aggregateOrder = new List<string>();
        var aggregateBackend = new GodotDurableRuntimeBackend(
            new LifecycleRuntime(aggregateOrder, failDispose: true),
            new LifecycleStore(
                aggregateOrder,
                failFlush: true,
                failDispose: true),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        var aggregate = await AssertThrowsAsync<AggregateException>(
            aggregateBackend.StopAsync(CancellationToken.None).AsTask(),
            "Multiple durable shutdown failures were not aggregated.");
        Assert(
            aggregate.Flatten().InnerExceptions.Count == 3,
            "Durable shutdown did not preserve every cleanup failure.");
        Assert(
            aggregateOrder.SequenceEqual(
                new[] { "runtime_dispose", "flush", "store_dispose" }),
            "A cleanup failure changed the durable shutdown order.");
    }

    private async Task VerifyTypedContinuationSnapshotAsync()
    {
        var tree = GetTree();
        var runtime = new ContinuationCaptureRuntime();
        var node = new GameAgentRuntimeNode
        {
            Name = "ContinuationSnapshotRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            runtime,
            new LifecycleStore(new List<string>(), failFlush: false));

        node.Typed.ResumeRun(
            "continuation-snapshot-run",
            new DurableRunContinuation
            {
                ActiveSkills = Array.Empty<SkillReference>(),
                ReplaceActiveSkills = true,
                LaneId = "continuation-snapshot-lane"
            });
        var captured = await runtime.ContinuationReceived
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert(captured is not null, "Godot dropped a typed continuation.");
        Assert(
            captured!.ReplaceActiveSkills,
            "Godot dropped the explicit active-skill replacement flag.");
        Assert(
            captured.ActiveSkills.Count == 0,
            "Godot changed an explicit empty active-skill replacement.");
        Assert(
            captured.LaneId == "continuation-snapshot-lane",
            "Godot changed the typed continuation lane.");

        await node.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private static async Task VerifyBackendWaitsForRuntimeBeforeFlushAsync()
    {
        var order = new List<string>();
        var runtime = new AwaitableLifecycleRuntime(order);
        var backend = new GodotDurableRuntimeBackend(
            runtime,
            new LifecycleStore(order, failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);

        var stop = backend.StopAsync(CancellationToken.None).AsTask();
        await runtime.StopStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            order.SequenceEqual(new[] { "runtime_stop_started" }),
            "The backend flushed the store before the runtime finished stopping.");
        Assert(
            !stop.IsCompleted,
            "The backend completed shutdown while the runtime was still active.");

        runtime.ReleaseActiveRun();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            order.SequenceEqual(
                new[]
                {
                    "runtime_stop_started",
                    "runtime_final_write",
                    "runtime_stop_completed",
                    "flush",
                    "store_dispose"
                }),
            "The backend did not await the runtime's final journal write before flushing the store.");
    }

    private static async Task
        VerifyBackendWaitCancellationDoesNotPoisonShutdownAsync()
    {
        var order = new List<string>();
        var runtime = new AwaitableLifecycleRuntime(order);
        var backend = new GodotDurableRuntimeBackend(
            runtime,
            new LifecycleStore(order, failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        using var waitCancellation = new CancellationTokenSource();

        var cancelledWait = backend
            .StopAsync(waitCancellation.Token)
            .AsTask();
        await runtime.StopStarted.WaitAsync(TimeSpan.FromSeconds(2));
        waitCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            cancelledWait,
            "Cancelling one backend waiter did not cancel only that wait.");

        var completedWait = backend
            .StopAsync(CancellationToken.None)
            .AsTask();
        Assert(
            !completedWait.IsCompleted,
            "A cancelled backend waiter poisoned the shared shutdown task.");
        runtime.ReleaseActiveRun();
        await completedWait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            order.SequenceEqual(
                new[]
                {
                    "runtime_stop_started",
                    "runtime_final_write",
                    "runtime_stop_completed",
                    "flush",
                    "store_dispose"
                }),
            "Backend waiter cancellation skipped durable cleanup.");
    }

    private async Task VerifyNodeWaitCancellationDoesNotPoisonShutdownAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var runtime = new AwaitableLifecycleRuntime(order);
        var node = new GameAgentRuntimeNode
        {
            Name = "CancelledShutdownWaitRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            runtime,
            new LifecycleStore(order, failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        using var waitCancellation = new CancellationTokenSource();

        var cancelledWait = node.Typed
            .StopAsync(
                TimeSpan.FromSeconds(5),
                waitCancellation.Token)
            .AsTask();
        await runtime.StopStarted.WaitAsync(TimeSpan.FromSeconds(2));
        waitCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            cancelledWait,
            "Cancelling one node waiter did not cancel only that wait.");

        var completedWait = node.Typed
            .StopAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None)
            .AsTask();
        Assert(
            !completedWait.IsCompleted,
            "A cancelled node waiter poisoned the shared shutdown task.");
        runtime.ReleaseActiveRun();
        await completedWait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            order.SequenceEqual(
                new[]
                {
                    "runtime_stop_started",
                    "runtime_final_write",
                    "runtime_stop_completed",
                    "flush",
                    "store_dispose"
                }),
            "Node waiter cancellation skipped durable cleanup.");

        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyNodeSurfacesShutdownFailureAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var node = new GameAgentRuntimeNode
        {
            Name = "FailingShutdownRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            new LifecycleRuntime(order),
            new LifecycleStore(order, failFlush: true),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);

        await AssertThrowsAsync<InvalidOperationException>(
            node.Typed
                .StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .AsTask(),
            "The Godot node hid a durable shutdown failure.");
        Assert(
            order.SequenceEqual(
                new[] { "runtime_dispose", "flush", "store_dispose" }),
            "The Godot host did not stop its runtime before flushing and disposing the store.");

        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyNodeContinuesShutdownAfterCancellationFailureAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var runtime = new ThrowingCancellationRuntime(order);
        var node = new GameAgentRuntimeNode
        {
            Name = "ThrowingCancellationRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            runtime,
            new LifecycleStore(order, failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);

        node.Typed.StartRun(
            new DurableRunRequest
            {
                Run = SampleRuntimeFactory.CreateRun(
                    "throwing-cancellation-run",
                    "shutdown-test-world",
                    DateTimeOffset.UtcNow)
            });
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(2));

        await AssertThrowsAsync<AggregateException>(
            node.Typed
                .StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .AsTask(),
            "A throwing cancellation callback was not surfaced.");
        Assert(
            order.SequenceEqual(
                new[] { "runtime_dispose", "flush", "store_dispose" }),
            "A throwing cancellation callback skipped durable cleanup.");
        Assert(
            !node.EventPump.TryPublish(
                new GodotEventMessage
                {
                    Kind = GodotEventKinds.RuntimeStarted
                }),
            "A throwing cancellation callback left the event queue open.");
        await AssertThrowsAsync<GodotDispatchCancelledBeforeExecutionException>(
            node.Dispatcher
                .InvokeAsync(
                    static () => 1,
                    "post-shutdown-command")
                .AsTask(),
            "A throwing cancellation callback left the dispatcher open.");

        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyNodeDoesNotRunCancellationCallbacksInlineAsync()
    {
        var tree = GetTree();
        using var release = new ManualResetEventSlim(initialState: false);
        var runtime = new BlockingCancellationRuntime(release);
        var node = new GameAgentRuntimeNode
        {
            Name = "BlockingCancellationRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            runtime,
            new LifecycleStore(new List<string>(), failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);

        node.Typed.StartRun(
            new DurableRunRequest
            {
                Run = SampleRuntimeFactory.CreateRun(
                    "blocking-cancellation-run",
                    "shutdown-test-world",
                    DateTimeOffset.UtcNow)
            });
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var elapsed = Stopwatch.StartNew();
            var shutdown = node.Typed
                .StopAsync(
                    TimeSpan.FromMilliseconds(150),
                    CancellationToken.None)
                .AsTask();
            elapsed.Stop();

            Assert(
                elapsed.Elapsed < TimeSpan.FromMilliseconds(250),
                "StopAsync synchronously ran a blocking cancellation callback.");
            await AssertThrowsAsync<TimeoutException>(
                shutdown,
                "A blocking cancellation callback escaped the shutdown deadline.");
        }
        finally
        {
            release.Set();
        }

        await node.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private static async Task VerifyFacadeErrorAsync(
        GameAgentRuntimeNode runtime)
    {
        var errorCompletion = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRuntimeError(GodotDictionary error) =>
            errorCompletion.TrySetResult(error);

        runtime.RuntimeError += OnRuntimeError;
        try
        {
            var malformedObservations = new GodotArray { 42 };
            var requestId = runtime.start_agent_run(
                new GodotDictionary(),
                malformedObservations);
            Assert(
                string.IsNullOrEmpty(requestId),
                "Malformed Variant input produced a runtime request.");

            var error = await errorCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                error["code"].AsString() == "invalid_run_request",
                "Malformed Variant input was not normalized into a stable error.");
        }
        finally
        {
            runtime.RuntimeError -= OnRuntimeError;
        }
    }

    private static void VerifyGodotJsonNumberCompatibility(
        SampleRuntimeFixture fixture)
    {
        var run = global::Godot.Json
            .ParseString(ProtocolJson.Serialize(fixture.Request.Run))
            .AsGodotDictionary();
        var observations = new GodotArray();
        foreach (var observation in fixture.Observations)
        {
            observations.Add(
                global::Godot.Json
                    .ParseString(ProtocolJson.Serialize(observation))
                    .AsGodotDictionary());
        }

        var mapped = GodotProtocolVariantMapper.ToDurableRunRequest(
            run,
            observations);
        Assert(mapped.Run.Budget.MaxTurns == 6, "Godot JSON float broke maxTurns.");
        Assert(
            mapped.Context[0].Priority == 100,
            "Godot JSON float broke observation priority.");
        Assert(
            mapped.Context[0].Required && !mapped.Context[0].CanDefer,
            "Observation-to-context mapping lost its required boundary.");
    }

    private TaskCompletionSource<GodotDictionary> Completion(
        string requestId)
    {
        return _completions.GetOrAdd(
            requestId,
            static _ => new TaskCompletionSource<GodotDictionary>(
                TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private void OnRuntimeEvent(GodotDictionary runtimeEvent)
    {
        Interlocked.Increment(ref _runtimeEventCount);
        lock (_runtimeEventGate)
        {
            _runtimeEventKinds.Add(runtimeEvent["kind"].AsString());
        }
    }

    private bool OrderedRuntimeEvent(string first, string second)
    {
        lock (_runtimeEventGate)
        {
            var firstIndex = _runtimeEventKinds.IndexOf(first);
            var secondIndex = _runtimeEventKinds.IndexOf(second);
            return firstIndex >= 0 && secondIndex > firstIndex;
        }
    }

    private void OnRunCompleted(GodotDictionary outcome)
    {
        Completion(outcome["request_id"].AsString()).TrySetResult(outcome);
    }

    private void OnRunFailed(GodotDictionary error)
    {
        Completion(error["request_id"].AsString()).TrySetException(
            new InvalidOperationException(
                $"Run failed: {error["code"].AsString()}"));
    }

    private static async Task<TException> AssertThrowsAsync<TException>(
        Task task,
        string message)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MutableClock : IRuntimeClock
    {
        public MutableClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class LegacyProvider : IModelProvider
    {
        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ModelResponse>(
                ModelResponse.Final(
                    ProtocolJson.ParseElement("""{"legacy":true}""")));
        }
    }

    private sealed class LegacyHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("No legacy action was expected.");
        }
    }

    private sealed class LegacyStore : ISessionStore
    {
        private readonly List<RuntimeEvent> _events = new();

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                _events.Add(
                    ProtocolJson.DeserializeRuntimeEvent(
                        ProtocolJson.Serialize(runtimeEvent)));
            }

            return default;
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                IReadOnlyList<RuntimeEvent> result = _events
                    .Where(item => string.Equals(
                        item.RunId,
                        runId,
                        StringComparison.Ordinal))
                    .ToArray();
                return new ValueTask<IReadOnlyList<RuntimeEvent>>(result);
            }
        }
    }

    private sealed class ContinuationCaptureRuntime : IDurableAgentRuntime
    {
        private readonly TaskCompletionSource<DurableRunContinuation?>
            _continuationReceived =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeControlPlane Controls { get; } = new();

        public Task<DurableRunContinuation?> ContinuationReceived =>
            _continuationReceived.Task;

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            _continuationReceived.TrySetResult(continuation);
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = new AgentRun
                    {
                        RunId = runId,
                        AgentId = "continuation-snapshot-agent",
                        WorldId = "continuation-snapshot-world",
                        State = RunStates.Completed,
                        Budget = new AgentBudget
                        {
                            MaxTurns = 1,
                            MaxDurationMs = 1_000,
                            MaxTokens = 1,
                            MaxActions = 1,
                            MaxCostUsd = "0"
                        },
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                });
        }
    }

    private sealed class LifecycleRuntime :
        IDurableAgentRuntime,
        IDisposable
    {
        private readonly List<string> _order;
        private readonly bool _failDispose;

        public LifecycleRuntime(
            List<string> order,
            bool failDispose = false)
        {
            _order = order;
            _failDispose = failDispose;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            _order.Add("runtime_dispose");
            if (_failDispose)
            {
                throw new InvalidOperationException(
                    "runtime dispose failed");
            }
        }
    }

    private sealed class AwaitableLifecycleRuntime :
        IDurableAgentRuntime,
        IAsyncDisposable
    {
        private readonly List<string> _order;
        private readonly TaskCompletionSource<bool> _releaseActiveRun =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stopStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AwaitableLifecycleRuntime(List<string> order)
        {
            _order = order;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public Task StopStarted => _stopStarted.Task;

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public void ReleaseActiveRun()
        {
            _releaseActiveRun.TrySetResult(true);
        }

        public async ValueTask DisposeAsync()
        {
            _order.Add("runtime_stop_started");
            _stopStarted.TrySetResult(true);
            await _releaseActiveRun.Task;
            _order.Add("runtime_final_write");
            _order.Add("runtime_stop_completed");
        }
    }

    private sealed class ThrowingCancellationRuntime :
        IDurableAgentRuntime,
        IDisposable
    {
        private readonly List<string> _order;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThrowingCancellationRuntime(List<string> order)
        {
            _order = order;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public Task Started => _started.Task;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException(
                    "cancellation callback failed"));
            _started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The run did not cancel.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            _order.Add("runtime_dispose");
        }
    }

    private sealed class BlockingCancellationRuntime :
        IDurableAgentRuntime,
        IDisposable
    {
        private readonly ManualResetEventSlim _release;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCancellationRuntime(ManualResetEventSlim release)
        {
            _release = release;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public Task Started => _started.Task;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                () => _release.Wait(TimeSpan.FromSeconds(5)));
            _started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The run did not cancel.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class LifecycleStore : IDurableSessionStore
    {
        private readonly List<string> _order;
        private readonly bool _failFlush;
        private readonly bool _failDispose;

        public LifecycleStore(
            List<string> order,
            bool failFlush,
            bool failDispose = false)
        {
            _order = order;
            _failFlush = failFlush;
            _failDispose = failDispose;
        }

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
            _ = expectedRunRevision;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(0, 1, false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            index,
                            expectedRunRevision.GetValueOrDefault()
                            + index
                            + 1,
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
            _order.Add("flush");
            return _failFlush
                ? ValueTask.FromException(
                    new InvalidOperationException("flush failed"))
                : default;
        }

        public ValueTask DisposeAsync()
        {
            _order.Add("store_dispose");
            return _failDispose
                ? ValueTask.FromException(
                    new InvalidOperationException(
                        "store dispose failed"))
                : default;
        }
    }
}
