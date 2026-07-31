using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Persistence.Tests;

public sealed class FileWorldPresentationStoreTests
{
    [Fact]
    public void ResidentMinimumIncludesMaximumAudiencePostingIndex()
    {
        using var directory = new TemporaryDirectory();
        var options = new FileWorldPresentationStoreOptions
        {
            MaxFramePayloadBytes = 1_024,
            MaxResidentBytes = 12_287,
            Limits = new WorldPresentationLimits(
                maxAudienceMembers: 2)
        };

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileWorldPresentationStore(
                directory.File("presentations.log"),
                options));
    }

    [Fact]
    public void EquivalentPathCannotAcquireASecondPresentationWriter()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var equivalent = System.IO.Path.Combine(
            directory.Path,
            ".",
            "presentations.log");
        using (var first = new FileWorldPresentationStore(path))
        {
            _ = Assert.Throws<IOException>(
                () => new FileWorldPresentationStore(equivalent));
        }

        using var reopened = new FileWorldPresentationStore(equivalent);
        Assert.Equal(System.IO.Path.GetFullPath(path), reopened.Path);
    }

    [Fact]
    public async Task VerifiedPresentationReloadsWithTypedContentAndEvidence()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();

        string projectionDigest;
        await using (var store = new FileWorldPresentationStore(path))
        {
            var result = await Publisher(store, fixture.Evidence)
                .PublishAsync(fixture.Draft, -1);

            Assert.Equal(
                WorldPresentationWriteStatuses.Applied,
                result.Status);
            Assert.Equal(1, result.Presentation!.Sequence);
            Assert.Equal(
                fixture.Evidence.SemanticDigest,
                result.Presentation.EvidenceDigest);
            Assert.Equal(
                "dialogue.line",
                result.Presentation.Content.Kind);
            Assert.Equal(
                "npc.greeting",
                result.Presentation.Content.Localization!.Key);
            Assert.Single(result.Presentation.Content.MediaCues);
            var initialProjection = await ReadLatestAsync(store, fixture);
            projectionDigest = initialProjection!.ProjectionDigest;
        }

        await using var recovered = new FileWorldPresentationStore(path);
        var presentation = await ReadLatestAsync(recovered, fixture);

        Assert.NotNull(presentation);
        Assert.Equal(projectionDigest, presentation.ProjectionDigest);
        Assert.Equal(0, presentation.ContentRevision);
        Assert.Equal(12, presentation.Binding.GameTime!.Tick);
        Assert.Equal("secret", presentation.PrivacyClass);
        Assert.Equal("subtitle", presentation.RedactionClass);
        Assert.Equal("alice", presentation.Viewer.EntityId);
        Assert.Equal(1, recovered.StoreRevision);
    }

    [Fact]
    public async Task MissingOrMismatchedReceiptEvidenceFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var fixture = Fixture();

        var missing = Publisher(store, evidence: null);
        var missingError =
            await Assert.ThrowsAsync<WorldPresentationEvidenceException>(
                () => missing.PublishAsync(fixture.Draft, -1).AsTask());
        Assert.Equal(
            "world_presentation_receipt_not_committed",
            missingError.ReasonCode);

        var wrongBinding = new CommittedWorldPresentationEvidence(
            fixture.Source,
            Binding(saveRevision: 99),
            WorldPresentationCommitStatus.Applied,
            "world_effect_applied");
        var bindingError =
            await Assert.ThrowsAsync<WorldPresentationEvidenceException>(
                () => Publisher(store, wrongBinding)
                    .PublishAsync(fixture.Draft, -1)
                    .AsTask());
        Assert.Equal(
            "world_presentation_binding_mismatch",
            bindingError.ReasonCode);

        var wrongSource = new CommittedWorldPresentationEvidence(
            new WorldPresentationSource(
                fixture.Source.WorldReceiptId,
                Digest("other-receipt"),
                fixture.Source.OccurrenceId,
                fixture.Source.ActionId),
            fixture.Binding,
            WorldPresentationCommitStatus.Applied,
            "world_effect_applied");
        var sourceError =
            await Assert.ThrowsAsync<WorldPresentationEvidenceException>(
                () => Publisher(store, wrongSource)
                    .PublishAsync(fixture.Draft, -1)
                    .AsTask());
        Assert.Equal(
            "world_presentation_source_mismatch",
            sourceError.ReasonCode);
        Assert.Equal(0, store.RecordCount);
        Assert.Equal(0, store.StoreRevision);
    }

    [Fact]
    public void EvidenceAndAudienceRejectNonAppliedOrMixedLifetimes()
    {
        var fixture = Fixture();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CommittedWorldPresentationEvidence(
                fixture.Source,
                fixture.Binding,
                (WorldPresentationCommitStatus)999,
                "rejected"));
        _ = Assert.Throws<ArgumentException>(
            () => new WorldPresentationAudience(
                "party",
                3,
                new[]
                {
                    Identity("alice", 1),
                    Identity("alice", 2)
                },
                "secret",
                "subtitle"));
    }

    [Fact]
    public async Task PublishIsIdempotentAndContentRevisionUsesCas()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);

        var applied = await publisher.PublishAsync(fixture.Draft, -1);
        var replay = await publisher.PublishAsync(fixture.Draft, -1);
        var stale = await publisher.PublishAsync(
            Draft(fixture, revision: 1, text: "updated"),
            expectedPreviousContentRevision: -1);
        var updated = await publisher.PublishAsync(
            Draft(fixture, revision: 1, text: "updated"),
            expectedPreviousContentRevision: 0);
        var oldReplay = await publisher.PublishAsync(fixture.Draft, -1);

        Assert.Equal(
            WorldPresentationWriteStatuses.Applied,
            applied.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.Idempotent,
            replay.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.RevisionConflict,
            stale.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.Applied,
            updated.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.Idempotent,
            oldReplay.Status);
        Assert.Equal(2, store.RecordCount);
        Assert.Equal(2, store.StoreRevision);
        Assert.Equal(
            1,
            (await ReadLatestAsync(store, fixture))!
            .ContentRevision);
    }

    [Fact]
    public async Task RevisionCannotChangeReceiptBindingOrAudience()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        _ = await Publisher(store, fixture.Evidence)
            .PublishAsync(fixture.Draft, -1);

        var changedAudience = new WorldPresentationDraft(
            fixture.Draft.PresentationId,
            contentRevision: 1,
            fixture.Source,
            fixture.Binding,
            new WorldPresentationAudience(
                "party",
                4,
                new[] { Identity("alice", 1) },
                "public",
                "subtitle"),
            Content("changed"),
            fixture.Draft.Provenance);
        var result = await Publisher(store, fixture.Evidence)
            .PublishAsync(changedAudience, 0);

        Assert.Equal(
            WorldPresentationWriteStatuses.PresentationConflict,
            result.Status);
        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public async Task QueryRequiresExactCoordinateIncarnationMembershipAndClasses()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        _ = await Publisher(store, fixture.Evidence)
            .PublishAsync(fixture.Draft, -1);

        var reader = Reader(store);
        var visible = await reader.QueryAsync(
            Query(
                fixture.Binding,
                Identity("bob", 2),
                membershipRevision: 3,
                privacy: "secret",
                redaction: "subtitle"));
        var wrongIncarnation = await reader.QueryAsync(
            Query(
                fixture.Binding,
                Identity("bob", 3),
                3,
                "secret",
                "subtitle"));
        var wrongMembership = await reader.QueryAsync(
            Query(
                fixture.Binding,
                Identity("bob", 2),
                4,
                "secret",
                "subtitle"));
        var wrongPrivacy = await reader.QueryAsync(
            Query(
                fixture.Binding,
                Identity("bob", 2),
                3,
                "public",
                "subtitle"));
        var wrongSave = await reader.QueryAsync(
            Query(
                Binding(saveRevision: 8),
                Identity("bob", 2),
                3,
                "secret",
                "subtitle"));
        var unauthorizedLatest = await reader.ReadLatestAsync(
            fixture.Draft.PresentationId,
            new WorldPresentationAccessRequest(
                fixture.Binding,
                Identity("bob", 3),
                "party",
                3,
                new[] { "secret" },
                new[] { "subtitle" }));

        Assert.Single(visible.Items);
        Assert.Empty(wrongIncarnation.Items);
        Assert.Empty(wrongMembership.Items);
        Assert.Empty(wrongPrivacy.Items);
        Assert.Empty(wrongSave.Items);
        Assert.Null(unauthorizedLatest);
    }

    [Fact]
    public async Task SamePresentationIdIsIsolatedAcrossExactSaveBindings()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        var forkBinding = Binding(saveRevision: 8);
        var forkSource = new WorldPresentationSource(
            "receipt-fork",
            Digest("receipt-fork"),
            "event-7",
            "action-speak",
            "operation-fork");
        var forkDraft = new WorldPresentationDraft(
            fixture.Draft.PresentationId,
            0,
            forkSource,
            forkBinding,
            fixture.Draft.Audience,
            Content("fork"),
            fixture.Draft.Provenance);
        var forkEvidence = new CommittedWorldPresentationEvidence(
            forkSource,
            forkBinding,
            WorldPresentationCommitStatus.Applied,
            "world_effect_applied");
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));

        var original = await Publisher(store, fixture.Evidence)
            .PublishAsync(fixture.Draft, -1);
        var forked = await Publisher(store, forkEvidence)
            .PublishAsync(forkDraft, -1);
        var originalRead = await Reader(store).ReadLatestAsync(
            fixture.Draft.PresentationId,
            Query(
                fixture.Binding,
                Identity("alice", 1),
                3,
                "secret",
                "subtitle"));
        var forkRead = await Reader(store).ReadLatestAsync(
            forkDraft.PresentationId,
            Query(
                forkBinding,
                Identity("alice", 1),
                3,
                "secret",
                "subtitle"));

        Assert.Equal(
            WorldPresentationWriteStatuses.Applied,
            original.Status);
        Assert.Equal(
            WorldPresentationWriteStatuses.Applied,
            forked.Status);
        Assert.Equal(
            "hello",
            originalRead!.Content.Localization!.FallbackText);
        Assert.Equal(
            "fork",
            forkRead!.Content.Localization!.FallbackText);
        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public async Task ExportContainsOnlyAuthorizedPageAndBindsAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);
        _ = await publisher.PublishAsync(fixture.Draft, -1);
        _ = await publisher.PublishAsync(
            Draft(fixture, 1, "updated"),
            0);
        var access = Query(
            fixture.Binding,
            Identity("alice", 1),
            3,
            "secret",
            "subtitle");

        var export = await Reader(store).ExportAsync(
            access,
            maxItems: 1);

        Assert.Single(export.Items);
        Assert.True(export.HasMore);
        Assert.Equal(
            export.Items[0].Cursor,
            export.ContinuationCursor);
        Assert.True(
            CanonicalJsonDigest.IsSha256(export.SemanticDigest));
        Assert.Equal("alice", export.Viewer.EntityId);
        Assert.True(export.Binding.IsSameAs(fixture.Binding));
        var publicProperties = typeof(WorldPresentationProjection)
            .GetProperties()
            .Select(item => item.Name)
            .ToArray();
        Assert.DoesNotContain("Audience", publicProperties);
        Assert.DoesNotContain("Source", publicProperties);
        Assert.DoesNotContain("Provenance", publicProperties);
        Assert.DoesNotContain("AudienceDigest", publicProperties);
        Assert.DoesNotContain("SourceDigest", publicProperties);
        Assert.DoesNotContain("ProvenanceDigest", publicProperties);
        Assert.DoesNotContain("EvidenceDigest", publicProperties);
        Assert.DoesNotContain("SemanticDigest", publicProperties);
        Assert.DoesNotContain("Sequence", publicProperties);
        Assert.True(CanonicalJsonDigest.IsSha256(
            export.Items[0].ProjectionDigest));
    }

    [Fact]
    public async Task HostReadAuthorizerCannotBeBypassedByCallerClaims()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        _ = await Publisher(store, fixture.Evidence)
            .PublishAsync(fixture.Draft, -1);
        var request = Query(
            fixture.Binding,
            Identity("alice", 1),
            3,
            "secret",
            "subtitle");

        var error =
            await Assert.ThrowsAsync<
                WorldPresentationAccessDeniedException>(
                () => Reader(store, allowed: false)
                    .QueryAsync(request)
                    .AsTask());

        Assert.Equal(
            "world_presentation_access_denied",
            error.ReasonCode);
    }

    [Fact]
    public async Task TornTailIsTruncatedToLastCommittedPresentation()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        var injector = new PartialSecondFrameFaultInjector();
        long committedLength;

        await using (var store = new FileWorldPresentationStore(
                         path,
                         new FileWorldPresentationStoreOptions
                         {
                             FaultInjector = injector
                         }))
        {
            var publisher = Publisher(store, fixture.Evidence);
            _ = await publisher.PublishAsync(fixture.Draft, -1);
            committedLength = new FileInfo(path).Length;
            _ = await Assert.ThrowsAsync<IOException>(
                () => publisher.PublishAsync(
                        Draft(fixture, 1, "updated"),
                        0)
                    .AsTask());
            _ = await Assert.ThrowsAsync<
                FileWorldPresentationStoreFaultedException>(
                () => ReadLatestAsync(store, fixture).AsTask());
        }

        Assert.True(new FileInfo(path).Length > committedLength);
        await using var recovered = new FileWorldPresentationStore(path);
        var presentation = await ReadLatestAsync(recovered, fixture);
        Assert.Equal(committedLength, new FileInfo(path).Length);
        Assert.NotNull(presentation);
        Assert.Equal(0, presentation.ContentRevision);
        Assert.Equal(1, recovered.RecordCount);
    }

    [Fact]
    public async Task MidFileChecksumCorruptionFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        await using (var store = new FileWorldPresentationStore(path))
        {
            var publisher = Publisher(store, fixture.Evidence);
            _ = await publisher.PublishAsync(fixture.Draft, -1);
            _ = await publisher.PublishAsync(
                Draft(fixture, 1, "updated"),
                0);
        }

        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            stream.Position =
                FileWorldPresentationStore.FrameHeaderSize + 10;
            var value = stream.ReadByte();
            Assert.True(value >= 0);
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x5A));
            stream.Flush(flushToDisk: true);
        }

        _ = Assert.Throws<FileWorldPresentationStoreCorruptionException>(
            () => new FileWorldPresentationStore(path));
    }

    [Fact]
    public async Task CancellationBeforeWriteLeavesNoPresentation()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher(store, fixture.Evidence)
                .PublishAsync(fixture.Draft, -1, cancellation.Token)
                .AsTask());

        Assert.Equal(0, store.RecordCount);
        Assert.Equal(0, store.StoreRevision);
    }

    [Fact]
    public async Task DraftSnapshotsMutableInputs()
    {
        using var directory = new TemporaryDirectory();
        var binding = Binding();
        var source = Source();
        var members = new List<GameEntityIdentity>
        {
            Identity("bob", 2),
            Identity("alice", 1)
        };
        using var payloadDocument = JsonDocument.Parse(
            """{"text":"original"}""");
        var draft = new WorldPresentationDraft(
            "scene-line-1",
            0,
            source,
            binding,
            new WorldPresentationAudience(
                "party",
                3,
                members,
                "secret",
                "subtitle"),
            new WorldPresentationContent(
                "dialogue.line",
                "application/json",
                payloadDocument.RootElement),
            Provenance());
        members.Clear();

        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var evidence = new CommittedWorldPresentationEvidence(
            source,
            binding,
            WorldPresentationCommitStatus.Applied,
            "world_effect_applied");
        var result = await Publisher(store, evidence)
            .PublishAsync(draft, -1);

        Assert.Equal(2, result.Presentation!.Audience.Members.Count);
        Assert.Equal(
            "original",
            result.Presentation.Content.Payload
                .GetProperty("text")
                .GetString());
    }

    [Fact]
    public void BoundedAudienceStopsInfiniteEnumerable()
    {
        var limits = new WorldPresentationLimits(maxAudienceMembers: 2);
        var calls = 0;

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => new WorldPresentationAudience(
                "party",
                0,
                Infinite(),
                "private",
                "full",
                limits));

        Assert.Equal(
            "world_presentation_audience_exceeded",
            error.LimitCode);
        Assert.Equal(3, calls);
        return;

        IEnumerable<GameEntityIdentity> Infinite()
        {
            while (true)
            {
                yield return Identity($"actor-{calls++}", 0);
            }
        }
    }

    [Fact]
    public async Task QueryChecksCancellationWhileScanning()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);
        _ = await publisher.PublishAsync(fixture.Draft, -1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader(store).QueryAsync(
                    Query(
                        fixture.Binding,
                        Identity("alice", 1),
                        3,
                        "secret",
                        "subtitle"),
                    cancellationToken: cancellation.Token)
                .AsTask());
    }

    [Fact]
    public async Task ConcurrentContentCasCommitsExactlyOneRevision()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);
        _ = await publisher.PublishAsync(fixture.Draft, -1);

        var attempts = await Task.WhenAll(
            publisher.PublishAsync(
                    Draft(fixture, 1, "left"),
                    expectedPreviousContentRevision: 0)
                .AsTask(),
            publisher.PublishAsync(
                    Draft(fixture, 1, "right"),
                    expectedPreviousContentRevision: 0)
                .AsTask());

        Assert.Single(
            attempts,
            item => item.Status
                    == WorldPresentationWriteStatuses.Applied);
        Assert.Single(
            attempts,
            item => item.Status
                    == WorldPresentationWriteStatuses
                        .PresentationConflict);
        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public async Task FrameCapacityRejectsBeforeAnyBytesAreWritten()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            path,
            new FileWorldPresentationStoreOptions
            {
                MaxFramePayloadBytes = 1_024
            });

        _ = await Assert.ThrowsAsync<
            FileWorldPresentationStoreCapacityException>(
            () => Publisher(store, fixture.Evidence)
                .PublishAsync(fixture.Draft, -1)
                .AsTask());

        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Equal(0, store.RecordCount);
        Assert.Empty(
            (await Reader(store).QueryAsync(
                Query(
                    fixture.Binding,
                    Identity("alice", 1),
                    3,
                    "secret",
                    "subtitle")))
            .Items);
    }

    [Fact]
    public async Task ValidChecksumCannotHideSemanticPayloadTampering()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        await using (var store = new FileWorldPresentationStore(path))
        {
            _ = await Publisher(store, fixture.Evidence)
                .PublishAsync(fixture.Draft, -1);
        }

        var bytes = await File.ReadAllBytesAsync(path);
        var payloadLength = BitConverter.ToInt32(bytes, 4);
        var original = Encoding.UTF8.GetBytes("hello");
        var replacement = Encoding.UTF8.GetBytes("jello");
        var relativeOffset = FindBytes(
            bytes,
            FileWorldPresentationStore.FrameHeaderSize,
            payloadLength,
            original);
        Assert.True(relativeOffset >= 0);
        Buffer.BlockCopy(
            replacement,
            0,
            bytes,
            FileWorldPresentationStore.FrameHeaderSize + relativeOffset,
            replacement.Length);
        var checksum = ComputeCrc32(
            bytes,
            FileWorldPresentationStore.FrameHeaderSize,
            payloadLength);
        bytes[8] = (byte)checksum;
        bytes[9] = (byte)(checksum >> 8);
        bytes[10] = (byte)(checksum >> 16);
        bytes[11] = (byte)(checksum >> 24);
        await File.WriteAllBytesAsync(path, bytes);

        _ = Assert.Throws<FileWorldPresentationStoreCorruptionException>(
            () => new FileWorldPresentationStore(path));
    }

    [Theory]
    [InlineData(
        """{"presentation":{"audience":{"members":[{},{},{}]}}}""",
        "audience members")]
    [InlineData(
        """{"presentation":{"content":{"mediaCues":[{},{}]}}}""",
        "media cues")]
    [InlineData(
        """{"presentation":{"provenance":{"parentPresentationIds":["a","b"]}}}""",
        "parent presentation IDs")]
    public void RawFrameGuardRejectsTargetArraysBeforeDtoAllocation(
        string json,
        string expectedLimit)
    {
        var limits = new WorldPresentationLimits(
            maxAudienceMembers: 2,
            maxMediaCues: 1,
            maxParentPresentationIds: 1);

        var error =
            Assert.Throws<FileWorldPresentationStoreCapacityException>(
                () => WorldPresentationFrameJsonGuard.Validate(
                    Encoding.UTF8.GetBytes(json),
                    limits,
                    maxTokens: 1_024));

        Assert.Equal(expectedLimit, error.LimitName);
    }

    [Fact]
    public async Task ValidLongPayloadPropertyRoundTripsThroughRawGuard()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        var propertyName = new string('k', 4_096);
        var content = new WorldPresentationContent(
            "world.snapshot",
            "application/json",
            Json($"{{\"{propertyName}\":1}}"));
        var draft = new WorldPresentationDraft(
            fixture.Draft.PresentationId,
            fixture.Draft.ContentRevision,
            fixture.Source,
            fixture.Binding,
            fixture.Draft.Audience,
            content,
            fixture.Draft.Provenance);

        await using (var store = new FileWorldPresentationStore(path))
        {
            _ = await Publisher(store, fixture.Evidence)
                .PublishAsync(draft, -1);
        }

        await using var recovered = new FileWorldPresentationStore(path);
        var projection = await ReadLatestAsync(recovered, fixture);
        var property = Assert.Single(
            projection!.Content.Payload.EnumerateObject());
        Assert.Equal(propertyName, property.Name);
        Assert.Equal(1, property.Value.GetInt32());
    }

    [Fact]
    public async Task MetadataMayExceedTheConfiguredPayloadStringLimit()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        var limits = new WorldPresentationLimits(
            maxPayloadUtf8Bytes: 1_024,
            maxMetadataUtf8Bytes: 65_536);
        var propertyName = new string('m', 8_192);
        var provenance = new WorldPresentationProvenance(
            "host.presentation",
            "1",
            "receipt_projection",
            metadata: Json($"{{\"{propertyName}\":1}}"),
            limits: limits);
        var draft = new WorldPresentationDraft(
            fixture.Draft.PresentationId,
            fixture.Draft.ContentRevision,
            fixture.Source,
            fixture.Binding,
            fixture.Draft.Audience,
            fixture.Draft.Content,
            provenance,
            limits);
        var options = new FileWorldPresentationStoreOptions
        {
            Limits = limits
        };

        await using (var store = new FileWorldPresentationStore(path, options))
        {
            _ = await Publisher(store, fixture.Evidence)
                .PublishAsync(draft, -1);
        }

        await using var recovered = new FileWorldPresentationStore(
            path,
            options);
        Assert.NotNull(await ReadLatestAsync(recovered, fixture));
    }

    [Fact]
    public void DraftEnforcesAggregateNodeBudgetBeforePublish()
    {
        var fixture = Fixture();
        var limits = new WorldPresentationLimits(
            maxJsonNodes: 32,
            maxAggregateJsonNodes: 40);

        _ = Assert.Throws<RuntimeContentLimitException>(
            () => new WorldPresentationDraft(
                fixture.Draft.PresentationId,
                fixture.Draft.ContentRevision,
                fixture.Source,
                fixture.Binding,
                fixture.Draft.Audience,
                fixture.Draft.Content,
                fixture.Draft.Provenance,
                limits));
    }

    [Fact]
    public async Task ResidentBudgetStopsGrowthBeforeSecondFrame()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"),
            new FileWorldPresentationStoreOptions
            {
                MaxFramePayloadBytes = 4_096,
                MaxResidentBytes = 28_672,
                Limits = new WorldPresentationLimits(
                    maxAudienceMembers: 2)
            });
        var publisher = Publisher(store, fixture.Evidence);
        _ = await publisher.PublishAsync(fixture.Draft, -1);

        var error =
            await Assert.ThrowsAsync<
                FileWorldPresentationStoreCapacityException>(
                () => publisher.PublishAsync(
                        Draft(fixture, 1, "updated"),
                        0)
                    .AsTask());

        Assert.Equal(
            nameof(
                FileWorldPresentationStoreOptions.MaxResidentBytes),
            error.LimitName);
        Assert.Equal(1, store.RecordCount);
        Assert.True(store.EstimatedResidentBytes > 0);
    }

    [Fact]
    public async Task EmptyAudiencePageDoesNotRevealHiddenScopeSequence()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        _ = await Publisher(store, fixture.Evidence)
            .PublishAsync(fixture.Draft, -1);
        var access = Query(
            fixture.Binding,
            Identity("charlie", 1),
            3,
            "secret",
            "subtitle");

        var first = await Reader(store).QueryAsync(access);
        var second = await Reader(store).QueryAsync(
            access,
            afterCursor: first.ContinuationCursor);

        Assert.Empty(first.Items);
        Assert.Null(first.ContinuationCursor);
        Assert.Empty(second.Items);
        Assert.Null(second.ContinuationCursor);
    }

    [Fact]
    public async Task OpaqueCursorSurvivesReloadAndCannotCrossViewers()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("presentations.log");
        var fixture = Fixture();
        string cursor;
        await using (var store = new FileWorldPresentationStore(path))
        {
            var publisher = Publisher(store, fixture.Evidence);
            _ = await publisher.PublishAsync(fixture.Draft, -1);
            _ = await publisher.PublishAsync(
                Draft(fixture, 1, "updated"),
                0);
            var first = await Reader(store).QueryAsync(
                Query(
                    fixture.Binding,
                    Identity("alice", 1),
                    3,
                    "secret",
                    "subtitle"),
                maxItems: 1);
            cursor = Assert.IsType<string>(first.ContinuationCursor);

            var error = await Assert.ThrowsAsync<
                WorldPresentationCursorException>(
                () => Reader(store).QueryAsync(
                        Query(
                            fixture.Binding,
                            Identity("bob", 2),
                            3,
                            "secret",
                            "subtitle"),
                        afterCursor: cursor)
                    .AsTask());
            Assert.Equal(
                "world_presentation_cursor_invalid",
                error.ReasonCode);

            var changedGrant = new WorldPresentationAccessRequest(
                fixture.Binding,
                Identity("alice", 1),
                "party",
                3,
                new[] { "public", "secret" },
                new[] { "subtitle" });
            _ = await Assert.ThrowsAsync<WorldPresentationCursorException>(
                () => Reader(store).QueryAsync(
                        changedGrant,
                        afterCursor: cursor)
                    .AsTask());
        }

        await using var recovered = new FileWorldPresentationStore(path);
        var second = await Reader(recovered).QueryAsync(
            Query(
                fixture.Binding,
                Identity("alice", 1),
                3,
                "secret",
                "subtitle"),
            afterCursor: cursor);

        var item = Assert.Single(second.Items);
        Assert.Equal(1, item.ContentRevision);
        Assert.NotEqual(cursor, item.Cursor);
    }

    [Fact]
    public async Task ExactClassPostingsMergeInCommitOrder()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);
        _ = await publisher.PublishAsync(fixture.Draft, -1);
        var publicDraft = new WorldPresentationDraft(
            "scene-line-public",
            0,
            fixture.Source,
            fixture.Binding,
            new WorldPresentationAudience(
                "party",
                3,
                fixture.Draft.Audience.Members,
                "public",
                "subtitle"),
            Content("public"),
            fixture.Draft.Provenance);
        _ = await publisher.PublishAsync(publicDraft, -1);
        var access = new WorldPresentationAccessRequest(
            fixture.Binding,
            Identity("alice", 1),
            "party",
            3,
            new[] { "public", "secret" },
            new[] { "subtitle" });

        var first = await Reader(store).QueryAsync(
            access,
            maxItems: 1);
        var firstItem = Assert.Single(first.Items);
        Assert.Equal(fixture.Draft.PresentationId, firstItem.PresentationId);
        Assert.True(first.HasMore);

        var second = await Reader(store).QueryAsync(
            access,
            afterCursor: first.ContinuationCursor,
            maxItems: 1);
        var secondItem = Assert.Single(second.Items);
        Assert.Equal(publicDraft.PresentationId, secondItem.PresentationId);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task QueryEnforcesAggregateProjectionByteBudget()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture();
        await using var store = new FileWorldPresentationStore(
            directory.File("presentations.log"));
        var publisher = Publisher(store, fixture.Evidence);
        var first = await publisher.PublishAsync(fixture.Draft, -1);
        _ = await publisher.PublishAsync(
            Draft(fixture, 1, "updated"),
            0);
        var access = Query(
            fixture.Binding,
            Identity("alice", 1),
            3,
            "secret",
            "subtitle");

        var page = await Reader(store).QueryAsync(
            access,
            maxProjectedUtf8Bytes:
                first.Presentation!.ProjectionUtf8Bytes + 1);
        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => Reader(store).QueryAsync(
                    access,
                    maxProjectedUtf8Bytes: 1_024)
                .AsTask());

        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        Assert.Equal(
            "world_presentation_projection_bytes_exceeded",
            error.LimitCode);
    }

    private static DurableWorldPresentationPublisher Publisher(
        IWorldPresentationStore store,
        CommittedWorldPresentationEvidence? evidence)
    {
        return new DurableWorldPresentationPublisher(
            new FixedEvidenceSource(evidence),
            store);
    }

    private static FixtureData Fixture()
    {
        var source = Source();
        var binding = Binding();
        var draft = new WorldPresentationDraft(
            "scene-line-1",
            0,
            source,
            binding,
            new WorldPresentationAudience(
                "party",
                3,
                new[]
                {
                    Identity("alice", 1),
                    Identity("bob", 2)
                },
                "secret",
                "subtitle"),
            Content("hello"),
            Provenance());
        var evidence = new CommittedWorldPresentationEvidence(
            source,
            binding,
            WorldPresentationCommitStatus.Applied,
            "world_effect_applied",
            Json("""{"receiptRevision":4}"""));
        return new FixtureData(
            source,
            binding,
            draft,
            evidence);
    }

    private static WorldPresentationDraft Draft(
        FixtureData fixture,
        long revision,
        string text)
    {
        return new WorldPresentationDraft(
            fixture.Draft.PresentationId,
            revision,
            fixture.Source,
            fixture.Binding,
            fixture.Draft.Audience,
            Content(text),
            fixture.Draft.Provenance);
    }

    private static WorldPresentationSource Source()
    {
        return new WorldPresentationSource(
            "receipt-4",
            Digest("receipt-4"),
            "event-7",
            "action-speak",
            "operation-4");
    }

    private static WorldPresentationBinding Binding(
        long saveRevision = 7)
    {
        return new WorldPresentationBinding(
            "world",
            "timeline",
            timelineEpoch: 2,
            saveRevision,
            stateVersion: 19,
            catalogDigest: Digest("catalog"),
            gameTime: new GameTimePoint(
                "month",
                "timeline",
                epoch: 2,
                tick: 12),
            committedStateDigest: Digest("state"));
    }

    private static WorldPresentationContent Content(string text)
    {
        return new WorldPresentationContent(
            "dialogue.line",
            "application/json",
            Json($$"""{"speaker":"alice","text":"{{text}}"}"""),
            new WorldPresentationLocalization(
                "npc.greeting",
                "en",
                Json("""{"name":"Alice"}"""),
                fallbackText: text),
            new[]
            {
                new WorldPresentationMediaCue(
                    "voice",
                    "audio.play",
                    "voices/greeting.ogg",
                    "audio/ogg",
                    Json("""{"volume":"1.0"}"""),
                    Digest("voice"))
            });
    }

    private static WorldPresentationProvenance Provenance()
    {
        return new WorldPresentationProvenance(
            "host.presentation",
            "1",
            "receipt_projection",
            metadata: Json("""{"policy":"dialogue-v1"}"""));
    }

    private static WorldPresentationAccessRequest Query(
        WorldPresentationBinding binding,
        GameEntityIdentity viewer,
        long membershipRevision,
        string privacy,
        string redaction)
    {
        return new WorldPresentationAccessRequest(
            binding,
            viewer,
            "party",
            membershipRevision,
            new[] { privacy },
            new[] { redaction });
    }

    private static ValueTask<WorldPresentationProjection?> ReadLatestAsync(
        IWorldPresentationStore store,
        FixtureData fixture)
    {
        return Reader(store).ReadLatestAsync(
            fixture.Draft.PresentationId,
            new WorldPresentationAccessRequest(
                fixture.Binding,
                Identity("alice", 1),
                "party",
                3,
                new[] { "secret" },
                new[] { "subtitle" }));
    }

    private static DurableWorldPresentationReader Reader(
        IWorldPresentationStore store,
        bool allowed = true)
    {
        return new DurableWorldPresentationReader(
            new FixedReadAuthorizer(allowed),
            store);
    }

    private static GameEntityIdentity Identity(
        string id,
        long incarnation)
    {
        return new GameEntityIdentity(id, incarnation);
    }

    private static string Digest(string value)
    {
        return CanonicalJsonDigest.ComputeSha256(
            JsonArrayBuilder.String(value));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
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
            var match = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (haystack[candidate + index] == needle[index])
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
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
            var item = value[index];
            checksum = table[(checksum ^ item) & 0xFF]
                       ^ checksum >> 8;
        }

        return ~checksum;
    }

    private sealed class FixedEvidenceSource
        : ICommittedWorldPresentationEvidenceSource
    {
        private readonly CommittedWorldPresentationEvidence? _evidence;

        public FixedEvidenceSource(
            CommittedWorldPresentationEvidence? evidence)
        {
            _evidence = evidence;
        }

        public ValueTask<CommittedWorldPresentationEvidence?>
            ReadCommittedAsync(
                string worldReceiptId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<CommittedWorldPresentationEvidence?>(
                _evidence);
        }
    }

    private sealed class FixedReadAuthorizer
        : IWorldPresentationReadAuthorizer
    {
        private readonly bool _allowed;

        public FixedReadAuthorizer(bool allowed)
        {
            _allowed = allowed;
        }

        public ValueTask<bool> IsAuthorizedAsync(
            WorldPresentationAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(_allowed);
        }
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
                "game-agent-presentation-tests",
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

    private sealed record FixtureData(
        WorldPresentationSource Source,
        WorldPresentationBinding Binding,
        WorldPresentationDraft Draft,
        CommittedWorldPresentationEvidence Evidence);
}
