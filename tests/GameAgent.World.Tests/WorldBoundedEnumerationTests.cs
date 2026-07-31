using System.Collections;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldBoundedEnumerationTests
{
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CatalogsStopEnumeratingAtMaxPlusOne()
    {
        var eventProbe = new EndlessProbe<WorldEventDefinition>(
            EventDefinition(),
            8_193);
        var interactionProbe = new EndlessProbe<InteractionDefinition>(
            Interaction(),
            8_193);

        Assert.Throws<ArgumentException>(
            () => new WorldEventCatalogSnapshot(
                "events",
                0,
                eventProbe));
        Assert.Throws<ArgumentException>(
            () => new InteractionCatalogSnapshot(
                "interactions",
                0,
                interactionProbe));

        Assert.Equal(8_193, eventProbe.MoveNextCalls);
        Assert.Equal(8_193, interactionProbe.MoveNextCalls);
    }

    [Fact]
    public void MutationAndResourceCollectionsStopAtMaxPlusOne()
    {
        var intent = new WorldValueMutationIntent(
            "intent",
            new GameEntityIdentity("actor", 1),
            "value",
            "actor:value",
            WorldValueMutationKind.Remove);
        var intentProbe = new EndlessProbe<IWorldMutationIntent>(
            intent,
            513);
        var keyProbe = new EndlessProbe<string>("resource", 513);

        Assert.Throws<ArgumentException>(
            () => new WorldAtomicMutationSet(
                "command",
                "operation",
                "world",
                "timeline",
                1,
                0,
                "0",
                Digest,
                intentProbe));
        Assert.Throws<ArgumentException>(
            () => new WorldEventDefinition(
                "event",
                "1",
                "tick",
                1,
                "condition",
                "selector",
                "resolver",
                "effect",
                readResourceKeys: keyProbe));

        Assert.Equal(513, intentProbe.MoveNextCalls);
        Assert.Equal(513, keyProbe.MoveNextCalls);
    }

    [Fact]
    public void DataAndIdentityCollectionsStopAtMaxPlusOne()
    {
        var clockProbe = new EndlessProbe<WorldClockSnapshot>(
            new WorldClockSnapshot("clock", 0, 0),
            257);
        var fileProbe = new EndlessProbe<WorldPackageFile>(
            new WorldPackageFile(
                "data/value.json",
                "application/json",
                Encoding.UTF8.GetBytes("{}")),
            4_097);
        var identityProbe = new EndlessProbe<GameEntityIdentity>(
            new GameEntityIdentity("target", 1),
            65);

        Assert.Throws<ArgumentException>(
            () => new WorldSaveDocument(
                "package",
                "1",
                Digest,
                "world",
                "timeline",
                0,
                "0",
                clockProbe,
                Json("{}"),
                Json("[]"),
                Json("[]")));
        var packageError = Assert.Throws<WorldDataContractException>(
            () => new WorldPackageDefinition(
                "package",
                "1",
                fileProbe));
        Assert.Equal(
            WorldDataReasonCodes.EntryLimitExceeded,
            packageError.ReasonCode);
        Assert.Throws<ArgumentException>(
            () => new InteractionQueryRequest(
                "world",
                "timeline",
                1,
                0,
                "0",
                new GameEntityIdentity("actor", 1),
                "scene",
                targets: identityProbe));

        Assert.Equal(257, clockProbe.MoveNextCalls);
        Assert.Equal(4_097, fileProbe.MoveNextCalls);
        Assert.Equal(65, identityProbe.MoveNextCalls);
    }

    [Fact]
    public void TypedWorldCollectionsStopAtMaxPlusOne()
    {
        var costProbe = new EndlessProbe<InteractionCostDefinition>(
            new InteractionCostDefinition(
                "cost",
                "value",
                "numeric",
                new WorldFixedPointValue(1, 0),
                "insufficient"),
            65);
        var conditionProbe = new EndlessProbe<NativeWorldCondition>(
            new NativeWorldAlwaysCondition(),
            8_193);
        var schemaProbe = new EndlessProbe<WorldNumericSchema>(
            new WorldNumericSchema(
                "numeric",
                0,
                "unit",
                "0",
                "10",
                "0"),
            8_193);

        Assert.Throws<ArgumentException>(
            () => new InteractionDefinitionDetails(
                "1",
                ParameterContract(),
                costs: costProbe));
        Assert.Throws<ArgumentException>(
            () => new NativeWorldAllCondition(conditionProbe));
        Assert.Throws<ArgumentException>(
            () => new WorldAtomicMutationEffect(
                Mutation(),
                schemaProbe,
                new WorldEntityMutationPathResolver(
                    "/entities",
                    "/relationships")));

        Assert.Equal(65, costProbe.MoveNextCalls);
        Assert.Equal(8_193, conditionProbe.MoveNextCalls);
        Assert.Equal(8_193, schemaProbe.MoveNextCalls);
    }

    [Fact]
    public void LyingReadOnlyDictionariesStopAtMaxPlusOne()
    {
        var parameterProbe =
            new LyingReadOnlyDictionary<string, string>(
                index => new KeyValuePair<string, string>(
                    "attribute." + index,
                    "value"),
                declaredCount: 1,
                maximumAllowedMoves: 129);
        var extensionProbe =
            new LyingReadOnlyDictionary<string, JsonElement>(
                index => new KeyValuePair<string, JsonElement>(
                    "test.extension." + index,
                    Json("{}")),
                declaredCount: 1,
                maximumAllowedMoves: 257);
        var componentProbe =
            new LyingReadOnlyDictionary<string, string>(
                index => new KeyValuePair<string, string>(
                    "component." + index,
                    Digest),
                declaredCount: 1,
                maximumAllowedMoves: 65);
        var incarnationProbe =
            new LyingReadOnlyDictionary<string, long>(
                index => new KeyValuePair<string, long>(
                    "entity." + index,
                    index),
                declaredCount: 1,
                maximumAllowedMoves: 4_097);

        Assert.Throws<ArgumentException>(
            () => new WorldEventDefinition(
                "event",
                "1",
                "tick",
                1,
                "condition",
                "selector",
                "resolver",
                "effect",
                attributes: parameterProbe));
        Assert.Throws<ArgumentException>(
            () => new WorldPackageDefinition(
                "package",
                "1",
                Array.Empty<WorldPackageFile>(),
                extensionData: extensionProbe));
        Assert.Throws<ArgumentException>(
            () => new WorldCatalogSnapshot(
                "catalog",
                0,
                Array.Empty<WorldEventDefinition>(),
                Array.Empty<InteractionDefinition>(),
                componentProbe));
        Assert.Throws<ArgumentException>(
            () => new WorldAuthoritativeStateSnapshot(
                new WorldAuthoritativeCoordinate(
                    "world",
                    "timeline",
                    1,
                    0,
                    0,
                    Digest),
                Json("{}"),
                incarnationProbe));

        Assert.Equal(129, parameterProbe.MoveNextCalls);
        Assert.Equal(257, extensionProbe.MoveNextCalls);
        Assert.Equal(65, componentProbe.MoveNextCalls);
        Assert.Equal(4_097, incarnationProbe.MoveNextCalls);
    }

    [Fact]
    public void FileStoreBoundsInitialStatesAndReleasesItsLock()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "world-bounded-enumeration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "world.json");
        var snapshot = Snapshot();
        var probe = new EndlessProbe<WorldAuthoritativeStateSnapshot>(
            snapshot,
            2);
        try
        {
            Assert.Throws<ArgumentException>(
                () => new FileWorldAuthoritativeTransactionStore(
                    path,
                    probe,
                    new FileWorldAuthoritativeTransactionStoreOptions(
                        maxStates: 1)));
            Assert.Equal(2, probe.MoveNextCalls);

            _ = new FileWorldAuthoritativeTransactionStore(
                path,
                new[] { snapshot },
                new FileWorldAuthoritativeTransactionStoreOptions(
                    maxStates: 1));
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static WorldEventDefinition EventDefinition()
    {
        return new WorldEventDefinition(
            "event",
            "1",
            "tick",
            1,
            "condition",
            "selector",
            "resolver",
            "effect");
    }

    private static InteractionDefinition Interaction()
    {
        return new InteractionDefinition(
            "interaction",
            "1",
            "input",
            1,
            "availability",
            "cost",
            "selector",
            "resolver",
            "effect",
            details: new InteractionDefinitionDetails(
                "1",
                ParameterContract()));
    }

    private static InteractionParameterContract ParameterContract()
    {
        return new InteractionParameterContract(
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
    }

    private static WorldAtomicMutationSet Mutation()
    {
        return new WorldAtomicMutationSet(
            "command",
            "operation",
            "world",
            "timeline",
            1,
            0,
            "0",
            Digest,
            new IWorldMutationIntent[]
            {
                new WorldValueMutationIntent(
                    "intent",
                    new GameEntityIdentity("actor", 1),
                    "value",
                    "actor:value",
                    WorldValueMutationKind.Remove)
            });
    }

    private static WorldAuthoritativeStateSnapshot Snapshot()
    {
        return new WorldAuthoritativeStateSnapshot(
            new WorldAuthoritativeCoordinate(
                "world",
                "timeline",
                1,
                0,
                0,
                Digest),
            Json("{}"));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class EndlessProbe<T> : IEnumerable<T>
    {
        private readonly T _value;
        private readonly int _maximumAllowedMoves;

        public EndlessProbe(T value, int maximumAllowedMoves)
        {
            _value = value;
            _maximumAllowedMoves = maximumAllowedMoves;
        }

        public int MoveNextCalls { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            while (true)
            {
                MoveNextCalls++;
                if (MoveNextCalls > _maximumAllowedMoves)
                {
                    throw new InvalidOperationException(
                        "The framework enumerated beyond Max+1.");
                }

                yield return _value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class LyingReadOnlyDictionary<TKey, TValue>
        : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Func<int, KeyValuePair<TKey, TValue>> _factory;
        private readonly int _maximumAllowedMoves;

        public LyingReadOnlyDictionary(
            Func<int, KeyValuePair<TKey, TValue>> factory,
            int declaredCount,
            int maximumAllowedMoves)
        {
            _factory = factory;
            Count = declaredCount;
            _maximumAllowedMoves = maximumAllowedMoves;
        }

        public int Count { get; }

        public int MoveNextCalls { get; private set; }

        public IEnumerable<TKey> Keys =>
            throw new InvalidOperationException(
                "The bounded copy must enumerate dictionary entries.");

        public IEnumerable<TValue> Values =>
            throw new InvalidOperationException(
                "The bounded copy must enumerate dictionary entries.");

        public TValue this[TKey key] => throw new KeyNotFoundException();

        public bool ContainsKey(TKey key)
        {
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            while (true)
            {
                MoveNextCalls++;
                if (MoveNextCalls > _maximumAllowedMoves)
                {
                    throw new InvalidOperationException(
                        "The framework enumerated beyond Max+1.");
                }

                yield return _factory(MoveNextCalls);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
