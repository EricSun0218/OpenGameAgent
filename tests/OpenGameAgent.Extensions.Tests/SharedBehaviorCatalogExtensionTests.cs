using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class SharedBehaviorCatalogExtensionTests
{
    [Fact]
    public void ContentHashSeparatesDifferentStructuredFields()
    {
        var left = new GameSharedBehaviorDefinition(
            "field-boundaries",
            1,
            "Field boundaries",
            "Keep structured fields distinct.",
            new GameBehaviorReflection("observation", "strategy", "outcome", "applicability", new[] { "x" }),
            inputTypes: new[] { "y" });
        var right = new GameSharedBehaviorDefinition(
            "field-boundaries",
            1,
            "Field boundaries",
            "Keep structured fields distinct.",
            new GameBehaviorReflection("observation", "strategy", "outcome", "applicability"),
            inputTypes: new[] { "x", "y" });

        Assert.NotEqual(left.ContentHash, right.ContentHash);
    }

    [Fact]
    public void ContentHashCanonicalizesSetOrderingAndRejectsMalformedUnicode()
    {
        var reflection = new GameBehaviorReflection(
            "observation",
            "strategy",
            "outcome",
            "applicability",
            new[] { "second failure", "first failure" });
        var reorderedReflection = new GameBehaviorReflection(
            "observation",
            "strategy",
            "outcome",
            "applicability",
            new[] { "first failure", "second failure" });
        var left = new GameSharedBehaviorDefinition(
            "canonical",
            1,
            "Canonical",
            "Keep sets canonical.",
            reflection,
            inputTypes: new[] { "second", "first" });
        var right = new GameSharedBehaviorDefinition(
            "canonical",
            1,
            "Canonical",
            "Keep sets canonical.",
            reorderedReflection,
            inputTypes: new[] { "first", "second" });

        Assert.Equal(left.ContentHash, right.ContentHash);
        Assert.Throws<ArgumentException>(() => new GameSharedBehaviorDefinition(
            "malformed",
            1,
            "Malformed",
            "invalid-\ud800",
            new GameBehaviorReflection("observation", "strategy", "outcome", "applicability")));
    }

    [Fact]
    public async Task PublishingMakesBehaviorDiscoverableButNeverAutoAdoptsIt()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        var boundary = Boundary();
        var learning = Learning(boundary);
        var shared = Shared(catalog, boundary);
        var sourceInput = Input("source", "learn");
        await RunAsync(sessions, new ScriptedProvider(Proposal(), Text("recorded")), sourceInput, learning);
        var sourceKey = Key(sourceInput);
        var sourceSession = await sessions.LoadAsync(sourceKey, TestContext.Current.CancellationToken);
        var learned = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            sessions,
            sourceKey,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        Assert.True((await learning.ActivateAsync(
            sessions,
            sourceKey,
            learned.BehaviorId,
            learned.Version,
            sourceSession!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);
        sourceSession = await sessions.LoadAsync(sourceKey, TestContext.Current.CancellationToken);
        var published = await shared.PublishAsync(
            sessions,
            sourceKey,
            learned.BehaviorId,
            learned.Version,
            "safe-route",
            1,
            sourceSession!.Revision,
            "safe-route-v1",
            RoleAudience,
            boundary,
            "host-publication-review",
            TestContext.Current.CancellationToken);
        Assert.True(published.Changed);

        var targetInput = Input("target", "before-adoption");
        var before = new ScriptedProvider(Text("before"));
        await RunAsync(sessions, before, targetInput, shared);
        var beforeRequest = Assert.Single(before.Requests);
        Assert.Empty(beforeRequest.Tools);
        Assert.DoesNotContain("publish", VisibleText(beforeRequest), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adopt", VisibleText(beforeRequest), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Use the verified safe route", VisibleText(beforeRequest), StringComparison.Ordinal);
        var discovered = await shared.DiscoverAsync(targetInput, TestContext.Current.CancellationToken);
        Assert.Equal("safe-route-v1", Assert.Single(discovered).PublicationId);

        var targetSession = await sessions.LoadAsync(Key(targetInput), TestContext.Current.CancellationToken);
        var adopted = await shared.AdoptAsync(
            sessions,
            targetInput,
            targetSession!.Revision,
            "safe-route-v1",
            boundary,
            "target-compatible",
            TestContext.Current.CancellationToken);
        Assert.True(adopted.Changed);

        var after = new ScriptedProvider(Text("after"));
        await RunAsync(sessions, after, Input("target", "after-adoption", 2), shared);
        Assert.Contains("Use the verified safe route", VisibleText(Assert.Single(after.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluationSuspendsOnlyTheFailingActorAndRevocationStopsEveryone()
    {
        var fixture = await PublishedFixtureAsync();
        var shared = Shared(
            fixture.Catalog,
            fixture.Boundary,
            new SharedBehaviorCatalogOptions { ConsecutiveFailuresBeforeSuspension = 2 });
        foreach (var actor in new[] { "actor-a", "actor-b" })
        {
            var input = Input(actor, "create-" + actor);
            await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input, shared);
            var snapshot = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
            Assert.True((await shared.AdoptAsync(
                fixture.Sessions,
                input,
                snapshot!.Revision,
                fixture.PublicationId,
                fixture.Boundary,
                "compatible-" + actor,
                TestContext.Current.CancellationToken)).Changed);
        }

        var actorA = new GameSessionKey("session", "actor-a");
        for (var index = 0; index < 2; index++)
        {
            var current = await fixture.Sessions.LoadAsync(actorA, TestContext.Current.CancellationToken);
            Assert.True((await shared.RecordEvaluationAsync(
                fixture.Sessions,
                actorA,
                fixture.PublicationId,
                current!.Revision,
                false,
                "failed-receipt-" + index,
                TestContext.Current.CancellationToken)).Changed);
        }

        var providerA = new ScriptedProvider(Text("a"));
        var providerB = new ScriptedProvider(Text("b"));
        await RunAsync(fixture.Sessions, providerA, Input("actor-a", "after-failure"), shared);
        await RunAsync(fixture.Sessions, providerB, Input("actor-b", "after-failure"), shared);
        Assert.DoesNotContain("Use the verified safe route", VisibleText(Assert.Single(providerA.Requests)), StringComparison.Ordinal);
        Assert.Contains("Use the verified safe route", VisibleText(Assert.Single(providerB.Requests)), StringComparison.Ordinal);

        var publication = await fixture.Catalog.LoadAsync(fixture.PublicationId, TestContext.Current.CancellationToken);
        Assert.True((await shared.RevokeAsync(
            fixture.PublicationId,
            publication!.Revision,
            "superseded by host policy",
            TestContext.Current.CancellationToken)).Changed);
        var afterRevoke = new ScriptedProvider(Text("revoked"));
        await RunAsync(fixture.Sessions, afterRevoke, Input("actor-b", "after-revoke", 3), shared);
        Assert.DoesNotContain("Use the verified safe route", VisibleText(Assert.Single(afterRevoke.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudienceAndWorldGenerationAreRecheckedAtUseTime()
    {
        var fixture = await PublishedFixtureAsync(
            new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.WorldGeneration, "save-1"));
        var currentBoundary = fixture.Boundary;
        var shared = new SharedBehaviorCatalogExtension(
            fixture.Catalog,
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(currentBoundary),
            (input, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(
                input.ActorId == "eligible"
                    ? new[] { new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.WorldGeneration, "save-1") }
                    : new[] { new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "other") }),
            (_, _) => new ValueTask<bool>(true),
            (_, _) => new ValueTask<bool>(true));
        var deniedInput = Input("denied", "denied");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), deniedInput, shared);
        var deniedSession = await fixture.Sessions.LoadAsync(Key(deniedInput), TestContext.Current.CancellationToken);
        var denied = await shared.AdoptAsync(
            fixture.Sessions,
            deniedInput,
            deniedSession!.Revision,
            fixture.PublicationId,
            currentBoundary,
            "attempt",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameSharedBehaviorMutationStatus.AudienceDenied, denied.Status);

        var eligible = Input("eligible", "eligible");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), eligible, shared);
        var eligibleSession = await fixture.Sessions.LoadAsync(Key(eligible), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            fixture.Sessions,
            eligible,
            eligibleSession!.Revision,
            fixture.PublicationId,
            currentBoundary,
            "compatible",
            TestContext.Current.CancellationToken)).Changed);
        currentBoundary = new GameBehaviorWorldBoundary("world", "save-2", 1);
        var provider = new ScriptedProvider(Text("loaded"));
        await RunAsync(fixture.Sessions, provider, Input("eligible", "after-load", 2), shared);
        Assert.DoesNotContain("Use the verified safe route", VisibleText(Assert.Single(provider.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectionFailsClosedWhenStoreChangesAdoptedLineage()
    {
        var fixture = await PublishedFixtureAsync();
        var original = await fixture.Catalog.LoadAsync(
            fixture.PublicationId,
            TestContext.Current.CancellationToken);
        var forged = new GameSharedBehaviorPublication(
            original!.PublicationId,
            "different-family",
            original.FamilyVersion,
            original.Revision,
            original.Status,
            original.Audience,
            original.Behavior,
            original.SourceSession,
            original.TimelineId,
            original.WorldGeneration,
            original.WorldRevision,
            original.AuditReference);
        var normal = Shared(fixture.Catalog, fixture.Boundary);
        var input = Input("lineage-target", "create");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input, normal);
        var session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await normal.AdoptAsync(
            fixture.Sessions,
            input,
            session!.Revision,
            fixture.PublicationId,
            fixture.Boundary,
            "lineage-validated",
            TestContext.Current.CancellationToken)).Changed);

        var guarded = Shared(new SubstitutingStore(fixture.Catalog, forged), fixture.Boundary);
        var provider = new ScriptedProvider(Text("after"));
        await RunAsync(fixture.Sessions, provider, Input("lineage-target", "after", 2), guarded);

        Assert.DoesNotContain("Use the verified safe route", VisibleText(Assert.Single(provider.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentAdoptionUsesSessionCasAndHasOneWinner()
    {
        var fixture = await PublishedFixtureAsync();
        var shared = Shared(fixture.Catalog, fixture.Boundary);
        var input = Input("target", "adopt");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input, shared);
        var session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        var attempts = await Task.WhenAll(
            shared.AdoptAsync(
                fixture.Sessions, input, session!.Revision, fixture.PublicationId, fixture.Boundary, "one",
                TestContext.Current.CancellationToken).AsTask(),
            shared.AdoptAsync(
                fixture.Sessions, input, session.Revision, fixture.PublicationId, fixture.Boundary, "two",
                TestContext.Current.CancellationToken).AsTask());
        Assert.Single(attempts, value => value.Status == GameSharedBehaviorMutationStatus.Changed);
        Assert.Single(attempts, value => value.Status is GameSharedBehaviorMutationStatus.RevisionConflict
            or GameSharedBehaviorMutationStatus.SessionConflict);
    }

    [Fact]
    public async Task AdoptionRejectsCallerForgedWorldBoundaryBeforeValidationOrMutation()
    {
        var fixture = await PublishedFixtureAsync();
        var validationCalls = 0;
        var authoritative = new GameBehaviorWorldBoundary(
            fixture.Boundary.TimelineId,
            fixture.Boundary.Generation,
            fixture.Boundary.Revision + 1);
        var shared = new SharedBehaviorCatalogExtension(
            fixture.Catalog,
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(authoritative),
            (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[] { RoleAudience }),
            (_, _) => new ValueTask<bool>(true),
            (_, _) =>
            {
                Interlocked.Increment(ref validationCalls);
                return new ValueTask<bool>(true);
            });
        var input = Input("target", "forged-boundary");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input);
        var before = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);

        var result = await shared.AdoptAsync(
            fixture.Sessions,
            input,
            before!.Revision,
            fixture.PublicationId,
            fixture.Boundary,
            "forged",
            TestContext.Current.CancellationToken);

        Assert.Equal(GameSharedBehaviorMutationStatus.WorldChanged, result.Status);
        Assert.Equal(0, validationCalls);
        var after = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.Equal(before.Revision, after!.Revision);
        Assert.Empty((await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            fixture.Sessions,
            Key(input),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions);
    }

    [Fact]
    public async Task ValidatorsFailClosedWithoutChangingCatalogOrActorSession()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        var boundary = Boundary();
        var learning = Learning(boundary);
        var source = Input("source", "learn");
        await RunAsync(sessions, new ScriptedProvider(Proposal(), Text("recorded")), source, learning);
        var sourceSession = await sessions.LoadAsync(Key(source), TestContext.Current.CancellationToken);
        var behavior = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            sessions,
            Key(source),
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        Assert.True((await learning.ActivateAsync(
            sessions,
            Key(source),
            behavior.BehaviorId,
            behavior.Version,
            sourceSession!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);
        sourceSession = await sessions.LoadAsync(Key(source), TestContext.Current.CancellationToken);
        var rejectingPublication = new SharedBehaviorCatalogExtension(
            catalog,
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[] { RoleAudience }),
            (_, _) => throw new InvalidOperationException("validator failed"),
            (_, _) => new ValueTask<bool>(true));
        var rejected = await rejectingPublication.PublishAsync(
            sessions,
            Key(source),
            behavior.BehaviorId,
            behavior.Version,
            "rejected-behavior",
            1,
            sourceSession!.Revision,
            "rejected-publication",
            RoleAudience,
            boundary,
            "review",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameSharedBehaviorMutationStatus.ValidationRejected, rejected.Status);
        Assert.Null(await catalog.LoadAsync("rejected-publication", TestContext.Current.CancellationToken));

        var publishing = Shared(catalog, boundary);
        Assert.True((await publishing.PublishAsync(
            sessions,
            Key(source),
            behavior.BehaviorId,
            behavior.Version,
            "accepted-behavior",
            1,
            sourceSession.Revision,
            "accepted-publication",
            RoleAudience,
            boundary,
            "review",
            TestContext.Current.CancellationToken)).Changed);
        var target = Input("target", "create");
        await RunAsync(sessions, new ScriptedProvider(Text("created")), target);
        var before = await sessions.LoadAsync(Key(target), TestContext.Current.CancellationToken);
        var rejectingAdoption = new SharedBehaviorCatalogExtension(
            catalog,
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[] { RoleAudience }),
            (_, _) => new ValueTask<bool>(true),
            (_, _) => throw new InvalidOperationException("validator failed"));
        var adoption = await rejectingAdoption.AdoptAsync(
            sessions,
            target,
            before!.Revision,
            "accepted-publication",
            boundary,
            "attempt",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameSharedBehaviorMutationStatus.ValidationRejected, adoption.Status);
        var after = await sessions.LoadAsync(Key(target), TestContext.Current.CancellationToken);
        Assert.Equal(before.Revision, after!.Revision);
        Assert.Empty((await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            sessions,
            Key(target),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions);
    }

    [Fact]
    public async Task WithdrawalStopsProjectionUntilAnExplicitReadopt()
    {
        var fixture = await PublishedFixtureAsync();
        var shared = Shared(fixture.Catalog, fixture.Boundary);
        var input = Input("target", "create");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input, shared);
        var session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            fixture.Sessions,
            input,
            session!.Revision,
            fixture.PublicationId,
            fixture.Boundary,
            "compatible",
            TestContext.Current.CancellationToken)).Changed);
        session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.WithdrawAsync(
            fixture.Sessions,
            Key(input),
            fixture.PublicationId,
            session!.Revision,
            "host-withdrawn",
            TestContext.Current.CancellationToken)).Changed);

        var withdrawn = new ScriptedProvider(Text("withdrawn"));
        await RunAsync(fixture.Sessions, withdrawn, Input("target", "withdrawn", 2), shared);
        Assert.DoesNotContain(
            "Use the verified safe route",
            VisibleText(Assert.Single(withdrawn.Requests)),
            StringComparison.Ordinal);

        session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            fixture.Sessions,
            Input("target", "readopt", 3),
            session!.Revision,
            fixture.PublicationId,
            fixture.Boundary,
            "explicit-readopt",
            TestContext.Current.CancellationToken)).Changed);
        var readopted = new ScriptedProvider(Text("readopted"));
        await RunAsync(fixture.Sessions, readopted, Input("target", "after-readopt", 4), shared);
        Assert.Contains(
            "Use the verified safe route",
            VisibleText(Assert.Single(readopted.Requests)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuspendedAdoptionCanBeWithdrawnAndDoesNotExhaustLifetimeCapacity()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        await SavePublicationAsync(catalog, "failing-publication", "failing-behavior", 1);
        await SavePublicationAsync(catalog, "replacement-publication", "replacement-behavior", 1);
        var shared = Shared(
            catalog,
            Boundary(),
            new SharedBehaviorCatalogOptions
            {
                MaximumAdoptionsPerActor = 1,
                ConsecutiveFailuresBeforeSuspension = 1,
            });
        var input = Input("target", "create");
        await RunAsync(sessions, new ScriptedProvider(Text("created")), input);
        var session = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        var adoption = await shared.AdoptAsync(
            sessions,
            Input("target", "adopt-failing"),
            session!.Revision,
            "failing-publication",
            Boundary(),
            "reviewed",
            TestContext.Current.CancellationToken);
        Assert.True(adoption.Changed);

        var evaluation = await shared.RecordEvaluationAsync(
            sessions,
            Key(input),
            "failing-publication",
            adoption.SessionRevision!.Value,
            false,
            "authoritative-failure",
            TestContext.Current.CancellationToken);
        Assert.True(evaluation.Changed);
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Suspended, evaluation.Adoption!.Status);

        var withdrawal = await shared.WithdrawAsync(
            sessions,
            Key(input),
            "failing-publication",
            evaluation.SessionRevision!.Value,
            "host-retired",
            TestContext.Current.CancellationToken);
        Assert.True(withdrawal.Changed);
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Withdrawn, withdrawal.Adoption!.Status);

        var replacement = await shared.AdoptAsync(
            sessions,
            Input("target", "adopt-replacement", 2),
            withdrawal.SessionRevision!.Value,
            "replacement-publication",
            Boundary(),
            "reviewed",
            TestContext.Current.CancellationToken);
        Assert.True(replacement.Changed);
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Active, replacement.Adoption!.Status);
    }

    [Fact]
    public async Task SameFamilyVersionReplacementDoesNotConsumeAnExtraAdoptionSlot()
    {
        var fixture = await PublishedFixtureAsync();
        await SavePublicationAsync(fixture.Catalog, "safe-route-v2", "safe-route", 2);
        var shared = Shared(
            fixture.Catalog,
            fixture.Boundary,
            new SharedBehaviorCatalogOptions { MaximumAdoptionsPerActor = 1 });
        var input = Input("target", "create");
        await RunAsync(fixture.Sessions, new ScriptedProvider(Text("created")), input);
        var session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            fixture.Sessions,
            input,
            session!.Revision,
            fixture.PublicationId,
            fixture.Boundary,
            "version-one",
            TestContext.Current.CancellationToken)).Changed);
        session = await fixture.Sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            fixture.Sessions,
            Input("target", "version-two", 2),
            session!.Revision,
            "safe-route-v2",
            fixture.Boundary,
            "version-two",
            TestContext.Current.CancellationToken)).Changed);

        var adoptions = (await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            fixture.Sessions,
            Key(input),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions;
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Superseded, adoptions.Single(value => value.PublicationId == fixture.PublicationId).Status);
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Active, adoptions.Single(value => value.PublicationId == "safe-route-v2").Status);
    }

    [Fact]
    public async Task CatalogFamilyVersionIsIndependentFromSourceLocalVersionAndSupportsRollback()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        await SavePublicationAsync(catalog, "route-family-v1", "local-route", 1, "route-family", 1);
        await SavePublicationAsync(catalog, "route-family-v2", "other-local-route", 1, "route-family", 2);
        await SavePublicationAsync(catalog, "unrelated-family-v1", "local-route", 1, "unrelated-family", 1);
        var shared = Shared(catalog, Boundary());
        var input = Input("target", "create");
        await RunAsync(sessions, new ScriptedProvider(Text("created")), input);

        foreach (var publicationId in new[] { "route-family-v2", "unrelated-family-v1", "route-family-v1" })
        {
            var current = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
            Assert.True((await shared.AdoptAsync(
                sessions,
                Input("target", "adopt-" + publicationId),
                current!.Revision,
                publicationId,
                Boundary(),
                "host-selected",
                TestContext.Current.CancellationToken)).Changed);
        }

        var adoptions = (await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            sessions,
            Key(input),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions;
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Superseded, adoptions.Single(value => value.PublicationId == "route-family-v2").Status);
        var rolledBack = adoptions.Single(value => value.PublicationId == "route-family-v1");
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Active, rolledBack.Status);
        Assert.Equal("route-family", rolledBack.BehaviorFamilyId);
        Assert.Equal(1, rolledBack.FamilyVersion);
        Assert.Equal("local-route", rolledBack.SourceBehaviorId);
        Assert.Equal(1, rolledBack.SourceBehaviorVersion);
        Assert.Equal(
            GameSharedBehaviorAdoptionStatus.Active,
            adoptions.Single(value => value.PublicationId == "unrelated-family-v1").Status);

        var provider = new ScriptedProvider(Text("done"));
        await RunAsync(sessions, provider, Input("target", "project", 2), shared);
        var prompt = VisibleText(Assert.Single(provider.Requests));
        Assert.Contains("shared.route-family.v1.", prompt, StringComparison.Ordinal);
        Assert.Contains("shared.unrelated-family.v1.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("shared.route-family.v2.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkillLimitCountsEligiblePublicationsAndDiscoveryPagesPastOtherWorlds()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        await SavePublicationAsync(
            catalog,
            "a-ineligible",
            "ineligible",
            1,
            "ineligible",
            1,
            toolName: "hidden-tool");
        await SavePublicationAsync(catalog, "z-eligible", "eligible", 1, "eligible", 1);
        var shared = Shared(catalog, Boundary());
        var input = Input("target", "create");
        await RunAsync(sessions, new ScriptedProvider(Text("created")), input);
        foreach (var publicationId in new[] { "a-ineligible", "z-eligible" })
        {
            var current = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
            Assert.True((await shared.AdoptAsync(
                sessions,
                Input("target", "adopt-" + publicationId),
                current!.Revision,
                publicationId,
                Boundary(),
                "host-selected",
                TestContext.Current.CancellationToken)).Changed);
        }

        var provider = new ScriptedProvider(Text("done"));
        var builder = new GameAgentBuilder(provider, "model")
            .UseSessionStore(sessions)
            .UseExtension(shared)
            .Configure(options => options.Limits = new GameRuntimeLimits { MaxSkillsPerRun = 1 });
        await using (var runtime = builder.Build())
        {
            Assert.True((await runtime.RunAsync(
                Input("target", "project", 2),
                TestContext.Current.CancellationToken)).Succeeded);
        }

        Assert.Contains("eligible", VisibleText(Assert.Single(provider.Requests)), StringComparison.OrdinalIgnoreCase);

        var worldAudience = new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.WorldGeneration, "save-1");
        var pagedCatalog = new InMemoryGameSharedBehaviorStore();
        for (var index = 0; index < 300; index++)
        {
            await SavePublicationAsync(
                pagedCatalog,
                "future-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                "future-" + index,
                1,
                "future-" + index,
                1,
                audience: worldAudience,
                generation: "future");
        }

        await SavePublicationAsync(
            pagedCatalog,
            "z-current",
            "current",
            1,
            "current",
            1,
            audience: worldAudience);
        var discovery = new SharedBehaviorCatalogExtension(
            pagedCatalog,
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(Boundary()),
            (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[] { worldAudience }),
            (_, _) => new ValueTask<bool>(true),
            (_, _) => new ValueTask<bool>(true),
            new SharedBehaviorCatalogOptions
            {
                MaximumDiscoverableBehaviors = 1,
                MaximumCatalogRecordsScannedPerDiscovery = 1_000,
            });
        Assert.Equal(
            "z-current",
            Assert.Single(await discovery.DiscoverAsync(Input("target", "discover"), TestContext.Current.CancellationToken)).PublicationId);
    }

    [Fact]
    public async Task DiscoveryScanLimitCountsRevokedRecordsInsteadOfSkippingPastThem()
    {
        var catalog = new InMemoryGameSharedBehaviorStore();
        foreach (var publicationId in new[] { "a-revoked", "b-revoked", "z-published" })
        {
            await SavePublicationAsync(catalog, publicationId, publicationId, 1);
        }

        foreach (var publicationId in new[] { "a-revoked", "b-revoked" })
        {
            var current = await catalog.LoadAsync(publicationId, TestContext.Current.CancellationToken);
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
                "test revocation");
            Assert.True((await catalog.SaveAsync(
                revoked,
                current.Revision,
                TestContext.Current.CancellationToken)).Saved);
        }

        var limited = Shared(
            catalog,
            Boundary(),
            new SharedBehaviorCatalogOptions
            {
                MaximumDiscoverableBehaviors = 1,
                MaximumCatalogRecordsScannedPerDiscovery = 2,
            });
        Assert.Empty(await limited.DiscoverAsync(
            Input("target", "limited-discovery"),
            TestContext.Current.CancellationToken));

        var complete = Shared(
            catalog,
            Boundary(),
            new SharedBehaviorCatalogOptions
            {
                MaximumDiscoverableBehaviors = 1,
                MaximumCatalogRecordsScannedPerDiscovery = 3,
            });
        Assert.Equal(
            "z-published",
            Assert.Single(await complete.DiscoverAsync(
                Input("target", "complete-discovery"),
                TestContext.Current.CancellationToken)).PublicationId);
    }

    [Fact]
    public async Task InactiveRetentionIsBoundedAndCannotBypassTheActiveLimit()
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        await SavePublicationAsync(catalog, "publication-one", "behavior-one", 1);
        await SavePublicationAsync(catalog, "publication-two", "behavior-two", 1);
        await SavePublicationAsync(catalog, "publication-three", "behavior-three", 1);
        var shared = Shared(
            catalog,
            Boundary(),
            new SharedBehaviorCatalogOptions
            {
                MaximumAdoptionsPerActor = 1,
                MaximumRetainedInactiveAdoptions = 1,
            });
        var input = Input("target", "create");
        await RunAsync(sessions, new ScriptedProvider(Text("created")), input);
        foreach (var publicationId in new[] { "publication-one", "publication-two" })
        {
            var session = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
            Assert.True((await shared.AdoptAsync(
                sessions,
                Input("target", "adopt-" + publicationId),
                session!.Revision,
                publicationId,
                Boundary(),
                "adopt",
                TestContext.Current.CancellationToken)).Changed);
            session = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
            Assert.True((await shared.WithdrawAsync(
                sessions,
                Key(input),
                publicationId,
                session!.Revision,
                "withdraw",
                TestContext.Current.CancellationToken)).Changed);
        }

        var afterRetention = (await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            sessions,
            Key(input),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions;
        Assert.DoesNotContain(afterRetention, value => value.PublicationId == "publication-one");
        Assert.Equal("publication-two", Assert.Single(afterRetention).PublicationId);

        var current = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        Assert.True((await shared.AdoptAsync(
            sessions,
            Input("target", "adopt-three"),
            current!.Revision,
            "publication-three",
            Boundary(),
            "adopt",
            TestContext.Current.CancellationToken)).Changed);
        current = await sessions.LoadAsync(Key(input), TestContext.Current.CancellationToken);
        var blocked = await shared.AdoptAsync(
            sessions,
            Input("target", "readopt-two"),
            current!.Revision,
            "publication-two",
            Boundary(),
            "readopt",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameSharedBehaviorMutationStatus.LimitExceeded, blocked.Status);
        var final = (await SharedBehaviorCatalogExtension.ReadAdoptionsAsync(
            sessions,
            Key(input),
            cancellationToken: TestContext.Current.CancellationToken)).Adoptions;
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Withdrawn, final.Single(value => value.PublicationId == "publication-two").Status);
        Assert.Equal(GameSharedBehaviorAdoptionStatus.Active, final.Single(value => value.PublicationId == "publication-three").Status);
    }

    private static GameSharedBehaviorAudience RoleAudience =>
        new(GameSharedBehaviorAudienceKind.Role, "settler");

    private static GameBehaviorWorldBoundary Boundary() => new("world", "save-1", 7);

    private static BehaviorLearningExtension Learning(GameBehaviorWorldBoundary boundary) => new(
        (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
        (_, _) => new ValueTask<bool>(true),
        inRunPolicy: _ => true);

    private static SharedBehaviorCatalogExtension Shared(
        IGameSharedBehaviorStore catalog,
        GameBehaviorWorldBoundary boundary,
        SharedBehaviorCatalogOptions? options = null) => new(
        catalog,
        (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
        (_, _) => new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[] { RoleAudience }),
        (_, _) => new ValueTask<bool>(true),
        (_, _) => new ValueTask<bool>(true),
        options);

    private static GameInput Input(string actor, string inputId, long tick = 1) =>
        new("session", actor, "request", "{}", new GameMoment("world", tick), inputId);

    private static GameSessionKey Key(GameInput input) => new(input.SessionId, input.ActorId);

    private static ModelResponse Proposal() => new(
        new AgentContent[]
        {
            new ToolCallContent(
                "proposal",
                "propose_behavior_learning",
                "{\"behaviorId\":\"safe-route\",\"title\":\"Safe route\",\"instructions\":\"Use the verified safe route.\",\"scope\":\"world_generation\",\"inputTypes\":[\"request\"],\"toolNames\":[],\"reflection\":{\"observation\":\"The primary path was unsafe.\",\"strategy\":\"Use the inspected alternate path.\",\"outcome\":\"The actor arrived safely.\",\"applicability\":\"Use in matching travel tasks.\"},\"evidence\":[{\"kind\":\"receipt\",\"reference\":\"travel-1\"}]}")
        },
        ModelStopReason.ToolUse);

    private static ModelResponse Text(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private static string VisibleText(ModelRequest request) => request.SystemPrompt + "\n" + string.Join(
        "\n",
        request.Messages.SelectMany(message => message.Content).OfType<TextContent>().Select(value => value.Text));

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

    private static async Task<PublishedFixture> PublishedFixtureAsync(GameSharedBehaviorAudience? audience = null)
    {
        var sessions = new InMemoryGameSessionStore();
        var catalog = new InMemoryGameSharedBehaviorStore();
        var boundary = Boundary();
        var learning = Learning(boundary);
        var shared = Shared(catalog, boundary);
        var source = Input("source", "learn");
        await RunAsync(sessions, new ScriptedProvider(Proposal(), Text("recorded")), source, learning);
        var sourceKey = Key(source);
        var sourceSession = await sessions.LoadAsync(sourceKey, TestContext.Current.CancellationToken);
        var behavior = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            sessions,
            sourceKey,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        Assert.True((await learning.ActivateAsync(
            sessions,
            sourceKey,
            behavior.BehaviorId,
            behavior.Version,
            sourceSession!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);
        sourceSession = await sessions.LoadAsync(sourceKey, TestContext.Current.CancellationToken);
        const string publicationId = "safe-route-v1";
        Assert.True((await shared.PublishAsync(
            sessions,
            sourceKey,
            behavior.BehaviorId,
            behavior.Version,
            "safe-route",
            1,
            sourceSession!.Revision,
            publicationId,
            audience ?? RoleAudience,
            boundary,
            "host-reviewed",
            TestContext.Current.CancellationToken)).Changed);
        return new PublishedFixture(sessions, catalog, boundary, publicationId);
    }

    private static async Task SavePublicationAsync(
        IGameSharedBehaviorStore catalog,
        string publicationId,
        string behaviorId,
        int behaviorVersion,
        string? behaviorFamilyId = null,
        int? familyVersion = null,
        string? toolName = null,
        GameSharedBehaviorAudience? audience = null,
        string generation = "save-1")
    {
        var definition = new GameSharedBehaviorDefinition(
            behaviorId,
            behaviorVersion,
            "Shared " + behaviorId,
            "Use the verified shared procedure for " + behaviorId + ".",
            new GameBehaviorReflection(
                "The source procedure committed.",
                "Reuse the verified procedure.",
                "The authoritative result succeeded.",
                "Use for matching requests."),
            steps: toolName is null
                ? null
                : new[] { new GameBehaviorStep("step", toolName, "Use the required tool.") },
            toolNames: toolName is null ? null : new[] { toolName });
        var publication = new GameSharedBehaviorPublication(
            publicationId,
            behaviorFamilyId ?? behaviorId,
            familyVersion ?? behaviorVersion,
            1,
            GameSharedBehaviorPublicationStatus.Published,
            audience ?? RoleAudience,
            definition,
            new GameSessionKey("source", "source"),
            Boundary().TimelineId,
            generation,
            Boundary().Revision,
            "test-publication");
        Assert.True((await catalog.SaveAsync(
            publication,
            0,
            TestContext.Current.CancellationToken)).Saved);
    }

    private sealed record PublishedFixture(
        InMemoryGameSessionStore Sessions,
        InMemoryGameSharedBehaviorStore Catalog,
        GameBehaviorWorldBoundary Boundary,
        string PublicationId);

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelResponse> _responses;
        private int _calls;

        public ScriptedProvider(params ModelResponse[] responses) => _responses = responses;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var index = Interlocked.Increment(ref _calls) - 1;
            yield return ModelStreamEvent.Terminal(index < _responses.Count ? _responses[index] : Text("done"));
            await Task.CompletedTask;
        }
    }

    private sealed class SubstitutingStore : IGameSharedBehaviorStore
    {
        private readonly IGameSharedBehaviorStore _inner;
        private readonly GameSharedBehaviorPublication _substitute;

        public SubstitutingStore(
            IGameSharedBehaviorStore inner,
            GameSharedBehaviorPublication substitute)
        {
            _inner = inner;
            _substitute = substitute;
        }

        public ValueTask<GameSharedBehaviorPublication?> LoadAsync(
            string publicationId,
            CancellationToken cancellationToken) =>
            new(string.Equals(publicationId, _substitute.PublicationId, StringComparison.Ordinal)
                ? _substitute
                : null);

        public ValueTask<GameSharedBehaviorStoreSaveResult> SaveAsync(
            GameSharedBehaviorPublication publication,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            _inner.SaveAsync(publication, expectedRevision, cancellationToken);

        public ValueTask<IReadOnlyList<GameSharedBehaviorPublication>> QueryAsync(
            GameSharedBehaviorStoreQuery query,
            CancellationToken cancellationToken) =>
            _inner.QueryAsync(query, cancellationToken);
    }
}
