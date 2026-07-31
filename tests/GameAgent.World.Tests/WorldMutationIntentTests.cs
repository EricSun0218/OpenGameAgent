using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldMutationIntentTests
{
    [Fact]
    public void AtomicSetCarriesExactCoordinateAndUnionOfResources()
    {
        var first = new WorldValueMutationIntent(
            "set-a",
            new GameEntityIdentity("a", 1),
            "component/a",
            "entity:a",
            WorldValueMutationKind.Set,
            Json("""{"value":"10"}"""));
        var second = new WorldValueMutationIntent(
            "set-b",
            new GameEntityIdentity("b", 2),
            "component/b",
            "entity:b",
            WorldValueMutationKind.Set,
            Json("""{"value":"20"}"""));

        var batch = Batch(first, second);

        Assert.Equal("world", batch.WorldId);
        Assert.Equal("timeline", batch.TimelineId);
        Assert.Equal(3, batch.TimelineEpoch);
        Assert.Equal(4, batch.ExpectedSaveRevision);
        Assert.Equal("state-4", batch.ExpectedStateVersion);
        Assert.Equal(new[] { "entity:a", "entity:b" }, batch.WriteResourceKeys);
        Assert.Equal(2, batch.Intents.Count);
        Assert.True(CanonicalJsonDigest.IsSha256(batch.Digest));
    }

    [Fact]
    public void AtomicDigestIgnoresObjectPropertyOrderButPreservesIntentOrder()
    {
        var orderedValue = new WorldValueMutationIntent(
            "set",
            new GameEntityIdentity("a", 1),
            "component",
            "entity:a",
            WorldValueMutationKind.Set,
            Json("""{"a":"1","b":"2"}"""));
        var reorderedValue = new WorldValueMutationIntent(
            "set",
            new GameEntityIdentity("a", 1),
            "component",
            "entity:a",
            WorldValueMutationKind.Set,
            Json("""{"b":"2","a":"1"}"""));
        var other = new WorldNumericMutationIntent(
            "numeric",
            new GameEntityIdentity("b", 1),
            "component/value",
            "entity:b",
            "numeric",
            WorldNumericMutationKind.Add,
            new WorldFixedPointValue(10, 2));

        Assert.Equal(
            Batch(orderedValue, other).Digest,
            Batch(reorderedValue, other).Digest);
        Assert.NotEqual(
            Batch(orderedValue, other).Digest,
            Batch(other, orderedValue).Digest);
    }

    [Fact]
    public void TransferExplicitlyCarriesBothSidesInOneIntent()
    {
        var transfer = new WorldTransferMutationIntent(
            "transfer",
            new GameEntityIdentity("source", 4),
            "resources/value",
            "entity:source:value",
            new GameEntityIdentity("target", 9),
            "resources/value",
            "entity:target:value",
            "numeric.value",
            new WorldFixedPointValue(125, 2));

        var json = transfer.ToPortableJson();

        Assert.Equal(2, transfer.WriteResourceKeys.Count);
        Assert.Equal(
            "source",
            json.GetProperty("source").GetProperty("entityId").GetString());
        Assert.Equal(
            "target",
            json.GetProperty("target").GetProperty("entityId").GetString());
        Assert.Equal("125", json.GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("amount").ValueKind);
    }

    [Fact]
    public void RelationshipDirectionIsExplicitAndNeverMirrored()
    {
        var forward = new WorldRelationshipMutationIntent(
            "relation",
            new GameEntityIdentity("a", 1),
            new GameEntityIdentity("b", 1),
            "game.edge",
            "relationship:a:b",
            WorldRelationshipMutationKind.Upsert,
            Json("""{"state":"declared"}"""));
        var reverse = new WorldRelationshipMutationIntent(
            "relation",
            new GameEntityIdentity("b", 1),
            new GameEntityIdentity("a", 1),
            "game.edge",
            "relationship:b:a",
            WorldRelationshipMutationKind.Upsert,
            Json("""{"state":"declared"}"""));

        Assert.NotEqual(
            CanonicalJsonDigest.ComputeSha256(forward.ToPortableJson()),
            CanonicalJsonDigest.ComputeSha256(reverse.ToPortableJson()));
        Assert.Equal(
            "a",
            forward.Source.EntityId);
        Assert.Equal(
            "b",
            forward.Target.EntityId);
    }

    [Fact]
    public void GenericAuthoritativeValuesRejectEveryJsonNumber()
    {
        var exception = Assert.Throws<WorldMutationValidationException>(
            () => new WorldValueMutationIntent(
                "set",
                new GameEntityIdentity("a", 1),
                "component",
                "entity:a",
                WorldValueMutationKind.Set,
                Json("""{"nested":[{"value":1.25}]}""")));

        Assert.Equal(
            WorldNumericReasonCodes.BinaryFloatForbidden,
            exception.ReasonCode);
    }

    [Fact]
    public void AtomicSetRejectsDuplicateIntentIds()
    {
        var first = new WorldValueMutationIntent(
            "same",
            new GameEntityIdentity("a", 1),
            "a",
            "entity:a",
            WorldValueMutationKind.Remove);
        var second = new WorldValueMutationIntent(
            "same",
            new GameEntityIdentity("b", 1),
            "b",
            "entity:b",
            WorldValueMutationKind.Remove);

        Assert.Throws<ArgumentException>(() => Batch(first, second));
    }

    [Fact]
    public void AtomicDigestSupportsMaximumSmallIntentCount()
    {
        var intents = Enumerable.Range(0, 512)
            .Select(
                index => (IWorldMutationIntent)
                    new WorldNumericMutationIntent(
                        "intent."
                        + index.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        new GameEntityIdentity(
                            "entity."
                            + index.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            1),
                        "/value",
                        "resource."
                        + index.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        "numeric",
                        WorldNumericMutationKind.Add,
                        new WorldFixedPointValue(1, 0)))
            .ToArray();

        var batch = Batch(intents);

        Assert.Equal(512, batch.Intents.Count);
        Assert.True(CanonicalJsonDigest.IsSha256(batch.Digest));
    }

    [Fact]
    public void PortableMutationLongsRemainExactBeyondJavaScriptSafeRange()
    {
        const long firstUnsafeInteger = 9_007_199_254_740_992;
        const long adjacentInteger = firstUnsafeInteger + 1;
        var firstIntent = new WorldValueMutationIntent(
            "set",
            new GameEntityIdentity("actor", firstUnsafeInteger),
            "component",
            "entity:actor",
            WorldValueMutationKind.Set,
            Json("\"value\""));
        var adjacentIntent = new WorldValueMutationIntent(
            "set",
            new GameEntityIdentity("actor", adjacentInteger),
            "component",
            "entity:actor",
            WorldValueMutationKind.Set,
            Json("\"value\""));
        var first = new WorldAtomicMutationSet(
            "command",
            "operation",
            "world",
            "timeline",
            firstUnsafeInteger,
            firstUnsafeInteger,
            "state",
            new string('a', 64),
            new[] { firstIntent });
        var adjacent = new WorldAtomicMutationSet(
            "command",
            "operation",
            "world",
            "timeline",
            adjacentInteger,
            adjacentInteger,
            "state",
            new string('a', 64),
            new[] { adjacentIntent });

        Assert.Equal(
            JsonValueKind.String,
            first.PortableJson.GetProperty("timelineEpoch").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            first.PortableJson.GetProperty("expectedSaveRevision").ValueKind);
        Assert.Equal(
            firstUnsafeInteger.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            first.PortableJson.GetProperty("timelineEpoch").GetString());
        Assert.Equal(
            JsonValueKind.String,
            firstIntent.ToPortableJson()
                .GetProperty("entity")
                .GetProperty("incarnation")
                .ValueKind);
        Assert.NotEqual(first.Digest, adjacent.Digest);
        Assert.NotEqual(
            first.PortableJson.GetRawText(),
            adjacent.PortableJson.GetRawText());
    }

    [Fact]
    public void EntityPathResolverCannotEscapeIntoAnotherEntityRoot()
    {
        var resolver = new WorldEntityMutationPathResolver(
            "/entities",
            "/relationships");

        var resolved = resolver.ResolveValuePath(
            new GameEntityIdentity("actor", 3),
            "/entities/target/balance");

        Assert.Equal(
            "/entities/actor/entities/target/balance",
            resolved);
    }

    [Fact]
    public void AbsolutePathsRequireExplicitTrustedHostOptIn()
    {
        var mutation = new WorldValueMutationIntent(
            "set",
            new GameEntityIdentity("actor", 1),
            "/entities/target/value",
            "entity:actor",
            WorldValueMutationKind.Set,
            Json("\"changed\""));

        Assert.Throws<ArgumentException>(
            () => new WorldAtomicMutationEffect(
                Batch(mutation),
                Array.Empty<WorldNumericSchema>(),
                new WorldAbsoluteMutationPathResolver("/relationships")));
    }

    private static WorldAtomicMutationSet Batch(
        params IWorldMutationIntent[] intents)
    {
        return new WorldAtomicMutationSet(
            "command",
            "operation",
            "world",
            "timeline",
            3,
            4,
            "state-4",
            new string('a', 64),
            intents);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
