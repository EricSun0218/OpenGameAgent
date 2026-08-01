using System.Collections;
using System.Text.Json;
using GameAgent.Core;
using Xunit;

namespace GameAgent.Tests;

public sealed class GroupInteractionTests
{
    [Fact]
    public async Task StructuredPayloadIsSharedWithoutAssumingNaturalLanguage()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        var bob = Identity("bob", 4);
        var created = await store.CreateAsync(
            new GroupInteractionCreateRequest(
                "create-1",
                "session-1",
                "party-7",
                Json("""{"location":"courtyard","tick":"9007199254740993"}"""),
                new[]
                {
                    new GroupInteractionMember(bob, new[] { "guest" }),
                    new GroupInteractionMember(alice, new[] { "host" })
                }));

        var appended = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "append-1",
                "session-1",
                expectedRevision: 0,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "message-1",
                        "ui.selection",
                        Json(
                            """
                            {
                              "choiceId": "offer_item",
                              "arguments": {"itemId": "jade-2", "count": 1}
                            }
                            """),
                        GroupInteractionAudienceModes.AllMembers,
                        author: alice,
                        causationId: "input-3")
                }));

        var aliceView = await store.ProjectAsync("session-1", alice);
        var bobView = await store.ProjectAsync("session-1", bob);

        Assert.Equal(GroupInteractionWriteStatuses.Applied, created.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Applied, appended.Status);
        Assert.Equal(new[] { "alice", "bob" }, aliceView!.Members
            .Select(item => item.Actor.EntityId));
        Assert.Single(aliceView.Messages);
        Assert.Single(bobView!.Messages);
        Assert.Equal(
            "offer_item",
            bobView.Messages[0].Payload
                .GetProperty("choiceId")
                .GetString());
        Assert.Equal(
            "9007199254740993",
            bobView.SharedScope.GetProperty("tick").GetString());
        Assert.Equal(0, bobView.Messages[0].Sequence);
        Assert.Equal(2, bobView.Messages[0].Audience.Count);
    }

    [Fact]
    public async Task ExplicitAudienceAndIncarnationPreventPrivateLeakage()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        var oldBob = Identity("bob", 2);
        var carol = Identity("carol", 1);
        await CreateAsync(store, alice, oldBob, carol);

        var first = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "append-private",
                "session",
                0,
                0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "directed-1",
                        "directed.fact",
                        Json("""{"fact":"only bob may observe this"}"""),
                        GroupInteractionAudienceModes.Explicit,
                        author: alice,
                        audience: new[] { oldBob })
                }));

        Assert.Equal(GroupInteractionWriteStatuses.Applied, first.Status);
        Assert.Single(
            (await store.ProjectAsync("session", oldBob))!.Messages);
        Assert.Empty(
            (await store.ProjectAsync("session", carol))!.Messages);

        var replaced = await store.ReplaceMembersAsync(
            new GroupInteractionMembershipRequest(
                "replace-bob",
                "session",
                expectedRevision: 1,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMember(alice),
                    new GroupInteractionMember(Identity("bob", 3)),
                    new GroupInteractionMember(carol)
                }));

        var newBobView = await store.ProjectAsync(
            "session",
            Identity("bob", 3));
        Assert.Equal(GroupInteractionWriteStatuses.Applied, replaced.Status);
        Assert.Empty(newBobView!.Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.ProjectAsync("session", oldBob));
    }

    [Fact]
    public async Task MembershipRevisionFencesAStaleSpeakerSnapshot()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        var bob = Identity("bob", 1);
        await CreateAsync(store, alice, bob);
        var replaced = await store.ReplaceMembersAsync(
            new GroupInteractionMembershipRequest(
                "membership-1",
                "session",
                0,
                0,
                new[]
                {
                    new GroupInteractionMember(alice),
                    new GroupInteractionMember(bob),
                    new GroupInteractionMember(Identity("carol", 1))
                }));

        var stale = await store.AppendAsync(
            Append(
                "stale",
                expectedRevision: 1,
                expectedMembershipRevision: 0,
                alice));

        Assert.Equal(GroupInteractionWriteStatuses.Applied, replaced.Status);
        Assert.Equal(
            GroupInteractionWriteStatuses.MembershipRevisionConflict,
            stale.Status);
        Assert.Empty(stale.Session!.Messages);
    }

    [Fact]
    public async Task RetryAfterLaterWritesIsIdempotentAndDoesNotAppendAgain()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);
        var request = Append("append-once", 0, 0, alice);
        var first = await store.AppendAsync(request);
        var second = await store.AppendAsync(
            Append("append-two", 1, 0, alice, "message-two"));
        var replay = await store.AppendAsync(request);

        Assert.Equal(GroupInteractionWriteStatuses.Applied, first.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Applied, second.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Idempotent, replay.Status);
        Assert.Equal(1, replay.AppliedRevision);
        Assert.Equal(2, replay.Session!.Revision);
        Assert.Equal(2, replay.Session.Messages.Count);
    }

    [Fact]
    public async Task ReusingOperationIdForDifferentPayloadFailsClosed()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);
        await store.AppendAsync(
            Append("operation", 0, 0, alice, "message-a"));

        var conflict = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "operation",
                "session",
                1,
                0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "message-b",
                        "event",
                        Json("""{"value":2}"""),
                        GroupInteractionAudienceModes.AllMembers,
                        alice)
                }));

        Assert.Equal(
            GroupInteractionWriteStatuses.OperationConflict,
            conflict.Status);
        Assert.Single(conflict.Session!.Messages);
    }

    [Fact]
    public async Task ConcurrentCompareAndSwapAllowsOnlyOneAppend()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);

        var firstTask = store.AppendAsync(
                Append("first", 0, 0, alice, "message-first"))
            .AsTask();
        var secondTask = store.AppendAsync(
                Append("second", 0, 0, alice, "message-second"))
            .AsTask();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(
            results,
            item => item.Status
                    == GroupInteractionWriteStatuses.Applied);
        Assert.Single(
            results,
            item => item.Status
                    == GroupInteractionWriteStatuses.RevisionConflict);
        Assert.Single((await store.ReadAsync("session"))!.Messages);
    }

    [Fact]
    public void DefinitionOrderDoesNotChangeCreateOperationDigest()
    {
        var stateMachine = new GroupInteractionStateMachine();
        var alice = new GroupInteractionMember(
            Identity("alice", 1),
            new[] { "speaker", "merchant" });
        var bob = new GroupInteractionMember(
            Identity("bob", 2),
            new[] { "listener" });
        var first = stateMachine.Create(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("""{"b":2,"a":1}"""),
                new[] { bob, alice }));
        var second = stateMachine.Create(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("""{"a":1,"b":2}"""),
                new[]
                {
                    new GroupInteractionMember(
                        Identity("alice", 1),
                        new[] { "merchant", "speaker" }),
                    bob
                }));

        Assert.Equal(
            first.Session!.Operations[0].RequestDigest,
            second.Session!.Operations[0].RequestDigest);
        Assert.Equal(
            new[] { "alice", "bob" },
            first.Session.Members.Select(item => item.Actor.EntityId));
    }

    [Fact]
    public async Task ClosingSessionRejectsNewWritesButRetryStillResolves()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);
        var closeRequest = new GroupInteractionCloseRequest(
            "close",
            "session",
            0,
            0);
        var closed = await store.CloseAsync(closeRequest);
        var rejected = await store.AppendAsync(
            Append("late", 1, 0, alice));
        var replay = await store.CloseAsync(closeRequest);

        Assert.Equal(GroupInteractionWriteStatuses.Applied, closed.Status);
        Assert.Equal(
            GroupInteractionWriteStatuses.SessionClosed,
            rejected.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Idempotent, replay.Status);
        Assert.Equal(1, replay.AppliedRevision);
    }

    [Fact]
    public void RequestDoesNotTrustACollectionCount()
    {
        var alice = new GroupInteractionMember(Identity("alice", 1));
        var liar = new LyingReadOnlyCollection<GroupInteractionMember>(
            Enumerable.Repeat(alice, 4_097));

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("{}"),
                liar));

        Assert.Equal(
            "group_interaction_member_hard_limit_exceeded",
            error.LimitCode);
    }

    [Fact]
    public async Task AggregatePayloadCapacityRejectsWholeAtomicAppend()
    {
        var limits = new GroupInteractionLimits(
            maxMessages: 8,
            maxOperations: 8,
            maxMessagesPerAppend: 4,
            maxPayloadUtf8Bytes: 1_024,
            maxTotalPayloadUtf8Bytes: 1_024);
        var store = new InMemoryGroupInteractionStore(limits);
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);
        var payload = Json(
            "{\"value\":\"" + new string('x', 600) + "\"}");

        var rejected = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "too-large-together",
                "session",
                0,
                0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "one",
                        "event",
                        payload,
                        GroupInteractionAudienceModes.AllMembers,
                        alice),
                    new GroupInteractionMessageDraft(
                        "two",
                        "event",
                        payload,
                        GroupInteractionAudienceModes.AllMembers,
                        alice)
                }));

        Assert.Equal(
            GroupInteractionWriteStatuses.CapacityExceeded,
            rejected.Status);
        Assert.Equal(0, rejected.Session!.Revision);
        Assert.Empty(rejected.Session.Messages);
    }

    [Fact]
    public async Task AuthorAndAudienceMustMatchExactCurrentMembership()
    {
        var store = new InMemoryGroupInteractionStore();
        var alice = Identity("alice", 2);
        await CreateAsync(store, alice);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.AppendAsync(
                Append(
                    "wrong-incarnation",
                    0,
                    0,
                    Identity("alice", 1))));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.AppendAsync(
                new GroupInteractionAppendRequest(
                    "unknown-audience",
                    "session",
                    0,
                    0,
                    new[]
                    {
                        new GroupInteractionMessageDraft(
                            "message",
                            "event",
                            Json("{}"),
                            GroupInteractionAudienceModes.Explicit,
                            alice,
                            new[] { Identity("bob", 1) })
                    })));
        Assert.Equal(0, (await store.ReadAsync("session"))!.Revision);
    }

    [Fact]
    public void DurableRestoreRevalidatesPayloadEvidenceAndOperationHistory()
    {
        var machine = new GroupInteractionStateMachine();
        var alice = Identity("alice", 1);
        var created = machine.Create(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("""{"place":"hall"}"""),
                new[] { new GroupInteractionMember(alice) }));
        var appended = machine.Append(
            created.Session!,
            Append("append", 0, 0, alice));
        var source = appended.Session!;

        var restored = machine.Restore(
            source.SessionId,
            source.GroupId,
            source.SharedScope,
            source.SharedScopeDigest,
            source.Status,
            source.Revision,
            source.MembershipRevision,
            source.Members,
            source.MembershipHistory,
            source.Messages,
            source.Operations);

        Assert.Equal(source.Revision, restored.Revision);
        Assert.Equal(
            source.Messages[0].PayloadDigest,
            restored.Messages[0].PayloadDigest);

        var corrupted = new GroupInteractionMessage(
            0,
            "message",
            "event",
            Json("""{"value":999}"""),
            source.Messages[0].PayloadDigest,
            source.Messages[0].PayloadUtf8Bytes,
            source.Messages[0].AudienceMode,
            alice,
            new[] { alice },
            0,
            source.Messages[0].AppliedRevision,
            null);
        Assert.Throws<ArgumentException>(
            () => machine.Restore(
                source.SessionId,
                source.GroupId,
                source.SharedScope,
                source.SharedScopeDigest,
                source.Status,
                source.Revision,
                source.MembershipRevision,
                source.Members,
                source.MembershipHistory,
                new[] { corrupted },
                source.Operations));
    }

    [Fact]
    public async Task ConfiguredLargeStructuredValueHasNoHiddenDigestLimit()
    {
        var store = new InMemoryGroupInteractionStore(
            new GroupInteractionLimits(
                maxPayloadUtf8Bytes: 131_072,
                maxTotalPayloadUtf8Bytes: 262_144,
                maxSharedScopeUtf8Bytes: 131_072));
        var alice = Identity("alice", 1);
        var scope = Json(
            "{\"structuredContext\":\"" + new string('s', 70_000) + "\"}");
        var created = await store.CreateAsync(
            new GroupInteractionCreateRequest(
                "create-large",
                "large-session",
                "group",
                scope,
                new[] { new GroupInteractionMember(alice) }));
        var appended = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "append-large",
                "large-session",
                0,
                0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "large-message",
                        "structured.context",
                        Json(
                            "{\"data\":\""
                            + new string('p', 70_000)
                            + "\"}"),
                        GroupInteractionAudienceModes.AllMembers,
                        alice)
                }));

        Assert.Equal(GroupInteractionWriteStatuses.Applied, created.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Applied, appended.Status);
        Assert.True(
            appended.Session!.Messages[0].PayloadUtf8Bytes > 65_536);
    }

    [Fact]
    public async Task OperationCapacityAlwaysReservesAClosingTransition()
    {
        var store = new InMemoryGroupInteractionStore(
            new GroupInteractionLimits(
                maxMessages: 4,
                maxOperations: 3,
                maxMessagesPerAppend: 1));
        var alice = Identity("alice", 1);
        await CreateAsync(store, alice);
        var appended = await store.AppendAsync(
            Append("append-before-reserve", 0, 0, alice));
        var full = await store.AppendAsync(
            Append(
                "append-uses-reserve",
                1,
                0,
                alice,
                "message-two"));
        var closed = await store.CloseAsync(
            new GroupInteractionCloseRequest(
                "close-reserved",
                "session",
                expectedRevision: 1,
                expectedMembershipRevision: 0));

        Assert.Equal(GroupInteractionWriteStatuses.Applied, appended.Status);
        Assert.Equal(
            GroupInteractionWriteStatuses.CapacityExceeded,
            full.Status);
        Assert.Equal(GroupInteractionWriteStatuses.Applied, closed.Status);
        Assert.Equal(GroupInteractionStatuses.Closed, closed.Session!.Status);
        Assert.Equal(3, closed.Session.Operations.Count);
    }

    [Fact]
    public void ExternalStoreCanConstructEveryWriteOutcome()
    {
        var machine = new GroupInteractionStateMachine();
        var session = machine.Create(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("{}"),
                new[]
                {
                    new GroupInteractionMember(Identity("alice", 1))
                })).Session!;

        var applied = new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.Applied,
            session,
            appliedRevision: 0);
        var notFound = new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.NotFound,
            session: null);
        var conflict = new GroupInteractionWriteResult(
            GroupInteractionWriteStatuses.RevisionConflict,
            session);

        Assert.True(applied.Succeeded);
        Assert.Null(notFound.Session);
        Assert.False(conflict.Succeeded);
        Assert.Throws<ArgumentException>(
            () => new GroupInteractionWriteResult(
                "unknown",
                session));
    }

    [Fact]
    public void DurableRestoreRejectsForgedAudienceAndOperationEvidence()
    {
        var machine = new GroupInteractionStateMachine();
        var alice = Identity("alice", 1);
        var source = machine.Append(
            machine.Create(
                new GroupInteractionCreateRequest(
                    "create",
                    "session",
                    "group",
                    Json("{}"),
                    new[]
                    {
                        new GroupInteractionMember(alice)
                    })).Session!,
            Append("append", 0, 0, alice)).Session!;
        var message = source.Messages[0];
        var forgedAudience = new GroupInteractionMessage(
            message.Sequence,
            message.MessageId,
            message.Kind,
            message.Payload,
            message.PayloadDigest,
            message.PayloadUtf8Bytes,
            GroupInteractionAudienceModes.Explicit,
            message.Author,
            new[] { Identity("mallory", 1) },
            message.MembershipRevision,
            message.AppliedRevision,
            message.CausationId);

        Assert.Throws<ArgumentException>(
            () => machine.Restore(
                source.SessionId,
                source.GroupId,
                source.SharedScope,
                source.SharedScopeDigest,
                source.Status,
                source.Revision,
                source.MembershipRevision,
                source.Members,
                source.MembershipHistory,
                new[] { forgedAudience },
                source.Operations));

        var forgedOperations = source.Operations
            .Select(
                item => item.AppliedRevision == 1
                    ? new GroupInteractionOperationRecord(
                        item.OperationId,
                        item.Kind,
                        new string('0', 64),
                        item.AppliedRevision)
                    : item)
            .ToArray();
        Assert.Throws<ArgumentException>(
            () => machine.Restore(
                source.SessionId,
                source.GroupId,
                source.SharedScope,
                source.SharedScopeDigest,
                source.Status,
                source.Revision,
                source.MembershipRevision,
                source.Members,
                source.MembershipHistory,
                source.Messages,
                forgedOperations));
    }

    [Fact]
    public void DurableRestoreAcceptsHistoricalMembershipEvidence()
    {
        var machine = new GroupInteractionStateMachine();
        var alice = Identity("alice", 1);
        var oldBob = Identity("bob", 1);
        var created = machine.Create(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("{}"),
                new[]
                {
                    new GroupInteractionMember(alice),
                    new GroupInteractionMember(oldBob)
                }));
        var appended = machine.Append(
            created.Session!,
            new GroupInteractionAppendRequest(
                "private",
                "session",
                0,
                0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "private-message",
                        "event",
                        Json("""{"secret":true}"""),
                        GroupInteractionAudienceModes.Explicit,
                        alice,
                        new[] { oldBob })
                }));
        var replaced = machine.ReplaceMembers(
            appended.Session!,
            new GroupInteractionMembershipRequest(
                "replace",
                "session",
                1,
                0,
                new[]
                {
                    new GroupInteractionMember(alice),
                    new GroupInteractionMember(Identity("bob", 2))
                }));
        var source = replaced.Session!;

        var restored = machine.Restore(
            source.SessionId,
            source.GroupId,
            source.SharedScope,
            source.SharedScopeDigest,
            source.Status,
            source.Revision,
            source.MembershipRevision,
            source.Members,
            source.MembershipHistory,
            source.Messages,
            source.Operations);

        Assert.Equal(2, restored.MembershipHistory.Count);
        Assert.Equal(1, restored.Messages[0].Audience[0].Incarnation);
        Assert.Empty(
            machine.Project(restored, Identity("bob", 2)).Messages);
    }

    private static async ValueTask CreateAsync(
        IGroupInteractionStore store,
        params GameEntityIdentity[] members)
    {
        var result = await store.CreateAsync(
            new GroupInteractionCreateRequest(
                "create",
                "session",
                "group",
                Json("""{"scope":"shared"}"""),
                members.Select(item => new GroupInteractionMember(item))));
        Assert.Equal(GroupInteractionWriteStatuses.Applied, result.Status);
    }

    private static GroupInteractionAppendRequest Append(
        string operationId,
        long expectedRevision,
        long expectedMembershipRevision,
        GameEntityIdentity author,
        string messageId = "message")
    {
        return new GroupInteractionAppendRequest(
            operationId,
            "session",
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

    private sealed class LyingReadOnlyCollection<T> :
        IReadOnlyCollection<T>
    {
        private readonly IEnumerable<T> _source;

        public LyingReadOnlyCollection(IEnumerable<T> source)
        {
            _source = source;
        }

        public int Count => 0;

        public IEnumerator<T> GetEnumerator() => _source.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
