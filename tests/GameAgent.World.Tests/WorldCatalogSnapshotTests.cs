using System.Text.Json;

namespace GameAgent.World.Tests;

public sealed class WorldCatalogSnapshotTests
{
    [Fact]
    public void CompositeCatalogBindsEventsAndInteractionsToOneCoordinate()
    {
        var original = Snapshot(
            eventVersion: "1",
            interactionRevision: "content-1");
        var fence = original.CreateFence(
            "world",
            "timeline",
            7,
            11,
            "state-11");

        Assert.Equal(original.Digest, original.Events.Digest);
        Assert.Equal(original.Digest, original.Interactions.Digest);
        Assert.Equal(original.Digest, fence.CatalogDigest);
        Assert.Equal(
            original.Events.ComponentDigest,
            fence.EventCatalogDigest);
        Assert.Equal(
            original.Interactions.ComponentDigest,
            fence.InteractionCatalogDigest);

        var changedEvent = Snapshot(
            eventVersion: "2",
            interactionRevision: "content-1");
        Assert.NotEqual(original.Digest, changedEvent.Digest);
        Assert.NotEqual(
            original.Events.ComponentDigest,
            changedEvent.Events.ComponentDigest);
        Assert.Equal(
            original.Interactions.ComponentDigest,
            changedEvent.Interactions.ComponentDigest);

        var changedInteraction = Snapshot(
            eventVersion: "1",
            interactionRevision: "content-2");
        Assert.NotEqual(original.Digest, changedInteraction.Digest);
        Assert.Equal(
            original.Events.ComponentDigest,
            changedInteraction.Events.ComponentDigest);
        Assert.NotEqual(
            original.Interactions.ComponentDigest,
            changedInteraction.Interactions.ComponentDigest);
    }

    private static WorldCatalogSnapshot Snapshot(
        string eventVersion,
        string interactionRevision)
    {
        return new WorldCatalogSnapshot(
            "world.catalog",
            3,
            new[]
            {
                new WorldEventDefinition(
                    "event.month",
                    eventVersion,
                    "month",
                    10,
                    "condition",
                    "selector",
                    "resolver",
                    "effect")
            },
            new[]
            {
                new InteractionDefinition(
                    "interaction.talk",
                    "1",
                    "input",
                    10,
                    "availability",
                    "cost",
                    "selector",
                    "resolver",
                    "effect",
                    details: new InteractionDefinitionDetails(
                        interactionRevision,
                        new InteractionParameterContract(
                            "input",
                            "1",
                            Json(
                                """
                                {
                                  "type": "object",
                                  "properties": {},
                                  "additionalProperties": false
                                }
                                """))))
            });
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
