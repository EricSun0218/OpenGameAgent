using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class BehaviorLearningExtensionTests
{
    [Fact]
    public async Task VersionTwoDescriptorAndConfiguredCompositeBoundsReachTheAdvertisedSchema()
    {
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions
            {
                MaximumInstructionCharacters = 321,
                MaximumInputTypes = 2,
                MaximumToolNames = 3,
                MaximumSteps = 4,
                MaximumEvidenceItems = 5,
            },
            inRunPolicy: _ => true);
        var provider = new ScriptedProvider(new[] { Text("schema") });

        await RunAsync(new InMemoryGameSessionStore(), extension, provider, Input("schema"));

        Assert.Equal("2.0.0", extension.Descriptor.Version);
        var schema = Assert.Single(Assert.Single(provider.Requests).Tools).InputSchemaJson;
        Assert.Contains("\"instructions\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":321}", schema, StringComparison.Ordinal);
        Assert.Contains("\"inputTypes\":{\"type\":\"array\",\"maxItems\":2", schema, StringComparison.Ordinal);
        Assert.Contains("\"toolNames\":{\"type\":\"array\",\"maxItems\":3", schema, StringComparison.Ordinal);
        Assert.Contains("\"steps\":{\"type\":\"array\",\"maxItems\":4", schema, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":5", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedCompositeProposalRejectsDuplicateAndUndeclaredSteps()
    {
        var evidence = new[] { new GameBehaviorEvidence("receipt", "receipt-1") };
        Assert.Throws<ArgumentException>(() => new GameBehaviorLearningProposal(
            "duplicate-steps",
            "Duplicate steps",
            "This proposal is invalid.",
            GameLearnedBehaviorScope.Actor,
            Reflection(),
            evidence,
            toolNames: new[] { "move" },
            steps: new[]
            {
                new GameBehaviorStep("same", "move", "Move once."),
                new GameBehaviorStep("same", "move", "Move twice."),
            }));
        Assert.Throws<ArgumentException>(() => new GameBehaviorLearningProposal(
            "undeclared-step",
            "Undeclared step",
            "This proposal is invalid.",
            GameLearnedBehaviorScope.Actor,
            Reflection(),
            evidence,
            toolNames: new[] { "move" },
            steps: new[] { new GameBehaviorStep("step", "teleport", "Use an undeclared tool.") }));
        Assert.Throws<ArgumentException>(() => new GameBehaviorLearningProposal(
            "unused-tool",
            "Unused tool",
            "This proposal is invalid.",
            GameLearnedBehaviorScope.Actor,
            Reflection(),
            evidence,
            toolNames: new[] { "move", "inspect" },
            steps: new[] { new GameBehaviorStep("step", "move", "Move without using inspect.") }));
    }

    [Fact]
    public void TypedProposalSupportsTheAdvertisedMaximumAndStopsEnumeratingAtItsRawBound()
    {
        var steps = Enumerable.Range(0, 64)
            .Select(index => new GameBehaviorStep("step-" + index, "move", "Move in the verified order."));
        var proposal = new GameBehaviorLearningProposal(
            "long-composite",
            "Long composite",
            "Execute the validated long procedure.",
            GameLearnedBehaviorScope.Actor,
            Reflection(),
            new[] { new GameBehaviorEvidence("receipt", "receipt-1") },
            toolNames: new[] { "move" },
            steps: steps);
        Assert.Equal(64, proposal.Steps.Count);

        Assert.Throws<ArgumentException>(() => new GameBehaviorReflection(
            "observation",
            "strategy",
            "outcome",
            "applicability",
            ValuesThenThrow(17)));
        Assert.Throws<ArgumentException>(() => new GameBehaviorLearningProposal(
            "bounded-evidence",
            "Bounded evidence",
            "Reject raw input beyond the public bound.",
            GameLearnedBehaviorScope.Actor,
            Reflection(),
            EvidenceThenThrow(65)));
    }

    [Theory]
    [InlineData(".leading-dot")]
    [InlineData("with/slash")]
    [InlineData("行为")]
    public void TypedProposalUsesTheSameStableBehaviorIdGrammarAsTheModelTool(string behaviorId)
    {
        Assert.Throws<ArgumentException>(() => new GameBehaviorLearningProposal(
            behaviorId,
            "Invalid identifier",
            "This proposal must be rejected before it reaches storage.",
            GameLearnedBehaviorScope.WorldGeneration,
            Reflection(),
            new[] { new GameBehaviorEvidence("receipt", "receipt-1") }));
    }

    [Fact]
    public async Task ModelCanOnlyProposeAndHostMustActivateBeforeSkillIsVisible()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        var proposalProvider = new ScriptedProvider(new[]
        {
            Proposal("learn-safe-route", "Use the inspected safe route before moving."),
            Text("proposal recorded"),
        });
        await RunAsync(store, extension, proposalProvider, Input("learn"));

        var proposed = await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken);
        var candidate = Assert.Single(proposed.Behaviors);
        Assert.Equal(GameLearnedBehaviorStatus.Proposed, candidate.Status);

        var beforeActivation = new ScriptedProvider(new[] { Text("before") });
        await RunAsync(store, extension, beforeActivation, Input("before"));
        Assert.DoesNotContain(
            "Use the inspected safe route",
            VisibleText(Assert.Single(beforeActivation.Requests)),
            StringComparison.Ordinal);

        var current = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var activated = await extension.ActivateAsync(
            store,
            Key,
            candidate.BehaviorId,
            candidate.Version,
            current!.Revision,
            boundary,
            TestContext.Current.CancellationToken);
        Assert.True(activated.Changed);
        Assert.Equal(GameLearnedBehaviorStatus.Active, activated.Behavior!.Status);

        var afterActivation = new ScriptedProvider(new[] { Text("after") });
        await RunAsync(store, extension, afterActivation, Input("after"));
        Assert.Contains(
            "Use the inspected safe route",
            VisibleText(Assert.Single(afterActivation.Requests)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedEvidenceFailsClosedAndNeverBecomesASkill()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (request, _) => new ValueTask<bool>(request.Proposal.Evidence.Any(value => value.Reference == "trusted-receipt")),
            inRunPolicy: _ => true);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[]
            {
                Proposal("invented", "Do something based on invented evidence.", "invented-receipt"),
                Text("rejected"),
            }),
            Input("forged"));

        var read = await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GameLearnedBehaviorStatus.Rejected, Assert.Single(read.Behaviors).Status);

        var provider = new ScriptedProvider(new[] { Text("next") });
        await RunAsync(store, extension, provider, Input("next"));
        Assert.DoesNotContain("invented evidence", VisibleText(Assert.Single(provider.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestrictedExecutionScopeHidesProposalToolBeforeModelRequest()
    {
        var store = new InMemoryGameSessionStore();
        var extension = Extension(new GameBehaviorWorldBoundary("world", "save-1", 7));
        var provider = new ScriptedProvider(new[] { Text("no learning") });
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExecutionScope(GameExecutionScope.NoOptionalCapabilities)
            .UseExtension(extension)
            .Build();

        var result = await runtime.RunAsync(Input("restricted"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
        Assert.DoesNotContain(
            Assert.Single(provider.Requests).Tools,
            tool => tool.Name == "propose_behavior_learning");
    }

    [Fact]
    public async Task InRunProposalToolIsOptInEvenWithUnrestrictedExecutionScope()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true));
        var provider = new ScriptedProvider(new[] { Text("no additional tool") });

        await RunAsync(store, extension, provider, Input("default"));

        Assert.DoesNotContain(
            Assert.Single(provider.Requests).Tools,
            tool => tool.Name == "propose_behavior_learning");
    }

    [Fact]
    public async Task DisabledModeHidesToolsAndExistingLearnedSkillsAndRejectsNewProposals()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var enabled = Extension(boundary);
        await RunAsync(
            store,
            enabled,
            new ScriptedProvider(new[] { Proposal("disabled", "This must be hidden when learning is disabled."), Text("done") }),
            Input("learn"));
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        Assert.True((await enabled.ActivateAsync(
            store,
            Key,
            "disabled",
            1,
            session!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);

        var disabled = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions { Mode = GameBehaviorLearningMode.Disabled },
            inRunPolicy: _ => true);
        var provider = new ScriptedProvider(new[] { Text("disabled mode") });
        await RunAsync(store, disabled, provider, Input("disabled-input"));
        var request = Assert.Single(provider.Requests);
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "propose_behavior_learning");
        Assert.DoesNotContain("This must be hidden", VisibleText(request), StringComparison.Ordinal);

        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var revision = session!.Revision;
        var result = await disabled.ProposeAsync(
            store,
            Input("disabled-input"),
            revision,
            boundary,
            new GameBehaviorLearningProposal(
                "never-stored",
                "Never stored",
                "Disabled mode cannot store this.",
                GameLearnedBehaviorScope.WorldGeneration,
                Reflection(),
                new[] { new GameBehaviorEvidence("receipt", "receipt-disabled") }),
            "disabled-review",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameBehaviorLearningMutationStatus.Disabled, result.Status);
        Assert.Equal(revision, (await store.LoadAsync(Key, TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task ValidatedAutoActivateModeReplacesPriorActiveVersionWithoutManualApproval()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions { Mode = GameBehaviorLearningMode.ValidatedAutoActivate },
            inRunPolicy: _ => true);

        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("auto-route", "Use validated route version one."), Text("done") }),
            Input("auto-1"));
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("auto-route", "Use validated route version two."), Text("done") }),
            Input("auto-2"));

        var behaviors = (await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors;
        Assert.Equal(GameLearnedBehaviorStatus.Superseded, behaviors.Single(value => value.Version == 1).Status);
        Assert.Equal(GameLearnedBehaviorStatus.Active, behaviors.Single(value => value.Version == 2).Status);

        var provider = new ScriptedProvider(new[] { Text("uses latest") });
        await RunAsync(store, extension, provider, Input("after-auto"));
        var visible = VisibleText(Assert.Single(provider.Requests));
        Assert.Contains("Use validated route version two", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("Use validated route version one", visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationScopedBehaviorIsHiddenAfterLoadBoundaryChanges()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new MutableBoundary(new GameBehaviorWorldBoundary("world", "save-1", 7));
        var extension = new BehaviorLearningExtension(
            boundary.ReadAsync,
            (_, _) => new ValueTask<bool>(true),
            inRunPolicy: _ => true);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("generation-only", "Only for the original generation."), Text("done") }),
            Input("learn"));
        var proposal = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        Assert.True((await extension.ActivateAsync(
            store,
            Key,
            proposal.BehaviorId,
            proposal.Version,
            session!.Revision,
            boundary.Value,
            TestContext.Current.CancellationToken)).Changed);

        boundary.Value = new GameBehaviorWorldBoundary("world", "save-2", 0);
        var provider = new ScriptedProvider(new[] { Text("new save") });
        await RunAsync(store, extension, provider, Input("new-generation"));

        Assert.DoesNotContain("Only for the original generation", VisibleText(Assert.Single(provider.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedFailedEvaluationsDemoteAndRollbackCanReactivateExactVersion()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions { ConsecutiveFailuresBeforeDemotion = 2 },
            inRunPolicy: _ => true);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("route", "Prefer the verified route."), Text("done") }),
            Input("learn"));
        var behavior = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        Assert.True((await extension.ActivateAsync(
            store, Key, "route", 1, session!.Revision, boundary, TestContext.Current.CancellationToken)).Changed);

        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        Assert.True((await extension.RecordEvaluationAsync(
            store, Key, "route", 1, session!.Revision, false, "eval-1", TestContext.Current.CancellationToken)).Changed);
        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var second = await extension.RecordEvaluationAsync(
            store, Key, "route", 1, session!.Revision, false, "eval-2", TestContext.Current.CancellationToken);
        Assert.Equal(GameLearnedBehaviorStatus.Demoted, second.Behavior!.Status);

        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var rollback = await extension.ActivateAsync(
            store,
            Key,
            "route",
            1,
            session!.Revision,
            new GameBehaviorWorldBoundary("world", "save-1", 12),
            TestContext.Current.CancellationToken);
        Assert.True(rollback.Changed);
        Assert.Equal(GameLearnedBehaviorStatus.Active, rollback.Behavior!.Status);
        Assert.Equal(0, rollback.Behavior.ConsecutiveFailures);

        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var demoted = await extension.DemoteAsync(
            store,
            Key,
            "route",
            1,
            session!.Revision,
            "prepare rewind check",
            TestContext.Current.CancellationToken);
        Assert.True(demoted.Changed);
        session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var rewound = await extension.ActivateAsync(
            store,
            Key,
            "route",
            1,
            session!.Revision,
            new GameBehaviorWorldBoundary("world", "save-1", 6),
            TestContext.Current.CancellationToken);
        Assert.Equal(GameBehaviorLearningMutationStatus.WorldChanged, rewound.Status);
    }

    [Fact]
    public async Task HostMutationsUseSessionCasAndRejectChangedWorldBoundary()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("cas", "CAS protected behavior."), Text("done") }),
            Input("learn"));
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);

        var stale = await extension.ActivateAsync(
            store, Key, "cas", 1, session!.Revision - 1, boundary, TestContext.Current.CancellationToken);
        Assert.Equal(GameBehaviorLearningMutationStatus.RevisionConflict, stale.Status);
        var changedWorld = await extension.ActivateAsync(
            store,
            Key,
            "cas",
            1,
            session.Revision,
            new GameBehaviorWorldBoundary("world", "save-1", 8),
            TestContext.Current.CancellationToken);
        Assert.Equal(GameBehaviorLearningMutationStatus.WorldChanged, changedWorld.Status);
    }

    [Fact]
    public async Task IsolatedReviewerCanProposeWithoutPollutingNpcTranscript()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(store, extension, new ScriptedProvider(new[] { Text("completed task") }), Input("source"));
        var before = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var messageCount = before!.Messages.Count;
        var proposal = new GameBehaviorLearningProposal(
            "reviewed",
            "Reviewed procedure",
            "Use the receipt-backed procedure.",
            GameLearnedBehaviorScope.WorldGeneration,
            Reflection(),
            new[] { new GameBehaviorEvidence("receipt", "trusted-receipt") },
            inputTypes: new[] { "request" });

        var result = await extension.ProposeAsync(
            store,
            Input("source"),
            before.Revision,
            boundary,
            proposal,
            "background-review-1",
            TestContext.Current.CancellationToken);

        Assert.True(result.Changed);
        Assert.Equal(GameLearnedBehaviorStatus.Proposed, result.Behavior!.Status);
        Assert.Equal("background-review-1", result.Behavior.CreatedRunId);
        var after = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        Assert.Equal(messageCount, after!.Messages.Count);

        var duplicate = await extension.ProposeAsync(
            store,
            Input("source"),
            after.Revision,
            boundary,
            proposal,
            "background-review-retry",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameBehaviorLearningMutationStatus.AlreadyExists, duplicate.Status);
    }

    [Fact]
    public async Task IsolatedReviewerCanUseValidatedAutoActivationMode()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions { Mode = GameBehaviorLearningMode.ValidatedAutoActivate });
        var input = Input("auto-review-source");
        await RunAsync(store, extension, new ScriptedProvider(new[] { Text("completed task") }), input);
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);

        var result = await extension.ProposeAsync(
            store,
            input,
            session!.Revision,
            boundary,
            new GameBehaviorLearningProposal(
                "auto-reviewed",
                "Auto reviewed",
                "Use the independently reviewed procedure.",
                GameLearnedBehaviorScope.WorldGeneration,
                Reflection(),
                new[] { new GameBehaviorEvidence("receipt", "auto-review-receipt") }),
            "auto-review-run",
            TestContext.Current.CancellationToken);

        Assert.True(result.Changed);
        Assert.Equal(GameLearnedBehaviorStatus.Active, result.Behavior!.Status);
        var provider = new ScriptedProvider(new[] { Text("next") });
        await RunAsync(store, extension, provider, Input("after-auto-review"));
        Assert.Contains(
            "Use the independently reviewed procedure",
            VisibleText(Assert.Single(provider.Requests)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsolatedReviewerCannotLearnFromUncommittedInput()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(store, extension, new ScriptedProvider(new[] { Text("existing") }), Input("existing"));
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var result = await extension.ProposeAsync(
            store,
            Input("never-ran"),
            session!.Revision,
            boundary,
            new GameBehaviorLearningProposal(
                "uncommitted",
                "Uncommitted",
                "This must not persist.",
                GameLearnedBehaviorScope.WorldGeneration,
                Reflection(),
                new[] { new GameBehaviorEvidence("claim", "not-committed") }),
            "review-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(GameBehaviorLearningMutationStatus.InputNotCommitted, result.Status);
        Assert.Empty((await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
    }

    [Fact]
    public async Task RejectedAuditRetentionIsBoundedWithoutPruningProposals()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(false),
            new BehaviorLearningOptions { MaximumRetainedInactiveVersions = 1 },
            inRunPolicy: _ => true);
        for (var index = 0; index < 3; index++)
        {
            await RunAsync(
                store,
                extension,
                new ScriptedProvider(new[]
                {
                    Proposal("rejected-" + index, "Rejected candidate " + index + "."),
                    Text("done"),
                }),
                Input("input-" + index));
        }

        var read = await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(read.Behaviors);
        Assert.Equal("rejected-2", read.Behaviors[0].BehaviorId);
    }

    [Fact]
    public async Task VersionRetentionDoesNotExhaustLongLivedBehaviorLearning()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            new BehaviorLearningOptions
            {
                MaximumVersionsPerBehavior = 2,
                MaximumRetainedInactiveVersions = 2,
            });

        for (var index = 1; index <= 5; index++)
        {
            var input = Input("source-" + index);
            await RunAsync(store, extension, new ScriptedProvider(new[] { Text("completed") }), input);
            var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
            var proposed = await extension.ProposeAsync(
                store,
                input,
                session!.Revision,
                boundary,
                new GameBehaviorLearningProposal(
                    "evolving-route",
                    "Evolving route",
                    "Use verified route version " + index + ".",
                    GameLearnedBehaviorScope.WorldGeneration,
                    Reflection(),
                    new[] { new GameBehaviorEvidence("receipt", "receipt-" + index) }),
                "review-" + index,
                TestContext.Current.CancellationToken);
            Assert.True(proposed.Changed);
            Assert.Equal(index, proposed.Behavior!.Version);

            session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
            Assert.True((await extension.ActivateAsync(
                store,
                Key,
                "evolving-route",
                index,
                session!.Revision,
                boundary,
                TestContext.Current.CancellationToken)).Changed);
        }

        var retained = (await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors;
        Assert.Equal(new[] { 4, 5 }, retained.Select(value => value.Version));
        Assert.Equal(GameLearnedBehaviorStatus.Active, retained.Single(value => value.Version == 5).Status);
    }

    [Fact]
    public async Task LearnedBehaviorStateIsIsolatedBySessionAndActor()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("private", "Actor-private procedure."), Text("done") }),
            Input("learn"));

        var otherActor = await BehaviorLearningExtension.ReadAsync(
            store,
            new GameSessionKey("session", "other-actor"),
            cancellationToken: TestContext.Current.CancellationToken);
        var otherSession = await BehaviorLearningExtension.ReadAsync(
            store,
            new GameSessionKey("other-session", "actor"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(otherActor.Behaviors);
        Assert.Empty(otherSession.Behaviors);
    }

    [Fact]
    public async Task ConcurrentHostActivationHasSingleCasWinner()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { Proposal("concurrent", "Single winner."), Text("done") }),
            Input("learn"));
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);

        var attempts = await Task.WhenAll(
            extension.ActivateAsync(
                store, Key, "concurrent", 1, session!.Revision, boundary, TestContext.Current.CancellationToken).AsTask(),
            extension.ActivateAsync(
                store, Key, "concurrent", 1, session.Revision, boundary, TestContext.Current.CancellationToken).AsTask());

        Assert.Single(attempts, value => value.Status == GameBehaviorLearningMutationStatus.Changed);
        Assert.Single(attempts, value => value.Status is GameBehaviorLearningMutationStatus.RevisionConflict
            or GameBehaviorLearningMutationStatus.SessionConflict);
    }

    [Fact]
    public async Task StructuredReflectionAndCompositeStepsBecomeASafeProcedureOverExistingTools()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { CompositeProposal(), Text("recorded") }),
            Input("learn-composite"),
            ToolExtension());
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var candidate = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        Assert.Equal("A blocked route was observed.", candidate.Reflection.Observation);
        var step = Assert.Single(candidate.Steps);
        Assert.Equal("move", step.ToolName);
        Assert.True((await extension.ActivateAsync(
            store,
            Key,
            candidate.BehaviorId,
            candidate.Version,
            session!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);

        var provider = new ScriptedProvider(new[] { Text("used") });
        await RunAsync(store, extension, provider, Input("use-composite"), ToolExtension());
        var request = Assert.Single(provider.Requests);
        Assert.Contains("Step 1 (move): Use the inspected alternate tile.", VisibleText(request), StringComparison.Ordinal);
        Assert.Contains(request.Tools, value => value.Name == "move");
    }

    [Fact]
    public async Task ThreeStepCompositeSkillUsesTheNormalAgentLoopAndExistingToolsInOrder()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        var executions = new ConcurrentQueue<string>();
        var tools = CompositeToolExtension(executions);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { BuildCompositeProposal(), Text("recorded") }),
            Input("learn-build-sequence"),
            tools);
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var candidate = Assert.Single((await BehaviorLearningExtension.ReadAsync(
            store,
            Key,
            cancellationToken: TestContext.Current.CancellationToken)).Behaviors);
        Assert.Equal(
            new[] { "collect_resource", "construct_structure", "install_light" },
            candidate.Steps.Select(value => value.ToolName).ToArray());
        Assert.True((await extension.ActivateAsync(
            store,
            Key,
            candidate.BehaviorId,
            candidate.Version,
            session!.Revision,
            boundary,
            TestContext.Current.CancellationToken)).Changed);

        var provider = new ScriptedProvider(new[]
        {
            Call("collect", "collect_resource"),
            Call("construct", "construct_structure"),
            Call("light", "install_light"),
            Text("completed"),
        });
        await RunAsync(store, extension, provider, Input("use-build-sequence"), tools);

        Assert.Equal(
            new[] { "collect_resource", "construct_structure", "install_light" },
            executions.ToArray());
        var firstRequest = provider.Requests.First();
        Assert.Contains("Step 1 (collect_resource)", VisibleText(firstRequest), StringComparison.Ordinal);
        Assert.Contains("Step 2 (construct_structure)", VisibleText(firstRequest), StringComparison.Ordinal);
        Assert.Contains("Step 3 (install_light)", VisibleText(firstRequest), StringComparison.Ordinal);
        Assert.All(
            new[] { "collect_resource", "construct_structure", "install_light" },
            name => Assert.Contains(firstRequest.Tools, value => value.Name == name));
    }

    [Fact]
    public async Task PersistedCompositeWithAnUndeclaredStepToolFailsClosed()
    {
        var store = new InMemoryGameSessionStore();
        var boundary = new GameBehaviorWorldBoundary("world", "save-1", 7);
        var extension = Extension(boundary);
        await RunAsync(
            store,
            extension,
            new ScriptedProvider(new[] { CompositeProposal(), Text("recorded") }),
            Input("learn-corrupt"),
            ToolExtension());
        var session = await store.LoadAsync(Key, TestContext.Current.CancellationToken);
        var state = new Dictionary<string, string>(session!.ExtensionState, StringComparer.Ordinal);
        var behaviorState = Assert.Single(state, value => value.Value.Contains("\"ToolName\":\"move\"", StringComparison.Ordinal));
        state[behaviorState.Key] = behaviorState.Value.Replace(
            "\"ToolName\":\"move\"",
            "\"ToolName\":\"undeclared_tool\"",
            StringComparison.Ordinal);
        var corrupted = new GameSessionSnapshot(
            session.Key,
            checked(session.Revision + 1),
            session.Messages,
            session.ProcessedInputIds,
            session.LastMoment,
            state,
            session.PendingInputId,
            session.UsageLedger);
        Assert.True((await store.SaveAsync(
            corrupted,
            session.Revision,
            TestContext.Current.CancellationToken)).Saved);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await BehaviorLearningExtension.ReadAsync(
                store,
                Key,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static GameSessionKey Key => new("session", "actor");

    private static GameBehaviorReflection Reflection() => new(
        "A completed task produced durable evidence.",
        "Reuse the verified procedure.",
        "The authoritative result succeeded.",
        "Use only when the declared tools and input type are available.");

    private static BehaviorLearningExtension Extension(GameBehaviorWorldBoundary boundary) => new(
        (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
        (_, _) => new ValueTask<bool>(true),
        inRunPolicy: _ => true);

    private static GameInput Input(string inputId) =>
        new("session", "actor", "request", "{}", new GameMoment("world", 1), inputId);

    private static ModelResponse Proposal(
        string behaviorId,
        string instructions,
        string evidence = "trusted-receipt") =>
        new(
            new AgentContent[]
            {
                new ToolCallContent(
                    "proposal-" + behaviorId,
                    "propose_behavior_learning",
                    "{\"behaviorId\":\"" + behaviorId
                    + "\",\"title\":\"Learned " + behaviorId
                    + "\",\"instructions\":\"" + instructions
                    + "\",\"scope\":\"world_generation\",\"inputTypes\":[\"request\"],\"toolNames\":[],\"reflection\":{\"observation\":\"The task completed with durable evidence.\",\"strategy\":\"Reuse the verified procedure.\",\"outcome\":\"The authoritative result succeeded.\",\"applicability\":\"Use for matching requests in this world generation.\"},\"evidence\":[{\"kind\":\"action_receipt\",\"reference\":\""
                    + evidence
                    + "\"}]}"),
            },
            ModelStopReason.ToolUse);

    private static ModelResponse CompositeProposal() => new(
        new AgentContent[]
        {
            new ToolCallContent(
                "proposal-composite",
                "propose_behavior_learning",
                "{\"behaviorId\":\"alternate-route\",\"title\":\"Alternate route\",\"instructions\":\"Use the verified alternate route.\",\"scope\":\"world_generation\",\"inputTypes\":[\"request\"],\"toolNames\":[\"move\"],\"reflection\":{\"observation\":\"A blocked route was observed.\",\"strategy\":\"Choose the inspected alternate tile.\",\"outcome\":\"The actor reached the target.\",\"applicability\":\"Use when the primary route is blocked.\",\"failureModes\":[\"The alternate tile may become blocked.\"]},\"steps\":[{\"stepId\":\"move-alternate\",\"toolName\":\"move\",\"instruction\":\"Use the inspected alternate tile.\"}],\"evidence\":[{\"kind\":\"action_receipt\",\"reference\":\"move-receipt\"}]}")
        },
        ModelStopReason.ToolUse);

    private static ModelResponse BuildCompositeProposal() => new(
        new AgentContent[]
        {
            new ToolCallContent(
                "proposal-build-composite",
                "propose_behavior_learning",
                "{\"behaviorId\":\"build-with-light\",\"title\":\"Build with light\",\"instructions\":\"Complete the verified three-step construction procedure.\",\"scope\":\"world_generation\",\"inputTypes\":[\"request\"],\"toolNames\":[\"collect_resource\",\"construct_structure\",\"install_light\"],\"reflection\":{\"observation\":\"A completed build required resources, construction, and lighting.\",\"strategy\":\"Repeat the verified dependency order.\",\"outcome\":\"The structure and its lighting were committed.\",\"applicability\":\"Use only for matching authorized construction tasks.\",\"failureModes\":[\"A required tool can become unavailable.\"]},\"steps\":[{\"stepId\":\"collect\",\"toolName\":\"collect_resource\",\"instruction\":\"Collect the validated required resources.\"},{\"stepId\":\"construct\",\"toolName\":\"construct_structure\",\"instruction\":\"Construct only after resource success.\"},{\"stepId\":\"light\",\"toolName\":\"install_light\",\"instruction\":\"Install lighting only after construction success.\"}],\"evidence\":[{\"kind\":\"action_receipt\",\"reference\":\"completed-build-receipts\"}]}")
        },
        ModelStopReason.ToolUse);

    private static ModelResponse Call(string callId, string toolName) => new(
        new AgentContent[] { new ToolCallContent(callId, toolName, "{}") },
        ModelStopReason.ToolUse);

    private static IGameAgentExtension ToolExtension() => new DelegateGameAgentExtension(
        new GameAgentExtensionDescriptor("test.move", "1.0.0"),
        api => api.RegisterTool(new AgentTool(
            new ToolDefinition("move", "Move to a validated game location.", "{\"type\":\"object\",\"additionalProperties\":false}"),
            (_, _, _) => new ValueTask<ToolResult>(
                new ToolResult(new AgentContent[] { new TextContent("moved") })),
            ToolRisk.IdempotentWrite)));

    private static IGameAgentExtension CompositeToolExtension(ConcurrentQueue<string> executions) =>
        new DelegateGameAgentExtension(
            new GameAgentExtensionDescriptor("test.composite-tools", "1.0.0"),
            api =>
            {
                foreach (var name in new[] { "collect_resource", "construct_structure", "install_light" })
                {
                    api.RegisterTool(new AgentTool(
                        new ToolDefinition(
                            name,
                            "A host-authorized step in a composite game procedure.",
                            "{\"type\":\"object\",\"additionalProperties\":false}"),
                        (_, _, _) =>
                        {
                            executions.Enqueue(name);
                            return new ValueTask<ToolResult>(
                                new ToolResult(new AgentContent[] { new TextContent(name + " completed") }));
                        },
                        ToolRisk.IdempotentWrite));
                }
            });

    private static ModelResponse Text(string value) =>
        new(new AgentContent[] { new TextContent(value) }, ModelStopReason.Stop);

    private static IEnumerable<string> ValuesThenThrow(int values)
    {
        for (var index = 0; index < values; index++)
        {
            yield return "value-" + index;
        }

        throw new InvalidOperationException("The bounded copy enumerated past its declared limit.");
    }

    private static IEnumerable<GameBehaviorEvidence> EvidenceThenThrow(int values)
    {
        for (var index = 0; index < values; index++)
        {
            yield return new GameBehaviorEvidence("receipt", "receipt-" + index);
        }

        throw new InvalidOperationException("The bounded copy enumerated past its declared limit.");
    }

    private static string VisibleText(ModelRequest request) => request.SystemPrompt + "\n" + string.Join(
        "\n",
        request.Messages.SelectMany(message => message.Content).OfType<TextContent>().Select(value => value.Text));

    private static async Task RunAsync(
        IGameSessionStore store,
        BehaviorLearningExtension extension,
        IModelProvider provider,
        GameInput input,
        params IGameAgentExtension[] additionalExtensions)
    {
        var builder = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(extension);
        foreach (var additional in additionalExtensions)
        {
            builder.UseExtension(additional);
        }

        await using var runtime = builder.Build();
        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
    }

    private sealed class MutableBoundary
    {
        public MutableBoundary(GameBehaviorWorldBoundary value) => Value = value;

        public GameBehaviorWorldBoundary Value { get; set; }

        public ValueTask<GameBehaviorWorldBoundary> ReadAsync(GameInput _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameBehaviorWorldBoundary>(Value);
        }
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelResponse> _responses;
        private int _calls;

        public ScriptedProvider(IReadOnlyList<ModelResponse> responses) => _responses = responses;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var index = Interlocked.Increment(ref _calls) - 1;
            yield return ModelStreamEvent.Terminal(index < _responses.Count ? _responses[index] : Text("done"));
            await Task.CompletedTask;
        }
    }
}
