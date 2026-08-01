using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameAgent.Core;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class WorkflowRunnerTests
{
    [Fact]
    public async Task RoutedWorkflowRuntimeExecutesRegisteredWorkflow()
    {
        var executor = new PassExecutor();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            WorkflowTestData.StringSchema(),
            WorkflowTestData.StringSchema());
        var routed = new RoutedWorkflowRuntime(
            new WorkflowRunner(
                new InMemoryWorkflowRunStore(),
                new WorkflowStepExecutorRegistry(new[] { executor })),
            new[] { workflow });

        var outcome = await routed.RunAsync(
            new RoutedWorkflowRequest
            {
                WorkflowId = "single",
                RunKey = "routed",
                OwnerId = "owner",
                Input = WorkflowTestData.Json("\"value\"")
            },
            default);

        Assert.Equal("single", outcome.WorkflowId);
        Assert.Equal("completed", outcome.Status);
        Assert.Equal("value", outcome.Output!.Value.GetString());
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task IndependentRootsRunInParallelAndReduceInStableOrder()
    {
        var valueExecutor = new ParallelValueExecutor();
        var reduceExecutor = new ReduceExecutor();
        var registry = new WorkflowStepExecutorRegistry(
            new IWorkflowStepExecutor[] { valueExecutor, reduceExecutor });
        var workflow = CompileParallelWorkflow();
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            registry);

        var result = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "parallel-run",
                "owner-a",
                WorkflowTestData.Json("""{"seed":"x"}""")));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("a=A,b=B", result.Output!.Value.GetString());
        Assert.Equal(2, valueExecutor.MaxActive);
        Assert.Equal(new[] { "a", "b" }, reduceExecutor.LastOrder);
        Assert.Equal(3, result.Usage.ExecuteCalls);
    }

    [Fact]
    public async Task ForeachUsesExplicitIdentityRejectsCollisionsAndKeepsInputOrder()
    {
        var executor = new ForeachValueExecutor();
        var registry = new WorkflowStepExecutorRegistry(
            new[] { executor });
        var workflow = CompileForeachWorkflow(maxItems: 8);
        var store = new InMemoryWorkflowRunStore();
        var runner = new WorkflowRunner(store, registry);

        var completed = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "foreach-ok",
                "owner-a",
                WorkflowTestData.Json(
                    """
                    {
                      "items": [
                        { "id": "z", "value": "Z" },
                        { "id": "a", "value": "A" }
                      ]
                    }
                    """)));

        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(
            new[] { "Z", "A" },
            completed.Output!.Value
                .EnumerateArray()
                .Select(item => item.GetString()));
        var root = Assert.Single(
            completed.StageInstances,
            item => item.InstanceKind == WorkflowInstanceKind.Stage);
        var children = completed.StageInstances
            .Where(item =>
                item.InstanceKind == WorkflowInstanceKind.ForeachItem)
            .OrderBy(item => item.ItemOrdinal)
            .ToArray();
        Assert.Equal(2, children.Length);
        var zDigest = WorkflowIdentity.ComputeJsonDigest(
            WorkflowTestData.Json("\"z\""));
        Assert.Equal(
            WorkflowIdentity.CreateForeachChildId(
                root.InstanceId,
                zDigest),
            children[0].InstanceId);

        var duplicate = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "foreach-duplicate",
                "owner-a",
                WorkflowTestData.Json(
                    """
                    {
                      "items": [
                        { "id": "same", "value": "one" },
                        { "id": "same", "value": "two" }
                      ]
                    }
                    """)));

        Assert.Equal(WorkflowRunStatus.Failed, duplicate.Status);
        Assert.Equal(
            WorkflowReasonCodes.ForeachIdentityCollision,
            duplicate.ReasonCode);
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task StartedStepIsRecoveredWithANewGenerationAfterCrash()
    {
        var executor = new CrashThenRecoverExecutor();
        var store = new InMemoryWorkflowRunStore();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            WorkflowTestData.StringSchema(),
            WorkflowTestData.StringSchema());
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "crash-run",
                    "owner-a",
                    WorkflowTestData.Json("\"input\""))));

        var runId = Assert.IsType<string>(executor.LastRunId);
        var interrupted = await store.ReadAsync(runId);
        Assert.NotNull(interrupted);
        var started = Assert.Single(interrupted!.StageInstances);
        Assert.Equal(WorkflowStageStatus.Started, started.Status);
        Assert.Equal(1, started.Generation);
        Assert.Equal(
            "saved",
            started.Checkpoint!.Value.GetProperty("token").GetString());
        Assert.Equal(
            WorkflowIdentity.ComputeJsonDigest(started.Checkpoint.Value),
            started.CheckpointDigest);

        var recovered = await runner.RecoverAsync(
            workflow,
            runId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal("input", recovered.Output!.Value.GetString());
        var terminal = Assert.Single(recovered.StageInstances);
        Assert.Equal(2, terminal.Generation);
        Assert.Equal(2, terminal.Attempt);
        Assert.Equal(1, terminal.RecoveryAttempts);
        Assert.Equal(1, executor.RecoverCalls);
        Assert.True(executor.RecoveredCheckpoint);
        Assert.False(
            await executor.OriginalContext!.SaveCheckpointAsync(
                WorkflowTestData.Json("""{"late":true}""")));
    }

    [Fact]
    public async Task LoopPersistsCursorAndRecoversInterruptedIteration()
    {
        var executor = new LoopCrashExecutor();
        var store = new InMemoryWorkflowRunStore();
        var schema = WorkflowTestData.LoopValueSchema();
        var stage = WorkflowStageDefinition.CreateLoop(
            "loop",
            new WorkflowLoopDefinition(
                new WorkflowStepReference(executor.Kind),
                "/done",
                3,
                schema,
                schema),
            schema,
            schema);
        var workflow = new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "loop-recovery",
                "v1",
                schema,
                schema,
                "loop",
                new[] { stage }));
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "loop-run",
                    "owner-a",
                    WorkflowTestData.Json(
                        """{"value":0,"done":false}"""))));

        var runId = Assert.IsType<string>(executor.LastRunId);
        var interrupted = await store.ReadAsync(runId);
        Assert.NotNull(interrupted);
        var interruptedRoot = Assert.Single(
            interrupted!.StageInstances,
            item => item.InstanceKind == WorkflowInstanceKind.Stage);
        Assert.Equal(1, interruptedRoot.Cursor);
        Assert.Equal(
            2,
            interrupted.StageInstances.Count(item =>
                item.InstanceKind == WorkflowInstanceKind.LoopIteration));

        var recovered = await runner.RecoverAsync(
            workflow,
            runId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal(2, recovered.Output!.Value.GetProperty("value").GetInt32());
        var root = Assert.Single(
            recovered.StageInstances,
            item => item.InstanceKind == WorkflowInstanceKind.Stage);
        Assert.Equal(2, root.Cursor);
        Assert.Equal(2, recovered.Usage.LoopIterations);
        Assert.Equal(1, recovered.Usage.RecoveryCalls);
    }

    [Fact]
    public async Task PersistedCancellationStopsRecoveryAndBecomesTerminal()
    {
        var executor = new CrashThenRecoverExecutor();
        var store = new InMemoryWorkflowRunStore();
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema);
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "cancel-run",
                    "owner-a",
                    WorkflowTestData.Json("\"input\""))));

        var runId = Assert.IsType<string>(executor.LastRunId);
        var cancellation = await store.RequestCancellationAsync(
            runId,
            "user_cancelled",
            DateTimeOffset.UtcNow);
        Assert.Equal(WorkflowCancelStatus.Requested, cancellation.Status);
        Assert.True(cancellation.Snapshot!.CancellationRequested);

        var terminal = await runner.RecoverAsync(
            workflow,
            runId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Cancelled, terminal.Status);
        Assert.Equal(0, executor.RecoverCalls);
        Assert.All(
            terminal.StageInstances,
            item => Assert.Equal(
                WorkflowStageStatus.Cancelled,
                item.Status));
    }

    [Fact]
    public async Task InvalidExecutorOutputFailsClosed()
    {
        var executor = new InvalidOutputExecutor();
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema);
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new[] { executor }));

        var result = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "invalid-output",
                "owner-a",
                WorkflowTestData.Json("\"input\"")));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(WorkflowReasonCodes.SchemaMismatch, result.ReasonCode);
    }

    [Fact]
    public async Task StageExecutionLimitIsAggregateAcrossTheGraph()
    {
        var executor = new PassExecutor();
        var schema = WorkflowTestData.StringSchema();
        var first = WorkflowStageDefinition.CreateStep(
            "first",
            new WorkflowStepReference(executor.Kind),
            schema,
            schema);
        var second = WorkflowStageDefinition.CreateStep(
            "second",
            new WorkflowStepReference(executor.Kind),
            schema,
            schema,
            new[] { "first" });
        var workflow = new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "execution-limit",
                "v1",
                schema,
                schema,
                "second",
                new[] { second, first },
                new WorkflowLimits(maxStageExecutions: 1)));
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new[] { executor }));

        var result = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "limit-run",
                "owner-a",
                WorkflowTestData.Json("\"input\"")));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(WorkflowReasonCodes.LimitExceeded, result.ReasonCode);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task WorkflowDeadlineFencesANonCooperativeExecutor()
    {
        var executor = new NonCooperativeDeadlineExecutor();
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema,
            new WorkflowLimits(maxDurationMs: 100));
        var store = new InMemoryWorkflowRunStore();
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }),
            options: new WorkflowRunnerOptions(
                leaseDuration: TimeSpan.FromMilliseconds(300)));

        var execution = runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "deadline-run",
                    "owner-a",
                    WorkflowTestData.Json("\"input\"")))
            .AsTask();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(WorkflowReasonCodes.LimitExceeded, result.ReasonCode);
        var persisted = await store.ReadAsync(
            result.RunId,
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Null(persisted.Lease);

        executor.Release.TrySetResult(true);
        await executor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(executor.LateCheckpointAccepted);
    }

    [Fact]
    public async Task CancellationOwnershipIsReservedBeforeExecutorDispatch()
    {
        await WaitForConditionAsync(
            () => WorkflowCancellationDispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
        var capacity = WorkflowCancellationDispatcher.ReservationCapacity;
        var executor = new ReservationHoldingExecutor(capacity);
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema);
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new[] { executor }));
        var owners = Enumerable.Range(0, capacity)
            .Select(index => runner.ExecuteAsync(
                    workflow,
                    new WorkflowRunRequest(
                        "reservation-owner-" + index,
                        "owner-a",
                        WorkflowTestData.Json("\"input\"")))
                .AsTask())
            .ToArray();

        try
        {
            await executor.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(capacity, executor.Calls);
            Assert.Equal(
                capacity,
                WorkflowCancellationDispatcher.ActiveReservations);

            var overflow = runner.ExecuteAsync(
                    workflow,
                    new WorkflowRunRequest(
                        "reservation-overflow",
                        "owner-a",
                        WorkflowTestData.Json("\"input\"")))
                .AsTask();
            var failure = await Assert.ThrowsAsync<
                WorkflowExecutorInterruptedException>(() => overflow);

            Assert.Contains(
                "cancellation capacity",
                failure.Message,
                StringComparison.Ordinal);
            Assert.Equal(capacity, executor.Calls);
        }
        finally
        {
            executor.ReleaseAll();
        }

        var completed = await Task.WhenAll(owners)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(
            completed,
            run => Assert.Equal(WorkflowRunStatus.Completed, run.Status));
        await WaitForConditionAsync(
            () => WorkflowCancellationDispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletedInvocationCancelsOutstandingWorkflowTimers()
    {
        var executor = new ReservationHoldingExecutor(expectedCalls: 1);
        var delays = new TrackingWorkflowDelayFactory();
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema);
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new[] { executor }),
            clock: null,
            options: null,
            delayFactory: delays);
        var execution = runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "timer-cleanup",
                    "owner-a",
                    WorkflowTestData.Json("\"input\"")))
            .AsTask();

        await executor.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await delays.TwoCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        executor.ReleaseAll();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(
            () => delays.Active == 0,
            TimeSpan.FromSeconds(2));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(2, delays.Created);
        Assert.Equal(2, delays.Cancelled);
    }

    [Fact]
    public async Task DetachedInvocationKeepsExecutionTokenAliveUntilItEnds()
    {
        await WaitForConditionAsync(
            () => WorkflowCancellationDispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
        var executor = new LateRegistrationExecutor();
        var delays = new TrackingWorkflowDelayFactory();
        var schema = WorkflowTestData.StringSchema();
        var workflow = CompileSingleStepWorkflow(
            executor.Kind,
            schema,
            schema,
            new WorkflowLimits(maxDurationMs: 100));
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new[] { executor }),
            clock: null,
            options: new WorkflowRunnerOptions(
                leaseDuration: TimeSpan.FromSeconds(3)),
            delayFactory: delays);
        var execution = runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "late-token-registration",
                    "owner-a",
                    WorkflowTestData.Json("\"input\"")))
            .AsTask();

        try
        {
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
            await executor.ProbeCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await WaitForConditionAsync(
                () => delays.Active == 0,
                TimeSpan.FromSeconds(2));

            Assert.Equal(WorkflowRunStatus.Failed, result.Status);
            Assert.Equal(WorkflowReasonCodes.LimitExceeded, result.ReasonCode);
            Assert.True(executor.LateRegistrationSucceeded);
            Assert.Equal(
                1,
                WorkflowCancellationDispatcher.ActiveReservations);
            Assert.Equal(2, delays.Created);
            Assert.Equal(1, delays.Cancelled);
        }
        finally
        {
            executor.Release.TrySetResult(true);
        }

        await executor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(
            () => WorkflowCancellationDispatcher.ActiveReservations == 0,
            TimeSpan.FromSeconds(2));
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= timeout)
            {
                Assert.Fail("The expected condition was not reached in time.");
            }

            await Task.Delay(10);
        }
    }

    private static CompiledWorkflow CompileParallelWorkflow()
    {
        var seed = WorkflowTestData.SeedSchema();
        var text = WorkflowTestData.StringSchema();
        var a = WorkflowStageDefinition.CreateStep(
            "a",
            new WorkflowStepReference(
                "test/value",
                WorkflowTestData.Json("""{"value":"A"}""")),
            seed,
            text);
        var b = WorkflowStageDefinition.CreateStep(
            "b",
            new WorkflowStepReference(
                "test/value",
                WorkflowTestData.Json("""{"value":"B"}""")),
            seed,
            text);
        var reduce = WorkflowStageDefinition.CreateReduce(
            "reduce",
            new WorkflowReduceDefinition(
                new WorkflowStepReference("test/reduce")),
            WorkflowTestData.ReduceInputSchema(),
            text,
            new[] { "b", "a" });
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "parallel",
                "v1",
                seed,
                text,
                "reduce",
                new[] { reduce, b, a },
                new WorkflowLimits(maxParallelism: 2)));
    }

    private static CompiledWorkflow CompileForeachWorkflow(int maxItems)
    {
        var input = WorkflowTestData.ForeachInputSchema(maxItems);
        var item = WorkflowTestData.ForeachItemSchema();
        var text = WorkflowTestData.StringSchema();
        var output = WorkflowTestData.StringArraySchema(maxItems);
        var stage = WorkflowStageDefinition.CreateForeach(
            "map",
            new WorkflowForEachDefinition(
                new WorkflowStepReference("test/foreach-value"),
                "/items",
                "/id",
                maxItems,
                item,
                text),
            input,
            output);
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "foreach",
                "v1",
                input,
                output,
                "map",
                new[] { stage },
                new WorkflowLimits(
                    maxForeachItems: maxItems,
                    maxParallelism: 4)));
    }

    private static CompiledWorkflow CompileSingleStepWorkflow(
        string kind,
        JsonElement inputSchema,
        JsonElement outputSchema,
        WorkflowLimits? limits = null)
    {
        var stage = WorkflowStageDefinition.CreateStep(
            "only",
            new WorkflowStepReference(kind),
            inputSchema,
            outputSchema);
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "single",
                "v1",
                inputSchema,
                outputSchema,
                "only",
                new[] { stage },
                limits));
    }

    private sealed class NonCooperativeDeadlineExecutor
        : IWorkflowStepExecutor
    {
        public string Kind => "test/non-cooperative-deadline";

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool LateCheckpointAccepted { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(context, input);

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(context, input);

        private async ValueTask<WorkflowStepResult> ExecuteCoreAsync(
            WorkflowStepContext context,
            JsonElement input)
        {
            Entered.TrySetResult(true);
            await Release.Task;
            LateCheckpointAccepted = await context.SaveCheckpointAsync(
                WorkflowTestData.Json("\"late\""),
                CancellationToken.None);
            Finished.TrySetResult(true);
            return WorkflowStepResult.Completed(input);
        }
    }

    private sealed class ReservationHoldingExecutor : IWorkflowStepExecutor
    {
        private readonly int _expectedCalls;
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _releases =
            new();
        private int _calls;

        public ReservationHoldingExecutor(int expectedCalls)
        {
            _expectedCalls = expectedCalls;
        }

        public string Kind => "test/reservation-holding";

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource<bool> AllEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(input);

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(input);

        public void ReleaseAll()
        {
            foreach (var release in _releases)
            {
                release.TrySetResult(true);
            }
        }

        private async ValueTask<WorkflowStepResult> ExecuteCoreAsync(
            JsonElement input)
        {
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _releases.Add(release);
            if (Interlocked.Increment(ref _calls) == _expectedCalls)
            {
                AllEntered.TrySetResult(true);
            }

            await release.Task;
            return WorkflowStepResult.Completed(input);
        }
    }

    private sealed class TrackingWorkflowDelayFactory : IWorkflowDelayFactory
    {
        private int _active;
        private int _cancelled;
        private int _created;

        public int Active => Volatile.Read(ref _active);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int Created => Volatile.Read(ref _created);

        public TaskCompletionSource<bool> TwoCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _active);
            if (Interlocked.Increment(ref _created) >= 2)
            {
                TwoCreated.TrySetResult(true);
            }

            return ObserveAsync(delay, cancellationToken);
        }

        private async Task ObserveAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class LateRegistrationExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/late-token-registration";

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ProbeCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool LateRegistrationSucceeded { get; private set; } = true;

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(input, cancellationToken);

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteCoreAsync(input, cancellationToken);

        private async ValueTask<WorkflowStepResult> ExecuteCoreAsync(
            JsonElement input,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }

            var probe = Stopwatch.StartNew();
            while (probe.Elapsed < TimeSpan.FromMilliseconds(150))
            {
                try
                {
                    using var registration = cancellationToken.Register(
                        static () => { });
                }
                catch (ObjectDisposedException)
                {
                    LateRegistrationSucceeded = false;
                    break;
                }

                await Task.Delay(1);
            }

            ProbeCompleted.TrySetResult(true);
            await Release.Task;
            Finished.TrySetResult(true);
            return WorkflowStepResult.Completed(input);
        }
    }

    private sealed class ParallelValueExecutor : IWorkflowStepExecutor
    {
        private readonly TaskCompletionSource<bool> _bothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maxActive;

        public string Kind => "test/value";

        public int MaxActive => Volatile.Read(ref _maxActive);

        public async ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (active >= 2)
            {
                _bothStarted.TrySetResult(true);
            }

            try
            {
                await _bothStarted.Task.WaitAsync(cancellationToken);
                return WorkflowStepResult.Completed(
                    context.Settings.GetProperty("value"));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(context, input, cancellationToken);
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActive);
                if (value <= current
                    || Interlocked.CompareExchange(
                        ref _maxActive,
                        value,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ReduceExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/reduce";

        public IReadOnlyList<string> LastOrder { get; private set; } =
            Array.Empty<string>();

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            LastOrder = input
                .EnumerateArray()
                .Select(item => item.GetProperty("stageId").GetString()!)
                .ToArray();
            var value = string.Join(
                ",",
                input.EnumerateArray().Select(item =>
                    item.GetProperty("stageId").GetString()
                    + "="
                    + item.GetProperty("output").GetString()));
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(
                    WorkflowTestData.Json(
                        "\"" + value + "\"")));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(context, input, cancellationToken);
        }
    }

    private sealed class ForeachValueExecutor : IWorkflowStepExecutor
    {
        private int _calls;

        public string Kind => "test/foreach-value";

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input.GetProperty("value")));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(context, input, cancellationToken);
        }
    }

    private sealed class CrashThenRecoverExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/crash-recover";

        public string? LastRunId { get; private set; }

        public int RecoverCalls { get; private set; }

        public WorkflowStepContext? OriginalContext { get; private set; }

        public bool RecoveredCheckpoint { get; private set; }

        public async ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            LastRunId = context.RunId;
            OriginalContext = context;
            Assert.True(
                await context.SaveCheckpointAsync(
                    WorkflowTestData.Json("""{"token":"saved"}"""),
                    cancellationToken));
            throw new WorkflowExecutorInterruptedException("simulated crash");
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            RecoverCalls++;
            RecoveredCheckpoint =
                context.Checkpoint?.GetProperty("token").GetString()
                == "saved";
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input));
        }
    }

    private sealed class LoopCrashExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/loop-crash";

        public string? LastRunId { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            LastRunId = context.RunId;
            var value = input.GetProperty("value").GetInt32();
            if (value == 0)
            {
                return new ValueTask<WorkflowStepResult>(
                    WorkflowStepResult.Completed(
                        WorkflowTestData.Json(
                            """{"value":1,"done":false}""")));
            }

            throw new WorkflowExecutorInterruptedException(
                "simulated loop crash");
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(
                    WorkflowTestData.Json(
                        """{"value":2,"done":true}""")));
        }
    }

    private sealed class InvalidOutputExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/invalid-output";

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(
                    WorkflowTestData.Json("42")));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(context, input, cancellationToken);
        }
    }

    private sealed class PassExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/pass";

        public int Calls { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(context, input, cancellationToken);
        }
    }
}
