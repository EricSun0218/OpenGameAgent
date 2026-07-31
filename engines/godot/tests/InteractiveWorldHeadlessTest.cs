using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.Godot.Tests;

public partial class InteractiveWorldHeadlessTest : global::Godot.Node
{
    public override async void _Ready()
    {
        try
        {
            await ToSignal(
                GetTree(),
                global::Godot.SceneTree.SignalName.ProcessFrame);
            await RunAssertionsAsync();
            global::Godot.GD.Print("GODOT_WORLD_TEST_PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            global::Godot.GD.PushError(
                $"GODOT_WORLD_TEST_FAIL {exception}");
            GetTree().Quit(1);
        }
    }

    private async Task RunAssertionsAsync()
    {
        var node = GetNode<GodotInteractiveWorldNode>(
            "/root/GameAgentRuntime/InteractiveWorld");
        var portable = CreatePortableFacade();
        node.Configure(portable);

        VerifyArtifactRoundTrip(node, portable);
        await VerifyTypedInteractionParityAndPumpAsync(
            node,
            portable);
        await VerifyStaleFencesAsync(node);
    }

    private static void VerifyArtifactRoundTrip(
        GodotInteractiveWorldNode node,
        InteractiveWorldFacade portable)
    {
        var package = Package();
        var save = Save(package);
        var packageBytes = portable.ExportPackage(package);
        var imported = node.ImportPackage(packageBytes);
        Assert(
            imported.PackageDigest == package.PackageDigest,
            "Godot package import changed the semantic digest.");
        Assert(
            node.ExportPackage().SequenceEqual(packageBytes),
            "Godot package export was not deterministic.");

        node.SetSave(save);
        var saveBytes = node.ExportSave();
        var importedSave = node.ImportSave(saveBytes);
        Assert(
            importedSave.SaveDigest == save.SaveDigest,
            "Godot save import changed the semantic digest.");

        var packagePath = "user://interactive-world-test.gaworld";
        var savePath = "user://interactive-world-test.save.json";
        var packageFile = global::Godot.ProjectSettings.GlobalizePath(
            packagePath);
        var saveFile = global::Godot.ProjectSettings.GlobalizePath(savePath);
        try
        {
            node.ExportPackageFile(packagePath);
            node.ExportSaveFile(savePath);
            Assert(
                node.ImportPackageFile(packagePath).PackageDigest
                == package.PackageDigest,
                "Godot package file round-trip failed.");
            Assert(
                node.ImportSaveFile(savePath).SaveDigest
                == save.SaveDigest,
                "Godot save file round-trip failed.");
        }
        finally
        {
            if (File.Exists(packageFile))
            {
                File.Delete(packageFile);
            }

            if (File.Exists(saveFile))
            {
                File.Delete(saveFile);
            }
        }
    }

    private static async Task VerifyTypedInteractionParityAndPumpAsync(
        GodotInteractiveWorldNode node,
        InteractiveWorldFacade portable)
    {
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            generation: 7,
            new[] { Interaction() });
        var request = Request(catalog.Digest);
        var fence = Fence(catalog.Digest);
        var portableResult = await portable.PlanInteractionAsync(
            catalog,
            request,
            fence);
        var godotResult = await node.Typed.PlanInteractionAsync(
            catalog,
            request,
            fence);
        Assert(
            portableResult.Succeeded && godotResult.Succeeded,
            "Typed interaction planning was unexpectedly rejected.");
        var portableInstance = portableResult.Value!.Plan.Instances.Single();
        var godotInstance = godotResult.Value!.Plan.Instances.Single();
        Assert(
            portableInstance.InstanceId == godotInstance.InstanceId
            && portableInstance.PlanFingerprint
            == godotInstance.PlanFingerprint,
            "Godot planning diverged from the portable world plan.");
        Assert(
            godotResult.Value.Compilation.Trigger.Parameters
                .GetProperty("amount")
                .GetString() == "1.25",
            "Structured non-language input was not preserved.");

        var completion = new TaskCompletionSource<
            WorldBackgroundOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mainThreadId = global::Godot.OS.GetMainThreadId();
        void OnCompleted(WorldBackgroundOperationResult result)
        {
            if (result.OperationId != "godot-typed-operation")
            {
                return;
            }

            Assert(
                global::Godot.OS.GetThreadCallerId() == mainThreadId,
                "Godot published a world result off the main thread.");
            completion.TrySetResult(result);
        }

        node.TypedOperationCompleted += OnCompleted;
        try
        {
            Assert(
                node.TryScheduleInteraction(
                    "godot-typed-operation",
                    catalog,
                    request,
                    fence,
                    out var rejection),
                "Godot did not schedule typed interaction: " + rejection);
            var result = await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert(
                result.Succeeded,
                "Godot background interaction planning failed.");
            var scheduled = (InteractiveWorldResult<WorldInteractionPlan>)
                result.Value!;
            Assert(
                scheduled.Value!.Plan.Instances.Single().InstanceId
                == godotInstance.InstanceId,
                "Godot background planning changed occurrence identity.");
        }
        finally
        {
            node.TypedOperationCompleted -= OnCompleted;
        }
    }

    private static async Task VerifyStaleFencesAsync(
        GodotInteractiveWorldNode node)
    {
        var catalog = new InteractionCatalogSnapshot(
            "catalog",
            generation: 8,
            new[] { Interaction() });
        var request = Request(catalog.Digest);
        var staleState = await node.Typed.PlanInteractionAsync(
            catalog,
            request,
            new WorldStateFence(
                "world",
                "timeline",
                1,
                saveRevision: 10,
                stateVersion: "state-10",
                catalog.Digest));
        Assert(
            !staleState.Succeeded
            && staleState.ReasonCode
            == InteractiveWorldReasonCodes.StaleState,
            "Godot did not reject a stale state fence.");

        var staleCatalog = await node.Typed.PlanInteractionAsync(
            catalog,
            request,
            Fence(new string('a', 64)));
        Assert(
            !staleCatalog.Succeeded
            && staleCatalog.ReasonCode
            == InteractiveWorldReasonCodes.StaleCatalog,
            "Godot did not reject a stale catalog fence.");

        var staleRequest = await node.Typed.PlanInteractionAsync(
            catalog,
            Request(new string('b', 64)),
            Fence(catalog.Digest));
        Assert(
            !staleRequest.Succeeded
            && staleRequest.ReasonCode
            == InteractionReasonCodes.StaleCatalog,
            "Godot did not reject a stale interaction request.");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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
