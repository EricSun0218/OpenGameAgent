using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class PortableWorldCarrierTests
{
    private const long BeyondSafeInteger = 9_007_199_254_740_992;
    private const long BeyondSafeIntegerPlusOne =
        BeyondSafeInteger + 1;

    [Fact]
    public void InteractionTriggerPreservesInt64FieldsAndStringIdentities()
    {
        var first = RequestedTrigger(
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var next = RequestedTrigger(
            BeyondSafeIntegerPlusOne,
            BeyondSafeIntegerPlusOne,
            BeyondSafeIntegerPlusOne,
            BeyondSafeIntegerPlusOne);

        Assert.Equal(BeyondSafeInteger, first.TimelineEpoch);
        Assert.Equal(BeyondSafeInteger, first.GameTime!.Epoch);
        Assert.Equal(BeyondSafeInteger, first.GameTime.Tick);
        Assert.Equal(BeyondSafeInteger, first.Actor.Incarnation);
        Assert.Equal(BeyondSafeInteger, first.Target!.Incarnation);

        var payload = first.Payload!.Value;
        AssertCanonicalInt64String(
            payload.GetProperty("actor"),
            "incarnation",
            BeyondSafeInteger);
        AssertCanonicalInt64String(
            payload.GetProperty("target"),
            "incarnation",
            BeyondSafeInteger);
        Assert.NotEqual(first.PayloadDigest, next.PayloadDigest);
        Assert.False(
            Encoding.UTF8.GetBytes(payload.GetRawText()).AsSpan()
                .SequenceEqual(
                    Encoding.UTF8.GetBytes(
                        next.Payload!.Value.GetRawText())));
    }

    [Fact]
    public void InteractionExecutionCarriesInt64FieldsWithoutPrecisionLoss()
    {
        var catalog = Catalog(
            generation: 1,
            cooldownTicks: 2,
            durationTicks: 1);
        var first = Compile(
            catalog,
            ExecutionRequest(
                catalog,
                BeyondSafeInteger,
                BeyondSafeInteger,
                BeyondSafeInteger,
                BeyondSafeInteger,
                BeyondSafeInteger));
        var next = Compile(
            catalog,
            ExecutionRequest(
                catalog,
                BeyondSafeIntegerPlusOne,
                BeyondSafeIntegerPlusOne,
                BeyondSafeIntegerPlusOne,
                BeyondSafeIntegerPlusOne,
                BeyondSafeIntegerPlusOne));

        Assert.Equal(BeyondSafeInteger, first.TimelineEpoch);
        Assert.Equal(BeyondSafeInteger, first.ExpectedSaveRevision);
        Assert.Equal(BeyondSafeInteger, first.Actor.Incarnation);
        Assert.Equal(
            BeyondSafeInteger,
            Assert.Single(first.Targets).Incarnation);
        Assert.Equal(BeyondSafeInteger, first.GameTime!.Epoch);
        Assert.Equal(BeyondSafeInteger, first.GameTime.Tick);

        var payload = first.Payload!.Value;
        AssertCanonicalInt64String(
            payload,
            "expectedSaveRevision",
            BeyondSafeInteger);
        AssertCanonicalInt64String(
            payload.GetProperty("actor"),
            "incarnation",
            BeyondSafeInteger);
        AssertCanonicalInt64String(
            Assert.Single(payload.GetProperty("targets").EnumerateArray()),
            "incarnation",
            BeyondSafeInteger);
        Assert.NotEqual(first.PayloadDigest, next.PayloadDigest);
        Assert.False(
            Encoding.UTF8.GetBytes(payload.GetRawText()).AsSpan()
                .SequenceEqual(
                    Encoding.UTF8.GetBytes(
                        next.Payload!.Value.GetRawText())));
    }

    [Fact]
    public async Task InteractionEvidenceDistinguishesAdjacentInt64Values()
    {
        var catalog = Catalog(
            generation: BeyondSafeInteger,
            cooldownTicks: 2,
            durationTicks: 1);
        var baseline = await EvidenceAsync(
            catalog,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextEpoch = await EvidenceAsync(
            catalog,
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextRevision = await EvidenceAsync(
            catalog,
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextActor = await EvidenceAsync(
            catalog,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger);
        var nextTarget = await EvidenceAsync(
            catalog,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne);

        Assert.Equal(BeyondSafeInteger, catalog.Generation);
        Assert.NotEqual(baseline, nextEpoch);
        Assert.NotEqual(baseline, nextRevision);
        Assert.NotEqual(baseline, nextActor);
        Assert.NotEqual(baseline, nextTarget);
        Assert.NotEqual(
            catalog.Digest,
            Catalog(
                BeyondSafeIntegerPlusOne,
                cooldownTicks: 2,
                durationTicks: 1).Digest);
    }

    [Fact]
    public void InteractionDefinitionUsesStringTicksInCanonicalJson()
    {
        var first = Definition(
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextCooldown = Definition(
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger);
        var nextDuration = Definition(
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne);
        var firstBytes = WriteCanonicalDefinition(first);
        var nextCooldownBytes = WriteCanonicalDefinition(nextCooldown);
        var nextDurationBytes = WriteCanonicalDefinition(nextDuration);

        using var document = JsonDocument.Parse(firstBytes);
        var root = document.RootElement;
        AssertCanonicalInt64String(
            root,
            "minimumCooldownTicks",
            BeyondSafeInteger);
        AssertCanonicalInt64String(
            root.GetProperty("details").GetProperty("cooldown"),
            "minimumTicks",
            BeyondSafeInteger);
        AssertCanonicalInt64String(
            root.GetProperty("details").GetProperty("duration"),
            "ticks",
            BeyondSafeInteger);

        Assert.False(firstBytes.AsSpan().SequenceEqual(nextCooldownBytes));
        Assert.False(firstBytes.AsSpan().SequenceEqual(nextDurationBytes));
        Assert.NotEqual(first.ContentDigest, nextCooldown.ContentDigest);
        Assert.NotEqual(first.ContentDigest, nextDuration.ContentDigest);
    }

    [Fact]
    public void NativeAuthoredInt64StringsCompileAndRemainDistinct()
    {
        var baseline = NativePackage(
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextEvery = NativePackage(
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextOffset = NativePackage(
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger,
            BeyondSafeInteger);
        var nextSelectorIncarnation = NativePackage(
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne,
            BeyondSafeInteger);
        var nextWorldIncarnation = NativePackage(
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeIntegerPlusOne);

        AssertNativeSourceStrings(baseline, BeyondSafeInteger);
        var compiled = CompileNative(baseline);
        var trigger = Assert.IsType<NativeWorldClockEventTrigger>(
            Assert.Single(compiled.Events).Trigger);
        var selector = Assert.IsType<NativeWorldEntitySelector>(
            Assert.Single(compiled.Events).Selector);
        Assert.Equal(BeyondSafeInteger, trigger.EveryTicks);
        Assert.Equal(BeyondSafeInteger, trigger.OffsetTicks);
        Assert.Equal(
            BeyondSafeInteger,
            selector.RequiredIncarnation);
        Assert.Equal(
            BeyondSafeInteger,
            compiled.World.EntityIncarnations["actor"]);

        AssertNativeVariantDiffers(baseline, compiled, nextEvery);
        AssertNativeVariantDiffers(baseline, compiled, nextOffset);
        AssertNativeVariantDiffers(
            baseline,
            compiled,
            nextSelectorIncarnation);
        AssertNativeVariantDiffers(
            baseline,
            compiled,
            nextWorldIncarnation);
    }

    [Theory]
    [InlineData("everyTicks")]
    [InlineData("offsetTicks")]
    [InlineData("selectorIncarnation")]
    [InlineData("worldIncarnation")]
    public void NativeAuthoredInt64FieldsRejectJsonNumbers(string field)
    {
        var package = NativePackage(
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            BeyondSafeInteger,
            numericField: field);

        var result = new NativeWorldPackageCompiler().Compile(package);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code
                          == NativeWorldSemanticReasonCodes.InvalidShape);
    }

    private static InteractionRequestedTrigger RequestedTrigger(
        long timelineEpoch,
        long gameTimeTick,
        long actorIncarnation,
        long targetIncarnation)
    {
        return new InteractionRequestedTrigger(
            "request",
            "world",
            "timeline",
            timelineEpoch,
            new GameEntityIdentity("actor", actorIncarnation),
            "input",
            Json("""{"choice":"observe"}"""),
            new GameEntityIdentity("target", targetIncarnation),
            gameTime: new GameTimePoint(
                "turn",
                "timeline",
                timelineEpoch,
                gameTimeTick));
    }

    private static InteractionExecutionRequest ExecutionRequest(
        InteractionCatalogSnapshot catalog,
        long timelineEpoch,
        long saveRevision,
        long actorIncarnation,
        long targetIncarnation,
        long gameTimeTick)
    {
        return new InteractionExecutionRequest(
            "command",
            "operation",
            "world",
            "timeline",
            timelineEpoch,
            saveRevision,
            "state",
            catalog.Digest,
            "interact",
            "1",
            new GameEntityIdentity("actor", actorIncarnation),
            new[]
            {
                new GameEntityIdentity(
                    "target",
                    targetIncarnation)
            },
            "scene",
            Json("""{}"""),
            gameTime: new GameTimePoint(
                "turn",
                "timeline",
                timelineEpoch,
                gameTimeTick));
    }

    private static InteractionExecutionTrigger Compile(
        InteractionCatalogSnapshot catalog,
        InteractionExecutionRequest request)
    {
        var result = InteractionExecutionCompiler.Compile(
            catalog,
            request);
        Assert.True(result.Succeeded, result.ReasonCode);
        return Assert.IsType<InteractionExecutionTrigger>(
            result.Execution!.Trigger);
    }

    private static async Task<string> EvidenceAsync(
        InteractionCatalogSnapshot catalog,
        long timelineEpoch,
        long saveRevision,
        long actorIncarnation,
        long targetIncarnation)
    {
        var request = new InteractionQueryRequest(
            "world",
            "timeline",
            timelineEpoch,
            saveRevision,
            "state",
            new GameEntityIdentity("actor", actorIncarnation),
            "scene",
            new[]
            {
                new GameEntityIdentity(
                    "target",
                    targetIncarnation)
            });
        var result = await new InteractionQueryService().QueryAsync(
            catalog,
            request,
            new AvailableAdmissionEvaluator());
        return Assert.Single(result.Items).AvailabilityEvidenceDigest;
    }

    private static InteractionCatalogSnapshot Catalog(
        long generation,
        long cooldownTicks,
        long durationTicks)
    {
        return new InteractionCatalogSnapshot(
            "catalog",
            generation,
            new[] { Definition(cooldownTicks, durationTicks) });
    }

    private static InteractionDefinition Definition(
        long cooldownTicks,
        long durationTicks)
    {
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
            new InteractionTargetContract("entity", 1, 1),
            cooldown: new InteractionCooldownDefinition(
                "turn",
                cooldownTicks,
                "actor"),
            duration: new InteractionDurationDefinition(
                "turn",
                durationTicks,
                "completed"));
        return new InteractionDefinition(
            "interact",
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

    private static byte[] WriteCanonicalDefinition(
        InteractionDefinition definition)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            InteractionCanonicalJson.WriteDefinition(writer, definition);
        }

        return output.ToArray();
    }

    private static WorldPackageDefinition NativePackage(
        long everyTicks,
        long offsetTicks,
        long selectorIncarnation,
        long worldIncarnation,
        string? numericField = null)
    {
        var every = JsonInt64(
            everyTicks,
            numericField == "everyTicks");
        var offset = JsonInt64(
            offsetTicks,
            numericField == "offsetTicks");
        var selector = JsonInt64(
            selectorIncarnation,
            numericField == "selectorIncarnation");
        var world = JsonInt64(
            worldIncarnation,
            numericField == "worldIncarnation");
        return new WorldPackageDefinition(
            "portable-world",
            "1",
            new[]
            {
                JsonFile(
                    "world.json",
                    $$"""
                    {
                      "contract": "game-agent.world-definition.v1",
                      "worldId": "world",
                      "defaultTimelineId": "timeline",
                      "entityStateRootPath": "/entities",
                      "relationshipRootPath": "/relationships",
                      "initialState": {
                        "entities": { "actor": { } },
                        "relationships": {}
                      },
                      "entityIncarnations": {
                        "actor": {{world}}
                      }
                    }
                    """),
                JsonFile(
                    "clocks.json",
                    """
                    {
                      "contract": "game-agent.world-clocks.v1",
                      "clocks": [
                        {
                          "clockId": "turn",
                          "statePath": "/clocks/turn/tick",
                          "initialTick": "0"
                        }
                      ]
                    }
                    """),
                JsonFile(
                    "events.json",
                    $$"""
                    {
                      "contract": "game-agent.world-events.v1",
                      "events": [
                        {
                          "definitionId": "tick",
                          "version": "1",
                          "priority": 0,
                          "trigger": {
                            "kind": "clock",
                            "clockId": "turn",
                            "everyTicks": {{every}},
                            "offsetTicks": {{offset}}
                          },
                          "selector": {
                            "kind": "entity",
                            "entityId": "actor",
                            "incarnation": {{selector}}
                          },
                          "condition": {"kind": "always"},
                          "effects": []
                        }
                      ]
                    }
                    """)
            });
    }

    private static string JsonInt64(long value, bool asNumber)
    {
        var text = value.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return asNumber ? text : "\"" + text + "\"";
    }

    private static void AssertNativeSourceStrings(
        WorldPackageDefinition package,
        long expected)
    {
        using var world = JsonDocument.Parse(
            package.Files.Single(file => file.Path == "world.json")
                .GetContentCopy());
        AssertCanonicalInt64String(
            world.RootElement.GetProperty("entityIncarnations"),
            "actor",
            expected);
        using var events = JsonDocument.Parse(
            package.Files.Single(file => file.Path == "events.json")
                .GetContentCopy());
        var item = Assert.Single(
            events.RootElement.GetProperty("events").EnumerateArray());
        var trigger = item.GetProperty("trigger");
        AssertCanonicalInt64String(
            trigger,
            "everyTicks",
            expected);
        AssertCanonicalInt64String(
            trigger,
            "offsetTicks",
            expected);
        AssertCanonicalInt64String(
            item.GetProperty("selector"),
            "incarnation",
            expected);
    }

    private static ActivatedWorldPackage CompileNative(
        WorldPackageDefinition package)
    {
        var result = new NativeWorldPackageCompiler().Compile(package);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(
                    diagnostic => diagnostic.Code
                                  + " "
                                  + diagnostic.Path
                                  + " "
                                  + diagnostic.Message)));
        return Assert.IsType<ActivatedWorldPackage>(result.Package);
    }

    private static void AssertNativeVariantDiffers(
        WorldPackageDefinition baseline,
        ActivatedWorldPackage compiledBaseline,
        WorldPackageDefinition variant)
    {
        var compiledVariant = CompileNative(variant);

        Assert.NotEqual(baseline.PackageDigest, variant.PackageDigest);
        Assert.False(
            WritePackage(baseline).AsSpan()
                .SequenceEqual(WritePackage(variant)));
        Assert.NotEqual(
            compiledBaseline.CatalogDigest,
            compiledVariant.CatalogDigest);
    }

    private static byte[] WritePackage(WorldPackageDefinition package)
    {
        using var output = new MemoryStream();
        WorldPackageArchive.Write(output, package);
        return output.ToArray();
    }

    private static WorldPackageFile JsonFile(
        string path,
        string content)
    {
        return new WorldPackageFile(
            path,
            "application/json",
            Encoding.UTF8.GetBytes(content));
    }

    private static void AssertCanonicalInt64String(
        JsonElement parent,
        string propertyName,
        long expected)
    {
        var value = parent.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(
            expected.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.GetString());
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class AvailableAdmissionEvaluator
        : IInteractionAdmissionEvaluator
    {
        public ValueTask<InteractionAdmissionDecision> EvaluateAsync(
            InteractionAdmissionContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(
                new InteractionAdmissionDecision(
                    InteractionAvailabilityState.Available,
                    "available"));
        }
    }
}
