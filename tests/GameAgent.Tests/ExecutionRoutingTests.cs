using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

[CollectionDefinition(
    ProcessCancellationWorkerPoolCollection.Name,
    DisableParallelization = true)]
public sealed class ProcessCancellationWorkerPoolCollection
{
    public const string Name = "Process cancellation worker pool";
}

[Collection(ProcessCancellationWorkerPoolCollection.Name)]
public sealed class ExecutionRoutingTests
{
    [Fact]
    public async Task CapabilityFreeRequestExecutesDurableDirectPath()
    {
        var agent = new RecordingAgentRuntime();
        var workflow = new RecordingWorkflowRuntime();
        var router = new RoutedExecutionRuntime(agent, workflow);

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    OperationKind = "npc-dialogue"
                },
                Run = RunRequest("route-direct")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Direct, outcome.Decision.Path);
        Assert.Equal(
            DurableExecutionModes.Direct,
            agent.LastRequest!.ExecutionMode);
        Assert.Equal(1, agent.CallCount);
        Assert.Equal(0, workflow.CallCount);
    }

    [Fact]
    public async Task ToolRequirementExecutesAgentPath()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(agent);

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    OperationKind = "npc-action",
                    Requirements = ExecutionRequirements.Tools
                                   | ExecutionRequirements.DurableEffects
                },
                Run = RunRequest("route-agent")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Agent, outcome.Decision.Path);
        Assert.Equal(
            DurableExecutionModes.Agent,
            agent.LastRequest!.ExecutionMode);
        Assert.Equal(1, agent.CallCount);
    }

    [Fact]
    public async Task WorkflowRequirementInvokesOnlyWorkflowRuntime()
    {
        var agent = new RecordingAgentRuntime();
        var workflow = new RecordingWorkflowRuntime();
        var router = new RoutedExecutionRuntime(agent, workflow);

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    OperationKind = "month-evolution",
                    Requirements = ExecutionRequirements.Workflow
                },
                Workflow = new RoutedWorkflowRequest
                {
                    WorkflowId = "month",
                    RunKey = "month-17",
                    OwnerId = "save-1",
                    Input = JsonDocument.Parse("{}").RootElement.Clone()
                }
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Workflow, outcome.Decision.Path);
        Assert.Equal(0, agent.CallCount);
        Assert.Equal(1, workflow.CallCount);
        Assert.Equal("workflow-run", outcome.Workflow!.RunId);
    }

    [Fact]
    public async Task ParallelActorRequirementCannotUseASingleAgentRun()
    {
        var agent = new RecordingAgentRuntime();
        var workflow = new RecordingWorkflowRuntime();
        var router = new RoutedExecutionRuntime(agent, workflow);

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    OperationKind = "parallel-decisions",
                    Requirements = ExecutionRequirements.ParallelActors
                },
                Workflow = WorkflowRequest()
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Workflow, outcome.Decision.Path);
        Assert.Equal(0, agent.CallCount);
        Assert.Equal(1, workflow.CallCount);
    }

    [Fact]
    public async Task IncompatibleExplicitDirectPathFailsBeforeDispatch()
    {
        var agent = new RecordingAgentRuntime();
        var workflow = new RecordingWorkflowRuntime();
        var router = new RoutedExecutionRuntime(agent, workflow);

        await Assert.ThrowsAsync<ArgumentException>(
            () => router.RunAsync(
                    new RoutedExecutionRequest
                    {
                        Route = new ExecutionRouteRequest
                        {
                            ExplicitPath = ExecutionPath.Direct,
                            Requirements = ExecutionRequirements.Tools
                        },
                        Run = RunRequest("invalid-route")
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(0, agent.CallCount);
        Assert.Equal(0, workflow.CallCount);
    }

    [Fact]
    public async Task PolicyTimeoutFallsBackToDirectForCapabilityFreeRequest()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new BlockingPolicy(),
            options: new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromMilliseconds(20),
                MaxConcurrentPolicyCalls = 1
            });

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("route-timeout")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Direct, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyTimeoutFallback,
            outcome.Decision.ReasonCode);
        Assert.Equal(
            DurableExecutionModes.Direct,
            agent.LastRequest!.ExecutionMode);
    }

    [Fact]
    public async Task PolicyErrorFallsBackToDirectForCapabilityFreeRequest()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new ThrowingPolicy());

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("route-policy-error")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Direct, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyErrorFallback,
            outcome.Decision.ReasonCode);
        Assert.Equal(
            DurableExecutionModes.Direct,
            agent.LastRequest!.ExecutionMode);
    }

    [Fact]
    public async Task PolicyCallbackCapacityFallsBackAndRecovers()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var policyDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var blockerDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            workflow: null,
            policy: null,
            options: null,
            shutdownDispatcher: new BoundedCancellationDispatcher(),
            policyCancellationDispatcher:
            new BoundedCancellationDispatcher(),
            callbackExecutionDispatcher: policyDispatcher);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            blockerDispatcher.TryExecute(
                () =>
                {
                    entered.TrySetResult(true);
                    release.Wait();
                    return new ValueTask<int>(1);
                },
                out var blocker));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            var fallback = await router.RunAsync(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest(),
                    Run = RunRequest("route-policy-capacity")
                }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                ExecutionRouteReasonCodes.PolicyErrorFallback,
                fallback.Decision.ReasonCode);
        }
        finally
        {
            release.Set();
            await blocker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        }

        var recovered = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("route-policy-capacity-recovered")
            }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(
            ExecutionRouteReasonCodes.PolicyErrorFallback,
            recovered.Decision.ReasonCode);
        Assert.Equal(2, agent.CallCount);
        await router.DisposeAsync();
    }

    [Fact]
    public async Task InvalidPolicyResultFallsBackToDirectForCapabilityFreeRequest()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new UnknownPathPolicy());

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("route-policy-invalid")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Direct, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
            outcome.Decision.ReasonCode);
        Assert.Equal(
            DurableExecutionModes.Direct,
            agent.LastRequest!.ExecutionMode);
    }

    [Fact]
    public async Task PolicyTimeoutPreservesRequiredWorkflowPath()
    {
        var agent = new RecordingAgentRuntime();
        var workflow = new RecordingWorkflowRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            workflow,
            new BlockingPolicy(),
            new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromMilliseconds(20),
                MaxConcurrentPolicyCalls = 1
            });

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    Requirements = ExecutionRequirements.Workflow
                },
                Workflow = WorkflowRequest()
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Workflow, outcome.Decision.Path);
        Assert.Equal(0, agent.CallCount);
        Assert.Equal(1, workflow.CallCount);
    }

    [Fact]
    public async Task PolicyCannotMutateRequirementsToAuthorizeDirectPath()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new MutatingDirectPolicy());

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    Requirements = ExecutionRequirements.Tools
                },
                Run = RunRequest("route-policy-mutation")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Agent, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
            outcome.Decision.ReasonCode);
    }

    [Fact]
    public async Task InvalidCustomDirectDecisionFailsSafeToAgent()
    {
        var agent = new RecordingAgentRuntime();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new AlwaysDirectPolicy());

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    Requirements = ExecutionRequirements.Tools
                },
                Run = RunRequest("route-invalid-policy")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionPath.Agent, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
            outcome.Decision.ReasonCode);
    }

    [Fact]
    public async Task CallerCancellationDoesNotWaitForIgnoringPolicy()
    {
        var agent = new RecordingAgentRuntime();
        var policy = new IgnoringCancellationPolicy();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: policy,
            options: new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromSeconds(2),
                MaxConcurrentPolicyCalls = 1
            });
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => router.RunAsync(
                    new RoutedExecutionRequest
                    {
                        Route = new ExecutionRouteRequest(),
                        Run = RunRequest("route-cancelled")
                    },
                    cancellation.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, agent.CallCount);
        policy.Release.TrySetResult();
    }

    [Fact]
    public async Task RoutedPayloadIsSnapshottedBeforeCustomPolicyAwaits()
    {
        var agent = new RecordingAgentRuntime();
        var policy = new GatedAgentPolicy();
        var router = new RoutedExecutionRuntime(agent, policy: policy);
        var request = new RoutedExecutionRequest
        {
            Route = new ExecutionRouteRequest
            {
                Requirements = ExecutionRequirements.Tools
            },
            Run = RunRequest("owned-route-payload")
        };
        var running = router.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        request.Run.Run.RunId = "mutated-route-payload";
        request.Run.Run.AgentId = "mutated-agent";
        policy.Release.TrySetResult();
        await running;

        Assert.Equal("owned-route-payload", agent.LastRequest!.Run.RunId);
        Assert.Equal("agent", agent.LastRequest.Run.AgentId);
    }

    [Fact]
    public async Task PolicyTimeoutCancelsCooperativePolicyAndReleasesSlot()
    {
        var agent = new RecordingAgentRuntime();
        var policy = new CancellationAwarePolicy();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: policy,
            options: new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromMilliseconds(25),
                MaxConcurrentPolicyCalls = 1
            });

        var first = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("first-policy-timeout")
            }, cancellationToken: TestContext.Current.CancellationToken);
        await policy.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var second = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    Requirements = ExecutionRequirements.Tools
                },
                Run = RunRequest("second-policy-call")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyTimeoutFallback,
            first.Decision.ReasonCode);
        Assert.Equal(ExecutionPath.Agent, second.Decision.Path);
        Assert.Equal(2, policy.CallCount);
    }

    [Fact]
    public async Task PolicyTimeoutDoesNotRunBlockingCancellationInline()
    {
        var agent = new RecordingAgentRuntime();
        using var release = new ManualResetEventSlim(false);
        var policy = new BlockingCancellationCallbackPolicy(release);
        var router = new RoutedExecutionRuntime(
            agent,
            policy: policy,
            options: new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromMilliseconds(25),
                MaxConcurrentPolicyCalls = 1
            });

        try
        {
            var outcome = await router.RunAsync(
                    new RoutedExecutionRequest
                    {
                        Route = new ExecutionRouteRequest(),
                        Run = RunRequest("blocking-policy-callback")
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                ExecutionRouteReasonCodes.PolicyTimeoutFallback,
                outcome.Decision.ReasonCode);
            await policy.CancellationCallbackEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(policy.CancellationRanOnThreadPool);
        }
        finally
        {
            release.Set();
            await router.DisposeAsync();
        }
    }

    [Fact]
    public async Task IsolatedCancellationContainsCallbackFailureAndReleasesCapacity()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var cancellation = IsolatedCancellationLease.Create(dispatcher);
        var idle = IsolatedCancellationLease.Create(dispatcher);
        await idle.DisposeAsync();
        var callbackEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ranOnThreadPool = true;
        using var registration = cancellation.Token.Register(
            () =>
            {
                ranOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                callbackEntered.TrySetResult(true);
                throw new InvalidOperationException("untrusted callback");
            });

        Assert.True(cancellation.TryCancel());
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await cancellation.DisposeAsync();

        Assert.False(ranOnThreadPool);
        Assert.Equal(0, dispatcher.ActiveReservations);

        Assert.True(dispatcher.TryReserve(out var occupied));
        var rejected = IsolatedCancellationLease.Create(dispatcher);
        Assert.False(rejected.TryCancel());
        await rejected.DisposeAsync();
        Assert.Equal(1, dispatcher.ActiveReservations);
        occupied!.Dispose();

        var next = IsolatedCancellationLease.Create(dispatcher);
        var nextCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var nextRegistration = next.Token.Register(
            () => nextCallback.TrySetResult(true));
        Assert.True(next.TryCancel());
        await nextCallback.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await next.DisposeAsync();
        Assert.Equal(0, dispatcher.ActiveReservations);
    }

    [Fact]
    public async Task SaturatedMemoryCancellationCannotStarveOtherDomains()
    {
        var capacity = ProcessCancellationWorkerPool.WorkersPerClass
                       + ProcessCancellationWorkerPool.QueueCapacityPerClass;
        var extensionDispatcher = new BoundedCancellationDispatcher(
            capacity + 1,
            CancellationWorkerClass.MemoryExtension);
        var leases = new List<IsolatedCancellationLease>();
        var registrations = new List<CancellationTokenRegistration>();
        using var release = new ManualResetEventSlim(false);
        IsolatedCancellationLease? rejected = null;
        try
        {
            var entered = Enumerable.Range(
                    0,
                    ProcessCancellationWorkerPool.WorkersPerClass)
                .Select(
                    _ => new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            for (var index = 0; index < entered.Length; index++)
            {
                var lease = IsolatedCancellationLease.Create(
                    extensionDispatcher);
                var signal = entered[index];
                registrations.Add(lease.Token.Register(
                    () =>
                    {
                        signal.TrySetResult(true);
                        release.Wait();
                    }));
                leases.Add(lease);
                Assert.True(lease.TryCancel());
            }

            await Task.WhenAll(entered.Select(item => item.Task))
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            for (var index = 0;
                 index < ProcessCancellationWorkerPool.QueueCapacityPerClass;
                 index++)
            {
                var lease = IsolatedCancellationLease.Create(
                    extensionDispatcher);
                registrations.Add(lease.Token.Register(() => release.Wait()));
                leases.Add(lease);
                Assert.True(lease.TryCancel());
            }

            rejected = IsolatedCancellationLease.Create(extensionDispatcher);
            Assert.False(rejected.TryCancel());
            Assert.True(extensionDispatcher.TryReserve(out var directRejected));
            using var directSource = new CancellationTokenSource();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => directRejected!.DispatchAsync(directSource));

            var controlDispatcher = new BoundedCancellationDispatcher(
                capacity: 1,
                workerClass: CancellationWorkerClass.ControlPlane);
            var control = IsolatedCancellationLease.Create(controlDispatcher);
            var controlEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var controlRegistration = control.Token.Register(
                () => controlEntered.TrySetResult(true));
            Assert.True(control.TryCancel());
            await controlEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await control.DisposeAsync();
            Assert.Equal(0, controlDispatcher.ActiveReservations);

            var policyDispatcher = new BoundedCancellationDispatcher(
                capacity: 1,
                workerClass: CancellationWorkerClass.ExecutionPolicy);
            var policy = IsolatedCancellationLease.Create(policyDispatcher);
            var policyEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var policyRegistration = policy.Token.Register(
                () => policyEntered.TrySetResult(true));
            Assert.True(policy.TryCancel());
            await policyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            await policy.DisposeAsync();
            Assert.Equal(0, policyDispatcher.ActiveReservations);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(
                leases.Select(lease => lease.DisposeAsync().AsTask()))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            if (rejected is not null)
            {
                await rejected.DisposeAsync();
            }
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }

        var recovered = IsolatedCancellationLease.Create(extensionDispatcher);
        var recoveredCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var recoveredRegistration = recovered.Token.Register(
            () => recoveredCallback.TrySetResult(true));
        Assert.True(recovered.TryCancel());
        await recoveredCallback.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await recovered.DisposeAsync();
        Assert.Equal(0, extensionDispatcher.ActiveReservations);
    }

    [Fact]
    public async Task HealthyPolicyIsNotRejectedByOccupiedCancellationLane()
    {
        var policyDispatcher = new BoundedCancellationDispatcher(capacity: 1);
        Assert.True(policyDispatcher.TryReserve(out var occupied));
        await using var router = new RoutedExecutionRuntime(
            new RecordingAgentRuntime(),
            workflow: null,
            new AlwaysDirectPolicy(),
            new ExecutionRouterOptions(),
            BoundedCancellationDispatcher.LifecycleShared,
            policyDispatcher);

        var admittedWhileOccupied = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("policy-capacity-occupied")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("always_direct", admittedWhileOccupied.Decision.ReasonCode);
        occupied!.Dispose();
        Assert.Equal(0, policyDispatcher.ActiveReservations);
    }

    [Fact]
    public async Task ConcurrentLeaseDisposalWaitsForCancellationDispatch()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var cancellation = IsolatedCancellationLease.Create(dispatcher);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        using var registration = cancellation.Token.Register(
            () =>
            {
                entered.TrySetResult(true);
                release.Wait();
            });

        Assert.True(cancellation.TryCancel());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var first = cancellation.DisposeAsync().AsTask();
        var second = cancellation.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, dispatcher.ActiveReservations);
    }

    [Fact]
    public async Task StopIsBoundedAndDisposeWaitsForIgnoringPolicy()
    {
        var agent = new RecordingAgentRuntime();
        var policy = new IgnoringCancellationPolicy();
        var router = new RoutedExecutionRuntime(
            agent,
            policy: policy,
            options: new ExecutionRouterOptions
            {
                PolicyTimeout = TimeSpan.FromMilliseconds(20),
                MaxConcurrentPolicyCalls = 1,
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });

        var outcome = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest(),
                Run = RunRequest("route-detached-policy")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyTimeoutFallback,
            outcome.Decision.ReasonCode);
        Assert.False(await router.StopAsync());

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => router.RunAsync(
                    new RoutedExecutionRequest
                    {
                        Route = new ExecutionRouteRequest(),
                        Run = RunRequest("route-after-stop")
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        var disposal = router.DisposeAsync().AsTask();
        await Task.Delay(30, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(disposal.IsCompleted);

        policy.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShutdownCancellationWorkersAreGloballyBounded()
    {
        using var release = new ManualResetEventSlim(false);
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var blockingAgent = new BlockingCancellationAgentRuntime(release);
        var options = new ExecutionRouterOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(20)
        };
        var first = new RoutedExecutionRuntime(
            blockingAgent,
            workflow: null,
            policy: null,
            options,
            dispatcher);
        var cooperativeAgent = new CooperativeCancellationAgentRuntime();
        var second = new RoutedExecutionRuntime(
            cooperativeAgent,
            workflow: null,
            policy: null,
            options,
            dispatcher);

        var running = first.RunAsync(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest(),
                    Run = RunRequest("blocking-router-shutdown")
                }, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await blockingAgent.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var secondRun = second.RunAsync(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest(),
                    Run = RunRequest("waiting-router-shutdown")
                }, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await cooperativeAgent.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        var firstStop = first.StopAsync().AsTask();
        await blockingAgent.CancellationCallbackEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(await firstStop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, dispatcher.ActiveReservations);

        var idle = new RoutedExecutionRuntime(
            new RecordingAgentRuntime(),
            workflow: null,
            policy: null,
            options,
            dispatcher);
        await idle.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatcher.ActiveReservations);

        var naturalAgent = new NaturallyCompletingAgentRuntime();
        var natural = new RoutedExecutionRuntime(
            naturalAgent,
            workflow: null,
            policy: null,
            options,
            dispatcher);
        var naturalRun = natural.RunAsync(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest(),
                    Run = RunRequest("natural-router-shutdown")
                }, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await naturalAgent.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var naturalDisposal = natural.DisposeAsync().AsTask();
        var concurrentNaturalDisposal = natural.DisposeAsync().AsTask();
        naturalAgent.Release.TrySetResult();
        await naturalRun.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await Task.WhenAll(naturalDisposal, concurrentNaturalDisposal)
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatcher.ActiveReservations);

        Assert.False(await second.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, dispatcher.ActiveReservations);
        var secondDisposal = second.DisposeAsync().AsTask();
        await Task.Delay(30, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(secondDisposal.IsCompleted);

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await first.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => secondRun.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        await secondDisposal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await first.DisposeAsync();
        Assert.Equal(0, dispatcher.ActiveReservations);
    }

    private static DurableRunRequest RunRequest(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = runId,
                AgentId = "agent",
                WorldId = "world",
                State = RunStates.Queued,
                CreatedAt = now,
                UpdatedAt = now
            }
        };
    }

    private static RoutedWorkflowRequest WorkflowRequest() =>
        new()
        {
            WorkflowId = "month",
            RunKey = "month-17",
            OwnerId = "save-1",
            Input = JsonDocument.Parse("{}").RootElement.Clone()
        };

    private sealed class RecordingAgentRuntime : IDurableAgentRuntime
    {
        private int _callCount;

        public RuntimeControlPlane Controls { get; } = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public DurableRunRequest? LastRequest { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            LastRequest = request;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWorkflowRuntime : IRoutedWorkflowRuntime
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<RoutedWorkflowOutcome> RunAsync(
            RoutedWorkflowRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return new ValueTask<RoutedWorkflowOutcome>(
                new RoutedWorkflowOutcome
                {
                    RunId = "workflow-run",
                    WorkflowId = request.WorkflowId,
                    Status = "completed",
                    Output = request.Input.Clone()
                });
        }
    }

    private sealed class BlockingCancellationAgentRuntime :
        IDurableAgentRuntime
    {
        private readonly ManualResetEventSlim _release;

        internal BlockingCancellationAgentRuntime(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public RuntimeControlPlane Controls { get; } = new();

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationCallbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CancellationCallbackEntered.TrySetResult();
                    _release.Wait();
                });
            Started.TrySetResult();
            await cancellation;
            throw new InvalidOperationException("The run did not cancel.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CooperativeCancellationAgentRuntime :
        IDurableAgentRuntime
    {
        public RuntimeControlPlane Controls { get; } = new();

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The run did not cancel.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NaturallyCompletingAgentRuntime :
        IDurableAgentRuntime
    {
        public RuntimeControlPlane Controls { get; } = new();

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            return new DurableRunOutcome { Run = request.Run };
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "blocking";

        public string Version => "1";

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return new ExecutionRouteDecision(
                ExecutionPath.Direct,
                "late",
                PolicyId,
                Version);
        }
    }

    private sealed class ThrowingPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "throwing";

        public string Version => "1";

        public ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("policy failed");
        }
    }

    private sealed class UnknownPathPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "unknown-path";

        public string Version => "1";

        public ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ExecutionRouteDecision>(
                new ExecutionRouteDecision(
                    (ExecutionPath)999,
                    "unknown_path",
                    PolicyId,
                    Version));
        }
    }

    private sealed class AlwaysDirectPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "always-direct";

        public string Version => "1";

        public ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken) =>
            new(
                new ExecutionRouteDecision(
                    ExecutionPath.Direct,
                    "always_direct",
                    PolicyId,
                    Version));
    }

    private sealed class IgnoringCancellationPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "ignoring-cancellation";

        public string Version => "1";

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            await Release.Task;
            return new ExecutionRouteDecision(
                ExecutionPath.Direct,
                "released",
                PolicyId,
                Version);
        }
    }

    private sealed class GatedAgentPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "gated-agent";

        public string Version => "1";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ExecutionRouteDecision(
                ExecutionPath.Agent,
                "gated_agent",
                PolicyId,
                Version);
        }
    }

    private sealed class CancellationAwarePolicy : IExecutionRoutePolicy
    {
        private int _callCount;

        public string PolicyId => "cancellation-aware";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }

            return new ExecutionRouteDecision(
                ExecutionPath.Agent,
                "cooperative_agent",
                PolicyId,
                Version);
        }
    }

    private sealed class BlockingCancellationCallbackPolicy :
        IExecutionRoutePolicy
    {
        private readonly ManualResetEventSlim _release;

        public BlockingCancellationCallbackPolicy(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public string PolicyId => "blocking-cancellation-callback";

        public string Version => "1";

        public TaskCompletionSource CancellationCallbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationRanOnThreadPool { get; private set; }

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CancellationRanOnThreadPool =
                        Thread.CurrentThread.IsThreadPoolThread;
                    CancellationCallbackEntered.TrySetResult();
                    _release.Wait();
                });
            await cancellation;
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class MutatingDirectPolicy : IExecutionRoutePolicy
    {
        public string PolicyId => "mutating-direct";

        public string Version => "1";

        public ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Requirements = ExecutionRequirements.None;
            return new ValueTask<ExecutionRouteDecision>(
                new ExecutionRouteDecision(
                    ExecutionPath.Direct,
                    "mutated",
                    PolicyId,
                    Version));
        }
    }
}
