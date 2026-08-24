using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class BehaviorLearningExtensionTests
{
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
            .UseExecutionScope(GameExecutionScope.ShortTaskOnly)
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

    private static GameSessionKey Key => new("session", "actor");

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
                    + "\",\"scope\":\"world_generation\",\"inputTypes\":[\"request\"],\"toolNames\":[],\"evidence\":[{\"kind\":\"action_receipt\",\"reference\":\""
                    + evidence
                    + "\"}]}"),
            },
            ModelStopReason.ToolUse);

    private static ModelResponse Text(string value) =>
        new(new AgentContent[] { new TextContent(value) }, ModelStopReason.Stop);

    private static string VisibleText(ModelRequest request) => request.SystemPrompt + "\n" + string.Join(
        "\n",
        request.Messages.SelectMany(message => message.Content).OfType<TextContent>().Select(value => value.Text));

    private static async Task RunAsync(
        IGameSessionStore store,
        BehaviorLearningExtension extension,
        IModelProvider provider,
        GameInput input)
    {
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(extension)
            .Build();
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
