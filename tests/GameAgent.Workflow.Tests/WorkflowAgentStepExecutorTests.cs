using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class WorkflowAgentStepExecutorTests
{
    [Fact]
    public async Task CompletedAgentRunUsesStableStageIdentity()
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Completed(
                    request.Run.RunId,
                    WorkflowTestData.Json("""{"value":"hello"}"""))
        };
        var adapter = new TestAdapter();
        var executor = new WorkflowAgentStepExecutor(runtime, adapter);
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            executor);

        var result = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "completed",
                "owner",
                WorkflowTestData.Json("""{"value":"hello"}""")));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(
            "hello",
            result.Output!.Value.GetProperty("value").GetString());
        var instance = Assert.Single(result.StageInstances);
        Assert.Equal(
            WorkflowAgentStepExecutor.CreateAgentRunId(
                result.RunId,
                instance.InstanceId),
            runtime.LastRunId);
        Assert.Equal(runtime.LastRunId, adapter.LastInvocation!.AgentRunId);
        Assert.Equal(1, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task NonterminalAgentRunLeavesStepStartedThenRecoveryResumes()
    {
        var runtime = new ScriptedRuntime();
        runtime.RunHandler = (request, _) =>
            Outcome(request.Run.RunId, RunStates.Running);
        runtime.ResumeHandler = (runId, _, _, _, _) =>
            Completed(runId, WorkflowTestData.Json("""{"value":"done"}"""));
        var store = new InMemoryWorkflowRunStore();
        var adapter = new TestAdapter();
        var runner = CreateRunner(
            store,
            new WorkflowAgentStepExecutor(runtime, adapter));
        var workflow = Compile();

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "recover",
                    "owner-a",
                    WorkflowTestData.Json("""{"value":"input"}"""))));

        var runId = Assert.IsType<string>(
            adapter.LastInvocation?.WorkflowRunId);
        var started = await store.ReadAsync(runId);
        Assert.Equal(
            WorkflowStageStatus.Started,
            Assert.Single(started!.StageInstances).Status);

        var completed = await runner.RecoverAsync(
            workflow,
            runId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(1, runtime.RunCalls);
        Assert.Equal(1, runtime.ResumeCalls);
        Assert.Equal(runtime.LastRunId, runtime.LastResumeId);
        Assert.NotNull(runtime.LastGuard);
    }

    [Fact]
    public async Task RecoveryStartsSameRunOnlyWhenJournalSaysItIsMissing()
    {
        var firstRuntime = new ScriptedRuntime
        {
            RunHandler = (_, _) =>
                throw new WorkflowExecutorInterruptedException(
                    "simulated crash before journal start")
        };
        var store = new InMemoryWorkflowRunStore();
        var workflow = Compile();
        var firstAdapter = new TestAdapter();
        var firstRunner = CreateRunner(
            store,
            new WorkflowAgentStepExecutor(
                firstRuntime,
                firstAdapter));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await firstRunner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "missing",
                    "owner-a",
                    WorkflowTestData.Json("""{"value":"input"}"""))));

        var workflowRunId =
            Assert.IsType<string>(
                firstAdapter.LastInvocation?.WorkflowRunId);
        var recoveredRuntime = new ScriptedRuntime
        {
            ResumeHandler = (_, _, _, _, _) =>
                throw new KeyNotFoundException("missing"),
            RunHandler = (request, _) =>
                Completed(
                    request.Run.RunId,
                    WorkflowTestData.Json("""{"value":"restarted"}"""))
        };
        var recoveryRunner = CreateRunner(
            store,
            new WorkflowAgentStepExecutor(
                recoveredRuntime,
                new TestAdapter()));

        var completed = await recoveryRunner.RecoverAsync(
            workflow,
            workflowRunId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(1, recoveredRuntime.ResumeCalls);
        Assert.Equal(1, recoveredRuntime.RunCalls);
        Assert.Equal(
            recoveredRuntime.LastResumeId,
            recoveredRuntime.LastRunId);
    }

    [Fact]
    public async Task DuplicateStartFallsBackToResumeWithoutReplay()
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                throw new DuplicateRunException(request.Run.RunId),
            ResumeHandler = (runId, _, _, _, _) =>
                Completed(
                    runId,
                    WorkflowTestData.Json("""{"value":"existing"}"""))
        };
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowAgentStepExecutor(runtime, new TestAdapter()));

        var completed = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "duplicate",
                "owner",
                WorkflowTestData.Json("""{"value":"input"}""")));

        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(1, runtime.RunCalls);
        Assert.Equal(1, runtime.ResumeCalls);
        Assert.Equal(runtime.LastRunId, runtime.LastResumeId);
    }

    [Theory]
    [InlineData(
        RunStates.Failed,
        WorkflowAgentReasonCodes.Failed)]
    [InlineData(
        RunStates.Cancelled,
        WorkflowAgentReasonCodes.Cancelled)]
    [InlineData(
        RunStates.Interrupted,
        WorkflowAgentReasonCodes.Interrupted)]
    [InlineData(
        RunStates.BudgetExhausted,
        WorkflowAgentReasonCodes.BudgetExhausted)]
    public async Task TerminalAgentFailuresUseStableReasonCodes(
        string state,
        string reasonCode)
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Outcome(request.Run.RunId, state)
        };
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowAgentStepExecutor(runtime, new TestAdapter()));

        var failed = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "terminal-" + state,
                "owner",
                WorkflowTestData.Json("""{"value":"input"}""")));

        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal(reasonCode, failed.ReasonCode);
    }

    [Theory]
    [InlineData(RunStates.Failed)]
    [InlineData(RunStates.BudgetExhausted)]
    public async Task GamePolicyCanProjectSelectedTerminalOutcome(
        string state)
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Outcome(request.Run.RunId, state)
        };
        var adapter = new TestAdapter(
            terminalFallback: WorkflowTestData.Json(
                """{"value":"local-default"}"""));
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowAgentStepExecutor(runtime, adapter));

        var completed = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "projected-" + state,
                "owner",
                WorkflowTestData.Json("""{"value":"input"}""")));

        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(
            "local-default",
            completed.Output!.Value.GetProperty("value").GetString());
        Assert.Equal(state, adapter.LastTerminalState);
    }

    [Fact]
    public async Task UndefinedTerminalProjectionFailsClosed()
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Outcome(request.Run.RunId, RunStates.Failed)
        };
        var adapter = new TestAdapter(
            terminalFallback: default(JsonElement));
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowAgentStepExecutor(runtime, adapter));

        var failed = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "undefined-projection",
                "owner",
                WorkflowTestData.Json("""{"value":"input"}""")));

        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal(
            WorkflowAgentReasonCodes.InvalidOutcome,
            failed.ReasonCode);
    }

    [Fact]
    public async Task AdapterCannotChangeTheDerivedAgentRunId()
    {
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Completed(
                    request.Run.RunId,
                    WorkflowTestData.Json("""{"value":"unused"}"""))
        };
        var runner = CreateRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowAgentStepExecutor(
                runtime,
                new TestAdapter(useWrongRunId: true)));

        var failed = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "wrong-id",
                "owner",
                WorkflowTestData.Json("""{"value":"input"}""")));

        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal(
            WorkflowAgentReasonCodes.InvalidRunIdentity,
            failed.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
    }

    [Fact]
    public async Task NonReturningCreateRequestIsBoundedAndTracked()
    {
        var adapter = new BlockingAdapter(blockCreateRequest: true);
        var runtime = new ScriptedRuntime();
        var executor = new WorkflowAgentStepExecutor(
            runtime,
            adapter,
            reconciler: null,
            options: ShortAdapterOptions());
        try
        {
            var execution = CreateRunner(
                    new InMemoryWorkflowRunStore(),
                    executor)
                .ExecuteAsync(
                    Compile(),
                    new WorkflowRunRequest(
                        "blocked-create",
                        "owner",
                        WorkflowTestData.Json("""{"value":"input"}""")))
                .AsTask();
            await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            var failed = await execution.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
            Assert.Equal(WorkflowReasonCodes.ExecutorFailed, failed.ReasonCode);
            Assert.Equal(0, runtime.RunCalls);
            Assert.Equal(1, executor.DetachedAdapterCallCount);
        }
        finally
        {
            adapter.Release();
            await executor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DetachedAdapterCallRetainsItsConcurrencySlot()
    {
        var adapter = new BlockingAdapter(blockCreateRequest: true);
        var executor = new WorkflowAgentStepExecutor(
            new ScriptedRuntime(),
            adapter,
            reconciler: null,
            options: new WorkflowAgentStepExecutorOptions
            {
                MaxConcurrentAdapterCalls = 1,
                AdapterCallTimeout = TimeSpan.FromMilliseconds(50),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
        var runner = CreateRunner(new InMemoryWorkflowRunStore(), executor);
        try
        {
            var first = runner.ExecuteAsync(
                    Compile(),
                    new WorkflowRunRequest(
                        "slot-first",
                        "owner",
                        WorkflowTestData.Json("""{"value":"input"}""")))
                .AsTask();
            await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            var second = runner.ExecuteAsync(
                    Compile(),
                    new WorkflowRunRequest(
                        "slot-second",
                        "owner",
                        WorkflowTestData.Json("""{"value":"input"}""")))
                .AsTask();

            var outcomes = await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.All(
                outcomes,
                outcome => Assert.Equal(
                    WorkflowRunStatus.Failed,
                    outcome.Status));
            Assert.Equal(1, adapter.CreateRequestCalls);
            Assert.Equal(1, executor.DetachedAdapterCallCount);
        }
        finally
        {
            adapter.Release();
            await executor.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonReturningTerminalProjectorIsBoundedAndTracked()
    {
        var adapter = new BlockingAdapter(blockTerminalProjector: true);
        var runtime = new ScriptedRuntime
        {
            RunHandler = (request, _) =>
                Outcome(request.Run.RunId, RunStates.Failed)
        };
        var executor = new WorkflowAgentStepExecutor(
            runtime,
            adapter,
            reconciler: null,
            options: ShortAdapterOptions());
        try
        {
            var execution = CreateRunner(
                    new InMemoryWorkflowRunStore(),
                    executor)
                .ExecuteAsync(
                    Compile(),
                    new WorkflowRunRequest(
                        "blocked-projector",
                        "owner",
                        WorkflowTestData.Json("""{"value":"input"}""")))
                .AsTask();
            await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            var failed = await execution.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
            Assert.Equal(WorkflowReasonCodes.ExecutorFailed, failed.ReasonCode);
            Assert.Equal(1, runtime.RunCalls);
            Assert.Equal(1, executor.DetachedAdapterCallCount);
        }
        finally
        {
            adapter.Release();
            await executor.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShutdownIsBoundedButDisposeWaitsForDetachedAdapterCall()
    {
        var adapter = new BlockingAdapter(blockCreateRequest: true);
        var executor = new WorkflowAgentStepExecutor(
            new ScriptedRuntime(),
            adapter,
            reconciler: null,
            options: new WorkflowAgentStepExecutorOptions
            {
                AdapterCallTimeout = TimeSpan.FromSeconds(5),
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });
        var execution = CreateRunner(
                new InMemoryWorkflowRunStore(),
                executor)
            .ExecuteAsync(
                Compile(),
                new WorkflowRunRequest(
                    "shutdown-drain",
                    "owner",
                    WorkflowTestData.Json("""{"value":"input"}""")))
            .AsTask();
        await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.False(await executor.StopAsync());
            var failed = await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
            Assert.Equal(1, executor.DetachedAdapterCallCount);

            var dispose = executor.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.False(dispose.IsCompleted);

            adapter.Release();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, executor.DetachedAdapterCallCount);
            Assert.True(await executor.StopAsync());
        }
        finally
        {
            adapter.Release();
            await executor.DisposeAsync();
        }
    }

    private static WorkflowAgentStepExecutorOptions ShortAdapterOptions() =>
        new()
        {
            AdapterCallTimeout = TimeSpan.FromMilliseconds(25),
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        };

    private static WorkflowRunner CreateRunner(
        IWorkflowRunStore store,
        IWorkflowStepExecutor executor)
    {
        var runtime = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));
        return runtime;
    }

    private static CompiledWorkflow Compile()
    {
        var schema = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "value": {
                  "type": "string",
                  "maxLength": 128
                }
              },
              "required": ["value"],
              "additionalProperties": false
            }
            """);
        var stage = WorkflowStageDefinition.CreateStep(
            "agent",
            new WorkflowStepReference(WorkflowAgentStepKinds.Run),
            schema,
            schema);
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "agent-workflow",
                "v1",
                schema,
                schema,
                "agent",
                new[] { stage }));
    }

    private static DurableRunOutcome Completed(
        string runId,
        JsonElement output)
    {
        return new DurableRunOutcome
        {
            Run = AgentRun(runId, RunStates.Completed),
            FinalOutput = output.Clone()
        };
    }

    private static DurableRunOutcome Outcome(
        string runId,
        string state)
    {
        return new DurableRunOutcome
        {
            Run = AgentRun(runId, state)
        };
    }

    private static AgentRun AgentRun(string runId, string state)
    {
        return new AgentRun
        {
            RunId = runId,
            AgentId = "npc",
            WorldId = "world",
            State = state
        };
    }

    private sealed class TestAdapter :
        IWorkflowAgentRunAdapter,
        IWorkflowAgentTerminalOutcomeProjector
    {
        private readonly bool _useWrongRunId;
        private readonly JsonElement? _terminalFallback;

        public TestAdapter(
            bool useWrongRunId = false,
            JsonElement? terminalFallback = null)
        {
            _useWrongRunId = useWrongRunId;
            _terminalFallback = terminalFallback.HasValue
                                && terminalFallback.Value.ValueKind
                                != JsonValueKind.Undefined
                ? terminalFallback.Value.Clone()
                : terminalFallback;
        }

        public WorkflowAgentInvocation? LastInvocation { get; private set; }

        public string? LastTerminalState { get; private set; }

        public DurableRunRequest CreateRequest(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            LastInvocation = invocation;
            return new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = _useWrongRunId
                        ? "wrong"
                        : invocation.AgentRunId,
                    AgentId = "npc",
                    WorldId = "world",
                    Trigger = new AgentTrigger
                    {
                        Type = "workflow",
                        SourceId = invocation.WorkflowRunId
                    }
                }
            };
        }

        public DurableRunContinuation? CreateContinuation(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            LastInvocation = invocation;
            return new DurableRunContinuation();
        }

        public DurableRunResumeGuard? CreateResumeGuard(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            return new DurableRunResumeGuard
            {
                ExpectedAgentId = "npc"
            };
        }

        public JsonElement ProjectOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome)
        {
            LastInvocation = invocation;
            return outcome.FinalOutput!.Value.Clone();
        }

        public bool TryProjectTerminalOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome,
            out JsonElement output)
        {
            LastInvocation = invocation;
            LastTerminalState = outcome.Run.State;
            if (!_terminalFallback.HasValue)
            {
                output = default;
                return false;
            }

            output = _terminalFallback.Value.ValueKind
                     == JsonValueKind.Undefined
                ? default
                : _terminalFallback.Value.Clone();
            return true;
        }
    }

    private sealed class BlockingAdapter :
        IWorkflowAgentRunAdapter,
        IWorkflowAgentTerminalOutcomeProjector
    {
        private readonly bool _blockCreateRequest;
        private readonly bool _blockTerminalProjector;
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createRequestCalls;

        public BlockingAdapter(
            bool blockCreateRequest = false,
            bool blockTerminalProjector = false)
        {
            _blockCreateRequest = blockCreateRequest;
            _blockTerminalProjector = blockTerminalProjector;
        }

        public Task Entered => _entered.Task;

        public int CreateRequestCalls =>
            Volatile.Read(ref _createRequestCalls);

        public DurableRunRequest CreateRequest(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            _ = input;
            Interlocked.Increment(ref _createRequestCalls);
            BlockIfRequested(_blockCreateRequest);
            return new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = invocation.AgentRunId,
                    AgentId = "npc",
                    WorldId = "world"
                }
            };
        }

        public DurableRunContinuation? CreateContinuation(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            _ = invocation;
            _ = input;
            return new DurableRunContinuation();
        }

        public DurableRunResumeGuard? CreateResumeGuard(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            _ = invocation;
            _ = input;
            return new DurableRunResumeGuard
            {
                ExpectedAgentId = "npc"
            };
        }

        public JsonElement ProjectOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome)
        {
            _ = invocation;
            _ = input;
            return outcome.FinalOutput!.Value.Clone();
        }

        public bool TryProjectTerminalOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome,
            out JsonElement output)
        {
            _ = invocation;
            _ = input;
            _ = outcome;
            BlockIfRequested(_blockTerminalProjector);
            output = default;
            return false;
        }

        public void Release() => _release.TrySetResult(true);

        private void BlockIfRequested(bool block)
        {
            if (!block)
            {
                return;
            }

            _entered.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class ScriptedRuntime : IGuardedDurableAgentRuntime
    {
        public Func<DurableRunRequest, CancellationToken,
            DurableRunOutcome>? RunHandler
        { get; set; }

        public Func<
            string,
            DurableRunContinuation?,
            IGameOperationReconciler?,
            CancellationToken,
            DurableRunResumeGuard?,
            DurableRunOutcome>? ResumeHandler
        { get; set; }

        public RuntimeControlPlane Controls { get; } = new();

        public int RunCalls { get; private set; }

        public int ResumeCalls { get; private set; }

        public string? LastRunId { get; private set; }

        public string? LastResumeId { get; private set; }

        public DurableRunResumeGuard? LastGuard { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RunCalls++;
            LastRunId = request.Run.RunId;
            var handler = RunHandler
                          ?? throw new InvalidOperationException(
                              "No run handler was configured.");
            return new ValueTask<DurableRunOutcome>(
                handler(request, cancellationToken));
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
            ResumeCalls++;
            LastResumeId = runId;
            LastGuard = guard;
            var handler = ResumeHandler
                          ?? throw new InvalidOperationException(
                              "No resume handler was configured.");
            return new ValueTask<DurableRunOutcome>(
                handler(
                    runId,
                    continuation,
                    reconciler,
                    cancellationToken,
                    guard));
        }
    }

}
