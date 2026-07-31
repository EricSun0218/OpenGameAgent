using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class FileWorldAuthoritativeTransactionStoreTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task IssuedIncarnationLedgerSurvivesRestartAndRemoval()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = Store(path);
        var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(Address(), default));
        var upgraded = await MutateIncarnationsAsync(
            store,
            initial,
            "upgrade-ledger",
            draft => draft.SetIncarnation("actor", 3));

        store = new FileWorldAuthoritativeTransactionStore(path);
        var restarted = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(Address(), default));
        Assert.Equal(3, restarted.EntityIncarnations["actor"]);
        Assert.True(restarted.WasIncarnationIssued("actor", 1));
        Assert.False(restarted.WasIncarnationIssued("actor", 2));
        Assert.True(restarted.WasIncarnationIssued("actor", 3));

        _ = await MutateIncarnationsAsync(
            store,
            upgraded,
            "remove-ledger",
            draft => draft.RemoveIncarnation("actor"));
        var removedStore =
            new FileWorldAuthoritativeTransactionStore(path);
        var removed = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await removedStore.ReadAsync(Address(), default));
        Assert.False(removed.TryGetIncarnation("actor", out _));
        Assert.True(removed.WasIncarnationIssued("actor", 1));
        Assert.True(removed.WasIncarnationIssued("actor", 3));
    }

    [Fact]
    public async Task LegacyCurrentOnlyStateSeedsLedgerOnRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        _ = Store(path);
        RewriteStorePayload(
            path,
            payload =>
            {
                var state = payload["states"]!.AsArray()[0]!.AsObject();
                var packed = DecodePackedState(state);
                var incarnations = new JsonArray();
                foreach (var pair in packed.Current)
                {
                    incarnations.Add(
                        new JsonObject
                        {
                            ["entityId"] = pair.Key,
                            ["incarnation"] = pair.Value
                        });
                }

                state["incarnations"] = incarnations;
                Assert.True(state.Remove("packedIncarnations"));
            });

        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var snapshot = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await restarted.ReadAsync(Address(), default));

        Assert.True(snapshot.WasIncarnationIssued("actor", 1));
        Assert.Single(snapshot.IssuedEntityIncarnations);
    }

    [Fact]
    public async Task LegacyArrayIncarnationLedgerSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        _ = Store(path);
        RewriteStorePayload(
            path,
            payload =>
            {
                var state = payload["states"]!.AsArray()[0]!.AsObject();
                var packed = DecodePackedState(state);
                var current = new JsonArray();
                foreach (var pair in packed.Current)
                {
                    current.Add(
                        new JsonObject
                        {
                            ["entityId"] = pair.Key,
                            ["incarnation"] = pair.Value
                        });
                }

                var issued = new JsonArray();
                foreach (var item in packed.Issued)
                {
                    issued.Add(
                        new JsonObject
                        {
                            ["entityId"] = item.EntityId,
                            ["incarnation"] = item.Incarnation
                        });
                }

                state["incarnations"] = current;
                state["issuedIncarnations"] = issued;
                Assert.True(state.Remove("packedIncarnations"));
            });

        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var snapshot = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await restarted.ReadAsync(Address(), default));

        Assert.Equal(1, snapshot.EntityIncarnations["actor"]);
        Assert.True(snapshot.WasIncarnationIssued("actor", 1));
        Assert.Single(snapshot.IssuedEntityIncarnations);
    }

    [Fact]
    public void CorruptPackedIncarnationLedgerFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        _ = Store(path);
        RewriteStorePayload(
            path,
            payload =>
            {
                var state = payload["states"]!.AsArray()[0]!.AsObject();
                var chunks = state["packedIncarnations"]!
                    .AsObject()["chunks"]!
                    .AsArray();
                var first = chunks[0]!.GetValue<string>();
                chunks[0] = "\"" + first[1..];
            });

        AssertStoreCorrupt(path);
    }

    [Fact]
    public void IssuedIncarnationCapacityRejectsInitialStateBeforePublish()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var snapshot = new WorldAuthoritativeStateSnapshot(
            Coordinate(),
            Json("""{"value":"before"}"""),
            new Dictionary<string, long> { ["actor"] = 2 },
            [
                new WorldIssuedEntityIncarnation("actor", 1),
                new WorldIssuedEntityIncarnation("actor", 2)
            ]);
        var error = Assert.Throws<
            FileWorldAuthoritativeStoreException>(
            () => new FileWorldAuthoritativeTransactionStore(
                path,
                [snapshot],
                new FileWorldAuthoritativeTransactionStoreOptions(
                    maxIssuedEntityIncarnations: 1)));

        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.CapacityExceeded,
            error.ReasonCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task BeginIsDurableBeforeReturnAndRestartNeverRedispatches()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = Store(path);
        var instance = Assert.Single(
            (await PlanAsync(store, "effect")).Instances);
        var calls = 0;
        var execution = Execution(
            instance,
            Coordinate(),
            "command",
            "operation",
            new DelegateEffect(
                _ =>
                {
                    calls++;
                    return Applied("unexpected");
                }));
        var begin = await store.BeginAsync(
            execution.TransactionRequest,
            default);
        Assert.Equal(WorldTransactionBeginStatus.Acquired, begin.Status);
        await begin.Transaction!.DisposeAsync();

        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var result = await new WorldEventTransactionExecutor(restarted)
            .ExecuteAsync(execution, default);
        var reconciliation = await restarted.ReconcileAsync(
            execution.ExpectedCoordinate.Scope,
            execution.OperationId,
            execution.TransactionRequest.RequestFingerprint,
            default);

        Assert.Equal(
            WorldTransactionExecutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            WorldTransactionReconciliationStatus.Pending,
            reconciliation.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CommitPublishesStateHistoryAndReceiptInOneRestartImage()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = Store(path);
        var instance = Assert.Single(
            (await PlanAsync(store, "effect")).Instances);
        var execution = Execution(
            instance,
            Coordinate(),
            "command",
            "operation",
            new DelegateEffect(
                context =>
                {
                    context.Draft.ReplaceState(Json("""{"value":"after"}"""));
                    return Applied("changed");
                }));
        var committed = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(execution, default);

        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var state = await restarted.ReadAsync(Address(), default);
        var history = await restarted.FindInstanceAsync(
            instance.InstanceId,
            default);
        var receipt = await restarted.ReconcileAsync(
            execution.ExpectedCoordinate.Scope,
            execution.OperationId,
            execution.TransactionRequest.RequestFingerprint,
            default);

        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            committed.Status);
        Assert.Equal(
            "after",
            state!.State.GetProperty("value").GetString());
        Assert.Equal(1, state.Coordinate.SaveRevision);
        Assert.Equal(1, state.Coordinate.StateVersion);
        Assert.NotNull(history);
        Assert.Equal(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            receipt.Status);
        Assert.Equal(committed.Receipt!.ReceiptId, receipt.Receipt!.ReceiptId);
        Assert.Equal(state.StateDigest, receipt.Receipt.ResultingStateDigest);
    }

    [Fact]
    public async Task LostCommitAcknowledgementReconcilesAndReplayDoesNotApply()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var inner = Store(path);
        var instance = Assert.Single(
            (await PlanAsync(inner, "effect")).Instances);
        var calls = 0;
        var execution = Execution(
            instance,
            Coordinate(),
            "command",
            "operation",
            new DelegateEffect(
                context =>
                {
                    calls++;
                    context.Draft.ReplaceState(Json("""{"value":"once"}"""));
                    return Applied("once");
                }));
        var store = new LoseCommitAcknowledgementStore(inner);
        var first = await new WorldEventTransactionExecutor(store)
            .ExecuteAsync(execution, default);
        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var second = await new WorldEventTransactionExecutor(restarted)
            .ExecuteAsync(execution, default);

        Assert.Equal(WorldTransactionExecutionStatus.Replayed, first.Status);
        Assert.Equal(WorldTransactionExecutionStatus.Replayed, second.Status);
        Assert.Equal(first.Receipt!.ReceiptId, second.Receipt!.ReceiptId);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CorruptTruncatedDuplicateAndOversizedFilesFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        _ = Store(path);
        var valid = File.ReadAllBytes(path);

        File.WriteAllBytes(path, valid[..(valid.Length / 2)]);
        AssertStoreCorrupt(path);

        File.WriteAllText(
            path,
            """{"contract":"x","contract":"y","contentDigest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","payload":{}}""",
            new UTF8Encoding(false));
        AssertStoreCorrupt(path);

        File.WriteAllBytes(path, valid);
        var exception = Assert.Throws<
            FileWorldAuthoritativeStoreException>(
            () => new FileWorldAuthoritativeTransactionStore(
                path,
                options:
                new FileWorldAuthoritativeTransactionStoreOptions(
                    maxFileBytes: valid.Length - 1)));
        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.ByteLimitExceeded,
            exception.ReasonCode);
    }

    [Fact]
    public void LargeStoreDigestPreservesTheExistingCanonicalAlgorithm()
    {
        var value = Json(
            """
            {
              "z": [1, 1.0, "山海", "\u0001", "<value>"],
              "a": {"b": true, "a": null}
            }
            """);

        Assert.Equal(
            CanonicalJsonDigest.ComputeSha256(value),
            WorldLargeCanonicalJsonDigest.Compute(
                value,
                WorldPackageLimits.HardMaximumFileBytes,
                "value"));
    }

    [Fact]
    public async Task CrashBeforeAtomicPublishLeavesThePreviousImageReadable()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = Store(path);
        File.WriteAllText(
            path + ".next",
            """{"incomplete":""",
            new UTF8Encoding(false));

        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var state = await restarted.ReadAsync(Address(), default);
        var request = Request("operation", "command");
        var begin = await restarted.BeginAsync(request, default);

        Assert.Equal(
            "before",
            state!.State.GetProperty("value").GetString());
        Assert.Equal(WorldTransactionBeginStatus.Acquired, begin.Status);
        Assert.True(File.Exists(path));
        await begin.Transaction!.DisposeAsync();
    }

    [Fact]
    public async Task FailedAtomicReplacementRemovesUniqueNextFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = Store(path);
        FileWorldAuthoritativeStoreException exception;
        using (new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            exception = await Assert.ThrowsAsync<
                FileWorldAuthoritativeStoreException>(
                async () => await store.BeginAsync(
                    Request("operation", "command"),
                    default));
        }

        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.AtomicReplaceFailed,
            exception.ReasonCode);
        Assert.Empty(
            Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".*.next"));
        var restarted =
            new FileWorldAuthoritativeTransactionStore(path);
        var state = await restarted.ReadAsync(Address(), default);
        var reconciliation = await restarted.ReconcileAsync(
            Coordinate().Scope,
            "operation",
            Request("operation", "command").RequestFingerprint,
            default);
        Assert.Equal(
            "before",
            state!.State.GetProperty("value").GetString());
        Assert.Equal(
            WorldTransactionReconciliationStatus.NotFound,
            reconciliation.Status);
    }

    [Fact]
    public async Task TwoStoreInstancesCannotAcquireTheSameTimeline()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var firstStore = Store(path);
        var secondStore =
            new FileWorldAuthoritativeTransactionStore(path);
        var firstRequest = Request("operation-a", "command-a");
        var secondRequest = Request("operation-b", "command-b");
        using var start = new ManualResetEventSlim(false);
        var firstTask = Task.Run(
            async () =>
            {
                start.Wait();
                return await firstStore.BeginAsync(firstRequest, default);
            });
        var secondTask = Task.Run(
            async () =>
            {
                start.Wait();
                return await secondStore.BeginAsync(secondRequest, default);
            });
        start.Set();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(
            1,
            results.Count(
                item => item.Status == WorldTransactionBeginStatus.Acquired));
        Assert.Equal(
            1,
            results.Count(
                item => item.Status == WorldTransactionBeginStatus.Busy));
        foreach (var transaction in results
                     .Where(
                         item => item.Status
                                 == WorldTransactionBeginStatus.Acquired)
                     .Select(item => item.Transaction!))
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task OperationAndCommandIdentityAreTimelineScoped()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = new FileWorldAuthoritativeTransactionStore(
            path,
            [
                Snapshot("world", "timeline-a"),
                Snapshot("world", "timeline-b")
            ]);
        var first = Request(
            "shared-operation",
            "shared-command",
            Coordinate("world", "timeline-a"));
        var second = Request(
            "shared-operation",
            "shared-command",
            Coordinate("world", "timeline-b"));
        var firstBegin = await store.BeginAsync(first, default);
        var secondBegin = await store.BeginAsync(second, default);
        var wrongScope = await store.ReconcileAsync(
            new WorldTransactionScope("world", "timeline-a", 2),
            first.OperationId,
            first.RequestFingerprint,
            default);
        var wrongCancel = await store.CancelPendingAsync(
            new WorldTransactionScope("world", "timeline-a", 2),
            first.OperationId,
            first.RequestFingerprint,
            "wrong_scope",
            default);
        var stillPending = await store.ReconcileAsync(
            first.ExpectedCoordinate.Scope,
            first.OperationId,
            first.RequestFingerprint,
            default);

        Assert.Equal(
            WorldTransactionBeginStatus.Acquired,
            firstBegin.Status);
        Assert.Equal(
            WorldTransactionBeginStatus.Acquired,
            secondBegin.Status);
        Assert.Equal(
            WorldTransactionReconciliationStatus.NotFound,
            wrongScope.Status);
        Assert.Equal(
            WorldTransactionReconciliationStatus.NotFound,
            wrongCancel.Status);
        Assert.Equal(
            WorldTransactionReconciliationStatus.Pending,
            stillPending.Status);
        await firstBegin.Transaction!.DisposeAsync();
        await secondBegin.Transaction!.DisposeAsync();
    }

    [Fact]
    public async Task OperationAndCommandIdentityAreWorldScoped()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = new FileWorldAuthoritativeTransactionStore(
            path,
            [
                Snapshot("world-a", "timeline"),
                Snapshot("world-b", "timeline")
            ]);
        var first = Request(
            "shared-operation",
            "shared-command",
            Coordinate("world-a", "timeline"));
        var second = Request(
            "shared-operation",
            "shared-command",
            Coordinate("world-b", "timeline"));

        var firstBegin = await store.BeginAsync(first, default);
        var secondBegin = await store.BeginAsync(second, default);
        var firstInspection = await store.InspectAsync(
            first.ExpectedCoordinate.Scope,
            first.OperationId,
            default);
        var secondInspection = await store.InspectAsync(
            second.ExpectedCoordinate.Scope,
            second.OperationId,
            default);

        Assert.Equal(
            WorldTransactionBeginStatus.Acquired,
            firstBegin.Status);
        Assert.Equal(
            WorldTransactionBeginStatus.Acquired,
            secondBegin.Status);
        Assert.Equal(
            "world-a",
            firstInspection.Request!.ExpectedCoordinate.WorldId);
        Assert.Equal(
            "world-b",
            secondInspection.Request!.ExpectedCoordinate.WorldId);
        await firstBegin.Transaction!.DisposeAsync();
        await secondBegin.Transaction!.DisposeAsync();
    }

    [Fact]
    public void LargeHistoryValidationRemainsNearLinear()
    {
        using var directory = new TemporaryDirectory();
        var smallPath = directory.File("small.json");
        var largePath = directory.File("large.json");
        _ = Store(smallPath);
        _ = Store(largePath);
        WriteHistorySnapshot(smallPath, 2_000);
        WriteHistorySnapshot(largePath, 8_000);
        var options = new FileWorldAuthoritativeTransactionStoreOptions(
            maxHistoryRecords: 10_000,
            maxFileBytes: WorldPackageLimits.HardMaximumFileBytes);

        _ = new FileWorldAuthoritativeTransactionStore(
            smallPath,
            options: options);
        var smallTicks = MedianLoadTicks(smallPath, options);
        var largeTicks = MedianLoadTicks(largePath, options);

        Assert.True(
            largeTicks
            <= (smallTicks * 8)
               + (Stopwatch.Frequency / 10),
            "Large history load exceeded the broad near-linear guard: "
            + smallTicks
            + " ticks for 2,000 records and "
            + largeTicks
            + " ticks for 8,000 records.");
    }

    [Fact]
    public async Task ConfiguredCapacityIsEnforcedWithoutDroppingPendingWork()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("world.json");
        var store = new FileWorldAuthoritativeTransactionStore(
            path,
            [
                Snapshot("world", "timeline-a"),
                Snapshot("world", "timeline-b")
            ],
            new FileWorldAuthoritativeTransactionStoreOptions(
                maxOperations: 1));
        var first = Request(
            "operation-a",
            "command-a",
            Coordinate("world", "timeline-a"));
        var second = Request(
            "operation-b",
            "command-b",
            Coordinate("world", "timeline-b"));
        var begin = await store.BeginAsync(first, default);

        var exception = await Assert.ThrowsAsync<
            FileWorldAuthoritativeStoreException>(
            async () => await store.BeginAsync(second, default));
        var stillPending = await store.ReconcileAsync(
            first.ExpectedCoordinate.Scope,
            first.OperationId,
            first.RequestFingerprint,
            default);

        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.CapacityExceeded,
            exception.ReasonCode);
        Assert.Equal(
            WorldTransactionReconciliationStatus.Pending,
            stillPending.Status);
        await begin.Transaction!.DisposeAsync();
    }

    private static void AssertStoreCorrupt(string path)
    {
        var exception = Assert.Throws<
            FileWorldAuthoritativeStoreException>(
            () => new FileWorldAuthoritativeTransactionStore(path));
        Assert.Equal(
            FileWorldAuthoritativeStoreReasonCodes.Corrupt,
            exception.ReasonCode);
    }

    private static long MedianLoadTicks(
        string path,
        FileWorldAuthoritativeTransactionStoreOptions options)
    {
        var samples = new long[3];
        for (var index = 0; index < samples.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = new FileWorldAuthoritativeTransactionStore(
                path,
                options: options);
            stopwatch.Stop();
            samples[index] = stopwatch.ElapsedTicks;
        }

        Array.Sort(samples);
        return samples[1];
    }

    private static void WriteHistorySnapshot(string path, int count)
    {
        var root = JsonNode.Parse(
                       File.ReadAllText(path, Encoding.UTF8))
                   ?.AsObject()
                   ?? throw new InvalidOperationException(
                       "The store fixture root is missing.");
        var payload = root["payload"]?.AsObject()
                      ?? throw new InvalidOperationException(
                          "The store fixture payload is missing.");
        var history = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString(
                "D8",
                System.Globalization.CultureInfo.InvariantCulture);
            history.Add(
                new JsonObject
                {
                    ["instanceId"] = "instance-" + suffix,
                    ["definition"] = new JsonObject
                    {
                        ["worldId"] = "world",
                        ["timelineId"] = "timeline",
                        ["timelineEpoch"] = 1,
                        ["definitionId"] = "event",
                        ["definitionVersion"] = "1"
                    },
                    ["triggerId"] = "trigger",
                    ["resolutionKey"] = "resolution-" + suffix,
                    ["planFingerprint"] = new string('b', 64),
                    ["occurredAt"] = new JsonObject
                    {
                        ["clockId"] = "clock",
                        ["timelineId"] = "timeline",
                        ["epoch"] = 1,
                        ["tick"] = index
                    },
                    ["parentInstanceId"] = null
                });
        }

        payload["history"] = history;
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

    private static void RewriteStorePayload(
        string path,
        Action<JsonObject> rewrite)
    {
        var root = JsonNode.Parse(
                       File.ReadAllText(path, Encoding.UTF8))
                   ?.AsObject()
                   ?? throw new InvalidOperationException(
                       "The store fixture root is missing.");
        var payload = root["payload"]?.AsObject()
                      ?? throw new InvalidOperationException(
                          "The store fixture payload is missing.");
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

    private static NativeWorldPackedIncarnationLedger
        DecodePackedState(JsonObject state)
    {
        var packedNode = state["packedIncarnations"]!.AsObject();
        var chunks = packedNode["chunks"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();
        return NativeWorldIncarnationLedgerCodec.Decode(
            chunks,
            packedNode["byteLength"]!.GetValue<int>(),
            WorldAuthoritativeStateSnapshot
                .MaximumIssuedIncarnationCount,
            WorldValidation.MaximumParticipants);
    }

    private static async Task<WorldAuthoritativeStateSnapshot>
        MutateIncarnationsAsync(
            FileWorldAuthoritativeTransactionStore store,
            WorldAuthoritativeStateSnapshot source,
            string suffix,
            Action<IWorldStateDraft> mutate)
    {
        var request = MutationRequest(source, suffix);
        var begin = await store.BeginAsync(request, default);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        mutate(transaction.Draft);
        var result = await transaction.CommitEventAsync(
            new WorldEffectReceipt(true, "applied"),
            default);
        await transaction.DisposeAsync();
        Assert.Equal(
            WorldTransactionCommitStatus.Committed,
            result.Status);
        return Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await store.ReadAsync(source.Coordinate.Address, default));
    }

    private static WorldTransactionRequest MutationRequest(
        WorldAuthoritativeStateSnapshot source,
        string suffix)
    {
        var coordinate = source.Coordinate;
        return new WorldTransactionRequest(
            "operation-" + suffix,
            "command-" + suffix,
            CatalogDigest,
            coordinate,
            eventOccurrence: new WorldEventHistoryRecord(
                "instance-" + suffix,
                new WorldEventDefinitionKey(
                    coordinate.WorldId,
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch,
                    "incarnation-change",
                    "1"),
                "trigger-" + suffix,
                "resolution-" + suffix,
                CatalogDigest,
                occurredAt: null));
    }

    private static FileWorldAuthoritativeTransactionStore Store(
        string path)
    {
        return new FileWorldAuthoritativeTransactionStore(
            path,
            [Snapshot("world", "timeline")]);
    }

    private static WorldAuthoritativeStateSnapshot Snapshot(
        string worldId,
        string timelineId)
    {
        return new WorldAuthoritativeStateSnapshot(
            Coordinate(worldId, timelineId),
            Json("""{"value":"before"}"""),
            new Dictionary<string, long> { ["actor"] = 1 });
    }

    private static WorldTransactionRequest Request(
        string operationId,
        string commandId,
        WorldAuthoritativeCoordinate? coordinate = null)
    {
        return new WorldTransactionRequest(
            operationId,
            commandId,
            CatalogDigest,
            coordinate ?? Coordinate(),
            [new WorldEntityIncarnationExpectation("actor", 1)]);
    }

    private static WorldEventTransactionExecutionRequest Execution(
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

    private static async Task<WorldEventPlan> PlanAsync(
        IWorldEventHistory history,
        params string[] effectHandlerIds)
    {
        var builder = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver());
        var definitions = new List<WorldEventDefinition>();
        for (var index = 0; index < effectHandlerIds.Length; index++)
        {
            var effectId = effectHandlerIds[index];
            builder.AddEffect(effectId, new PlanningEffect());
            definitions.Add(
                new WorldEventDefinition(
                    "event-" + index,
                    "1",
                    "tick",
                    effectHandlerIds.Length - index,
                    "condition",
                    "selector",
                    "resolver",
                    effectId,
                    writeResourceKeys: ["state:value"]));
        }

        return await new WorldEventPlanner(builder.Build(), history)
            .PlanAsync(
                new WorldEventPlanningRequest(
                    new WorldEvolutionTrigger(
                        "trigger",
                        "tick",
                        "world",
                        "timeline",
                        1,
                        new GameTimePoint(
                            "clock",
                            "timeline",
                            1,
                            1)),
                    definitions));
    }

    private static WorldAuthoritativeCoordinate Coordinate(
        string worldId = "world",
        string timelineId = "timeline",
        long timelineEpoch = 1,
        long saveRevision = 0,
        long stateVersion = 0,
        string catalogDigest = CatalogDigest)
    {
        return new WorldAuthoritativeCoordinate(
            worldId,
            timelineId,
            timelineEpoch,
            saveRevision,
            stateVersion,
            catalogDigest);
    }

    private static WorldTimelineAddress Address()
    {
        return new WorldTimelineAddress("world", "timeline");
    }

    private static WorldEventEffectResult Applied(string outcome)
    {
        return new WorldEventEffectResult(true, outcome);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-world-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string File(string name)
        {
            return System.IO.Path.Combine(_path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }

    private sealed class Condition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class Selector : IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventParticipant> participants =
            [
                new WorldEventParticipant("actor", 1, "actor")
            ];
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                participants);
        }
    }

    private sealed class Resolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventResolution> resolutions =
            [
                new WorldEventResolution(
                    "resolution",
                    selectedParticipants)
            ];
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                resolutions);
        }
    }

    private sealed class PlanningEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventEffectResult>(Applied("planned"));
        }
    }

    private sealed class DelegateEffect(
        Func<
                WorldTransactionalEventEffectContext,
                WorldEventEffectResult> apply) : IWorldTransactionalEventEffect
    {
        private readonly Func<
            WorldTransactionalEventEffectContext,
            WorldEventEffectResult> _apply = apply;

        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldTransactionalEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(_apply(context));
        }
    }

    private sealed class LoseCommitAcknowledgementStore(
        IWorldAuthoritativeTransactionStore inner)
                : IWorldAuthoritativeTransactionStore
    {
        private readonly IWorldAuthoritativeTransactionStore _inner = inner;

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
            var result = await _inner.BeginAsync(request, cancellationToken);
            return result.Status == WorldTransactionBeginStatus.Acquired
                ? WorldTransactionBeginResult.Acquired(
                    new LoseCommitAcknowledgementTransaction(
                        result.Transaction!))
                : result;
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

    private sealed class LoseCommitAcknowledgementTransaction(
        IWorldAuthoritativeTransaction inner)
                : IWorldAuthoritativeTransaction
    {
        private readonly IWorldAuthoritativeTransaction _inner = inner;

        public WorldTransactionRequest Request => _inner.Request;

        public WorldAuthoritativeStateSnapshot Source => _inner.Source;

        public IWorldStateDraft Draft => _inner.Draft;

        public async ValueTask<WorldTransactionCommitResult>
            CommitEventAsync(
                WorldEffectReceipt effect,
                CancellationToken cancellationToken)
        {
            _ = await _inner.CommitEventAsync(effect, cancellationToken);
            throw new IOException("The commit acknowledgement was lost.");
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
