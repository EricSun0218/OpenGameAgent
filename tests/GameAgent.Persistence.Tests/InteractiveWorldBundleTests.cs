using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.Persistence.Tests;

public sealed class InteractiveWorldBundleTests
{
    [Fact]
    public async Task PrivateBundleCopiesEverySettledSidecarDeterministically()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);

        var first = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var second = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.GetBytes(), second.GetBytes());

        var target = directory.Path("restored");
        var imported = await InteractiveWorldBundle.ImportAsync(
            fixture.Package,
            first.GetBytes(),
            target);

        Assert.Equal(target, imported.TargetDirectory);
        Assert.True(File.Exists(imported.AuthoritativeStorePath));
        using (var memories = new FileMemoryStore(
                   imported.MemoryStorePath))
        {
            var records = await memories
                .CaptureInteractiveWorldBundleAsync(
                    10,
                    CancellationToken.None);
            Assert.Equal(
                new[] { "memory-1" },
                records.Select(static item => item.MemoryId));
        }

        using (var groups = new FileGroupInteractionStore(
                   imported.GroupInteractionStorePath))
        {
            var session = await groups.ReadAsync("session-1");
            Assert.NotNull(session);
            Assert.Equal(GroupInteractionStatuses.Closed, session!.Status);
            Assert.Single(session.Messages);
            Assert.Equal(
                "actor",
                Assert.Single(session.Messages[0].Audience).EntityId);
        }

        using var presentations = new FileWorldPresentationStore(
            imported.PresentationStorePath);
        var presentationRecords = await presentations
            .CaptureInteractiveWorldBundleAsync(
                10,
                CancellationToken.None);
        var presentation = Assert.Single(presentationRecords);
        Assert.Equal("presentation-1", presentation.PresentationId);
        Assert.Equal("actor", Assert.Single(
            presentation.Audience.Members).EntityId);
    }

    [Fact]
    public async Task HistoricalPrivateSidecarsSurviveUpgradeAndRemoval()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var beforeUpgrade = Assert.IsType<
            WorldAuthoritativeStateSnapshot>(
            await fixture.Runtime.ReadSnapshotAsync());
        var upgraded = await MutateIncarnationsAsync(
            fixture.Runtime,
            beforeUpgrade,
            "upgrade-private-sidecars",
            draft => draft.SetIncarnation("actor", 2));
        Assert.Equal(2, upgraded.EntityIncarnations["actor"]);
        Assert.True(upgraded.WasIncarnationIssued("actor", 1));
        Assert.True(upgraded.WasIncarnationIssued("actor", 2));

        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var imported = await InteractiveWorldBundle.ImportAsync(
            fixture.Package,
            artifact.GetBytes(),
            directory.Path("upgraded"));
        await using (var memories = new FileMemoryStore(
                         imported.MemoryStorePath))
        {
            var oldActor = await memories.SearchAsync(
                new MemoryQuery(
                    "actor-private",
                    Json("""{"fact":"remember"}"""),
                    observer: new GameEntityIdentity("actor", 1),
                    gameTime: new GameTimePoint(
                        "turn",
                        imported.Binding.TimelineId,
                        imported.Binding.TimelineEpoch,
                        0)),
                CancellationToken.None);
            var newActor = await memories.SearchAsync(
                new MemoryQuery(
                    "actor-private",
                    Json("""{"fact":"remember"}"""),
                    observer: new GameEntityIdentity("actor", 2),
                    gameTime: new GameTimePoint(
                        "turn",
                        imported.Binding.TimelineId,
                        imported.Binding.TimelineEpoch,
                        0)),
                CancellationToken.None);
            Assert.Single(oldActor);
            Assert.Empty(newActor);
        }

        await using (var groups = new FileGroupInteractionStore(
                         imported.GroupInteractionStorePath))
        {
            Assert.NotNull(await groups.ReadAsync("session-1"));
        }

        await using (var presentations =
                     new FileWorldPresentationStore(
                         imported.PresentationStorePath))
        {
            Assert.Single(
                await presentations.CaptureInteractiveWorldBundleAsync(
                    10,
                    CancellationToken.None));
        }

        var removed = await MutateIncarnationsAsync(
            fixture.Runtime,
            upgraded,
            "remove-private-sidecars",
            draft => draft.RemoveIncarnation("actor"));
        Assert.False(removed.TryGetIncarnation("actor", out _));
        Assert.True(removed.WasIncarnationIssued("actor", 1));
        _ = await InteractiveWorldBundle.CaptureAsync(fixture.Source);
    }

    [Fact]
    public async Task FutureAndUnknownSidecarIdentitiesFailImport()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);

        var future = RehashEntryText(
            artifact.GetBytes(),
            "memory-sidecar.json",
            "\"incarnation\":1",
            "\"incarnation\":3");
        var futureTarget = directory.Path("future-incarnation");
        var futureError =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    future,
                    futureTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            futureError.ReasonCode);
        Assert.False(Directory.Exists(futureTarget));

        var unknown = RehashEntryText(
            artifact.GetBytes(),
            "memory-sidecar.json",
            "\"entityId\":\"actor\"",
            "\"entityId\":\"ghost\"");
        var unknownTarget = directory.Path("unknown-entity");
        var unknownError =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    unknown,
                    unknownTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            unknownError.ReasonCode);
        Assert.False(Directory.Exists(unknownTarget));

        var initial = Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await fixture.Runtime.ReadSnapshotAsync());
        var jumped = await MutateIncarnationsAsync(
            fixture.Runtime,
            initial,
            "skip-two",
            draft => draft.SetIncarnation("actor", 3));
        Assert.False(jumped.WasIncarnationIssued("actor", 2));
        var jumpedArtifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var skipped = RehashEntryText(
            jumpedArtifact.GetBytes(),
            "memory-sidecar.json",
            "\"incarnation\":1",
            "\"incarnation\":2");
        var skippedTarget = directory.Path("skipped-incarnation");
        var skippedError =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    skipped,
                    skippedTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            skippedError.ReasonCode);
        Assert.False(Directory.Exists(skippedTarget));
    }

    [Fact]
    public async Task PublicBundleUsesFixedRedactionWithoutAudienceInput()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);

        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source,
            InteractiveWorldBundleExportMode.PublicExport);
        var imported = await InteractiveWorldBundle.ImportAsync(
            fixture.Package,
            artifact.GetBytes(),
            directory.Path("public"));

        Assert.Equal(
            InteractiveWorldBundleExportMode.PublicExport,
            imported.ExportMode);
        using (var memories = new FileMemoryStore(
                   imported.MemoryStorePath))
        {
            Assert.Empty(
                await memories.CaptureInteractiveWorldBundleAsync(
                    0,
                    CancellationToken.None));
        }

        using (var groups = new FileGroupInteractionStore(
                   imported.GroupInteractionStorePath))
        {
            Assert.Equal(0, groups.SessionCount);
        }

        using var presentations = new FileWorldPresentationStore(
            imported.PresentationStorePath);
        Assert.Equal(0, presentations.RecordCount);
    }

    [Fact]
    public async Task ForkRebindsMemoryAndExcludesAbandonedFutureAndEvidence()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var source = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        await fixture.Memory.UpsertAsync(
            Memory(
                "future-memory",
                fixture.Snapshot,
                committed: true),
            CancellationToken.None);

        var fork = await InteractiveWorldBundle.ForkAsync(
            fixture.Package,
            source.GetBytes(),
            "fork");
        var repeatedFork = await InteractiveWorldBundle.ForkAsync(
            fixture.Package,
            source.GetBytes(),
            "fork");
        Assert.Equal(fork.Digest, repeatedFork.Digest);
        Assert.Equal(fork.GetBytes(), repeatedFork.GetBytes());
        var imported = await InteractiveWorldBundle.ImportAsync(
            fixture.Package,
            fork.GetBytes(),
            directory.Path("fork"));
        Assert.Equal("fork", imported.Binding.TimelineId);
        Assert.Equal(0, imported.Binding.SaveRevision);
        Assert.Equal(
            fixture.Snapshot.Coordinate.TimelineEpoch + 1,
            imported.Binding.TimelineEpoch);

        using (var memories = new FileMemoryStore(
                   imported.MemoryStorePath))
        {
            var records = await memories
                .CaptureInteractiveWorldBundleAsync(
                    10,
                    CancellationToken.None);
            var record = Assert.Single(records);
            Assert.Equal("memory-1", record.MemoryId);
            Assert.Equal("fork", record.Provenance!.TimelineId);
            Assert.Equal(0, record.Provenance.SaveRevision);
            Assert.Equal(
                imported.Binding.TimelineEpoch,
                record.GameTimeWindow!.ValidFrom!.Epoch);
        }

        using (var groups = new FileGroupInteractionStore(
                   imported.GroupInteractionStorePath))
        {
            var group = Assert.IsType<GroupInteractionSession>(
                await groups.ReadAsync("session-1"));
            Assert.Equal("fork", group.WorldBinding!.TimelineId);
            Assert.Equal(
                imported.Binding.TimelineEpoch,
                group.WorldBinding.TimelineEpoch);
            Assert.Equal(0, group.WorldBinding.SaveRevision);
        }

        using var presentations = new FileWorldPresentationStore(
            imported.PresentationStorePath);
        Assert.Equal(0, presentations.RecordCount);
    }

    [Fact]
    public async Task CorruptionCapacityAndPackageMismatchPublishNoTarget()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var corrupt = artifact.GetBytes();
        corrupt[^1] ^= 0x20;
        var corruptTarget = directory.Path("corrupt");
        var corruptError =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    corrupt,
                    corruptTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.DigestMismatch,
            corruptError.ReasonCode);
        Assert.False(Directory.Exists(corruptTarget));

        var truncatedTarget = directory.Path("truncated");
        await Assert.ThrowsAsync<InteractiveWorldBundleException>(
            async () => await InteractiveWorldBundle.ImportAsync(
                fixture.Package,
                artifact.GetBytes()[..^1],
                truncatedTarget));
        Assert.False(Directory.Exists(truncatedTarget));

        var capacityTarget = directory.Path("capacity");
        var capacity = new InteractiveWorldBundleImportOptions(
            bundle: new InteractiveWorldBundleOptions(
                new InteractiveWorldBundleLimits(
                    maxMemoryRecords: 0)));
        var capacityError =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    artifact.GetBytes(),
                    capacityTarget,
                    capacity));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.CapacityExceeded,
            capacityError.ReasonCode);
        Assert.False(Directory.Exists(capacityTarget));

        var wrongPackage = Compile(Package("different-package"));
        var mismatchTarget = directory.Path("mismatch");
        var mismatch =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    wrongPackage,
                    artifact.GetBytes(),
                    mismatchTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            mismatch.ReasonCode);
        Assert.False(Directory.Exists(mismatchTarget));
    }

    [Fact]
    public async Task ExistingTargetIsPreservedAndOpenSessionRoundTrips()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var target = directory.Path("existing");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "marker.txt");
        File.WriteAllText(marker, "keep");

        var exists =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    artifact.GetBytes(),
                    target));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.TargetExists,
            exists.ReasonCode);
        Assert.Equal("keep", File.ReadAllText(marker));

        await using var openFixture = await BundleFixture.CreateAsync(
            directory,
            closeGroup: false);
        var openArtifact = await InteractiveWorldBundle.CaptureAsync(
            openFixture.Source);
        var openImport = await InteractiveWorldBundle.ImportAsync(
            openFixture.Package,
            openArtifact.GetBytes(),
            directory.Path("open-session"));
        using var restoredGroups = new FileGroupInteractionStore(
            openImport.GroupInteractionStorePath);
        var restored = Assert.IsType<GroupInteractionSession>(
            await restoredGroups.ReadAsync("session-1"));
        Assert.Equal(
            GroupInteractionStatuses.Open,
            restored.Status);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(0, restored.MembershipRevision);
        var continued = await restoredGroups.AppendAsync(
            new GroupInteractionAppendRequest(
                "continued-after-import",
                "session-1",
                restored.Revision,
                restored.MembershipRevision,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "message-2",
                        "world.event",
                        Json("""{"choice":"continue"}"""),
                        GroupInteractionAudienceModes.AllMembers,
                        new GameEntityIdentity("actor", 1))
                }));
        Assert.True(continued.Succeeded);
        Assert.Equal(2, continued.Session!.Revision);
    }

    [Fact]
    public async Task CaptureUsesOnlyCoordinatorTopologyOutboxFence()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        await using var substituteOutbox =
            new FileWorldSettlementStore(
                directory.Path("substitute-outbox.log"));
        _ = Assert.Throws<InvalidOperationException>(
            () => new WorldSettlementCoordinator(
                new EmptyEvidenceSource(),
                new DenyAuthorityGuard(),
                substituteOutbox,
                fixture.Memory,
                fixture.Groups,
                fixture.Presentations));

        var plan = SettlementPlan(fixture.Snapshot);
        var begin = await fixture.Settlements.BeginAsync(plan);
        Assert.Equal(WorldSettlementBeginStatus.Created, begin.Status);
        var pending =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    fixture.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.Unsettled,
            pending.ReasonCode);

        var reconciliation = await fixture.Settlements.TryTransitionAsync(
            SettlementTransition(
                plan,
                recordRevision: 0,
                WorldSettlementStage.Pending,
                WorldSettlementStage.Reconciliation));
        Assert.Equal(
            WorldSettlementTransitionStatus.Applied,
            reconciliation.Status);
        var uncertain =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    fixture.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.Unsettled,
            uncertain.ReasonCode);

        var rejected = await fixture.Settlements.TryTransitionAsync(
            SettlementTransition(
                plan,
                recordRevision: 1,
                WorldSettlementStage.Reconciliation,
                WorldSettlementStage.Rejected));
        Assert.Equal(
            WorldSettlementTransitionStatus.Applied,
            rejected.Status);
        _ = await InteractiveWorldBundle.CaptureAsync(fixture.Source);
    }

    [Fact]
    public async Task CaptureSourceRejectsNullOrUnsupportedTopology()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        _ = Assert.Throws<ArgumentNullException>(
            () => new InteractiveWorldBundleCaptureSource(
                fixture.Runtime,
                topology: null!));

        var unsupportedSidecar = new WorldSettlementCoordinator(
            new EmptyEvidenceSource(),
            new DenyAuthorityGuard(),
            new InMemoryWorldSettlementStore(),
            memory: new Bm25MemoryStore());
        var sidecarError =
            Assert.Throws<InteractiveWorldBundleException>(
                () => new InteractiveWorldBundleCaptureSource(
                    fixture.Runtime,
                    unsupportedSidecar.Topology));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.TopologyUnsupported,
            sidecarError.ReasonCode);

        var unsupportedOutbox = new WorldSettlementCoordinator(
            new EmptyEvidenceSource(),
            new DenyAuthorityGuard(),
            new NonQuiescentSettlementStore());
        var outboxError =
            Assert.Throws<InteractiveWorldBundleException>(
                () => new InteractiveWorldBundleCaptureSource(
                    fixture.Runtime,
                    unsupportedOutbox.Topology));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.QuiescenceRequired,
            outboxError.ReasonCode);
    }

    [Fact]
    public async Task CompositeCaptureLeasesBlockEverySidecarMutation()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(
            directory,
            closeGroup: false);
        await using var memoryLease =
            await fixture.Memory.AcquireInteractiveWorldBundleCaptureAsync(
                10,
                CancellationToken.None);
        await using var groupLease =
            await fixture.Groups.AcquireInteractiveWorldBundleCaptureAsync(
                10,
                CancellationToken.None);
        await using var presentationLease =
            await fixture.Presentations
                .AcquireInteractiveWorldBundleCaptureAsync(
                    10,
                    CancellationToken.None);

        var memoryWrite = fixture.Memory.UpsertAsync(
                Memory(
                    "blocked-memory",
                    fixture.Snapshot,
                    committed: true),
                CancellationToken.None)
            .AsTask();
        var groupWrite = fixture.Groups.AppendAsync(
                new GroupInteractionAppendRequest(
                    "blocked-group-write",
                    "session-1",
                    expectedRevision: 1,
                    expectedMembershipRevision: 0,
                    new[]
                    {
                        new GroupInteractionMessageDraft(
                            "blocked-message",
                            "world.event",
                            Json("""{"choice":"wait"}"""),
                            GroupInteractionAudienceModes.AllMembers,
                            new GameEntityIdentity("actor", 1))
                    }))
            .AsTask();
        var presentationWrite = fixture.Presentations
            .PublishVerifiedAsync(
                fixture.Presentation,
                expectedPreviousContentRevision: -1)
            .AsTask();
        await Task.Yield();
        Assert.False(memoryWrite.IsCompleted);
        Assert.False(groupWrite.IsCompleted);
        Assert.False(presentationWrite.IsCompleted);

        await presentationLease.DisposeAsync();
        await groupLease.DisposeAsync();
        await memoryLease.DisposeAsync();
        await Task.WhenAll(
            memoryWrite,
            groupWrite,
            presentationWrite);
        var groupResult = await groupWrite;
        var presentationResult = await presentationWrite;
        Assert.Equal(
            GroupInteractionWriteStatuses.Applied,
            groupResult.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.Idempotent,
            presentationResult.Status);
    }

    [Fact]
    public async Task CancelledCaptureReleasesOutboxFence()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        await using var heldMemory =
            await fixture.Memory.AcquireInteractiveWorldBundleCaptureAsync(
                10,
                CancellationToken.None);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await InteractiveWorldBundle.CaptureAsync(
                fixture.Source,
                cancellationToken: cancellation.Token));
        await heldMemory.DisposeAsync();

        var begin = await fixture.Settlements.BeginAsync(
            SettlementPlan(fixture.Snapshot));
        Assert.Equal(WorldSettlementBeginStatus.Created, begin.Status);
    }

    [Fact]
    public async Task UncommittedOrCrossTimelineMemoryIsRejected()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(
            directory,
            addMemory: false);
        await fixture.Memory.UpsertAsync(
            Memory(
                "uncommitted",
                fixture.Snapshot,
                committed: false),
            CancellationToken.None);
        var unsettled =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    fixture.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.Unsettled,
            unsettled.ReasonCode);

        await using var mismatch = await BundleFixture.CreateAsync(
            directory,
            addMemory: false);
        await mismatch.Memory.UpsertAsync(
            Memory(
                "wrong-scope",
                mismatch.Snapshot,
                committed: true,
                timelineId: "other"),
            CancellationToken.None);
        var binding =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    mismatch.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            binding.ReasonCode);
    }

    [Fact]
    public async Task MemoryRequiresExactTimelineEpochOnCapture()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var missing = await BundleFixture.CreateAsync(
            directory,
            addMemory: false);
        await missing.Memory.UpsertAsync(
            Memory(
                "missing-epoch",
                missing.Snapshot,
                committed: true,
                omitTimelineEpoch: true),
            CancellationToken.None);
        var incomplete =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    missing.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.Unsettled,
            incomplete.ReasonCode);

        // A validation failure must release the aggregate capture fence.
        await missing.Memory.UpsertAsync(
            Memory(
                "missing-epoch",
                missing.Snapshot,
                committed: true),
            CancellationToken.None);
        _ = await InteractiveWorldBundle.CaptureAsync(missing.Source);

        await using var stale = await BundleFixture.CreateAsync(
            directory,
            addMemory: false);
        await stale.Memory.UpsertAsync(
            Memory(
                "stale-epoch",
                stale.Snapshot,
                committed: true,
                timelineEpoch:
                    stale.Snapshot.Coordinate.TimelineEpoch + 1),
            CancellationToken.None);
        var mismatch =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    stale.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            mismatch.ReasonCode);
    }

    [Fact]
    public async Task GroupRequiresExactWorldTimelineBinding()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var unbound = await BundleFixture.CreateAsync(
            directory);
        var actor = new GameEntityIdentity("actor", 1);
        _ = await unbound.Groups.CreateAsync(
            new GroupInteractionCreateRequest(
                "legacy-create",
                "legacy-session",
                "legacy-group",
                Json("""{"topic":"legacy"}"""),
                new[] { new GroupInteractionMember(actor) }));
        var missing =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    unbound.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            missing.ReasonCode);

        await using var crossTimeline = await BundleFixture.CreateAsync(
            directory);
        _ = await crossTimeline.Groups.CreateAsync(
            new GroupInteractionCreateRequest(
                "cross-create",
                "cross-session",
                "cross-group",
                Json("""{"topic":"cross"}"""),
                new[] { new GroupInteractionMember(actor) },
                new GroupInteractionWorldBinding(
                    crossTimeline.Snapshot.Coordinate.WorldId,
                    "another-timeline",
                    crossTimeline.Snapshot.Coordinate.TimelineEpoch,
                    crossTimeline.Snapshot.Coordinate.SaveRevision)));
        var mismatch =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    crossTimeline.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            mismatch.ReasonCode);
    }

    [Fact]
    public async Task PresentationMustMatchAuthoritativeReceiptLedger()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var snapshot = fixture.Snapshot;
        var source = new WorldPresentationSource(
            "fabricated-receipt",
            Digest("fabricated-receipt"),
            operationId: "fabricated-operation");
        var binding = new WorldPresentationBinding(
            snapshot.Coordinate.WorldId,
            snapshot.Coordinate.TimelineId,
            snapshot.Coordinate.TimelineEpoch,
            snapshot.Coordinate.SaveRevision,
            snapshot.Coordinate.StateVersion,
            snapshot.Coordinate.CatalogDigest,
            gameTime: null,
            snapshot.StateDigest);
        var draft = new WorldPresentationDraft(
            "fabricated-presentation",
            0,
            source,
            binding,
            new WorldPresentationAudience(
                "session-1",
                0,
                new[] { new GameEntityIdentity("actor", 1) },
                "private",
                "full"),
            new WorldPresentationContent(
                "dialogue",
                "application/json",
                Json("""{"text":"fabricated"}""")),
            new WorldPresentationProvenance(
                "host",
                "1",
                "receipt"));
        var evidence = new CommittedWorldPresentationEvidence(
            source,
            binding,
            WorldPresentationCommitStatus.Applied,
            "applied");
        var published =
            await fixture.Presentations.PublishVerifiedAsync(
                new VerifiedWorldPresentation(0, draft, evidence),
                expectedPreviousContentRevision: -1);
        Assert.Equal(
            WorldPresentationWriteStatuses.Applied,
            published.Status);

        var error =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.CaptureAsync(
                    fixture.Source));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            error.ReasonCode);
    }

    [Fact]
    public async Task RehashedScopeAndPublicPolicyTamperingFailBeforeTarget()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var privateArtifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);

        var scopeBytes = RehashMemoryTimeline(
            privateArtifact.GetBytes(),
            "main",
            "evil");
        var scopeTarget = directory.Path("scope-tamper");
        var scope =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    scopeBytes,
                    scopeTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.BindingMismatch,
            scope.ReasonCode);
        Assert.False(Directory.Exists(scopeTarget));

        var publicBytes = ReplaceManifestTextAndRehash(
            privateArtifact.GetBytes(),
            "private-local",
            "public-export");
        var publicTarget = directory.Path("policy-tamper");
        var policy =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    publicBytes,
                    publicTarget));
        Assert.Equal(
            InteractiveWorldBundleReasonCodes.PrivacyPolicyViolation,
            policy.ReasonCode);
        Assert.False(Directory.Exists(publicTarget));
    }

    [Fact]
    public async Task FailedSidecarSeedNeverPublishesTarget()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var artifact = await InteractiveWorldBundle.CaptureAsync(
            fixture.Source);
        var target = directory.Path("failed-seed");
        var options = new InteractiveWorldBundleImportOptions(
            memoryStore: new FileMemoryStoreOptions
            {
                FaultInjector = new ZeroWriteFaultInjector()
            });

        var error =
            await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                async () => await InteractiveWorldBundle.ImportAsync(
                    fixture.Package,
                    artifact.GetBytes(),
                    target,
                    options));

        Assert.Equal(
            InteractiveWorldBundleReasonCodes.PublicationFailed,
            error.ReasonCode);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".bundle.seed"));
    }

    [Fact]
    public async Task PendingAuthoritativeOwnershipRejectsCapture()
    {
        using var directory = new BundleTemporaryDirectory();
        await using var fixture = await BundleFixture.CreateAsync(directory);
        var request = new WorldTransactionRequest(
            "pending-operation",
            "pending-command",
            CanonicalJsonDigest.ComputeSha256(Json("""{}""")),
            fixture.Snapshot.Coordinate);
        var pending = await fixture.Runtime.TransactionStore.BeginAsync(
            request,
            CancellationToken.None);
        Assert.NotNull(pending.Transaction);
        try
        {
            var error =
                await Assert.ThrowsAsync<InteractiveWorldBundleException>(
                    async () => await InteractiveWorldBundle.CaptureAsync(
                        fixture.Source));
            Assert.Equal(
                InteractiveWorldBundleReasonCodes.Unsettled,
                error.ReasonCode);
        }
        finally
        {
            await pending.Transaction!.DisposeAsync();
        }
    }

    private static MemoryRecord Memory(
        string id,
        WorldAuthoritativeStateSnapshot snapshot,
        bool committed,
        string? timelineId = null,
        long? timelineEpoch = null,
        bool omitTimelineEpoch = false,
        string sourceEventId = "event")
    {
        var coordinate = snapshot.Coordinate;
        var admittedTimeline = timelineId ?? coordinate.TimelineId;
        var instant = new DateTimeOffset(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        return new MemoryRecord(
            id,
            "actor-private",
            Json("""{"fact":"remember"}"""),
            new[] { "fact" },
            50,
            instant,
            instant,
            provenance: new MemoryProvenance(
                coordinate.WorldId,
                "session",
                coordinate.SaveRevision,
                "run",
                sourceEventId,
                committed,
                admittedTimeline,
                new GameKnowledgePerspective(
                    new GameEntityIdentity("actor", 1),
                    "observed"),
                omitTimelineEpoch
                    ? null
                    : timelineEpoch
                      ?? coordinate.TimelineEpoch),
            gameTimeWindow: new GameTimeWindow(
                new GameTimePoint(
                    "turn",
                    admittedTimeline,
                    coordinate.TimelineEpoch,
                    0)));
    }

    private static byte[] RehashMemoryTimeline(
        byte[] artifact,
        string before,
        string after)
    {
        Assert.Equal(
            Encoding.UTF8.GetByteCount(before),
            Encoding.UTF8.GetByteCount(after));
        var manifestLength = ReadInt32(artifact, 8);
        using var manifest = JsonDocument.Parse(
            artifact.AsMemory(44, manifestLength));
        var offset = 44 + manifestLength;
        string? oldDigest = null;
        foreach (var entry in manifest.RootElement
                     .GetProperty("entries")
                     .EnumerateArray())
        {
            var length = int.Parse(
                entry.GetProperty("length").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(
                    entry.GetProperty("path").GetString(),
                    "memory-sidecar.json",
                    StringComparison.Ordinal))
            {
                oldDigest = entry.GetProperty("sha256").GetString();
                ReplaceBytes(
                    artifact,
                    offset,
                    length,
                    Encoding.UTF8.GetBytes(
                        $"\"timelineId\":\"{before}\""),
                    Encoding.UTF8.GetBytes(
                        $"\"timelineId\":\"{after}\""));
                ReplaceBytes(
                    artifact,
                    offset,
                    length,
                    Encoding.UTF8.GetBytes(
                        $"\"timelineId\":\"{before}\""),
                    Encoding.UTF8.GetBytes(
                        $"\"timelineId\":\"{after}\""));
                var newDigest = Convert.ToHexString(
                        SHA256.HashData(
                            artifact.AsSpan(offset, length)))
                    .ToLowerInvariant();
                ReplaceBytes(
                    artifact,
                    44,
                    manifestLength,
                    Encoding.ASCII.GetBytes(oldDigest!),
                    Encoding.ASCII.GetBytes(newDigest));
                break;
            }

            offset += length;
        }

        Assert.NotNull(oldDigest);
        RewriteManifestDigest(artifact, manifestLength);
        return artifact;
    }

    private static byte[] RehashEntryText(
        byte[] artifact,
        string path,
        string before,
        string after)
    {
        Assert.Equal(
            Encoding.UTF8.GetByteCount(before),
            Encoding.UTF8.GetByteCount(after));
        var manifestLength = ReadInt32(artifact, 8);
        using var manifest = JsonDocument.Parse(
            artifact.AsMemory(44, manifestLength));
        var offset = 44 + manifestLength;
        string? oldDigest = null;
        foreach (var entry in manifest.RootElement
                     .GetProperty("entries")
                     .EnumerateArray())
        {
            var length = int.Parse(
                entry.GetProperty("length").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(
                    entry.GetProperty("path").GetString(),
                    path,
                    StringComparison.Ordinal))
            {
                oldDigest = entry.GetProperty("sha256").GetString();
                ReplaceBytes(
                    artifact,
                    offset,
                    length,
                    Encoding.UTF8.GetBytes(before),
                    Encoding.UTF8.GetBytes(after));
                var newDigest = Convert.ToHexString(
                        SHA256.HashData(
                            artifact.AsSpan(offset, length)))
                    .ToLowerInvariant();
                ReplaceBytes(
                    artifact,
                    44,
                    manifestLength,
                    Encoding.ASCII.GetBytes(oldDigest!),
                    Encoding.ASCII.GetBytes(newDigest));
                break;
            }

            offset += length;
        }

        Assert.NotNull(oldDigest);
        RewriteManifestDigest(artifact, manifestLength);
        return artifact;
    }

    private static byte[] ReplaceManifestTextAndRehash(
        byte[] artifact,
        string before,
        string after)
    {
        Assert.Equal(before.Length, after.Length);
        var manifestLength = ReadInt32(artifact, 8);
        ReplaceBytes(
            artifact,
            44,
            manifestLength,
            Encoding.ASCII.GetBytes(before),
            Encoding.ASCII.GetBytes(after));
        RewriteManifestDigest(artifact, manifestLength);
        return artifact;
    }

    private static void RewriteManifestDigest(
        byte[] artifact,
        int manifestLength)
    {
        SHA256.HashData(
                artifact.AsSpan(44, manifestLength))
            .CopyTo(artifact.AsSpan(12, 32));
    }

    private static void ReplaceBytes(
        byte[] value,
        int offset,
        int length,
        byte[] before,
        byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        var last = offset + length - before.Length;
        for (var candidate = offset; candidate <= last; candidate++)
        {
            if (!value.AsSpan(candidate, before.Length)
                    .SequenceEqual(before))
            {
                continue;
            }

            after.CopyTo(value, candidate);
            return;
        }

        Assert.Fail("Expected byte sequence was not found.");
    }

    private static int ReadInt32(byte[] value, int offset)
    {
        return value[offset]
               | value[offset + 1] << 8
               | value[offset + 2] << 16
               | value[offset + 3] << 24;
    }

    private static async ValueTask PopulateGroupAsync(
        FileGroupInteractionStore store,
        bool close,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var actor = new GameEntityIdentity("actor", 1);
        _ = await store.CreateAsync(
            new GroupInteractionCreateRequest(
                "group-create",
                "session-1",
                "group-1",
                Json("""{"topic":"meeting"}"""),
                new[] { new GroupInteractionMember(actor) },
                new GroupInteractionWorldBinding(
                    snapshot.Coordinate.WorldId,
                    snapshot.Coordinate.TimelineId,
                    snapshot.Coordinate.TimelineEpoch,
                    snapshot.Coordinate.SaveRevision)));
        _ = await store.AppendAsync(
            new GroupInteractionAppendRequest(
                "group-append",
                "session-1",
                expectedRevision: 0,
                expectedMembershipRevision: 0,
                new[]
                {
                    new GroupInteractionMessageDraft(
                        "message-1",
                        "world.event",
                        Json("""{"choice":"wait"}"""),
                        GroupInteractionAudienceModes.AllMembers,
                        actor)
                }));
        if (close)
        {
            _ = await store.CloseAsync(
                new GroupInteractionCloseRequest(
                    "group-close",
                    "session-1",
                    expectedRevision: 1,
                    expectedMembershipRevision: 0));
        }
    }

    private static async Task<WorldAuthoritativeStateSnapshot>
        MutateIncarnationsAsync(
            NativeWorldRuntime runtime,
            WorldAuthoritativeStateSnapshot source,
            string suffix,
            Action<IWorldStateDraft> mutate)
    {
        var coordinate = source.Coordinate;
        var request = new WorldTransactionRequest(
            "operation-" + suffix,
            "command-" + suffix,
            coordinate.CatalogDigest,
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
                coordinate.CatalogDigest,
                occurredAt: null));
        var begin = await runtime.TransactionStore.BeginAsync(
            request,
            CancellationToken.None);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        mutate(transaction.Draft);
        var committed = await transaction.CommitEventAsync(
            new WorldEffectReceipt(true, "applied"),
            CancellationToken.None);
        await transaction.DisposeAsync();
        Assert.Equal(
            WorldTransactionCommitStatus.Committed,
            committed.Status);
        return Assert.IsType<WorldAuthoritativeStateSnapshot>(
            await runtime.ReadSnapshotAsync());
    }

    private static async ValueTask<VerifiedWorldPresentation>
        CommittedPresentationAsync(
            NativeWorldRuntime runtime,
            WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        var gameTime = new GameTimePoint(
            "turn",
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            tick: 0);
        var occurrence = new WorldEventHistoryRecord(
            "bundle-presentation-occurrence",
            new WorldEventDefinitionKey(
                coordinate.WorldId,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                "bundle-presentation",
                "1"),
            "bundle-trigger",
            "bundle-resolution",
            Digest("bundle-presentation-plan"),
            gameTime);
        var request = new WorldTransactionRequest(
            "bundle-presentation-operation",
            "bundle-presentation-command",
            Digest("bundle-presentation-payload"),
            coordinate,
            eventOccurrence: occurrence);
        var begin = await runtime.TransactionStore.BeginAsync(
            request,
            CancellationToken.None);
        var transaction = Assert.IsAssignableFrom<
            IWorldAuthoritativeTransaction>(begin.Transaction);
        var committed = await transaction.CommitEventAsync(
            new WorldEffectReceipt(
                applied: true,
                "bundle-presentation-applied"),
            CancellationToken.None);
        await transaction.DisposeAsync();
        Assert.Equal(
            WorldTransactionCommitStatus.Committed,
            committed.Status);
        var evidence = WorldCommandPresentationEvidence.CreateApplied(
            Assert.IsType<WorldCommandReceipt>(committed.Receipt),
            gameTime);
        var draft = new WorldPresentationDraft(
            "presentation-1",
            0,
            evidence.Source,
            evidence.Binding,
            new WorldPresentationAudience(
                "session-1",
                0,
                new[] { new GameEntityIdentity("actor", 1) },
                "private",
                "full"),
            new WorldPresentationContent(
                "dialogue",
                "application/json",
                Json("""{"text":"hello"}""")),
            new WorldPresentationProvenance(
                "host",
                "1",
                "receipt"));
        return new VerifiedWorldPresentation(1, draft, evidence);
    }

    private static VerifiedWorldPresentation Presentation(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var source = new WorldPresentationSource(
            "receipt",
            Digest("receipt"));
        var binding = new WorldPresentationBinding(
            snapshot.Coordinate.WorldId,
            snapshot.Coordinate.TimelineId,
            snapshot.Coordinate.TimelineEpoch,
            snapshot.Coordinate.SaveRevision,
            snapshot.Coordinate.StateVersion,
            snapshot.Coordinate.CatalogDigest,
            new GameTimePoint(
                "turn",
                snapshot.Coordinate.TimelineId,
                snapshot.Coordinate.TimelineEpoch,
                0),
            snapshot.StateDigest);
        var draft = new WorldPresentationDraft(
            "presentation-1",
            0,
            source,
            binding,
            new WorldPresentationAudience(
                "session-1",
                0,
                new[] { new GameEntityIdentity("actor", 1) },
                "private",
                "full"),
            new WorldPresentationContent(
                "dialogue",
                "application/json",
                Json("""{"text":"hello"}""")),
            new WorldPresentationProvenance(
                "host",
                "1",
                "receipt"));
        var evidence = new CommittedWorldPresentationEvidence(
            source,
            binding,
            WorldPresentationCommitStatus.Applied,
            "applied");
        return new VerifiedWorldPresentation(1, draft, evidence);
    }

    private static WorldSettlementPlan SettlementPlan(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var presentation = Presentation(snapshot);
        var evidence = new CommittedWorldPresentationEvidence(
            presentation.Source,
            presentation.Binding,
            WorldPresentationCommitStatus.Applied,
            "applied");
        var owner = new GameEntityIdentity("actor", 1);
        return new WorldSettlementPlan(
            "bundle-fence-settlement",
            evidence,
            new[]
            {
                new WorldSettlementMemoryDelivery(
                    "bundle-fence-delivery",
                    new WorldSettlementAudienceClaim(
                        "actor-private",
                        membershipRevision: 0,
                        new[] { owner },
                        WorldSettlementPrivacyClasses.Private,
                        redactionClass: "none"),
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Memory(
                                "bundle-fence-memory",
                                snapshot,
                                committed: true,
                                sourceEventId:
                                    evidence.Source.WorldReceiptId))
                    })
            });
    }

    private static WorldSettlementTransition SettlementTransition(
        WorldSettlementPlan plan,
        long recordRevision,
        WorldSettlementStage expected,
        WorldSettlementStage next)
    {
        return new WorldSettlementTransition(
            plan.SettlementId,
            plan.SemanticDigest,
            recordRevision,
            plan.Deliveries[0].OperationId,
            expected,
            next,
            next == WorldSettlementStage.Reconciliation
                ? WorldSettlementReasonCodes.DispatchIntentCommitted
                : "test_terminal_rejection");
    }

    private static WorldPackageDefinition Package(
        string packageId = "bundle-package")
    {
        return new WorldPackageDefinition(
            packageId,
            "1",
            new[]
            {
                JsonFile(
                    "world.json",
                    """
                    {
                      "contract": "game-agent.world-definition.v1",
                      "worldId": "world",
                      "defaultTimelineId": "main",
                      "entityStateRootPath": "/entities",
                      "relationshipRootPath": "/relationships",
                      "initialState": {
                        "entities": {"actor": {}},
                        "relationships": {}
                      },
                      "entityIncarnations": {"actor": "1"}
                    }
                    """),
                JsonFile(
                    "clocks.json",
                    """
                    {
                      "contract": "game-agent.world-clocks.v1",
                      "clocks": [{
                        "clockId": "turn",
                        "statePath": "/clocks/turn/tick",
                        "initialTick": "0"
                      }]
                    }
                    """)
            });
    }

    private static ActivatedWorldPackage Compile(
        WorldPackageDefinition definition)
    {
        var compilation = new NativeWorldPackageCompiler().Compile(
            definition);
        Assert.True(
            compilation.Succeeded,
            string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(
                    static item => item.Code + " " + item.Message)));
        return Assert.IsType<ActivatedWorldPackage>(compilation.Package);
    }

    private static WorldPackageFile JsonFile(
        string path,
        string value)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(value));
    }

    private static string Digest(string value)
    {
        return CanonicalJsonDigest.ComputeSha256(Json($"\"{value}\""));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class BundleFixture : IAsyncDisposable
    {
        private BundleFixture(
            ActivatedWorldPackage package,
            NativeWorldRuntime runtime,
            WorldAuthoritativeStateSnapshot snapshot,
            FileMemoryStore memory,
            FileGroupInteractionStore groups,
            FileWorldPresentationStore presentations,
            FileWorldSettlementStore settlements,
            VerifiedWorldPresentation presentation,
            WorldSettlementCoordinator coordinator)
        {
            Package = package;
            Runtime = runtime;
            Snapshot = snapshot;
            Memory = memory;
            Groups = groups;
            Presentations = presentations;
            Settlements = settlements;
            Presentation = presentation;
            Coordinator = coordinator;
            Source = new InteractiveWorldBundleCaptureSource(
                runtime,
                coordinator.Topology);
        }

        public ActivatedWorldPackage Package { get; }

        public NativeWorldRuntime Runtime { get; }

        public WorldAuthoritativeStateSnapshot Snapshot { get; }

        public FileMemoryStore Memory { get; }

        public FileGroupInteractionStore Groups { get; }

        public FileWorldPresentationStore Presentations { get; }

        public FileWorldSettlementStore Settlements { get; }

        public VerifiedWorldPresentation Presentation { get; }

        public WorldSettlementCoordinator Coordinator { get; }

        public InteractiveWorldBundleCaptureSource Source { get; }

        public static async ValueTask<BundleFixture> CreateAsync(
            BundleTemporaryDirectory directory,
            bool closeGroup = true,
            bool addMemory = true)
        {
            var package = Compile(Package());
            var runtime = NativeWorldRuntime.CreateInMemory(package);
            var snapshot = Assert.IsType<WorldAuthoritativeStateSnapshot>(
                await runtime.ReadSnapshotAsync());
            var suffix = Guid.NewGuid().ToString("N");
            var memory = new FileMemoryStore(
                directory.Path($"memory-{suffix}.log"));
            var groups = new FileGroupInteractionStore(
                directory.Path($"groups-{suffix}.log"));
            var presentations = new FileWorldPresentationStore(
                directory.Path($"presentations-{suffix}.log"));
            var settlements = new FileWorldSettlementStore(
                directory.Path($"settlements-{suffix}.log"));
            if (addMemory)
            {
                await memory.UpsertAsync(
                    Memory("memory-1", snapshot, committed: true),
                    CancellationToken.None);
            }

            await PopulateGroupAsync(groups, closeGroup, snapshot);
            var presentation =
                await CommittedPresentationAsync(runtime, snapshot);
            var currentSnapshot =
                Assert.IsType<WorldAuthoritativeStateSnapshot>(
                    await runtime.ReadSnapshotAsync());
            var published = await presentations.PublishVerifiedAsync(
                presentation,
                expectedPreviousContentRevision: -1);
            Assert.Equal(
                WorldPresentationWriteStatuses.Applied,
                published.Status);
            var coordinator = new WorldSettlementCoordinator(
                new EmptyEvidenceSource(),
                new DenyAuthorityGuard(),
                settlements,
                memory,
                groups,
                presentations);
            return new BundleFixture(
                package,
                runtime,
                currentSnapshot,
                memory,
                groups,
                presentations,
                settlements,
                presentation,
                coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            await Settlements.DisposeAsync();
            await Presentations.DisposeAsync();
            await Groups.DisposeAsync();
            await Memory.DisposeAsync();
        }
    }

    private sealed class BundleTemporaryDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-bundle-tests",
            Guid.NewGuid().ToString("N"));

        public BundleTemporaryDirectory()
        {
            Directory.CreateDirectory(_root);
        }

        public string Path(string name)
        {
            return System.IO.Path.Combine(_root, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class EmptyEvidenceSource
        : ICommittedWorldPresentationEvidenceSource
    {
        public ValueTask<CommittedWorldPresentationEvidence?>
            ReadCommittedAsync(
                string worldReceiptId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<
                CommittedWorldPresentationEvidence?>(
                result: null);
        }
    }

    private sealed class DenyAuthorityGuard
        : IWorldSettlementAuthorityGuard
    {
        public ValueTask<IWorldSettlementAuthorityLease?> AcquireAsync(
            WorldSettlementAuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IWorldSettlementAuthorityLease?>(
                result: null);
        }
    }

    private sealed class NonQuiescentSettlementStore
        : IWorldSettlementStore
    {
        private readonly InMemoryWorldSettlementStore _inner = new();

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

        public ValueTask<WorldSettlementTransitionResult>
            TryTransitionAsync(
                WorldSettlementTransition transition,
                CancellationToken cancellationToken = default)
        {
            return _inner.TryTransitionAsync(
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

    private sealed class ZeroWriteFaultInjector : IJournalFaultInjector
    {
        public int GetWriteLength(int frameLength)
        {
            return 0;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }
}
