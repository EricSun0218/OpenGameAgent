using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using Xunit;

namespace GameAgent.Tests;

public sealed class WorldSettlementCoordinatorTests
{
    [Fact]
    public async Task TopologyIsOpaqueUniqueAndRejectsPartialOverlap()
    {
        var fixture = await Fixture.CreateAsync();
        Assert.Empty(
            typeof(WorldSettlementTopology).GetConstructors());
        var duplicate = new WorldSettlementCoordinator(
            fixture.Evidence,
            fixture.Authority,
            fixture.Outbox,
            fixture.Memory,
            fixture.Groups,
            fixture.Presentations);
        Assert.Same(fixture.Coordinator.Topology, duplicate.Topology);

        _ = Assert.Throws<InvalidOperationException>(
            () => new WorldSettlementCoordinator(
                fixture.Evidence,
                fixture.Authority,
                new InMemoryWorldSettlementStore(),
                fixture.Memory,
                fixture.Groups,
                fixture.Presentations));
    }

    [Fact]
    public async Task InMemoryQuiescenceLeaseBlocksNewSettlement()
    {
        var fixture = await Fixture.CreateAsync();
        var store = new InMemoryWorldSettlementStore();
        var source = Assert.IsAssignableFrom<
            IWorldSettlementQuiescenceSource>(store);
        var lease = Assert.IsAssignableFrom<
            IWorldSettlementQuiescenceLease>(
            await source.TryAcquireSettledQuiescenceAsync());
        Assert.Equal(0, lease.StoreRevision);
        var beginTask = store.BeginAsync(fixture.Plan).AsTask();
        await Task.Yield();
        Assert.False(beginTask.IsCompleted);

        await lease.DisposeAsync();
        var begin = await beginTask;
        Assert.Equal(WorldSettlementBeginStatus.Created, begin.Status);
        Assert.Null(
            await source.TryAcquireSettledQuiescenceAsync());
    }

    [Fact]
    public async Task SettlementRejectsCrossTimelineGroupSession()
    {
        var fixture = await Fixture.CreateAsync(
            crossTimelineGroup: true);

        var result = await fixture.Coordinator.SettleAsync(fixture.Plan);

        Assert.Equal(WorldSettlementStage.Rejected, result.Stage);
        var group = Assert.Single(
            result.DeliveryStates,
            item => item.Kind == WorldSettlementSinkKind.Group);
        Assert.Equal(WorldSettlementStage.Rejected, group.Stage);
        Assert.Equal(
            GroupInteractionWriteStatuses.WorldBindingMismatch,
            group.ReasonCode);
        Assert.Equal(0, fixture.Groups.AppendCalls);
    }

    [Fact]
    public async Task CrashAtEverySinkBoundaryResumesOnlyUnsettledWork()
    {
        var fixture = await Fixture.CreateAsync(
            throwAfterMemory: true,
            throwAfterGroup: true,
            throwAfterPresentation: true);

        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());
        var afterMemory = await fixture.Outbox.ReadAsync(
            fixture.Plan.SettlementId);
        Assert.Equal(
            new[]
            {
                WorldSettlementStage.Reconciliation,
                WorldSettlementStage.Pending,
                WorldSettlementStage.Pending
            },
            afterMemory!.DeliveryStates.Select(item => item.Stage));
        Assert.Equal(1, fixture.Memory.Calls);
        Assert.Equal(0, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);

        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.ResumeAsync(
                    fixture.Plan.SettlementId)
                .AsTask());
        var afterGroup = await fixture.Outbox.ReadAsync(
            fixture.Plan.SettlementId);
        Assert.Equal(
            new[]
            {
                WorldSettlementStage.Applied,
                WorldSettlementStage.Reconciliation,
                WorldSettlementStage.Pending
            },
            afterGroup!.DeliveryStates.Select(item => item.Stage));
        Assert.Equal(2, fixture.Memory.Calls);
        Assert.Equal(1, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);

        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.ResumeAsync(
                    fixture.Plan.SettlementId)
                .AsTask());
        var afterPresentation = await fixture.Outbox.ReadAsync(
            fixture.Plan.SettlementId);
        Assert.Equal(
            new[]
            {
                WorldSettlementStage.Applied,
                WorldSettlementStage.Applied,
                WorldSettlementStage.Reconciliation
            },
            afterPresentation!.DeliveryStates.Select(item => item.Stage));
        Assert.Equal(2, fixture.Memory.Calls);
        Assert.Equal(2, fixture.Groups.AppendCalls);
        Assert.Equal(1, fixture.Presentations.PublishCalls);

        var completed = await fixture.Coordinator.ResumeAsync(
            fixture.Plan.SettlementId);
        Assert.Equal(WorldSettlementStage.Applied, completed.Stage);
        Assert.All(
            completed.DeliveryStates,
            item => Assert.Equal(
                WorldSettlementStage.Applied,
                item.Stage));
        Assert.Equal(2, fixture.Memory.Calls);
        Assert.Equal(2, fixture.Groups.AppendCalls);
        Assert.Equal(2, fixture.Presentations.PublishCalls);

        var duplicate = await fixture.Coordinator.SettleAsync(fixture.Plan);
        Assert.Equal(WorldSettlementStage.Applied, duplicate.Stage);
        Assert.Equal(2, fixture.Memory.Calls);
        Assert.Equal(2, fixture.Groups.AppendCalls);
        Assert.Equal(2, fixture.Presentations.PublishCalls);
        Assert.Equal(7, fixture.Outbox.StoreRevisionForTests);

        var recalled = await fixture.Memory.Inner.SearchAsync(
            new MemoryQuery(
                "actor:alice",
                Json("""{"secret":"memory-only"}"""),
                worldId: fixture.Binding.WorldId,
                maximumSaveRevision: fixture.Binding.SaveRevision,
                requireCommittedProvenance: true,
                timelineId: fixture.Binding.TimelineId,
                observer: fixture.Alice,
                gameTime: fixture.Binding.GameTime),
            default);
        Assert.Single(recalled);
        var group = await fixture.Groups.Inner.ReadAsync("group-session");
        Assert.DoesNotContain(
            "memory-only",
            group!.Messages[0].Payload.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "memory-only",
            fixture.Presentations.LastPublished!.Content.Payload
                .GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrMismatchedEvidenceNeverBeginsOutbox()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Evidence.Current = null;

        var missing = await Assert.ThrowsAsync<
            WorldSettlementEvidenceException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());

        Assert.Equal(
            WorldSettlementReasonCodes.EvidenceMissing,
            missing.ReasonCode);
        Assert.Null(
            await fixture.Outbox.ReadAsync(fixture.Plan.SettlementId));

        fixture.Evidence.Current =
            new CommittedWorldPresentationEvidence(
                fixture.Source,
                new WorldPresentationBinding(
                    fixture.Binding.WorldId,
                    fixture.Binding.TimelineId,
                    fixture.Binding.TimelineEpoch,
                    fixture.Binding.SaveRevision,
                    fixture.Binding.StateVersion + 1,
                    fixture.Binding.CatalogDigest,
                    fixture.Binding.GameTime,
                    fixture.Binding.CommittedStateDigest),
                WorldPresentationCommitStatus.Applied,
                "applied");
        var mismatch = await Assert.ThrowsAsync<
            WorldSettlementEvidenceException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());

        Assert.Equal(
            WorldSettlementReasonCodes.EvidenceMismatch,
            mismatch.ReasonCode);
        Assert.Null(
            await fixture.Outbox.ReadAsync(fixture.Plan.SettlementId));
        Assert.Equal(0, fixture.Memory.Calls);
        Assert.Equal(0, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);
    }

    [Fact]
    public async Task LateMembershipAndIncarnationFailClosedBeforeAppend()
    {
        var fixture = await Fixture.CreateAsync();
        var replacement = await fixture.Groups.Inner.ReplaceMembersAsync(
            new GroupInteractionMembershipRequest(
                "replace-members",
                "group-session",
                expectedRevision: 0,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMember(fixture.Alice),
                    new GroupInteractionMember(
                        new GameEntityIdentity("bob", 2))
                }));
        Assert.True(replacement.Succeeded);

        var settled = await fixture.Coordinator.SettleAsync(fixture.Plan);

        Assert.Equal(WorldSettlementStage.Rejected, settled.Stage);
        Assert.Equal(
            WorldSettlementStage.Applied,
            settled.DeliveryStates[0].Stage);
        Assert.Equal(
            WorldSettlementStage.Rejected,
            settled.DeliveryStates[1].Stage);
        Assert.Equal(
            WorldSettlementStage.Pending,
            settled.DeliveryStates[2].Stage);
        Assert.Equal(0, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);
        var group = await fixture.Groups.Inner.ReadAsync("group-session");
        Assert.Empty(group!.Messages);
    }

    [Fact]
    public async Task AuthorityDenialRejectsPendingButNeverRetriesUncertainSink()
    {
        var fixture = await Fixture.CreateAsync(
            throwAfterMemory: true);
        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());
        fixture.Authority.AllowDeliveries = false;

        var result = await fixture.Coordinator.ResumeAsync(
            fixture.Plan.SettlementId);

        Assert.Equal(WorldSettlementStage.Reconciliation, result.Stage);
        Assert.Equal(1, fixture.Memory.Calls);
        Assert.Equal(0, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);
    }

    [Fact]
    public async Task GroupReplayCanBeConfirmedAfterLateMembershipChange()
    {
        var fixture = await Fixture.CreateAsync(
            throwAfterGroup: true);
        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());
        var changed = await fixture.Groups.Inner.ReplaceMembersAsync(
            new GroupInteractionMembershipRequest(
                "late-replace",
                "group-session",
                expectedRevision: 1,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMember(fixture.Alice),
                    new GroupInteractionMember(
                        new GameEntityIdentity("bob", 2))
                }));
        Assert.True(changed.Succeeded);
        fixture.Authority.AllowDeliveries = false;

        var resumed = await fixture.Coordinator.ResumeAsync(
            fixture.Plan.SettlementId);

        Assert.Equal(
            WorldSettlementStage.Applied,
            resumed.DeliveryStates[1].Stage);
        Assert.Equal(
            WorldSettlementStage.Rejected,
            resumed.DeliveryStates[2].Stage);
        Assert.Equal(2, fixture.Groups.AppendCalls);
        Assert.Equal(0, fixture.Presentations.PublishCalls);
    }

    [Fact]
    public async Task StoreUsesExactPlanDigestAndCompareAndSwap()
    {
        var fixture = await Fixture.CreateAsync();
        var first = await fixture.Outbox.BeginAsync(fixture.Plan);
        var duplicate = await fixture.Outbox.BeginAsync(fixture.Plan);
        var conflictingPlan = new WorldSettlementPlan(
            fixture.Plan.SettlementId,
            fixture.Plan.Evidence,
            new[] { fixture.Plan.Deliveries[0] });
        var conflict = await fixture.Outbox.BeginAsync(conflictingPlan);

        Assert.Equal(WorldSettlementBeginStatus.Created, first.Status);
        Assert.Equal(WorldSettlementBeginStatus.Existing, duplicate.Status);
        Assert.Equal(WorldSettlementBeginStatus.Conflict, conflict.Status);

        var transition = new WorldSettlementTransition(
            fixture.Plan.SettlementId,
            fixture.Plan.SemanticDigest,
            expectedRecordRevision: 0,
            fixture.Plan.Deliveries[0].OperationId,
            WorldSettlementStage.Pending,
            WorldSettlementStage.Reconciliation,
            WorldSettlementReasonCodes.DispatchIntentCommitted);
        var applied = await fixture.Outbox.TryTransitionAsync(transition);
        var stale = await fixture.Outbox.TryTransitionAsync(transition);

        Assert.Equal(
            WorldSettlementTransitionStatus.Applied,
            applied.Status);
        Assert.Equal(
            WorldSettlementTransitionStatus.Conflict,
            stale.Status);
        Assert.Equal(1, stale.Record!.Revision);
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldSettlementListRequest(4_097));
    }

    [Fact]
    public async Task ReconciliationDominatesRejectionAndStillResumes()
    {
        var fixture = await Fixture.CreateAsync();
        _ = await fixture.Outbox.BeginAsync(fixture.Plan);
        var group = fixture.Plan.Deliveries[1];
        var memory = fixture.Plan.Deliveries[0];
        _ = await fixture.Outbox.TryTransitionAsync(
            new WorldSettlementTransition(
                fixture.Plan.SettlementId,
                fixture.Plan.SemanticDigest,
                expectedRecordRevision: 0,
                group.OperationId,
                WorldSettlementStage.Pending,
                WorldSettlementStage.Reconciliation,
                WorldSettlementReasonCodes.DispatchIntentCommitted));
        var mixed = await fixture.Outbox.TryTransitionAsync(
            new WorldSettlementTransition(
                fixture.Plan.SettlementId,
                fixture.Plan.SemanticDigest,
                expectedRecordRevision: 1,
                memory.OperationId,
                WorldSettlementStage.Pending,
                WorldSettlementStage.Rejected,
                "test_rejected"));

        Assert.Equal(
            WorldSettlementStage.Reconciliation,
            mixed.Record!.Stage);
        var page = await fixture.Outbox.ListUnsettledAsync(
            new WorldSettlementListRequest(1));
        Assert.Single(page.Items);

        var resumed = await fixture.Coordinator.ResumeAsync(
            fixture.Plan.SettlementId);

        Assert.Equal(WorldSettlementStage.Rejected, resumed.Stage);
        Assert.Equal(
            WorldSettlementStage.Applied,
            resumed.DeliveryStates[1].Stage);
        Assert.Equal(1, fixture.Groups.AppendCalls);
        Assert.Single(
            (await fixture.Groups.Inner.ReadAsync("group-session"))!
            .Messages);
    }

    [Fact]
    public async Task UnsettledEnumerationUsesBoundedKeysetPagination()
    {
        var fixture = await Fixture.CreateAsync();
        var store = new InMemoryWorldSettlementStore();
        var firstPlan = new WorldSettlementPlan(
            "a-settlement",
            fixture.Plan.Evidence,
            fixture.Plan.Deliveries);
        var secondPlan = new WorldSettlementPlan(
            "z-settlement",
            fixture.Plan.Evidence,
            fixture.Plan.Deliveries);
        _ = await store.BeginAsync(firstPlan);
        _ = await store.BeginAsync(secondPlan);

        var first = await store.ListUnsettledAsync(
            new WorldSettlementListRequest(maxResults: 1));
        var second = await store.ListUnsettledAsync(
            new WorldSettlementListRequest(
                maxResults: 1,
                first.ContinuationCursor));

        Assert.Equal("a-settlement", Assert.Single(first.Items)
            .SettlementId);
        Assert.True(first.HasMore);
        Assert.NotNull(first.ContinuationCursor);
        Assert.Equal("z-settlement", Assert.Single(second.Items)
            .SettlementId);
        Assert.False(second.HasMore);
        _ = Assert.Throws<ArgumentException>(
            () => new WorldSettlementListRequest(
                continuationCursor: first.ContinuationCursor + "x"));
    }

    [Fact]
    public async Task ExactAuthorityClaimsIncludeBindingEvidenceAndAudience()
    {
        var fixture = await Fixture.CreateAsync();

        var result = await fixture.Coordinator.SettleAsync(fixture.Plan);

        Assert.Equal(WorldSettlementStage.Applied, result.Stage);
        var request = Assert.Single(fixture.Authority.Requests);
        Assert.True(request.Source.IsSameAs(fixture.Source));
        Assert.True(request.Binding.IsSameAs(fixture.Binding));
        Assert.Equal(fixture.Plan.EvidenceDigest, request.EvidenceDigest);
        Assert.Equal(fixture.Plan.SemanticDigest, request.PlanDigest);
        Assert.Equal(
            fixture.Plan.SemanticDigest,
            request.Plan.SemanticDigest);
        Assert.Equal(
            fixture.Plan.Deliveries.Select(item => item.OperationId),
            fixture.Authority.Claims.Select(item => item.OperationId));
        var claimedMemory =
            Assert.IsType<WorldSettlementMemoryDelivery>(
                fixture.Authority.Claims[0].Delivery);
        Assert.Equal("memory-1", claimedMemory.Mutations[0].MemoryId);
        Assert.True(
            CanonicalJsonDigest.IsSha256(
                fixture.Authority.Claims[0].DeliveryDigest));
        Assert.True(
            fixture.Authority.Claims[0].Audience.Members[0]
                .IsSameIncarnation(fixture.Alice));
        Assert.Equal(
            WorldSettlementPrivacyClasses.Private,
            fixture.Authority.Claims[0].Audience.PrivacyClass);
    }

    [Fact]
    public async Task PlanBoundsAndPrivateMemoryProvenanceFailClosed()
    {
        var fixture = await Fixture.CreateAsync();
        var badRecord = new MemoryRecord(
            "bad-memory",
            "actor:alice",
            Json("""{"secret":"bad"}"""),
            tags: null,
            importance: 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                fixture.Binding.WorldId,
                sessionId: null,
                fixture.Binding.SaveRevision,
                "run",
                fixture.Source.WorldReceiptId,
                committed: true,
                fixture.Binding.TimelineId,
                new GameKnowledgePerspective(
                    new GameEntityIdentity("alice", 99),
                    "observed")));
        var badMemory = new WorldSettlementMemoryDelivery(
            "bad-memory-op",
            fixture.Plan.Deliveries[0].Audience,
            new[] { MemoryMutation.Upsert(badRecord) });

        _ = Assert.Throws<ArgumentException>(
            () => new WorldSettlementPlan(
                "bad-settlement",
                fixture.Plan.Evidence,
                new[] { badMemory }));

        var epochlessRecord = new MemoryRecord(
            "epochless-memory",
            "actor:alice",
            Json("""{"secret":"wrong-epoch-risk"}"""),
            tags: null,
            importance: 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                fixture.Binding.WorldId,
                sessionId: null,
                fixture.Binding.SaveRevision,
                "run",
                fixture.Source.WorldReceiptId,
                committed: true,
                fixture.Binding.TimelineId,
                new GameKnowledgePerspective(
                    fixture.Alice,
                    "observed")));
        _ = Assert.Throws<ArgumentException>(
            () => new WorldSettlementPlan(
                "epochless-settlement",
                fixture.Plan.Evidence,
                new[]
                {
                    new WorldSettlementMemoryDelivery(
                        "epochless-memory-op",
                        fixture.Plan.Deliveries[0].Audience,
                        new[]
                        {
                            MemoryMutation.Upsert(epochlessRecord)
                        })
                }));

        _ = Assert.Throws<ArgumentException>(
            () => new WorldSettlementMemoryDelivery(
                "unsafe-delete",
                fixture.Plan.Deliveries[0].Audience,
                new[] { MemoryMutation.Delete("another-owner-memory") }));

        var tooMany = fixture.Plan.Deliveries
            .Take(1)
            .Concat(
                Enumerable.Range(0, 2)
                    .Select(
                        index => (WorldSettlementDelivery)
                            new WorldSettlementPresentationDelivery(
                                $"bounded-{index}",
                                NewPresentation(
                                    fixture.Source,
                                    fixture.Binding,
                                    fixture.Alice,
                                    fixture.Bob,
                                    $"bounded-presentation-{index}"),
                                -1)));
        _ = Assert.Throws<RuntimeContentLimitException>(
            () => new WorldSettlementPlan(
                "bounded",
                fixture.Plan.Evidence,
                tooMany,
                new WorldSettlementLimits(maxDeliveries: 2)));
    }

    [Fact]
    public async Task PresentationDispatchDoesNotReenterAuthorityEvidenceLock()
    {
        var fixture = await Fixture.CreateAsync();
        var evidence = fixture.Evidence.Current!;
        var authority = new SharedEvidenceAuthority(evidence);
        var outbox = new InMemoryWorldSettlementStore();
        var presentations = new TrackingPresentationStore(
            throwAfterNextPublish: false);
        var plan = new WorldSettlementPlan(
            "locked-presentation-settlement",
            evidence,
            new[] { fixture.Plan.Deliveries[2] });
        var coordinator = new WorldSettlementCoordinator(
            authority,
            authority,
            outbox,
            presentations: presentations);

        var result = await coordinator.SettleAsync(plan)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WorldSettlementStage.Applied, result.Stage);
        Assert.Equal(1, authority.EvidenceReads);
        Assert.Equal(1, presentations.PublishCalls);
    }

    [Fact]
    public async Task MemoryCommitIdentityIsNamespacedBySettlement()
    {
        var fixture = await Fixture.CreateAsync();
        var first = new WorldSettlementPlan(
            "memory-settlement-a",
            fixture.Plan.Evidence,
            new[] { fixture.Plan.Deliveries[0] });
        var original =
            Assert.IsType<WorldSettlementMemoryDelivery>(
                fixture.Plan.Deliveries[0]);
        var sourceRecord = original.Mutations[0].Record!;
        var secondRecord = new MemoryRecord(
            "memory-2",
            sourceRecord.Scope,
            Json("""{"secret":"second"}"""),
            sourceRecord.Tags,
            sourceRecord.Importance,
            sourceRecord.CreatedAt,
            sourceRecord.UpdatedAt,
            sourceRecord.ExpiresAt,
            sourceRecord.Provenance,
            sourceRecord.GameTimeWindow);
        var second = new WorldSettlementPlan(
            "memory-settlement-b",
            fixture.Plan.Evidence,
            new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    original.OperationId,
                    original.Audience,
                    new[] { MemoryMutation.Upsert(secondRecord) })
            });

        var settledFirst = await fixture.Coordinator.SettleAsync(first);
        var settledSecond = await fixture.Coordinator.SettleAsync(second);

        Assert.Equal(WorldSettlementStage.Applied, settledFirst.Stage);
        Assert.Equal(WorldSettlementStage.Applied, settledSecond.Stage);
        Assert.Equal(2, fixture.Memory.Calls);
    }

    [Fact]
    public async Task TimelessSettlementMemoryIsRetrievableWithoutGameClock()
    {
        var fixture = await Fixture.CreateAsync();
        var binding = new WorldPresentationBinding(
            fixture.Binding.WorldId,
            fixture.Binding.TimelineId,
            fixture.Binding.TimelineEpoch,
            fixture.Binding.SaveRevision,
            fixture.Binding.StateVersion,
            fixture.Binding.CatalogDigest,
            gameTime: null,
            fixture.Binding.CommittedStateDigest);
        var evidence = new CommittedWorldPresentationEvidence(
            fixture.Source,
            binding,
            WorldPresentationCommitStatus.Applied,
            "applied");
        var memory = new TrackingMemoryStore(throwAfterNextApply: false);
        var record = new MemoryRecord(
            "timeless-memory",
            "actor:alice",
            Json("""{"fact":"timeless"}"""),
            tags: null,
            importance: 50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                binding.WorldId,
                sessionId: null,
                binding.SaveRevision,
                "timeless-run",
                fixture.Source.WorldReceiptId,
                committed: true,
                binding.TimelineId,
                new GameKnowledgePerspective(
                    fixture.Alice,
                    "observed"),
                binding.TimelineEpoch),
            gameTimeWindow: null);
        var plan = new WorldSettlementPlan(
            "timeless-settlement",
            evidence,
            new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    "memory-op",
                    new WorldSettlementAudienceClaim(
                        "actor:alice",
                        membershipRevision: 1,
                        new[] { fixture.Alice },
                        WorldSettlementPrivacyClasses.Private,
                        "none"),
                    new[] { MemoryMutation.Upsert(record) })
            });
        var coordinator = new WorldSettlementCoordinator(
            new MutableEvidenceSource(evidence),
            new TrackingAuthorityGuard(),
            new InMemoryWorldSettlementStore(),
            memory);

        var settled = await coordinator.SettleAsync(plan);
        var recalled = await memory.SearchAsync(
            new MemoryQuery(
                "actor:alice",
                Json("""{"fact":"timeless"}"""),
                worldId: binding.WorldId,
                maximumSaveRevision: binding.SaveRevision,
                requireCommittedProvenance: true,
                timelineId: binding.TimelineId,
                observer: fixture.Alice,
                timelineEpoch: binding.TimelineEpoch),
            default);
        var wrongEpoch = await memory.SearchAsync(
            new MemoryQuery(
                "actor:alice",
                Json("""{"fact":"timeless"}"""),
                worldId: binding.WorldId,
                maximumSaveRevision: binding.SaveRevision,
                requireCommittedProvenance: true,
                timelineId: binding.TimelineId,
                observer: fixture.Alice,
                timelineEpoch: binding.TimelineEpoch + 1),
            default);

        Assert.Equal(WorldSettlementStage.Applied, settled.Stage);
        Assert.Single(recalled);
        Assert.Empty(wrongEpoch);
    }

    [Fact]
    public async Task ReconciliationUsesPersistedEvidenceAfterLedgerPruning()
    {
        var fixture = await Fixture.CreateAsync(
            throwAfterMemory: true);
        _ = await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.Coordinator.SettleAsync(fixture.Plan).AsTask());
        fixture.Evidence.Current = null;

        var resumed = await fixture.Coordinator.ResumeAsync(
            fixture.Plan.SettlementId);

        Assert.Equal(WorldSettlementStage.Applied, resumed.Stage);
        Assert.Equal(2, fixture.Memory.Calls);
        Assert.Equal(1, fixture.Groups.AppendCalls);
        Assert.Equal(1, fixture.Presentations.PublishCalls);
    }

    [Fact]
    public async Task PrivateMemoryIdsCannotOverwriteAnotherIncarnation()
    {
        var fixture = await Fixture.CreateAsync();
        var bobPlan = PrivateMemoryPlan(
            "bob-private-settlement",
            "shared-private-id",
            fixture.Bob,
            """{"owner":"bob"}""",
            fixture);
        var alicePlan = PrivateMemoryPlan(
            "alice-private-settlement",
            "shared-private-id",
            fixture.Alice,
            """{"owner":"alice"}""",
            fixture);
        var otherSource = new WorldPresentationSource(
            "receipt-other-world",
            Digest("receipt-other-world"));
        var otherBinding = new WorldPresentationBinding(
            "other-world",
            fixture.Binding.TimelineId,
            timelineEpoch: fixture.Binding.TimelineEpoch + 1,
            saveRevision: fixture.Binding.SaveRevision,
            stateVersion: fixture.Binding.StateVersion,
            fixture.Binding.CatalogDigest,
            new GameTimePoint(
                "month",
                fixture.Binding.TimelineId,
                fixture.Binding.TimelineEpoch + 1,
                tick: 80),
            fixture.Binding.CommittedStateDigest);
        var otherEvidence = new CommittedWorldPresentationEvidence(
            otherSource,
            otherBinding,
            WorldPresentationCommitStatus.Applied,
            "applied");
        var otherWorldPlan = PrivateMemoryPlan(
            "alice-other-world-settlement",
            "shared-private-id",
            fixture.Alice,
            """{"owner":"alice-other-world"}""",
            fixture,
            otherEvidence);

        Assert.Equal(
            WorldSettlementStage.Applied,
            (await fixture.Coordinator.SettleAsync(bobPlan)).Stage);
        Assert.Equal(
            WorldSettlementStage.Applied,
            (await fixture.Coordinator.SettleAsync(alicePlan)).Stage);
        var otherWorldCoordinator = new WorldSettlementCoordinator(
            new MutableEvidenceSource(otherEvidence),
            new TrackingAuthorityGuard(),
            fixture.Outbox,
            fixture.Memory,
            fixture.Groups,
            fixture.Presentations);
        Assert.Equal(
            WorldSettlementStage.Applied,
            (await otherWorldCoordinator.SettleAsync(otherWorldPlan)).Stage);

        var bob = await SearchPrivateMemoryAsync(fixture, fixture.Bob);
        var alice = await SearchPrivateMemoryAsync(
            fixture,
            fixture.Alice);
        var otherWorld = await SearchPrivateMemoryAsync(
            fixture,
            fixture.Alice,
            otherBinding);
        Assert.Single(bob);
        Assert.Single(alice);
        Assert.Single(otherWorld);
        Assert.Equal(
            "bob",
            bob[0].Record.Content.GetProperty("owner").GetString());
        Assert.Equal(
            "alice",
            alice[0].Record.Content.GetProperty("owner").GetString());
        Assert.Equal(
            "alice-other-world",
            otherWorld[0].Record.Content.GetProperty("owner").GetString());
        Assert.NotEqual(
            bob[0].Record.MemoryId,
            alice[0].Record.MemoryId);
        Assert.NotEqual(
            alice[0].Record.MemoryId,
            otherWorld[0].Record.MemoryId);
    }

    [Fact]
    public async Task GroupOperationIdsAreNamespacedBySettlement()
    {
        var fixture = await Fixture.CreateAsync();
        var template =
            Assert.IsType<WorldSettlementGroupDelivery>(
                fixture.Plan.Deliveries[1]);
        WorldSettlementPlan Plan(
            string settlementId,
            long revision,
            string messageId)
        {
            var request = new GroupInteractionAppendRequest(
                template.OperationId,
                template.Request.SessionId,
                revision,
                template.Request.ExpectedMembershipRevision,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        messageId,
                        "world.notice",
                        Json($$"""{"message":"{{messageId}}"}"""),
                        GroupInteractionAudienceModes.AllMembers,
                        author: fixture.Alice,
                        causationId: fixture.Source.WorldReceiptId)
                });
            return new WorldSettlementPlan(
                settlementId,
                fixture.Plan.Evidence,
                new WorldSettlementDelivery[]
                {
                    new WorldSettlementGroupDelivery(
                        template.OperationId,
                        template.ExpectedGroupId,
                        template.ExpectedMembers,
                        request)
                });
        }

        var first = await fixture.Coordinator.SettleAsync(
            Plan(
                "group-settlement-a",
                revision: 0,
                messageId: "message-a"));
        var second = await fixture.Coordinator.SettleAsync(
            Plan(
                "group-settlement-b",
                revision: 1,
                messageId: "message-b"));
        var session = await fixture.Groups.Inner.ReadAsync(
            template.Request.SessionId);

        Assert.Equal(WorldSettlementStage.Applied, first.Stage);
        Assert.Equal(WorldSettlementStage.Applied, second.Stage);
        Assert.Equal(2, session!.Messages.Count);
        Assert.Equal(3, session.Operations.Count);
        Assert.NotEqual(
            session.Operations[1].OperationId,
            session.Operations[2].OperationId);
    }

    [Fact]
    public async Task ConcurrentGroupReplayCannotRejectCommittedAppend()
    {
        var fixture = await Fixture.CreateAsync();
        var plan = new WorldSettlementPlan(
            "concurrent-group-settlement",
            fixture.Plan.Evidence,
            new[] { fixture.Plan.Deliveries[1] });
        var outbox = new SynchronizedIntentStore();
        var groups = new OrderedConcurrentGroupStore(
            fixture.Groups.Inner);
        var first = new WorldSettlementCoordinator(
            fixture.Evidence,
            new TrackingAuthorityGuard(),
            outbox,
            groups: groups);
        var second = new WorldSettlementCoordinator(
            fixture.Evidence,
            new TrackingAuthorityGuard(),
            outbox,
            groups: groups);

        var results = await Task.WhenAll(
            first.SettleAsync(plan).AsTask(),
            second.SettleAsync(plan).AsTask());
        var persisted = await outbox.ReadAsync(plan.SettlementId);
        var session = await fixture.Groups.Inner.ReadAsync("group-session");

        Assert.All(
            results,
            item => Assert.Equal(
                WorldSettlementStage.Applied,
                item.Stage));
        Assert.Equal(WorldSettlementStage.Applied, persisted!.Stage);
        Assert.Single(session!.Messages);
        Assert.Equal(2, session.Operations.Count);
    }

    private static WorldSettlementPlan PrivateMemoryPlan(
        string settlementId,
        string memoryId,
        GameEntityIdentity owner,
        string content,
        Fixture fixture,
        CommittedWorldPresentationEvidence? evidence = null)
    {
        var admittedEvidence = evidence ?? fixture.Plan.Evidence;
        var binding = admittedEvidence.Binding;
        var source = admittedEvidence.Source;
        var record = new MemoryRecord(
            memoryId,
            "private-shared-scope",
            Json(content),
            tags: null,
            importance: 50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                binding.WorldId,
                sessionId: null,
                binding.SaveRevision,
                "private-test-run",
                source.WorldReceiptId,
                committed: true,
                binding.TimelineId,
                new GameKnowledgePerspective(owner, "observed"),
                binding.TimelineEpoch),
            gameTimeWindow: new GameTimeWindow(
                validFrom: binding.GameTime));
        return new WorldSettlementPlan(
            settlementId,
            admittedEvidence,
            new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    "memory-op",
                    new WorldSettlementAudienceClaim(
                        $"actor:{owner.EntityId}",
                        membershipRevision: 1,
                        new[] { owner },
                        WorldSettlementPrivacyClasses.Private,
                        "none"),
                    new[] { MemoryMutation.Upsert(record) })
            });
    }

    private static ValueTask<IReadOnlyList<MemorySearchResult>>
        SearchPrivateMemoryAsync(
            Fixture fixture,
            GameEntityIdentity owner,
            WorldPresentationBinding? binding = null)
    {
        var admittedBinding = binding ?? fixture.Binding;
        return fixture.Memory.SearchAsync(
            new MemoryQuery(
                "private-shared-scope",
                Json("""{"owner":true}"""),
                worldId: admittedBinding.WorldId,
                maximumSaveRevision: admittedBinding.SaveRevision,
                requireCommittedProvenance: true,
                timelineId: admittedBinding.TimelineId,
                observer: owner,
                gameTime: admittedBinding.GameTime,
                timelineEpoch: admittedBinding.TimelineEpoch),
            default);
    }

    private static WorldPresentationDraft NewPresentation(
        WorldPresentationSource source,
        WorldPresentationBinding binding,
        GameEntityIdentity alice,
        GameEntityIdentity bob,
        string presentationId = "presentation-op")
    {
        return new WorldPresentationDraft(
            presentationId,
            contentRevision: 0,
            source,
            binding,
            new WorldPresentationAudience(
                "group-session",
                membershipRevision: 0,
                new[] { alice, bob },
                privacyClass: "group",
                redactionClass: "none"),
            new WorldPresentationContent(
                "world.notice",
                "application/json",
                Json("""{"message":"group-only"}""")),
            new WorldPresentationProvenance(
                "test-producer",
                "1",
                "receipt_projection"));
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

    private sealed class Fixture
    {
        private Fixture(
            GameEntityIdentity alice,
            GameEntityIdentity bob,
            WorldPresentationSource source,
            WorldPresentationBinding binding,
            MutableEvidenceSource evidence,
            TrackingAuthorityGuard authority,
            TrackingMemoryStore memory,
            TrackingGroupStore groups,
            TrackingPresentationStore presentations,
            CountingSettlementStore outbox,
            WorldSettlementPlan plan,
            WorldSettlementCoordinator coordinator)
        {
            Alice = alice;
            Bob = bob;
            Source = source;
            Binding = binding;
            Evidence = evidence;
            Authority = authority;
            Memory = memory;
            Groups = groups;
            Presentations = presentations;
            Outbox = outbox;
            Plan = plan;
            Coordinator = coordinator;
        }

        public GameEntityIdentity Alice { get; }

        public GameEntityIdentity Bob { get; }

        public WorldPresentationSource Source { get; }

        public WorldPresentationBinding Binding { get; }

        public MutableEvidenceSource Evidence { get; }

        public TrackingAuthorityGuard Authority { get; }

        public TrackingMemoryStore Memory { get; }

        public TrackingGroupStore Groups { get; }

        public TrackingPresentationStore Presentations { get; }

        public CountingSettlementStore Outbox { get; }

        public WorldSettlementPlan Plan { get; }

        public WorldSettlementCoordinator Coordinator { get; }

        public static async ValueTask<Fixture> CreateAsync(
            bool throwAfterMemory = false,
            bool throwAfterGroup = false,
            bool throwAfterPresentation = false,
            bool crossTimelineGroup = false)
        {
            var alice = new GameEntityIdentity("alice", 1);
            var bob = new GameEntityIdentity("bob", 1);
            var source = new WorldPresentationSource(
                "receipt-1",
                Digest("receipt-1"),
                occurrenceId: "occurrence-1",
                actionId: "action-1",
                operationId: "world-operation-1");
            var binding = new WorldPresentationBinding(
                "world",
                "timeline",
                timelineEpoch: 4,
                saveRevision: 9,
                stateVersion: 12,
                catalogDigest: Digest("catalog"),
                gameTime: new GameTimePoint(
                    "month",
                    "timeline",
                    epoch: 4,
                    tick: 80),
                committedStateDigest: Digest("state"));
            var committed = new CommittedWorldPresentationEvidence(
                source,
                binding,
                WorldPresentationCommitStatus.Applied,
                "world_action_applied",
                Json("""{"receiptVersion":1}"""));
            var evidence = new MutableEvidenceSource(committed);
            var authority = new TrackingAuthorityGuard();
            var memory = new TrackingMemoryStore(throwAfterMemory);
            var groupInner = new InMemoryGroupInteractionStore();
            _ = await groupInner.CreateAsync(
                new GroupInteractionCreateRequest(
                    "create-group",
                    "group-session",
                    "group",
                    Json("""{"location":"square"}"""),
                    new[]
                    {
                        new GroupInteractionMember(alice),
                        new GroupInteractionMember(bob)
                    },
                    new GroupInteractionWorldBinding(
                        binding.WorldId,
                        crossTimelineGroup
                            ? "another-timeline"
                            : binding.TimelineId,
                        binding.TimelineEpoch,
                        binding.SaveRevision)));
            var groups = new TrackingGroupStore(
                groupInner,
                throwAfterGroup);
            var presentations = new TrackingPresentationStore(
                throwAfterPresentation);
            var privateAudience = new WorldSettlementAudienceClaim(
                "actor:alice",
                membershipRevision: 3,
                new[] { alice },
                WorldSettlementPrivacyClasses.Private,
                redactionClass: "none");
            var memoryRecord = new MemoryRecord(
                "memory-1",
                "actor:alice",
                Json("""{"secret":"memory-only"}"""),
                new[] { "receipt" },
                importance: 80,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                provenance: new MemoryProvenance(
                    binding.WorldId,
                    sessionId: null,
                    binding.SaveRevision,
                    sourceRunId: "agent-run-1",
                    sourceEventId: source.WorldReceiptId,
                    committed: true,
                    binding.TimelineId,
                    new GameKnowledgePerspective(
                        alice,
                        "observed"),
                    binding.TimelineEpoch),
                gameTimeWindow: new GameTimeWindow(
                    validFrom: binding.GameTime));
            var groupRequest = new GroupInteractionAppendRequest(
                "group-op",
                "group-session",
                expectedRevision: 0,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "message-1",
                        "world.notice",
                        Json("""{"message":"group-only"}"""),
                        GroupInteractionAudienceModes.AllMembers,
                        author: alice,
                        causationId: source.WorldReceiptId)
                });
            var deliveries = new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    "memory-op",
                    privateAudience,
                    new[] { MemoryMutation.Upsert(memoryRecord) }),
                new WorldSettlementGroupDelivery(
                    "group-op",
                    "group",
                    new[]
                    {
                        new GroupInteractionMember(alice),
                        new GroupInteractionMember(bob)
                    },
                    groupRequest),
                new WorldSettlementPresentationDelivery(
                    "presentation-op",
                    NewPresentation(source, binding, alice, bob),
                    expectedPreviousContentRevision: -1)
            };
            var plan = new WorldSettlementPlan(
                "settlement-1",
                committed,
                deliveries);
            var outbox = new CountingSettlementStore();
            var coordinator = new WorldSettlementCoordinator(
                evidence,
                authority,
                outbox,
                memory,
                groups,
                presentations);
            return new Fixture(
                alice,
                bob,
                source,
                binding,
                evidence,
                authority,
                memory,
                groups,
                presentations,
                outbox,
                plan,
                coordinator);
        }
    }

    private sealed class MutableEvidenceSource
        : ICommittedWorldPresentationEvidenceSource
    {
        public MutableEvidenceSource(
            CommittedWorldPresentationEvidence? current)
        {
            Current = current;
        }

        public CommittedWorldPresentationEvidence? Current { get; set; }

        public ValueTask<CommittedWorldPresentationEvidence?>
            ReadCommittedAsync(
                string worldReceiptId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<CommittedWorldPresentationEvidence?>(
                Current);
        }
    }

    private sealed class TrackingAuthorityGuard
        : IWorldSettlementAuthorityGuard
    {
        public bool AllowAcquire { get; set; } = true;

        public bool AllowDeliveries { get; set; } = true;

        public List<WorldSettlementAuthorityRequest> Requests { get; } =
            new();

        public List<WorldSettlementDeliveryClaim> Claims { get; } = new();

        public ValueTask<IWorldSettlementAuthorityLease?> AcquireAsync(
            WorldSettlementAuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return new ValueTask<IWorldSettlementAuthorityLease?>(
                AllowAcquire ? new Lease(this) : null);
        }

        private sealed class Lease : IWorldSettlementAuthorityLease
        {
            private readonly TrackingAuthorityGuard _owner;

            public Lease(TrackingAuthorityGuard owner)
            {
                _owner = owner;
            }

            public ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _owner.Claims.Add(claim);
                return new ValueTask<WorldSettlementAuthorityDecision>(
                    _owner.AllowDeliveries
                        ? WorldSettlementAuthorityDecision.Allow()
                        : WorldSettlementAuthorityDecision.Deny(
                            "test_authority_denied"));
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }
    }

    private sealed class SharedEvidenceAuthority :
        ICommittedWorldPresentationEvidenceSource,
        IWorldSettlementAuthorityGuard
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CommittedWorldPresentationEvidence _evidence;

        public SharedEvidenceAuthority(
            CommittedWorldPresentationEvidence evidence)
        {
            _evidence = evidence;
        }

        public int EvidenceReads { get; private set; }

        public async ValueTask<CommittedWorldPresentationEvidence?>
            ReadCommittedAsync(
                string worldReceiptId,
                CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                EvidenceReads++;
                return string.Equals(
                    worldReceiptId,
                    _evidence.Source.WorldReceiptId,
                    StringComparison.Ordinal)
                    ? _evidence
                    : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask<IWorldSettlementAuthorityLease?> AcquireAsync(
            WorldSettlementAuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new SharedLease(_gate);
        }

        private sealed class SharedLease : IWorldSettlementAuthorityLease
        {
            private readonly SemaphoreSlim _gate;
            private bool _disposed;

            public SharedLease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public ValueTask<WorldSettlementAuthorityDecision> ValidateAsync(
                WorldSettlementDeliveryClaim claim,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<WorldSettlementAuthorityDecision>(
                    WorldSettlementAuthorityDecision.Allow());
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _gate.Release();
                }

                return default;
            }
        }
    }

    private sealed class TrackingMemoryStore
        : IIdempotentAtomicMemoryBatchStore
    {
        private bool _throwAfterNextApply;

        public TrackingMemoryStore(bool throwAfterNextApply)
        {
            _throwAfterNextApply = throwAfterNextApply;
        }

        public Bm25MemoryStore Inner { get; } = new();

        public int Calls { get; private set; }

        public string ProviderId => Inner.ProviderId;

        public ValueTask UpsertAsync(
            MemoryRecord record,
            CancellationToken cancellationToken)
        {
            return Inner.UpsertAsync(record, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(
            string memoryId,
            CancellationToken cancellationToken)
        {
            return Inner.DeleteAsync(memoryId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            return Inner.SearchAsync(query, cancellationToken);
        }

        public ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyAtomicBatchAsync(
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            return Inner.ApplyAtomicBatchAsync(mutations, cancellationToken);
        }

        public async ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyIdempotentAtomicBatchAsync(
                string commitId,
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = await Inner.ApplyIdempotentAtomicBatchAsync(
                commitId,
                mutations,
                cancellationToken);
            if (_throwAfterNextApply)
            {
                _throwAfterNextApply = false;
                throw new InjectedCrashException();
            }

            return result;
        }
    }

    private sealed class TrackingGroupStore : IGroupInteractionStore
    {
        private bool _throwAfterNextAppend;

        public TrackingGroupStore(
            InMemoryGroupInteractionStore inner,
            bool throwAfterNextAppend)
        {
            Inner = inner;
            _throwAfterNextAppend = throwAfterNextAppend;
        }

        public InMemoryGroupInteractionStore Inner { get; }

        public int AppendCalls { get; private set; }

        public ValueTask<GroupInteractionSession?> ReadAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Inner.ReadAsync(sessionId, cancellationToken);
        }

        public ValueTask<GroupInteractionWriteResult> CreateAsync(
            GroupInteractionCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Inner.CreateAsync(request, cancellationToken);
        }

        public ValueTask<GroupInteractionWriteResult> ReplaceMembersAsync(
            GroupInteractionMembershipRequest request,
            CancellationToken cancellationToken = default)
        {
            return Inner.ReplaceMembersAsync(request, cancellationToken);
        }

        public async ValueTask<GroupInteractionWriteResult> AppendAsync(
            GroupInteractionAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            var result = await Inner.AppendAsync(
                request,
                cancellationToken);
            if (_throwAfterNextAppend)
            {
                _throwAfterNextAppend = false;
                throw new InjectedCrashException();
            }

            return result;
        }

        public ValueTask<GroupInteractionWriteResult> CloseAsync(
            GroupInteractionCloseRequest request,
            CancellationToken cancellationToken = default)
        {
            return Inner.CloseAsync(request, cancellationToken);
        }

        public ValueTask<GroupInteractionProjection?> ProjectAsync(
            string sessionId,
            GameEntityIdentity viewer,
            CancellationToken cancellationToken = default)
        {
            return Inner.ProjectAsync(
                sessionId,
                viewer,
                cancellationToken);
        }
    }

    private sealed class TrackingPresentationStore
        : IWorldPresentationStore
    {
        private readonly Dictionary<string, VerifiedWorldPresentation>
            _latest = new(StringComparer.Ordinal);
        private bool _throwAfterNextPublish;

        public TrackingPresentationStore(bool throwAfterNextPublish)
        {
            _throwAfterNextPublish = throwAfterNextPublish;
        }

        public int PublishCalls { get; private set; }

        public VerifiedWorldPresentation? LastPublished { get; private set; }

        public ValueTask<WorldPresentationPublishResult>
            PublishVerifiedAsync(
                VerifiedWorldPresentation presentation,
                long expectedPreviousContentRevision,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishCalls++;
            WorldPresentationPublishResult result;
            if (_latest.TryGetValue(
                    presentation.PresentationId,
                    out var current))
            {
                result = string.Equals(
                    current.SemanticDigest,
                    presentation.SemanticDigest,
                    StringComparison.Ordinal)
                    ? new WorldPresentationPublishResult(
                        WorldPresentationWriteStatuses.Idempotent,
                        current.ContentRevision,
                        current)
                    : new WorldPresentationPublishResult(
                        WorldPresentationWriteStatuses
                            .PresentationConflict,
                        current.ContentRevision,
                        current);
            }
            else if (expectedPreviousContentRevision != -1)
            {
                result = new WorldPresentationPublishResult(
                    WorldPresentationWriteStatuses.RevisionConflict,
                    currentContentRevision: -1);
            }
            else
            {
                _latest.Add(
                    presentation.PresentationId,
                    presentation);
                LastPublished = presentation;
                result = new WorldPresentationPublishResult(
                    WorldPresentationWriteStatuses.Applied,
                    presentation.ContentRevision,
                    presentation);
            }

            if (_throwAfterNextPublish)
            {
                _throwAfterNextPublish = false;
                throw new InjectedCrashException();
            }

            return new ValueTask<WorldPresentationPublishResult>(result);
        }

        public ValueTask<WorldPresentationProjection?> ReadLatestAsync(
            string presentationId,
            WorldPresentationQuery query,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<WorldPresentationPage> QueryAsync(
            WorldPresentationQuery query,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<WorldPresentationExport> ExportAsync(
            WorldPresentationQuery query,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CountingSettlementStore : IWorldSettlementStore
    {
        private readonly InMemoryWorldSettlementStore _inner = new();

        public long StoreRevisionForTests { get; private set; }

        public ValueTask<WorldSettlementRecord?> ReadAsync(
            string settlementId,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(settlementId, cancellationToken);
        }

        public async ValueTask<WorldSettlementBeginResult> BeginAsync(
            WorldSettlementPlan plan,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.BeginAsync(plan, cancellationToken);
            if (result.Status == WorldSettlementBeginStatus.Created)
            {
                StoreRevisionForTests++;
            }

            return result;
        }

        public async ValueTask<WorldSettlementTransitionResult>
            TryTransitionAsync(
                WorldSettlementTransition transition,
                CancellationToken cancellationToken = default)
        {
            var result = await _inner.TryTransitionAsync(
                transition,
                cancellationToken);
            if (result.Status == WorldSettlementTransitionStatus.Applied)
            {
                StoreRevisionForTests++;
            }

            return result;
        }

        public ValueTask<WorldSettlementPage>
            ListUnsettledAsync(
                WorldSettlementListRequest request,
                CancellationToken cancellationToken = default)
        {
            return _inner.ListUnsettledAsync(
                request,
                cancellationToken);
        }
    }

    private sealed class SynchronizedIntentStore : IWorldSettlementStore
    {
        private readonly InMemoryWorldSettlementStore _inner = new();
        private readonly TaskCompletionSource _bothIntents =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _intentCalls;

        public ValueTask<WorldSettlementRecord?> ReadAsync(
            string settlementId,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(settlementId, cancellationToken);
        }

        public ValueTask<WorldSettlementBeginResult> BeginAsync(
            WorldSettlementPlan plan,
            CancellationToken cancellationToken = default)
        {
            return _inner.BeginAsync(plan, cancellationToken);
        }

        public async ValueTask<WorldSettlementTransitionResult>
            TryTransitionAsync(
                WorldSettlementTransition transition,
                CancellationToken cancellationToken = default)
        {
            if (transition.ExpectedStage == WorldSettlementStage.Pending
                && transition.NextStage
                == WorldSettlementStage.Reconciliation)
            {
                if (Interlocked.Increment(ref _intentCalls) == 2)
                {
                    _bothIntents.TrySetResult();
                }

                await _bothIntents.Task.WaitAsync(cancellationToken);
            }

            return await _inner.TryTransitionAsync(
                transition,
                cancellationToken);
        }

        public ValueTask<WorldSettlementPage> ListUnsettledAsync(
            WorldSettlementListRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.ListUnsettledAsync(request, cancellationToken);
        }
    }

    private sealed class OrderedConcurrentGroupStore
        : IGroupInteractionStore
    {
        private readonly InMemoryGroupInteractionStore _inner;
        private readonly TaskCompletionSource _firstAppendCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCalls;
        private int _readCalls;

        public OrderedConcurrentGroupStore(
            InMemoryGroupInteractionStore inner)
        {
            _inner = inner;
        }

        public async ValueTask<GroupInteractionSession?> ReadAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCalls) == 2)
            {
                await _firstAppendCommitted.Task.WaitAsync(
                    cancellationToken);
            }

            return await _inner.ReadAsync(sessionId, cancellationToken);
        }

        public ValueTask<GroupInteractionWriteResult> CreateAsync(
            GroupInteractionCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateAsync(request, cancellationToken);
        }

        public ValueTask<GroupInteractionWriteResult> ReplaceMembersAsync(
            GroupInteractionMembershipRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReplaceMembersAsync(request, cancellationToken);
        }

        public async ValueTask<GroupInteractionWriteResult> AppendAsync(
            GroupInteractionAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _appendCalls) == 1)
            {
                var result = await _inner.AppendAsync(
                    request,
                    cancellationToken);
                _firstAppendCommitted.TrySetResult();
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken);
                return result;
            }

            return await _inner.AppendAsync(request, cancellationToken);
        }

        public ValueTask<GroupInteractionWriteResult> CloseAsync(
            GroupInteractionCloseRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.CloseAsync(request, cancellationToken);
        }

        public ValueTask<GroupInteractionProjection?> ProjectAsync(
            string sessionId,
            GameEntityIdentity viewer,
            CancellationToken cancellationToken = default)
        {
            return _inner.ProjectAsync(
                sessionId,
                viewer,
                cancellationToken);
        }
    }

    private sealed class InjectedCrashException : Exception;
}
