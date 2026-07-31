using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class FileWorkflowRunStoreTests
{
    [Fact]
    public async Task CreateDuplicateAndReopenPreserveStableBytes()
    {
        using var directory = new TemporaryWorkflowDirectory();
        using var independentDirectory =
            new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial(
            "create-reopen",
            WorkflowTestData.Json("""{"b":2,"a":1}"""));
        var equivalentSnapshot = SnapshotFactory.CreateInitial(
            "create-reopen",
            WorkflowTestData.Json("""{"a":1,"b":2}"""));
        var store = CreateStore(directory.Path);

        var created = await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var firstBytes = File.ReadAllBytes(path);
        var duplicate = await store.CreateAsync(snapshot.Copy());
        var secondBytes = File.ReadAllBytes(path);
        var reopened = await CreateStore(directory.Path)
            .ReadAsync(snapshot.RunId);
        var independentStore = CreateStore(independentDirectory.Path);
        Assert.Equal(snapshot.RunId, equivalentSnapshot.RunId);
        await independentStore.CreateAsync(equivalentSnapshot);
        var independentBytes = File.ReadAllBytes(
            independentStore.GetRunFilePath(snapshot.RunId));

        Assert.Equal(WorkflowCreateStatus.Created, created.Status);
        Assert.Equal(
            WorkflowCreateStatus.AlreadyExists,
            duplicate.Status);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(firstBytes, independentBytes);
        Assert.NotNull(reopened);
        Assert.Equal(snapshot.RunId, reopened!.RunId);
        Assert.Equal(snapshot.InputDigest, reopened.InputDigest);
        Assert.Equal(0, reopened.Revision);
    }

    [Fact]
    public async Task StartedCheckpointRecoversAfterStoreReopen()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var executor = new FileCrashExecutor();
        var workflow = SnapshotFactory.CompileSingle(executor.Kind);
        var firstStore = CreateStore(directory.Path);
        var firstRunner = new WorkflowRunner(
            firstStore,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await firstRunner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "file-crash",
                    "owner-a",
                    WorkflowTestData.Json("\"input\""))));

        var runId = Assert.IsType<string>(executor.RunId);
        var persisted = await CreateStore(directory.Path).ReadAsync(runId);
        Assert.NotNull(persisted);
        var started = Assert.Single(persisted!.StageInstances);
        Assert.Equal(WorkflowStageStatus.Started, started.Status);
        Assert.Equal(
            "durable",
            started.Checkpoint!.Value.GetProperty("token").GetString());

        var secondRunner = new WorkflowRunner(
            CreateStore(directory.Path),
            new WorkflowStepExecutorRegistry(new[] { executor }));
        var recovered = await secondRunner.RecoverAsync(
            workflow,
            runId,
            "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal("input", recovered.Output!.Value.GetString());
        Assert.Equal(1, executor.ExecuteCalls);
        Assert.Equal(1, executor.RecoverCalls);
        Assert.True(executor.RecoveredCheckpoint);
    }

    [Fact]
    public async Task LoopCursorSurvivesReopen()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var executor = new FileLoopExecutor();
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
                "file-loop",
                "v1",
                schema,
                schema,
                "loop",
                new[] { stage }));
        var runner = new WorkflowRunner(
            CreateStore(directory.Path),
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "file-loop",
                    "owner-a",
                    WorkflowTestData.Json(
                        """{"value":0,"done":false}"""))));

        var interrupted = await CreateStore(directory.Path)
            .ReadAsync(executor.RunId!);
        Assert.NotNull(interrupted);
        var interruptedRoot = Assert.Single(
            interrupted!.StageInstances,
            item => item.InstanceKind == WorkflowInstanceKind.Stage);
        Assert.Equal(1, interruptedRoot.Cursor);

        var recovered = await new WorkflowRunner(
                CreateStore(directory.Path),
                new WorkflowStepExecutorRegistry(new[] { executor }))
            .RecoverAsync(
                workflow,
                executor.RunId!,
                "owner-b");

        var root = Assert.Single(
            recovered.StageInstances,
            item => item.InstanceKind == WorkflowInstanceKind.Stage);
        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal(2, root.Cursor);
        Assert.Equal(2, recovered.Output!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PersistedCancellationSurvivesReopen()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var executor = new FileCrashExecutor(saveCheckpoint: false);
        var workflow = SnapshotFactory.CompileSingle(executor.Kind);
        var store = CreateStore(directory.Path);
        var runner = new WorkflowRunner(
            store,
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "file-cancel",
                    "owner-a",
                    WorkflowTestData.Json("\"input\""))));
        var requested = await CreateStore(directory.Path)
            .RequestCancellationAsync(
                executor.RunId!,
                "user_cancelled",
                DateTimeOffset.UtcNow);
        Assert.Equal(WorkflowCancelStatus.Requested, requested.Status);

        var terminal = await new WorkflowRunner(
                CreateStore(directory.Path),
                new WorkflowStepExecutorRegistry(new[] { executor }))
            .RecoverAsync(
                workflow,
                executor.RunId!,
                "owner-b");

        Assert.Equal(WorkflowRunStatus.Cancelled, terminal.Status);
        Assert.Equal(0, executor.RecoverCalls);
    }

    [Fact]
    public async Task LeaseRenewTakeoverFenceAndCasRaceMatchMemorySemantics()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("lease-cas");
        var firstStore = CreateStore(directory.Path);
        var secondStore = CreateStore(directory.Path);
        Assert.Equal(
            WorkflowCreateStatus.Created,
            (await firstStore.CreateAsync(snapshot)).Status);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = await firstStore.TryAcquireLeaseAsync(
            snapshot.RunId,
            "owner-a",
            TimeSpan.FromSeconds(1),
            start);
        Assert.True(
            await firstStore.RenewLeaseAsync(
                snapshot.RunId,
                first.Token!,
                TimeSpan.FromSeconds(1),
                start.AddMilliseconds(500)));
        Assert.Equal(
            WorkflowLeaseAcquireStatus.Busy,
            (await secondStore.TryAcquireLeaseAsync(
                snapshot.RunId,
                "owner-b",
                TimeSpan.FromSeconds(1),
                start.AddMilliseconds(1_200))).Status);

        var takeover = await secondStore.TryAcquireLeaseAsync(
            snapshot.RunId,
            "owner-b",
            TimeSpan.FromSeconds(2),
            start.AddMilliseconds(1_600));
        Assert.True(
            takeover.Token!.FencingEpoch
            > first.Token!.FencingEpoch);
        var current = await secondStore.ReadAsync(snapshot.RunId);
        Assert.NotNull(current);
        var replacement = SnapshotFactory.WithRevision(
            current!,
            current.Revision + 1,
            WorkflowRunStatus.Running);
        var stale = await firstStore.TryCommitAsync(
            snapshot.RunId,
            current.Revision,
            first.Token,
            replacement,
            start.AddMilliseconds(1_700));
        Assert.Equal(WorkflowCommitStatus.LeaseLost, stale.Status);

        var leftTask = firstStore.TryCommitAsync(
            snapshot.RunId,
            current.Revision,
            takeover.Token,
            replacement,
            start.AddMilliseconds(1_700)).AsTask();
        var rightTask = secondStore.TryCommitAsync(
            snapshot.RunId,
            current.Revision,
            takeover.Token,
            replacement,
            start.AddMilliseconds(1_700)).AsTask();
        var results = await Task.WhenAll(leftTask, rightTask);

        Assert.Single(
            results,
            item => item.Status == WorkflowCommitStatus.Committed);
        Assert.Single(
            results,
            item => item.Status == WorkflowCommitStatus.RevisionConflict);
    }

    [Fact]
    public async Task LostCommitAcknowledgementRetriesAsRevisionConflict()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("commit-ack");
        var injector = new SequenceFaultInjector(
            WorkflowFileStoreFaultPoint
                .AfterCommitFlushBeforeAcknowledge,
            frameSequence: 3);
        var faultedStore = CreateStore(
            directory.Path,
            faultInjector: injector);
        await faultedStore.CreateAsync(snapshot);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var lease = await faultedStore.TryAcquireLeaseAsync(
            snapshot.RunId,
            "owner",
            TimeSpan.FromSeconds(10),
            start);
        var current = await faultedStore.ReadAsync(snapshot.RunId);
        var replacement = SnapshotFactory.WithRevision(
            current!,
            current!.Revision + 1,
            WorkflowRunStatus.Running);

        await Assert.ThrowsAsync<InjectedStoreFaultException>(
            async () => await faultedStore.TryCommitAsync(
                snapshot.RunId,
                current.Revision,
                lease.Token!,
                replacement,
                start.AddMilliseconds(1)));
        var path = faultedStore.GetRunFilePath(snapshot.RunId);
        var committedBytes = File.ReadAllBytes(path);

        var retry = await CreateStore(directory.Path).TryCommitAsync(
            snapshot.RunId,
            current.Revision,
            lease.Token!,
            replacement,
            start.AddMilliseconds(2));

        Assert.Equal(
            WorkflowCommitStatus.RevisionConflict,
            retry.Status);
        Assert.Equal(1, retry.Snapshot!.Revision);
        Assert.Equal(committedBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task TornTailIsIgnoredAndTruncatedByNextMutation()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("torn-tail");
        var store = CreateStore(directory.Path);
        await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var committedLength = new FileInfo(path).Length;
        using (var stream = new FileStream(
                   path,
                   FileMode.Append,
                   FileAccess.Write,
                   FileShare.ReadWrite))
        {
            stream.Write(
                Encoding.ASCII.GetBytes("partial-frame"),
                0,
                "partial-frame".Length);
            stream.Flush(flushToDisk: true);
        }

        var reopened = CreateStore(directory.Path);
        Assert.NotNull(await reopened.ReadAsync(snapshot.RunId));
        Assert.True(new FileInfo(path).Length > committedLength);
        var lease = await reopened.TryAcquireLeaseAsync(
            snapshot.RunId,
            "owner",
            TimeSpan.FromSeconds(10),
            DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowLeaseAcquireStatus.Acquired, lease.Status);
        Assert.True(new FileInfo(path).Length > committedLength);
        Assert.False(
            ContainsSequence(
                File.ReadAllBytes(path),
                Encoding.ASCII.GetBytes("partial-frame")));
        Assert.NotNull(await CreateStore(directory.Path)
            .ReadAsync(snapshot.RunId));
    }

    [Fact]
    public async Task CommittedChecksumCorruptionFailsClosed()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("checksum");
        var store = CreateStore(directory.Path);
        await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var bytes = File.ReadAllBytes(path);
        bytes[WorkflowRunLogTestConstants.PayloadOffset] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        var exception =
            await Assert.ThrowsAsync<WorkflowFileStoreCorruptionException>(
                async () => await CreateStore(directory.Path)
                    .ReadAsync(snapshot.RunId));

        Assert.Equal(
            WorkflowFileStoreReasonCodes.CorruptCommittedFrame,
            exception.ReasonCode);
    }

    [Fact]
    public async Task CommittedPrefixCorruptionFailsClosed()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("prefix-checksum");
        var store = CreateStore(directory.Path);
        await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var bytes = File.ReadAllBytes(path);
        bytes[WorkflowRunLogTestConstants.PayloadLengthOffset + 3] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        var exception =
            await Assert.ThrowsAsync<WorkflowFileStoreCorruptionException>(
                async () => await CreateStore(directory.Path)
                    .ReadAsync(snapshot.RunId));

        Assert.Equal(
            WorkflowFileStoreReasonCodes.CorruptCommittedFrame,
            exception.ReasonCode);
    }

    [Fact]
    public async Task UnknownFileVersionFailsClosed()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("version");
        var store = CreateStore(directory.Path);
        await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var bytes = File.ReadAllBytes(path);
        bytes[8] = 2;
        File.WriteAllBytes(path, bytes);

        var exception =
            await Assert.ThrowsAsync<WorkflowFileStoreCorruptionException>(
                async () => await CreateStore(directory.Path)
                    .ReadAsync(snapshot.RunId));

        Assert.Equal(
            WorkflowFileStoreReasonCodes.UnsupportedVersion,
            exception.ReasonCode);
    }

    [Fact]
    public async Task UnknownSnapshotSchemaVersionFailsClosed()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("snapshot-version");
        var store = CreateStore(directory.Path);
        await store.CreateAsync(snapshot);
        var path = store.GetRunFilePath(snapshot.RunId);
        var bytes = File.ReadAllBytes(path);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(
                WorkflowRunLogTestConstants.PayloadLengthOffset,
                sizeof(int)));
        var versionToken = Encoding.UTF8.GetBytes("\"version\":1");
        var tokenOffset = IndexOfSequence(
            bytes,
            versionToken,
            WorkflowRunLogTestConstants.PayloadOffset,
            payloadLength);
        Assert.True(tokenOffset >= 0);
        bytes[tokenOffset + versionToken.Length - 1] =
            (byte)'2';
        using (var sha = SHA256.Create())
        {
            var checksum = sha.ComputeHash(
                bytes,
                WorkflowRunLogTestConstants.PayloadOffset,
                payloadLength);
            var footerOffset =
                WorkflowRunLogTestConstants.PayloadOffset
                + payloadLength;
            checksum.CopyTo(
                bytes,
                footerOffset
                + WorkflowRunLogTestConstants.FooterChecksumOffset);
        }

        File.WriteAllBytes(path, bytes);
        var exception =
            await Assert.ThrowsAsync<WorkflowFileStoreCorruptionException>(
                async () => await CreateStore(directory.Path)
                    .ReadAsync(snapshot.RunId));

        Assert.Equal(
            WorkflowFileStoreReasonCodes.UnsupportedVersion,
            exception.ReasonCode);
    }

    [Fact]
    public async Task ExclusiveWriterHonorsTimeoutAndCancellation()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial("exclusive");
        await CreateStore(directory.Path).CreateAsync(snapshot);
        var blocker = new BlockingFaultInjector(
            WorkflowFileStoreFaultPoint.BeforeFrameWrite);
        var blockingStore = CreateStore(
            directory.Path,
            faultInjector: blocker,
            lockTimeout: TimeSpan.FromSeconds(3));
        var blockedMutation = Task.Run(async () =>
            await blockingStore.TryAcquireLeaseAsync(
                snapshot.RunId,
                "owner-a",
                TimeSpan.FromSeconds(10),
                DateTimeOffset.UtcNow));
        Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(2)));

        var shortStore = CreateStore(
            directory.Path,
            lockTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<WorkflowFileStoreLockTimeoutException>(
            async () => await shortStore.ReadAsync(snapshot.RunId));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CreateStore(
                    directory.Path,
                    lockTimeout: TimeSpan.FromSeconds(3))
                .ReadAsync(snapshot.RunId, cancellation.Token));

        blocker.Release.Set();
        Assert.Equal(
            WorkflowLeaseAcquireStatus.Acquired,
            (await blockedMutation).Status);
    }

    [Fact]
    public async Task CountOperationSnapshotAndFileLimitsLeaveNoPartialState()
    {
        using var countDirectory = new TemporaryWorkflowDirectory();
        var countStore = CreateStore(countDirectory.Path, maxRuns: 1);
        var first = SnapshotFactory.CreateInitial("count-1");
        var second = SnapshotFactory.CreateInitial("count-2");
        Assert.Equal(
            WorkflowCreateStatus.Created,
            (await countStore.CreateAsync(first)).Status);
        Assert.Equal(
            WorkflowCreateStatus.CapacityExceeded,
            (await countStore.CreateAsync(second)).Status);
        Assert.Null(await countStore.ReadAsync(second.RunId));
        Assert.False(
            File.Exists(countStore.GetRunFilePath(second.RunId)));

        using var operationDirectory = new TemporaryWorkflowDirectory();
        var operationStore = CreateStore(
            operationDirectory.Path,
            maxOperations: 1);
        var operationSnapshot =
            SnapshotFactory.CreateInitial("operation-limit");
        await operationStore.CreateAsync(operationSnapshot);
        await Assert.ThrowsAsync<WorkflowFileStoreCapacityException>(
            async () => await operationStore.TryAcquireLeaseAsync(
                operationSnapshot.RunId,
                "owner",
                TimeSpan.FromSeconds(1),
                DateTimeOffset.UtcNow));
        Assert.Null(
            (await operationStore.ReadAsync(operationSnapshot.RunId))!
                .Lease);

        using var sizeDirectory = new TemporaryWorkflowDirectory();
        var largeInput = new string('x', 4_000);
        var oversized = SnapshotFactory.CreateInitial(
            "snapshot-limit",
            WorkflowTestData.Json("\"" + largeInput + "\""));
        var sizeStore = CreateStore(
            sizeDirectory.Path,
            maxSnapshotBytes: 1_024,
            maxFrameBytes: 1_136,
            maxFileBytes: 1_184,
            maxRootBytes: 1_184);
        await Assert.ThrowsAsync<WorkflowFileStoreCapacityException>(
            async () => await sizeStore.CreateAsync(oversized));
        Assert.Null(await sizeStore.ReadAsync(oversized.RunId));
        Assert.False(
            File.Exists(sizeStore.GetRunFilePath(oversized.RunId)));

        using var fileDirectory = new TemporaryWorkflowDirectory();
        const long maxFileBytes = 4_256;
        var fileStore = CreateStore(
            fileDirectory.Path,
            maxSnapshotBytes: 4_096,
            maxFrameBytes: 4_208,
            maxFileBytes: maxFileBytes,
            maxRootBytes: 8_192);
        var fileSnapshot =
            SnapshotFactory.CreateInitial("file-size-limit");
        await fileStore.CreateAsync(fileSnapshot);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var lease = await fileStore.TryAcquireLeaseAsync(
            fileSnapshot.RunId,
            "owner",
            TimeSpan.FromSeconds(10),
            start);
        Assert.Equal(WorkflowLeaseAcquireStatus.Acquired, lease.Status);
        var exhausted = false;
        for (var index = 1; index <= 10 && !exhausted; index++)
        {
            try
            {
                Assert.True(
                    await fileStore.RenewLeaseAsync(
                        fileSnapshot.RunId,
                        lease.Token!,
                        TimeSpan.FromSeconds(10),
                        start.AddMilliseconds(index)));
            }
            catch (WorkflowFileStoreCapacityException)
            {
                exhausted = true;
            }
        }

        Assert.True(exhausted);
        Assert.InRange(
            new FileInfo(
                fileStore.GetRunFilePath(fileSnapshot.RunId)).Length,
            1,
            maxFileBytes);
        Assert.NotNull(await fileStore.ReadAsync(fileSnapshot.RunId));
    }

    [Theory]
    [InlineData(WorkflowFileStoreFaultPoint.BeforeFrameWrite, false)]
    [InlineData(WorkflowFileStoreFaultPoint.AfterFramePrefixWrite, false)]
    [InlineData(WorkflowFileStoreFaultPoint.AfterFramePayloadWrite, false)]
    [InlineData(WorkflowFileStoreFaultPoint.BeforePayloadFlush, false)]
    [InlineData(
        WorkflowFileStoreFaultPoint.AfterPayloadFlushBeforeCommitMarker,
        false)]
    [InlineData(
        WorkflowFileStoreFaultPoint.AfterCommitMarkerWriteBeforeFlush,
        true)]
    [InlineData(
        WorkflowFileStoreFaultPoint.AfterCommitFlushBeforeAcknowledge,
        true)]
    public async Task FaultedCreateRetryNeverDuplicatesCommittedState(
        WorkflowFileStoreFaultPoint point,
        bool mayAlreadyBeCommitted)
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial(
            "fault-" + (int)point);
        var injector = new ThrowOnceFaultInjector(point);
        var faulted = CreateStore(
            directory.Path,
            faultInjector: injector);

        await Assert.ThrowsAsync<InjectedStoreFaultException>(
            async () => await faulted.CreateAsync(snapshot));
        var recovered = await CreateStore(directory.Path)
            .ReadAsync(snapshot.RunId);
        if (!mayAlreadyBeCommitted)
        {
            Assert.Null(recovered);
        }

        var retry = await CreateStore(directory.Path)
            .CreateAsync(snapshot);
        Assert.Contains(
            retry.Status,
            new[]
            {
                WorkflowCreateStatus.Created,
                WorkflowCreateStatus.AlreadyExists
            });
        var terminal = await CreateStore(directory.Path)
            .ReadAsync(snapshot.RunId);
        Assert.NotNull(terminal);
        Assert.Equal(0, terminal!.Revision);
    }

    [Fact]
    public async Task LostStartedAcknowledgementRecoversWithoutExecuteReplay()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var injector = new SequenceFaultInjector(
            WorkflowFileStoreFaultPoint
                .AfterCommitFlushBeforeAcknowledge,
            frameSequence: 4);
        var executor = new RecoverOnlyExecutor();
        var workflow = SnapshotFactory.CompileSingle(executor.Kind);
        var runner = new WorkflowRunner(
            CreateStore(directory.Path, faultInjector: injector),
            new WorkflowStepExecutorRegistry(new[] { executor }));

        await Assert.ThrowsAsync<InjectedStoreFaultException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    "lost-started-ack",
                    "owner-a",
                    WorkflowTestData.Json("\"input\""))));
        Assert.Equal(0, executor.ExecuteCalls);

        var runId = WorkflowIdentity.CreateRunId(
            workflow.DefinitionDigest,
            WorkflowIdentity.ComputeJsonDigest(
                WorkflowTestData.Json("\"input\"")),
            "lost-started-ack");
        var persisted = await CreateStore(directory.Path).ReadAsync(runId);
        Assert.NotNull(persisted);
        Assert.Equal(
            WorkflowStageStatus.Started,
            Assert.Single(persisted!.StageInstances).Status);

        var recovered = await new WorkflowRunner(
                CreateStore(directory.Path),
                new WorkflowStepExecutorRegistry(new[] { executor }))
            .RecoverAsync(workflow, runId, "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal(0, executor.ExecuteCalls);
        Assert.Equal(1, executor.RecoverCalls);
    }

    [Fact]
    public async Task RestoreEnumeratesLyingStageCollection()
    {
        using var directory = new TemporaryWorkflowDirectory();
        var snapshot = SnapshotFactory.CreateInitial(
            "lying-restore",
            stagesAsLyingCollection: true);
        var store = CreateStore(directory.Path);

        await store.CreateAsync(snapshot);
        var reopened = await CreateStore(directory.Path)
            .ReadAsync(snapshot.RunId);

        Assert.NotNull(reopened);
        Assert.Single(reopened!.StageInstances);
    }

    private static bool ContainsSequence(
        byte[] source,
        byte[] value)
    {
        return IndexOfSequence(source, value, 0, source.Length) >= 0;
    }

    private static int IndexOfSequence(
        byte[] source,
        byte[] value,
        int offset,
        int count)
    {
        if (value.Length == 0)
        {
            return offset;
        }

        var end = checked(offset + count - value.Length);
        for (var index = offset; index <= end; index++)
        {
            if (source.AsSpan(index, value.Length)
                .SequenceEqual(value))
            {
                return index;
            }
        }

        return -1;
    }

    private static FileWorkflowRunStore CreateStore(
        string root,
        int maxRuns = 128,
        int maxOperations = 1_024,
        int maxSnapshotBytes = 1_048_576,
        int maxFrameBytes = 1_048_688,
        long maxFileBytes = 67_108_864,
        long maxRootBytes = 134_217_728,
        TimeSpan? lockTimeout = null,
        IWorkflowFileStoreFaultInjector? faultInjector = null)
    {
        return new FileWorkflowRunStore(
            new FileWorkflowRunStoreOptions(
                root,
                maxRuns,
                maxOperations,
                maxSnapshotBytes,
                maxFrameBytes,
                maxFileBytes,
                maxRootBytes,
                maxStageInstancesPerRun: 10_000,
                lockTimeout: lockTimeout,
                lockRetryDelay: TimeSpan.FromMilliseconds(5),
                faultInjector: faultInjector));
    }

    private sealed class FileCrashExecutor : IWorkflowStepExecutor
    {
        private readonly bool _saveCheckpoint;

        public FileCrashExecutor(bool saveCheckpoint = true)
        {
            _saveCheckpoint = saveCheckpoint;
        }

        public string Kind => "test/file-crash";

        public string? RunId { get; private set; }

        public int ExecuteCalls { get; private set; }

        public int RecoverCalls { get; private set; }

        public bool RecoveredCheckpoint { get; private set; }

        public async ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            RunId = context.RunId;
            ExecuteCalls++;
            if (_saveCheckpoint)
            {
                Assert.True(
                    await context.SaveCheckpointAsync(
                        WorkflowTestData.Json(
                            """{"token":"durable"}"""),
                        cancellationToken));
            }

            throw new WorkflowExecutorInterruptedException("crash");
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            RecoverCalls++;
            RecoveredCheckpoint =
                context.Checkpoint?.GetProperty("token").GetString()
                == "durable";
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input));
        }
    }

    private sealed class FileLoopExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/file-loop";

        public string? RunId { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            RunId = context.RunId;
            if (input.GetProperty("value").GetInt32() == 0)
            {
                return new ValueTask<WorkflowStepResult>(
                    WorkflowStepResult.Completed(
                        WorkflowTestData.Json(
                            """{"value":1,"done":false}""")));
            }

            throw new WorkflowExecutorInterruptedException("loop crash");
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

    private sealed class RecoverOnlyExecutor : IWorkflowStepExecutor
    {
        public string Kind => "test/recover-only";

        public int ExecuteCalls { get; private set; }

        public int RecoverCalls { get; private set; }

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            RecoverCalls++;
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(input));
        }
    }

    private sealed class ThrowOnceFaultInjector
        : IWorkflowFileStoreFaultInjector
    {
        private readonly WorkflowFileStoreFaultPoint _point;
        private int _thrown;

        public ThrowOnceFaultInjector(
            WorkflowFileStoreFaultPoint point)
        {
            _point = point;
        }

        public void OnFaultPoint(
            WorkflowFileStoreFaultPoint point,
            string runId,
            long frameSequence)
        {
            if (point == _point
                && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InjectedStoreFaultException();
            }
        }
    }

    private sealed class SequenceFaultInjector
        : IWorkflowFileStoreFaultInjector
    {
        private readonly WorkflowFileStoreFaultPoint _point;
        private readonly long _frameSequence;
        private int _thrown;

        public SequenceFaultInjector(
            WorkflowFileStoreFaultPoint point,
            long frameSequence)
        {
            _point = point;
            _frameSequence = frameSequence;
        }

        public void OnFaultPoint(
            WorkflowFileStoreFaultPoint point,
            string runId,
            long frameSequence)
        {
            if (point == _point
                && frameSequence == _frameSequence
                && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InjectedStoreFaultException();
            }
        }
    }

    private sealed class BlockingFaultInjector
        : IWorkflowFileStoreFaultInjector
    {
        private readonly WorkflowFileStoreFaultPoint _point;

        public BlockingFaultInjector(
            WorkflowFileStoreFaultPoint point)
        {
            _point = point;
        }

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void OnFaultPoint(
            WorkflowFileStoreFaultPoint point,
            string runId,
            long frameSequence)
        {
            if (point != _point)
            {
                return;
            }

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test blocker timed out.");
            }
        }
    }

    private sealed class InjectedStoreFaultException : Exception
    {
    }
}

internal static class SnapshotFactory
{
    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    public static CompiledWorkflow CompileSingle(string executorKind)
    {
        var schema = WorkflowTestData.StringSchema(8_192);
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "file-single",
                "v1",
                schema,
                schema,
                "only",
                new[]
                {
                    WorkflowStageDefinition.CreateStep(
                        "only",
                        new WorkflowStepReference(executorKind),
                        schema,
                        schema)
                }));
    }

    public static WorkflowRunSnapshot CreateInitial(
        string runKey,
        JsonElement? input = null,
        bool stagesAsLyingCollection = false)
    {
        var workflow = CompileSingle("test/file-snapshot");
        var actualInput = input
                          ?? WorkflowTestData.Json("\"input\"");
        var inputDigest =
            WorkflowIdentity.ComputeJsonDigest(actualInput);
        var runId = WorkflowIdentity.CreateRunId(
            workflow.DefinitionDigest,
            inputDigest,
            runKey);
        var root = WorkflowStageInstanceSnapshot.Restore(
            WorkflowIdentity.CreateStageInstanceId(runId, "only"),
            "only",
            WorkflowInstanceKind.Stage,
            null,
            null,
            null,
            null,
            WorkflowStageStatus.Pending,
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Timestamp);
        IEnumerable<WorkflowStageInstanceSnapshot> stages =
            stagesAsLyingCollection
                ? new LyingCollection<WorkflowStageInstanceSnapshot>(root)
                : new[] { root };
        return WorkflowRunSnapshot.Restore(
            runId,
            workflow.Definition.Id,
            workflow.Definition.Version,
            workflow.DefinitionDigest,
            actualInput,
            inputDigest,
            0,
            WorkflowRunStatus.Pending,
            null,
            false,
            null,
            null,
            null,
            Timestamp,
            Timestamp,
            0,
            null,
            new WorkflowUsage(),
            stages);
    }

    public static WorkflowRunSnapshot WithRevision(
        WorkflowRunSnapshot value,
        long revision,
        WorkflowRunStatus status)
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
            revision,
            status,
            value.ReasonCode,
            value.CancellationRequested,
            value.CancellationReason,
            value.Output,
            value.OutputDigest,
            value.CreatedAt,
            value.UpdatedAt.AddMilliseconds(1),
            value.FencingEpoch,
            value.Lease,
            value.Usage,
            stages);
    }
}

internal sealed class TemporaryWorkflowDirectory : IDisposable
{
    public TemporaryWorkflowDirectory()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-workflow-tests");
        Directory.CreateDirectory(root);
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class WorkflowRunLogTestConstants
{
    private const int HeaderBytes = 48;
    private const int FramePrefixBytes = 60;

    public const int PayloadOffset = HeaderBytes + FramePrefixBytes;

    public const int PayloadLengthOffset = HeaderBytes + 24;

    public const int FooterChecksumOffset = 20;
}
