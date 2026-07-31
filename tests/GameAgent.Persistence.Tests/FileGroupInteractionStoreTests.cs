using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence.Tests;

public sealed class FileGroupInteractionStoreTests
{
    [Fact]
    public async Task RestartRestoresCommittedSessionAndProjection()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);
        var bob = Identity("bob", 1);

        await using (var store = new FileGroupInteractionStore(path))
        {
            Assert.Equal(
                GroupInteractionWriteStatuses.Applied,
                (await store.CreateAsync(
                    Create(
                        "create",
                        "session",
                        alice,
                        bob))).Status);
            Assert.Equal(
                GroupInteractionWriteStatuses.Applied,
                (await store.AppendAsync(
                    ExplicitAppend(
                        "append",
                        "session",
                        expectedRevision: 0,
                        expectedMembershipRevision: 0,
                        alice,
                        bob))).Status);
            Assert.Equal(2, store.StoreRevision);
        }

        await using var recovered = new FileGroupInteractionStore(path);
        var session = await recovered.ReadAsync("session");
        var projection = await recovered.ProjectAsync("session", bob);

        Assert.NotNull(session);
        Assert.Equal(1, session.Revision);
        Assert.Single(session.Messages);
        Assert.Equal("private-message", session.Messages[0].MessageId);
        Assert.NotNull(projection);
        Assert.Single(projection.Messages);
        Assert.Equal(2, recovered.StoreRevision);
    }

    [Fact]
    public async Task CasConflictsDoNotAppendOrChangeRecoveredState()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);

        await using (var store = new FileGroupInteractionStore(path))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            var revisionConflict = await store.AppendAsync(
                Append(
                    "stale",
                    "session",
                    expectedRevision: 1,
                    expectedMembershipRevision: 0,
                    alice));
            var membershipConflict = await store.AppendAsync(
                Append(
                    "stale-membership",
                    "session",
                    expectedRevision: 0,
                    expectedMembershipRevision: 1,
                    alice,
                    messageId: "membership-message"));

            Assert.Equal(
                GroupInteractionWriteStatuses.RevisionConflict,
                revisionConflict.Status);
            Assert.Equal(
                GroupInteractionWriteStatuses
                    .MembershipRevisionConflict,
                membershipConflict.Status);
            Assert.Equal(1, store.StoreRevision);
            Assert.Equal(0, revisionConflict.Session!.Revision);
            Assert.Equal(0, membershipConflict.Session!.Revision);
        }

        await using var recovered = new FileGroupInteractionStore(path);
        var session = await recovered.ReadAsync("session");
        Assert.NotNull(session);
        Assert.Equal(0, session.Revision);
        Assert.Empty(session.Messages);
        Assert.Equal(1, recovered.StoreRevision);
    }

    [Fact]
    public async Task DuplicateRequestIsIdempotentAndConflictIsStable()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);
        var request = Append(
            "append",
            "session",
            expectedRevision: 0,
            expectedMembershipRevision: 0,
            alice);

        await using (var store = new FileGroupInteractionStore(path))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            var applied = await store.AppendAsync(request);
            Assert.Equal(
                GroupInteractionWriteStatuses.Applied,
                applied.Status);
            Assert.Equal(2, store.StoreRevision);
        }

        await using (var recovered = new FileGroupInteractionStore(path))
        {
            var replay = await recovered.AppendAsync(request);
            var conflict = await recovered.AppendAsync(
                Append(
                    "append",
                    "session",
                    expectedRevision: 0,
                    expectedMembershipRevision: 0,
                    alice,
                    messageId: "different-message"));

            Assert.Equal(
                GroupInteractionWriteStatuses.Idempotent,
                replay.Status);
            Assert.Equal(1, replay.AppliedRevision);
            Assert.Equal(
                GroupInteractionWriteStatuses.OperationConflict,
                conflict.Status);
            Assert.Equal(2, recovered.StoreRevision);
            var session = await recovered.ReadAsync("session");
            Assert.NotNull(session);
            Assert.Single(session.Messages);
            Assert.Equal(2, session.Operations.Count);
        }
    }

    [Fact]
    public async Task MembershipHistoryPreservesHistoricalAudienceAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);
        var oldBob = Identity("bob", 1);
        var newBob = Identity("bob", 2);

        await using (var store = new FileGroupInteractionStore(path))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice, oldBob));
            _ = await store.AppendAsync(
                ExplicitAppend(
                    "private",
                    "session",
                    expectedRevision: 0,
                    expectedMembershipRevision: 0,
                    alice,
                    oldBob));
            var replaced = await store.ReplaceMembersAsync(
                new GroupInteractionMembershipRequest(
                    "replace",
                    "session",
                    expectedRevision: 1,
                    expectedMembershipRevision: 0,
                    new[]
                    {
                        new GroupInteractionMember(alice),
                        new GroupInteractionMember(newBob)
                    }));
            Assert.Equal(
                GroupInteractionWriteStatuses.Applied,
                replaced.Status);
        }

        await using var recovered = new FileGroupInteractionStore(path);
        var session = await recovered.ReadAsync("session");
        var aliceProjection = await recovered.ProjectAsync(
            "session",
            alice);
        var newProjection = await recovered.ProjectAsync(
            "session",
            newBob);

        Assert.NotNull(session);
        Assert.Equal(2, session.MembershipHistory.Count);
        Assert.Equal(1, session.Messages[0].Audience[0].Incarnation);
        Assert.NotNull(aliceProjection);
        Assert.Empty(aliceProjection.Messages);
        Assert.NotNull(newProjection);
        Assert.Empty(newProjection.Messages);
    }

    [Fact]
    public async Task TornTailIsTruncatedToLastCommittedFrame()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);
        var injector = new PartialSecondFrameFaultInjector();
        long committedLength;

        await using (var store = new FileGroupInteractionStore(
                         path,
                         new FileGroupInteractionStoreOptions
                         {
                             FaultInjector = injector
                         }))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            committedLength = new FileInfo(path).Length;
            _ = await Assert.ThrowsAsync<IOException>(
                () => store.AppendAsync(
                        Append(
                            "append",
                            "session",
                            0,
                            0,
                            alice))
                    .AsTask());
            _ = await Assert.ThrowsAsync<
                FileGroupInteractionStoreFaultedException>(
                () => store.ReadAsync("session").AsTask());
        }

        Assert.True(new FileInfo(path).Length > committedLength);
        await using var recovered = new FileGroupInteractionStore(path);
        var session = await recovered.ReadAsync("session");

        Assert.Equal(committedLength, new FileInfo(path).Length);
        Assert.NotNull(session);
        Assert.Equal(0, session.Revision);
        Assert.Empty(session.Messages);
        Assert.Equal(1, recovered.StoreRevision);
    }

    [Fact]
    public async Task MidFileChecksumCorruptionFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);

        await using (var store = new FileGroupInteractionStore(path))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            _ = await store.AppendAsync(
                Append("append", "session", 0, 0, alice));
        }

        var bytes = await File.ReadAllBytesAsync(path);
        bytes[FileGroupInteractionStore.FrameHeaderSize + 10] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);

        var error = Assert.Throws<
            FileGroupInteractionStoreCorruptionException>(
            () => new FileGroupInteractionStore(path));
        Assert.Equal(0, error.Offset);
        Assert.Contains(
            "checksum",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MutationFrameCapacityRejectsWithoutChangingState()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var alice = Identity("alice", 1);
        var options = new FileGroupInteractionStoreOptions
        {
            MaxMutationFrames = 1
        };

        await using (var store = new FileGroupInteractionStore(
                         path,
                         options))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            var error = await Assert.ThrowsAsync<
                FileGroupInteractionStoreCapacityException>(
                () => store.AppendAsync(
                        Append(
                            "append",
                            "session",
                            0,
                            0,
                            alice))
                    .AsTask());

            Assert.Equal(
                nameof(
                    FileGroupInteractionStoreOptions.MaxMutationFrames),
                error.LimitName);
            Assert.Equal(1, store.StoreRevision);
            Assert.Equal(
                0,
                (await store.ReadAsync("session"))!.Revision);
        }

        await using var recovered = new FileGroupInteractionStore(
            path,
            options);
        Assert.Equal(
            0,
            (await recovered.ReadAsync("session"))!.Revision);
    }

    [Fact]
    public async Task OversizedFrameAndSessionCountFailBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        var framePath = directory.File("frame-limit.log");
        var sessionPath = directory.File("session-limit.log");
        var alice = Identity("alice", 1);

        await using (var frameStore = new FileGroupInteractionStore(
                         framePath,
                         new FileGroupInteractionStoreOptions
                         {
                             MaxFramePayloadBytes = 1_024,
                             MaxLogBytes = 2_048
                         }))
        {
            var error = await Assert.ThrowsAsync<
                FileGroupInteractionStoreCapacityException>(
                () => frameStore.CreateAsync(
                        new GroupInteractionCreateRequest(
                            "create",
                            "large",
                            "group",
                            Json(
                                "{\"value\":\""
                                + new string('x', 2_000)
                                + "\"}"),
                            new[]
                            {
                                new GroupInteractionMember(alice)
                            }))
                    .AsTask());
            Assert.Equal(
                nameof(
                    FileGroupInteractionStoreOptions
                        .MaxFramePayloadBytes),
                error.LimitName);
            Assert.Equal(0, frameStore.StoreRevision);
            Assert.Null(await frameStore.ReadAsync("large"));
        }

        await using var sessionStore = new FileGroupInteractionStore(
            sessionPath,
            new FileGroupInteractionStoreOptions
            {
                MaxSessions = 1
            });
        _ = await sessionStore.CreateAsync(
            Create("create-one", "one", alice));
        var sessionError = await Assert.ThrowsAsync<
            FileGroupInteractionStoreCapacityException>(
            () => sessionStore.CreateAsync(
                    Create("create-two", "two", alice))
                .AsTask());
        Assert.Equal(
            nameof(FileGroupInteractionStoreOptions.MaxSessions),
            sessionError.LimitName);
        Assert.Null(await sessionStore.ReadAsync("two"));
    }

    [Fact]
    public async Task LogByteCapacityRejectsBeforePublishingSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("log-limit.log");
        var alice = Identity("alice", 1);
        var options = new FileGroupInteractionStoreOptions
        {
            Limits = new GroupInteractionLimits(
                maxMessages: 4,
                maxOperations: 5,
                maxMessagesPerAppend: 1,
                maxPayloadUtf8Bytes: 262_144,
                maxTotalPayloadUtf8Bytes: 1_048_576),
            MaxFramePayloadBytes = 1_048_576,
            MaxLogBytes =
                1_048_576
                + FileGroupInteractionStore.FrameHeaderSize
                + FileGroupInteractionStore.FrameFooterSize
        };
        var payload = Json(
            "{\"value\":\"" + new string('x', 240_000) + "\"}");

        await using (var store = new FileGroupInteractionStore(
                         path,
                         options))
        {
            _ = await store.CreateAsync(
                Create("create", "session", alice));
            _ = await store.AppendAsync(
                LargeAppend("append-one", "message-one", 0, alice, payload));
            _ = await store.AppendAsync(
                LargeAppend("append-two", "message-two", 1, alice, payload));
            var committedLength = new FileInfo(path).Length;

            var error = await Assert.ThrowsAsync<
                FileGroupInteractionStoreCapacityException>(
                () => store.AppendAsync(
                        LargeAppend(
                            "append-three",
                            "message-three",
                            2,
                            alice,
                            payload))
                    .AsTask());

            Assert.Equal(
                nameof(FileGroupInteractionStoreOptions.MaxLogBytes),
                error.LimitName);
            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(3, store.StoreRevision);
            Assert.Equal(
                2,
                (await store.ReadAsync("session"))!.Revision);
        }

        await using var recovered = new FileGroupInteractionStore(
            path,
            options);
        var session = await recovered.ReadAsync("session");
        Assert.NotNull(session);
        Assert.Equal(2, session.Revision);
        Assert.Equal(2, session.Messages.Count);
    }

    [Fact]
    public async Task CancellationBeforeAndWhileWaitingDoesNotPublish()
    {
        using var directory = new TemporaryDirectory();
        var cancelledPath = directory.File("cancelled.log");
        var blockedPath = directory.File("blocked.log");
        var alice = Identity("alice", 1);

        await using (var cancelledStore =
                     new FileGroupInteractionStore(cancelledPath))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelledStore.CreateAsync(
                        Create("create", "session", alice),
                        cancelled.Token)
                    .AsTask());
            Assert.Equal(0, cancelledStore.StoreRevision);
            Assert.Equal(0, new FileInfo(cancelledPath).Length);
        }

        var blocker = new BlockingWriteFaultInjector();
        await using var blockedStore = new FileGroupInteractionStore(
            blockedPath,
            new FileGroupInteractionStoreOptions
            {
                FaultInjector = blocker
            });
        var createTask = Task.Run(
            async () => await blockedStore.CreateAsync(
                Create("create", "session", alice)));
        Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => blockedStore.AppendAsync(
                        Append(
                            "cancelled-append",
                            "session",
                            0,
                            0,
                            alice),
                        timeout.Token)
                    .AsTask());
        }
        finally
        {
            blocker.Release.Set();
        }

        Assert.Equal(
            GroupInteractionWriteStatuses.Applied,
            (await createTask).Status);
        var committed = await blockedStore.ReadAsync("session");
        Assert.NotNull(committed);
        Assert.Equal(0, committed.Revision);
        Assert.Empty(committed.Messages);
        Assert.Equal(1, blockedStore.StoreRevision);
    }

    [Fact]
    public void EquivalentPathCannotAcquireASecondWriter()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("groups.log");
        var equivalent = System.IO.Path.Combine(
            directory.Path,
            ".",
            "groups.log");
        using (var first = new FileGroupInteractionStore(path))
        {
            _ = Assert.Throws<IOException>(
                () => new FileGroupInteractionStore(equivalent));
            Assert.Equal(
                System.IO.Path.GetFullPath(path),
                first.Path);
        }

        using var reopened = new FileGroupInteractionStore(equivalent);
        Assert.Equal(System.IO.Path.GetFullPath(path), reopened.Path);
    }

    private static GroupInteractionCreateRequest Create(
        string operationId,
        string sessionId,
        params GameEntityIdentity[] members)
    {
        return new GroupInteractionCreateRequest(
            operationId,
            sessionId,
            "group",
            Json("""{"scope":"shared"}"""),
            members.Select(item => new GroupInteractionMember(item)));
    }

    private static GroupInteractionAppendRequest Append(
        string operationId,
        string sessionId,
        long expectedRevision,
        long expectedMembershipRevision,
        GameEntityIdentity author,
        string messageId = "message")
    {
        return new GroupInteractionAppendRequest(
            operationId,
            sessionId,
            expectedRevision,
            expectedMembershipRevision,
            new[]
            {
                new GroupInteractionMessageDraft(
                    messageId,
                    "event",
                    Json("""{"value":1}"""),
                    GroupInteractionAudienceModes.AllMembers,
                    author)
            });
    }

    private static GroupInteractionAppendRequest ExplicitAppend(
        string operationId,
        string sessionId,
        long expectedRevision,
        long expectedMembershipRevision,
        GameEntityIdentity author,
        GameEntityIdentity audience)
    {
        return new GroupInteractionAppendRequest(
            operationId,
            sessionId,
            expectedRevision,
            expectedMembershipRevision,
            new[]
            {
                new GroupInteractionMessageDraft(
                    "private-message",
                    "event",
                    Json("""{"secret":true}"""),
                    GroupInteractionAudienceModes.Explicit,
                    author,
                    new[] { audience })
            });
    }

    private static GroupInteractionAppendRequest LargeAppend(
        string operationId,
        string messageId,
        long expectedRevision,
        GameEntityIdentity author,
        JsonElement payload)
    {
        return new GroupInteractionAppendRequest(
            operationId,
            "session",
            expectedRevision,
            expectedMembershipRevision: 0,
            new[]
            {
                new GroupInteractionMessageDraft(
                    messageId,
                    "event",
                    payload,
                    GroupInteractionAudienceModes.AllMembers,
                    author)
            });
    }

    private static GameEntityIdentity Identity(
        string entityId,
        long incarnation)
    {
        return new GameEntityIdentity(entityId, incarnation);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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

    private sealed class BlockingWriteFaultInjector
        : IJournalFaultInjector
    {
        private int _blocked;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int GetWriteLength(int frameLength)
        {
            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
            if (stage == JournalWriteStage.BeforeWrite
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "game-agent-group-store-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
