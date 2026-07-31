using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldAuthoritativeTransactionTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task StateHistoryAndReceiptCommitAtomicallyAndRetryReplays()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var effectCalls = 0;
        var request = Request(
            instance,
            Coordinate(),
            "command-1",
            "operation-1",
            new DelegateTransactionalEffect(
                context =>
                {
                    effectCalls++;
                    context.Draft.ReplaceState(
                        Json("""{"value":"after"}"""));
                    return new WorldEventEffectResult(
                        true,
                        "changed",
                        Json("""{"receipt":"ok"}"""));
                }));
        var executor = new WorldEventTransactionExecutor(store);

        var first = await executor.ExecuteAsync(request, default);
        var replay = await executor.ExecuteAsync(request, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            first.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.Replayed,
            replay.Status);
        Assert.Equal(1, effectCalls);
        Assert.NotNull(first.Receipt);
        Assert.Equal(
            first.Receipt!.ReceiptId,
            replay.Receipt!.ReceiptId);
        Assert.Equal(
            1,
            first.Receipt.ResultingCoordinate!.SaveRevision);
        Assert.Equal(
            1,
            first.Receipt.ResultingCoordinate.StateVersion);
        var state = await store.ReadAsync(Address(), default);
        Assert.Equal(
            "after",
            state!.State.GetProperty("value").GetString());
        Assert.NotNull(
            await store.FindInstanceAsync(instance.InstanceId, default));
        Assert.Equal(
            1,
            (await store.ReadDefinitionAsync(
                WorldEventHistoryRecord.FromInstance(instance).Definition,
                default)).OccurrenceCount);
    }

    [Fact]
    public void StateDigestSupportsLargeStateAndIgnoresPropertyOrder()
    {
        var payload = new string('x', 300_000);
        var large = Json(
            "{\"nested\":{\"b\":\"2\",\"a\":\"1\"},\"payload\":\""
            + payload
            + "\"}");
        var reordered = Json(
            "{\"payload\":\""
            + payload
            + "\",\"nested\":{\"a\":\"1\",\"b\":\"2\"}}");

        var first = new WorldAuthoritativeStateSnapshot(
            Coordinate(),
            large);
        var second = new WorldAuthoritativeStateSnapshot(
            Coordinate(),
            reordered);

        Assert.Equal(first.StateDigest, second.StateDigest);
        Assert.Equal(64, first.StateDigest.Length);
    }

    [Fact]
    public void StateRejectsJsonNumbersAndDuplicateProperties()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":1}""")));
        Assert.ThrowsAny<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":"first","value":"second"}""")));
    }

    [Fact]
    public void IssuedIncarnationLedgerIsExactBoundedAndDeepCopied()
    {
        var source = new List<WorldIssuedEntityIncarnation>
        {
            new("actor", 3),
            new("actor", 1),
            new("retired", 7)
        };
        var snapshot = new WorldAuthoritativeStateSnapshot(
            Coordinate(),
            Json("""{"value":"before"}"""),
            new Dictionary<string, long> { ["actor"] = 3 },
            source);
        source.Clear();

        Assert.Equal(
            new[] { "actor:1", "actor:3", "retired:7" },
            snapshot.IssuedEntityIncarnations.Select(
                item => item.EntityId + ":" + item.Incarnation));
        Assert.True(snapshot.WasIncarnationIssued("actor", 1));
        Assert.False(snapshot.WasIncarnationIssued("actor", 2));
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":"before"}"""),
                new Dictionary<string, long> { ["actor"] = 3 },
                new[]
                {
                    new WorldIssuedEntityIncarnation("actor", 3),
                    new WorldIssuedEntityIncarnation("actor", 3)
                }));
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":"before"}"""),
                new Dictionary<string, long> { ["actor"] = 3 },
                new[]
                {
                    new WorldIssuedEntityIncarnation("actor", 1)
                }));
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":"before"}"""),
                new Dictionary<string, long> { ["actor"] = 1 },
                new[]
                {
                    new WorldIssuedEntityIncarnation("actor", 1),
                    new WorldIssuedEntityIncarnation("actor", 3)
                }));
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                Json("""{"value":"before"}"""),
                entityIncarnations: null,
                Enumerable.Range(
                        0,
                        WorldAuthoritativeStateSnapshot
                            .MaximumIssuedIncarnationCount + 1)
                    .Select(
                        index =>
                            new WorldIssuedEntityIncarnation(
                                "entity-" + index,
                                1))));
        Assert.Throws<ArgumentException>(
            () => new WorldIssuedEntityIncarnation(
                new string(
                    '\0',
                    WorldValidation.MaximumIdentifierUtf8Bytes + 1),
                1));
    }

    [Fact]
    public async Task IssuedIncarnationsSurviveUpgradeAndRemovalAndCannotBeReused()
    {
        var store = Store();
        var first = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(Address(), default));
        var upgraded = await MutateIncarnationsAsync(
            store,
            first,
            "upgrade",
            draft => draft.SetIncarnation("actor", 3));

        Assert.Equal(3, upgraded.EntityIncarnations["actor"]);
        Assert.True(upgraded.WasIncarnationIssued("actor", 1));
        Assert.False(upgraded.WasIncarnationIssued("actor", 2));
        Assert.True(upgraded.WasIncarnationIssued("actor", 3));

        var skippedRequest = MutationRequest(
            upgraded,
            "skipped-incarnation");
        var skippedBegin = await store.BeginAsync(
            skippedRequest,
            default);
        var skipped = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(skippedBegin.Transaction);
        Assert.Throws<InvalidOperationException>(
            () => skipped.Draft.SetIncarnation("actor", 2));
        Assert.False(skipped.Draft.HasChanges);
        _ = await store.CancelPendingAsync(
            skippedRequest.ExpectedCoordinate.Scope,
            skippedRequest.OperationId,
            skippedRequest.RequestFingerprint,
            "test_cancelled",
            default);
        await skipped.DisposeAsync();

        var idempotentRequest = MutationRequest(
            upgraded,
            "idempotent-current");
        var idempotentBegin = await store.BeginAsync(
            idempotentRequest,
            default);
        var idempotent = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(
            idempotentBegin.Transaction);
        idempotent.Draft.SetIncarnation("actor", 3);
        Assert.False(idempotent.Draft.HasChanges);
        _ = await store.CancelPendingAsync(
            idempotentRequest.ExpectedCoordinate.Scope,
            idempotentRequest.OperationId,
            idempotentRequest.RequestFingerprint,
            "test_cancelled",
            default);
        await idempotent.DisposeAsync();

        var removed = await MutateIncarnationsAsync(
            store,
            upgraded,
            "remove",
            draft => draft.RemoveIncarnation("actor"));
        Assert.False(removed.TryGetIncarnation("actor", out _));
        Assert.True(removed.WasIncarnationIssued("actor", 1));
        Assert.True(removed.WasIncarnationIssued("actor", 3));

        var request = MutationRequest(removed, "reuse");
        var begin = await store.BeginAsync(request, default);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        Assert.Throws<InvalidOperationException>(
            () => transaction.Draft.SetIncarnation("actor", 1));
        Assert.Throws<InvalidOperationException>(
            () => transaction.Draft.SetIncarnation("actor", 2));
        var cancelled = await store.CancelPendingAsync(
            request.ExpectedCoordinate.Scope,
            request.OperationId,
            request.RequestFingerprint,
            "test_cancelled",
            default);
        Assert.Equal(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            cancelled.Status);
        await transaction.DisposeAsync();

        var after = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(Address(), default));
        Assert.False(after.TryGetIncarnation("actor", out _));
        Assert.True(after.WasIncarnationIssued("actor", 1));
        Assert.False(after.WasIncarnationIssued("actor", 2));
    }

    [Fact]
    public async Task TimelineKeysCannotCollideThroughIdentifierDelimiters()
    {
        var firstCoordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline\u001fbranch",
            1,
            0,
            0,
            CatalogDigest);
        var secondCoordinate = new WorldAuthoritativeCoordinate(
            "world\u001ftimeline",
            "branch",
            1,
            0,
            0,
            CatalogDigest);
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            new[]
            {
                new WorldAuthoritativeStateSnapshot(
                    firstCoordinate,
                    Json("""{"value":"first"}""")),
                new WorldAuthoritativeStateSnapshot(
                    secondCoordinate,
                    Json("""{"value":"second"}"""))
            });

        var first = await store.ReadAsync(
            firstCoordinate.Address,
            default);
        var second = await store.ReadAsync(
            secondCoordinate.Address,
            default);

        Assert.Equal(
            "first",
            first!.State.GetProperty("value").GetString());
        Assert.Equal(
            "second",
            second!.State.GetProperty("value").GetString());
    }

    [Fact]
    public void ParticipantKeysCannotCollideThroughIdentifierDelimiters()
    {
        var resolution = new WorldEventResolution(
            "resolution",
            new[]
            {
                new WorldEventParticipant(
                    "entity\u001fpart",
                    2,
                    "role"),
                new WorldEventParticipant(
                    "part",
                    2,
                    "role\u001fentity")
            });

        Assert.Equal(2, resolution.Participants.Count);
    }

    [Fact]
    public async Task SingleWriterRejectsConcurrentWriter()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Request(
            instance,
            Coordinate(),
            "command-first",
            "operation-first",
            new AsyncTransactionalEffect(
                async context =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    context.Draft.ReplaceState(
                        Json("""{"value":"winner"}"""));
                    return new WorldEventEffectResult(true, "winner");
                }));
        var second = Request(
            instance,
            Coordinate(),
            "command-second",
            "operation-second",
            new DelegateTransactionalEffect(
                _ => new WorldEventEffectResult(true, "loser")));
        var executor = new WorldEventTransactionExecutor(store);

        var firstTask = executor.ExecuteAsync(first, default).AsTask();
        await entered.Task;
        var competing = await executor.ExecuteAsync(second, default);
        release.TrySetResult(true);
        var settled = await firstTask;

        Assert.Equal(
            WorldTransactionExecutionStatus.Busy,
            competing.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            settled.Status);
        Assert.Equal(
            "winner",
            (await store.ReadAsync(Address(), default))!
                .State.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ReusingOperationWithDifferentCommandFailsIdempotency()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var executor = new WorldEventTransactionExecutor(store);
        var first = Request(
            instance,
            Coordinate(),
            "command-original",
            "operation-shared",
            new DelegateTransactionalEffect(
                context =>
                {
                    context.Draft.ReplaceState(
                        Json("""{"value":"original"}"""));
                    return new WorldEventEffectResult(true, "original");
                }));
        var conflictingCalls = 0;
        var conflict = Request(
            instance,
            Coordinate(),
            "command-conflicting",
            "operation-shared",
            new DelegateTransactionalEffect(
                _ =>
                {
                    conflictingCalls++;
                    return new WorldEventEffectResult(true, "conflict");
                }));

        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            (await executor.ExecuteAsync(first, default)).Status);
        var result = await executor.ExecuteAsync(conflict, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.IdempotencyConflict,
            result.Status);
        Assert.Equal(0, conflictingCalls);
        Assert.Equal(
            "original",
            (await store.ReadAsync(Address(), default))!
                .State.GetProperty("value").GetString());
    }

    [Fact]
    public async Task LostCommitAcknowledgementRequiresReconciliationThenReplays()
    {
        var inner = Store();
        var instance = await InstanceAsync(inner);
        var wrapper = new LostAcknowledgementStore(inner);
        var calls = 0;
        var request = Request(
            instance,
            Coordinate(),
            "command-ack",
            "operation-ack",
            new DelegateTransactionalEffect(
                context =>
                {
                    calls++;
                    context.Draft.ReplaceState(
                        Json("""{"value":"committed"}"""));
                    return new WorldEventEffectResult(true, "committed");
                }));
        var executor = new WorldEventTransactionExecutor(wrapper);

        var uncertain = await executor.ExecuteAsync(request, default);
        var retry = await executor.ExecuteAsync(request, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.ReconciliationRequired,
            uncertain.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.Replayed,
            retry.Status);
        Assert.Equal(1, calls);
        Assert.Equal(
            "committed",
            (await inner.ReadAsync(Address(), default))!
                .State.GetProperty("value").GetString());
        Assert.NotNull(
            await inner.FindInstanceAsync(instance.InstanceId, default));
    }

    [Fact]
    public async Task RejectedEffectDiscardsEveryDraftMutation()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var calls = 0;
        var request = Request(
            instance,
            Coordinate(),
            "command-rejected",
            "operation-rejected",
            new DelegateTransactionalEffect(
                context =>
                {
                    calls++;
                    context.Draft.ReplaceState(
                        Json("""{"value":"must-not-commit"}"""));
                    context.Draft.SetIncarnation("actor", 99);
                    return new WorldEventEffectResult(
                        false,
                        "rule_rejected");
                }));
        var executor = new WorldEventTransactionExecutor(store);

        var rejected = await executor.ExecuteAsync(request, default);
        var retry = await executor.ExecuteAsync(request, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Rejected,
            rejected.Status);
        Assert.Equal("rule_rejected", rejected.ReasonCode);
        Assert.Equal(1, calls);
        Assert.Equal(
            WorldTransactionExecutionStatus.Rejected,
            retry.Status);
        var state = await store.ReadAsync(Address(), default);
        Assert.Equal(
            "before",
            state!.State.GetProperty("value").GetString());
        Assert.Equal(0, state.Coordinate.SaveRevision);
        Assert.Equal(0, state.Coordinate.StateVersion);
        Assert.True(state.TryGetIncarnation("actor", out var incarnation));
        Assert.Equal(1, incarnation);
        Assert.Null(
            await store.FindInstanceAsync(instance.InstanceId, default));
    }

    [Fact]
    public async Task UnknownEffectNeverRedispatchesUntilExplicitResolution()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var calls = 0;
        var request = Request(
            instance,
            Coordinate(),
            "command-unknown",
            "operation-unknown",
            new DelegateTransactionalEffect(
                _ =>
                {
                    calls++;
                    throw new WorldEffectOutcomeUnknownException(
                        "The host must reconcile.");
                }));
        var executor = new WorldEventTransactionExecutor(store);

        var first = await executor.ExecuteAsync(request, default);
        var retry = await executor.ExecuteAsync(request, default);
        var cancelled = await executor.CancelPendingAsync(
            request.TransactionRequest,
            "host_reconciled_cancelled",
            default);

        Assert.Equal(
            WorldTransactionExecutionStatus.ReconciliationRequired,
            first.Status);
        Assert.Equal(
            WorldTransactionExecutionStatus.ReconciliationRequired,
            retry.Status);
        Assert.Equal(1, calls);
        Assert.Equal(
            WorldTransactionExecutionStatus.Cancelled,
            cancelled.Status);
        Assert.Equal(
            "before",
            (await store.ReadAsync(Address(), default))!
                .State.GetProperty("value").GetString());
    }

    [Fact]
    public async Task CancelledLeaseFencesLateCommitAndPreservesCoordinate()
    {
        var store = Store();
        var instance = await InstanceAsync(store);
        var transactionRequest = new WorldTransactionRequest(
            "operation-late",
            "command-late",
            instance.PlanFingerprint,
            Coordinate(),
            new[]
            {
                new WorldEntityIncarnationExpectation("actor", 1)
            },
            WorldEventHistoryRecord.FromInstance(instance));
        var begin = await store.BeginAsync(transactionRequest, default);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        transaction.Draft.ReplaceState(
            Json("""{"value":"late"}"""));

        var cancelled = await store.CancelPendingAsync(
            transactionRequest.ExpectedCoordinate.Scope,
            transactionRequest.OperationId,
            transactionRequest.RequestFingerprint,
            "cancelled_by_host",
            default);
        var late = await transaction.CommitEventAsync(
            new WorldEffectReceipt(true, "late"),
            default);
        await transaction.DisposeAsync();

        Assert.Equal(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            cancelled.Status);
        Assert.Equal(
            WorldCommandReceiptStatus.Cancelled,
            cancelled.Receipt!.Status);
        Assert.Equal(WorldTransactionCommitStatus.LeaseLost, late.Status);
        var state = await store.ReadAsync(Address(), default);
        Assert.Equal(
            "before",
            state!.State.GetProperty("value").GetString());
        Assert.Equal(0, state.Coordinate.SaveRevision);
        Assert.Equal(0, state.Coordinate.StateVersion);
        Assert.Null(
            await store.FindInstanceAsync(instance.InstanceId, default));
    }

    [Theory]
    [InlineData("version", WorldTransactionReasonCodes.StaleVersion)]
    [InlineData("catalog", WorldTransactionReasonCodes.StaleCatalog)]
    [InlineData("incarnation", WorldTransactionReasonCodes.StaleIncarnation)]
    [InlineData("epoch", WorldTransactionReasonCodes.StaleCoordinate)]
    public async Task StaleAdmissionFailsClosedWithoutCallingEffect(
        string mismatch,
        string expectedReason)
    {
        var actualCoordinate = mismatch == "epoch"
            ? new WorldAuthoritativeCoordinate(
                "world",
                "timeline",
                2,
                0,
                0,
                CatalogDigest)
            : mismatch == "version"
                ? new WorldAuthoritativeCoordinate(
                    "world",
                    "timeline",
                    1,
                    1,
                    2,
                    CatalogDigest)
                : Coordinate();
        var incarnations = new Dictionary<string, long>
        {
            ["actor"] = mismatch == "incarnation" ? 2 : 1
        };
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            new WorldAuthoritativeStateSnapshot(
                actualCoordinate,
                Json("""{"value":"before"}"""),
                incarnations));
        var planStore = mismatch == "epoch" ? Store() : store;
        var instance = await InstanceAsync(planStore);
        var calls = 0;
        var expectedCoordinate = mismatch == "catalog"
            ? new WorldAuthoritativeCoordinate(
                "world",
                "timeline",
                1,
                0,
                0,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            : Coordinate();
        var request = Request(
            instance,
            expectedCoordinate,
            "command-stale-" + mismatch,
            "operation-stale-" + mismatch,
            new DelegateTransactionalEffect(
                _ =>
                {
                    calls++;
                    return new WorldEventEffectResult(true, "bad");
                }));

        var result = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(request, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Rejected,
            result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(0, calls);
        Assert.Null(
            await store.FindInstanceAsync(instance.InstanceId, default));
    }

    [Fact]
    public async Task TypedMutationSetCommitsTransferAndDirectionalEdgeTogether()
    {
        var state = Json(
            """
            {
              "entities": {
                "actor": {"balance": "100"},
                "target": {"balance": "20"}
              },
              "relations": {}
            }
            """);
        var store = Store(state, includeTarget: true);
        var resources = new[]
        {
            "balance:actor",
            "balance:target",
            "label:actor",
            "relation:actor:target"
        };
        var instance = await InstanceAsync(
            store,
            participants: new[]
            {
                new WorldEventParticipant("actor", 1, "actor"),
                new WorldEventParticipant("target", 1, "target")
            },
            writes: resources);
        var mutationSet = new WorldAtomicMutationSet(
            "command-mutations",
            "operation-mutations",
            "world",
            "timeline",
            1,
            0,
            "0",
            CatalogDigest,
            new IWorldMutationIntent[]
            {
                new WorldValueMutationIntent(
                    "label",
                    new GameEntityIdentity("actor", 1),
                    "/label",
                    "label:actor",
                    WorldValueMutationKind.Set,
                    Json("\"ready\"")),
                new WorldTransferMutationIntent(
                    "transfer",
                    new GameEntityIdentity("actor", 1),
                    "/balance",
                    "balance:actor",
                    new GameEntityIdentity("target", 1),
                    "/balance",
                    "balance:target",
                    "balance",
                    new WorldFixedPointValue(30, 0)),
                new WorldRelationshipMutationIntent(
                    "relationship",
                    new GameEntityIdentity("actor", 1),
                    new GameEntityIdentity("target", 1),
                    "observes",
                    "relation:actor:target",
                    WorldRelationshipMutationKind.Upsert,
                    Json("""{"state":"active"}"""))
            });
        var effect = new WorldAtomicMutationEffect(
            mutationSet,
            new[] { BalanceSchema() },
            new WorldEntityMutationPathResolver(
                "/entities",
                "/relations"));
        var request = Request(
            instance,
            Coordinate(),
            mutationSet.CommandId,
            mutationSet.OperationId,
            effect);

        var result = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(request, default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            result.Status);
        var committed = (await store.ReadAsync(Address(), default))!.State;
        Assert.Equal(
            "70",
            committed.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("balance")
                .GetString());
        Assert.Equal(
            "50",
            committed.GetProperty("entities")
                .GetProperty("target")
                .GetProperty("balance")
                .GetString());
        Assert.Equal(
            "ready",
            committed.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("label")
                .GetString());
        var relations = committed.GetProperty("relations");
        Assert.True(
            relations.GetProperty("actor")
                .GetProperty("1")
                .GetProperty("observes")
                .GetProperty("target")
                .TryGetProperty("1", out _));
        Assert.False(relations.TryGetProperty("target", out _));
    }

    [Fact]
    public async Task TypedMutationFailureRollsBackEarlierIntentsAndTransfer()
    {
        var state = Json(
            """
            {
              "entities": {
                "actor": {"balance": "10"},
                "target": {"balance": "95"}
              }
            }
            """);
        var store = Store(state, includeTarget: true);
        var instance = await InstanceAsync(
            store,
            participants: new[]
            {
                new WorldEventParticipant("actor", 1, "actor"),
                new WorldEventParticipant("target", 1, "target")
            },
            writes: new[]
            {
                "label:actor",
                "balance:actor",
                "balance:target"
            });
        var mutations = new WorldAtomicMutationSet(
            "command-rollback",
            "operation-rollback",
            "world",
            "timeline",
            1,
            0,
            "0",
            CatalogDigest,
            new IWorldMutationIntent[]
            {
                new WorldValueMutationIntent(
                    "early",
                    new GameEntityIdentity("actor", 1),
                    "/label",
                    "label:actor",
                    WorldValueMutationKind.Set,
                    Json("\"must-disappear\"")),
                new WorldTransferMutationIntent(
                    "failing-transfer",
                    new GameEntityIdentity("actor", 1),
                    "/balance",
                    "balance:actor",
                    new GameEntityIdentity("target", 1),
                    "/balance",
                    "balance:target",
                    "bounded",
                    new WorldFixedPointValue(10, 0))
            });
        var effect = new WorldAtomicMutationEffect(
            mutations,
            new[]
            {
                new WorldNumericSchema(
                    "bounded",
                    0,
                    "unit",
                    "0",
                    "100",
                    "0")
            },
            new WorldEntityMutationPathResolver(
                "/entities",
                "/relations"));

        var result = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(
                Request(
                    instance,
                    Coordinate(),
                    mutations.CommandId,
                    mutations.OperationId,
                    effect),
                default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Rejected,
            result.Status);
        Assert.Equal(
            WorldNumericReasonCodes.OutOfBounds,
            result.ReasonCode);
        var unchanged = (await store.ReadAsync(Address(), default))!;
        Assert.Equal(0, unchanged.Coordinate.StateVersion);
        Assert.False(
            unchanged.State.GetProperty("entities")
                .GetProperty("actor")
                .TryGetProperty("label", out _));
        Assert.Equal(
            "10",
            unchanged.State.GetProperty("entities")
                .GetProperty("actor")
                .GetProperty("balance")
                .GetString());
        Assert.Equal(
            "95",
            unchanged.State.GetProperty("entities")
                .GetProperty("target")
                .GetProperty("balance")
                .GetString());
        Assert.Null(
            await store.FindInstanceAsync(instance.InstanceId, default));
    }

    private static WorldEventTransactionExecutionRequest Request(
        WorldEventInstance instance,
        WorldAuthoritativeCoordinate coordinate,
        string commandId,
        string operationId,
        IWorldTransactionalEventEffect effect)
    {
        return new WorldEventTransactionExecutionRequest(
            instance,
            coordinate,
            commandId,
            operationId,
            effect);
    }

    private static async Task<WorldAuthoritativeStateSnapshot>
        MutateIncarnationsAsync(
            InMemoryWorldAuthoritativeTransactionStore store,
            WorldAuthoritativeStateSnapshot source,
            string suffix,
            Action<IWorldStateDraft> mutate)
    {
        var request = MutationRequest(source, suffix);
        var begin = await store.BeginAsync(request, default);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        mutate(transaction.Draft);
        var committed = await transaction.CommitEventAsync(
            new WorldEffectReceipt(true, "applied"),
            default);
        await transaction.DisposeAsync();
        Assert.Equal(
            WorldTransactionCommitStatus.Committed,
            committed.Status);
        return Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(source.Coordinate.Address, default));
    }

    private static WorldTransactionRequest MutationRequest(
        WorldAuthoritativeStateSnapshot source,
        string suffix)
    {
        var coordinate = source.Coordinate;
        var occurrence = new WorldEventHistoryRecord(
            "incarnation-" + suffix,
            new WorldEventDefinitionKey(
                coordinate.WorldId,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                "incarnation-change",
                "1"),
            "trigger-" + suffix,
            "resolution-" + suffix,
            CatalogDigest,
            occurredAt: null);
        return new WorldTransactionRequest(
            "operation-" + suffix,
            "command-" + suffix,
            CatalogDigest,
            coordinate,
            eventOccurrence: occurrence);
    }

    private static async Task<WorldEventInstance> InstanceAsync(
        IWorldEventHistory history,
        IReadOnlyList<WorldEventParticipant>? participants = null,
        IEnumerable<string>? writes = null)
    {
        var selected = participants
                       ?? new[]
                       {
                           new WorldEventParticipant(
                               "actor",
                               1,
                               "actor")
                       };
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new PlannerCondition())
            .AddParticipantSelector(
                "selector",
                new PlannerSelector(selected))
            .AddResolver("resolver", new PlannerResolver())
            .AddEffect("effect", new PlannerEffect())
            .Build();
        var definition = new WorldEventDefinition(
            "event",
            "1",
            "event_requested",
            1,
            "condition",
            "selector",
            "resolver",
            "effect",
            writeResourceKeys: writes);
        var trigger = new WorldEvolutionTrigger(
            "trigger",
            "event_requested",
            "world",
            "timeline",
            1,
            new GameTimePoint("clock", "timeline", 1, 10));
        var plan = await new WorldEventPlanner(handlers, history)
            .PlanAsync(
                new WorldEventPlanningRequest(
                    trigger,
                    new[] { definition }));
        return Assert.Single(plan.Instances);
    }

    private static InMemoryWorldAuthoritativeTransactionStore Store(
        JsonElement? state = null,
        bool includeTarget = false)
    {
        var incarnations = new Dictionary<string, long>
        {
            ["actor"] = 1
        };
        if (includeTarget)
        {
            incarnations["target"] = 1;
        }

        return new InMemoryWorldAuthoritativeTransactionStore(
            new WorldAuthoritativeStateSnapshot(
                Coordinate(),
                state ?? Json("""{"value":"before"}"""),
                incarnations));
    }

    private static WorldAuthoritativeCoordinate Coordinate()
    {
        return new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            1,
            0,
            0,
            CatalogDigest);
    }

    private static WorldTimelineAddress Address()
    {
        return new WorldTimelineAddress("world", "timeline");
    }

    private static WorldNumericSchema BalanceSchema()
    {
        return new WorldNumericSchema(
            "balance",
            0,
            "unit",
            "0",
            "1000",
            "0");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class DelegateTransactionalEffect
        : IWorldTransactionalEventEffect
    {
        private readonly Func<WorldTransactionalEventEffectContext,
            WorldEventEffectResult> _callback;

        public DelegateTransactionalEffect(
            Func<WorldTransactionalEventEffectContext,
                WorldEventEffectResult> callback)
        {
            _callback = callback;
        }

        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldTransactionalEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(
                _callback(context));
        }
    }

    private sealed class AsyncTransactionalEffect
        : IWorldTransactionalEventEffect
    {
        private readonly Func<WorldTransactionalEventEffectContext,
            Task<WorldEventEffectResult>> _callback;

        public AsyncTransactionalEffect(
            Func<WorldTransactionalEventEffectContext,
                Task<WorldEventEffectResult>> callback)
        {
            _callback = callback;
        }

        public async ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldTransactionalEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _callback(context);
        }
    }

    private sealed class PlannerCondition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class PlannerSelector : IWorldEventParticipantSelector
    {
        private readonly IReadOnlyList<WorldEventParticipant> _participants;

        public PlannerSelector(
            IReadOnlyList<WorldEventParticipant> participants)
        {
            _participants = participants;
        }

        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                _participants);
        }
    }

    private sealed class PlannerResolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventResolution> result = new[]
            {
                new WorldEventResolution(
                    "resolution",
                    selectedParticipants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                result);
        }
    }

    private sealed class PlannerEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "planned"));
        }
    }

    private sealed class LostAcknowledgementStore
        : IWorldAuthoritativeTransactionStore
    {
        private readonly IWorldAuthoritativeTransactionStore _inner;

        private bool _hideFirstReconciliation = true;

        public LostAcknowledgementStore(
            IWorldAuthoritativeTransactionStore inner)
        {
            _inner = inner;
        }

        public ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
            WorldTimelineAddress address,
            CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(address, cancellationToken);
        }

        public async ValueTask<WorldTransactionBeginResult> BeginAsync(
            WorldTransactionRequest request,
            CancellationToken cancellationToken)
        {
            var begin = await _inner.BeginAsync(
                request,
                cancellationToken);
            return begin.Status == WorldTransactionBeginStatus.Acquired
                ? WorldTransactionBeginResult.Acquired(
                    new LostAcknowledgementTransaction(
                        begin.Transaction!))
                : begin;
        }

        public ValueTask<WorldTransactionInspectionResult> InspectAsync(
            WorldTransactionScope scope,
            string operationId,
            CancellationToken cancellationToken)
        {
            return _inner.InspectAsync(
                scope,
                operationId,
                cancellationToken);
        }

        public ValueTask<WorldTransactionReconciliationResult>
            ReconcileAsync(
                WorldTransactionScope scope,
                string operationId,
                string requestFingerprint,
                CancellationToken cancellationToken)
        {
            if (_hideFirstReconciliation)
            {
                _hideFirstReconciliation = false;
                throw new IOException(
                    "Reconciliation transport is temporarily unavailable.");
            }

            return _inner.ReconcileAsync(
                scope,
                operationId,
                requestFingerprint,
                cancellationToken);
        }

        public ValueTask<WorldTransactionReconciliationResult>
            CancelPendingAsync(
                WorldTransactionScope scope,
                string operationId,
                string requestFingerprint,
                string outcomeCode,
                CancellationToken cancellationToken)
        {
            return _inner.CancelPendingAsync(
                scope,
                operationId,
                requestFingerprint,
                outcomeCode,
                cancellationToken);
        }
    }

    private sealed class LostAcknowledgementTransaction
        : IWorldAuthoritativeTransaction
    {
        private readonly IWorldAuthoritativeTransaction _inner;

        public LostAcknowledgementTransaction(
            IWorldAuthoritativeTransaction inner)
        {
            _inner = inner;
        }

        public WorldTransactionRequest Request => _inner.Request;

        public WorldAuthoritativeStateSnapshot Source => _inner.Source;

        public IWorldStateDraft Draft => _inner.Draft;

        public async ValueTask<WorldTransactionCommitResult>
            CommitEventAsync(
                WorldEffectReceipt effect,
                CancellationToken cancellationToken)
        {
            _ = await _inner.CommitEventAsync(effect, cancellationToken);
            throw new IOException("Commit acknowledgement was lost.");
        }

        public ValueTask<WorldTransactionCommitResult>
            CompleteWithoutMutationAsync(
                WorldCommandReceiptStatus status,
                string outcomeCode,
                WorldEffectReceipt? effect,
                CancellationToken cancellationToken)
        {
            return _inner.CompleteWithoutMutationAsync(
                status,
                outcomeCode,
                effect,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }
}
