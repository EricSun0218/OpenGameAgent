using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class WorldInteractionContractTests
{
    [Fact]
    public void ParameterContractRequiresClosedBoundedSchema()
    {
        Assert.Throws<ArgumentException>(
            () => new InteractionParameterContract(
                "input",
                "1",
                Json(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "text": { "type": "string" }
                      }
                    }
                    """)));

        var contract = ParameterContract();
        Assert.True(
            contract.Validate(Json("""{"amount":"125"}""")).IsValid);
        Assert.False(
            contract.Validate(Json("""{"amount":"125","extra":true}"""))
                .IsValid);
        Assert.False(
            contract.Validate(Json("""{"amount":125}""")).IsValid);
        Assert.Throws<ArgumentException>(
            () => new InteractionParameterContract(
                "numeric-json",
                "1",
                Json(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "amount": { "type": "number" }
                      },
                      "additionalProperties": false
                    }
                    """)));
    }

    [Fact]
    public void CatalogDigestIsStableAcrossInputOrderAndSchemaPropertyOrder()
    {
        var first = Definition(
            "world.interact.b",
            ParameterContract());
        var reorderedSchema = new InteractionParameterContract(
            "input",
            "1",
            Json(
                """
                {
                  "additionalProperties": false,
                  "required": ["amount"],
                  "properties": {
                    "amount": {
                      "maxLength": 20,
                      "minLength": 1,
                      "type": "string"
                    }
                  },
                  "type": "object"
                }
                """));
        var equivalent = Definition(
            "world.interact.b",
            reorderedSchema);
        var second = Definition(
            "world.interact.a",
            ParameterContract());

        var catalogA = new InteractionCatalogSnapshot(
            "catalog",
            7,
            new[] { first, second });
        var catalogB = new InteractionCatalogSnapshot(
            "catalog",
            7,
            new[] { second, equivalent });

        Assert.Equal(first.ContentDigest, equivalent.ContentDigest);
        Assert.Equal(catalogA.Digest, catalogB.Digest);
        Assert.NotEqual(
            catalogA.Digest,
            new InteractionCatalogSnapshot(
                "catalog",
                8,
                new[] { first, second }).Digest);
    }

    [Fact]
    public void CatalogDigestSupportsMoreEntriesThanSharedJsonDigestLimit()
    {
        var contract = ParameterContract();
        var definitions = Enumerable.Range(0, 1_500)
            .Select(
                index => Definition(
                    "interact."
                    + index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    contract))
            .ToArray();

        var catalog = new InteractionCatalogSnapshot(
            "large-catalog",
            1,
            definitions);

        Assert.Equal(1_500, catalog.Definitions.Count);
        Assert.True(CanonicalJsonDigest.IsSha256(catalog.Digest));
    }

    [Fact]
    public async Task QueryIsReadOnlyDeterministicAndGameAdmitted()
    {
        var allowed = Definition(
            "world.interact.allowed",
            ParameterContract(),
            channels: new[] { "scene" },
            capabilities: new[] { "cap.speak" });
        var wrongChannel = Definition(
            "world.interact.remote",
            ParameterContract(),
            channels: new[] { "remote" });
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[] { wrongChannel, allowed });
        var request = QueryRequest(
            channel: "scene",
            capabilities: new[] { "cap.speak" });
        var evaluator = new DelegateAdmissionEvaluator(
            context => new InteractionAdmissionDecision(
                InteractionAvailabilityState.Available,
                "available",
                Json(
                    $$"""{"definition":"{{context.Definition.InteractionId}}"}""")));

        var first = await new InteractionQueryService().QueryAsync(
            catalog,
            request,
            evaluator);
        var second = await new InteractionQueryService().QueryAsync(
            catalog,
            request,
            evaluator);

        var item = Assert.Single(first.Items);
        Assert.Equal("world.interact.allowed", item.InteractionId);
        Assert.Equal(
            first.Items[0].AvailabilityEvidenceDigest,
            second.Items[0].AvailabilityEvidenceDigest);
        Assert.Equal(catalog.Digest, first.CatalogDigest);
        Assert.Equal("state-5", first.StateVersion);
        Assert.Equal(2, evaluator.Seen.Count);
        Assert.Equal(5, first.SaveRevision);
    }

    [Fact]
    public async Task QueryPaginationBindsCatalogDigest()
    {
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[]
            {
                Definition("a", ParameterContract()),
                Definition("b", ParameterContract()),
                Definition("c", ParameterContract())
            });
        var evaluator = new DelegateAdmissionEvaluator(
            _ => new InteractionAdmissionDecision(
                InteractionAvailabilityState.Available,
                "available"));
        var first = await new InteractionQueryService().QueryAsync(
            catalog,
            QueryRequest(maximumResults: 1),
            evaluator);
        var next = await new InteractionQueryService().QueryAsync(
            catalog,
            QueryRequest(
                maximumResults: 1,
                cursor: first.NextContinuationCursor),
            evaluator);

        Assert.Equal("a", Assert.Single(first.Items).InteractionId);
        Assert.Equal("b", Assert.Single(next.Items).InteractionId);
        Assert.Throws<ArgumentException>(
            () => new InteractionQueryService().QueryAsync(
                new InteractionCatalogSnapshot(
                    "catalog",
                    2,
                    catalog.Definitions),
                QueryRequest(
                    maximumResults: 1,
                    cursor: first.NextContinuationCursor),
                evaluator).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task AvailabilityEvidenceBindsTargetsCapabilitiesAndContext()
    {
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[]
            {
                Definition(
                    "interact",
                    ParameterContract(),
                    capabilities: new[] { "cap.use" })
            });
        var evaluator = new DelegateAdmissionEvaluator(
            _ => new InteractionAdmissionDecision(
                InteractionAvailabilityState.Available,
                "available"));
        var first = await new InteractionQueryService().QueryAsync(
            catalog,
            QueryRequest(),
            evaluator);
        var otherContext = new InteractionQueryRequest(
            "world",
            "timeline",
            1,
            5,
            "state-5",
            new GameEntityIdentity("actor", 1),
            "scene",
            targets: new[] { new GameEntityIdentity("other", 2) },
            context: Json("""{"presence":"different"}"""),
            capabilityTags: new[] { "cap.use", "cap.extra" });
        var second = await new InteractionQueryService().QueryAsync(
            catalog,
            otherContext,
            evaluator);

        Assert.NotEqual(
            Assert.Single(first.Items).AvailabilityEvidenceDigest,
            Assert.Single(second.Items).AvailabilityEvidenceDigest);
    }

    [Fact]
    public void ExecutionRevalidatesCatalogTargetsChannelCapabilitiesAndSchema()
    {
        var definition = Definition(
            "interact",
            ParameterContract(),
            channels: new[] { "scene" },
            capabilities: new[] { "cap.use" });
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[] { definition });

        var stale = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}"""),
                catalogDigest: new string('a', 64)));
        var invalidTargets = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}"""),
                targets: Array.Empty<GameEntityIdentity>()));
        var invalidSchema = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(catalog, Json("""{"amount":125}""")));
        var invalidChannel = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}"""),
                channel: "remote"));
        var missingCapability = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}"""),
                capabilities: Array.Empty<string>()));

        Assert.Equal(
            InteractionReasonCodes.StaleCatalog,
            stale.ReasonCode);
        Assert.Equal(
            InteractionReasonCodes.InvalidTargetCount,
            invalidTargets.ReasonCode);
        Assert.Equal(
            InteractionReasonCodes.InvalidParameters,
            invalidSchema.ReasonCode);
        Assert.NotEmpty(invalidSchema.ParameterErrors);
        Assert.Equal(
            InteractionReasonCodes.UnsupportedChannel,
            invalidChannel.ReasonCode);
        Assert.Equal(
            InteractionReasonCodes.CapabilityUnavailable,
            missingCapability.ReasonCode);
    }

    [Fact]
    public void ExecutionOnlyCompilesOneRootEventWithCanonicalTypedPayload()
    {
        var definition = Definition(
            "interact",
            ParameterContract(),
            channels: new[] { "scene" },
            capabilities: new[] { "cap.use" });
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[] { definition });
        var first = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}""")));
        var reordered = InteractionExecutionCompiler.Compile(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{ "amount": "125" }""")));

        Assert.True(first.Succeeded);
        Assert.NotNull(first.Execution);
        Assert.IsType<InteractionExecutionTrigger>(
            first.Execution!.Trigger);
        Assert.Equal(
            WorldInteractionKinds.Requested,
            first.Execution.Trigger.Kind);
        Assert.Equal(
            definition.InteractionId,
            first.Execution.RootEventDefinition.DefinitionId);
        Assert.Equal(
            first.Execution.Trigger.PayloadDigest,
            reordered.Execution!.Trigger.PayloadDigest);
        Assert.Equal(
            "125",
            first.Execution.Trigger.Parameters
                .GetProperty("amount")
                .GetString());
        Assert.Equal(
            catalog.Digest,
            first.Execution.Trigger.CatalogDigest);
    }

    [Fact]
    public async Task
        AuthoritativeInteractionBindingRejectsAnyStaleCoordinate()
    {
        var definition = Definition(
            "interact",
            ParameterContract(),
            channels: new[] { "scene" },
            capabilities: new[] { "cap.use" });
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            1,
            new[] { definition });
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("availability", new AlwaysCondition())
            .AddAdmission("cost-admission", new AlwaysAdmission())
            .AddAdmission("confirmation", new AlwaysAdmission())
            .AddParticipantSelector(
                "selector",
                new InteractionSelector())
            .AddResolver("resolver", new InteractionResolver())
            .AddEffect("effect", new UnusedEffect())
            .Build();
        var facade = new InteractiveWorldFacade(
            new WorldEventPlanner(
                handlers,
                new InMemoryWorldEventHistory()));
        var planned = await facade.PlanInteractionAsync(
            catalog,
            ExecuteRequest(
                catalog,
                Json("""{"amount":"125"}"""),
                expectedStateVersion: "5",
                gameTime: new GameTimePoint(
                    "clock",
                    "timeline",
                    1,
                    5)),
            new WorldStateFence(
                "world",
                "timeline",
                1,
                5,
                "5",
                catalog.Digest));
        Assert.True(planned.Succeeded);
        var interaction = planned.Value!;
        var exact = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            1,
            5,
            5,
            catalog.Digest);

        var artifact = interaction.Bind(exact);

        Assert.Same(interaction.Plan, artifact.Plan);
        Assert.True(artifact.ExpectedCoordinate.IsExactMatch(exact));
        Assert.Throws<ArgumentException>(
            () => interaction.Bind(
                new WorldAuthoritativeCoordinate(
                    "world",
                    "timeline",
                    1,
                    6,
                    5,
                    catalog.Digest)));
        Assert.Throws<ArgumentException>(
            () => interaction.Bind(
                new WorldAuthoritativeCoordinate(
                    "world",
                    "timeline",
                    1,
                    5,
                    6,
                    catalog.Digest)));
    }

    [Fact]
    public void DefinitionBoundsCollectionsAndRejectsDuplicateCatalogEntries()
    {
        var definition = Definition("interact", ParameterContract());

        Assert.Throws<ArgumentException>(
            () => new InteractionCatalogSnapshot(
                "catalog",
                1,
                new[] { definition, definition }));
        Assert.Throws<ArgumentException>(
            () => new InteractionDefinitionDetails(
                "1",
                ParameterContract(),
                channelIds: new[] { "same", "same" }));
        var legacy = new InteractionDefinition(
            "legacy",
            "1",
            "input",
            0,
            "availability",
            "cost",
            "selector",
            "resolver",
            "effect");
        Assert.Throws<ArgumentException>(
            () => new InteractionCatalogSnapshot(
                "catalog",
                1,
                new[] { legacy }));
        Assert.Throws<ArgumentException>(
            () => new InteractionCatalogSnapshot(
                "\ud800",
                1,
                new[] { definition }));
    }

    private static InteractionDefinition Definition(
        string id,
        InteractionParameterContract contract,
        IEnumerable<string>? channels = null,
        IEnumerable<string>? capabilities = null)
    {
        var details = new InteractionDefinitionDetails(
            "content-1",
            contract,
            new InteractionTargetContract("target", 1, 2),
            channelIds: channels,
            tags: new[] { "tag.interact" },
            requiredCapabilities: capabilities,
            costs: new[]
            {
                new InteractionCostDefinition(
                    "cost",
                    "component/value",
                    "numeric.value",
                    new WorldFixedPointValue(10, 2),
                    "insufficient")
            },
            cooldown: new InteractionCooldownDefinition(
                "clock",
                2,
                "scope"),
            duration: new InteractionDurationDefinition(
                "clock",
                1,
                "interaction_completed"),
            steps: new[]
            {
                new InteractionStepDefinition(
                    "step",
                    "effect",
                    Json("""{"mode":"typed"}"""),
                    new[] { "actor:read" },
                    new[] { "target:write" })
            },
            visibilityHandlerId: "visibility",
            presentation: new Dictionary<string, string>
            {
                ["label"] = "label.interact"
            });
        return new InteractionDefinition(
            id,
            "1",
            "input",
            10,
            "availability",
            "cost-admission",
            "selector",
            "resolver",
            "effect",
            confirmationAdmissionHandlerId: "confirmation",
            readResourceKeys: new[] { "actor:read" },
            writeResourceKeys: new[] { "target:write" },
            agentInvocationPolicy:
            WorldAgentInvocationPolicy.OncePerInstance,
            details: details);
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
                  "properties": {
                    "amount": {
                      "type": "string",
                      "minLength": 1,
                      "maxLength": 20
                    }
                  },
                  "required": ["amount"],
                  "additionalProperties": false
                }
                """));
    }

    private static InteractionQueryRequest QueryRequest(
        string channel = "scene",
        IEnumerable<string>? capabilities = null,
        int maximumResults = 64,
        string? cursor = null)
    {
        return new InteractionQueryRequest(
            "world",
            "timeline",
            1,
            5,
            "state-5",
            new GameEntityIdentity("actor", 1),
            channel,
            targets: new[] { new GameEntityIdentity("target", 2) },
            context: Json("""{"presence":"game-defined"}"""),
            capabilityTags: capabilities ?? new[] { "cap.use" },
            maximumResults: maximumResults,
            continuationCursor: cursor);
    }

    private static InteractionExecutionRequest ExecuteRequest(
        InteractionCatalogSnapshot catalog,
        JsonElement parameters,
        string? catalogDigest = null,
        IEnumerable<GameEntityIdentity>? targets = null,
        string channel = "scene",
        IEnumerable<string>? capabilities = null,
        string expectedStateVersion = "state-5",
        GameTimePoint? gameTime = null)
    {
        return new InteractionExecutionRequest(
            "command",
            "idempotency",
            "world",
            "timeline",
            1,
            5,
            expectedStateVersion,
            catalogDigest ?? catalog.Digest,
            "interact",
            "1",
            new GameEntityIdentity("actor", 1),
            targets ?? new[] { new GameEntityIdentity("target", 2) },
            channel,
            parameters,
            capabilities ?? new[] { "cap.use" },
            confirmationToken: "confirmed",
            gameTime: gameTime);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class DelegateAdmissionEvaluator :
        IInteractionAdmissionEvaluator
    {
        private readonly Func<
            InteractionAdmissionContext,
            InteractionAdmissionDecision> _evaluate;

        public DelegateAdmissionEvaluator(
            Func<
                InteractionAdmissionContext,
                InteractionAdmissionDecision> evaluate)
        {
            _evaluate = evaluate;
        }

        public List<string> Seen { get; } = new();

        public ValueTask<InteractionAdmissionDecision> EvaluateAsync(
            InteractionAdmissionContext context,
            CancellationToken cancellationToken)
        {
            Seen.Add(context.Definition.InteractionId);
            return new ValueTask<InteractionAdmissionDecision>(
                _evaluate(context));
        }
    }

    private sealed class AlwaysCondition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class AlwaysAdmission : IWorldEventAdmissionHandler
    {
        public ValueTask<WorldEventAdmissionDecision> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventAdmissionDecision>(
                WorldEventAdmissionDecision.Accept());
        }
    }

    private sealed class InteractionSelector :
        IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventParticipant> participants = new[]
            {
                new WorldEventParticipant("actor", 1, "actor"),
                new WorldEventParticipant("target", 2, "target")
            };
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                participants);
        }
    }

    private sealed class InteractionResolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldEventResolution> resolutions = new[]
            {
                new WorldEventResolution(
                    "interaction",
                    selectedParticipants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                resolutions);
        }
    }

    private sealed class UnusedEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "unused"));
        }
    }
}
