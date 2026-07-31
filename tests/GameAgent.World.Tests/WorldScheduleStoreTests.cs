using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldScheduleStoreTests
{
    [Fact]
    public async Task InMemoryLifecycleIsCasIdempotentAndOrdered()
    {
        var snapshot = Snapshot();
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var scope = snapshot.Coordinate.Scope;
        var b = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "create-b",
                Intent(scope, "b", 10)),
            CancellationToken.None);
        var a = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "create-a",
                Intent(scope, "a", 10)),
            CancellationToken.None);
        var c = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "create-c",
                Intent(scope, "c", 5)),
            CancellationToken.None);
        Assert.True(a.Applied);
        Assert.True(b.Applied);
        Assert.True(c.Applied);

        var firstPage = await store.QueryDueAsync(
            new WorldScheduleDueQuery(
                scope,
                "turn",
                throughTick: 10,
                maximumResults: 2),
            CancellationToken.None);
        Assert.Equal(
            new[] { "c", "a" },
            firstPage.Items.Select(item => item.ScheduleId));
        Assert.NotNull(firstPage.Next);
        var secondPage = await store.QueryDueAsync(
            new WorldScheduleDueQuery(
                scope,
                "turn",
                throughTick: 10,
                maximumResults: 2,
                firstPage.Next),
            CancellationToken.None);
        Assert.Equal(
            new[] { "b" },
            secondPage.Items.Select(item => item.ScheduleId));
        Assert.Null(secondPage.Next);

        var originalOccurrence = b.Schedule!.OccurrenceId;
        var reschedule = WorldScheduleCommand.Reschedule(
            "reschedule-b",
            scope,
            "b",
            expectedGeneration: 0,
            new GameTimePoint("turn", "main", 1, 2));
        var changed = await store.ExecuteAsync(
            reschedule,
            CancellationToken.None);
        var replay = await store.ExecuteAsync(
            reschedule,
            CancellationToken.None);
        Assert.True(changed.Applied);
        Assert.False(changed.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(
            changed.Receipt!.ReceiptId,
            replay.Receipt!.ReceiptId);
        Assert.Equal(1, changed.Schedule!.Generation);
        Assert.NotEqual(
            originalOccurrence,
            changed.Schedule.OccurrenceId);

        var stale = WorldScheduleCommand.Reschedule(
            "stale-reschedule",
            scope,
            "b",
            expectedGeneration: 0,
            new GameTimePoint("turn", "main", 1, 3));
        var staleResult = await store.ExecuteAsync(
            stale,
            CancellationToken.None);
        Assert.False(staleResult.Applied);
        Assert.Equal(
            WorldScheduleReasonCodes.GenerationMismatch,
            staleResult.ReasonCode);
        var staleReplay = await store.ExecuteAsync(
            stale,
            CancellationToken.None);
        Assert.True(staleReplay.IsReplay);
        Assert.Equal(
            staleResult.Receipt!.ReceiptId,
            staleReplay.Receipt!.ReceiptId);

        var conflict = await store.ExecuteAsync(
            WorldScheduleCommand.Reschedule(
                "stale-reschedule",
                scope,
                "b",
                expectedGeneration: 1,
                new GameTimePoint("turn", "main", 1, 4)),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.IdempotencyConflict,
            conflict.ReasonCode);
        Assert.Null(conflict.Receipt);

        var cancel = WorldScheduleCommand.Cancel(
            "cancel-a",
            scope,
            "a",
            expectedGeneration: 0);
        var cancelled = await store.ExecuteAsync(
            cancel,
            CancellationToken.None);
        var cancelledReplay = await store.ExecuteAsync(
            cancel,
            CancellationToken.None);
        Assert.True(cancelled.Applied);
        Assert.Equal(
            WorldScheduleStatus.Cancelled,
            cancelled.Schedule!.Status);
        Assert.True(cancelledReplay.IsReplay);
    }

    [Fact]
    public async Task ClaimRecoveryNeverChangesOccurrenceIdentity()
    {
        var snapshot = Snapshot();
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var scope = snapshot.Coordinate.Scope;
        var created = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "create",
                Intent(scope, "intent", 5)),
            CancellationToken.None);
        var occurrenceId = created.Schedule!.OccurrenceId;

        var early = await store.ExecuteAsync(
            WorldScheduleCommand.Claim(
                "early-claim",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 4),
                "worker-a"),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.NotDue,
            early.ReasonCode);
        var earlyConflict = await store.ExecuteAsync(
            WorldScheduleCommand.Claim(
                "early-claim",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 5),
                "worker-a"),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.IdempotencyConflict,
            earlyConflict.ReasonCode);

        var claimed = await store.ExecuteAsync(
            WorldScheduleCommand.Claim(
                "claim",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 5),
                "worker-a"),
            CancellationToken.None);
        Assert.True(claimed.Applied);
        Assert.Equal(
            occurrenceId,
            claimed.Schedule!.OccurrenceId);
        Assert.Equal(
            occurrenceId,
            claimed.Receipt!.OccurrenceId);

        var blockedReschedule = await store.ExecuteAsync(
            WorldScheduleCommand.Reschedule(
                "reschedule-claimed",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 6)),
            CancellationToken.None);
        var blockedCancel = await store.ExecuteAsync(
            WorldScheduleCommand.Cancel(
                "cancel-claimed",
                scope,
                "intent",
                expectedGeneration: 0),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.ClaimedByAnother,
            blockedReschedule.ReasonCode);
        Assert.Equal(
            WorldScheduleReasonCodes.ClaimedByAnother,
            blockedCancel.ReasonCode);
        Assert.Equal(
            occurrenceId,
            blockedReschedule.Schedule!.OccurrenceId);

        var duplicateWorker = await store.ExecuteAsync(
            WorldScheduleCommand.Claim(
                "claim-other",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 50),
                "worker-b"),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.ClaimedByAnother,
            duplicateWorker.ReasonCode);
        Assert.Equal(
            occurrenceId,
            duplicateWorker.Receipt!.OccurrenceId);

        var reassigned = await store.ExecuteAsync(
            WorldScheduleCommand.Reassign(
                "recover",
                scope,
                "intent",
                expectedGeneration: 0,
                occurrenceId,
                "worker-a",
                "worker-b"),
            CancellationToken.None);
        Assert.True(reassigned.Applied);
        Assert.Equal(
            occurrenceId,
            reassigned.Schedule!.OccurrenceId);
        Assert.Equal(
            "worker-b",
            reassigned.Schedule.Claim!.ClaimantId);

        var staleCompletion = await store.ExecuteAsync(
            WorldScheduleCommand.Complete(
                "stale-complete",
                scope,
                "intent",
                expectedGeneration: 0,
                occurrenceId,
                "worker-a",
                claimed.Schedule.Claim!.ClaimToken),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.ClaimLost,
            staleCompletion.ReasonCode);

        var completed = await store.ExecuteAsync(
            WorldScheduleCommand.Complete(
                "complete",
                scope,
                "intent",
                expectedGeneration: 0,
                occurrenceId,
                "worker-b",
                reassigned.Schedule.Claim!.ClaimToken),
            CancellationToken.None);
        Assert.True(completed.Applied);
        Assert.Equal(
            WorldScheduleStatus.Completed,
            completed.Schedule!.Status);
        Assert.Equal(occurrenceId, completed.Schedule.OccurrenceId);

        var due = await store.QueryDueAsync(
            new WorldScheduleDueQuery(scope, "turn", 100),
            CancellationToken.None);
        Assert.Empty(due.Items);

        var nextGeneration = await store.ExecuteAsync(
            WorldScheduleCommand.Reschedule(
                "next-generation",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 200)),
            CancellationToken.None);
        Assert.True(nextGeneration.Applied);
        Assert.Equal(1, nextGeneration.Schedule!.Generation);
        Assert.Equal(
            WorldScheduleStatus.Active,
            nextGeneration.Schedule.Status);
        Assert.NotEqual(
            occurrenceId,
            nextGeneration.Schedule.OccurrenceId);
    }

    [Fact]
    public async Task ConcurrentSameOperationHasOneMutationAndStableReplay()
    {
        var snapshot = Snapshot();
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var command = WorldScheduleCommand.Create(
            "create",
            Intent(snapshot.Coordinate.Scope, "intent", 1));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(
                    _ => store.ExecuteAsync(
                            command,
                            CancellationToken.None)
                        .AsTask()));

        Assert.Single(results, result => !result.IsReplay);
        Assert.Equal(31, results.Count(result => result.IsReplay));
        Assert.Single(
            results.Select(result => result.Receipt!.ReceiptId)
                .Distinct(StringComparer.Ordinal));
        Assert.Single(
            results.Select(
                    result => result.Schedule!.OccurrenceId)
                .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReassignmentCannotBypassOwnerIncarnationFence()
    {
        var snapshot = Snapshot();
        var scope = snapshot.Coordinate.Scope;
        var original =
            new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var created = await original.ExecuteAsync(
            WorldScheduleCommand.Create(
                "create",
                Intent(scope, "intent", 1)),
            CancellationToken.None);
        var claimed = await original.ExecuteAsync(
            WorldScheduleCommand.Claim(
                "claim",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 1),
                "worker-a"),
            CancellationToken.None);
        var missingClaimReceipt =
            Assert.Throws<NativeWorldSaveBridgeException>(
                () => new WorldAuthoritativeStoreCapture(
                    snapshot,
                    Array.Empty<WorldCommandReceipt>(),
                    Array.Empty<WorldEventHistoryRecord>(),
                    maximumTransactionRecords: 1,
                    maximumHistoryRecords: 1,
                    new[] { claimed.Schedule! },
                    new[] { created.Receipt! },
                    maximumScheduleRecords: 1,
                    maximumScheduleOperations: 1));
        Assert.Equal(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            missingClaimReceipt.ReasonCode);
        var recovered =
            new InMemoryWorldAuthoritativeTransactionStore(
                new WorldAuthoritativeStoreCapture(
                    Snapshot(
                        ownerIncarnation: 2,
                        saveRevision: 1,
                        stateVersion: 1),
                    Array.Empty<WorldCommandReceipt>(),
                    Array.Empty<WorldEventHistoryRecord>(),
                    maximumTransactionRecords: 1,
                    maximumHistoryRecords: 1,
                    new[] { claimed.Schedule! },
                    new[] { created.Receipt!, claimed.Receipt! },
                    maximumScheduleRecords: 1,
                    maximumScheduleOperations: 2));

        var reassigned = await recovered.ExecuteAsync(
            WorldScheduleCommand.Reassign(
                "reassign",
                scope,
                "intent",
                expectedGeneration: 0,
                claimed.Schedule!.OccurrenceId,
                "worker-a",
                "worker-b"),
            CancellationToken.None);

        Assert.False(reassigned.Applied);
        Assert.Equal(
            WorldScheduleReasonCodes.StaleOwner,
            reassigned.ReasonCode);
        Assert.Equal(
            "worker-a",
            reassigned.Schedule!.Claim!.ClaimantId);
    }

    [Fact]
    public async Task OwnerIncarnationAndCancellationFailClosed()
    {
        var snapshot = Snapshot();
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var scope = snapshot.Coordinate.Scope;
        var staleOwner = await store.ExecuteAsync(
            WorldScheduleCommand.Create(
                "stale-owner",
                Intent(scope, "intent", 1, ownerIncarnation: 2)),
            CancellationToken.None);
        Assert.Equal(
            WorldScheduleReasonCodes.StaleOwner,
            staleOwner.ReasonCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.ExecuteAsync(
                WorldScheduleCommand.Create(
                    "cancelled",
                    Intent(scope, "cancelled", 1)),
                cancellation.Token));
        Assert.Null(
            await store.FindAsync(
                scope,
                "cancelled",
                CancellationToken.None));
    }

    [Fact]
    public async Task PayloadSchemaAndAggregateBytesAreBounded()
    {
        var snapshot = Snapshot();
        var scope = snapshot.Coordinate.Scope;
        Assert.Throws<ArgumentException>(
            () => new WorldScheduleIntent(
                "invalid",
                scope,
                new GameTimePoint("turn", "main", 1, 1),
                new GameEntityIdentity("owner", 1),
                "intent",
                "1",
                Json(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "intent": {"type": "string"}
                      },
                      "required": ["intent"],
                      "additionalProperties": false
                    }
                    """),
                Json("""{"unexpected":"value"}""")));
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(
                snapshot,
                new WorldScheduleStoreOptions(
                    maxAggregatePayloadBytes: 16));

        var failure =
            await Assert.ThrowsAsync<WorldScheduleStoreException>(
                async () => await store.ExecuteAsync(
                    WorldScheduleCommand.Create(
                        "create",
                        Intent(scope, "intent", 1)),
                    CancellationToken.None));

        Assert.Equal(
            WorldScheduleReasonCodes.CapacityExceeded,
            failure.ReasonCode);
        Assert.Null(
            await store.FindAsync(
                scope,
                "intent",
                CancellationToken.None));
    }

    [Fact]
    public async Task OperationCapacityPreservesExistingReplayAndState()
    {
        var snapshot = Snapshot();
        var scope = snapshot.Coordinate.Scope;
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(
                snapshot,
                new WorldScheduleStoreOptions(
                    maxSchedules: 2,
                    maxOperations: 1));
        var create = WorldScheduleCommand.Create(
            "create",
            Intent(scope, "intent", 1));
        var created = await store.ExecuteAsync(
            create,
            CancellationToken.None);
        var replay = await store.ExecuteAsync(
            create,
            CancellationToken.None);

        Assert.True(created.Applied);
        Assert.True(replay.IsReplay);
        var failure =
            await Assert.ThrowsAsync<WorldScheduleStoreException>(
                async () => await store.ExecuteAsync(
                    WorldScheduleCommand.Cancel(
                        "cancel",
                        scope,
                        "intent",
                        expectedGeneration: 0),
                    CancellationToken.None));
        Assert.Equal(
            WorldScheduleReasonCodes.CapacityExceeded,
            failure.ReasonCode);
        var current = await store.FindAsync(
            scope,
            "intent",
            CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(WorldScheduleStatus.Active, current!.Status);
    }

    [Fact]
    public async Task FileStoreRestartsWithSameClaimAndTerminalState()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var scope = snapshot.Coordinate.Scope;
            var first = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            var created = await first.ExecuteAsync(
                WorldScheduleCommand.Create(
                    "create",
                    Intent(scope, "intent", 2)),
                CancellationToken.None);
            var claimCommand = WorldScheduleCommand.Claim(
                "claim",
                scope,
                "intent",
                expectedGeneration: 0,
                new GameTimePoint("turn", "main", 1, 2),
                "worker");
            var claimed = await first.ExecuteAsync(
                claimCommand,
                CancellationToken.None);

            var reopened =
                new FileWorldAuthoritativeTransactionStore(path);
            var replay = await reopened.ExecuteAsync(
                claimCommand,
                CancellationToken.None);
            Assert.True(replay.IsReplay);
            Assert.Equal(
                claimed.Schedule!.OccurrenceId,
                replay.Schedule!.OccurrenceId);
            Assert.Equal(
                claimed.Schedule.Claim!.ClaimToken,
                replay.Schedule.Claim!.ClaimToken);
            Assert.Equal(
                claimed.Receipt!.ReceiptId,
                replay.Receipt!.ReceiptId);

            var competingClaim = await reopened.ExecuteAsync(
                WorldScheduleCommand.Claim(
                    "claim-other",
                    scope,
                    "intent",
                    expectedGeneration: 0,
                    new GameTimePoint("turn", "main", 1, 100),
                    "other-worker"),
                CancellationToken.None);
            Assert.False(competingClaim.Applied);
            Assert.Equal(
                WorldScheduleReasonCodes.ClaimedByAnother,
                competingClaim.ReasonCode);
            Assert.Equal(
                claimed.Schedule.OccurrenceId,
                competingClaim.Schedule!.OccurrenceId);

            var completed = await reopened.ExecuteAsync(
                WorldScheduleCommand.Complete(
                    "complete",
                    scope,
                    "intent",
                    expectedGeneration: 0,
                    created.Schedule!.OccurrenceId,
                    "worker",
                    replay.Schedule.Claim.ClaimToken),
                CancellationToken.None);
            Assert.True(completed.Applied);

            var afterRestart =
                new FileWorldAuthoritativeTransactionStore(path);
            var terminal = await afterRestart.FindAsync(
                scope,
                "intent",
                CancellationToken.None);
            Assert.NotNull(terminal);
            Assert.Equal(
                WorldScheduleStatus.Completed,
                terminal!.Status);
            Assert.Equal(
                created.Schedule.OccurrenceId,
                terminal.OccurrenceId);
            Assert.Empty(
                (await afterRestart.QueryDueAsync(
                    new WorldScheduleDueQuery(scope, "turn", 100),
                    CancellationToken.None)).Items);
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileStoreSerializesConcurrentSameOperationAcrossInstances()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var first = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            var second = new FileWorldAuthoritativeTransactionStore(path);
            var command = WorldScheduleCommand.Create(
                "create",
                Intent(snapshot.Coordinate.Scope, "intent", 1));

            var results = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(
                        index => Task.Run(
                            async () => await (index % 2 == 0
                                    ? first
                                    : second)
                                .ExecuteAsync(
                                    command,
                                    CancellationToken.None))));

            Assert.Single(results, result => !result.IsReplay);
            Assert.Equal(15, results.Count(result => result.IsReplay));
            Assert.Single(
                results.Select(result => result.Receipt!.ReceiptId)
                    .Distinct(StringComparer.Ordinal));
            Assert.Single(
                results.Select(result => result.Schedule!.OccurrenceId)
                    .Distinct(StringComparer.Ordinal));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileScheduleCancellationDoesNotPublish()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var store = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await store.ExecuteAsync(
                    WorldScheduleCommand.Create(
                        "cancelled-create",
                        Intent(
                            snapshot.Coordinate.Scope,
                            "intent",
                            1)),
                    cancellation.Token));

            var reopened =
                new FileWorldAuthoritativeTransactionStore(path);
            Assert.Null(
                await reopened.FindAsync(
                    snapshot.Coordinate.Scope,
                    "intent",
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileTimelineNotFoundRejectionsReplayAfterRestart()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var store = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            var missingWorld = WorldScheduleCommand.Cancel(
                "missing-world",
                new WorldTransactionScope(
                    "other-world",
                    "main",
                    timelineEpoch: 1),
                "intent",
                expectedGeneration: 0);
            var wrongEpoch = WorldScheduleCommand.Cancel(
                "wrong-epoch",
                new WorldTransactionScope(
                    "world",
                    "main",
                    timelineEpoch: 2),
                "intent",
                expectedGeneration: 0);
            var missingResult = await store.ExecuteAsync(
                missingWorld,
                CancellationToken.None);
            var epochResult = await store.ExecuteAsync(
                wrongEpoch,
                CancellationToken.None);
            Assert.Equal(
                WorldScheduleReasonCodes.TimelineNotFound,
                missingResult.ReasonCode);
            Assert.Equal(
                WorldScheduleReasonCodes.TimelineNotFound,
                epochResult.ReasonCode);

            var reopened =
                new FileWorldAuthoritativeTransactionStore(path);
            var missingReplay = await reopened.ExecuteAsync(
                missingWorld,
                CancellationToken.None);
            var epochReplay = await reopened.ExecuteAsync(
                wrongEpoch,
                CancellationToken.None);
            Assert.True(missingReplay.IsReplay);
            Assert.True(epochReplay.IsReplay);
            Assert.Equal(
                missingResult.Receipt!.ReceiptId,
                missingReplay.Receipt!.ReceiptId);
            Assert.Equal(
                epochResult.Receipt!.ReceiptId,
                epochReplay.Receipt!.ReceiptId);
            Assert.NotNull(
                await reopened.ReadAsync(
                    snapshot.Coordinate.Address,
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileCapacityFailureLeavesPriorStateIntact()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var scope = snapshot.Coordinate.Scope;
            var options =
                new FileWorldAuthoritativeTransactionStoreOptions(
                    schedules:
                        new WorldScheduleStoreOptions(
                            maxSchedules: 1,
                            maxOperations: 8));
            var store =
                new FileWorldAuthoritativeTransactionStore(
                    path,
                    new[] { snapshot },
                    options);
            var first = await store.ExecuteAsync(
                WorldScheduleCommand.Create(
                    "create-a",
                    Intent(scope, "a", 1)),
                CancellationToken.None);
            Assert.True(first.Applied);

            var failure =
                await Assert.ThrowsAsync<WorldScheduleStoreException>(
                    async () => await store.ExecuteAsync(
                        WorldScheduleCommand.Create(
                            "create-b",
                            Intent(scope, "b", 1)),
                        CancellationToken.None));
            Assert.Equal(
                WorldScheduleReasonCodes.CapacityExceeded,
                failure.ReasonCode);

            var reopened =
                new FileWorldAuthoritativeTransactionStore(
                    path,
                    initialStates: null,
                    options);
            Assert.NotNull(
                await reopened.FindAsync(
                    scope,
                    "a",
                    CancellationToken.None));
            Assert.Null(
                await reopened.FindAsync(
                    scope,
                    "b",
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileScheduleCorruptionFailsClosedAfterOuterRehash()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var scope = snapshot.Coordinate.Scope;
            var store =
                new FileWorldAuthoritativeTransactionStore(
                    path,
                    new[] { snapshot });
            _ = await store.ExecuteAsync(
                WorldScheduleCommand.Create(
                    "create",
                    Intent(scope, "intent", 1)),
                CancellationToken.None);
            RewriteStorePayload(
                path,
                payload =>
                {
                    var schedules = payload["schedules"]!.AsArray();
                    schedules[0]!["recordDigest"] =
                        new string('f', 64);
                });

            var exception = Assert.Throws<
                FileWorldAuthoritativeStoreException>(
                () => new FileWorldAuthoritativeTransactionStore(path));
            Assert.Equal(
                FileWorldAuthoritativeStoreReasonCodes.Corrupt,
                exception.ReasonCode);
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Theory]
    [InlineData("record-claim-token")]
    [InlineData("receipt-occurrence")]
    public async Task FileDerivedBindingsFailClosedAfterOuterRehash(
        string corruption)
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            var scope = snapshot.Coordinate.Scope;
            var store =
                new FileWorldAuthoritativeTransactionStore(
                    path,
                    new[] { snapshot });
            _ = await store.ExecuteAsync(
                WorldScheduleCommand.Create(
                    "create",
                    Intent(scope, "intent", 1)),
                CancellationToken.None);
            _ = await store.ExecuteAsync(
                WorldScheduleCommand.Claim(
                    "claim",
                    scope,
                    "intent",
                    expectedGeneration: 0,
                    new GameTimePoint("turn", "main", 1, 1),
                    "worker"),
                CancellationToken.None);
            RewriteStorePayload(
                path,
                payload =>
                {
                    if (string.Equals(
                            corruption,
                            "record-claim-token",
                            StringComparison.Ordinal))
                    {
                        payload["schedules"]!
                            .AsArray()[0]!["claim"]!["claimToken"] =
                            "forged-token";
                        return;
                    }

                    var receipt = payload["scheduleOperations"]!
                        .AsArray()
                        .Select(item => item!.AsObject())
                        .Single(
                            item => string.Equals(
                                item["operationId"]!
                                    .GetValue<string>(),
                                "claim",
                                StringComparison.Ordinal));
                    receipt["occurrenceId"] = "forged-occurrence";
                });

            var exception = Assert.Throws<
                FileWorldAuthoritativeStoreException>(
                () => new FileWorldAuthoritativeTransactionStore(path));
            Assert.Equal(
                FileWorldAuthoritativeStoreReasonCodes.Corrupt,
                exception.ReasonCode);
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FilePayloadWithoutOptionalScheduleArraysLoadsEmpty()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            _ = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            RewriteStorePayload(
                path,
                payload =>
                {
                    _ = payload.Remove("schedules");
                    _ = payload.Remove("scheduleOperations");
                });

            var reopened =
                new FileWorldAuthoritativeTransactionStore(path);

            Assert.Null(
                await reopened.FindAsync(
                    snapshot.Coordinate.Scope,
                    "not-present",
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    [Fact]
    public async Task FileRestartRemovesBoundedStaleNextImage()
    {
        var path = TemporaryStorePath();
        try
        {
            var snapshot = Snapshot();
            _ = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot });
            var nextPath = path + ".next";
            File.WriteAllText(
                nextPath,
                "uncommitted",
                new UTF8Encoding(false));

            var reopened =
                new FileWorldAuthoritativeTransactionStore(path);

            Assert.False(File.Exists(nextPath));
            Assert.NotNull(
                await reopened.ReadAsync(
                    snapshot.Coordinate.Address,
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryStore(path);
        }
    }

    private static WorldScheduleIntent Intent(
        WorldTransactionScope scope,
        string scheduleId,
        long dueTick,
        long ownerIncarnation = 1)
    {
        return new WorldScheduleIntent(
            scheduleId,
            scope,
            new GameTimePoint(
                "turn",
                scope.TimelineId,
                scope.TimelineEpoch,
                dueTick),
            new GameEntityIdentity(
                "owner",
                ownerIncarnation),
            "intent",
            "1",
            Json(
                """
                {
                  "type": "object",
                  "properties": {
                    "intent": {"type": "string"}
                  },
                  "required": ["intent"],
                  "additionalProperties": false
                }
                """),
            Json("""{"intent":"remember"}"""));
    }

    private static WorldAuthoritativeStateSnapshot Snapshot(
        long ownerIncarnation = 1,
        long saveRevision = 0,
        long stateVersion = 0)
    {
        return new WorldAuthoritativeStateSnapshot(
            new WorldAuthoritativeCoordinate(
                "world",
                "main",
                timelineEpoch: 1,
                saveRevision,
                stateVersion,
                new string('a', 64)),
            Json("""{"value":"initial"}"""),
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["owner"] = ownerIncarnation
            });
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static void RewriteStorePayload(
        string path,
        Action<JsonObject> rewrite)
    {
        var root = JsonNode.Parse(
                       File.ReadAllText(path, Encoding.UTF8))
                   ?.AsObject()
                   ?? throw new InvalidOperationException(
                       "The store root is missing.");
        var payload = root["payload"]?.AsObject()
                      ?? throw new InvalidOperationException(
                          "The store payload is missing.");
        rewrite(payload);
        using var payloadDocument =
            JsonDocument.Parse(payload.ToJsonString());
        root["contentDigest"] =
            WorldLargeCanonicalJsonDigest.Compute(
                payloadDocument.RootElement,
                WorldPackageLimits.HardMaximumFileBytes,
                "payload");
        File.WriteAllText(
            path,
            root.ToJsonString(),
            new UTF8Encoding(false));
    }

    private static string TemporaryStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "game-agent-world-schedule-tests",
            Guid.NewGuid().ToString("N"),
            "world-store.json");
    }

    private static void DeleteTemporaryStore(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
