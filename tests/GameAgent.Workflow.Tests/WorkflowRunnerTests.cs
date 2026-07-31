using System.Text.Json;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class WorkflowRunnerTests
{
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
        JsonElement outputSchema)
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
                new[] { stage }));
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
