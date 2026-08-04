using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class GameSemanticsTests
{
    [Fact]
    public void CanonicalJsonDigestIgnoresObjectPropertyOrder()
    {
        var first = ProtocolJson.ParseElement(
            """{"b":[true,"x"],"a":1}""");
        var reordered = ProtocolJson.ParseElement(
            """{"a":1,"b":[true,"x"]}""");
        var reorderedArray = ProtocolJson.ParseElement(
            """{"a":1,"b":["x",true]}""");

        var digest = CanonicalJsonDigest.ComputeSha256(first);

        Assert.True(CanonicalJsonDigest.IsSha256(digest));
        Assert.Equal(
            digest,
            CanonicalJsonDigest.ComputeSha256(reordered));
        Assert.NotEqual(
            digest,
            CanonicalJsonDigest.ComputeSha256(reorderedArray));
        Assert.Throws<RuntimeContentLimitException>(
            () => CanonicalJsonDigest.ComputeSha256(default));
    }

    [Fact]
    public void SemanticExpectationComputesAndValidatesCanonicalDigest()
    {
        var value = ProtocolJson.ParseElement("""{"state":12}""");

        var expectation = DurableRunSemanticExpectation.FromJson(
            "game.coordinate",
            value);

        Assert.Equal("game.coordinate", expectation.ExtensionName);
        Assert.Equal(
            CanonicalJsonDigest.ComputeSha256(value),
            expectation.ExpectedSha256);
        Assert.Throws<ArgumentException>(
            () => new DurableRunSemanticExpectation(
                "game.coordinate",
                new string('A', 64)));
    }

    [Fact]
    public void CanonicalJsonDigestRejectsUntrustedValuesOutsideBounds()
    {
        var oversized = ProtocolJson.ParseElement(
            "\"" + new string(
                'x',
                CanonicalJsonDigest.MaximumStringUtf8Bytes + 1) + "\"");
        var duplicate = ProtocolJson.ParseElement(
            """{"state":1,"state":2}""");
        var tooDeep = ProtocolJson.ParseElement(
            new string('[', CanonicalJsonDigest.MaximumDepth)
            + "0"
            + new string(']', CanonicalJsonDigest.MaximumDepth));

        var oversizedError = Assert.Throws<RuntimeContentLimitException>(
            () => CanonicalJsonDigest.ComputeSha256(oversized));
        var duplicateError = Assert.Throws<RuntimeContentLimitException>(
            () => CanonicalJsonDigest.ComputeSha256(duplicate));
        var depthError = Assert.Throws<RuntimeContentLimitException>(
            () => CanonicalJsonDigest.ComputeSha256(tooDeep));

        Assert.Equal(
            "json_string_bytes_exceeded",
            oversizedError.LimitCode);
        Assert.Equal(
            "json_duplicate_property",
            duplicateError.LimitCode);
        Assert.Equal("json_depth_exceeded", depthError.LimitCode);
    }

    [Fact]
    public void GameTimeOnlyComparesWithinOneClockTimelineAndEpoch()
    {
        var morning = Time("main", "prime", epoch: 2, tick: 100);
        var noon = Time("main", "prime", epoch: 2, tick: 200);

        Assert.True(morning.CompareTo(noon) < 0);
        Assert.Throws<InvalidOperationException>(
            () => morning.CompareTo(Time("dream", "prime", 2, 100)));
        Assert.Throws<InvalidOperationException>(
            () => morning.CompareTo(Time("main", "branch", 2, 100)));
        Assert.Throws<InvalidOperationException>(
            () => morning.CompareTo(Time("main", "prime", 3, 100)));
    }

    [Fact]
    public void GameTimeWindowUsesExclusiveUpperBound()
    {
        var window = new GameTimeWindow(
            Time("main", "prime", 1, 10),
            Time("main", "prime", 1, 20));

        Assert.True(window.Contains(Time("main", "prime", 1, 10)));
        Assert.True(window.Contains(Time("main", "prime", 1, 19)));
        Assert.False(window.Contains(Time("main", "prime", 1, 20)));
        Assert.False(window.Contains(Time("main", "prime", 2, 15)));
    }

    [Fact]
    public void ContextEnvelopeRoundTripsStructuredGameCoordinates()
    {
        var coordinate = new GameContextCoordinate(
            "world",
            "prime",
            saveRevision: 8,
            new GameEntityIdentity("npc", incarnation: 3),
            sceneId: "market",
            regionId: "north",
            stateVersion: "world-v42",
            gameTime: Time("calendar", "prime", 4, 90),
            causality: new GameCausalityStamp(
                "event-9",
                "world-v42",
                new[] { "event-7", "event-8" }));
        var run = ValidRun("world");

        GameContextEnvelope.Attach(run, coordinate);

        Assert.True(GameContextEnvelope.TryRead(run, out var restored));
        Assert.Equal("world", restored!.WorldId);
        Assert.Equal("prime", restored.TimelineId);
        Assert.Equal(8, restored.SaveRevision);
        Assert.True(
            coordinate.Observer!.IsSameIncarnation(restored.Observer));
        Assert.Equal("market", restored.SceneId);
        Assert.Equal(90, restored.GameTime!.Tick);
        Assert.Equal("event-9", restored.Causality!.EventId);
        Assert.Equal(
            new[] { "event-7", "event-8" },
            restored.Causality.ParentEventIds);
    }

    [Fact]
    public void ContextEnvelopePreservesPortableLongsBeyondSafeIntegerRange()
    {
        const long firstUnsafeInteger = 9_007_199_254_740_992;
        const long adjacentInteger = firstUnsafeInteger + 1;
        var first = new GameContextCoordinate(
            "world",
            "timeline",
            firstUnsafeInteger,
            new GameEntityIdentity("observer", firstUnsafeInteger),
            stateVersion: "state",
            gameTime: new GameTimePoint(
                "calendar",
                "timeline",
                firstUnsafeInteger,
                firstUnsafeInteger));
        var adjacent = new GameContextCoordinate(
            "world",
            "timeline",
            adjacentInteger,
            new GameEntityIdentity("observer", adjacentInteger),
            stateVersion: "state",
            gameTime: new GameTimePoint(
                "calendar",
                "timeline",
                adjacentInteger,
                adjacentInteger));

        var json = GameContextEnvelope.ToJson(first);
        Assert.Equal(
            JsonValueKind.String,
            json.GetProperty("saveRevision").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            json.GetProperty("observer")
                .GetProperty("incarnation")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            json.GetProperty("gameTime").GetProperty("epoch").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            json.GetProperty("gameTime").GetProperty("tick").ValueKind);
        Assert.True(GameContextEnvelope.TryRead(json, out var restored));
        Assert.Equal(firstUnsafeInteger, restored!.SaveRevision);
        Assert.Equal(
            firstUnsafeInteger,
            restored.Observer!.Incarnation);
        Assert.Equal(firstUnsafeInteger, restored.GameTime!.Epoch);
        Assert.Equal(firstUnsafeInteger, restored.GameTime.Tick);
        Assert.NotEqual(
            CanonicalJsonDigest.ComputeSha256(json),
            CanonicalJsonDigest.ComputeSha256(
                GameContextEnvelope.ToJson(adjacent)));

        var malformed = ProtocolJson.ParseElement(
            $$"""
            {
              "worldId": "world",
              "timelineId": "timeline",
              "saveRevision": {{firstUnsafeInteger}}
            }
            """);
        Assert.False(
            GameContextEnvelope.TryRead(malformed, out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void ContextEnvelopeAttachmentIsCapacityCheckedAtomically()
    {
        var run = ValidRun("world");
        for (var index = 0;
             index < ProtocolLimits.MaxProtocolExtensions;
             index++)
        {
            run.Extensions["extension-" + index] =
                ProtocolJson.ParseElement("true");
        }

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => GameContextEnvelope.Attach(
                run,
                new GameContextCoordinate("world", "prime", 1)));

        Assert.Equal("run_extensions_exceeded", error.LimitCode);
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            run.Extensions.Count);
        Assert.False(
            run.Extensions.ContainsKey(GameContextEnvelope.ExtensionName));
    }

    [Fact]
    public void ContextEnvelopeRejectsInvalidRunBeforeMutation()
    {
        var run = ValidRun("world");
        run.Extensions["invalid"] = ProtocolJson.ParseElement(
            new string('[', ProtocolLimits.MaxProtocolJsonDepth)
            + "0"
            + new string(']', ProtocolLimits.MaxProtocolJsonDepth));

        Assert.Throws<JsonException>(
            () => GameContextEnvelope.Attach(
                run,
                new GameContextCoordinate("world", "prime", 1)));

        Assert.False(
            run.Extensions.ContainsKey(GameContextEnvelope.ExtensionName));
    }

    [Fact]
    public void ContextEnvelopeRejectsMalformedKnownFields()
    {
        var malformed = ProtocolJson.ParseElement(
            """
            {
              "worldId": "world",
              "timelineId": "prime",
              "saveRevision": 1,
              "stateVersion": 42
            }
            """);

        Assert.False(
            GameContextEnvelope.TryRead(
                malformed,
                out var coordinate));
        Assert.Null(coordinate);
    }

    [Fact]
    public async Task MemoryGameValidityIsIndependentFromWallClockExpiry()
    {
        var store = new DeterministicMemoryStore();
        var wallNow = new DateTimeOffset(
            2040,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        await store.UpsertAsync(
            Record(
                "festival",
                new GameTimeWindow(
                    Time("calendar", "prime", 1, 50),
                    Time("calendar", "prime", 1, 60)),
                expiresAt: wallNow.AddDays(1),
                provenance: new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime")),
            CancellationToken.None);

        var during = await store.SearchAsync(
            Query(Time("calendar", "prime", 1, 55), wallNow),
            CancellationToken.None);
        var afterGameTime = await store.SearchAsync(
            Query(Time("calendar", "prime", 1, 61), wallNow),
            CancellationToken.None);
        var afterWallTime = await store.SearchAsync(
            Query(Time("calendar", "prime", 1, 55), wallNow.AddDays(2)),
            CancellationToken.None);

        Assert.Single(during);
        Assert.Empty(afterGameTime);
        Assert.Empty(afterWallTime);
    }

    [Fact]
    public async Task GameTimeWindowSuppliesTimelineWhenProvenanceOmitsIt()
    {
        var store = new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record(
                "window-owned-timeline",
                new GameTimeWindow(
                    Time("calendar", "prime", 1, 50),
                    Time("calendar", "prime", 1, 60)),
                provenance: new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true)),
            CancellationToken.None);

        var prime = await store.SearchAsync(
            Query(
                Time("calendar", "prime", 1, 55),
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        var branch = await store.SearchAsync(
            Query(
                Time("calendar", "branch", 1, 55),
                DateTimeOffset.UnixEpoch,
                timelineId: "branch"),
            CancellationToken.None);

        Assert.Single(prime);
        Assert.Empty(branch);
    }

    private static AgentRun ValidRun(string worldId)
    {
        return new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = worldId
        };
    }

    [Fact]
    public async Task TimelineAndEntityIncarnationPreventKnowledgeLeakage()
    {
        var store = new DeterministicMemoryStore();
        var originalNpc = new GameEntityIdentity("npc", 1);
        await store.UpsertAsync(
            Record(
                "shared",
                gameTimeWindow: null,
                provenance: new MemoryProvenance(
                    "world",
                    "session",
                    4,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime")),
            CancellationToken.None);
        await store.UpsertAsync(
            Record(
                "secret",
                gameTimeWindow: null,
                provenance: new MemoryProvenance(
                    "world",
                    "session",
                    4,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime",
                    perspective: new GameKnowledgePerspective(
                        originalNpc,
                        "witnessed"))),
            CancellationToken.None);

        var original = await store.SearchAsync(
            Query(
                gameTime: null,
                DateTimeOffset.UnixEpoch,
                timelineId: "prime",
                observer: originalNpc),
            CancellationToken.None);
        var branch = await store.SearchAsync(
            Query(
                gameTime: null,
                DateTimeOffset.UnixEpoch,
                timelineId: "branch",
                observer: originalNpc),
            CancellationToken.None);
        var reusedId = await store.SearchAsync(
            Query(
                gameTime: null,
                DateTimeOffset.UnixEpoch,
                timelineId: "prime",
                observer: new GameEntityIdentity("npc", 2)),
            CancellationToken.None);
        var noObserver = await store.SearchAsync(
            Query(
                gameTime: null,
                DateTimeOffset.UnixEpoch,
                timelineId: "prime"),
            CancellationToken.None);
        var privileged = await store.SearchAsync(
            Query(
                gameTime: null,
                DateTimeOffset.UnixEpoch,
                timelineId: "prime",
                includeAllPerspectives: true),
            CancellationToken.None);

        Assert.Equal(
            new[] { "secret", "shared" },
            original.Select(item => item.Record.MemoryId));
        Assert.Empty(branch);
        Assert.Equal(
            new[] { "shared" },
            reusedId.Select(item => item.Record.MemoryId));
        Assert.Equal(
            new[] { "shared" },
            noObserver.Select(item => item.Record.MemoryId));
        Assert.Equal(
            new[] { "secret", "shared" },
            privileged.Select(item => item.Record.MemoryId));
    }

    private static GameTimePoint Time(
        string clockId,
        string timelineId,
        long epoch,
        long tick)
    {
        return new GameTimePoint(clockId, timelineId, epoch, tick);
    }

    private static MemoryRecord Record(
        string id,
        GameTimeWindow? gameTimeWindow,
        DateTimeOffset? expiresAt = null,
        MemoryProvenance? provenance = null)
    {
        return new MemoryRecord(
            id,
            "npc",
            ProtocolJson.ParseElement("""{"text":"festival secret"}"""),
            tags: null,
            importance: 50,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            expiresAt,
            provenance,
            gameTimeWindow);
    }

    private static MemoryQuery Query(
        GameTimePoint? gameTime,
        DateTimeOffset wallNow,
        string? timelineId = "prime",
        GameEntityIdentity? observer = null,
        bool includeAllPerspectives = false)
    {
        return new MemoryQuery(
            "npc",
            ProtocolJson.ParseElement("""{"text":"festival secret"}"""),
            now: wallNow,
            worldId: "world",
            requireCommittedProvenance: false,
            timelineId: timelineId,
            observer: observer,
            gameTime: gameTime,
            includeAllPerspectives: includeAllPerspectives);
    }
}
