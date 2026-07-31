using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldAggregateCapacityTests
{
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AtomicMutationSetRejectsAggregateWhileWriting()
    {
        var payload = new string('x', 15_000);
        var intents = Enumerable.Range(0, 20)
            .Select(
                index => (IWorldMutationIntent)
                    new WorldValueMutationIntent(
                        "intent." + index,
                        new GameEntityIdentity("entity." + index, 1),
                        "value",
                        "resource." + index,
                        WorldValueMutationKind.Set,
                        Json(
                            "{\"a\":\""
                            + payload
                            + "\",\"b\":\""
                            + payload
                            + "\",\"c\":\""
                            + payload
                            + "\",\"d\":\""
                            + payload
                            + "\"}")))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(
            () => new WorldAtomicMutationSet(
                "command",
                "operation",
                "world",
                "timeline",
                1,
                1,
                "state",
                Digest,
                intents));

        Assert.Equal("intents", exception.ParamName);
        Assert.Contains(
            "byte limit",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LargeInteractionDefinitionHasStableCanonicalDigest()
    {
        var ordered = Definition(reorderedParameters: false);
        var reordered = Definition(reorderedParameters: true);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            InteractionCanonicalJson.WriteDefinition(writer, ordered);
        }

        Assert.True(output.Length > 262_144);
        Assert.True(CanonicalJsonDigest.IsSha256(ordered.ContentDigest));
        Assert.Equal(ordered.ContentDigest, reordered.ContentDigest);
    }

    [Fact]
    public void SmallInteractionDigestPreservesCanonicalCompatibility()
    {
        var definition = Definition(
            reorderedParameters: false,
            stepCount: 1,
            valueLength: 8);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            InteractionCanonicalJson.WriteDefinition(writer, definition);
        }

        using var document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(
            CanonicalJsonDigest.ComputeSha256(document.RootElement),
            definition.ContentDigest);
    }

    [Fact]
    public async Task AdvanceClockRejectsTheFirstExcessUniqueResource()
    {
        var package = PackageWithTickResources(512);
        var snapshot = package.CreateInitialSnapshot();
        var store = new InMemoryWorldAuthoritativeTransactionStore(snapshot);
        var runner = new WorldAdvanceClockRunner(package, store);
        var command = new WorldAdvanceClockCommand(
            "command",
            "operation",
            snapshot.Coordinate,
            "turn",
            expectedClockTick: 0,
            ticks: 1);

        var exception = await Assert.ThrowsAsync<
            WorldEvolutionLimitException>(
            async () => await runner.ExecuteAsync(command));

        Assert.Equal(
            WorldEvolutionReasonCodes.ResourceLimitExceeded,
            exception.ReasonCode);
        Assert.Equal(
            0,
            (await store.ReadAsync(snapshot.Coordinate.Address, default))!
                .Coordinate.SaveRevision);
    }

    private static InteractionDefinition Definition(
        bool reorderedParameters,
        int stepCount = 10,
        int valueLength = 7_500)
    {
        var value = new string('x', valueLength);
        var steps = Enumerable.Range(0, stepCount)
            .Select(
                index => new InteractionStepDefinition(
                    "step." + index,
                    "effect",
                    Json(
                        reorderedParameters
                            ? "{\"d\":\""
                              + value
                              + "\",\"c\":\""
                              + value
                              + "\",\"b\":\""
                              + value
                              + "\",\"a\":\""
                              + value
                              + "\"}"
                            : "{\"a\":\""
                              + value
                              + "\",\"b\":\""
                              + value
                              + "\",\"c\":\""
                              + value
                              + "\",\"d\":\""
                              + value
                              + "\"}")))
            .ToArray();
        var parameterContract = new InteractionParameterContract(
            "input",
            "1",
            Json(
                """
                {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
                """));
        var details = new InteractionDefinitionDetails(
            "1",
            parameterContract,
            steps: steps);
        return new InteractionDefinition(
            "interaction",
            "1",
            "input",
            0,
            "availability",
            "cost",
            "selector",
            "resolver",
            "effect",
            details: details);
    }

    private static ActivatedWorldPackage PackageWithTickResources(int count)
    {
        var source = new WorldPackageDefinition(
            "package",
            "1",
            new[]
            {
                new WorldPackageFile(
                    "world.json",
                    "application/json",
                    Encoding.UTF8.GetBytes("{}"))
            });
        var state = Json("""{"clocks":{"turn":"0"}}""");
        var world = new NativeWorldDefinition(
            "world",
            "timeline",
            "/entities",
            "/relationships",
            state,
            new Dictionary<string, long>(),
            Digest);
        var events = Enumerable.Range(0, count)
            .Select(
                index => new NativeWorldEventDefinition(
                    "event." + index,
                    "1",
                    0,
                    new NativeWorldClockEventTrigger("turn", 1, 0),
                    new NativeWorldSingletonSelector(),
                    new NativeWorldAlwaysCondition(),
                    Array.Empty<NativeWorldEffect>(),
                    Array.Empty<string>(),
                    new[] { "resource." + index },
                    Digest))
            .ToArray();
        var emptyAgents = new NativeWorldContentCatalog(
            "agents",
            Array.Empty<NativeWorldContentEntry>(),
            Digest);
        var emptyKnowledge = new NativeWorldContentCatalog(
            "knowledge",
            Array.Empty<NativeWorldContentEntry>(),
            Digest);
        return new ActivatedWorldPackage(
            source,
            world,
            new[] { new NativeWorldClockDefinition("turn", "/clocks/turn", 0) },
            Array.Empty<WorldNumericSchema>(),
            events,
            Array.Empty<NativeWorldInteractionDefinition>(),
            emptyAgents,
            emptyKnowledge,
            Digest,
            Digest,
            Digest,
            Digest,
            Digest);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
