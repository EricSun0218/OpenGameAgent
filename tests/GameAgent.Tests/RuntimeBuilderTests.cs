using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Tests;

public sealed class RuntimeBuilderTests
{
    [Fact]
    public async Task DirectDurableRunHidesToolsAndSkillsAndCapturesMode()
    {
        var store = new ShutdownTrackingStore();
        var provider = new DirectInspectionProvider();
        var publisher = new RecordingRuntimeEventPublisher();
        await using var built = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .WithTools(new[] { SlowTool() })
            .WithSkills(new[] { Skill("hidden-skill") })
            .PublishEventsTo(publisher)
            .Build();
        var request = new DurableRunRequest
        {
            Run = NewRun("direct-durable"),
            ExecutionMode = DurableExecutionModes.Direct
        };
        DurableExecutionPolicyBinding.Attach(
            request.Run,
            built.Runtime.CaptureExecutionPolicyIdentity(
                DurableExecutionModes.Direct));

        var outcome = await built.Runtime.RunAsync(request);

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(provider.ToolNames);
        Assert.DoesNotContain(
            "hidden-skill",
            provider.Prompt,
            StringComparison.Ordinal);
        var captured = Assert.Single(
            publisher.Events,
            item => string.Equals(
                item.Kind,
                RuntimeEventKinds.RunInputCaptured,
                StringComparison.Ordinal));
        Assert.Equal(
            DurableExecutionModes.Direct,
            captured.Payload.GetProperty("executionMode").GetString());
    }

    [Fact]
    public async Task DirectDurableRunRejectsSkillActivationBeforeDispatch()
    {
        var store = new ShutdownTrackingStore();
        var provider = new DirectInspectionProvider();
        await using var built = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .WithSkills(new[] { Skill("forbidden-skill") })
            .Build();
        var request = new DurableRunRequest
        {
            Run = NewRun("direct-skill-rejected"),
            ExecutionMode = DurableExecutionModes.Direct,
            ActiveSkills = new[]
            {
                new SkillReference("forbidden-skill", "1.0.0")
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => built.Runtime.RunAsync(request).AsTask());

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await store.ReadRunAsync(request.Run.RunId, default));
    }

    [Fact]
    public async Task DirectDurableRunNeverRepairsHallucinatedToolWithSecondTurn()
    {
        var directory = TempDirectory();
        var provider = new ToolThenFinalProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .WithTools(new[] { SlowTool() })
                .Build();

            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("direct-hallucinated-tool"),
                    ExecutionMode = DurableExecutionModes.Direct
                });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(0, outcome.Run.Usage.Actions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurableRunPinsInferenceAndConfiguredModelRoute()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var first = new DirectInspectionProvider("first-model");
        var selected = new DirectInspectionProvider("quality-model");
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(path)
                .AddProvider(first)
                .AddProvider(selected)
                .Build();
            var route = new ProviderRoutePreference
            {
                ProviderIds = new[] { "quality-model" }
            };
            var request = new DurableRunRequest
            {
                Run = NewRun("durable-model-route"),
                ExecutionMode = DurableExecutionModes.Direct,
                Inference = new ModelInferenceOptions
                {
                    ReasoningEnabled = false,
                    Temperature = 0.35,
                    PromptCachingEnabled = true
                },
                RoutePreference = route
            };
            DurableExecutionPolicyBinding.Attach(
                request.Run,
                built.Runtime.CaptureExecutionPolicyIdentity(
                    request.ExecutionMode,
                    route));

            var outcome = await built.Runtime.RunAsync(request);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(0, first.CallCount);
            Assert.Equal(1, selected.CallCount);
            Assert.Equal(0.35, selected.Inference!.Temperature);
            Assert.True(selected.Inference.PromptCachingEnabled == true);
            var runInput = Assert.Single(
                await built.SessionStore.ReadRunAsync(
                    request.Run.RunId,
                    default),
                item => item.Kind == RuntimeEventKinds.RunInputCaptured);
            Assert.Equal(
                "quality-model",
                runInput.Payload.GetProperty("routePreference")
                    .GetProperty("providerIds")[0]
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownProviderRouteFailsBeforeDurableRunStart()
    {
        var directory = TempDirectory();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(new FinalProvider())
                .Build();
            var request = new DurableRunRequest
            {
                Run = NewRun("unknown-provider-route"),
                RoutePreference = new ProviderRoutePreference
                {
                    ProviderIds = new[] { "not-configured" }
                }
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => built.Runtime.RunAsync(request).AsTask());

            Assert.Equal(RunStates.Queued, request.Run.State);
            Assert.Empty(
                await built.SessionStore.ReadRunAsync(
                    request.Run.RunId,
                    default));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderUsesAndStopsCustomConversationContextEngine()
    {
        var engine = new RecordingContextEngine();
        var store = new ShutdownTrackingStore();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithConversationContextEngine(engine)
            .Build();

        var outcome = await built.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = NewRun("custom-context-engine"),
                ExecutionMode = DurableExecutionModes.Direct
            });
        await built.StopAsync();

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.True(engine.PrepareCount > 0);
        Assert.True(engine.CleanupCompleted);
    }

    [Fact]
    public async Task BuilderSurfacesIncompleteContextCleanupAndCanRetry()
    {
        var engine = new RetriableStopContextEngine(succeedOnAttempt: 6);
        var store = new ShutdownTrackingStore();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithConversationContextEngine(engine)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(built.Runtime.ShutdownResourceCleanupCompleted);
        Assert.False(store.WasDisposed);

        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(engine.CleanupCompleted);
        Assert.True(built.Runtime.ShutdownResourceCleanupCompleted);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public void BuilderRejectsCompactorAndCustomContextEngineCombination()
    {
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .WithConversationCompactor(
                new ExtractiveConversationCompactor());

        Assert.Throws<InvalidOperationException>(
            () => builder.WithConversationContextEngine(
                new RecordingContextEngine()));
    }

    [Fact]
    public async Task LifecycleMiddlewareObservesRunModelAndToolBoundaries()
    {
        var directory = TempDirectory();
        var middleware = new RecordingLifecycleMiddleware();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RecordingSucceededHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(new ToolThenFinalProvider())
                .WithTools(new[] { SlowTool() })
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();

            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("lifecycle-boundaries")
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(
                new[]
                {
                    AgentLifecycleEventKinds.RunStarting,
                    AgentLifecycleEventKinds.ModelDispatching,
                    AgentLifecycleEventKinds.ModelCompleted,
                    AgentLifecycleEventKinds.ToolBatchDispatching,
                    AgentLifecycleEventKinds.ToolBatchCompleted,
                    AgentLifecycleEventKinds.ModelDispatching,
                    AgentLifecycleEventKinds.ModelCompleted,
                    AgentLifecycleEventKinds.RunCompleted
                },
                middleware.Kinds);
            var tool = Assert.IsType<ToolBatchDispatchingLifecycleEvent>(
                middleware.Events.Single(
                    item => item.Kind
                            == AgentLifecycleEventKinds
                                .ToolBatchDispatching));
            Assert.Equal("slow_tool", Assert.Single(tool.Calls).ToolName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RequiredLifecycleMiddlewareCanRejectBeforeModelDispatch()
    {
        var directory = TempDirectory();
        var provider = new FinalProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(
                            new RejectingLifecycleMiddleware())
                    })
                .Build();

            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("lifecycle-rejection")
                });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(0, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ModelDispatchMiddlewareDoesNotHoldProviderAdmissionLease()
    {
        var directory = TempDirectory();
        var provider = new FinalProvider();
        var middleware = new FirstModelDispatchGate();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .WithRuntimeOptions(
                    new DurableAgentRuntimeOptions
                    {
                        MaxConcurrentProviderCalls = 1
                    })
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();
            var first = built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = NewRun("lifecycle-provider-lease-first"),
                        LaneId = "provider-lease-first"
                    })
                .AsTask();
            await middleware.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = NewRun("lifecycle-provider-lease-second"),
                        LaneId = "provider-lease-second"
                    })
                .AsTask();

            await WaitUntilAsync(
                () => provider.CallCount == 1,
                TimeSpan.FromSeconds(2));
            middleware.Release.TrySetResult(true);
            var outcomes = await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.All(
                outcomes,
                outcome => Assert.Equal(
                    RunStates.Completed,
                    outcome.Run.State));
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            middleware.Release.TrySetResult(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LifecyclePipelineSnapshotsMiddlewareIdentity()
    {
        var middleware = new MutableIdentityLifecycleMiddleware();
        using var pipeline = new AgentLifecyclePipeline(
            new[]
            {
                new AgentLifecycleMiddlewareRegistration(middleware)
            });
        middleware.MiddlewareId = "changed-after-registration";
        middleware.ThrowOnIdentityRead = true;

        var error = await Assert.ThrowsAsync<AgentLifecycleRejectedException>(
            () => pipeline.InvokeAsync(
                    new RunStartingLifecycleEvent(
                        "identity-snapshot-run",
                        agentId: null,
                        worldId: null,
                        sessionId: null,
                        isResume: false),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("original-lifecycle", error.MiddlewareId);
        Assert.True(await pipeline.StopAsync());
    }

    [Fact]
    public async Task LifecycleStopTracksMiddlewareAlreadyInFlight()
    {
        var middleware = new GatedLifecycleMiddleware();
        using var pipeline = new AgentLifecyclePipeline(
            new[]
            {
                new AgentLifecycleMiddlewareRegistration(middleware)
            },
            new AgentLifecyclePipelineOptions
            {
                MiddlewareTimeout = TimeSpan.FromSeconds(2),
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });
        var invocation = pipeline.InvokeAsync(
                new RunStartingLifecycleEvent(
                    "lifecycle-stop-race",
                    agentId: null,
                    worldId: null,
                    sessionId: null,
                    isResume: false),
                allowRejection: true,
                CancellationToken.None)
            .AsTask();
        await middleware.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await pipeline.StopAsync());
        Assert.False(invocation.IsCompleted);
        middleware.Release.TrySetResult();
        await invocation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunInputIsSnapshottedBeforeLifecycleMiddlewareAwaits()
    {
        var directory = TempDirectory();
        var middleware = new GatedRunStartingMiddleware(gateResume: false);
        var provider = new DirectInspectionProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();
            var request = new DurableRunRequest
            {
                Run = NewRun("input-snapshot-before-lifecycle"),
                ExecutionMode = DurableExecutionModes.Direct,
                Inference = new ModelInferenceOptions { Temperature = 0.1 }
            };

            var running = built.Runtime.RunAsync(request).AsTask();
            await middleware.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            request.Run.RunId = "mutated-run-id";
            request.Run.AgentId = "mutated-agent";
            request.Inference.Temperature = 0.9;
            middleware.Release.TrySetResult(true);
            var outcome = await running;

            Assert.Equal("input-snapshot-before-lifecycle", outcome.Run.RunId);
            Assert.Equal("agent-1", outcome.Run.AgentId);
            Assert.Equal(0.1, provider.Inference!.Temperature);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeGuardIsSnapshottedBeforeLifecycleMiddlewareAwaits()
    {
        var directory = TempDirectory();
        var middleware = new GatedRunStartingMiddleware(gateResume: true);
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(new FinalProvider())
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();
            var initial = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("resume-input-snapshot")
                });
            Assert.Equal(RunStates.Completed, initial.Run.State);
            var guard = new DurableRunResumeGuard
            {
                ExpectedAgentId = "agent-1"
            };

            var resuming = built.Runtime.ResumeAsync(
                    initial.Run.RunId,
                    guard: guard)
                .AsTask();
            await middleware.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            guard.ExpectedAgentId = "mutated-agent";
            middleware.Release.TrySetResult(true);
            var resumed = await resuming;

            Assert.Equal(RunStates.Completed, resumed.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeLifecycleReceivesRecoveredRunIdentityAndGameContext()
    {
        var directory = TempDirectory();
        var middleware = new RecordingLifecycleMiddleware();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(new FinalProvider())
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();
            var run = NewRun("resume-lifecycle-identity");
            run.SessionId = "session-resume";
            GameContextEnvelope.Attach(
                run,
                new GameContextCoordinate(
                    "world-1",
                    "timeline-main",
                    saveRevision: 7,
                    observer: new GameEntityIdentity("agent-1", 3)));
            var initial = await built.Runtime.RunAsync(
                new DurableRunRequest { Run = run });

            _ = await built.Runtime.ResumeAsync(initial.Run.RunId);

            var resumed = Assert.Single(
                middleware.Events
                    .OfType<RunStartingLifecycleEvent>(),
                item => item.IsResume);
            Assert.Equal("agent-1", resumed.AgentId);
            Assert.Equal("world-1", resumed.WorldId);
            Assert.Equal("session-resume", resumed.SessionId);
            Assert.NotNull(resumed.GameContext);
            Assert.Equal("timeline-main", resumed.GameContext!.TimelineId);
            Assert.Equal(7, resumed.GameContext.SaveRevision);
            Assert.Equal("agent-1", resumed.GameContext.Observer!.EntityId);
            Assert.Equal(3, resumed.GameContext.Observer.Incarnation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunLifecycleKeepsSameAgentWorldSessionsDistinct()
    {
        var directory = TempDirectory();
        var middleware = new RecordingLifecycleMiddleware();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(new FinalProvider())
                .WithLifecycleMiddleware(
                    new[]
                    {
                        new AgentLifecycleMiddlewareRegistration(middleware)
                    })
                .Build();
            foreach (var sessionId in new[] { "save-a", "save-b" })
            {
                var run = NewRun("session-lifecycle-" + sessionId);
                run.SessionId = sessionId;
                _ = await built.Runtime.RunAsync(
                    new DurableRunRequest { Run = run });
            }

            var starts = middleware.Events
                .OfType<RunStartingLifecycleEvent>()
                .Where(item => !item.IsResume)
                .ToArray();
            Assert.Equal(2, starts.Length);
            Assert.All(starts, item => Assert.Equal("agent-1", item.AgentId));
            Assert.All(starts, item => Assert.Equal("world-1", item.WorldId));
            Assert.Equal(
                new[] { "save-a", "save-b" },
                starts.Select(item => item.SessionId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ModelInferenceRejectsNonFiniteSamplingValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelInferenceOptions
            {
                Temperature = double.NaN
            }.CloneValidated());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelInferenceOptions
            {
                TopP = double.NaN
            }.CloneValidated());
    }

    [Fact]
    public void ModelInferenceRejectsContradictoryReasoningControls()
    {
        Assert.Throws<ArgumentException>(
            () => new ModelInferenceOptions
            {
                ReasoningEnabled = true,
                ReasoningEffort = ModelReasoningEfforts.None
            }.CloneValidated());
        Assert.Throws<ArgumentException>(
            () => new ModelInferenceOptions
            {
                ReasoningEffort = ModelReasoningEfforts.None,
                ReasoningTokenBudget = 2_048
            }.CloneValidated());
    }

    [Fact]
    public void BuilderRejectsNullRecoveryOptions()
    {
        var builder = new GameAgentRuntimeBuilder(new RejectingHost());

        Assert.Throws<ArgumentNullException>(
            () => builder.WithRecoveryOptions(null!));
    }

    [Fact]
    public void BuilderBoundsInfiniteCatalogEnumeration()
    {
        var builder = new GameAgentRuntimeBuilder(new RejectingHost());
        var tool = SlowTool();

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => builder.WithTools(InfiniteTools(tool)));

        Assert.Equal("tool_count_exceeded", error.LimitCode);
    }

    [Fact]
    public async Task BuilderUsesHostOwnedRegistriesWithoutReplacingThem()
    {
        var tools = new ToolCatalogRegistry();
        tools.Replace(new[] { SlowTool() });
        var skills = new SkillCatalogRegistry();
        skills.Replace(new[] { Skill("initial-skill") });
        var store = new ShutdownTrackingStore();

        await using var built = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithToolRegistry(tools)
            .WithSkillRegistry(skills)
            .Build();

        Assert.Same(tools, built.Tools);
        Assert.Same(skills, built.Skills);
        Assert.Single(built.Tools.Current.Tools);
        Assert.Equal(
            "initial-skill",
            Assert.Single(built.Skills.Current.Skills).SkillId);

        skills.Replace(new[] { Skill("reloaded-skill") });

        Assert.Equal(
            "reloaded-skill",
            Assert.Single(built.Skills.Current.Skills).SkillId);
    }

    [Fact]
    public async Task RuntimePolicyReloadAppliesOnlyToTheNextLoopInvocation()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var tools = new ToolCatalogRegistry();
        tools.Replace(new[] { SlowTool() });
        var skills = new SkillCatalogRegistry();
        skills.Replace(new[] { Skill("initial-skill") });
        var provider = new MidRunPolicyReloadProvider(
            "policy-run-1",
            tools,
            skills);
        var host = new RecordingSucceededHost();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(path)
                .AddProvider(provider)
                .WithToolRegistry(tools)
                .WithSkillRegistry(skills)
                .Build();

            var firstRequest = new DurableRunRequest
            {
                Run = NewRun("policy-run-1"),
                ActiveSkills = new[]
                {
                    new SkillReference("initial-skill", "1.0.0")
                }
            };
            DurableExecutionPolicyBinding.Attach(
                firstRequest.Run,
                built.Runtime.CaptureExecutionPolicyIdentity());
            var first = await built.Runtime.RunAsync(firstRequest);

            Assert.Equal(RunStates.Completed, first.Run.State);
            Assert.Equal(new[] { "slow_tool" }, host.ActionNames);
            var firstTurn = provider.Observation("policy-run-1", 1);
            var secondTurn = provider.Observation("policy-run-1", 2);
            Assert.Contains("slow_tool", firstTurn.ToolNames);
            Assert.Contains("slow_tool", secondTurn.ToolNames);
            Assert.DoesNotContain("reloaded_tool", secondTurn.ToolNames);
            Assert.True(firstTurn.HasInitialSkill);
            Assert.True(secondTurn.HasInitialSkill);
            Assert.False(secondTurn.HasReloadedSkill);

            var secondRequest = new DurableRunRequest
            {
                Run = NewRun("policy-run-2"),
                ActiveSkills = new[]
                {
                    new SkillReference("reloaded-skill", "1.0.0")
                }
            };
            DurableExecutionPolicyBinding.Attach(
                secondRequest.Run,
                built.Runtime.CaptureExecutionPolicyIdentity());
            var second = await built.Runtime.RunAsync(secondRequest);

            Assert.Equal(RunStates.Completed, second.Run.State);
            var nextInvocation = provider.Observation("policy-run-2", 1);
            Assert.Contains("reloaded_tool", nextInvocation.ToolNames);
            Assert.True(nextInvocation.HasReloadedSkill);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutionPolicyBindingRejectsBeforeProviderOrToolDispatch()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var provider = new FinalProvider();
        var host = new RecordingSucceededHost();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(path)
                .AddProvider(provider)
                .Build();
            var current =
                built.Runtime.CaptureExecutionPolicyIdentity();
            var request = new DurableRunRequest
            {
                Run = NewRun("policy-binding-mismatch")
            };
            DurableExecutionPolicyBinding.Attach(
                request.Run,
                new DurableExecutionPolicyIdentity(
                    new string('a', 64),
                    current.SkillCatalogDigest,
                    current.ProviderPolicyDigest,
                    current.ModelPolicyDigest));

            await Assert.ThrowsAsync<DurableExecutionPolicyMismatchException>(
                () => built.Runtime.RunAsync(request).AsTask());
            Assert.Equal(0, provider.CallCount);
            Assert.Empty(host.ActionNames);
            Assert.Empty(
                await built.SessionStore.ReadRunAsync(
                    request.Run.RunId,
                    default));

            DurableExecutionPolicyBinding.Attach(request.Run, current);
            var retried = await built.Runtime.RunAsync(request);

            Assert.Equal(RunStates.Completed, retried.Run.State);
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutionPolicyIdentityUsesEffectiveConfiguredModelId()
    {
        var firstStore = new ShutdownTrackingStore();
        var secondStore = new ShutdownTrackingStore();
        await using var first = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .UseDurableStore(
                firstStore,
                firstStore,
                disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ModelId = "configured-model-a"
                })
            .Build();
        await using var second = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .UseDurableStore(
                secondStore,
                secondStore,
                disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ModelId = "configured-model-b"
                })
            .Build();

        var firstIdentity =
            first.Runtime.CaptureExecutionPolicyIdentity();
        var secondIdentity =
            second.Runtime.CaptureExecutionPolicyIdentity();

        Assert.Equal(
            firstIdentity.ProviderPolicyDigest,
            secondIdentity.ProviderPolicyDigest);
        Assert.NotEqual(
            firstIdentity.ModelPolicyDigest,
            secondIdentity.ModelPolicyDigest);
        Assert.False(firstIdentity.Matches(secondIdentity));
    }

    [Fact]
    public async Task ResumeCapturesOnePolicyLeaseAfterOperationReconciliation()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var skills = new SkillCatalogRegistry();
        skills.Replace(new[] { Skill("initial-skill") });
        try
        {
            await using (var first = new GameAgentRuntimeBuilder(
                             new UnknownToolHost())
                         .UseFileJournal(path)
                         .AddProvider(new SingleToolCallProvider())
                         .WithTools(new[] { SlowTool() })
                         .WithSkillRegistry(skills)
                         .Build())
            {
                var request = new DurableRunRequest
                {
                    Run = NewRun("resume-policy-lease"),
                    ActiveSkills = new[]
                    {
                        new SkillReference("initial-skill", "1.0.0")
                    }
                };

                var waiting = await first.Runtime.RunAsync(request);

                Assert.Equal(RunStates.Reconciling, waiting.Run.State);
            }

            var provider = new FinalProvider();
            await using var resumed = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(path)
                .AddProvider(provider)
                .WithTools(new[] { SlowTool() })
                .WithSkillRegistry(skills)
                .Build();
            var reconciler = new ReloadingSucceededOperationReconciler(
                skills);

            var outcome = await resumed.Runtime.ResumeAsync(
                "resume-policy-lease",
                new DurableRunContinuation
                {
                    ActiveSkills = new[]
                    {
                        new SkillReference("initial-skill", "1.0.0")
                    },
                    ReplaceActiveSkills = true
                },
                reconciler);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, reconciler.CallCount);
            Assert.Equal(1, provider.CallCount);
            Assert.Contains(
                "reloaded",
                Assert.Single(skills.Current.Skills).PromptFragments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalResumeDoesNotRequireObsoleteExecutionPolicy()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var skills = new SkillCatalogRegistry();
        skills.Replace(new[] { Skill("initial-skill") });
        var provider = new FinalProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(path)
                .AddProvider(provider)
                .WithSkillRegistry(skills)
                .Build();
            var request = new DurableRunRequest
            {
                Run = NewRun("terminal-policy-replay")
            };
            DurableExecutionPolicyBinding.Attach(
                request.Run,
                built.Runtime.CaptureExecutionPolicyIdentity());
            var completed = await built.Runtime.RunAsync(request);
            skills.Replace(
                new[]
                {
                    Skill("initial-skill", "reloaded")
                });

            var replayed = await built.Runtime.ResumeAsync(
                request.Run.RunId);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.Equal(RunStates.Completed, replayed.Run.State);
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeReconcilesPendingOperationBeforePolicyMismatch()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var skills = new SkillCatalogRegistry();
        skills.Replace(new[] { Skill("initial-skill") });
        var provider = new ToolThenFinalProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new UnknownToolHost())
                .UseFileJournal(path)
                .AddProvider(provider)
                .WithTools(new[] { SlowTool() })
                .WithSkillRegistry(skills)
                .Build();
            var request = new DurableRunRequest
            {
                Run = NewRun("reconcile-before-policy-check")
            };
            var originalPolicy =
                built.Runtime.CaptureExecutionPolicyIdentity();
            DurableExecutionPolicyBinding.Attach(
                request.Run,
                originalPolicy);
            var waiting = await built.Runtime.RunAsync(request);
            Assert.Equal(RunStates.Reconciling, waiting.Run.State);
            skills.Replace(
                new[]
                {
                    Skill("initial-skill", "reloaded")
                });
            var reconciler = new SucceededOperationReconciler();

            await Assert.ThrowsAsync<DurableExecutionPolicyMismatchException>(
                () => built.Runtime.ResumeAsync(
                        request.Run.RunId,
                        reconciler: reconciler)
                    .AsTask());

            Assert.Equal(1, reconciler.CallCount);
            Assert.Equal(1, provider.CallCount);

            skills.Replace(new[] { Skill("initial-skill") });
            Assert.True(
                originalPolicy.Matches(
                    built.Runtime.CaptureExecutionPolicyIdentity()));
            var retried = await built.Runtime.ResumeAsync(
                request.Run.RunId);

            Assert.Equal(RunStates.Completed, retried.Run.State);
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSkillReloadPublishesIntoTheRunningBuilderRegistry()
    {
        var directory = TempDirectory();
        var packageDirectory = Path.Combine(directory, "package");
        Directory.CreateDirectory(packageDirectory);
        var manifestPath = Path.Combine(packageDirectory, "skill.json");
        var registry = new SkillCatalogRegistry();
        var packages = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "game-skills",
                    directory,
                    SkillPackageSourceTrust.Trusted)
            });
        var store = new ShutdownTrackingStore();
        try
        {
            WriteSkill("first");
            var initial = packages.Reload();
            Assert.True(initial.Applied);

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseDurableStore(store, store, disposeOnShutdown: true)
                .AddProvider(new FinalProvider())
                .WithSkillRegistry(registry)
                .Build();

            Assert.Equal(
                "first",
                Assert.Single(built.Skills.Current.Skills).SkillId);

            WriteSkill("second");
            var reloaded = packages.Reload();

            Assert.True(reloaded.Applied);
            Assert.True(reloaded.Changed);
            Assert.Equal(
                "second",
                Assert.Single(built.Skills.Current.Skills).SkillId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        void WriteSkill(string skillId)
        {
            File.WriteAllText(
                manifestPath,
                ProtocolJson.Serialize(Skill(skillId)),
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
        }
    }

    [Fact]
    public async Task BuilderRejectsAmbiguousStaticAndHostOwnedCatalogs()
    {
        var staticSkills = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .WithSkills(Array.Empty<SkillManifest>());
        var externalSkills = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .WithSkillRegistry(new SkillCatalogRegistry());
        var staticTools = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .WithTools(Array.Empty<ToolDescriptor>());
        var externalTools = new GameAgentRuntimeBuilder(
                new RejectingHost())
            .WithToolRegistry(new ToolCatalogRegistry());

        Assert.Throws<InvalidOperationException>(
            () => staticSkills.WithSkillRegistry(
                new SkillCatalogRegistry()));
        Assert.Throws<InvalidOperationException>(
            () => externalSkills.WithSkills(
                Array.Empty<SkillManifest>()));
        Assert.Throws<InvalidOperationException>(
            () => staticTools.WithToolRegistry(
                new ToolCatalogRegistry()));
        Assert.Throws<InvalidOperationException>(
            () => externalTools.WithTools(
                Array.Empty<ToolDescriptor>()));

        await staticSkills.DisposeAsync();
        await externalSkills.DisposeAsync();
        await staticTools.DisposeAsync();
        await externalTools.DisposeAsync();
    }

    private static IEnumerable<ToolDescriptor> InfiniteTools(
        ToolDescriptor tool)
    {
        while (true)
        {
            yield return tool;
        }
    }

    [Fact]
    public async Task BuilderCreatesRunnableOwnedRuntimeAndReleasesJournal()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            await using (var built = new GameAgentRuntimeBuilder(
                                 new RejectingHost())
                             .UseFileJournal(path)
                             .AddProvider(new FinalProvider())
                             .Build())
            {
                var now = DateTimeOffset.UtcNow;
                var outcome = await built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = new AgentRun
                        {
                            RunId = "builder-run",
                            AgentId = "agent-1",
                            WorldId = "world-1",
                            State = RunStates.Queued,
                            CreatedAt = now,
                            UpdatedAt = now
                        }
                    });

                Assert.Equal(RunStates.Completed, outcome.Run.State);
                Assert.Equal("ok", outcome.FinalOutput!.Value.GetString());
            }

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderInjectsCustomSkillAdmissionPolicy()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var policy = new DenyingSkillPolicy();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(path)
                .AddProvider(new FinalProvider())
                .WithSkills(
                    new[]
                    {
                        new SkillManifest
                        {
                            SkillId = "builder-skill",
                            Version = "1.0.0",
                            Digest = "declared:builder-skill",
                            Description = "Builder injection test.",
                            PromptFragments = new List<string>
                            {
                                "This prompt must not be disclosed."
                            },
                            CapabilityRequirements =
                                ProtocolJson.ParseElement("{}"),
                            ActivationPolicy =
                                ProtocolJson.ParseElement("{}"),
                            Trust = "trusted"
                        }
                    })
                .WithSkillAdmissionPolicy(policy)
                .Build();
            var now = DateTimeOffset.UtcNow;

            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "builder-skill-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    ActiveSkills = new[]
                    {
                        new SkillReference("builder-skill", "1.0.0")
                    }
                });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal("game_skill_denied", outcome.ErrorCode);
            Assert.Equal(1, policy.ActivationCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedBuildReleasesOwnedJournalAfterAsyncCleanup()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            var builder = new GameAgentRuntimeBuilder(new RejectingHost())
                .UseFileJournal(path);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
            await builder.DisposeAsync();

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
    public async Task BuilderCleanupNeverSynchronouslyWaitsForAsyncStore()
    {
        var store = new ShutdownTrackingStore(blockDispose: true);
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true);

        Assert.False((object)builder is IDisposable);
        var cleanup = builder.DisposeAsync().AsTask();
        await store.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(cleanup.IsCompleted);
        Assert.False(store.WasDisposed);

        store.ReleaseDispose();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task BuilderPublishesDisposeTaskBeforeReentrantStoreCleanup()
    {
        var store = new ShutdownTrackingStore();
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true);
        Task? reentrantWait = null;
        var callbacks = 0;
        store.DisposeCallback = () =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                reentrantWait = builder.DisposeAsync().AsTask();
            }
        };

        var dispose = builder.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(reentrantWait);
        Assert.Same(dispose, reentrantWait);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, store.DisposeCount);

        await builder.DisposeAsync();
        Assert.Equal(1, store.DisposeCount);
    }

    [Fact]
    public async Task ShutdownDisposesOwnedStoreWhenFlushFails()
    {
        var store = new ShutdownTrackingStore(
            flushException: new IOException("flush failed"));
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();

        var error = await Assert.ThrowsAsync<IOException>(
            () => built.DisposeAsync().AsTask());

        Assert.Equal("flush failed", error.Message);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task PreStoppedRuntimePublishesShutdownBeforeReentrantFlush()
    {
        var store = new ShutdownTrackingStore();
        var ownedTransport = new TrackingDisposable();
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider());
        var ownedDisposables = Assert.IsType<List<IDisposable>>(
            typeof(GameAgentRuntimeBuilder)
                .GetField(
                    "_ownedDisposables",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(builder));
        ownedDisposables.Add(ownedTransport);
        var built = builder.Build();
        await built.Runtime.WaitForShutdownDrainAsync();
        var reentrantCalls = 0;
        store.FlushCallback = () =>
        {
            Interlocked.Increment(ref reentrantCalls);
            _ = built.StopAsync();
        };

        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, reentrantCalls);
        Assert.Equal(1, store.FlushCount);
        Assert.Equal(1, ownedTransport.DisposeCount);
        Assert.Equal(1, store.DisposeCount);

        await built.DisposeAsync();
        Assert.Equal(1, store.FlushCount);
        Assert.Equal(1, ownedTransport.DisposeCount);
        Assert.Equal(1, store.DisposeCount);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotAbortSharedCleanup()
    {
        var store = new ShutdownTrackingStore(blockFlush: true);
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => built.StopAsync(cancellation.Token).AsTask());

        Assert.False(store.WasDisposed);
        store.ReleaseFlush();
        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task CancelledShutdownWaitCanReplaySharedCleanupFailure()
    {
        var store = new ShutdownTrackingStore(
            flushException: new IOException("late flush failure"),
            blockFlush: true);
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();
        using var cancellation = new CancellationTokenSource();

        var cancelledWait = built.StopAsync(cancellation.Token).AsTask();
        await store.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledWait);

        Assert.False(store.WasDisposed);
        store.ReleaseFlush();
        var replayed = await Assert.ThrowsAsync<IOException>(
            () => built.StopAsync().AsTask());

        Assert.Equal("late flush failure", replayed.Message);
        Assert.Equal(1, store.FlushCount);
        Assert.Equal(1, store.DisposeCount);

        var repeated = await Assert.ThrowsAsync<IOException>(
            () => built.DisposeAsync().AsTask());
        Assert.Equal("late flush failure", repeated.Message);
        Assert.Equal(1, store.FlushCount);
        Assert.Equal(1, store.DisposeCount);
    }

    [Fact]
    public async Task ShutdownAdmissionRejectionKeepsOwnedResourcesRetriable()
    {
        var store = new ShutdownTrackingStore();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        Assert.True(dispatcher.TryReserve(out var reservation));
        var field = typeof(DurableAgentRuntime).GetField(
            "_shutdownCancellationDispatcher",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(built.Runtime, dispatcher);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => built.StopAsync().AsTask());
            Assert.False(store.WasDisposed);

            var now = DateTimeOffset.UtcNow;
            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "run-after-rejected-stop",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                });
            Assert.Equal(RunStates.Completed, outcome.Run.State);
        }
        finally
        {
            reservation!.Dispose();
        }

        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task OwnedMemoryAdmissionRejectionDefersDownstreamDisposal()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        Assert.True(dispatcher.TryReserve(out var occupied));
        var memory = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>(),
            writeStore: null,
            options: new MemoryLifecycleOptions(),
            dispatcher);
        var store = new ShutdownTrackingStore();
        var ownedTransport = new TrackingDisposable();
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithRuntimeMemory(
                memory,
                new NoOpMemoryPolicy(),
                disposeOnShutdown: true);
        var ownedDisposables = Assert.IsType<List<IDisposable>>(
            typeof(GameAgentRuntimeBuilder)
                .GetField(
                    "_ownedDisposables",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(builder));
        ownedDisposables.Add(ownedTransport);
        var built = builder.Build();
        try
        {
            var rejection = await Assert.ThrowsAsync<InvalidOperationException>(
                () => built.StopAsync().AsTask());

            Assert.Contains(
                "capacity",
                rejection.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, ownedTransport.DisposeCount);
            Assert.Equal(0, store.DisposeCount);
            Assert.False(memory.ShutdownResourceCleanupCompleted);

            occupied!.Dispose();
            occupied = null;
            await built.StopAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(memory.ShutdownResourceCleanupCompleted);
            Assert.True(built.MemoryProviderCallsDrainedOnStop);
            Assert.Equal(1, ownedTransport.DisposeCount);
            Assert.Equal(1, store.DisposeCount);

            await built.DisposeAsync();
            Assert.Equal(1, ownedTransport.DisposeCount);
            Assert.Equal(1, store.DisposeCount);
        }
        finally
        {
            occupied?.Dispose();
            if (!memory.ShutdownResourceCleanupCompleted)
            {
                await built.StopAsync();
            }
        }
    }

    [Fact]
    public async Task BuilderDisposeDrainsOwnedMemoryBeforeOtherDependencies()
    {
        var provider = new NonCooperativeMemoryProvider();
        var memory = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromMilliseconds(30)
            });
        var recall = memory.RecallAsync(
                new MemoryQuery(
                    "agent:builder-dispose",
                    ProtocolJson.ParseElement("{}")))
            .AsTask();
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var recallReport = await recall.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recallReport.IsPartial);

        var transportObservedMemoryDrain = false;
        var transport = new TrackingDisposable(
            () => transportObservedMemoryDrain =
                memory.ShutdownResourceCleanupCompleted);
        var store = new ShutdownTrackingStore();
        var storeObservedMemoryDrain = false;
        var storeObservedTransportDisposal = false;
        store.DisposeCallback = () =>
        {
            storeObservedMemoryDrain =
                memory.ShutdownResourceCleanupCompleted;
            storeObservedTransportDisposal = transport.DisposeCount == 1;
        };
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .WithRuntimeMemory(
                memory,
                new NoOpMemoryPolicy(),
                disposeOnShutdown: true);
        var ownedDisposables = Assert.IsType<List<IDisposable>>(
            typeof(GameAgentRuntimeBuilder)
                .GetField(
                    "_ownedDisposables",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(builder));
        ownedDisposables.Add(transport);

        var dispose = builder.DisposeAsync().AsTask();
        bool completedBeforeRelease;
        bool memoryCleanedBeforeRelease;
        int transportDisposalsBeforeRelease;
        int storeDisposalsBeforeRelease;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            completedBeforeRelease = dispose.IsCompleted;
            memoryCleanedBeforeRelease =
                memory.ShutdownResourceCleanupCompleted;
            transportDisposalsBeforeRelease = transport.DisposeCount;
            storeDisposalsBeforeRelease = store.DisposeCount;
        }
        finally
        {
            provider.Release();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(completedBeforeRelease);
        Assert.False(memoryCleanedBeforeRelease);
        Assert.Equal(0, transportDisposalsBeforeRelease);
        Assert.Equal(0, storeDisposalsBeforeRelease);
        Assert.True(memory.ShutdownResourceCleanupCompleted);
        Assert.True(transportObservedMemoryDrain);
        Assert.True(storeObservedMemoryDrain);
        Assert.True(storeObservedTransportDisposal);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, store.DisposeCount);

        await builder.DisposeAsync();
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, store.DisposeCount);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsActiveRunBeforeDisposingStore()
    {
        var store = new ShutdownTrackingStore();
        var provider = new CancellableProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .Build();
        var now = DateTimeOffset.UtcNow;
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "shutdown-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                })
            .AsTask();
        var startedOrCompleted = await Task.WhenAny(
            provider.Started.Task,
            run,
            Task.Delay(TimeSpan.FromSeconds(2)));
        if (ReferenceEquals(startedOrCompleted, run))
        {
            var premature = await run;
            throw new Xunit.Sdk.XunitException(
                $"Run ended before the provider started: "
                + $"state={premature.Run.State}, "
                + $"code={premature.ErrorCode}, "
                + $"message={premature.SafeErrorMessage}");
        }

        Assert.Same(provider.Started.Task, startedOrCompleted);

        var stop = built.StopAsync().AsTask();
        await provider.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        provider.Release.TrySetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        Assert.True(store.RunCancellationCommitted);
        Assert.False(store.DisposedBeforeRunCancellation);
        Assert.True(store.WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = new AgentRun
                        {
                            RunId = "late-run",
                            AgentId = "agent-1",
                            WorldId = "world-1",
                            State = RunStates.Queued,
                            CreatedAt = now,
                            UpdatedAt = now
                        }
                    })
                .AsTask());
    }

    [Fact]
    public async Task ShutdownDrainsActiveCompletionBeforeDisposingStore()
    {
        var store = new ShutdownTrackingStore();
        var provider = new CancellableProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .Build();
        var completion = built.Completion.CompleteAsync(
                new SimpleCompletionRequest
                {
                    OperationId = "shutdown-completion",
                    Messages = new[] { UserMessage("completion-message") }
                })
            .AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        await provider.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await Task.Delay(80);
        Assert.False(stop.IsCompleted);
        Assert.False(store.WasDisposed);

        provider.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownDrainsActiveChildBeforeDisposingStore()
    {
        var store = new ShutdownTrackingStore();
        var provider = new CancellableProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .Build();
        var child = built.Children.RunChildAsync(
                "root-parent",
                new DurableRunRequest
                {
                    Run = NewRun("shutdown-child")
                })
            .AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        await provider.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.False(store.WasDisposed);

        provider.Release.TrySetResult();
        _ = await child.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.RunCancellationCommitted);
        Assert.False(store.DisposedBeforeRunCancellation);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownDefersOwnedCleanupForDetachedRoutePolicy()
    {
        var store = new ShutdownTrackingStore();
        var policy = new IgnoringRoutePolicy();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithExecutionRoutePolicy(
                policy,
                new ExecutionRouterOptions
                {
                    PolicyTimeout = TimeSpan.FromMilliseconds(20),
                    ShutdownTimeout = TimeSpan.FromMilliseconds(20),
                    MaxConcurrentPolicyCalls = 1
                })
            .Build();

        var routed = await built.Execution.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = new DurableRunRequest
                {
                    Run = NewRun("detached-route-policy")
                }
            });
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyTimeoutFallback,
            routed.Decision.ReasonCode);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => built.StopAsync().AsTask());
        Assert.False(store.WasDisposed);

        policy.Release.TrySetResult();
        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownTimeoutDefersOwnedCleanupUntilActiveLeaseDrains()
    {
        var store = new ShutdownTrackingStore();
        var memory = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>());
        var memoryPolicy = new BlockingMemoryPolicy();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ShutdownDrainTimeout =
                        TimeSpan.FromMilliseconds(20)
                })
            .WithRuntimeMemory(
                memory,
                memoryPolicy,
                disposeOnShutdown: true)
            .Build();
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("shutdown-timeout-active-lease")
                })
            .AsTask();
        await memoryPolicy.SelectionEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        await WaitUntilAsync(
            () => built.Runtime.ActiveRunsDrainedOnStop.HasValue,
            TimeSpan.FromSeconds(2));

        Assert.False(built.Runtime.ActiveRunsDrainedOnStop);
        Assert.False(stop.IsCompleted);
        Assert.False(
            built.Runtime.ConversationContextCleanupCompleted);
        Assert.False(store.WasDisposed);
        Assert.Equal(0, store.DisposeCount);
        _ = await memory.RecallAsync(
            new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}")),
            CancellationToken.None);

        memoryPolicy.Release.TrySetResult();
        _ = await run.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(
            built.Runtime.ConversationContextCleanupCompleted);
        Assert.True(built.Runtime.ShutdownResourceCleanupCompleted);
        Assert.True(store.WasDisposed);
        Assert.Equal(1, store.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => memory.RecallAsync(
                    new MemoryQuery(
                        "agent:agent-1",
                        ProtocolJson.ParseElement("{}")),
                    CancellationToken.None)
                .AsTask());

        await built.DisposeAsync();
        Assert.Equal(1, store.DisposeCount);
    }

    [Fact]
    public async Task ShutdownWaitsForBoundedDetachedToolDrainBeforeDisposal()
    {
        var directory = TempDirectory();
        var store = new DetachedDrainTrackingStore(
            Path.Combine(directory, "runtime.journal"));
        var host = new BlockingToolHost();
        var built = new GameAgentRuntimeBuilder(host)
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new SingleToolCallProvider())
            .WithTools(new[] { SlowTool() })
            .WithSchedulerLimits(
                new ToolSchedulerLimits(
                    detachedShutdownDrainTimeoutMs: 2_000))
            .Build();
        try
        {
            var run = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("detached-drain-run")
                });
            Assert.Equal(RunStates.Reconciling, run.Run.State);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);
            Assert.Single(
                built.Runtime.GetDetachedToolExecutionSnapshot());

            var stop = built.StopAsync().AsTask();

            Assert.False(stop.IsCompleted);
            Assert.False(store.WasDisposed);
            Assert.Null(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => built.Runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = NewRun("blocked-after-stop")
                        })
                    .AsTask());

            host.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            Assert.Equal(0, built.Runtime.DetachedToolExecutionCount);
            Assert.True(store.WasDisposed);
        }
        finally
        {
            host.Release.TrySetResult();
            await built.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownTimeoutDoesNotWaitForeverForDetachedToolExecution()
    {
        var directory = TempDirectory();
        var store = new DetachedDrainTrackingStore(
            Path.Combine(directory, "runtime.journal"));
        var host = new BlockingToolHost();
        var built = new GameAgentRuntimeBuilder(host)
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new SingleToolCallProvider())
            .WithTools(new[] { SlowTool() })
            .WithSchedulerLimits(
                new ToolSchedulerLimits(
                    detachedShutdownDrainTimeoutMs: 25))
            .Build();
        try
        {
            var run = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("detached-timeout-run")
                });
            Assert.Equal(RunStates.Reconciling, run.Run.State);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);

            await built.StopAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);
            Assert.True(store.WasDisposed);
        }
        finally
        {
            host.Release.TrySetResult();
            await WaitUntilAsync(
                () => built.Runtime.DetachedToolExecutionCount == 0,
                TimeSpan.FromSeconds(2));
            await built.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DetachedProviderCleanupDelaysOwnedResourceDisposal()
    {
        var store = new ShutdownTrackingStore();
        var provider = new ThrowingCancellationProvider();
        var ownedTransport = new TrackingDisposable();
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ShutdownDrainTimeout =
                        TimeSpan.FromMilliseconds(25)
                });
        var ownedDisposables = Assert.IsType<List<IDisposable>>(
            typeof(GameAgentRuntimeBuilder)
                .GetField(
                    "_ownedDisposables",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(builder));
        ownedDisposables.Add(ownedTransport);
        var built = builder.Build();
        var now = DateTimeOffset.UtcNow;
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "throwing-callback-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                })
            .AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        await provider.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        await WaitUntilAsync(
            () => built.Runtime.DetachedProviderCleanupCount == 1,
            TimeSpan.FromSeconds(2));
        await built.Runtime.StopAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => built.Runtime.DetachedProviderCleanupsDrainedOnStop.HasValue,
            TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        Assert.False(store.WasDisposed);
        Assert.Equal(0, ownedTransport.DisposeCount);
        Assert.False(provider.CleanupCompleted.Task.IsCompleted);
        Assert.False(
            built.Runtime.DetachedProviderCleanupsDrainedOnStop);

        provider.Release.TrySetResult();
        await provider.CleanupCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(store.WasDisposed);
        Assert.Equal(1, ownedTransport.DisposeCount);
        Assert.Equal(0, built.Runtime.DetachedProviderCleanupCount);
        Assert.Equal(
            1,
            built.Runtime.DetachedProviderCleanupCompletedCount);
        Assert.False(
            built.Runtime.DetachedProviderCleanupsDrainedOnStop);
        Assert.True(built.Runtime.ShutdownResourceCleanupCompleted);
    }

    [Fact]
    public async Task FaultedDetachedProviderCleanupIsCountedAndDrained()
    {
        var store = new ShutdownTrackingStore();
        var provider = new FaultingDetachedCleanupProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .WithRetryPolicy(
                new ProviderRetryPolicy
                {
                    CleanupTimeout = TimeSpan.FromMilliseconds(20)
                })
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ShutdownDrainTimeout =
                        TimeSpan.FromMilliseconds(25)
                })
            .Build();
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("faulted-detached-provider-cleanup")
                })
            .AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        await WaitUntilAsync(
            () => built.Runtime.DetachedProviderCleanupCount == 1,
            TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => built.Runtime.DetachedProviderCleanupsDrainedOnStop
                  == false,
            TimeSpan.FromSeconds(2));

        Assert.False(stop.IsCompleted);
        Assert.False(store.WasDisposed);

        provider.Release.TrySetResult();
        await provider.CleanupAttempted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, built.Runtime.DetachedProviderCleanupCount);
        Assert.Equal(
            1,
            built.Runtime.DetachedProviderCleanupCompletedCount);
        Assert.Equal(
            1,
            built.Runtime.DetachedProviderCleanupFailureCount);
        Assert.True(built.Runtime.ShutdownResourceCleanupCompleted);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownDiscardsBufferedNotificationsWithoutOwningObserver()
    {
        var store = new ShutdownTrackingStore();
        var publisher = new BlockingDisposablePublisher();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store)
            .PublishEventsTo(publisher)
            .AddProvider(new FinalProvider())
            .Build();
        var now = DateTimeOffset.UtcNow;

        var outcome = await built.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "buffered-publisher-run",
                    AgentId = "agent-1",
                    WorldId = "world-1",
                    State = RunStates.Queued,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            });
        Assert.Equal(RunStates.Completed, outcome.Run.State);
        await publisher.FirstPublishEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, publisher.DisposeCount);

        publisher.ReleaseFirstPublish();
        await publisher.FirstPublishReturned.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, publisher.PublishCount);
        Assert.Equal(0, publisher.DisposeCount);
        publisher.Dispose();
    }

    [Fact]
    public async Task MultiActorParticipantResumesAfterJournalAndRuntimeReopen()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        const string batchId = "persisted-multi-actor";
        var firstLifecycle = new RecoveryLifecycle();
        MultiActorBatchParticipant participant;
        try
        {
            await using (var first = new GameAgentRuntimeBuilder(
                                     new UnknownToolHost())
                                 .UseFileJournal(path)
                                 .AddProvider(new SingleToolCallProvider())
                                 .WithTools(new[] { SlowTool() })
                                 .Build())
            {
                var request = new DurableRunRequest
                {
                    Run = NewRun("persisted-participant")
                };
                request.Run.DecisionKey = "persisted decision 决策";
                var coordinator = new MultiActorDecisionCoordinator(
                    first.Runtime,
                    lifecycle: firstLifecycle);
                var outcome = await coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        batchId,
                        new GameContextCoordinate(
                            "world-1",
                            "prime",
                            saveRevision: 1,
                            stateVersion: "state-1"),
                        new[] { request }));

                Assert.Equal(
                    RunStates.Reconciling,
                    Assert.Single(outcome.Results).Outcome!.Run.State);
                participant = Assert.Single(
                    outcome.Manifest.Participants);
                Assert.Equal(
                    participant.RunId,
                    Assert.Single(
                        firstLifecycle.Manifest!.Participants).RunId);
            }

            var checkpoint = new MultiActorBatchParticipant(
                participant.InputIndex,
                participant.AgentId,
                participant.RunId,
                participant.DecisionKey);
            var recoveredLifecycle = new RecoveryLifecycle();
            await using (var recovered = new GameAgentRuntimeBuilder(
                                           new RejectingHost())
                                       .UseFileJournal(path)
                                       .AddProvider(new FinalProvider())
                                       .WithTools(new[] { SlowTool() })
                                       .Build())
            {
                var coordinator = new MultiActorDecisionCoordinator(
                    recovered.Runtime,
                    lifecycle: recoveredLifecycle);
                var reconciler = new SucceededOperationReconciler();
                var forged = new MultiActorBatchParticipant(
                    checkpoint.InputIndex,
                    checkpoint.AgentId,
                    checkpoint.RunId,
                    "forged decision");
                var guardError = await Assert.ThrowsAsync<
                    DurableRunResumeGuardException>(
                    () => coordinator.ResumeParticipantAsync(
                            batchId,
                            forged,
                            reconciler: reconciler)
                        .AsTask());
                Assert.Equal(
                    DurableRunResumeGuardReasonCodes.DecisionKeyMismatch,
                    guardError.ReasonCode);
                Assert.Equal(0, reconciler.CallCount);
                Assert.Empty(recoveredLifecycle.Finished);

                var outcome = await coordinator.ResumeParticipantAsync(
                    batchId,
                    checkpoint,
                    reconciler: reconciler);

                Assert.True(outcome.Outcome!.IsTerminal);
                Assert.Equal(1, reconciler.CallCount);
                Assert.Equal(checkpoint.InputIndex, outcome.InputIndex);
                Assert.Equal(checkpoint.AgentId, outcome.AgentId);
                Assert.Equal(checkpoint.DecisionKey, outcome.DecisionKey);
                Assert.Equal(
                    checkpoint.RunId,
                    Assert.Single(recoveredLifecycle.Finished).Outcome!
                        .Run.RunId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MultiActorAbandonDurablyCancelsAfterJournalReopen()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        const string batchId = "abandon-persisted-multi-actor";
        try
        {
            MultiActorBatchParticipant participant;
            await using (var first = new GameAgentRuntimeBuilder(
                                     new UnknownToolHost())
                                 .UseFileJournal(path)
                                 .AddProvider(new SingleToolCallProvider())
                                 .WithTools(new[] { SlowTool() })
                                 .Build())
            {
                var request = new DurableRunRequest
                {
                    Run = NewRun("abandoned-persisted-participant")
                };
                request.Run.DecisionKey = "abandon decision";
                var outcome = await new MultiActorDecisionCoordinator(
                        first.Runtime)
                    .RunAsync(
                        new MultiActorDecisionBatch(
                            batchId,
                            new GameContextCoordinate(
                                "world-1",
                                "prime",
                                saveRevision: 1,
                                stateVersion: "state-1"),
                            new[] { request }));
                Assert.Equal(
                    RunStates.Reconciling,
                    Assert.Single(outcome.Results).Outcome!.Run.State);
                participant = Assert.Single(
                    outcome.Manifest.Participants);
            }

            var lifecycle = new RecoveryLifecycle();
            var provider = new FinalProvider();
            await using (var recovered = new GameAgentRuntimeBuilder(
                                           new RejectingHost())
                                       .UseFileJournal(path)
                                       .AddProvider(provider)
                                       .WithTools(new[] { SlowTool() })
                                       .Build())
            {
                var reconciler = new SucceededOperationReconciler();
                var abandoned = await new MultiActorDecisionCoordinator(
                        recovered.Runtime,
                        lifecycle: lifecycle)
                    .ReconcileAbandonedParticipantAsync(
                        batchId,
                        participant,
                        "actor_removed",
                        reconciler);

                Assert.Equal(
                    RunStates.Cancelled,
                    abandoned.Outcome!.Run.State);
                Assert.Empty(abandoned.Outcome.Run.PendingOperationIds);
                Assert.IsType<MultiActorParticipantAbandonedException>(
                    abandoned.Error);
                Assert.Equal(1, reconciler.CallCount);
                Assert.Equal(0, provider.CallCount);
                Assert.Equal(
                    participant.RunId,
                    Assert.Single(lifecycle.Finished).Outcome!.Run.RunId);
            }

            var replayProvider = new FinalProvider();
            await using var replay = new GameAgentRuntimeBuilder(
                                         new RejectingHost())
                                     .UseFileJournal(path)
                                     .AddProvider(replayProvider)
                                     .WithTools(new[] { SlowTool() })
                                     .Build();
            var durable = await replay.Runtime.ResumeAsync(
                participant.RunId);
            Assert.Equal(RunStates.Cancelled, durable.Run.State);
            Assert.Equal(0, replayProvider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-builder-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AgentRun NewRun(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRun
        {
            RunId = runId,
            AgentId = "agent-1",
            WorldId = "world-1",
            State = RunStates.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static NormalizedMessage UserMessage(string messageId)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UtcNow,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("Classify this game event.")
            }
        };
    }

    private static ToolDescriptor SlowTool()
    {
        return Tool("slow_tool");
    }

    private static ToolDescriptor Tool(string name)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = "A controlled shutdown test tool.",
            ParametersSchema = ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "world" },
            IdempotencyPolicy = ToolIdempotencyPolicies.BestEffort,
            TimeoutMs = 1_000
        };
    }

    private static SkillManifest Skill(
        string skillId,
        string prompt = "Use the registered skill.")
    {
        return new SkillManifest
        {
            SkillId = skillId,
            Version = "1.0.0",
            Digest = "declared:" + skillId,
            Description = "Runtime builder registry test.",
            PromptFragments = new List<string> { prompt },
            CapabilityRequirements = ProtocolJson.ParseElement("{}"),
            ActivationPolicy = ProtocolJson.ParseElement("{}"),
            Trust = "trusted"
        };
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The expected shutdown state was not observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
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

    private sealed class RecordingSucceededHost : IGameHost
    {
        private readonly List<string> _actionNames = new();

        public IReadOnlyList<string> ActionNames
        {
            get
            {
                lock (_actionNames)
                {
                    return _actionNames.ToArray();
                }
            }
        }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_actionNames)
            {
                _actionNames.Add(request.ActionName);
            }

            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement("""{"ok":true}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }

    private sealed class MidRunPolicyReloadProvider :
        IStreamingModelProvider
    {
        private readonly string _reloadRunId;
        private readonly ToolCatalogRegistry _tools;
        private readonly SkillCatalogRegistry _skills;
        private readonly Dictionary<string, int> _calls =
            new(StringComparer.Ordinal);
        private readonly List<PolicyObservation> _observations = new();

        public MidRunPolicyReloadProvider(
            string reloadRunId,
            ToolCatalogRegistry tools,
            SkillCatalogRegistry skills)
        {
            _reloadRunId = reloadRunId;
            _tools = tools;
            _skills = skills;
        }

        public string ProviderId => "mid-run-policy-reload";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public PolicyObservation Observation(string runId, int call)
        {
            lock (_observations)
            {
                return Assert.Single(
                    _observations,
                    item => string.Equals(
                                item.RunId,
                                runId,
                                StringComparison.Ordinal)
                            && item.Call == call);
            }
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call;
            lock (_calls)
            {
                _calls.TryGetValue(request.RunId, out call);
                call++;
                _calls[request.RunId] = call;
            }

            var messagePayload = string.Join(
                "\n",
                request.Messages.SelectMany(
                    message => message.Parts.Select(
                        part => part.Text
                                ?? part.Json?.GetRawText()
                                ?? string.Empty)));
            lock (_observations)
            {
                _observations.Add(
                    new PolicyObservation(
                        request.RunId,
                        call,
                        request.Tools.Select(item => item.Name).ToArray(),
                        messagePayload.Contains(
                            "initial-skill",
                            StringComparison.Ordinal),
                        messagePayload.Contains(
                            "reloaded-skill",
                            StringComparison.Ordinal)));
            }

            var reloadRun = string.Equals(
                request.RunId,
                _reloadRunId,
                StringComparison.Ordinal);
            if (reloadRun && call == 1)
            {
                _tools.Replace(new[] { Tool("reloaded_tool") });
                _skills.Replace(new[] { Skill("reloaded-skill") });
                Capabilities.ToolCalling = false;
                yield return ToolCall(request, "initial-call", "slow_tool");
            }
            else if (reloadRun && call == 2)
            {
                yield return ToolCall(
                    request,
                    "reloaded-call",
                    "reloaded_tool");
            }
            else
            {
                Capabilities.ToolCalling = true;
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = """{"ok":true}"""
                };
            }

            await Task.Yield();
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
                FinishReason = reloadRun && call < 3
                    ? "tool_calls"
                    : "stop"
            };
        }

        private static ModelStreamEvent ToolCall(
            StreamingModelRequest request,
            string toolCallId,
            string toolName)
        {
            return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = toolCallId,
                ToolNameDelta = toolName,
                ArgumentsJsonDelta = "{}"
            };
        }
    }

    private sealed record PolicyObservation(
        string RunId,
        int Call,
        IReadOnlyList<string> ToolNames,
        bool HasInitialSkill,
        bool HasReloadedSkill);

    private sealed class UnknownToolHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Unknown,
                    ReceivedAt = DateTimeOffset.UtcNow
                });
        }
    }

    private sealed class SucceededOperationReconciler
        : IGameOperationReconciler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 2,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"recovered":true}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }

    private sealed class ReloadingSucceededOperationReconciler
        : IGameOperationReconciler
    {
        private readonly SkillCatalogRegistry _skills;
        private int _callCount;

        public ReloadingSucceededOperationReconciler(
            SkillCatalogRegistry skills)
        {
            _skills = skills;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            _skills.Replace(
                new[]
                {
                    Skill("initial-skill", "reloaded")
                });
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 2,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"recovered":true}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }

    private sealed class RecoveryLifecycle
        : IMultiActorDecisionLifecycle
    {
        public MultiActorBatchManifest? Manifest { get; private set; }

        public List<MultiActorRunResult> Finished { get; } = new();

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Manifest = manifest;
            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            cancellationToken.ThrowIfCancellationRequested();
            Finished.Add(result);
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    private sealed class DenyingSkillPolicy : ISkillAdmissionPolicy
    {
        private int _activationCalls;

        public string PolicyId => "builder-skill-policy";

        public string Version => "1.0.0";

        public int ActivationCalls => Volatile.Read(ref _activationCalls);

        public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
        {
            if (request.IsExplicitActivation)
            {
                Interlocked.Increment(ref _activationCalls);
            }

            return SkillAdmissionDecision.Deny("game_skill_denied");
        }
    }

    private sealed class FinalProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "test";

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
            Interlocked.Increment(ref _callCount);
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"ok\""
            };
            await Task.Yield();
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
        }
    }

    private sealed class DirectInspectionProvider : IStreamingModelProvider
    {
        private int _callCount;

        private readonly string _providerId;

        public DirectInspectionProvider(
            string providerId = "direct-inspection")
        {
            _providerId = providerId;
        }

        public string ProviderId => _providerId;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string> ToolNames { get; private set; } =
            Array.Empty<string>();

        public string Prompt { get; private set; } = string.Empty;

        public ModelInferenceOptions? Inference { get; private set; }

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
            Interlocked.Increment(ref _callCount);
            Inference = request.Inference?.CloneValidated();
            ToolNames = request.Tools.Select(item => item.Name).ToArray();
            Prompt = string.Join(
                "\n",
                request.Messages.SelectMany(
                    message => message.Parts.Select(
                        part => part.Text
                                ?? part.Json?.GetRawText()
                                ?? string.Empty)));
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "Direct reply"
            };
            await Task.Yield();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 1,
                    OutputTokens = 1,
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
        }
    }

    private sealed class RecordingRuntimeEventPublisher :
        INonBlockingRuntimeEventPublisher
    {
        private readonly List<RuntimeEvent> _events = new();

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

        public void Publish(RuntimeEvent runtimeEvent)
        {
            lock (_events)
            {
                _events.Add(runtimeEvent);
            }
        }
    }

    private sealed class RecordingContextEngine : IConversationContextEngine
    {
        private readonly ConversationContextManager _inner = new(
            new ConversationContextOptions(),
            new ExtractiveConversationCompactor(),
            new SystemRuntimeClock());
        private int _prepareCount;

        public string EngineId => "recording-context";

        public string Version => "1";

        public int PrepareCount => Volatile.Read(ref _prepareCount);

        public bool CleanupCompleted => _inner.CleanupCompleted;

        public async ValueTask<ConversationContextView> PrepareAsync(
            string runId,
            string turnId,
            IReadOnlyList<NormalizedMessage> transcript,
            IReadOnlyCollection<string>? stablePrefixMessageIds = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _prepareCount);
            return await _inner.PrepareAsync(
                runId,
                turnId,
                transcript,
                stablePrefixMessageIds,
                cancellationToken);
        }

        public void RegisterCheckpoint(JsonElement checkpoint) =>
            _inner.RegisterCheckpoint(checkpoint);

        public ValueTask<bool> StopAsync() => _inner.StopAsync();
    }

    private sealed class RetriableStopContextEngine :
        IConversationContextEngine
    {
        private readonly int _succeedOnAttempt;
        private int _attempts;

        internal RetriableStopContextEngine(int succeedOnAttempt)
        {
            _succeedOnAttempt = succeedOnAttempt;
        }

        public string EngineId => "retriable-stop-context";

        public string Version => "1";

        public bool CleanupCompleted { get; private set; }

        public ValueTask<ConversationContextView> PrepareAsync(
            string runId,
            string turnId,
            IReadOnlyList<NormalizedMessage> transcript,
            IReadOnlyCollection<string>? stablePrefixMessageIds = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No run expected.");
        }

        public void RegisterCheckpoint(JsonElement checkpoint)
        {
            _ = checkpoint;
        }

        public ValueTask<bool> StopAsync()
        {
            CleanupCompleted = Interlocked.Increment(ref _attempts)
                >= _succeedOnAttempt;
            return new ValueTask<bool>(CleanupCompleted);
        }
    }

    private sealed class RecordingLifecycleMiddleware :
        IAgentLifecycleMiddleware
    {
        private readonly List<AgentLifecycleEvent> _events = new();

        public string MiddlewareId => "recording-lifecycle";

        public string Version => "1";

        public IReadOnlyList<AgentLifecycleEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Kinds =>
            Events.Select(item => item.Kind).ToArray();

        public ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                _events.Add(lifecycleEvent);
            }

            return new ValueTask<AgentLifecycleDecision>(
                AgentLifecycleDecision.Continue);
        }
    }

    private sealed class RejectingLifecycleMiddleware :
        IAgentLifecycleMiddleware
    {
        public string MiddlewareId => "rejecting-lifecycle";

        public string Version => "1";

        public ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<AgentLifecycleDecision>(
                lifecycleEvent is ModelDispatchingLifecycleEvent
                    ? AgentLifecycleDecision.Reject(
                        "model_dispatch_denied")
                    : AgentLifecycleDecision.Continue);
        }
    }

    private sealed class MutableIdentityLifecycleMiddleware :
        IAgentLifecycleMiddleware
    {
        private string _middlewareId = "original-lifecycle";

        public bool ThrowOnIdentityRead { get; set; }

        public string MiddlewareId
        {
            get => ThrowOnIdentityRead
                ? throw new InvalidOperationException("identity was re-read")
                : _middlewareId;
            set => _middlewareId = value;
        }

        public string Version => ThrowOnIdentityRead
            ? throw new InvalidOperationException("version was re-read")
            : "1";

        public ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<AgentLifecycleDecision>(
                AgentLifecycleDecision.Reject("expected-rejection"));
        }
    }

    private sealed class GatedLifecycleMiddleware :
        IAgentLifecycleMiddleware
    {
        public string MiddlewareId => "gated-lifecycle";

        public string Version => "1";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            _ = lifecycleEvent;
            _ = cancellationToken;
            Entered.TrySetResult();
            await Release.Task;
            return AgentLifecycleDecision.Continue;
        }
    }

    private sealed class FirstModelDispatchGate :
        IAgentLifecycleMiddleware
    {
        private int _modelDispatches;

        public string MiddlewareId => "first-model-dispatch-gate";

        public string Version => "1";

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            if (lifecycleEvent is ModelDispatchingLifecycleEvent
                && Interlocked.Increment(ref _modelDispatches) == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return AgentLifecycleDecision.Continue;
        }
    }

    private sealed class GatedRunStartingMiddleware :
        IAgentLifecycleMiddleware
    {
        private readonly bool _gateResume;

        public GatedRunStartingMiddleware(bool gateResume)
        {
            _gateResume = gateResume;
        }

        public string MiddlewareId => _gateResume
            ? "gated-resume"
            : "gated-run";

        public string Version => "1";

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            if (lifecycleEvent is RunStartingLifecycleEvent starting
                && starting.IsResume == _gateResume)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return AgentLifecycleDecision.Continue;
        }
    }

    private sealed class SingleToolCallProvider : IStreamingModelProvider
    {
        public string ProviderId => "single-tool-call";

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
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = "slow-call",
                ToolNameDelta = "slow_tool",
                ArgumentsJsonDelta = "{}"
            };
            await Task.Yield();
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
        }
    }

    private sealed class ToolThenFinalProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "tool-then-final";

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
            if (call == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "slow-call",
                    ToolNameDelta = "slow_tool",
                    ArgumentsJsonDelta = "{}"
                };
            }
            else
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "\"ok\""
                };
            }

            await Task.Yield();
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
                FinishReason = call == 1 ? "tool_calls" : "stop"
            };
        }
    }

    private sealed class BlockingToolHost : IGameHost
    {
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            await Release.Task.ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement("""{"released":true}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
        }
    }

    private sealed class CancellableProvider : IStreamingModelProvider
    {
        public string ProviderId => "cancellable";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class IgnoringRoutePolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "ignoring-route";

        public string Version => "1";

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            await Release.Task;
            return new ExecutionRouteDecision(
                ExecutionPath.Direct,
                "released",
                PolicyId,
                Version);
        }
    }

    private sealed class ThrowingCancellationProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "throwing-cancellation";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            try
            {
                var cancellation = Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                using var registration = cancellationToken.Register(
                    () =>
                    {
                        CallbackInvoked.TrySetResult();
                        throw new InvalidOperationException(
                            "cancellation callback failed");
                    });
                Started.TrySetResult();
                try
                {
                    await cancellation;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    await Release.Task;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                CleanupCompleted.TrySetResult();
            }

            yield break;
        }
    }

    private sealed class FaultingDetachedCleanupProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "faulting-detached-cleanup";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return new FaultingCleanupStream(this);
        }

        private sealed class FaultingCleanupStream :
            IAsyncEnumerable<ModelStreamEvent>,
            IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly FaultingDetachedCleanupProvider _owner;

            public FaultingCleanupStream(
                FaultingDetachedCleanupProvider owner)
            {
                _owner = owner;
            }

            public ModelStreamEvent Current =>
                throw new InvalidOperationException(
                    "The controlled stream never yields an event.");

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                _ = cancellationToken;
                return this;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                _owner.Started.TrySetResult();
                await _owner.Release.Task.ConfigureAwait(false);
                return false;
            }

            public ValueTask DisposeAsync()
            {
                _owner.CleanupAttempted.TrySetResult();
                return ValueTask.FromException(
                    new InvalidOperationException(
                        "controlled detached cleanup failure"));
            }
        }
    }

    private sealed class BlockingDisposablePublisher :
        IRuntimeEventPublisher,
        IDisposable
    {
        private readonly TaskCompletionSource _releaseFirstPublish =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _publishCount;
        private int _disposeCount;

        public TaskCompletionSource FirstPublishEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstPublishReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PublishCount => Volatile.Read(ref _publishCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Publish(RuntimeEvent runtimeEvent)
        {
            _ = runtimeEvent;
            if (Interlocked.Increment(ref _publishCount) != 1)
            {
                return;
            }

            FirstPublishEntered.TrySetResult();
            _releaseFirstPublish.Task.GetAwaiter().GetResult();
            FirstPublishReturned.TrySetResult();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }

        public void ReleaseFirstPublish()
        {
            _releaseFirstPublish.TrySetResult();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private readonly Action? _onDispose;
        private int _disposeCount;

        public TrackingDisposable(Action? onDispose = null)
        {
            _onDispose = onDispose;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            _onDispose?.Invoke();
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class NoOpMemoryPolicy : IRuntimeMemoryPolicy
    {
        public string PolicyId => "no-op-shutdown-policy";

        public string Version => "1.0.0";

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            _ = context;
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            _ = context;
            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class BlockingMemoryPolicy : IRuntimeMemoryPolicy
    {
        public string PolicyId => "blocking-shutdown-policy";

        public string Version => "1.0.0";

        public TaskCompletionSource SelectionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            _ = context;
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            _ = context;
            SelectionEntered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class NonCooperativeMemoryProvider : IMemoryProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "non-cooperative-builder-memory";

        public Task Started => _started.Task;

        public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            _ = cancellationToken;
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return Array.Empty<MemorySearchResult>();
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class DetachedDrainTrackingStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;

        public DetachedDrainTrackingStore(string path)
        {
            _inner = new FileSessionStore(path);
        }

        public bool WasDisposed { get; private set; }

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

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            WasDisposed = true;
        }
    }

    private sealed class ShutdownTrackingStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly Exception? _flushException;
        private readonly bool _blockFlush;
        private readonly bool _blockDispose;
        private readonly TaskCompletionSource _flushReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _sequence = -1;
        private int _flushCount;

        public ShutdownTrackingStore(
            Exception? flushException = null,
            bool blockFlush = false,
            bool blockDispose = false)
        {
            _flushException = flushException;
            _blockFlush = blockFlush;
            _blockDispose = blockDispose;
        }

        public TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FlushEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? FlushCallback { get; set; }

        public Action? DisposeCallback { get; set; }

        public int FlushCount => Volatile.Read(ref _flushCount);

        public bool WasDisposed { get; private set; }

        public int DisposeCount { get; private set; }

        public bool RunCancellationCommitted { get; private set; }

        public bool DisposedBeforeRunCancellation { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken) =>
            default;

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            new(Array.Empty<RuntimeEvent>());

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackRunCancellation(runtimeEvent);
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    Interlocked.Increment(ref _sequence),
                    checked(expectedRunRevision.GetValueOrDefault() + 1),
                    false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var runtimeEvent in runtimeEvents)
            {
                TrackRunCancellation(runtimeEvent);
            }

            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            Interlocked.Increment(ref _sequence),
                            checked(
                                expectedRunRevision.GetValueOrDefault()
                                + index
                                + 1),
                            false))
                    .ToArray());
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            new(new RunJournalCursor(runId, 0, 0));

        public async ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _flushCount);
            FlushEntered.TrySetResult();
            FlushCallback?.Invoke();
            if (_blockFlush)
            {
                await _flushReleased.Task.WaitAsync(cancellationToken);
            }

            if (_flushException is not null)
            {
                throw _flushException;
            }
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default) =>
            new((OperationLedgerEntry?)null);

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default) =>
            new(Array.Empty<OperationLedgerEntry>());

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            DisposeCallback?.Invoke();
            DisposedBeforeRunCancellation = !RunCancellationCommitted;
            if (_blockDispose)
            {
                await _disposeReleased.Task.ConfigureAwait(false);
            }

            WasDisposed = true;
            DisposeCount++;
        }

        public void ReleaseFlush()
        {
            _flushReleased.TrySetResult();
        }

        public void ReleaseDispose()
        {
            _disposeReleased.TrySetResult();
        }

        private void TrackRunCancellation(RuntimeEvent runtimeEvent)
        {
            if (runtimeEvent.Kind == RuntimeEventKinds.RunCancelled)
            {
                RunCancellationCommitted = true;
            }
        }
    }
}
