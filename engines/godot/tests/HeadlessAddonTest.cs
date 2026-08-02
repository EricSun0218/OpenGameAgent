using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Generation;
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
        VerifyResultingGameContextReceiptRoundTrip();

        await VerifyDispatcherBoundsAsync(runtime);
        await VerifyNodeWaitsForStartedActionHandlersAsync();
        await VerifyEventPumpBoundsAsync();
        VerifyMultiActorUncertaintyErrorSurface();
        await VerifyDurableShutdownOrderAsync();
        await VerifyTypedIngressSnapshotAdmissionAsync();
        await VerifyTypedIngressSnapshotCoverageAsync();
        await VerifyCustomBackendSemanticAuthorityAsync();
        await VerifyTypedContinuationSnapshotAsync();
        await VerifyVariantDurableOptionsAsync();
        await VerifyVariantMultiActorAsync();
        await VerifyBackendWaitsForRuntimeBeforeFlushAsync();
        await VerifyBackendWaitCancellationDoesNotPoisonShutdownAsync();
        await VerifyShutdownWaitCleansTimeoutOwnershipAsync();
        await VerifyNodeWaitCancellationDoesNotPoisonShutdownAsync();
        await VerifyNodeSurfacesShutdownFailureAsync();
        await VerifyExitTreeRetriesTransientShutdownFailureAsync();
        await VerifyExitTreePublishesTerminalEventBeforeBackgroundRetryAsync();
        await VerifyNodeContinuesShutdownAfterCancellationFailureAsync();
        await VerifyNodeDoesNotRunCancellationCallbacksInlineAsync();
        await VerifyBlockingRunCancellationDoesNotStarveAnotherRunAsync();
        await VerifyNodeBoundsBlockedCancellationAsync();
        await VerifyNodeCancellationDispatcherRejectsProcessOverflowAsync();
        await VerifyNodeRetainsLifecycleReservationUntilOwnerDrainAsync();
        await VerifyRequestCancellationCoalescingAsync();
        await VerifyNodeRejectsReentryAsync();
        await VerifyUnknownBackendEffectRequiresReconciliationAsync();

        var fixture = SampleRuntimeFactory.Configure(runtime);
        await VerifyGenerationSurfaceAsync(runtime);
        await VerifyFacadeErrorAsync(runtime);
        VerifyGodotJsonNumberCompatibility(fixture);
        VerifyFractionalPayloadRoundTrip();
        VerifyGodotFloatIngressBoundary(fixture);
        await VerifyJsonNumberOutputBoundaryAsync(runtime);
        VerifyHeadlessMapperCollectionBounds(fixture);
        VerifyCompletionMapperCollectionBounds();
        VerifyVariantIngressBounds(fixture);
        runtime.RuntimeEventPublished += OnRuntimeEvent;
        runtime.RunCompleted += OnRunCompleted;
        runtime.RunFailed += OnRunFailed;

        await VerifyDurableToolLoopAsync(runtime, fixture);
        await VerifyTypedRoutingAndCompletionAsync(runtime, fixture);
        await VerifyTypedChildAgentAsync(runtime, fixture);
        await VerifyRequestCancellationAsync(runtime, fixture);
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

    private static async Task VerifyGenerationSurfaceAsync(
        GameAgentRuntimeNode runtime)
    {
        var provider = new GodotGenerationProvider();
        runtime.Typed.ConfigureGeneration(
            new GenerationRuntime(
                new[] { provider },
                new InMemoryGenerationJobStore(),
                new GodotGenerationArtifactStore()));
        var completed = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(GodotDictionary value) => completed.TrySetResult(value);
        runtime.GenerationUpdated += OnUpdated;
        try
        {
            var requestId = runtime.start_generation(new GodotDictionary
            {
                ["operation_id"] = "godot-generation-1",
                ["modality"] = GenerationModalities.StructuredContent,
                ["input"] = new GodotDictionary
                {
                    ["event"] = "monthly_tick",
                    ["month"] = 3.5
                },
                ["authority_id"] = "npc-1"
            });
            Assert(!string.IsNullOrEmpty(requestId), "Godot generation was not admitted.");
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(
                string.Equals(
                    result["request_id"].AsString(),
                    requestId,
                    StringComparison.Ordinal),
                "Godot generation request identity was not preserved.");
            Assert(
                string.Equals(
                    result["status"].AsString(),
                    GenerationJobStatuses.Succeeded,
                    StringComparison.Ordinal),
                "Godot generation did not publish a successful job.");
            Assert(
                provider.CapturedInput.GetProperty("month").GetDouble() == 3.5,
                "Godot generation lost floating-point structured input.");
        }
        finally
        {
            runtime.GenerationUpdated -= OnUpdated;
        }
    }

    private static void VerifyResultingGameContextReceiptRoundTrip()
    {
        var receipt = new ActionReceipt
        {
            OperationId = "godot-coordinate-operation",
            Revision = 1,
            Status = ReceiptStatuses.Succeeded,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            CommittedAt = DateTimeOffset.UnixEpoch
        };
        GameContextReceiptEnvelope.AttachResulting(
            receipt,
            new GameContextCoordinate(
                "world-1",
                "timeline-1",
                2,
                new GameEntityIdentity("npc-1", 5),
                stateVersion: "state-2",
                gameTime: new GameTimePoint(
                    "world-clock",
                    "timeline-1",
                    2,
                    40),
                sessionId: "session-1"));

        var restored = GodotProtocolVariantMapper.ToActionReceipt(
            GodotProtocolVariantMapper.ToDictionary(receipt));

        Assert(
            GameContextReceiptEnvelope.TryReadResulting(
                restored,
                out var coordinate)
            && coordinate!.StateVersion == "state-2"
            && coordinate.SessionId == "session-1"
            && coordinate.Observer!.EntityId == "npc-1"
            && coordinate.Observer.Incarnation == 5,
            "Godot receipt mapping lost the resulting game-context "
            + "extension.");
    }

    private async Task VerifyNodeRejectsReentryAsync()
    {
        var tree = GetTree();
        var node = new GameAgentRuntimeNode
        {
            Name = "SingleUseRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        tree.Root.RemoveChild(node);

        var rejected = false;
        try
        {
            node._EnterTree();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(
            rejected,
            "A stopped Godot runtime node accepted scene-tree re-entry.");
        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private static void VerifyBoundSurface(GameAgentRuntimeNode runtime)
    {
        var methods = new[]
        {
            "start_run",
            "start_agent_run",
            "start_agent_run_with_options",
            "start_routed_run",
            "start_completion",
            "resume_agent_run",
            "resume_agent_run_with_options",
            "start_agent_batch",
            "resume_agent_batch_participant",
            "abandon_agent_batch_participant",
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
        Assert(
            runtime.HasSignal(
                GameAgentRuntimeNode.SignalName.RoutedRunCompleted)
            && runtime.HasSignal(
                GameAgentRuntimeNode.SignalName.CompletionCompleted),
            "Routed execution or stateless completion signals were not bound.");
        Assert(
            runtime.HasSignal(GameAgentRuntimeNode.SignalName.BatchCompleted)
            && runtime.HasSignal(
                GameAgentRuntimeNode.SignalName.BatchParticipantCompleted)
            && runtime.HasSignal(GameAgentRuntimeNode.SignalName.BatchFailed)
            && runtime.HasSignal(GameAgentRuntimeNode.SignalName.BatchStarted)
            && runtime.HasSignal(GameAgentRuntimeNode.SignalName.ActorFinished)
            && runtime.HasSignal(GameAgentRuntimeNode.SignalName.BatchAborted),
            "Multi-actor lifecycle or completion signals were not bound.");
    }

    private static void VerifyMultiActorUncertaintyErrorSurface()
    {
        var error = GameAgentRuntimeNode.ToErrorDictionary(
            new GodotEventMessage
            {
                RequestId = "request-uncertain",
                Code = "batch_execution_uncertain",
                Category = "reconciliation",
                Message = "The lifecycle result is uncertain.",
                ReconciliationRequired = true,
                Phase = "participant_execution",
                BatchId = "batch-7",
                ParticipantRunId = "run-2",
                ParticipantAgentId = "npc-2",
                ParticipantDecisionKey = "decision-2",
                ParticipantInputIndex = 2,
                AffectedRunIds = new[] { "run-2", "run-3" }
            });
        var affected = error["affected_run_ids"].AsGodotArray();

        Assert(
            error["reconciliation_required"].AsBool()
            && error["phase"].AsString() == "participant_execution"
            && error["batch_id"].AsString() == "batch-7"
            && error["participant_run_id"].AsString() == "run-2"
            && error["participant_agent_id"].AsString() == "npc-2"
            && error["participant_decision_key"].AsString()
                == "decision-2"
            && error["participant_input_index"].AsInt32() == 2
            && affected.Count == 2
            && affected[0].AsString() == "run-2"
            && affected[1].AsString() == "run-3",
            "Multi-actor uncertainty lost its reconciliation identity.");
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

    private static async Task VerifyTypedRoutingAndCompletionAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        var routedCompletion = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleCompletion = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRouted(GodotDictionary outcome) =>
            routedCompletion.TrySetResult(outcome);
        void OnCompleted(GodotDictionary outcome) =>
            simpleCompletion.TrySetResult(outcome);
        runtime.RoutedRunCompleted += OnRouted;
        runtime.CompletionCompleted += OnCompleted;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var routedRequestId = runtime.Typed.StartRoutedRun(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest
                    {
                        OperationKind = "npc-bark"
                    },
                    Run = new DurableRunRequest
                    {
                        Run = SampleRuntimeFactory.CreateRun(
                            "godot-routed-run",
                            fixture.Request.Run.WorldId,
                            now)
                    }
                });
            var routed = await routedCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert(
                routed["request_id"].AsString() == routedRequestId
                && routed["path"].AsString() == "direct",
                "Typed Godot routing did not execute and publish the direct path.");
            Assert(
                fixture.Provider.RequestsFor("godot-routed-run")
                    .Single()
                    .Tools.Count == 0,
                "The Godot direct path exposed agent tools.");

            var completionRequestId = runtime.Typed.StartCompletion(
                new SimpleCompletionRequest
                {
                    OperationId = "godot-simple-completion",
                    Messages = new[]
                    {
                        new NormalizedMessage
                        {
                            MessageId = "godot-completion-message",
                            Role = NormalizedRoles.User,
                            CreatedAt = now,
                            Parts = new List<NormalizedContentPart>
                            {
                                NormalizedContentPart.FromText(
                                    "Return a short ambient line.")
                            }
                        }
                    },
                    MaxOutputTokens = 32
                });
            var completed = await simpleCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert(
                completed["request_id"].AsString() == completionRequestId
                && completed["operationId"].AsString()
                    == "godot-simple-completion"
                && completed["text"].AsString() == "completed",
                "Typed Godot stateless completion did not publish its result.");
            var routeIdentity = completed["routeIdentity"]
                .AsGodotDictionary();
            var usage = completed["usage"].AsGodotDictionary();
            Assert(
                routeIdentity["providerId"].AsString()
                    == "godot-sample-provider"
                && !string.IsNullOrWhiteSpace(
                    routeIdentity["modelId"].AsString())
                && !string.IsNullOrWhiteSpace(
                    routeIdentity["routeDigest"].AsString())
                && usage.ContainsKey("samples")
                && usage.ContainsKey("cacheMissTokens")
                && usage.ContainsKey("providerTotalTokens")
                && usage.ContainsKey("availability"),
                "Godot completion dropped route or usage audit fields.");
            Assert(
                fixture.Provider.RequestsFor("godot-simple-completion")
                    .Single()
                    .Tools.Count == 0,
                "The Godot stateless completion exposed agent tools.");

            routedCompletion = new TaskCompletionSource<GodotDictionary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var route = new GodotDictionary
            {
                ["operation_kind"] = "gdscript-npc-bark",
                ["explicit_path"] = "direct",
                ["requirements"] = new GodotArray(),
                ["signal"] = new GodotDictionary
                {
                    ["trigger"] = "proximity"
                }
            };
            var gdscriptRun = SampleRuntimeFactory.CreateRun(
                "godot-gdscript-routed-run",
                fixture.Request.Run.WorldId,
                now);
            var gdscriptRoutedRequestId = runtime.start_routed_run(
                route,
                GodotProtocolVariantMapper.ToDictionary(gdscriptRun),
                new GodotArray(),
                new GodotDictionary(),
                new GodotDictionary());
            var gdscriptRouted = await routedCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert(
                gdscriptRouted["request_id"].AsString()
                    == gdscriptRoutedRequestId
                && gdscriptRouted["path"].AsString() == "direct",
                "The GDScript routed surface did not execute the direct path.");

            simpleCompletion = new TaskCompletionSource<GodotDictionary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var message = new NormalizedMessage
            {
                MessageId = "godot-gdscript-completion-message",
                Role = NormalizedRoles.User,
                CreatedAt = now,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText(
                        "Return another short ambient line.")
                }
            };
            var messages = new GodotArray
            {
                global::Godot.Json
                    .ParseString(
                        ProtocolJson.Serialize(
                            NormalizedMessageJournalCodec.Encode(message)))
                    .AsGodotDictionary()
            };
            var completionOptions = new GodotDictionary
            {
                ["operation_id"] = "godot-gdscript-completion",
                ["messages"] = messages,
                ["max_output_tokens"] = 32
            };
            var gdscriptCompletionRequestId = runtime.start_completion(
                completionOptions);
            var gdscriptCompleted = await simpleCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert(
                gdscriptCompleted["request_id"].AsString()
                    == gdscriptCompletionRequestId
                && gdscriptCompleted["operationId"].AsString()
                    == "godot-gdscript-completion"
                && gdscriptCompleted["text"].AsString() == "completed",
                "The GDScript stateless completion surface lost its result.");
        }
        finally
        {
            runtime.RoutedRunCompleted -= OnRouted;
            runtime.CompletionCompleted -= OnCompleted;
        }
    }

    private async Task VerifyTypedChildAgentAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        var requestId = runtime.Typed.StartChildRun(
            "godot-parent-run",
            new DurableRunRequest
            {
                Run = SampleRuntimeFactory.CreateRun(
                    "godot-child-run",
                    fixture.Request.Run.WorldId,
                    DateTimeOffset.UtcNow),
                ExecutionMode = DurableExecutionModes.Direct
            });
        var outcome = await Completion(requestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        var run = outcome["run"].AsGodotDictionary();
        var extensions = run["extensions"].AsGodotDictionary();
        var lineage = extensions[ChildAgentLineage.ExtensionName]
            .AsGodotDictionary();
        Assert(
            run["state"].AsString() == RunStates.Completed
            && lineage["parentRunId"].AsString()
                == "godot-parent-run"
            && lineage["childRunId"].AsString()
                == "godot-child-run"
            && lineage["depth"].AsInt32() == 1,
            "Typed Godot child-agent execution lost durable lineage.");

        var grandchildRun = SampleRuntimeFactory.CreateRun(
            "godot-grandchild-run",
            fixture.Request.Run.WorldId,
            DateTimeOffset.UtcNow);
        var options = global::Godot.Json.ParseString(
                """{"execution_mode":"direct"}""")
            .AsGodotDictionary();
        var grandchildRequestId = runtime
            .start_child_agent_run_with_parent(
                run,
                GodotProtocolVariantMapper.ToDictionary(grandchildRun),
                new GodotArray(),
                options);
        var grandchildOutcome = await Completion(grandchildRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        var grandchild = grandchildOutcome["run"].AsGodotDictionary();
        var grandchildLineage = grandchild["extensions"]
            .AsGodotDictionary()[ChildAgentLineage.ExtensionName]
            .AsGodotDictionary();
        Assert(
            grandchildLineage["rootRunId"].AsString()
                == "godot-parent-run"
            && grandchildLineage["parentRunId"].AsString()
                == "godot-child-run"
            && grandchildLineage["depth"].AsInt32() == 2,
            "GDScript persistent-parent child execution lost lineage.");
    }

    private async Task VerifyRequestCancellationAsync(
        GameAgentRuntimeNode runtime,
        SampleRuntimeFixture fixture)
    {
        var failed = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFailed(GodotDictionary error)
        {
            if (error["code"].AsString() == "completion_cancelled")
            {
                failed.TrySetResult(error);
            }
        }

        runtime.RunFailed += OnFailed;
        try
        {
            const string operationId = "control-cancel-completion";
            var requestId = runtime.Typed.StartCompletion(
                new SimpleCompletionRequest
                {
                    OperationId = operationId,
                    Messages = new[]
                    {
                        new NormalizedMessage
                        {
                            MessageId = "cancel-completion-message",
                            Role = NormalizedRoles.User,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Parts = new List<NormalizedContentPart>
                            {
                                NormalizedContentPart.FromText("cancel")
                            }
                        }
                    }
                });
            await fixture.Provider.WaitForAttemptAsync(
                operationId,
                1,
                TimeSpan.FromSeconds(5));
            Assert(
                runtime.cancel_request(requestId),
                "Godot request cancellation was not admitted.");
            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(
                error["request_id"].AsString() == requestId,
                "Godot request cancellation published the wrong identity.");
            await WaitForConditionAsync(
                () => !runtime.cancel_request(requestId),
                TimeSpan.FromSeconds(2),
                "Godot request cancellation ownership was not cleaned up.");
        }
        finally
        {
            runtime.RunFailed -= OnFailed;
        }
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

    private async Task VerifyUnknownBackendEffectRequiresReconciliationAsync()
    {
        var tree = GetTree();
        var backend = new EffectThenCancelBackend();
        var node = new GameAgentRuntimeNode
        {
            Name = "EffectThenCancelRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.Configure(backend);

        var failed = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        node.RunFailed += error => failed.TrySetResult(error);
        var requestId = node.Typed.StartRun(
            new HeadlessRunRequest
            {
                Run = SampleRuntimeFactory.CreateRun(
                    "unknown-effect-run",
                    "unknown-effect-world",
                    DateTimeOffset.UtcNow)
            });

        var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            backend.EffectRecorded
            && error["request_id"].AsString() == requestId
            && error["code"].AsString() == "run_cancelled"
            && error["reconciliation_required"].AsBool()
            && error["phase"].AsString() == "headless_execution",
            "An unknown backend effect was exposed as safe to retry.");

        await node.Typed.StopAsync(TimeSpan.FromSeconds(2));
        node.QueueFree();
        await ToSignal(
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
            Name = "StartedActionDrainRuntime",
            ShutdownTimeoutSeconds = 0.1
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
                        TimeSpan.FromSeconds(1),
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

    private static async Task VerifyEventPumpBoundsAsync()
    {
        var sink = new MetricsSink();
        var metrics = new RuntimeMetricsEmitter(sink);
        var pump = new GodotEventPump(2, metrics);
        for (var index = 0; index < 1_000; index++)
        {
            var accepted = pump.TryPublish(new GodotEventMessage
            {
                Kind = GodotEventKinds.RuntimeStarted
            });
            Assert(
                accepted == (index < 2),
                "Event pump did not enforce its configured flood bound.");
        }

        var messages = new List<GodotEventMessage>();
        pump.Drain(3, TimeSpan.FromMilliseconds(10), messages.Add);
        Assert(messages.Count == 3, "Event pump did not report and drain overflow.");
        Assert(
            messages[0].Kind == GodotEventKinds.PumpOverflow
            && messages[0].Count == 998,
            "Event pump did not coalesce overflow diagnostics.");
        pump.StopAccepting();
        Assert(
            await metrics.StopAsync(),
            "Event-pump metrics did not drain.");
        Assert(
            sink.Records.Any(
                item => item.Name == RuntimeMetricNames.EventPumpDropped
                        && item.Value == 998
                        && item.Dimensions.Engine == "godot"),
            "Event-pump drop metrics were not emitted.");
        Assert(
            sink.Records.Count(
                item => item.Name
                        == RuntimeMetricNames
                            .EventPumpDispatchLatencyMilliseconds) == 2,
            "Event-pump latency metrics did not cover dispatched events.");
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
                new[] { "runtime_dispose", "flush" }),
            "Durable shutdown disposed the store after its flush failed.");

        await AssertThrowsAsync<InvalidOperationException>(
            backend.StopAsync(CancellationToken.None).AsTask(),
            "Repeated durable stop did not retry the failed flush.");
        Assert(
            order.SequenceEqual(
                new[] { "runtime_dispose", "flush", "flush" }),
            "Repeated durable stop reran a committed phase or skipped the failed phase.");

        var aggregateOrder = new List<string>();
        var aggregateBackend = new GodotDurableRuntimeBackend(
            new LifecycleRuntime(aggregateOrder, failDispose: true),
            new LifecycleStore(
                aggregateOrder,
                failFlush: true,
                failDispose: true),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        await AssertThrowsAsync<InvalidOperationException>(
            aggregateBackend.StopAsync(CancellationToken.None).AsTask(),
            "A durable runtime shutdown failure was swallowed.");
        Assert(
            aggregateOrder.SequenceEqual(new[] { "runtime_dispose" }),
            "Durable shutdown touched persistence after runtime shutdown failed.");

        var retryOrder = new List<string>();
        var retryBackend = new GodotDurableRuntimeBackend(
            new FailsOnceLifecycleRuntime(retryOrder),
            new LifecycleStore(retryOrder, failFlush: false),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        await AssertThrowsAsync<InvalidOperationException>(
            retryBackend.StopAsync(CancellationToken.None).AsTask(),
            "A transient runtime shutdown failure was swallowed.");
        Assert(
            retryOrder.SequenceEqual(new[] { "runtime_dispose" }),
            "Persistence was touched before retryable runtime shutdown completed.");
        await retryBackend.StopAsync(CancellationToken.None);
        Assert(
            retryOrder.SequenceEqual(
                new[]
                {
                    "runtime_dispose",
                    "runtime_dispose",
                    "flush",
                    "store_dispose"
                }),
            "Retry did not finish lifecycle phases in dependency order.");
    }

    private async Task VerifyTypedIngressSnapshotAdmissionAsync()
    {
        var tree = GetTree();
        var backend = new SnapshotAdmissionBackend();
        var blockedInput = new GatedThrowingReadOnlyList<ObservationEnvelope>(
            new[]
            {
                SampleRuntimeFactory.CreateObservation(
                    "blocked-snapshot-observation",
                    "snapshot-world",
                    "{\"value\":1}",
                    DateTimeOffset.UtcNow)
            });
        var node = new GameAgentRuntimeNode
        {
            Name = "TypedSnapshotAdmissionRuntime",
            MaxActiveRuns = 1
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.Configure(backend);

        Task<Exception?>? firstAttempt = null;
        Task<Exception?>? competingAttempt = null;
        try
        {
            firstAttempt = Task.Run(
                () => CaptureFailure(
                    () => node.Typed.StartRun(
                        new HeadlessRunRequest
                        {
                            Run = SampleRuntimeFactory.CreateRun(
                                "blocked-snapshot-run",
                                "snapshot-world",
                                DateTimeOffset.UtcNow),
                            Observations = blockedInput
                        })));
            await blockedInput.EnumerationStarted.WaitAsync(
                TimeSpan.FromSeconds(2));

            competingAttempt = Task.Run(
                () => CaptureFailure(
                    () => node.Typed.StartRun(
                        new HeadlessRunRequest
                        {
                            Run = SampleRuntimeFactory.CreateRun(
                                "competing-snapshot-run",
                                "snapshot-world",
                                DateTimeOffset.UtcNow)
                        })));
            var competingFailure = await competingAttempt.WaitAsync(
                TimeSpan.FromSeconds(1));
            Assert(
                competingFailure is InvalidOperationException
                && competingFailure.Message.Contains(
                    "active-run limit",
                    StringComparison.Ordinal),
                "A blocked caller-owned snapshot either held the lifecycle lock "
                + "or did not reserve active-run capacity before enumeration.");

            blockedInput.Release();
            var snapshotFailure = await firstAttempt.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                snapshotFailure is SnapshotProbeException,
                "A caller-owned snapshot enumeration failure was not returned "
                + "to the typed caller.");
            Assert(
                node.get_runtime_status()["active_runs"].AsInt32() == 0,
                "A failed typed snapshot did not roll back its active-run reservation.");
            Assert(
                backend.CallCount == 0,
                "A failed typed snapshot reached the Godot backend.");

            var original = SampleRuntimeFactory.CreateObservation(
                "owned-snapshot-observation",
                "snapshot-world",
                "{\"value\":2}",
                DateTimeOffset.UtcNow);
            var callerOwned = new List<ObservationEnvelope> { original };
            var completed = new TaskCompletionSource<GodotDictionary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string? acceptedRequestId = null;
            void OnCompleted(GodotDictionary outcome)
            {
                if (outcome["request_id"].AsString() == acceptedRequestId)
                {
                    completed.TrySetResult(outcome);
                }
            }

            node.RunCompleted += OnCompleted;
            try
            {
                acceptedRequestId = node.Typed.StartRun(
                    new HeadlessRunRequest
                    {
                        Run = SampleRuntimeFactory.CreateRun(
                            "owned-snapshot-run",
                            "snapshot-world",
                            DateTimeOffset.UtcNow),
                        Observations = callerOwned
                    });
                callerOwned[0] = SampleRuntimeFactory.CreateObservation(
                    "mutated-after-admission",
                    "snapshot-world",
                    "{\"value\":3}",
                    DateTimeOffset.UtcNow);
                var captured = await backend.RequestReceived.WaitAsync(
                    TimeSpan.FromSeconds(2));
                Assert(
                    captured.Observations.Count == 1
                    && captured.Observations[0].ObservationId
                        == "owned-snapshot-observation"
                    && !ReferenceEquals(
                        original,
                        captured.Observations[0]),
                    "Typed headless ingress did not give the backend an owned snapshot.");
                backend.Release();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                node.RunCompleted -= OnCompleted;
            }
        }
        finally
        {
            blockedInput.Release();
            backend.Release();
            if (firstAttempt is not null)
            {
                try
                {
                    await firstAttempt.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            if (competingAttempt is not null)
            {
                try
                {
                    await competingAttempt.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            try
            {
                await node.Typed.StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None);
            }
            finally
            {
                if (node.IsInsideTree())
                {
                    node.QueueFree();
                    await ToSignal(
                        tree,
                        global::Godot.SceneTree.SignalName.ProcessFrame);
                }
            }
        }
    }

    private async Task VerifyTypedIngressSnapshotCoverageAsync()
    {
        var tree = GetTree();
        var backend = new SnapshotRejectingBackend();
        var multiActorRuntime = new ContinuationCaptureRuntime();
        var node = new GameAgentRuntimeNode
        {
            Name = "TypedSnapshotCoverageRuntime",
            MaxActiveRuns = 1,
            MaxActorBatchSize = 2
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(backend);
        node.Typed.ConfigureMultiActor(multiActorRuntime);
        var baselineCancellationReservations =
            GodotRequestCancellationDispatcher.ReservationCount;

        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                AssertSnapshotRejected(
                    () => node.Typed.StartRun(
                        BadDurableRequest($"durable-{attempt}")),
                    node,
                    backend,
                    "durable");
                AssertSnapshotRejected(
                    () => node.Typed.StartRoutedRun(
                        new RoutedExecutionRequest
                        {
                            Route = new ExecutionRouteRequest
                            {
                                OperationKind = "snapshot-routing",
                                ExplicitPath = ExecutionPath.Agent
                            },
                            Run = BadDurableRequest($"routed-{attempt}")
                        }),
                    node,
                    backend,
                    "routed");
                AssertSnapshotRejected(
                    () => node.Typed.StartCompletion(
                        new SimpleCompletionRequest
                        {
                            OperationId =
                                $"snapshot-completion-{attempt}",
                            Messages =
                                new ThrowingReadOnlyList<NormalizedMessage>()
                        }),
                    node,
                    backend,
                    "completion");
            }

            await WaitForConditionAsync(
                () => GodotRequestCancellationDispatcher.ReservationCount
                    == baselineCancellationReservations,
                TimeSpan.FromSeconds(2),
                "Rejected typed snapshots did not release cancellation "
                + "reservations after owner drain.");
            AssertSnapshotRejected(
                () => node.Typed.ResumeRun(
                    "snapshot-resume-run",
                    new DurableRunContinuation
                    {
                        Context =
                            new ThrowingReadOnlyList<ContextCandidate>()
                    }),
                node,
                backend,
                "resume");
            AssertSnapshotRejected(
                () => node.Typed.StartBatch(
                    new MultiActorDecisionBatch(
                        "snapshot-batch",
                        new GameContextCoordinate(
                            "snapshot-world",
                            "snapshot-timeline",
                            1),
                        new[] { BadDurableRequest("actor-batch") })),
                node,
                backend,
                "actor batch");
            Assert(
                multiActorRuntime.RunCallCount == 0,
                "A failed actor-batch snapshot reached the multi-actor runtime.");
        }
        finally
        {
            await node.Typed.StopAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            node.QueueFree();
            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task VerifyCustomBackendSemanticAuthorityAsync()
    {
        var tree = GetTree();
        var backend = new SemanticAuthorityBackend();
        var node = new GameAgentRuntimeNode
        {
            Name = "CustomBackendSemanticAuthorityRuntime",
            MaxActiveRuns = 2
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(backend);

        try
        {
            using var run = GodotProtocolVariantMapper.ToDictionary(
                new AgentRun
                {
                    RunId = "backend-defined-gdscript-run",
                    Budget = null!
                });
            run["budget"] = new global::Godot.Variant();
            using var observations = new GodotArray();
            var requestId = node.start_agent_run(run, observations);
            Assert(
                !string.IsNullOrEmpty(requestId),
                "The GDScript facade rejected a backend-defined run model.");
            var receivedRun = await backend.DurableReceived.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                receivedRun.Run.RunId == "backend-defined-gdscript-run"
                && receivedRun.Run.Budget is null,
                "The Godot facade imposed built-runtime run semantics on a custom backend.");

            var completionId = node.Typed.StartCompletion(
                new SimpleCompletionRequest
                {
                    OperationId = "backend-defined-empty-completion",
                    Messages = Array.Empty<NormalizedMessage>()
                });
            Assert(
                !string.IsNullOrEmpty(completionId),
                "The typed facade rejected a backend-defined empty completion.");
            var receivedCompletion = await backend.CompletionReceived.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                receivedCompletion.Messages.Count == 0,
                "The Godot facade imposed built-runtime completion semantics on a custom backend.");
        }
        finally
        {
            backend.Release();
            await node.Typed.StopAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            node.QueueFree();
            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task VerifyRequestCancellationCoalescingAsync()
    {
        var tree = GetTree();
        using var callbackRelease =
            new ManualResetEventSlim(initialState: false);
        var backend = new RequestCancellationProbeBackend(callbackRelease);
        var node = new GameAgentRuntimeNode
        {
            Name = "RequestCancellationCoalescingRuntime",
            MaxActiveRuns = 1
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(backend);
        var baselineReservations =
            GodotRequestCancellationDispatcher.ReservationCount;
        var failed = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestId = null;
        void OnFailed(GodotDictionary error)
        {
            if (error["request_id"].AsString() == requestId)
            {
                failed.TrySetResult(error);
            }
        }

        node.RunFailed += OnFailed;
        try
        {
            requestId = node.Typed.StartCompletion(
                new SimpleCompletionRequest
                {
                    OperationId = "coalesced-cancellation",
                    Messages = new[]
                    {
                        new NormalizedMessage
                        {
                            MessageId = "coalesced-cancellation-message",
                            Role = NormalizedRoles.User,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Parts = new List<NormalizedContentPart>
                            {
                                NormalizedContentPart.FromText("cancel")
                            }
                        }
                    }
                });
            await backend.Started.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(
                GodotRequestCancellationDispatcher.ReservationCount
                    == baselineReservations + 1,
                "A cancellable request did not reserve bounded cancellation capacity.");

            var accepted = 0;
            await Task.Run(
                () => Parallel.For(
                    0,
                    10_000,
                    _ =>
                    {
                        if (node.Typed.CancelRequest(requestId))
                        {
                            Interlocked.Increment(ref accepted);
                        }
                    }));
            await backend.CallbackStarted.WaitAsync(TimeSpan.FromSeconds(2));
            await backend.BackendObservedCancellation.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                accepted == 1 && backend.CallbackCount == 1,
                "Ten thousand duplicate request cancellations were not "
                + "coalesced into one dispatcher operation.");
            Assert(
                !node.Typed.CancelRequest(requestId),
                "A duplicate request cancellation was admitted after dispatch.");
            Assert(
                GodotRequestCancellationDispatcher.ReservationCount
                    == baselineReservations + 1
                && node.get_runtime_status()["active_runs"].AsInt32() == 1,
                "Request cancellation ownership was released before its "
                + "blocking callback drained.");

            callbackRelease.Set();
            var cancellationError = await failed.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            Assert(
                cancellationError["code"].AsString()
                    == "completion_cancelled",
                "Coalesced cancellation did not publish the terminal request error.");
            await WaitForConditionAsync(
                () => GodotRequestCancellationDispatcher.ReservationCount
                    == baselineReservations
                    && node.get_runtime_status()["active_runs"].AsInt32()
                        == 0,
                TimeSpan.FromSeconds(3),
                "Request cancellation capacity was not returned after owner drain.");
        }
        finally
        {
            node.RunFailed -= OnFailed;
            callbackRelease.Set();
            try
            {
                await node.Typed.StopAsync(
                    TimeSpan.FromSeconds(3),
                    CancellationToken.None);
            }
            finally
            {
                if (node.IsInsideTree())
                {
                    node.QueueFree();
                    await ToSignal(
                        tree,
                        global::Godot.SceneTree.SignalName.ProcessFrame);
                }
            }
        }
    }

    private static DurableRunRequest BadDurableRequest(string suffix)
    {
        return new DurableRunRequest
        {
            Run = SampleRuntimeFactory.CreateRun(
                "snapshot-" + suffix + "-run",
                "snapshot-world",
                DateTimeOffset.UtcNow),
            Context = new ThrowingReadOnlyList<ContextCandidate>()
        };
    }

    private static void AssertSnapshotRejected(
        Action start,
        GameAgentRuntimeNode node,
        SnapshotRejectingBackend backend,
        string surface)
    {
        var failure = CaptureFailure(start);
        Assert(
            failure is SnapshotProbeException,
            $"Typed {surface} ingress did not enumerate its caller-owned "
            + "snapshot through the shared guard.");
        Assert(
            node.get_runtime_status()["active_runs"].AsInt32() == 0,
            $"Typed {surface} ingress did not roll back failed admission.");
        Assert(
            backend.InvocationCount == 0,
            $"Typed {surface} ingress reached the backend after snapshot failure.");
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string message)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(message);
            }

            await Task.Delay(10);
        }
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
                LaneId = "continuation-snapshot-lane",
                WorkloadClass = ProviderWorkloadClasses.Background,
                RequestCancellation = true
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
        Assert(
            captured.WorkloadClass == ProviderWorkloadClasses.Background,
            "Godot dropped the typed continuation workload class.");
        Assert(
            captured.RequestCancellation,
            "Godot dropped the typed durable-cancellation request.");

        await node.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyVariantDurableOptionsAsync()
    {
        var tree = GetTree();
        var captureRuntime = new ContinuationCaptureRuntime();
        var node = new GameAgentRuntimeNode
        {
            Name = "VariantDurableOptionsRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            captureRuntime,
            new LifecycleStore(new List<string>(), failFlush: false));

        var now = DateTimeOffset.Parse(
            "2026-07-30T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var run = SampleRuntimeFactory.CreateRun(
            "variant-options-run",
            "variant-options-world",
            now);
        var startOptions = global::Godot.Json.ParseString(
            """
            {
              "active_skills": [
                {
                  "skill_id": "npc-navigation",
                  "version": "1"
                }
              ],
              "workload_class": "background",
              "lane_id": "npc-background-lane",
              "initial_transcript": [
                {
                  "messageId": "seed-1",
                  "role": "user",
                  "createdAt": "2026-07-30T00:00:00Z",
                  "parts": [
                    {
                      "type": "json",
                      "json": {
                        "goal": "patrol"
                      }
                    }
                  ]
                }
              ]
            }
            """).AsGodotDictionary();
        var startRequestId = node.start_agent_run_with_options(
            GodotProtocolVariantMapper.ToDictionary(run),
            new GodotArray(),
            startOptions);
        Assert(
            !string.IsNullOrWhiteSpace(startRequestId),
            "The advanced GDScript start surface rejected valid options.");

        var request = await captureRuntime.RequestReceived
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            request.ActiveSkills.Count == 1
            && request.ActiveSkills[0].Value == "npc-navigation@1",
            "The advanced GDScript start surface lost active skills.");
        Assert(
            request.WorkloadClass == ProviderWorkloadClasses.Background,
            "The advanced GDScript start surface lost workload class.");
        Assert(
            request.LaneId == "npc-background-lane",
            "The advanced GDScript start surface lost lane id.");
        Assert(
            request.InitialTranscript.Count == 1
            && request.InitialTranscript[0].MessageId == "seed-1",
            "The advanced GDScript start surface lost the initial transcript.");

        var resumeOptions = global::Godot.Json.ParseString(
            """
            {
              "context": [
                {
                  "id": "continued-state",
                  "category": "state",
                  "content": {
                    "danger": 7
                  },
                  "priority": 30,
                  "required": true,
                  "can_defer": false,
                  "estimated_tokens": 8,
                  "expires_at": "2026-07-30T01:00:00Z",
                  "provenance": "trusted-host"
                }
              ],
              "active_skills": [
                {
                  "skill_id": "npc-combat",
                  "version": "2"
                }
              ],
              "replace_active_skills": true,
              "lane_id": "npc-urgent-lane",
              "workload_class": "interactive"
            }
            """).AsGodotDictionary();
        var resumeRequestId = node.resume_agent_run_with_options(
            "variant-options-run",
            resumeOptions);
        Assert(
            !string.IsNullOrWhiteSpace(resumeRequestId),
            "The advanced GDScript resume surface rejected valid options.");

        var continuation = await captureRuntime.ContinuationReceived
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            continuation is not null
            && continuation.Context.Count == 1
            && continuation.Context[0].Id == "continued-state"
            && continuation.Context[0].Required
            && !continuation.Context[0].CanDefer,
            "The advanced GDScript resume surface lost continuation context.");
        Assert(
            continuation!.ActiveSkills.Count == 1
            && continuation.ActiveSkills[0].Value == "npc-combat@2"
            && continuation.ReplaceActiveSkills,
            "The advanced GDScript resume surface lost skill replacement.");
        Assert(
            continuation.LaneId == "npc-urgent-lane"
            && continuation.WorkloadClass
                == ProviderWorkloadClasses.Interactive,
            "The advanced GDScript resume surface lost scheduling options.");

        var semanticValue = ProtocolJson.ParseElement(
            """{"revision":12,"timeline":"prime"}""");
        var semanticDigest =
            CanonicalJsonDigest.ComputeSha256(semanticValue);
        var guardedResumeId = node.resume_agent_run_with_options(
            "variant-options-run",
            global::Godot.Json.ParseString(
                $$"""
                {
                  "resume_guard": {
                    "semantic_extension_name": "game.coordinate",
                    "expected_semantic_extension_sha256": "{{semanticDigest}}"
                  }
                }
                """).AsGodotDictionary());
        Assert(
            !string.IsNullOrWhiteSpace(guardedResumeId),
            "The GDScript resume surface rejected a semantic guard.");
        var capturedGuard = await captureRuntime.GuardReceived
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            capturedGuard.SemanticExtensionName == "game.coordinate"
            && capturedGuard.ExpectedSemanticExtensionSha256
                == semanticDigest,
            "The GDScript resume surface lost its semantic guard.");

        var runCalls = captureRuntime.RunCallCount;
        var resumeCalls = captureRuntime.ResumeCallCount;
        Assert(
            string.IsNullOrEmpty(
                node.start_agent_run_with_options(
                    GodotProtocolVariantMapper.ToDictionary(run),
                    new GodotArray(),
                    new GodotDictionary
                    {
                        ["unknown_option"] = true
                    })),
            "The advanced start surface accepted an unknown option.");
        Assert(
            string.IsNullOrEmpty(
                node.resume_agent_run_with_options(
                    "variant-options-run",
                    new GodotDictionary
                    {
                        ["replace_active_skills"] = "yes"
                    })),
            "The advanced resume surface accepted a non-Boolean replacement.");
        Assert(
            string.IsNullOrEmpty(
                node.resume_agent_run_with_options(
                    "variant-options-run",
                    new GodotDictionary
                    {
                        ["lane_id"] = new string('x', 257)
                    })),
            "The advanced resume surface accepted an oversized lane id.");
        Assert(
            string.IsNullOrEmpty(
                node.resume_agent_run_with_options(
                    "variant-options-run",
                    global::Godot.Json.ParseString(
                        """
                        {
                          "resume_guard": {
                            "semantic_extension_name": "game.coordinate",
                            "expected_semantic_extension_sha256":
                              "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                          }
                        }
                        """).AsGodotDictionary())),
            "The advanced resume surface accepted a noncanonical digest.");
        Assert(
            string.IsNullOrEmpty(
                node.start_agent_run_with_options(
                    GodotProtocolVariantMapper.ToDictionary(run),
                    new GodotArray(),
                    global::Godot.Json.ParseString(
                        """
                        {
                          "initial_transcript": [
                            {
                              "messageId": "invalid-role",
                              "role": "operator",
                              "createdAt": "2026-07-30T00:00:00Z",
                              "parts": [
                                {
                                  "type": "text",
                                  "text": "invalid"
                                }
                              ]
                            }
                          ]
                        }
                        """).AsGodotDictionary())),
            "The advanced start surface accepted an unsupported message role.");
        Assert(
            string.IsNullOrEmpty(
                node.resume_agent_run_with_options(
                    "variant-options-run",
                    global::Godot.Json.ParseString(
                        """
                        {
                          "context": [
                            {
                              "id": "ambiguous-context",
                              "category": "state",
                              "content": {},
                              "resource": {
                                "uri": "game://state",
                                "media_type": "application/json"
                              }
                            }
                          ]
                        }
                        """).AsGodotDictionary())),
            "The advanced resume surface accepted ambiguous context content.");

        var excessiveSkills = new GodotArray();
        for (var index = 0; index < 129; index++)
        {
            excessiveSkills.Add(
                new GodotDictionary
                {
                    ["skill_id"] = $"skill-{index}",
                    ["version"] = "1"
                });
        }

        Assert(
            string.IsNullOrEmpty(
                node.start_agent_run_with_options(
                    GodotProtocolVariantMapper.ToDictionary(run),
                    new GodotArray(),
                    new GodotDictionary
                    {
                        ["active_skills"] = excessiveSkills
                    })),
            "The advanced start surface accepted too many active skills.");
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        Assert(
            captureRuntime.RunCallCount == runCalls
            && captureRuntime.ResumeCallCount == resumeCalls,
            "Invalid advanced options reached the durable backend.");

        await node.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);

        var unsupportedNode = new GameAgentRuntimeNode
        {
            Name = "UnguardedDurableRuntime"
        };
        tree.Root.AddChild(unsupportedNode);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        unsupportedNode.Typed.ConfigureDurable(
            new UnguardedDurableBackend());
        DurableRunResumeGuardException? unsupported = null;
        try
        {
            unsupportedNode.Typed.ResumeRun(
                "unsupported-run",
                new DurableRunResumeGuard
                {
                    SemanticExtensionName = "game.coordinate",
                    ExpectedSemanticExtensionSha256 = semanticDigest
                });
        }
        catch (DurableRunResumeGuardException exception)
        {
            unsupported = exception;
        }

        Assert(
            unsupported?.ReasonCode
                == DurableRunResumeGuardReasonCodes.NotSupported,
            "A guard crossed a Godot backend without guarded-resume support.");
        await unsupportedNode.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        unsupportedNode.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyVariantMultiActorAsync()
    {
        var tree = GetTree();
        var captureRuntime = new MultiActorCaptureRuntime(
            initialParticipantCount: 4);
        var node = new GameAgentRuntimeNode
        {
            Name = "VariantMultiActorRuntime",
            MaxActorBatchSize = 8,
            MaxConcurrentActorRuns = 3,
            MaxConcurrentActorBatches = 1
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            captureRuntime,
            new LifecycleStore(new List<string>(), failFlush: false));

        var batchCompletions = new ConcurrentDictionary<
            string,
            TaskCompletionSource<GodotDictionary>>(StringComparer.Ordinal);
        var participantCompletions = new ConcurrentDictionary<
            string,
            TaskCompletionSource<GodotDictionary>>(StringComparer.Ordinal);
        var batchFailures = new ConcurrentDictionary<
            string,
            TaskCompletionSource<GodotDictionary>>(StringComparer.Ordinal);
        var aborts = new ConcurrentDictionary<
            string,
            TaskCompletionSource<GodotDictionary>>(StringComparer.Ordinal);
        var lifecycleOrder = new List<string>();
        var actorFinishedAgents = new List<string>();

        TaskCompletionSource<GodotDictionary> CompletionFor(
            ConcurrentDictionary<
                string,
                TaskCompletionSource<GodotDictionary>> completions,
            string key) =>
            completions.GetOrAdd(
                key,
                static _ => new TaskCompletionSource<GodotDictionary>(
                    TaskCreationOptions.RunContinuationsAsynchronously));

        node.BatchCompleted += outcome =>
            CompletionFor(
                    batchCompletions,
                    outcome["request_id"].AsString())
                .TrySetResult(outcome);
        node.BatchParticipantCompleted += result =>
            CompletionFor(
                    participantCompletions,
                    result["request_id"].AsString())
                .TrySetResult(result);
        node.BatchFailed += error =>
            CompletionFor(
                    batchFailures,
                    error["request_id"].AsString())
                .TrySetResult(error);
        node.BatchStarted += manifest =>
        {
            lifecycleOrder.Add(
                $"started:{manifest["batch_id"].AsString()}");
        };
        node.ActorFinished += result =>
        {
            var agentId = result["agent_id"].AsString();
            actorFinishedAgents.Add(agentId);
            lifecycleOrder.Add($"finished:{agentId}");
        };
        node.BatchAborted += error =>
        {
            var batchId = error["batch_id"].AsString();
            lifecycleOrder.Add($"aborted:{batchId}");
            CompletionFor(aborts, batchId).TrySetResult(error);
        };

        var now = DateTimeOffset.Parse(
            "2026-07-30T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var runEntries = new GodotArray();
        for (var index = 0; index < 4; index++)
        {
            var run = SampleRuntimeFactory.CreateRun(
                $"multi-run-{index}",
                "multi-world",
                now);
            run.AgentId = $"npc-{index}";
            run.DecisionKey = $"decision-{index}";
            runEntries.Add(
                new GodotDictionary
                {
                    ["run"] =
                        GodotProtocolVariantMapper.ToDictionary(run),
                    ["observations"] = new GodotArray(),
                    ["options"] = new GodotDictionary
                    {
                        ["workload_class"] = index == 0
                            ? "interactive"
                            : "background",
                        ["lane_id"] = $"npc-lane-{index}"
                    }
                });
        }

        var batchInput = new GodotDictionary
        {
            ["batch_id"] = "village-tick-7",
            ["coordinate"] = new GodotDictionary
            {
                ["world_id"] = "multi-world",
                ["timeline_id"] = "main-timeline",
                ["save_revision"] = 7,
                ["session_id"] = "godot-sample-session",
                ["scene_id"] = "village",
                ["region_id"] = "north",
                ["state_version"] = "state-7",
                ["game_time"] = new GodotDictionary
                {
                    ["clock_id"] = "world-clock",
                    ["timeline_id"] = "main-timeline",
                    ["epoch"] = 1,
                    ["tick"] = 700
                },
                ["causality"] = new GodotDictionary
                {
                    ["event_id"] = "tick-7",
                    ["based_on_state_version"] = "state-7",
                    ["parent_event_ids"] = new GodotArray
                    {
                        "tick-6"
                    }
                }
            },
            ["aggregate_budget"] = new GodotDictionary
            {
                ["max_tokens"] = 32_000,
                ["max_actions"] = 16,
                ["max_duration_ms"] = 120_000,
                ["max_cost_usd"] = "4"
            },
            ["runs"] = runEntries
        };
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var mapped =
                GodotMultiActorVariantMapper.ToDecisionBatch(
                    batchInput,
                    maximumBatchSize: 8);
            Assert(
                mapped.Runs.Count == 4
                && mapped.Runs[0].Run.RunId == "multi-run-0"
                && mapped.Runs[3].LaneId == "npc-lane-3"
                && mapped.Coordinate.SessionId
                    == "godot-sample-session",
                "Repeated multi-actor Variant conversion lost participant "
                + "or shared-session data.");
        }

        var batchRequestId = node
            .Call("start_agent_batch", batchInput)
            .AsString();
        Assert(
            !string.IsNullOrWhiteSpace(batchRequestId),
            "The GDScript multi-actor surface rejected a valid batch.");

        await captureRuntime.InitialConcurrencyReached
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            lifecycleOrder.Count > 0
            && lifecycleOrder[0] == "started:village-tick-7",
            "BatchStarted did not settle before participant execution.");
        Assert(
            captureRuntime.MaximumConcurrentRuns == 3,
            "The Core coordinator did not apply bounded actor concurrency.");

        var queuedRun = SampleRuntimeFactory.CreateRun(
            "multi-run-queued",
            "multi-world",
            now);
        queuedRun.AgentId = "npc-queued";
        queuedRun.DecisionKey = "decision-queued";
        var queuedBatch = new GodotDictionary
        {
            ["batch_id"] = "village-tick-7-queued",
            ["coordinate"] = new GodotDictionary
            {
                ["world_id"] = "multi-world",
                ["timeline_id"] = "main-timeline",
                ["save_revision"] = 7,
                ["session_id"] = "godot-sample-session"
            },
            ["runs"] = new GodotArray
            {
                new GodotDictionary
                {
                    ["run"] =
                        GodotProtocolVariantMapper.ToDictionary(queuedRun),
                    ["observations"] = new GodotArray()
                }
            }
        };
        var queuedRequestId = node
            .Call("start_agent_batch", queuedBatch)
            .AsString();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        Assert(
            captureRuntime.RunCallCount == 3
            && !lifecycleOrder.Contains(
                "started:village-tick-7-queued",
                StringComparer.Ordinal),
            "A second batch bypassed global Godot batch admission.");
        captureRuntime.ReleaseInitialParticipants();

        var batchOutcome = await CompletionFor(
                batchCompletions,
                batchRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        _ = await CompletionFor(batchCompletions, queuedRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        var manifest = batchOutcome["manifest"].AsGodotDictionary();
        var participants = manifest["participants"].AsGodotArray();
        var results = batchOutcome["results"].AsGodotArray();
        Assert(
            manifest["batch_id"].AsString() == "village-tick-7"
            && participants.Count == 4,
            "The batch completion did not return its durable manifest.");
        Assert(
            manifest["coordinate"]
                    .AsGodotDictionary()["game_time"]
                    .AsGodotDictionary()["tick"]
                    .AsInt64()
                == 700
            && manifest["coordinate"]
                    .AsGodotDictionary()["session_id"]
                    .AsString()
                == "godot-sample-session",
            "The batch manifest lost a shared coordinate field.");
        var budgetReservation =
            manifest["budget_reservation"].AsGodotDictionary();
        Assert(
            budgetReservation["reserved_tokens"].AsInt64() == 32_000
            && budgetReservation["reserved_actions"].AsInt64() == 16
            && budgetReservation["reserved_duration_ms"].AsInt64()
                == 120_000
            && budgetReservation["reserved_cost_usd"].AsString() == "4",
            "The batch manifest lost its aggregate budget reservation.");
        for (var index = 0; index < results.Count; index++)
        {
            Assert(
                results[index]
                        .AsGodotDictionary()["input_index"]
                        .AsInt32()
                    == index,
                "Concurrent batch results did not retain input order.");
        }
        Assert(
            results[1]
                    .AsGodotDictionary()["outcome"]
                    .AsGodotDictionary()["final_output_omitted"]
                    .AsBool()
            && results[1]
                    .AsGodotDictionary()["outcome"]
                    .AsGodotDictionary()["final_output"]
                    .VariantType
                == global::Godot.Variant.Type.Nil,
            "The batch result surface did not bound a large final output.");

        Assert(
            actorFinishedAgents.SequenceEqual(new[] { "npc-1" }),
            "Initial lifecycle settlement included a nonterminal participant.");

        var reconcilingParticipant =
            participants[0].AsGodotDictionary();
        var reconcileRequestId = node
            .Call(
                "resume_agent_batch_participant",
                "village-tick-7",
                reconcilingParticipant,
                new GodotDictionary
                {
                    ["lane_id"] = "needs-reconciler"
                })
            .AsString();
        var reconcileResult = await CompletionFor(
                participantCompletions,
                reconcileRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        var reconcileOutcome =
            reconcileResult["outcome"].AsGodotDictionary();
        Assert(
            reconcileResult["operation"].AsString() == "resume"
            && reconcileOutcome["reconciliation_required"].AsBool()
            && !reconcileOutcome["terminal"].AsBool(),
            "A pending participant did not return explicit reconciliation state.");
        Assert(
            actorFinishedAgents.SequenceEqual(new[] { "npc-1" }),
            "A reconciling participant was incorrectly marked finished.");

        var resumedParticipant = participants[3].AsGodotDictionary();
        var participantCoordinate = new GameContextCoordinate(
            "multi-world",
            "main-timeline",
            saveRevision: 7,
            sceneId: "village",
            regionId: "north",
            stateVersion: "state-7",
            gameTime: new GameTimePoint(
                "world-clock",
                "main-timeline",
                epoch: 1,
                tick: 700),
            causality: new GameCausalityStamp(
                "tick-7",
                "state-7",
                new[] { "tick-6" }),
            sessionId: "godot-sample-session");
        var participantSemanticDigest =
            CanonicalJsonDigest.ComputeSha256(
                GameContextEnvelope.ToJson(participantCoordinate));
        var resumeRequestId = node
            .Call(
                "resume_agent_batch_participant",
                "village-tick-7",
                resumedParticipant,
                global::Godot.Json.ParseString(
                    $$"""
                    {
                      "semantic_expectation": {
                        "extension_name": "{{GameContextEnvelope.ExtensionName}}",
                        "expected_sha256": "{{participantSemanticDigest}}"
                      }
                    }
                    """).AsGodotDictionary())
            .AsString();
        var resumeResult = await CompletionFor(
                participantCompletions,
                resumeRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            resumeResult["outcome"]
                    .AsGodotDictionary()["run"]
                    .AsGodotDictionary()["state"]
                    .AsString()
                == RunStates.Completed
            && actorFinishedAgents.Contains(
                "npc-3",
                StringComparer.Ordinal),
            "Guarded participant resume did not settle its lifecycle.");

        var abandonedParticipant =
            participants[2].AsGodotDictionary();
        var abandonRequestId = node
            .Call(
                "abandon_agent_batch_participant",
                "village-tick-7",
                abandonedParticipant,
                "npc_despawned")
            .AsString();
        var abandonResult = await CompletionFor(
                participantCompletions,
                abandonRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            abandonResult["operation"].AsString() == "abandon"
            && abandonResult["outcome"]
                    .AsGodotDictionary()["run"]
                    .AsGodotDictionary()["state"]
                    .AsString()
                == RunStates.Cancelled
            && abandonResult["error"]
                    .AsGodotDictionary()["code"]
                    .AsString()
                == "participant_abandoned"
            && actorFinishedAgents.Contains(
                "npc-2",
                StringComparer.Ordinal),
            "Durable participant abandonment did not settle its lifecycle.");

        var forgedParticipant = global::Godot.Json
            .ParseString(global::Godot.Json.Stringify(resumedParticipant))
            .AsGodotDictionary();
        forgedParticipant["agent_id"] = "forged-agent";
        var sideEffectsBeforeForgery =
            captureRuntime.GuardedResumeSideEffectCount;
        var forgedRequestId = node
            .Call(
                "resume_agent_batch_participant",
                "village-tick-7",
                forgedParticipant,
                new GodotDictionary())
            .AsString();
        var forgedFailure = await CompletionFor(
                batchFailures,
                forgedRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            forgedFailure["code"].AsString()
                == "participant_guard_failed"
            && captureRuntime.GuardedResumeSideEffectCount
                == sideEffectsBeforeForgery,
            "A forged manifest participant crossed the guarded resume fence.");

        var abortRun = SampleRuntimeFactory.CreateRun(
            "multi-run-throws",
            "multi-world",
            now);
        abortRun.AgentId = "npc-throws";
        abortRun.DecisionKey = "decision-throws";
        var abortBatch = new GodotDictionary
        {
            ["batch_id"] = "village-tick-8",
            ["coordinate"] = new GodotDictionary
            {
                ["world_id"] = "multi-world",
                ["timeline_id"] = "main-timeline",
                ["save_revision"] = 8,
                ["session_id"] = "godot-sample-session"
            },
            ["runs"] = new GodotArray
            {
                new GodotDictionary
                {
                    ["run"] =
                        GodotProtocolVariantMapper.ToDictionary(abortRun),
                    ["observations"] = new GodotArray()
                }
            }
        };
        var abortedRequestId = node
            .Call("start_agent_batch", abortBatch)
            .AsString();
        var aborted = await CompletionFor(aborts, "village-tick-8")
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        var abortedFailure = await CompletionFor(
                batchFailures,
                abortedRequestId)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        var uncertainRuns =
            abortedFailure["affected_run_ids"].AsGodotArray();
        Assert(
            aborted["reason_code"].AsString()
                == "batch_execution_failed"
            && abortedFailure["code"].AsString()
                == "batch_execution_uncertain"
            && abortedFailure["reconciliation_required"].AsBool()
            && abortedFailure["phase"].AsString()
                == "participant_execution"
            && abortedFailure["batch_id"].AsString()
                == "village-tick-8"
            && uncertainRuns.Count == 1
            && uncertainRuns[0].AsString() == "multi-run-throws",
            "A failed batch did not expose its verifiable abort and "
            + "reconciliation identities.");

        var runCallsBeforeInvalid = captureRuntime.RunCallCount;
        batchInput["unknown_field"] = true;
        Assert(
            string.IsNullOrEmpty(
                node.Call("start_agent_batch", batchInput).AsString())
            && captureRuntime.RunCallCount == runCallsBeforeInvalid,
            "An invalid batch schema reached the durable runtime.");

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

    private static async Task VerifyShutdownWaitCleansTimeoutOwnershipAsync()
    {
        var baseline = GodotShutdownWait.PendingTimeoutCount;
        var shutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();
        var wait = GodotShutdownWait
            .WaitAsync(
                shutdown.Task,
                TimeSpan.FromMinutes(5),
                callerCancellation.Token)
            .AsTask();

        Assert(
            GodotShutdownWait.PendingTimeoutCount == baseline + 1,
            "The shutdown wait did not register its timeout.");
        callerCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            wait,
            "Caller cancellation did not win the shutdown wait.");
        await WaitForConditionAsync(
            () => GodotShutdownWait.PendingTimeoutCount == baseline,
            TimeSpan.FromSeconds(1),
            "The losing shutdown timeout remained scheduled.");

        var failedRegistration = GodotShutdownWait
            .WaitAsync(
                shutdown.Task,
                TimeSpan.FromMinutes(5),
                CancellationToken.None,
                static (_, _) => throw new ObjectDisposedException(
                    "test-cancellation-source"))
            .AsTask();
        await AssertThrowsAsync<ObjectDisposedException>(
            failedRegistration,
            "A synchronous registration failure was not surfaced.");

        Assert(
            GodotShutdownWait.PendingTimeoutCount == baseline,
            "Failed callback registration leaked timeout ownership.");
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
            new LifecycleStore(order, failFlushAttempts: 1),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        var stoppedStatuses = new List<string>();
        node.RuntimeStopped += summary =>
            stoppedStatuses.Add(summary["status"].AsString());

        await AssertThrowsAsync<InvalidOperationException>(
            node.Typed
                .StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .AsTask(),
            "The Godot node hid a durable shutdown failure.");
        Assert(
            order.SequenceEqual(
                new[] { "runtime_dispose", "flush" }),
            "The Godot host disposed persistence after a failed flush.");
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        Assert(
            stoppedStatuses.SequenceEqual(new[] { "shutdown_incomplete" }),
            "A failed direct stop did not emit exactly one terminal event.");

        await node.Typed.StopAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert(
            order.SequenceEqual(
                new[]
                {
                    "runtime_dispose",
                    "flush",
                    "flush",
                    "store_dispose"
                }),
            "The Godot host did not retry and complete durable cleanup.");
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        Assert(
            stoppedStatuses.SequenceEqual(new[] { "shutdown_incomplete" }),
            "A cleanup retry emitted a duplicate terminal event.");

        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task VerifyExitTreeRetriesTransientShutdownFailureAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var node = new GameAgentRuntimeNode
        {
            Name = "ExitRetryRuntime"
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            new LifecycleRuntime(order),
            new LifecycleStore(order, failFlushAttempts: 1),
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        var stoppedStatuses = new List<string>();
        node.RuntimeStopped += summary =>
            stoppedStatuses.Add(summary["status"].AsString());

        var exited = ToSignal(
            node,
            global::Godot.Node.SignalName.TreeExited);
        node.QueueFree();
        await exited;

        Assert(
            order.SequenceEqual(
                new[]
                {
                    "runtime_dispose",
                    "flush",
                    "flush",
                    "store_dispose"
                }),
            "ExitTree returned before retryable durable cleanup completed.");
        Assert(
            stoppedStatuses.SequenceEqual(new[] { "graceful" }),
            "Transient shutdown retries emitted more than one terminal event.");
    }

    private async Task
        VerifyExitTreePublishesTerminalEventBeforeBackgroundRetryAsync()
    {
        var tree = GetTree();
        var order = new List<string>();
        var store = new LifecycleStore(order, failFlush: true);
        var node = new GameAgentRuntimeNode
        {
            Name = "ExitTerminalEventRuntime",
            ShutdownTimeoutSeconds = 0.05
        };
        tree.Root.AddChild(node);
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        node.Typed.ConfigureDurable(
            new LifecycleRuntime(order),
            store,
            disposeRuntimeOnShutdown: true,
            disposeStoreOnShutdown: true);
        var stoppedStatuses = new List<string>();
        node.RuntimeStopped += summary =>
            stoppedStatuses.Add(summary["status"].AsString());

        var exited = ToSignal(
            node,
            global::Godot.Node.SignalName.TreeExited);
        node.QueueFree();
        await exited;

        Assert(
            stoppedStatuses.SequenceEqual(new[] { "shutdown_incomplete" }),
            "ExitTree did not emit one terminal event before background retry.");
        store.AllowFlush();
        await store.Disposed.WaitAsync(TimeSpan.FromSeconds(2));
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
        var stoppedStatuses = new List<string>();
        var stoppedPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        node.RuntimeStopped += summary =>
        {
            stoppedStatuses.Add(summary["status"].AsString());
            stoppedPublished.TrySetResult(true);
        };

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
            node.IsShutdownIncomplete,
            "A throwing cancellation callback was not reflected in shutdown state.");
        await stoppedPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
        Assert(
            stoppedStatuses.SequenceEqual(new[] { "shutdown_incomplete" }),
            "A terminal cancellation failure emitted an invalid stop event.");
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

    private async Task VerifyBlockingRunCancellationDoesNotStarveAnotherRunAsync()
    {
        var tree = GetTree();
        using var release = new ManualResetEventSlim(initialState: false);
        var runtime = new TwoRunCancellationRuntime(release);
        var node = new GameAgentRuntimeNode
        {
            Name = "IndependentRunCancellationRuntime",
            MaxActiveRuns = 2,
            ShutdownTimeoutSeconds = 0.15
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
                    "blocking-cancellation-run-a",
                    "shutdown-test-world",
                    DateTimeOffset.UtcNow)
            });
        node.Typed.StartRun(
            new DurableRunRequest
            {
                Run = SampleRuntimeFactory.CreateRun(
                    "independent-cancellation-run-b",
                    "shutdown-test-world",
                    DateTimeOffset.UtcNow)
            });
        await runtime.BothStarted.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var stop = node.Typed.StopAsync(
                    TimeSpan.FromMilliseconds(150),
                    CancellationToken.None)
                .AsTask();
            await runtime.BlockingCallbackStarted.WaitAsync(
                TimeSpan.FromSeconds(2));
            await runtime.IndependentCancellationObserved.WaitAsync(
                TimeSpan.FromSeconds(2));
            await AssertThrowsAsync<TimeoutException>(
                stop,
                "A blocked run unexpectedly completed bounded shutdown.");
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

    private async Task VerifyNodeBoundsBlockedCancellationAsync()
    {
        var tree = GetTree();
        await WaitForConditionAsync(
            () => GodotRequestCancellationDispatcher.ActiveCount == 0
                && GodotRequestCancellationDispatcher.PendingCount == 0
                && GodotRequestCancellationDispatcher.ReservationCount == 0,
            TimeSpan.FromSeconds(10),
            "The operation cancellation dispatcher was not quiescent before the bounded-cancellation test.");
        using var release = new ManualResetEventSlim(initialState: false);
        var runtime = new BlockingCancellationRuntime(release);
        var node = new GameAgentRuntimeNode
        {
            Name = "BoundedBlockingCancellationRuntime",
            ShutdownTimeoutSeconds = 0.1
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
                    "bounded-blocking-cancellation-run",
                    "shutdown-test-world",
                    DateTimeOffset.UtcNow)
            });
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var shutdown = node.Typed
                .StopAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .AsTask();
            await runtime.CancellationCallbackStarted.WaitAsync(
                TimeSpan.FromSeconds(2));
            Exception? failure = null;
            try
            {
                await shutdown;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(
                failure is TimeoutException
                    || failure is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(
                        exception => exception is TimeoutException),
                "A blocked cancellation callback did not report a bounded timeout.");
        }
        finally
        {
            release.Set();
        }

        await WaitForConditionAsync(
            () => GodotRequestCancellationDispatcher.ActiveCount == 0
                && GodotRequestCancellationDispatcher.PendingCount == 0
                && GodotRequestCancellationDispatcher.ReservationCount == 0,
            TimeSpan.FromSeconds(10),
            "The operation cancellation dispatcher retained released work.");
        node.QueueFree();
        await ToSignal(
            tree,
            global::Godot.SceneTree.SignalName.ProcessFrame);
    }

    private async Task
        VerifyNodeCancellationDispatcherRejectsProcessOverflowAsync()
    {
        var tree = GetTree();
        using var release = new ManualResetEventSlim(initialState: false);
        using var overflowRelease =
            new ManualResetEventSlim(initialState: false);
        var nodes = new List<GameAgentRuntimeNode>();
        var stops = new List<Task>();
        GameAgentRuntimeNode? overflow = null;
        BlockingCancellationRuntime? overflowRuntime = null;
        var baselineLifecycleReservations =
            GodotCancellationDispatcher.ReservationCount;

        await WaitForConditionAsync(
            () => GodotRequestCancellationDispatcher.ActiveCount == 0
                && GodotRequestCancellationDispatcher.PendingCount == 0
                && GodotRequestCancellationDispatcher.ReservationCount == 0,
            TimeSpan.FromSeconds(10),
            "The operation cancellation dispatcher was not quiescent before the capacity test.");

        try
        {
            for (var index = 0;
                  index < GodotRequestCancellationDispatcher.Capacity;
                 index++)
            {
                var runtime = new BlockingCancellationRuntime(release);
                var node = new GameAgentRuntimeNode
                {
                    Name = "CancellationCapacityRuntime" + index,
                    ShutdownTimeoutSeconds = 15
                };
                tree.Root.AddChild(node);
                await ToSignal(
                    tree,
                    global::Godot.SceneTree.SignalName.ProcessFrame);
                node.Typed.ConfigureDurable(
                    runtime,
                    new LifecycleStore(
                        new List<string>(),
                        failFlush: false),
                    disposeRuntimeOnShutdown: true,
                    disposeStoreOnShutdown: true);
                node.Typed.StartRun(
                    new DurableRunRequest
                    {
                        Run = SampleRuntimeFactory.CreateRun(
                            "capacity-run-" + index,
                            "shutdown-test-world",
                            DateTimeOffset.UtcNow)
                    });
                await runtime.Started.WaitAsync(TimeSpan.FromSeconds(10));
                nodes.Add(node);
                var stop = node.Typed
                    .StopAsync(
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None)
                    .AsTask();
                stops.Add(stop);
                await runtime.CancellationCallbackStarted.WaitAsync(
                    TimeSpan.FromSeconds(10));
            }

            Assert(
                GodotRequestCancellationDispatcher.ActiveCount
                    == GodotRequestCancellationDispatcher.Capacity,
                "The process operation-cancellation dispatcher did not reach its fixed capacity.");

            overflow = new GameAgentRuntimeNode
            {
                Name = "CancellationCapacityOverflowRuntime",
                ShutdownTimeoutSeconds = 2
            };
            tree.Root.AddChild(overflow);
            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
            overflowRuntime =
                new BlockingCancellationRuntime(overflowRelease);
            overflow.Typed.ConfigureDurable(
                overflowRuntime,
                new LifecycleStore(
                    new List<string>(),
                    failFlush: false),
                disposeRuntimeOnShutdown: true,
                disposeStoreOnShutdown: true);
            overflow.Typed.StartRun(
                new DurableRunRequest
                {
                    Run = SampleRuntimeFactory.CreateRun(
                        "capacity-overflow-run",
                        "shutdown-test-world",
                        DateTimeOffset.UtcNow)
                });
            await overflowRuntime.Started.WaitAsync(
                TimeSpan.FromSeconds(10));

            var overflowExited = ToSignal(
                overflow,
                global::Godot.Node.SignalName.TreeExited);
            overflow.QueueFree();
            await overflowExited;
            overflow = null;
            Assert(
                GodotRequestCancellationDispatcher.PendingCount == 1,
                "Automatic per-operation cancellation was not queued.");
            Assert(
                GodotRequestCancellationDispatcher.ActiveCount
                    == GodotRequestCancellationDispatcher.Capacity,
                "Queued operation cancellation exceeded the worker bound.");
            Assert(
                !overflowRuntime.CancellationCallbackStarted.IsCompleted,
                "Queued lifecycle cancellation ran before capacity was released.");

            release.Set();
            await overflowRuntime.CancellationCallbackStarted.WaitAsync(
                TimeSpan.FromSeconds(10));
            overflowRelease.Set();
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.Set();
            overflowRelease.Set();
            try
            {
                await Task.WhenAll(stops).WaitAsync(
                    TimeSpan.FromSeconds(30));
            }
            catch
            {
            }

            foreach (var node in nodes)
            {
                node.QueueFree();
            }

            if (overflow is not null && overflow.IsInsideTree())
            {
                overflow.QueueFree();
            }

            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
        }

        await WaitForConditionAsync(
            () => GodotRequestCancellationDispatcher.ActiveCount == 0
                && GodotRequestCancellationDispatcher.PendingCount == 0
                && GodotRequestCancellationDispatcher.ReservationCount == 0,
            TimeSpan.FromSeconds(15),
            "The operation cancellation queue or reservation did not drain.");

        var reservations =
            new List<GodotCancellationDispatcher.Reservation>();
        try
        {
            while (GodotCancellationDispatcher.TryReserve(
                       out var reservation))
            {
                reservations.Add(reservation!);
            }

            Assert(
                GodotCancellationDispatcher.ReservationCount
                    == GodotCancellationDispatcher.ReservationCapacity,
                "The lifecycle reservation limit was not reached.");
            Assert(
                !GodotCancellationDispatcher.TryReserve(out _),
                "Lifecycle reservations exceeded the process-wide limit.");

            var rejected = new GameAgentRuntimeNode();
            InvalidOperationException? rejection = null;
            try
            {
                rejected._EnterTree();
            }
            catch (InvalidOperationException exception)
            {
                rejection = exception;
            }
            finally
            {
                rejected.Free();
            }

            Assert(
                rejection is not null
                && rejection.Message.Contains(
                    "lifecycle-cancellation capacity",
                    StringComparison.Ordinal),
                "A node without a future cancellation reservation was admitted.");
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }
        }

        await WaitForConditionAsync(
            () => GodotCancellationDispatcher.ReservationCount
                == baselineLifecycleReservations,
            TimeSpan.FromSeconds(2),
            "Unused lifecycle reservations were not returned.");
    }

    private async Task
        VerifyNodeRetainsLifecycleReservationUntilOwnerDrainAsync()
    {
        var tree = GetTree();
        var baselineReservations =
            GodotCancellationDispatcher.ReservationCount;
        var node = new GameAgentRuntimeNode
        {
            Name = "LifecycleOwnerDrainRuntime",
            ShutdownTimeoutSeconds = 0.1
        };
        var backend = new BlockingStopBackend();
        var dispatcherStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations =
            new List<GodotCancellationDispatcher.Reservation>();
        Task<int>? running = null;

        try
        {
            tree.Root.AddChild(node);
            await ToSignal(
                tree,
                global::Godot.SceneTree.SignalName.ProcessFrame);
            node.Typed.Configure(backend);
            running = node.Dispatcher
                .InvokeAsync(
                    async _ =>
                    {
                        dispatcherStarted.TrySetResult(true);
                        await dispatcherRelease.Task.ConfigureAwait(false);
                        return 1;
                    },
                    "lifecycle-owner-drain")
                .AsTask();
            node.Dispatcher.Drain(
                maxCommands: 1,
                TimeSpan.FromMilliseconds(10));
            await dispatcherStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Exception? shutdownFailure = null;
            try
            {
                await node.Typed
                    .StopAsync(
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .AsTask();
            }
            catch (Exception exception)
            {
                shutdownFailure = exception;
            }

            Assert(
                shutdownFailure is TimeoutException
                    || shutdownFailure is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(
                        exception => exception is TimeoutException),
                "Blocked owner work did not produce a bounded shutdown failure.");
            Assert(
                !backend.StopStarted.IsCompleted,
                "Backend cleanup started before dispatcher ownership drained.");
            Assert(
                GodotCancellationDispatcher.ReservationCount
                    == baselineReservations + 1,
                "The lifecycle reservation was released before owner work drained.");

            while (GodotCancellationDispatcher.TryReserve(
                       out var reservation))
            {
                reservations.Add(reservation!);
            }

            Assert(
                GodotCancellationDispatcher.ReservationCount
                    == GodotCancellationDispatcher.ReservationCapacity,
                "Hostile owner work did not retain its lifecycle reservation.");
            var rejected = new GameAgentRuntimeNode();
            InvalidOperationException? rejection = null;
            try
            {
                rejected._EnterTree();
            }
            catch (InvalidOperationException exception)
            {
                rejection = exception;
            }
            finally
            {
                rejected.Free();
            }

            Assert(
                rejection is not null,
                "A node was admitted while every lifecycle reservation was held.");
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }

            dispatcherRelease.TrySetResult(true);
            backend.Release();
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (node.IsInsideTree())
            {
                node.QueueFree();
                await ToSignal(
                    tree,
                    global::Godot.SceneTree.SignalName.ProcessFrame);
            }
        }

        await WaitForConditionAsync(
            () => GodotCancellationDispatcher.ReservationCount
                == baselineReservations,
            TimeSpan.FromSeconds(3),
            "The lifecycle reservation was not returned after real owner drain.");
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

            errorCompletion = new TaskCompletionSource<GodotDictionary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            requestId = runtime.start_child_agent_run(
                "facade-parent",
                new GodotDictionary(),
                malformedObservations,
                new GodotDictionary());
            Assert(
                string.IsNullOrEmpty(requestId),
                "Malformed child Variant input produced a runtime request.");

            error = await errorCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                error["code"].AsString() == "invalid_run_request",
                "Malformed child Variant input escaped the facade boundary.");
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

        var directOptions = global::Godot.Json.ParseString(
            """
            {
              "execution_mode":"direct",
              "inference": {
                "reasoning_enabled": false,
                "temperature": 0.4,
                "prompt_caching_enabled": true
              },
              "provider_route": {
                "provider_ids": ["fast-model"],
                "allow_unlisted_fallback": false
              }
            }
            """).AsGodotDictionary();
        var direct = GodotProtocolVariantMapper.ToDurableRunRequest(
            run,
            observations,
            directOptions);
        Assert(
            direct.ExecutionMode == DurableExecutionModes.Direct,
            "The Godot options mapper lost the durable direct mode.");
        Assert(
            direct.Inference?.Temperature == 0.4
            && direct.Inference.ReasoningEnabled == false
            && direct.RoutePreference?.ProviderIds.Single() == "fast-model",
            "The Godot options mapper lost inference or model routing controls.");
    }

    private static void VerifyFractionalPayloadRoundTrip()
    {
        var observation = new ObservationEnvelope
        {
            ObservationId = "fractional-payload",
            WorldId = "world",
            Source = "game.state",
            Kind = ObservationKinds.Snapshot,
            ContentType = "application/json",
            Payload = ProtocolJson.ParseElement(
                """
                {
                  "temperature": 36.625,
                  "position": [1.25, -3.5],
                  "nested": { "weight": 0.125 }
                }
                """),
            ObservedAt = DateTimeOffset.UnixEpoch,
            Trust = ObservationTrustLevels.Authoritative,
            Visibility = new VisibilityRule
            {
                Scope = ObservationVisibilityScopes.World
            }
        };

        using var mapped =
            GodotProtocolVariantMapper.ToDictionary(observation);
        var roundTrip = GodotProtocolVariantMapper.ToObservation(mapped);
        var payload = roundTrip.Payload!.Value;
        Assert(
            payload.GetProperty("temperature").GetDouble() == 36.625
            && payload.GetProperty("position")[0].GetDouble() == 1.25
            && payload.GetProperty("position")[1].GetDouble() == -3.5
            && payload.GetProperty("nested")
                .GetProperty("weight").GetDouble() == 0.125,
            "Godot lost a finite fractional JSON value.");
    }

    private static void VerifyGodotFloatIngressBoundary(
        SampleRuntimeFixture fixture)
    {
        using var observation = GodotProtocolVariantMapper.ToDictionary(
            fixture.Observations[0]);
        using var payload = new GodotDictionary
        {
            ["int64_exclusive_upper"] =
                9_223_372_036_854_775_808d,
            ["integral_float"] = 1.0d,
            ["negative_zero"] = -0.0d
        };
        observation["payload"] = payload;

        var mapped = GodotProtocolVariantMapper.ToObservation(observation);
        var json = mapped.Payload!.Value;
        Assert(
            json.GetProperty("int64_exclusive_upper").GetDouble()
            == 9_223_372_036_854_775_808d,
            "Godot silently saturated a Float at the Int64 upper boundary.");
        Assert(
            BitConverter.DoubleToInt64Bits(
                json.GetProperty("negative_zero").GetDouble())
            == BitConverter.DoubleToInt64Bits(-0.0d),
            "Godot silently erased the sign of negative zero.");

        using var roundTrip = GodotProtocolVariantMapper.ToDictionary(mapped);
        using var roundTripPayload = roundTrip["payload"].AsGodotDictionary();
        using var integralFloat = roundTripPayload["integral_float"];
        using var negativeZero = roundTripPayload["negative_zero"];
        Assert(
            integralFloat.VariantType == global::Godot.Variant.Type.Float
            && integralFloat.AsDouble() == 1.0d,
            "Godot changed an integral Float into an Int on round trip.");
        Assert(
            negativeZero.VariantType == global::Godot.Variant.Type.Float
            && BitConverter.DoubleToInt64Bits(negativeZero.AsDouble())
            == BitConverter.DoubleToInt64Bits(-0.0d),
            "Godot changed negative-zero Float identity on round trip.");
    }

    private static async Task VerifyJsonNumberOutputBoundaryAsync(
        GameAgentRuntimeNode runtime)
    {
        using (var fraction =
               GodotProtocolVariantMapper.ParseVariant("0.1"))
        {
            Assert(
                fraction.VariantType == global::Godot.Variant.Type.Float
                && fraction.AsDouble() == 0.1,
                "The Godot output mapper rejected an ordinary fraction.");
        }

        using (var scientific =
               GodotProtocolVariantMapper.ParseVariant("1e100"))
        {
            Assert(
                scientific.VariantType == global::Godot.Variant.Type.Float
                && scientific.AsDouble() == 1e100,
                "The Godot output mapper rejected a finite scientific value.");
        }

        using (var exactBeyondInt64 =
               GodotProtocolVariantMapper.ParseVariant(
                   "9223372036854775808"))
        {
            Assert(
                exactBeyondInt64.VariantType
                == global::Godot.Variant.Type.Float
                && exactBeyondInt64.AsDouble()
                == 9_223_372_036_854_775_808d,
                "The Godot output mapper rejected an exactly representable float.");
        }

        AssertNumberMappingFailure(
            "1e400",
            "godot_json_number_out_of_range");
        AssertNumberMappingFailure(
            "9223372036854775809",
            "godot_json_number_precision_loss");
        AssertNumberMappingFailure(
            "0.12345678901234567890123456789",
            "godot_json_number_precision_loss");

        var errorCompletion = new TaskCompletionSource<GodotDictionary>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRuntimeError(GodotDictionary error)
        {
            if (error["code"].AsString()
                == "godot_json_number_out_of_range")
            {
                errorCompletion.TrySetResult(error);
            }
        }

        runtime.RuntimeError += OnRuntimeError;
        try
        {
            Assert(
                runtime.EventPump.TryPublish(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.CompletionCompleted,
                        RequestId = "number-output-boundary",
                        Json = "{\"content\":1e400}"
                    }),
                "The numeric boundary fixture could not queue an event.");
            var error = await errorCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert(
                error["request_id"].AsString()
                == "number-output-boundary"
                && error["category"].AsString() == "mapping"
                && error["phase"].AsString() == "godot_variant_output",
                "An unrepresentable output number did not fail through the stable signal boundary.");
        }
        finally
        {
            runtime.RuntimeError -= OnRuntimeError;
        }
    }

    private static void AssertNumberMappingFailure(
        string json,
        string expectedReasonCode)
    {
        try
        {
            using var ignored =
                GodotProtocolVariantMapper.ParseVariant(json);
        }
        catch (GodotJsonNumberMappingException exception)
        {
            Assert(
                exception.ReasonCode == expectedReasonCode,
                "The Godot number mapper returned the wrong stable reason code.");
            return;
        }

        throw new InvalidOperationException(
            "The Godot output mapper silently accepted a lossy JSON number.");
    }

    private static void VerifyHeadlessMapperCollectionBounds(
        SampleRuntimeFixture fixture)
    {
        var run = GodotProtocolVariantMapper.ToDictionary(
            fixture.Request.Run);
        var observation = GodotProtocolVariantMapper.ToDictionary(
            fixture.Observations[0]);
        var tool = GodotProtocolVariantMapper.ToDictionary(
            new ToolDescriptor
            {
                Name = "observe",
                Version = "1",
                Description = "Observe the current game state.",
                ParametersSchema = ProtocolJson.ParseElement(
                    """{"type":"object"}"""),
                Effect = ToolEffects.PureRead
            });
        var observations = new GodotArray();
        var tools = new GodotArray();
        for (var index = 0; index < 512; index++)
        {
            observations.Add(observation);
            tools.Add(tool);
        }

        var accepted = GodotProtocolVariantMapper.ToRunRequest(
            run,
            observations,
            tools);
        Assert(
            accepted.Observations.Count == 512
            && accepted.Tools.Count == 512,
            "The documented headless collection boundary was rejected.");

        observations.Add(observation);
        AssertJsonFailure(
            () => GodotProtocolVariantMapper.ToRunRequest(
                run,
                observations,
                tools),
            "513 observations were allocated by the headless mapper.");
        observations.RemoveAt(observations.Count - 1);
        tools.Add(tool);
        AssertJsonFailure(
            () => GodotProtocolVariantMapper.ToRunRequest(
                run,
                observations,
                tools),
            "513 tools were allocated by the headless mapper.");
    }

    private static void VerifyCompletionMapperCollectionBounds()
    {
        using var messages = new GodotArray();
        for (var index = 0; index < 4_096; index++)
        {
            var message = new NormalizedMessage
            {
                MessageId = $"completion-boundary-{index}",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("x")
                }
            };
            messages.Add(
                global::Godot.Json
                    .ParseString(
                        ProtocolJson.Serialize(
                            NormalizedMessageJournalCodec.Encode(message)))
                    .AsGodotDictionary());
        }

        using var options = new GodotDictionary
        {
            ["messages"] = messages
        };
        var accepted = GodotProtocolVariantMapper
            .ToSimpleCompletionRequest(options);
        Assert(
            accepted.Messages.Count == 4_096,
            "The documented completion-message boundary was rejected.");

        var overflow = new NormalizedMessage
        {
            MessageId = "completion-boundary-overflow",
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("x")
            }
        };
        messages.Add(
            global::Godot.Json
                .ParseString(
                    ProtocolJson.Serialize(
                        NormalizedMessageJournalCodec.Encode(overflow)))
                .AsGodotDictionary());
        AssertJsonFailure(
            () => GodotProtocolVariantMapper
                .ToSimpleCompletionRequest(options),
            "4,097 completion messages crossed the mapper boundary.");
    }

    private static void VerifyVariantIngressBounds(
        SampleRuntimeFixture fixture)
    {
        using (var observation =
               GodotProtocolVariantMapper.ToDictionary(
                   fixture.Observations[0]))
        using (var oversizedPayload = new GodotDictionary
        {
            ["value"] = new string('x', 65_536)
        })
        {
            observation["payload"] = oversizedPayload;
            var boundary =
                GodotProtocolVariantMapper.ToObservation(observation);
            Assert(
                boundary.Payload!.Value
                    .GetProperty("value")
                    .GetString()!
                    .Length == 65_536,
                "The Variant string UTF-8 boundary was not preserved.");
            oversizedPayload["value"] = new string('x', 65_537);
            AssertJsonFailure(
                () => GodotProtocolVariantMapper.ToObservation(
                    observation),
                "An oversized Variant string reached Godot JSON serialization.");
        }

        using (var observation =
               GodotProtocolVariantMapper.ToDictionary(
                   fixture.Observations[0]))
        using (var widePayload = new GodotArray())
        {
            for (var index = 0;
                 index < ProtocolLimits.MaxProtocolJsonContainerItems;
                 index++)
            {
                widePayload.Add(index);
            }

            observation["payload"] = widePayload;
            var boundary =
                GodotProtocolVariantMapper.ToObservation(observation);
            Assert(
                boundary.Payload!.Value.GetArrayLength()
                == ProtocolLimits.MaxProtocolJsonContainerItems,
                "The protocol JSON container boundary was not preserved.");
            widePayload.Add(ProtocolLimits.MaxProtocolJsonContainerItems);
            AssertJsonFailure(
                () => GodotProtocolVariantMapper.ToObservation(
                    observation),
                "A wide Variant container reached Godot JSON serialization.");
        }

        using (var observation =
               GodotProtocolVariantMapper.ToDictionary(
                   fixture.Observations[0]))
        {
            var arrays = new List<GodotArray>();
            try
            {
                var root = new GodotArray();
                arrays.Add(root);
                var current = root;
                for (var depth = 1; depth < 65; depth++)
                {
                    var child = new GodotArray();
                    arrays.Add(child);
                    current.Add(child);
                    current = child;
                }

                observation["payload"] = root;
                AssertJsonFailure(
                    () => GodotProtocolVariantMapper.ToObservation(
                        observation),
                    "A deeply nested Variant graph reached Godot JSON serialization.");
                observation.Remove("payload");
            }
            finally
            {
                foreach (var array in arrays)
                {
                    array.Clear();
                }

                foreach (var array in arrays)
                {
                    array.Dispose();
                }
            }
        }

        using (var observation =
               GodotProtocolVariantMapper.ToDictionary(
                   fixture.Observations[0]))
        using (var batch = new GodotDictionary())
        using (var cycle = new GodotArray())
        {
            cycle.Add(cycle);
            observation["payload"] = cycle;
            batch["cycle"] = cycle;
            try
            {
                AssertJsonFailure(
                    () => GodotProtocolVariantMapper.ToObservation(
                        observation),
                    "A circular Variant graph reached Godot JSON serialization.");
                AssertJsonFailure(
                    () => GodotMultiActorVariantMapper.ToDecisionBatch(
                        batch,
                        maximumBatchSize: 1),
                    "A circular batch Variant graph reached Godot JSON serialization.");
            }
            finally
            {
                observation.Remove("payload");
                batch.Remove("cycle");
                cycle.Clear();
            }
        }
    }

    private static void AssertJsonFailure(
        Action action,
        string message)
    {
        try
        {
            action();
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        throw new InvalidOperationException(message);
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

    private sealed class GodotGenerationProvider : IGenerationProvider
    {
        public string Name => "godot_generation_test";

        public GenerationProviderCapabilities Capabilities { get; } = new()
        {
            Modalities = new[] { GenerationModalities.StructuredContent }
        };

        public JsonElement CapturedInput { get; private set; }

        public ValueTask<GenerationSubmission> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedInput = request.Input.Clone();
            return new ValueTask<GenerationSubmission>(new GenerationSubmission
            {
                Acceptance = GenerationAcceptance.Accepted,
                Result = new GenerationProviderResult
                {
                    Status = GenerationJobStatuses.Succeeded,
                    Output = request.Input.Clone()
                }
            });
        }

        public ValueTask<GenerationProviderResult> GetAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GenerationCancelResult> CancelAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class GodotGenerationArtifactStore : IGenerationArtifactStore
    {
        public ValueTask<GenerationArtifact> ImportAsync(
            string operationId,
            int ordinal,
            GenerationArtifactSource source,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SnapshotProbeException : Exception
    {
        internal SnapshotProbeException()
            : base("Caller-owned snapshot enumeration failed.")
        {
        }
    }

    private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count => 1;

        public T this[int index] => throw new SnapshotProbeException();

        public IEnumerator<T> GetEnumerator() =>
            throw new SnapshotProbeException();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GatedThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<bool> _enumerationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GatedThrowingReadOnlyList(IReadOnlyList<T> items)
        {
            _items = items;
        }

        internal Task EnumerationStarted => _enumerationStarted.Task;

        public int Count => _items.Count;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            _enumerationStarted.TrySetResult(true);
            _release.Wait();
            throw new SnapshotProbeException();
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        internal void Release()
        {
            _release.Set();
        }
    }

    private sealed class SnapshotAdmissionBackend : IGodotRuntimeBackend
    {
        private readonly TaskCompletionSource<HeadlessRunRequest>
            _requestReceived =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal Task<HeadlessRunRequest> RequestReceived =>
            _requestReceived.Task;

        internal int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _requestReceived.TrySetResult(request);
            await _release.Task.WaitAsync(cancellationToken);
            return new HeadlessRunOutcome
            {
                Run = request.Run
            };
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _release.TrySetResult(true);
            return default;
        }

        internal void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class SnapshotRejectingBackend :
        IGodotDurableRuntimeBackend,
        IGodotRoutedExecutionBackend
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _invocationCount);
            throw new InvalidOperationException(
                "Snapshot-rejected durable work reached the backend.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            _ = cancellationToken;
            Interlocked.Increment(ref _invocationCount);
            throw new InvalidOperationException(
                "Snapshot-rejected resume work reached the backend.");
        }

        public ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
            RoutedExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _invocationCount);
            throw new InvalidOperationException(
                "Snapshot-rejected routed work reached the backend.");
        }

        public ValueTask<SimpleCompletionOutcome> CompleteAsync(
            SimpleCompletionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _invocationCount);
            throw new InvalidOperationException(
                "Snapshot-rejected completion work reached the backend.");
        }

        public bool TryPostControl(
            string runId,
            RunControlCommand command)
        {
            _ = runId;
            _ = command;
            return false;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    private sealed class SemanticAuthorityBackend :
        IGodotDurableRuntimeBackend,
        IGodotRoutedExecutionBackend
    {
        private readonly TaskCompletionSource<DurableRunRequest>
            _durableReceived = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SimpleCompletionRequest>
            _completionReceived = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<DurableRunRequest> DurableReceived =>
            _durableReceived.Task;

        internal Task<SimpleCompletionRequest> CompletionReceived =>
            _completionReceived.Task;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            _durableReceived.TrySetResult(request);
            await _release.Task.WaitAsync(cancellationToken);
            return new DurableRunOutcome { Run = CompletedRun(request.Run.RunId) };
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
            RoutedExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask<SimpleCompletionOutcome> CompleteAsync(
            SimpleCompletionRequest request,
            CancellationToken cancellationToken)
        {
            _completionReceived.TrySetResult(request);
            await _release.Task.WaitAsync(cancellationToken);
            return new SimpleCompletionOutcome
            {
                OperationId = request.OperationId ?? "backend-completion",
                Text = "completed"
            };
        }

        public bool TryPostControl(
            string runId,
            RunControlCommand command) => false;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _release.TrySetResult(true);
            return default;
        }

        internal void Release() => _release.TrySetResult(true);

        private static AgentRun CompletedRun(string runId)
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentRun
            {
                RunId = runId,
                AgentId = "backend-defined-agent",
                WorldId = "backend-defined-world",
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
            };
        }
    }

    private sealed class RequestCancellationProbeBackend :
        IGodotDurableRuntimeBackend,
        IGodotRoutedExecutionBackend
    {
        private readonly ManualResetEventSlim _callbackRelease;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _callbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool>
            _backendObservedCancellation =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _callbackCount;

        internal RequestCancellationProbeBackend(
            ManualResetEventSlim callbackRelease)
        {
            _callbackRelease = callbackRelease;
        }

        internal Task Started => _started.Task;

        internal Task CallbackStarted => _callbackStarted.Task;

        internal Task BackendObservedCancellation =>
            _backendObservedCancellation.Task;

        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
            RoutedExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask<SimpleCompletionOutcome> CompleteAsync(
            SimpleCompletionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _registration = cancellationToken.Register(
                () =>
                {
                    Interlocked.Increment(ref _callbackCount);
                    _callbackStarted.TrySetResult(true);
                    _callbackRelease.Wait();
                });
            _started.TrySetResult(true);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            _backendObservedCancellation.TrySetResult(true);
            throw new OperationCanceledException(cancellationToken);
        }

        public bool TryPostControl(
            string runId,
            RunControlCommand command) => false;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _registration.Dispose();
            return default;
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

    private sealed class MultiActorCaptureRuntime :
        IGuardedDurableAgentRuntime
    {
        private readonly int _initialConcurrencyTarget;
        private readonly ConcurrentDictionary<string, AgentRun> _runs =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _initialConcurrencyReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseInitial =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRuns;
        private int _maximumConcurrentRuns;
        private int _runCallCount;
        private int _guardedResumeSideEffectCount;

        internal MultiActorCaptureRuntime(int initialParticipantCount)
        {
            if (initialParticipantCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialParticipantCount));
            }

            _initialConcurrencyTarget = Math.Min(
                3,
                initialParticipantCount);
        }

        public RuntimeControlPlane Controls { get; } = new();

        internal Task InitialConcurrencyReached =>
            _initialConcurrencyReached.Task;

        internal int MaximumConcurrentRuns =>
            Volatile.Read(ref _maximumConcurrentRuns);

        internal int RunCallCount => Volatile.Read(ref _runCallCount);

        internal int GuardedResumeSideEffectCount =>
            Volatile.Read(ref _guardedResumeSideEffectCount);

        internal void ReleaseInitialParticipants()
        {
            _releaseInitial.TrySetResult(true);
        }

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _runCallCount);
            if (string.Equals(
                    request.Run.AgentId,
                    "npc-throws",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Injected participant failure.");
            }

            var active = Interlocked.Increment(ref _activeRuns);
            UpdateMaximum(active);
            if (active >= _initialConcurrencyTarget)
            {
                _initialConcurrencyReached.TrySetResult(true);
            }

            try
            {
                await _releaseInitial.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                var run = CloneRun(request.Run);
                run.State = string.Equals(
                        run.AgentId,
                        "npc-1",
                        StringComparison.Ordinal)
                    ? RunStates.Completed
                    : RunStates.WaitingForAction;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                _runs[run.RunId] = CloneRun(run);
                return new DurableRunOutcome
                {
                    Run = run,
                    FinalOutput = string.Equals(
                            run.AgentId,
                            "npc-1",
                            StringComparison.Ordinal)
                        ? ProtocolJson.ParseElement(
                            "{\"value\":\""
                            + new string('x', 33_000)
                            + "\"}")
                        : null
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeRuns);
            }
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runs.TryGetValue(runId, out var stored))
            {
                throw new KeyNotFoundException();
            }

            if (guard is not null)
            {
                ValidateGuard(stored, runId, guard);
            }

            Interlocked.Increment(ref _guardedResumeSideEffectCount);
            var run = CloneRun(stored);
            run.PendingOperationIds.Clear();
            if (continuation?.RequestCancellation == true)
            {
                run.State = RunStates.Cancelled;
                run.TerminalReason = "cancelled_by_host";
            }
            else if (string.Equals(
                         continuation?.LaneId,
                         "needs-reconciler",
                         StringComparison.Ordinal))
            {
                run.State = RunStates.Reconciling;
                run.PendingOperationIds.Add("operation-1");
            }
            else
            {
                run.State = RunStates.Completed;
            }

            run.UpdatedAt = DateTimeOffset.UtcNow;
            _runs[runId] = CloneRun(run);
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = run });
        }

        private static void ValidateGuard(
            AgentRun run,
            string runId,
            DurableRunResumeGuard guard)
        {
            if (!string.Equals(run.RunId, runId, StringComparison.Ordinal))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.RunIdMismatch);
            }

            if (!string.Equals(
                    run.BatchId,
                    guard.ExpectedBatchId,
                    StringComparison.Ordinal))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.BatchIdMismatch);
            }

            if (!string.Equals(
                    run.AgentId,
                    guard.ExpectedAgentId,
                    StringComparison.Ordinal))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.AgentIdMismatch);
            }

            if (!string.Equals(
                    run.DecisionKey,
                    guard.ExpectedDecisionKey,
                    StringComparison.Ordinal))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.DecisionKeyMismatch);
            }

            var extensionName = guard.RequiredInt32ExtensionName
                ?? throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.ExtensionMissing);
            if (!run.Extensions.TryGetValue(
                    extensionName,
                    out var extension))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.ExtensionMissing);
            }

            if (!extension.TryGetInt32(out var value))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.ExtensionNotInt32);
            }

            if (value < guard.MinimumInt32ExtensionValue
                || value > guard.MaximumInt32ExtensionValue)
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.ExtensionOutOfRange);
            }

            if (guard.ExpectedInt32ExtensionValue.HasValue
                && value != guard.ExpectedInt32ExtensionValue.Value)
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.ExtensionValueMismatch);
            }

            if (guard.SemanticExtensionName is not null)
            {
                if (!run.Extensions.TryGetValue(
                        guard.SemanticExtensionName,
                        out var semanticExtension))
                {
                    throw new DurableRunResumeGuardException(
                        DurableRunResumeGuardReasonCodes
                            .SemanticExtensionMissing);
                }

                if (!string.Equals(
                        CanonicalJsonDigest.ComputeSha256(semanticExtension),
                        guard.ExpectedSemanticExtensionSha256,
                        StringComparison.Ordinal))
                {
                    throw new DurableRunResumeGuardException(
                        DurableRunResumeGuardReasonCodes
                            .SemanticExtensionDigestMismatch);
                }
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentRuns);
                if (value <= current
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrentRuns,
                        value,
                        current) == current)
                {
                    return;
                }
            }
        }

        private static AgentRun CloneRun(AgentRun run) =>
            ProtocolJson.DeserializeAgentRun(ProtocolJson.Serialize(run));
    }

    private sealed class ContinuationCaptureRuntime
        : IGuardedDurableAgentRuntime
    {
        private readonly TaskCompletionSource<DurableRunRequest>
            _requestReceived =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<DurableRunContinuation?>
            _continuationReceived =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<DurableRunResumeGuard>
            _guardReceived =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCallCount;
        private int _resumeCallCount;

        public RuntimeControlPlane Controls { get; } = new();

        public Task<DurableRunRequest> RequestReceived =>
            _requestReceived.Task;

        public Task<DurableRunContinuation?> ContinuationReceived =>
            _continuationReceived.Task;

        public Task<DurableRunResumeGuard> GuardReceived =>
            _guardReceived.Task;

        public int RunCallCount => Volatile.Read(ref _runCallCount);

        public int ResumeCallCount => Volatile.Read(ref _resumeCallCount);

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _runCallCount);
            _requestReceived.TrySetResult(request);
            return new ValueTask<DurableRunOutcome>(
                CompletedOutcome(request.Run.RunId));
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resumeCallCount);
            _continuationReceived.TrySetResult(continuation);
            return new ValueTask<DurableRunOutcome>(CompletedOutcome(runId));
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            if (guard is null)
            {
                return ResumeAsync(
                    runId,
                    continuation,
                    reconciler,
                    cancellationToken);
            }

            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resumeCallCount);
            _continuationReceived.TrySetResult(continuation);
            _guardReceived.TrySetResult(guard);
            return new ValueTask<DurableRunOutcome>(CompletedOutcome(runId));
        }

        private static DurableRunOutcome CompletedOutcome(string runId)
        {
            var now = DateTimeOffset.UtcNow;
            return new DurableRunOutcome
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
            };
        }
    }

    private sealed class UnguardedDurableBackend
        : IGodotDurableRuntimeBackend
    {
        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool TryPostControl(
            string runId,
            RunControlCommand command) => false;

        public ValueTask StopAsync(
            CancellationToken cancellationToken) => default;
    }

    private sealed class EffectThenCancelBackend : IGodotRuntimeBackend
    {
        public bool EffectRecorded { get; private set; }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            EffectRecorded = true;
            return new ValueTask<HeadlessRunOutcome>(
                Task.FromException<HeadlessRunOutcome>(
                    new OperationCanceledException(
                        "The backend outcome is unknown.")));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
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

    private sealed class FailsOnceLifecycleRuntime :
        IDurableAgentRuntime,
        IDisposable
    {
        private readonly List<string> _order;
        private int _disposeAttempts;

        public FailsOnceLifecycleRuntime(List<string> order)
        {
            _order = order;
        }

        public RuntimeControlPlane Controls { get; } = new();

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
            _order.Add("runtime_dispose");
            if (Interlocked.Increment(ref _disposeAttempts) == 1)
            {
                throw new InvalidOperationException(
                    "transient runtime dispose failure");
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
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException(
                    "cancellation callback failed"));
            _started.TrySetResult(true);
            await cancellation;
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
        private readonly TaskCompletionSource<bool> _cancellationCallbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCancellationRuntime(ManualResetEventSlim release)
        {
            _release = release;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public Task Started => _started.Task;

        public Task CancellationCallbackStarted =>
            _cancellationCallbackStarted.Task;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    _cancellationCallbackStarted.TrySetResult(true);
                    _release.Wait();
                });
            _started.TrySetResult(true);
            await cancellation;
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

    private sealed class TwoRunCancellationRuntime :
        IDurableAgentRuntime,
        IDisposable
    {
        private readonly ManualResetEventSlim _release;
        private readonly TaskCompletionSource<bool> _bothStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _blockingCallbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool>
            _independentCancellationObserved = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        internal TwoRunCancellationRuntime(ManualResetEventSlim release)
        {
            _release = release;
        }

        public RuntimeControlPlane Controls { get; } = new();

        internal Task BothStarted => _bothStarted.Task;

        internal Task BlockingCallbackStarted =>
            _blockingCallbackStarted.Task;

        internal Task IndependentCancellationObserved =>
            _independentCancellationObserved.Task;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                () =>
                {
                    if (request.Run.RunId == "blocking-cancellation-run-a")
                    {
                        _blockingCallbackStarted.TrySetResult(true);
                        _release.Wait();
                    }
                    else
                    {
                        _independentCancellationObserved.TrySetResult(true);
                    }
                });
            if (Interlocked.Increment(ref _started) == 2)
            {
                _bothStarted.TrySetResult(true);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The run did not cancel.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class BlockingStopBackend : IGodotRuntimeBackend
    {
        private readonly TaskCompletionSource<bool> _stopStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stopRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopStarted => _stopStarted.Task;

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<HeadlessRunOutcome>(
                Task.FromException<HeadlessRunOutcome>(
                    new NotSupportedException()));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stopStarted.TrySetResult(true);
            return new ValueTask(_stopRelease.Task);
        }

        public void Release()
        {
            _stopRelease.TrySetResult(true);
        }
    }

    private sealed class MetricsSink : IRuntimeMetricsSink
    {
        public ConcurrentQueue<RuntimeMetric> Records { get; } = new();

        public ValueTask RecordAsync(
            RuntimeMetric metric,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Enqueue(metric);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleStore : IDurableSessionStore
    {
        private readonly List<string> _order;
        private int _remainingFlushFailures;
        private readonly bool _failDispose;
        private readonly TaskCompletionSource<bool> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LifecycleStore(
            List<string> order,
            bool failFlush,
            bool failDispose = false)
        {
            _order = order;
            _remainingFlushFailures = failFlush ? int.MaxValue : 0;
            _failDispose = failDispose;
        }

        public Task Disposed => _disposed.Task;

        public void AllowFlush()
        {
            Interlocked.Exchange(ref _remainingFlushFailures, 0);
        }

        public LifecycleStore(
            List<string> order,
            int failFlushAttempts,
            bool failDispose = false)
        {
            _order = order;
            _remainingFlushFailures = Math.Max(0, failFlushAttempts);
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
            var remaining = Volatile.Read(ref _remainingFlushFailures);
            var shouldFail = remaining > 0;
            if (shouldFail && remaining != int.MaxValue)
            {
                Interlocked.Decrement(ref _remainingFlushFailures);
            }
            return shouldFail
                ? ValueTask.FromException(
                    new InvalidOperationException("flush failed"))
                : default;
        }

        public ValueTask DisposeAsync()
        {
            _order.Add("store_dispose");
            if (_failDispose)
            {
                return ValueTask.FromException(
                    new InvalidOperationException(
                        "store dispose failed"));
            }

            _disposed.TrySetResult(true);
            return default;
        }
    }
}
