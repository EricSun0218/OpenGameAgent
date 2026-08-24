using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class SharedBehaviorCatalogPersistenceTests
{
    [Fact]
    public async Task PublicationAndActorAdoptionSurviveFileStoreRestart()
    {
        using var directory = new TemporaryDirectory();
        var catalogPath = Path.Combine(directory.Path, "catalog");
        var sessionPath = Path.Combine(directory.Path, "sessions");
        var publication = Publication();
        var catalog = new FileGameSharedBehaviorStore(catalogPath);
        Assert.True((await catalog.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        catalog = new FileGameSharedBehaviorStore(catalogPath);
        var loaded = await catalog.LoadAsync(publication.PublicationId, TestContext.Current.CancellationToken);
        Assert.Equal(publication.Behavior.ContentHash, loaded!.Behavior.ContentHash);

        var sessions = new FileGameSessionStore(sessionPath);
        var input = Input("adopt");
        await RunAsync(sessions, new ScriptedProvider("created"), input);
        var session = await sessions.LoadAsync(new GameSessionKey("session", "actor"), TestContext.Current.CancellationToken);
        var extension = Extension(catalog);
        Assert.True((await extension.AdoptAsync(
            sessions,
            input,
            session!.Revision,
            publication.PublicationId,
            Boundary,
            "restart-test",
            TestContext.Current.CancellationToken)).Changed);

        sessions = new FileGameSessionStore(sessionPath);
        catalog = new FileGameSharedBehaviorStore(catalogPath);
        extension = Extension(catalog);
        var provider = new ScriptedProvider("after restart");
        await RunAsync(sessions, provider, Input("after-restart", 2), extension);
        Assert.Contains(
            "Use the restart-safe procedure.",
            VisibleText(Assert.Single(provider.Requests)),
            StringComparison.Ordinal);

        var current = await catalog.LoadAsync(publication.PublicationId, TestContext.Current.CancellationToken);
        var revoked = new GameSharedBehaviorPublication(
            current!.PublicationId,
            current.BehaviorFamilyId,
            current.FamilyVersion,
            2,
            GameSharedBehaviorPublicationStatus.Revoked,
            current.Audience,
            current.Behavior,
            current.SourceSession,
            current.TimelineId,
            current.WorldGeneration,
            current.WorldRevision,
            current.AuditReference,
            "host revoked");
        Assert.True((await catalog.SaveAsync(revoked, 1, TestContext.Current.CancellationToken)).Saved);
        Assert.Equal(
            GameSharedBehaviorPublicationStatus.Revoked,
            (await new FileGameSharedBehaviorStore(catalogPath).LoadAsync(
                publication.PublicationId,
                TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task CorruptedPublicationFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var publication = Publication();
        Assert.True((await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.shared-behavior.json"));
        File.WriteAllText(file, "{invalid");
        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await new FileGameSharedBehaviorStore(directory.Path).LoadAsync(
                publication.PublicationId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentFilePublicationCasHasOneWinner()
    {
        using var directory = new TemporaryDirectory();
        var first = new FileGameSharedBehaviorStore(directory.Path);
        var second = new FileGameSharedBehaviorStore(directory.Path);
        var publication = Publication();
        var attempts = await Task.WhenAll(
            first.SaveAsync(publication, 0, TestContext.Current.CancellationToken).AsTask(),
            second.SaveAsync(publication, 0, TestContext.Current.CancellationToken).AsTask());
        Assert.Single(attempts, value => value.Saved);
        Assert.Single(attempts, value => !value.Saved && value.Current?.Revision == 1);
        Assert.Equal(
            publication.Behavior.ContentHash,
            (await new FileGameSharedBehaviorStore(directory.Path).LoadAsync(
            publication.PublicationId,
            TestContext.Current.CancellationToken))!.Behavior.ContentHash);
    }

    [Fact]
    public async Task CapacitySerializationCannotCollideWithPublicationStripe()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSharedBehaviorStore(directory.Path, concurrencyStripes: 1);
        var publication = Publication("shared-behavior-catalog-capacity");

        var saved = await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(saved.Saved);
        Assert.Equal(
            publication.Behavior.ContentHash,
            (await store.LoadAsync(publication.PublicationId, TestContext.Current.CancellationToken))!.Behavior.ContentHash);
    }

    [Fact]
    public async Task FileQueryIsBoundedOrderedAudienceScopedAndFailsClosedOnCorruption()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var worker = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker");
        var other = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "other");
        var first = Publication("a-worker", worker);
        var revokedSource = Publication("b-worker", worker);
        var unrelated = Publication("c-other", other);
        foreach (var publication in new[] { unrelated, revokedSource, first })
        {
            Assert.True((await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        }

        var revoked = new GameSharedBehaviorPublication(
            revokedSource.PublicationId,
            revokedSource.BehaviorFamilyId,
            revokedSource.FamilyVersion,
            2,
            GameSharedBehaviorPublicationStatus.Revoked,
            revokedSource.Audience,
            revokedSource.Behavior,
            revokedSource.SourceSession,
            revokedSource.TimelineId,
            revokedSource.WorldGeneration,
            revokedSource.WorldRevision,
            revokedSource.AuditReference,
            "revoked");
        Assert.True((await store.SaveAsync(revoked, 1, TestContext.Current.CancellationToken)).Saved);

        var publishedOnly = await store.QueryAsync(
            new GameSharedBehaviorStoreQuery(new[] { worker }, 10),
            TestContext.Current.CancellationToken);
        Assert.Equal("a-worker", Assert.Single(publishedOnly).PublicationId);
        var includingRevoked = await new FileGameSharedBehaviorStore(directory.Path).QueryAsync(
            new GameSharedBehaviorStoreQuery(new[] { worker }, 1, includeRevoked: true),
            TestContext.Current.CancellationToken);
        Assert.Equal("a-worker", Assert.Single(includingRevoked).PublicationId);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { worker }, 10),
                cancelled.Token));

        var unrelatedPath = Directory.GetFiles(directory.Path, "*.shared-behavior.json")
            .Single(path => File.ReadAllText(path).Contains("c-other", StringComparison.Ordinal));
        File.WriteAllText(unrelatedPath, "{invalid");
        Assert.Equal(
            "a-worker",
            Assert.Single(await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { worker }, 10),
                TestContext.Current.CancellationToken)).PublicationId);

        var relevantPath = Directory.GetFiles(directory.Path, "*.shared-behavior.json")
            .Single(path => File.ReadAllText(path).Contains("a-worker", StringComparison.Ordinal));
        File.WriteAllText(relevantPath, "{invalid");
        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { worker }, 10),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PendingInsertRecoveryIndexesCommittedPayloadWithoutDuplicatingIt()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var interruptedPath = Path.Combine(directory.Path, "interrupted");
        var publication = Publication("recover-me");
        var source = new FileGameSharedBehaviorStore(sourcePath);
        Assert.True((await source.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);

        Directory.CreateDirectory(interruptedPath);
        var payload = Assert.Single(Directory.GetFiles(sourcePath, "*.shared-behavior.json"));
        File.Copy(payload, Path.Combine(interruptedPath, Path.GetFileName(payload)));
        var pendingPath = Path.Combine(interruptedPath, "shared-behavior-catalog.pending.json");
        await File.WriteAllTextAsync(
            pendingPath,
            JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                publication.PublicationId,
                AudienceKind = publication.Audience.Kind,
                AudienceId = publication.Audience.AudienceId,
                ExpectedEntryCount = 0,
                ExpectedAudienceCount = 0,
                publication.BehaviorFamilyId,
                publication.FamilyVersion,
                ContentHash = publication.Behavior.ContentHash,
            }),
            TestContext.Current.CancellationToken);

        var recovered = new FileGameSharedBehaviorStore(interruptedPath);
        var loaded = await recovered.LoadAsync(
            publication.PublicationId,
            TestContext.Current.CancellationToken);
        Assert.Equal(publication.Behavior.ContentHash, loaded!.Behavior.ContentHash);
        var discovered = await recovered.QueryAsync(
            new GameSharedBehaviorStoreQuery(new[] { publication.Audience }),
            TestContext.Current.CancellationToken);

        Assert.Equal(publication.PublicationId, Assert.Single(discovered).PublicationId);
        Assert.False(File.Exists(pendingPath));
        Assert.True(File.Exists(Path.Combine(interruptedPath, "shared-behavior-catalog.index.json")));
        Assert.False((await recovered.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
    }

    [Theory]
    [InlineData("after-audience-index", false, false, false)]
    [InlineData("after-audience-manifest", true, false, false)]
    [InlineData("after-family-reservation", true, true, false)]
    [InlineData("after-catalog-manifest", true, true, true)]
    public async Task RecoveryCompletesEveryDurableInsertStage(
        string stage,
        bool includeAudienceManifest,
        bool includeReservation,
        bool includeCommittedCatalog)
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var interruptedPath = Path.Combine(directory.Path, stage);
        var publication = Publication("recover-" + stage);
        var source = new FileGameSharedBehaviorStore(sourcePath);
        Assert.True((await source.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);

        Directory.CreateDirectory(interruptedPath);
        var patterns = new List<string>
        {
            "*.shared-behavior.json",
            "shared-behavior-audience-*.index.json",
        };
        if (includeAudienceManifest)
        {
            patterns.Add("shared-behavior-audience-*.manifest.json");
        }

        if (includeReservation)
        {
            patterns.Add("*.reservation.json");
        }

        if (includeCommittedCatalog)
        {
            patterns.Add("shared-behavior-catalog.index.json");
        }

        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.GetFiles(sourcePath, pattern))
            {
                File.Copy(path, Path.Combine(interruptedPath, Path.GetFileName(path)));
            }
        }

        if (!includeCommittedCatalog)
        {
            await File.WriteAllTextAsync(
                Path.Combine(interruptedPath, "shared-behavior-catalog.index.json"),
                JsonSerializer.Serialize(new { FormatVersion = 3, EntryCount = 0 }),
                TestContext.Current.CancellationToken);
        }

        await WritePendingAsync(interruptedPath, publication);

        var recovered = new FileGameSharedBehaviorStore(interruptedPath);
        var result = await recovered.QueryAsync(
            new GameSharedBehaviorStoreQuery(new[] { publication.Audience }),
            TestContext.Current.CancellationToken);

        Assert.Equal(publication.PublicationId, Assert.Single(result).PublicationId);
        Assert.False(File.Exists(Path.Combine(interruptedPath, "shared-behavior-catalog.pending.json")));
    }

    [Fact]
    public async Task QueryRejectsPublicationIndexedUnderAnotherAudience()
    {
        using var directory = new TemporaryDirectory();
        var worker = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker");
        var outsider = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "outsider");
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var workerPublication = Publication("worker-publication", worker);
        var outsiderPublication = Publication("outsider-publication", outsider);
        Assert.True((await store.SaveAsync(workerPublication, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.True((await store.SaveAsync(outsiderPublication, 0, TestContext.Current.CancellationToken)).Saved);
        var workerIndex = Directory.GetFiles(directory.Path, "shared-behavior-audience-*.index.json")
            .Single(path => File.ReadAllText(path).Contains("\"AudienceId\":\"worker\"", StringComparison.Ordinal));
        var index = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(
            workerIndex,
            TestContext.Current.CancellationToken))!;
        await File.WriteAllTextAsync(
            workerIndex,
            JsonSerializer.Serialize(new
            {
                FormatVersion = index["FormatVersion"].GetInt32(),
                Kind = index["Kind"].GetInt32(),
                AudienceId = index["AudienceId"].GetString(),
                PublicationIds = new[] { outsiderPublication.PublicationId },
            }),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { worker }),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AudienceManifestRejectsSameCountPublicationIdReplacement()
    {
        using var directory = new TemporaryDirectory();
        var audience = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker");
        var store = new FileGameSharedBehaviorStore(directory.Path);
        Assert.True((await store.SaveAsync(
            Publication("a-worker", audience),
            0,
            TestContext.Current.CancellationToken)).Saved);
        Assert.True((await store.SaveAsync(
            Publication("b-worker", audience),
            0,
            TestContext.Current.CancellationToken)).Saved);
        var indexPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "shared-behavior-audience-*.index.json"));
        var index = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(
            indexPath,
            TestContext.Current.CancellationToken))!;
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(new
            {
                FormatVersion = index["FormatVersion"].GetInt32(),
                Kind = index["Kind"].GetInt32(),
                AudienceId = index["AudienceId"].GetString(),
                PublicationIds = new[] { "a-worker", "c-forged" },
            }),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { audience }),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingAudienceIndexIsRebuiltFromCommittedPublications()
    {
        using var directory = new TemporaryDirectory();
        var worker = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker");
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var publication = Publication("rebuild-worker", worker);
        Assert.True((await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        var audienceIndex = Assert.Single(Directory.GetFiles(directory.Path, "shared-behavior-audience-*.index.json"));
        File.Delete(audienceIndex);

        var rebuilt = await new FileGameSharedBehaviorStore(directory.Path).QueryAsync(
            new GameSharedBehaviorStoreQuery(new[] { worker }),
            TestContext.Current.CancellationToken);

        Assert.Equal(publication.PublicationId, Assert.Single(rebuilt).PublicationId);
        Assert.True(File.Exists(audienceIndex));
    }

    [Fact]
    public async Task FamilyVersionReservationRejectsConflictingPublicationIdsAcrossStores()
    {
        using var directory = new TemporaryDirectory();
        var first = Publication("family-first");
        var secondSource = Publication("family-second");
        var conflicting = new GameSharedBehaviorPublication(
            secondSource.PublicationId,
            first.BehaviorFamilyId,
            first.FamilyVersion,
            secondSource.Revision,
            secondSource.Status,
            secondSource.Audience,
            secondSource.Behavior,
            secondSource.SourceSession,
            secondSource.TimelineId,
            secondSource.WorldGeneration,
            secondSource.WorldRevision,
            secondSource.AuditReference);
        var attempts = await Task.WhenAll(
            new FileGameSharedBehaviorStore(directory.Path).SaveAsync(first, 0, TestContext.Current.CancellationToken).AsTask(),
            new FileGameSharedBehaviorStore(directory.Path).SaveAsync(conflicting, 0, TestContext.Current.CancellationToken).AsTask());

        Assert.Single(attempts, value => value.Saved);
        Assert.Single(attempts, value => !value.Saved && value.Current is not null);
        Assert.Single(Directory.GetFiles(directory.Path, "*.shared-behavior.json"));
    }

    [Fact]
    public async Task InMemoryStoreAlsoReservesEachFamilyVersionExactlyOnce()
    {
        var store = new InMemoryGameSharedBehaviorStore();
        var first = Publication("memory-family-first");
        var secondSource = Publication("memory-family-second");
        var conflicting = new GameSharedBehaviorPublication(
            secondSource.PublicationId,
            first.BehaviorFamilyId,
            first.FamilyVersion,
            secondSource.Revision,
            secondSource.Status,
            secondSource.Audience,
            secondSource.Behavior,
            secondSource.SourceSession,
            secondSource.TimelineId,
            secondSource.WorldGeneration,
            secondSource.WorldRevision,
            secondSource.AuditReference);

        Assert.True((await store.SaveAsync(first, 0, TestContext.Current.CancellationToken)).Saved);
        var conflict = await store.SaveAsync(conflicting, 0, TestContext.Current.CancellationToken);

        Assert.False(conflict.Saved);
        Assert.Equal(first.PublicationId, conflict.Current!.PublicationId);
    }

    [Fact]
    public async Task ShardedAudienceIndexesReachConfiguredCapacityWithMaximumLengthIds()
    {
        using var directory = new TemporaryDirectory();
        const int capacity = 64;
        var store = new FileGameSharedBehaviorStore(
            directory.Path,
            maximumPublications: capacity,
            maximumFileBytes: 4_000);
        GameSharedBehaviorPublication? last = null;
        var publications = new List<GameSharedBehaviorPublication>();
        for (var index = 0; index < capacity; index++)
        {
            var prefix = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "-";
            var id = prefix + new string('p', 256 - prefix.Length);
            var audienceId = prefix + new string('a', 256 - prefix.Length);
            last = Publication(id, new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, audienceId));
            Assert.True((await store.SaveAsync(last, 0, TestContext.Current.CancellationToken)).Saved);
            publications.Add(last);
        }

        Assert.Equal(capacity, Directory.GetFiles(directory.Path, "*.shared-behavior.json").Length);
        Assert.Equal(capacity, Directory.GetFiles(directory.Path, "shared-behavior-audience-*.index.json").Length);
        Assert.Equal(capacity, Directory.GetFiles(directory.Path, "shared-behavior-audience-*.manifest.json").Length);
        Assert.Equal(
            last!.PublicationId,
            Assert.Single(await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { last.Audience }),
                TestContext.Current.CancellationToken)).PublicationId);
        var firstPage = await store.QueryAsync(
            new GameSharedBehaviorStoreQuery(publications.Select(value => value.Audience), maximumResults: 7),
            TestContext.Current.CancellationToken);
        Assert.Equal(publications.Take(7).Select(value => value.PublicationId), firstPage.Select(value => value.PublicationId));
        var secondPage = await store.QueryAsync(
            new GameSharedBehaviorStoreQuery(
                publications.Select(value => value.Audience),
                maximumResults: 7,
                afterPublicationId: firstPage[^1].PublicationId),
            TestContext.Current.CancellationToken);
        Assert.Equal(publications.Skip(7).Take(7).Select(value => value.PublicationId), secondPage.Select(value => value.PublicationId));
    }

    [Fact]
    public async Task AudienceIndexSizeFailureLeavesNoPendingPayloadAndExistingQueriesWork()
    {
        using var directory = new TemporaryDirectory();
        var audience = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "crowded");
        var store = new FileGameSharedBehaviorStore(
            directory.Path,
            maximumPublications: 100,
            maximumFileBytes: 4_000);
        var saved = 0;
        for (var index = 0; index < 100; index++)
        {
            var prefix = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "-";
            var id = prefix + new string('x', 256 - prefix.Length);
            try
            {
                Assert.True((await store.SaveAsync(
                    Publication(id, audience),
                    0,
                    TestContext.Current.CancellationToken)).Saved);
                saved++;
            }
            catch (PersistenceException)
            {
                break;
            }
        }

        Assert.InRange(saved, 1, 99);
        Assert.False(File.Exists(Path.Combine(directory.Path, "shared-behavior-catalog.pending.json")));
        Assert.Equal(saved, Directory.GetFiles(directory.Path, "*.shared-behavior.json").Length);
        Assert.Equal(
            saved,
            (await new FileGameSharedBehaviorStore(
                directory.Path,
                maximumPublications: 100,
                maximumFileBytes: 4_000).QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { audience }, maximumResults: 100),
                TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public void ReparsePointCatalogRootIsRejectedWhenPlatformSupportsLinks()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "target");
        var link = Path.Combine(directory.Path, "catalog-link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            return;
        }

        Assert.Throws<PersistenceException>(() => new FileGameSharedBehaviorStore(link));
    }

    [Fact]
    public async Task ReparsePointPublicationFileIsRejectedWhenPlatformSupportsLinks()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var publication = Publication("linked-publication");
        Assert.True((await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        var publicationPath = Assert.Single(Directory.GetFiles(directory.Path, "*.shared-behavior.json"));
        var targetPath = Path.Combine(directory.Path, "outside-target.json");
        File.Copy(publicationPath, targetPath);
        File.Delete(publicationPath);
        try
        {
            File.CreateSymbolicLink(publicationPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.LoadAsync(publication.PublicationId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("index")]
    [InlineData("audience-index")]
    [InlineData("manifest")]
    [InlineData("reservation")]
    public async Task ReparsePointCatalogMetadataIsRejectedWhenPlatformSupportsLinks(string kind)
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSharedBehaviorStore(directory.Path);
        var publication = Publication("linked-" + kind);
        Assert.True((await store.SaveAsync(publication, 0, TestContext.Current.CancellationToken)).Saved);
        var path = kind switch
        {
            "pending" => Path.Combine(directory.Path, "shared-behavior-catalog.pending.json"),
            "index" => Path.Combine(directory.Path, "shared-behavior-catalog.index.json"),
            "audience-index" => Assert.Single(Directory.GetFiles(
                directory.Path,
                "shared-behavior-audience-*.index.json")),
            "manifest" => Assert.Single(Directory.GetFiles(
                directory.Path,
                "shared-behavior-audience-*.manifest.json")),
            "reservation" => Assert.Single(Directory.GetFiles(directory.Path, "*.reservation.json")),
            _ => throw new InvalidOperationException("Unknown metadata kind."),
        };
        var target = Path.Combine(directory.Path, "outside-" + kind + ".json");
        if (File.Exists(path))
        {
            File.Copy(path, target);
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(target, "{}", TestContext.Current.CancellationToken);
        }

        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            return;
        }

        if (kind == "reservation")
        {
            await Assert.ThrowsAsync<PersistenceException>(async () =>
                await store.LoadAsync(publication.PublicationId, TestContext.Current.CancellationToken));
        }
        else if (kind == "index")
        {
            await Assert.ThrowsAsync<PersistenceException>(async () =>
                await store.SaveAsync(
                    Publication("second-linked-index"),
                    0,
                    TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<PersistenceException>(async () =>
                await store.QueryAsync(
                    new GameSharedBehaviorStoreQuery(new[] { publication.Audience }),
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void PersistentIdentityRejectsUnpairedUtf16Surrogates()
    {
        var invalidValues = new[]
        {
            new string((char)0xD800, 1),
            new string((char)0xDC00, 1),
        };
        foreach (var invalid in invalidValues)
        {
            Assert.Throws<ArgumentException>(() =>
                new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "audience-" + invalid));
            Assert.Throws<ArgumentException>(() => Publication("publication-" + invalid));
        }
    }

    [Fact]
    public async Task SupplementaryUnicodeScalarRoundTripsThroughPersistentIdentityAndContentHash()
    {
        using var directory = new TemporaryDirectory();
        var scalar = char.ConvertFromUtf32(0x1F600);
        var audience = new GameSharedBehaviorAudience(
            GameSharedBehaviorAudienceKind.Role,
            "audience-" + scalar);
        var behavior = new GameSharedBehaviorDefinition(
            "unicode-behavior",
            1,
            "Title " + scalar,
            "Instructions " + scalar,
            new GameBehaviorReflection(
                "Observation " + scalar,
                "Strategy " + scalar,
                "Outcome " + scalar,
                "Applicability " + scalar));
        var publication = new GameSharedBehaviorPublication(
            "publication-" + scalar,
            "unicode-family",
            1,
            1,
            GameSharedBehaviorPublicationStatus.Published,
            audience,
            behavior,
            new GameSessionKey("source", "source-actor"),
            "world",
            "save-1",
            7,
            "unicode-review");
        var store = new FileGameSharedBehaviorStore(directory.Path);

        Assert.True((await store.SaveAsync(
            publication,
            0,
            TestContext.Current.CancellationToken)).Saved);
        var loaded = await store.LoadAsync(
            publication.PublicationId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(publication.PublicationId, loaded.PublicationId);
        Assert.Equal(audience.AudienceId, loaded.Audience.AudienceId);
        Assert.Equal(behavior.Instructions, loaded.Behavior.Instructions);
        Assert.Equal(behavior.ContentHash, loaded.Behavior.ContentHash);
        Assert.Equal(
            publication.PublicationId,
            Assert.Single(await store.QueryAsync(
                new GameSharedBehaviorStoreQuery(new[] { audience }),
                TestContext.Current.CancellationToken)).PublicationId);
    }

    private static GameBehaviorWorldBoundary Boundary => new("world", "save-1", 7);

    private static GameSharedBehaviorPublication Publication(
        string publicationId = "restart-safe-v1",
        GameSharedBehaviorAudience? audience = null)
    {
        var stableFragment = publicationId.Length <= 100
            ? publicationId
            : publicationId.Substring(0, 100);
        var behavior = new GameSharedBehaviorDefinition(
            "behavior-" + stableFragment,
            1,
            "Restart safe",
            "Use the restart-safe procedure.",
            new GameBehaviorReflection(
                "The prior action committed.",
                "Reuse the verified procedure.",
                "The authoritative result succeeded.",
                "Use for matching requests."),
            inputTypes: new[] { "request" });
        return new GameSharedBehaviorPublication(
            publicationId,
            "family-" + stableFragment,
            1,
            1,
            GameSharedBehaviorPublicationStatus.Published,
            audience ?? new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker"),
            behavior,
            new GameSessionKey("source", "source-actor"),
            "world",
            "save-1",
            7,
            "publication-review");
    }

    private static Task WritePendingAsync(string directory, GameSharedBehaviorPublication publication) =>
        File.WriteAllTextAsync(
            Path.Combine(directory, "shared-behavior-catalog.pending.json"),
            JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                publication.PublicationId,
                AudienceKind = publication.Audience.Kind,
                AudienceId = publication.Audience.AudienceId,
                ExpectedEntryCount = 0,
                ExpectedAudienceCount = 0,
                publication.BehaviorFamilyId,
                publication.FamilyVersion,
                ContentHash = publication.Behavior.ContentHash,
            }),
            TestContext.Current.CancellationToken);

    private static SharedBehaviorCatalogExtension Extension(IGameSharedBehaviorStore store) => new(
        store,
        (_, _) => new ValueTask<GameBehaviorWorldBoundary>(Boundary),
        (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(
            new[] { new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "worker") }),
        (_, _) => new ValueTask<bool>(true),
        (_, _) => new ValueTask<bool>(true));

    private static GameInput Input(string inputId, long tick = 1) =>
        new("session", "actor", "request", "{}", new GameMoment("world", tick), inputId);

    private static async Task RunAsync(
        IGameSessionStore sessions,
        IModelProvider provider,
        GameInput input,
        params IGameAgentExtension[] extensions)
    {
        var builder = new GameAgentBuilder(provider, "model").UseSessionStore(sessions);
        foreach (var extension in extensions)
        {
            builder.UseExtension(extension);
        }

        await using var runtime = builder.Build();
        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
    }

    private static string VisibleText(ModelRequest request) => request.SystemPrompt + "\n" + string.Join(
        "\n",
        request.Messages.SelectMany(message => message.Content).OfType<TextContent>().Select(value => value.Text));

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly string _text;

        public ScriptedProvider(string text) => _text = text;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            yield return ModelStreamEvent.Terminal(
                new ModelResponse(new AgentContent[] { new TextContent(_text) }, ModelStopReason.Stop));
            await Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Tests",
                Guid.NewGuid().ToString("N"));
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
}
