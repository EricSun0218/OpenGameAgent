using System.Security.Cryptography;
using System.Text.Json;
using OpenGameAgent.Runtime.Protocol;
using Xunit;

namespace OpenGameAgent.Runtime.Protocol.Tests;

public sealed class RuntimeProtocolTests
{
    [Fact]
    public void EventRoundTripsWithoutLosingCoordinates()
    {
        var value = Event(1, GameRuntimeLifecycle.Started, "message_started");

        var roundTrip = GameRuntimeJson.Deserialize<GameRuntimeEventEnvelope>(
            GameRuntimeJson.Serialize(value));

        Assert.Equal(value.EventId, roundTrip.EventId);
        Assert.Equal(value.TurnId, roundTrip.TurnId);
        Assert.Equal(value.ItemId, roundTrip.ItemId);
        Assert.Equal(GameRuntimeItemKind.Message, roundTrip.ItemKind);
    }

    [Fact]
    public void ReducerRequiresContiguousLifecycleAndBuildsAuthoritativeItemSnapshot()
    {
        var reducer = new GameRuntimeReducer();
        reducer.Apply(Run(1, GameRuntimeLifecycle.Started, "run_started"));
        reducer.Apply(Event(2, GameRuntimeLifecycle.Started, "message_started"));
        reducer.Apply(Event(3, GameRuntimeLifecycle.Delta, "message_delta", "{\"delta\":\"hi\"}"));
        reducer.Apply(Event(4, GameRuntimeLifecycle.Completed, "message_completed", "{\"text\":\"hi\"}"));
        reducer.Apply(Run(5, GameRuntimeLifecycle.Completed, "run_completed", terminal: true));

        var snapshot = reducer.Snapshot;
        Assert.Equal(GameRuntimeRunStatus.Completed, snapshot.Status);
        Assert.False(snapshot.RequiresTranscriptReconciliation);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(GameRuntimeLifecycle.Completed, item.Lifecycle);
        Assert.Equal("{\"text\":\"hi\"}", item.PayloadJson);
    }

    [Fact]
    public void ReducerFailsClosedForGapOrInvalidLifecycle()
    {
        var reducer = new GameRuntimeReducer();
        reducer.Apply(Run(1, GameRuntimeLifecycle.Started, "run_started"));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(Event(
            2,
            GameRuntimeLifecycle.Completed,
            "message_completed")));

        var gapReducer = new GameRuntimeReducer();
        gapReducer.Apply(new GameRuntimeEventEnvelope(
            GameRuntimeProtocol.Version,
            "event-1",
            1,
            DateTimeOffset.UnixEpoch,
            "session",
            "actor",
            "input",
            GameRuntimeEventKind.Gap,
            GameRuntimeLifecycle.Completed,
            "events_gap",
            "{\"firstAvailableSequence\":5}",
            terminal: false));
        Assert.True(gapReducer.Snapshot.RequiresTranscriptReconciliation);
    }

    [Fact]
    public void ProtocolRejectsInconsistentCoordinatesAndOversizedPages()
    {
        Assert.Throws<ArgumentException>(() => new GameRuntimeEventEnvelope(
            GameRuntimeProtocol.Version,
            "event",
            1,
            DateTimeOffset.UnixEpoch,
            "session",
            "actor",
            "input",
            GameRuntimeEventKind.Item,
            GameRuntimeLifecycle.Started,
            "bad",
            "{}",
            runId: "run",
            turn: 1,
            turnId: null,
            itemId: "item",
            itemKind: GameRuntimeItemKind.Message));

        Assert.Throws<System.Text.Json.JsonException>(() =>
            GameRuntimeJson.Deserialize<GameRuntimeStartRequest>(
                "{\"requestId\":\"one\",\"requestId\":\"two\",\"inputJson\":\"{}\"}"));

        Assert.Throws<System.Text.Json.JsonException>(() =>
            GameRuntimeJson.Deserialize<GameRuntimeStartRequest>(
                "{\"requestId\":\"one\",\"inputJson\":\"{}\",\"unexpected\":true}"));
    }

    [Fact]
    public void SequenceRangeRemainsExactlyRepresentableAcrossSupportedLanguages()
    {
        Assert.Equal(9_007_199_254_740_991, GameRuntimeProtocol.MaximumSequence);
        Assert.Throws<ArgumentOutOfRangeException>(() => Event(
            GameRuntimeProtocol.MaximumSequence + 1,
            GameRuntimeLifecycle.Started,
            "message_started"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameRuntimeReadEventsRequest(
            "session",
            "actor",
            GameRuntimeProtocol.MaximumSequence + 1));
    }

    [Fact]
    public void PublishedSchemaCppSdkAndFixtureStayOnProtocolVersionOne()
    {
        var root = FindRepositoryRoot();
        var schemaPath = Path.Combine(root, "protocol", "runtime", "v1", "runtime.schema.json");
        using (var schema = JsonDocument.Parse(File.ReadAllText(schemaPath)))
        {
            Assert.Equal(
                1,
                schema.RootElement
                    .GetProperty("$defs")
                    .GetProperty("eventEnvelope")
                    .GetProperty("properties")
                    .GetProperty("protocolVersion")
                    .GetProperty("const")
                    .GetInt32());
            Assert.False(
                schema.RootElement
                    .GetProperty("$defs")
                    .GetProperty("eventEnvelope")
                    .GetProperty("additionalProperties")
                    .GetBoolean());
        }

        var cpp = File.ReadAllText(Path.Combine(
            root,
            "protocol",
            "runtime",
            "v1",
            "cpp",
            "OpenGameAgentRuntimeProtocol.hpp"));
        Assert.Contains("protocol_version = 1", cpp, StringComparison.Ordinal);
        Assert.Contains("maximum_sequence = 9007199254740991LL", cpp, StringComparison.Ordinal);

        var schemaHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant();
        foreach (var sdkPath in new[]
                 {
                     Path.Combine(root, "protocol", "runtime", "v1", "typescript", "src", "index.ts"),
                     Path.Combine(root, "protocol", "runtime", "v1", "python", "opengameagent_runtime_protocol", "__init__.py"),
                 })
        {
            Assert.Contains(schemaHash, File.ReadAllText(sdkPath), StringComparison.Ordinal);
        }

        var reducer = new GameRuntimeReducer();
        var fixturePath = Path.Combine(root, "protocol", "runtime", "v1", "fixtures", "canonical-run.jsonl");
        foreach (var line in File.ReadLines(fixturePath))
        {
            reducer.Apply(GameRuntimeJson.Deserialize<GameRuntimeEventEnvelope>(line));
        }

        Assert.Equal(GameRuntimeRunStatus.Completed, reducer.Snapshot.Status);
        Assert.Equal(7, reducer.Snapshot.LastSequence);
        Assert.False(reducer.Snapshot.RequiresTranscriptReconciliation);
    }

    [Fact]
    public void ProjectedPageCursorCanAdvanceBeyondItsLastVisibleEvent()
    {
        var visible = Event(2, GameRuntimeLifecycle.Started, "message_started");
        var page = new GameRuntimeEventPage(
            "session",
            "actor",
            requestedAfterSequence: 1,
            firstRetainedSequence: 1,
            lastSequence: 4,
            nextAfterSequence: 4,
            gap: false,
            new[] { visible });

        var roundTrip = GameRuntimeJson.Deserialize<GameRuntimeEventPage>(GameRuntimeJson.Serialize(page));
        Assert.Equal(4, roundTrip.NextAfterSequence);
        Assert.Equal(2, Assert.Single(roundTrip.Events).Sequence);
    }

    private static GameRuntimeEventEnvelope Event(
        long sequence,
        GameRuntimeLifecycle lifecycle,
        string name,
        string payload = "{}") => new(
        GameRuntimeProtocol.Version,
        $"event-{sequence}",
        sequence,
        DateTimeOffset.UnixEpoch,
        "session",
        "actor",
        "input",
        GameRuntimeEventKind.Item,
        lifecycle,
        name,
        payload,
        "run",
        1,
        "turn-1",
        "item-1",
        GameRuntimeItemKind.Message);

    private static GameRuntimeEventEnvelope Run(
        long sequence,
        GameRuntimeLifecycle lifecycle,
        string name,
        bool terminal = false) => new(
        GameRuntimeProtocol.Version,
        $"event-{sequence}",
        sequence,
        DateTimeOffset.UnixEpoch,
        "session",
        "actor",
        "input",
        GameRuntimeEventKind.Run,
        lifecycle,
        name,
        "{}",
        "run",
        terminal: terminal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenGameAgent.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
