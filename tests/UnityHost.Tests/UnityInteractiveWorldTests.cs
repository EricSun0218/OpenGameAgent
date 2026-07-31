using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Unity;
using GameAgent.World;

namespace GameAgent.Unity.Tests;

public sealed class UnityInteractiveWorldTests
{
    [Fact]
    public async Task NativeSessionActivatesInteractsEvolvesAndReloads()
    {
        var package = NativeFixturePackage();
        using var archive = new MemoryStream();
        WorldPackageArchive.Write(archive, package);
        var packageBytes = archive.ToArray();
        await using var unity = new UnityNativeWorldSessionFacade();

        var loaded = await unity.LoadPackageAsync(packageBytes);
        Assert.True(
            loaded.Activated,
            string.Join(
                " | ",
                loaded.Diagnostics.Select(
                    item => item.Code
                            + " "
                            + item.Path
                            + ": "
                            + item.Message)));
        Assert.Equal(
            package.PackageDigest,
            unity.Status.ActivePackageDigest);
        var active = loaded.Package
                     ?? throw new InvalidOperationException(
                         "Native package activation returned no package.");
        var initial = RequireSnapshot(
            await unity.Typed.ReadSnapshotAsync());
        var actor = new GameEntityIdentity("mira", 1);
        var target = new GameEntityIdentity("ren", 1);

        var query = await unity.Typed.QueryInteractionsAsync(
            new InteractionQueryRequest(
                initial.Coordinate.WorldId,
                initial.Coordinate.TimelineId,
                initial.Coordinate.TimelineEpoch,
                initial.Coordinate.SaveRevision,
                initial.Coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                actor,
                "local",
                new[] { target }));
        Assert.True(query.Succeeded);
        Assert.Single(query.Value!.Items);

        var plan = await unity.Typed.PlanInteractionAsync(
            new InteractionExecutionRequest(
                "unity-native-command",
                "unity-native-operation",
                initial.Coordinate.WorldId,
                initial.Coordinate.TimelineId,
                initial.Coordinate.TimelineEpoch,
                initial.Coordinate.SaveRevision,
                initial.Coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                active.CatalogDigest,
                "offer-garden-help",
                "1",
                actor,
                new[] { target },
                "local",
                Json("""{"topic":"garden"}""")));
        Assert.True(plan.Succeeded);
        var execution = await unity.Typed.ExecuteInteractionAsync(
            plan.Value
            ?? throw new InvalidOperationException(
                "Unity native interaction returned no plan."));
        Assert.True(execution.Succeeded);

        var interacted = RequireSnapshot(
            await unity.Typed.ReadSnapshotAsync());
        Assert.Equal("1250", Trust(interacted));
        Assert.True(
            interacted.State.GetProperty("entities")
                .GetProperty("ren")
                .GetProperty("helpReceived")
                .GetBoolean());

        var advanced = await unity.Typed.AdvanceClockAsync(
            new WorldAdvanceClockCommand(
                "unity-month-command",
                "unity-month-operation",
                interacted.Coordinate,
                "calendar.month",
                expectedClockTick: 0,
                ticks: 1));
        Assert.True(advanced.Succeeded);
        var settled = RequireSnapshot(
            await unity.Typed.ReadSnapshotAsync());
        Assert.Equal("1350", Trust(settled));

        var saveBytes = await unity.CaptureSaveAsync();
        var beforeReload = unity.Status.Generation;
        var reloaded = await unity.LoadSaveAsync(saveBytes);
        Assert.True(reloaded.Generation > beforeReload);
        Assert.Equal(
            settled.StateDigest,
            RequireSnapshot(await unity.Typed.ReadSnapshotAsync())
                .StateDigest);

        await unity.ShutdownAsync();
        Assert.False(unity.Status.IsAcceptingOperations);
    }

    [Fact]
    public void PackageAndSaveRoundTripThroughSharedUnityFacade()
    {
        var portable = CreatePortableFacade();
        var unity = new UnityInteractiveWorldFacade(portable);
        var package = Package();
        var save = Save(package);
        var temporary = Path.Combine(
            Path.GetTempPath(),
            "game-agent-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var packageBytes = portable.ExportPackage(package);
            var imported = unity.ImportPackage(packageBytes);
            Assert.Equal(package.PackageDigest, imported.PackageDigest);
            Assert.Equal(packageBytes, unity.ExportPackage());

            unity.SetSave(save);
            var saveBytes = unity.ExportSave();
            var importedSave = unity.ImportSave(saveBytes);
            Assert.Equal(save.SaveDigest, importedSave.SaveDigest);

            var packagePath = Path.Combine(temporary, "world.gaworld");
            var savePath = Path.Combine(temporary, "save.json");
            unity.ExportPackageFile(packagePath);
            unity.ExportSaveFile(savePath);
            Assert.Equal(
                package.PackageDigest,
                unity.ImportPackageFile(packagePath).PackageDigest);
            Assert.Equal(
                save.SaveDigest,
                unity.ImportSaveFile(savePath).SaveDigest);
        }
        finally
        {
            unity.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public async Task TypedInteractionHasPortablePlanningParityAndMainThreadPump()
    {
        var portable = CreatePortableFacade();
        await using var unity = new UnityInteractiveWorldFacade(portable);
        var interaction = Interaction();
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            generation: 7,
            new[] { interaction });
        var request = Request(catalog.Digest);
        var fence = Fence(catalog.Digest);

        var portableResult = await portable.PlanInteractionAsync(
            catalog,
            request,
            fence);
        var unityResult = await unity.PlanInteractionAsync(
            catalog,
            request,
            fence);

        Assert.True(portableResult.Succeeded);
        Assert.True(unityResult.Succeeded);
        var portableInstance = Assert.Single(
            portableResult.Value!.Plan.Instances);
        var unityInstance = Assert.Single(
            unityResult.Value!.Plan.Instances);
        Assert.Equal(portableInstance.InstanceId, unityInstance.InstanceId);
        Assert.Equal(
            portableInstance.PlanFingerprint,
            unityInstance.PlanFingerprint);
        Assert.Equal(
            "1.25",
            unityResult.Value.Compilation.Trigger.Parameters
                .GetProperty("amount")
                .GetString());

        Assert.True(
            unity.TryScheduleInteraction(
                "typed-operation",
                catalog,
                request,
                fence,
                out var rejection),
            rejection);
        WorldBackgroundOperationResult? completion = null;
        var callbackThread = -1;
        var expectedPumpThread = -1;
        for (var attempt = 0;
             attempt < 200 && completion is null;
             attempt++)
        {
            expectedPumpThread = Environment.CurrentManagedThreadId;
            unity.Pump(
                8,
                result =>
                {
                    callbackThread = Environment.CurrentManagedThreadId;
                    completion = result;
                });
            if (completion is null)
            {
                await Task.Delay(5);
            }
        }

        Assert.NotNull(completion);
        Assert.True(completion!.Succeeded);
        Assert.Equal(expectedPumpThread, callbackThread);
        var scheduled = Assert.IsType<
            InteractiveWorldResult<WorldInteractionPlan>>(
            completion.Value);
        Assert.Equal(
            unityInstance.InstanceId,
            Assert.Single(scheduled.Value!.Plan.Instances).InstanceId);
    }

    [Fact]
    public async Task StateAndCatalogFencesFailClosedBeforePlanning()
    {
        var portable = CreatePortableFacade();
        await using var unity = new UnityInteractiveWorldFacade(portable);
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            generation: 8,
            new[] { Interaction() });
        var request = Request(catalog.Digest);

        var staleState = await unity.PlanInteractionAsync(
            catalog,
            request,
            new WorldStateFence(
                "world",
                "timeline",
                1,
                saveRevision: 10,
                stateVersion: "state-10",
                catalog.Digest));
        Assert.False(staleState.Succeeded);
        Assert.Equal(
            InteractiveWorldReasonCodes.StaleState,
            staleState.ReasonCode);

        var staleCatalog = await unity.PlanInteractionAsync(
            catalog,
            request,
            Fence(new string('a', 64)));
        Assert.False(staleCatalog.Succeeded);
        Assert.Equal(
            InteractiveWorldReasonCodes.StaleCatalog,
            staleCatalog.ReasonCode);

        var staleRequest = await unity.PlanInteractionAsync(
            catalog,
            Request(new string('b', 64)),
            Fence(catalog.Digest));
        Assert.False(staleRequest.Succeeded);
        Assert.Equal(
            InteractionReasonCodes.StaleCatalog,
            staleRequest.ReasonCode);

        var wrongWorld = await unity.PlanTriggerAsync(
            new WorldEvolutionTrigger(
                "typed-trigger",
                "typed_event",
                "other-world",
                "timeline",
                1,
                gameTime: null,
                payload: Json("""{"choice":"east"}""")),
            new[] { Interaction().ToEventDefinition() },
            Fence(catalog.Digest));
        Assert.False(wrongWorld.Succeeded);
        Assert.Equal(
            InteractiveWorldReasonCodes.WorldFenceMismatch,
            wrongWorld.ReasonCode);
    }

    private static InteractiveWorldFacade CreatePortableFacade()
    {
        var registry = new WorldEventHandlerRegistryBuilder()
            .AddCondition("available", new Condition())
            .AddAdmission("cost", new Admission())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver())
            .AddEffect("effect", new Effect())
            .Build();
        return new InteractiveWorldFacade(
            new WorldEventPlanner(
                registry,
                new InMemoryWorldEventHistory()));
    }

    private static InteractionDefinition Interaction()
    {
        var parameterContract = new InteractionParameterContract(
            "schema.typed.v1",
            "1",
            Json(
                """
                {
                  "type": "object",
                  "properties": {
                    "choice": {
                      "type": "string",
                      "enum": ["east", "west"]
                    },
                    "amount": {
                      "type": "string",
                      "minLength": 1,
                      "maxLength": 32
                    }
                  },
                  "required": ["choice", "amount"],
                  "additionalProperties": false
                }
                """));
        return new InteractionDefinition(
            "interaction.typed",
            "1",
            "schema.typed.v1",
            priority: 20,
            availabilityHandlerId: "available",
            costAdmissionHandlerId: "cost",
            participantSelectorId: "selector",
            resolverId: "resolver",
            effectHandlerId: "effect",
            readResourceKeys: new[] { "actor:state" },
            writeResourceKeys: new[] { "target:state" },
            details: new InteractionDefinitionDetails(
                "content-1",
                parameterContract,
                new InteractionTargetContract(
                    "target.entity.v1",
                    minimumTargets: 1,
                    maximumTargets: 1),
                channelIds: new[] { "local" }));
    }

    private static InteractionExecutionRequest Request(string catalogDigest)
    {
        return new InteractionExecutionRequest(
            "command-11",
            "operation-11",
            "world",
            "timeline",
            1,
            expectedSaveRevision: 11,
            expectedStateVersion: "state-11",
            catalogDigest,
            "interaction.typed",
            "1",
            new GameEntityIdentity("actor", 2),
            new[] { new GameEntityIdentity("target", 4) },
            "local",
            Json("""{"choice":"east","amount":"1.25"}"""));
    }

    private static WorldStateFence Fence(string catalogDigest)
    {
        return new WorldStateFence(
            "world",
            "timeline",
            1,
            saveRevision: 11,
            stateVersion: "state-11",
            catalogDigest);
    }

    private static WorldPackageDefinition Package()
    {
        return new WorldPackageDefinition(
            "package",
            "1.0.0",
            new[]
            {
                new WorldPackageFile(
                    "content/world.json",
                    "application/json",
                    Encoding.UTF8.GetBytes(
                        """{"kind":"fixture","revision":"1"}"""))
            });
    }

    private static WorldPackageDefinition NativeFixturePackage()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "world-v1",
            "interactive-smoke");
        var files = Directory.GetFiles(root, "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(
                path => new WorldPackageFile(
                    Path.GetFileName(path),
                    "application/json",
                    File.ReadAllBytes(path)))
            .ToArray();
        Assert.Equal(7, files.Length);
        return new WorldPackageDefinition(
            "unity-native-e2e",
            "1",
            files);
    }

    private static WorldAuthoritativeStateSnapshot RequireSnapshot(
        WorldAuthoritativeStateSnapshot? snapshot)
    {
        return snapshot
               ?? throw new InvalidOperationException(
                   "The Unity native-world snapshot is missing.");
    }

    private static string Trust(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        return snapshot.State.GetProperty("entities")
            .GetProperty("mira")
            .GetProperty("trust")
            .GetString()
               ?? throw new InvalidOperationException(
                   "The native-world trust value is missing.");
    }

    private static WorldSaveDocument Save(
        WorldPackageDefinition package)
    {
        return new WorldSaveDocument(
            package.PackageId,
            package.ContentVersion,
            package.PackageDigest,
            "world",
            "timeline",
            saveRevision: 11,
            stateVersion: "state-11",
            new[] { new WorldClockSnapshot("clock", 1, 42) },
            Json("""{"values":{"amount":"1.25"}}"""),
            Json("[]"),
            Json("[]"));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class Condition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(true);
        }
    }

    private sealed class Admission : IWorldEventAdmissionHandler
    {
        public ValueTask<WorldEventAdmissionDecision> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventAdmissionDecision>(
                WorldEventAdmissionDecision.Accept());
        }
    }

    private sealed class Selector : IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventParticipant> participants =
                new[]
                {
                    new WorldEventParticipant("actor", 2, "actor"),
                    new WorldEventParticipant("target", 4, "target")
                };
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                participants);
        }
    }

    private sealed class Resolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> selectedParticipants,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventResolution> resolutions =
                new[]
                {
                    new WorldEventResolution(
                        "typed-resolution",
                        selectedParticipants)
                };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                resolutions);
        }
    }

    private sealed class Effect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "applied"));
        }
    }
}
