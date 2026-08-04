using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class WorkflowStoreTests
{
    [Fact]
    public async Task ExpiredLeaseTakeoverAdvancesFenceAndRejectsOldOwner()
    {
        var executor = new InterruptingExecutor();
        var store = new InMemoryWorkflowRunStore();
        var schema = WorkflowTestData.StringSchema();
        var stage = WorkflowStageDefinition.CreateStep(
            "only",
            new WorkflowStepReference(executor.Kind),
            schema,
            schema);
        var workflow = new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "lease",
                "v1",
                schema,
                schema,
                "only",
                new[] { stage }));
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "lease-run",
                    "bootstrap",
                    WorkflowTestData.Json("\"input\"")), cancellationToken: TestContext.Current.CancellationToken));

        var runId = Assert.IsType<string>(executor.RunId);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = await store.TryAcquireLeaseAsync(
            runId,
            "owner-a",
            TimeSpan.FromSeconds(1),
            start, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowLeaseAcquireStatus.Acquired, first.Status);

        Assert.True(
            await store.RenewLeaseAsync(
                runId,
                first.Token!,
                TimeSpan.FromSeconds(1),
                start.AddMilliseconds(500), cancellationToken: TestContext.Current.CancellationToken));

        var busy = await store.TryAcquireLeaseAsync(
            runId,
            "owner-b",
            TimeSpan.FromSeconds(1),
            start.AddMilliseconds(1_200), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowLeaseAcquireStatus.Busy, busy.Status);

        var takeover = await store.TryAcquireLeaseAsync(
            runId,
            "owner-b",
            TimeSpan.FromSeconds(1),
            start.AddMilliseconds(1_600), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowLeaseAcquireStatus.Acquired, takeover.Status);
        Assert.True(
            takeover.Token!.FencingEpoch
            > first.Token!.FencingEpoch);

        var snapshot = await store.ReadAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var restored = Restore(snapshot!);
        Assert.Equal(snapshot.Revision, restored.Revision);
        Assert.Equal(snapshot.FencingEpoch, restored.FencingEpoch);
        Assert.Equal(
            snapshot.StageInstances[0].InputDigest,
            restored.StageInstances[0].InputDigest);
        var staleCommit = await store.TryCommitAsync(
            runId,
            snapshot!.Revision,
            first.Token,
            snapshot.Copy(),
            start.AddMilliseconds(1_600), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowCommitStatus.LeaseLost, staleCommit.Status);
    }

    [Fact]
    public async Task CancellationIntentIsAtomicAndIdempotent()
    {
        var executor = new InterruptingExecutor();
        var store = new InMemoryWorkflowRunStore();
        var schema = WorkflowTestData.StringSchema();
        var workflow = new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "cancel-store",
                "v1",
                schema,
                schema,
                "only",
                new[]
                {
                    WorkflowStageDefinition.CreateStep(
                        "only",
                        new WorkflowStepReference(executor.Kind),
                        schema,
                        schema)
                }));
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "cancel-store-run",
                    "owner",
                    WorkflowTestData.Json("\"input\"")), cancellationToken: TestContext.Current.CancellationToken));

        var first = await store.RequestCancellationAsync(
            executor.RunId!,
            "user_cancelled",
            DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken);
        var second = await store.RequestCancellationAsync(
            executor.RunId!,
            "user_cancelled",
            DateTimeOffset.UtcNow.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowCancelStatus.Requested, first.Status);
        Assert.Equal(WorkflowCancelStatus.AlreadyRequested, second.Status);
        Assert.Equal(first.Snapshot!.Revision, second.Snapshot!.Revision);
        Assert.True(second.Snapshot.CancellationRequested);
        Assert.Equal(
            WorkflowRunStatus.CancelRequested,
            second.Snapshot.Status);
    }

    private static WorkflowRunSnapshot Restore(WorkflowRunSnapshot value)
    {
        var stages = value.StageInstances.Select(stage =>
            WorkflowStageInstanceSnapshot.Restore(
                stage.InstanceId,
                stage.StageId,
                stage.InstanceKind,
                stage.ParentInstanceId,
                stage.ItemIdentityDigest,
                stage.ItemOrdinal,
                stage.LoopIteration,
                stage.Status,
                stage.Attempt,
                stage.Generation,
                stage.RecoveryAttempts,
                stage.Cursor,
                stage.Input,
                stage.InputDigest,
                stage.Output,
                stage.OutputDigest,
                stage.Checkpoint,
                stage.CheckpointDigest,
                stage.ReasonCode,
                stage.UpdatedAt));
        return WorkflowRunSnapshot.Restore(
            value.RunId,
            value.WorkflowId,
            value.WorkflowVersion,
            value.DefinitionDigest,
            value.Input,
            value.InputDigest,
            value.Revision,
            value.Status,
            value.ReasonCode,
            value.CancellationRequested,
            value.CancellationReason,
            value.Output,
            value.OutputDigest,
            value.CreatedAt,
            value.UpdatedAt,
            value.FencingEpoch,
            value.Lease,
            value.Usage,
            stages);
    }

    private sealed class InterruptingExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/store-interrupt";

        public string? RunId { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            System.Text.Json.JsonElement input,
            CancellationToken cancellationToken)
        {
            RunId = context.RunId;
            throw new WorkflowExecutorInterruptedException("interrupted");
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            System.Text.Json.JsonElement input,
            CancellationToken cancellationToken)
        {
            throw new WorkflowExecutorInterruptedException("interrupted");
        }
    }
}
