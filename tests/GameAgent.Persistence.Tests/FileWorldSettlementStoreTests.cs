using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using Xunit;

namespace GameAgent.Persistence.Tests;

public sealed class FileWorldSettlementStoreTests
{
    [Fact]
    public async Task SettledQuiescenceLeaseBlocksNewOutboxMutation()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileWorldSettlementStore(
            System.IO.Path.Combine(directory.Path, "quiescence.log"));
        var source = Assert.IsAssignableFrom<
            IWorldSettlementQuiescenceSource>(store);
        var lease = Assert.IsAssignableFrom<
            IWorldSettlementQuiescenceLease>(
            await source.TryAcquireSettledQuiescenceAsync());
        var beginTask = store.BeginAsync(
                Plan("blocked-by-capture", "blocked-delivery"))
            .AsTask();
        await Task.Yield();
        Assert.False(beginTask.IsCompleted);

        await lease.DisposeAsync();
        var begin = await beginTask;
        Assert.Equal(WorldSettlementBeginStatus.Created, begin.Status);
        Assert.Null(
            await source.TryAcquireSettledQuiescenceAsync());
    }

    [Fact]
    public async Task RoundTripPreservesPlanStagesAndCompareAndSwap()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settlements.log");
        var plan = Plan("settlement-1", "memory-op-1");
        await using (var store = new FileWorldSettlementStore(path))
        {
            var begun = await store.BeginAsync(plan);
            var duplicate = await store.BeginAsync(plan);
            var intent = await store.TryTransitionAsync(
                Transition(
                    plan,
                    revision: 0,
                    WorldSettlementStage.Pending,
                    WorldSettlementStage.Reconciliation));
            var stale = await store.TryTransitionAsync(
                Transition(
                    plan,
                    revision: 0,
                    WorldSettlementStage.Pending,
                    WorldSettlementStage.Reconciliation));
            var applied = await store.TryTransitionAsync(
                Transition(
                    plan,
                    revision: 1,
                    WorldSettlementStage.Reconciliation,
                    WorldSettlementStage.Applied));

            Assert.Equal(WorldSettlementBeginStatus.Created, begun.Status);
            Assert.Equal(
                WorldSettlementBeginStatus.Existing,
                duplicate.Status);
            Assert.Equal(
                WorldSettlementTransitionStatus.Applied,
                intent.Status);
            Assert.Equal(
                WorldSettlementTransitionStatus.Conflict,
                stale.Status);
            Assert.Equal(
                WorldSettlementTransitionStatus.Applied,
                applied.Status);
            Assert.Equal(3, store.StoreRevision);
            Assert.Empty(
                (await store.ListUnsettledAsync(
                    new WorldSettlementListRequest(1))).Items);
        }

        await using var recovered = new FileWorldSettlementStore(path);
        var record = await recovered.ReadAsync(plan.SettlementId);
        Assert.NotNull(record);
        Assert.Equal(2, record!.Revision);
        Assert.Equal(WorldSettlementStage.Applied, record.Stage);
        Assert.Equal(plan.SemanticDigest, record.Plan.SemanticDigest);
        Assert.Equal(3, recovered.StoreRevision);
        Assert.Equal(1, recovered.RecordCount);
    }

    [Fact]
    public async Task RoundTripPreservesEveryTypedSinkDraft()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "all-sinks.log");
        var plan = AllSinkPlan();
        await using (var store = new FileWorldSettlementStore(path))
        {
            _ = await store.BeginAsync(plan);
        }

        await using var recovered = new FileWorldSettlementStore(path);
        var record = await recovered.ReadAsync(plan.SettlementId);

        Assert.NotNull(record);
        Assert.Equal(plan.SemanticDigest, record!.Plan.SemanticDigest);
        Assert.Equal(3, record.Plan.Deliveries.Count);
        var memory = Assert.IsType<WorldSettlementMemoryDelivery>(
            record.Plan.Deliveries[0]);
        var group = Assert.IsType<WorldSettlementGroupDelivery>(
            record.Plan.Deliveries[1]);
        var presentation =
            Assert.IsType<WorldSettlementPresentationDelivery>(
                record.Plan.Deliveries[2]);
        Assert.Equal("private fact", memory.Mutations[0].Record!
            .Content.GetProperty("text").GetString());
        Assert.Equal("group-session", group.Request.SessionId);
        Assert.Equal(
            plan.Source.WorldReceiptId,
            group.Request.Messages[0].CausationId);
        Assert.Equal(
            "visible notice",
            presentation.Draft.Content.Payload
                .GetProperty("text")
                .GetString());
        Assert.Equal(-1, presentation.ExpectedPreviousContentRevision);
    }

    [Fact]
    public async Task TornTransitionTailRecoversOnlyCommittedPendingRecord()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settlements.log");
        var plan = Plan("settlement-torn", "memory-op-torn");
        var fault = new PartialSecondFrameFaultInjector();
        await using (var store = new FileWorldSettlementStore(
                         path,
                         new FileWorldSettlementStoreOptions
                         {
                             FaultInjector = fault
                         }))
        {
            _ = await store.BeginAsync(plan);
            _ = await Assert.ThrowsAsync<IOException>(
                () => store.TryTransitionAsync(
                        Transition(
                            plan,
                            revision: 0,
                            WorldSettlementStage.Pending,
                            WorldSettlementStage.Reconciliation))
                    .AsTask());
            await Assert.ThrowsAsync<
                FileWorldSettlementStoreFaultedException>(
                () => store.ReadAsync(plan.SettlementId).AsTask());
        }

        await using (var recovered = new FileWorldSettlementStore(path))
        {
            var pending = await recovered.ReadAsync(plan.SettlementId);
            Assert.NotNull(pending);
            Assert.Equal(0, pending!.Revision);
            Assert.Equal(WorldSettlementStage.Pending, pending.Stage);
            Assert.Equal(1, recovered.StoreRevision);

            _ = await recovered.TryTransitionAsync(
                Transition(
                    plan,
                    revision: 0,
                    WorldSettlementStage.Pending,
                    WorldSettlementStage.Reconciliation));
        }

        await using var final = new FileWorldSettlementStore(path);
        var reconciled = await final.ReadAsync(plan.SettlementId);
        Assert.Equal(
            WorldSettlementStage.Reconciliation,
            reconciled!.Stage);
        Assert.Equal(2, final.StoreRevision);
    }

    [Fact]
    public async Task CommittedPlanDigestCorruptionFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settlements.log");
        var plan = Plan("settlement-corrupt", "memory-op-corrupt");
        await using (var store = new FileWorldSettlementStore(path))
        {
            _ = await store.BeginAsync(plan);
        }

        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.UTF8.GetBytes("\"planDigest\":\"");
        var markerOffset = FindBytes(
            bytes,
            FileWorldSettlementStore.FrameHeaderSize,
            ReadInt32(bytes, 4),
            marker);
        Assert.True(markerOffset >= 0);
        var digestOffset = checked(
            FileWorldSettlementStore.FrameHeaderSize
            + markerOffset
            + marker.Length);
        bytes[digestOffset] = bytes[digestOffset] == (byte)'0'
            ? (byte)'1'
            : (byte)'0';
        WriteUInt32(
            bytes,
            offset: 8,
            ComputeCrc32(
                bytes,
                FileWorldSettlementStore.FrameHeaderSize,
                ReadInt32(bytes, 4)));
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<FileWorldSettlementStoreCorruptionException>(
            () => new FileWorldSettlementStore(path));
    }

    [Fact]
    public async Task CapacityEnumerationAndWriterLeaseAreBounded()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settlements.log");
        var options = new FileWorldSettlementStoreOptions
        {
            MaxRecords = 1
        };
        await using var store = new FileWorldSettlementStore(path, options);
        var first = await store.BeginAsync(Plan("settlement-a", "memory-a"));
        var second = await store.BeginAsync(
            Plan("settlement-b", "memory-b"));

        Assert.Equal(WorldSettlementBeginStatus.Created, first.Status);
        Assert.Equal(
            WorldSettlementBeginStatus.CapacityExceeded,
            second.Status);
        Assert.Single(
            (await store.ListUnsettledAsync(
                new WorldSettlementListRequest(1))).Items);
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldSettlementListRequest(0));
        _ = Assert.Throws<IOException>(
            () => new FileWorldSettlementStore(path, options));
    }

    [Fact]
    public async Task WriteAdmissionCannotCreateUnreadableTokenFrame()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "tokens.log");
        var options = new FileWorldSettlementStoreOptions
        {
            MaxFrameJsonTokens = 1_024
        };
        var largeContent = JsonArrayBuilder.Object(
            ("values", JsonArrayBuilder.Array(
                Enumerable.Range(0, 1_500)
                    .Select(index => JsonArrayBuilder.Number(index)))));
        await using (var store = new FileWorldSettlementStore(path, options))
        {
            var error = await Assert.ThrowsAsync<
                FileWorldSettlementStoreCapacityException>(
                () => store.BeginAsync(
                        Plan(
                            "settlement-token-limit",
                            "memory-token-limit",
                            largeContent))
                    .AsTask());

            Assert.Equal(
                nameof(
                    FileWorldSettlementStoreOptions.MaxFrameJsonTokens),
                error.LimitName);
            Assert.Equal(0, store.StoreRevision);
            Assert.Equal(0, store.RecordCount);
            Assert.Equal(0, new FileInfo(path).Length);
        }

        await using var recovered = new FileWorldSettlementStore(
            path,
            options);
        Assert.Equal(0, recovered.StoreRevision);
        Assert.Equal(0, recovered.RecordCount);
    }

    [Fact]
    public async Task FileEnumerationCursorReachesPastBlockedPrefix()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "pages.log");
        await using var store = new FileWorldSettlementStore(path);
        _ = await store.BeginAsync(Plan("a-blocked", "memory-a"));
        _ = await store.BeginAsync(Plan("z-ready", "memory-z"));

        var first = await store.ListUnsettledAsync(
            new WorldSettlementListRequest(maxResults: 1));
        var second = await store.ListUnsettledAsync(
            new WorldSettlementListRequest(
                maxResults: 1,
                first.ContinuationCursor));

        Assert.Equal("a-blocked", Assert.Single(first.Items)
            .SettlementId);
        Assert.True(first.HasMore);
        Assert.Equal("z-ready", Assert.Single(second.Items)
            .SettlementId);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task DispatchIntentRequiresReservedTerminalFrame()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(
            directory.Path,
            "terminal-reserve.log");
        var options = new FileWorldSettlementStoreOptions
        {
            MaxMutationFrames = 2
        };
        var plan = Plan("reserve-settlement", "reserve-memory");
        await using (var store = new FileWorldSettlementStore(
                         path,
                         options))
        {
            _ = await store.BeginAsync(plan);

            var error = await Assert.ThrowsAsync<
                FileWorldSettlementStoreCapacityException>(
                () => store.TryTransitionAsync(
                        Transition(
                            plan,
                            revision: 0,
                            WorldSettlementStage.Pending,
                            WorldSettlementStage.Reconciliation))
                    .AsTask());
            var pending = await store.ReadAsync(plan.SettlementId);

            Assert.Equal(
                nameof(
                    FileWorldSettlementStoreOptions.MaxMutationFrames),
                error.LimitName);
            Assert.Equal(WorldSettlementStage.Pending, pending!.Stage);
            Assert.Equal(1, store.StoreRevision);
        }

        await using var recovered = new FileWorldSettlementStore(
            path,
            options);
        Assert.Equal(
            WorldSettlementStage.Pending,
            (await recovered.ReadAsync(plan.SettlementId))!.Stage);
    }

    private static WorldSettlementTransition Transition(
        WorldSettlementPlan plan,
        long revision,
        WorldSettlementStage expected,
        WorldSettlementStage next)
    {
        return new WorldSettlementTransition(
            plan.SettlementId,
            plan.SemanticDigest,
            revision,
            plan.Deliveries[0].OperationId,
            expected,
            next,
            next == WorldSettlementStage.Reconciliation
                ? WorldSettlementReasonCodes.DispatchIntentCommitted
                : WorldSettlementReasonCodes.Applied);
    }

    private static WorldSettlementPlan Plan(
        string settlementId,
        string operationId,
        JsonElement? content = null)
    {
        var owner = new GameEntityIdentity("actor", 2);
        var source = new WorldPresentationSource(
            "receipt",
            Digest("receipt"),
            operationId: "world-operation");
        var binding = new WorldPresentationBinding(
            "world",
            "timeline",
            timelineEpoch: 3,
            saveRevision: 4,
            stateVersion: 5,
            catalogDigest: Digest("catalog"),
            gameTime: new GameTimePoint(
                "turn",
                "timeline",
                epoch: 3,
                tick: 10),
            committedStateDigest: Digest("state"));
        var audience = new WorldSettlementAudienceClaim(
            "actor:actor",
            membershipRevision: 1,
            new[] { owner },
            WorldSettlementPrivacyClasses.Private,
            redactionClass: "none");
        var memory = new MemoryRecord(
            $"record-{operationId}",
            "actor:actor",
            content ?? Json("""{"fact":"remember"}"""),
            tags: null,
            importance: 50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                binding.WorldId,
                sessionId: null,
                binding.SaveRevision,
                sourceRunId: "run",
                sourceEventId: source.WorldReceiptId,
                committed: true,
                binding.TimelineId,
                new GameKnowledgePerspective(owner, "observed"),
                binding.TimelineEpoch),
            gameTimeWindow: new GameTimeWindow(
                validFrom: binding.GameTime));
        return new WorldSettlementPlan(
            settlementId,
            new CommittedWorldPresentationEvidence(
                source,
                binding,
                WorldPresentationCommitStatus.Applied,
                "applied"),
            new[]
            {
                new WorldSettlementMemoryDelivery(
                    operationId,
                    audience,
                    new[] { MemoryMutation.Upsert(memory) })
            });
    }

    private static WorldSettlementPlan AllSinkPlan()
    {
        var alice = new GameEntityIdentity("alice", 1);
        var bob = new GameEntityIdentity("bob", 5);
        var source = new WorldPresentationSource(
            "receipt-all",
            Digest("receipt-all"),
            occurrenceId: "occurrence-all",
            actionId: "action-all",
            operationId: "world-operation-all");
        var binding = new WorldPresentationBinding(
            "world",
            "timeline",
            timelineEpoch: 7,
            saveRevision: 8,
            stateVersion: 9,
            catalogDigest: Digest("catalog-all"),
            gameTime: new GameTimePoint(
                "month",
                "timeline",
                epoch: 7,
                tick: 11),
            committedStateDigest: Digest("state-all"));
        var memory = new MemoryRecord(
            "private-memory",
            "actor:alice",
            Json("""{"text":"private fact"}"""),
            tags: new[] { "receipt" },
            importance: 70,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                binding.WorldId,
                sessionId: null,
                binding.SaveRevision,
                sourceRunId: "run-all",
                sourceEventId: source.WorldReceiptId,
                committed: true,
                binding.TimelineId,
                new GameKnowledgePerspective(alice, "observed"),
                binding.TimelineEpoch),
            gameTimeWindow: new GameTimeWindow(
                validFrom: binding.GameTime));
        var groupRequest = new GroupInteractionAppendRequest(
            "group-delivery",
            "group-session",
            expectedRevision: 4,
            expectedMembershipRevision: 2,
            new[]
            {
                new GroupInteractionMessageDraft(
                    "message-all",
                    "world.notice",
                    Json("""{"text":"shared fact"}"""),
                    GroupInteractionAudienceModes.Explicit,
                    author: alice,
                    audience: new[] { bob },
                    causationId: source.WorldReceiptId)
            });
        var presentation = new WorldPresentationDraft(
            "presentation-all",
            contentRevision: 0,
            source,
            binding,
            new WorldPresentationAudience(
                "group-session",
                membershipRevision: 2,
                new[] { alice, bob },
                privacyClass: "group",
                redactionClass: "none"),
            new WorldPresentationContent(
                "world.notice",
                "application/json",
                Json("""{"text":"visible notice"}""")),
            new WorldPresentationProvenance(
                "test",
                "1",
                "receipt_projection"));
        return new WorldSettlementPlan(
            "settlement-all",
            new CommittedWorldPresentationEvidence(
                source,
                binding,
                WorldPresentationCommitStatus.Applied,
                "applied",
                Json("""{"receiptVersion":1}""")),
            new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    "memory-delivery",
                    new WorldSettlementAudienceClaim(
                        "actor:alice",
                        membershipRevision: 1,
                        new[] { alice },
                        WorldSettlementPrivacyClasses.Private,
                        redactionClass: "none"),
                    new[] { MemoryMutation.Upsert(memory) }),
                new WorldSettlementGroupDelivery(
                    "group-delivery",
                    "group",
                    new[]
                    {
                        new GroupInteractionMember(
                            alice,
                            new[] { "speaker" }),
                        new GroupInteractionMember(
                            bob,
                            new[] { "listener" })
                    },
                    groupRequest),
                new WorldSettlementPresentationDelivery(
                    "presentation-delivery",
                    presentation,
                    expectedPreviousContentRevision: -1)
            });
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string Digest(string value)
    {
        return CanonicalJsonDigest.ComputeSha256(
            JsonArrayBuilder.String(value));
    }

    private static int FindBytes(
        byte[] haystack,
        int offset,
        int count,
        byte[] needle)
    {
        var last = offset + count - needle.Length;
        for (var candidate = offset; candidate <= last; candidate++)
        {
            var matches = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (haystack[candidate + index] == needle[index])
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return candidate - offset;
            }
        }

        return -1;
    }

    private static uint ComputeCrc32(
        byte[] value,
        int offset,
        int count)
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var entry = index;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0
                    ? entry >> 1 ^ polynomial
                    : entry >> 1;
            }

            table[index] = entry;
        }

        var checksum = uint.MaxValue;
        for (var index = offset; index < offset + count; index++)
        {
            checksum = table[(checksum ^ value[index]) & 0xFF]
                       ^ checksum >> 8;
        }

        return ~checksum;
    }

    private static int ReadInt32(byte[] value, int offset)
    {
        return unchecked((int)(
            value[offset]
            | value[offset + 1] << 8
            | value[offset + 2] << 16
            | value[offset + 3] << 24));
    }

    private static void WriteUInt32(
        byte[] value,
        int offset,
        uint input)
    {
        value[offset] = (byte)input;
        value[offset + 1] = (byte)(input >> 8);
        value[offset + 2] = (byte)(input >> 16);
        value[offset + 3] = (byte)(input >> 24);
    }

    private sealed class PartialSecondFrameFaultInjector
        : IJournalFaultInjector
    {
        private int _frames;

        public int GetWriteLength(int frameLength)
        {
            return Interlocked.Increment(ref _frames) == 2
                ? frameLength / 2
                : frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "game-agent-settlement-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
