using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

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
            });

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
            });

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
            });

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
            });

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
                    })
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
            });

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
            });

        Assert.Equal(ExecutionPath.Direct, outcome.Decision.Path);
        Assert.Equal(
            ExecutionRouteReasonCodes.PolicyErrorFallback,
            outcome.Decision.ReasonCode);
        Assert.Equal(
            DurableExecutionModes.Direct,
            agent.LastRequest!.ExecutionMode);
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
            });

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
            });

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
            });

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
            });

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
                .WaitAsync(TimeSpan.FromMilliseconds(500)));

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
        var running = router.RunAsync(request).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

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
            });
        await policy.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await router.RunAsync(
            new RoutedExecutionRequest
            {
                Route = new ExecutionRouteRequest
                {
                    Requirements = ExecutionRequirements.Tools
                },
                Run = RunRequest("second-policy-call")
            });

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
        var router = new RoutedExecutionRuntime(
            agent,
            policy: new BlockingCancellationCallbackPolicy(release),
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
                    })
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Equal(
                ExecutionRouteReasonCodes.PolicyTimeoutFallback,
                outcome.Decision.ReasonCode);
        }
        finally
        {
            release.Set();
        }
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
            });

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
                    })
                .AsTask());

        var disposal = router.DisposeAsync().AsTask();
        await Task.Delay(30);
        Assert.False(disposal.IsCompleted);

        policy.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
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
                })
            .AsTask();
        await blockingAgent.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondRun = second.RunAsync(
                new RoutedExecutionRequest
                {
                    Route = new ExecutionRouteRequest(),
                    Run = RunRequest("waiting-router-shutdown")
                })
            .AsTask();
        await cooperativeAgent.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var firstStop = first.StopAsync().AsTask();
        await blockingAgent.CancellationCallbackEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.False(await firstStop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, dispatcher.ActiveReservations);

        var idle = new RoutedExecutionRuntime(
            new RecordingAgentRuntime(),
            workflow: null,
            policy: null,
            options,
            dispatcher);
        await idle.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2));
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
                })
            .AsTask();
        await naturalAgent.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var naturalDisposal = natural.DisposeAsync().AsTask();
        var concurrentNaturalDisposal = natural.DisposeAsync().AsTask();
        naturalAgent.Release.TrySetResult();
        await naturalRun.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(naturalDisposal, concurrentNaturalDisposal)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, dispatcher.ActiveReservations);

        Assert.False(await second.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, dispatcher.ActiveReservations);
        var secondDisposal = second.DisposeAsync().AsTask();
        await Task.Delay(30);
        Assert.False(secondDisposal.IsCompleted);

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await first.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => secondRun.WaitAsync(TimeSpan.FromSeconds(2)));
        await secondDisposal.WaitAsync(TimeSpan.FromSeconds(2));
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
            using var registration = cancellationToken.Register(
                () =>
                {
                    CancellationCallbackEntered.TrySetResult();
                    _release.Wait();
                });
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

        public async ValueTask<ExecutionRouteDecision> SelectAsync(
            ExecutionRouteRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                () => _release.Wait());
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
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
