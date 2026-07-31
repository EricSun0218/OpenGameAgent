using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Tests;

public sealed class RuntimeMemoryIntegrationTests
{
    [Fact]
    public async Task BuilderRecallsUntrustedContextAndCommitsFinalTranscript()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var policy = new RecordingPolicy(recall: true);
        var provider = new CapturingFinalProvider();
        try
        {
            var seedTime = DateTimeOffset.UtcNow;
            await store.UpsertAsync(
                new MemoryRecord(
                    "seed-memory",
                    "agent:agent-1",
                    ProtocolJson.ParseElement(
                        """{"bridge":"closed","source":"guard"}"""),
                    new[] { "bridge" },
                    70,
                    seedTime,
                    seedTime,
                    provenance: new MemoryProvenance(
                        "world-1",
                        "session-1",
                        4,
                        "seed-run",
                        "seed-event",
                        committed: true)),
                CancellationToken.None);

            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(provider)
                .WithRuntimeMemory(memory, policy)
                .Build();

            var outcome = await built.Runtime.RunAsync(
                Request("memory-final-run"));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Contains(
                "memory:untrusted-derived:seed-memory",
                provider.SerializedPrompt,
                StringComparison.Ordinal);
            var commit = Assert.Single(policy.CommitContexts);
            Assert.Empty(commit.Receipts);
            Assert.NotNull(commit.AssistantMessage);
            Assert.True(commit.AssistantOutput.HasValue);
            Assert.Contains(
                commit.CommittedTranscript,
                item => string.Equals(
                    item.Role,
                    NormalizedRoles.Assistant,
                    StringComparison.Ordinal));

            var saved = await store.SearchAsync(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement(
                        """{"remembered":"final"}"""),
                    requiredTags: new[] { "final" },
                    worldId: "world-1",
                    sessionId: "session-1",
                    requireCommittedProvenance: true),
                CancellationToken.None);
            Assert.Equal("final-memory", Assert.Single(saved).Record.MemoryId);

            var events = await built.SessionStore.ReadRunAsync(
                outcome.Run.RunId,
                CancellationToken.None);
            Assert.Contains(
                events,
                item => item.Kind
                        == RuntimeEventKinds.MemoryCommitPrepared);
            Assert.Contains(
                events,
                item => item.Kind
                        == RuntimeEventKinds.MemoryCommitCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalReceiptsDrivePolicyBeforeNextTurn()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var policy = new RecordingPolicy(recall: false);
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new SucceedingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new ToolThenFinalProvider())
                .WithTools(new[] { RememberTool() })
                .WithRuntimeMemory(memory, policy)
                .Build();

            var outcome = await built.Runtime.RunAsync(
                Request("memory-receipt-run"));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(2, policy.CommitContexts.Count);
            var receiptCommit = policy.CommitContexts[0];
            var receipt = Assert.Single(receiptCommit.Receipts);
            Assert.Equal(ReceiptStatuses.Succeeded, receipt.Status);
            Assert.False(receiptCommit.AssistantOutput.HasValue);
            Assert.Contains(
                receiptCommit.CommittedTranscript,
                item => item.Parts.Any(
                    part => string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolResult,
                        StringComparison.Ordinal)));

            var saved = await store.SearchAsync(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement(
                        """{"remembered":"receipt"}"""),
                    requiredTags: new[] { "receipt" },
                    worldId: "world-1",
                    sessionId: "session-1",
                    requireCommittedProvenance: true),
                CancellationToken.None);
            Assert.Equal(
                "receipt-memory",
                Assert.Single(saved).Record.MemoryId);
            var events = await built.SessionStore.ReadRunAsync(
                outcome.Run.RunId,
                CancellationToken.None);
            foreach (var context in policy.CommitContexts)
            {
                Assert.Single(
                    events,
                    item => item.Kind
                            == RuntimeEventKinds.MemoryCommitPrepared
                            && string.Equals(
                                item.TurnId,
                                context.TurnId,
                                StringComparison.Ordinal));
                Assert.Single(
                    events,
                    item => item.Kind
                            == RuntimeEventKinds.MemoryCommitCompleted
                            && string.Equals(
                                item.TurnId,
                                context.TurnId,
                                StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedCommitReplaysWithoutReinvokingPolicy()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new ApplyThenThrowStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var policy = new RecordingPolicy(recall: false);
        const string runId = "memory-outbox-run";
        try
        {
            await using (var first = new GameAgentRuntimeBuilder(
                                     new RejectingHost())
                                 .UseFileJournal(journalPath)
                                 .AddProvider(new CapturingFinalProvider())
                                 .WithRuntimeMemory(memory, policy)
                                 .Build())
            {
                var failed = await first.Runtime.RunAsync(Request(runId));
                Assert.Equal(RunStates.Failed, failed.Run.State);
                Assert.Equal(
                    RuntimeMemoryIntegrationReasonCodes.CommitFailed,
                    failed.ErrorCode);
            }

            var replayProvider = new CapturingFinalProvider();
            var replayPolicy = new RecordingPolicy(
                recall: false,
                version: "2.0.0");
            await using (var recovered = new GameAgentRuntimeBuilder(
                                           new RejectingHost())
                                       .UseFileJournal(journalPath)
                                       .AddProvider(replayProvider)
                                       .WithRuntimeMemory(memory, replayPolicy)
                                       .Build())
            {
                var outcome = await recovered.Runtime.ResumeAsync(runId);

                Assert.Equal(RunStates.Failed, outcome.Run.State);
                Assert.Equal(0, replayProvider.CallCount);
                Assert.Equal(1, policy.SelectCallCount);
                Assert.Equal(0, replayPolicy.SelectCallCount);
                Assert.Equal(2, store.ApplyCallCount);
                var events = await recovered.SessionStore.ReadRunAsync(
                    runId,
                    CancellationToken.None);
                Assert.Single(
                    events,
                    item => item.Kind
                            == RuntimeEventKinds.MemoryCommitPrepared);
                Assert.Single(
                    events,
                    item => item.Kind
                            == RuntimeEventKinds.MemoryCommitCompleted);
            }

            var secondReplayPolicy = new RecordingPolicy(
                recall: false,
                version: "3.0.0");
            await using (var secondRecovery = new GameAgentRuntimeBuilder(
                                           new RejectingHost())
                                       .UseFileJournal(journalPath)
                                       .AddProvider(new CapturingFinalProvider())
                                       .WithRuntimeMemory(
                                           memory,
                                           secondReplayPolicy)
                                       .Build())
            {
                var outcome = await secondRecovery.Runtime.ResumeAsync(runId);
                Assert.Equal(RunStates.Failed, outcome.Run.State);
                Assert.Equal(2, store.ApplyCallCount);
                Assert.Equal(0, secondReplayPolicy.SelectCallCount);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderOwnsAndDrainsConfiguredMemoryLifecycle()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseFileJournal(journalPath)
            .AddProvider(new CapturingFinalProvider())
            .WithRuntimeMemory(
                memory,
                new RecordingPolicy(recall: false),
                disposeOnShutdown: true)
            .Build();
        try
        {
            _ = await built.Runtime.RunAsync(Request("owned-memory-run"));
            await built.DisposeAsync();

            Assert.True(built.OwnsMemoryLifecycle);
            Assert.True(built.MemoryProviderCallsDrainedOnStop);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => memory.RecallAsync(
                        new MemoryQuery(
                            "agent:agent-1",
                            ProtocolJson.ParseElement("{}")),
                        CancellationToken.None)
                    .AsTask());
        }
        finally
        {
            await built.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyMutationDecisionUsesOneDurableSettlement()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var policy = new EmptyPolicy();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new CapturingFinalProvider())
                .WithRuntimeMemory(memory, policy)
                .Build();

            var outcome = await built.Runtime.RunAsync(
                Request("empty-memory-run"));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, policy.SelectCallCount);
            var events = await built.SessionStore.ReadRunAsync(
                outcome.Run.RunId,
                CancellationToken.None);
            Assert.DoesNotContain(
                events,
                item => item.Kind == RuntimeEventKinds.MemoryCommitPrepared);
            Assert.DoesNotContain(
                events,
                item => item.Kind == RuntimeEventKinds.MemoryCommitCompleted);
            Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.MemoryCommitSettled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SettledDecisionIsNotReinvokedAfterCrashWindow()
    {
        var directory = TempDirectory();
        var sourcePath = Path.Combine(directory, "source.journal");
        var crashPath = Path.Combine(directory, "crash.journal");
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        const string runId = "empty-memory-crash-run";
        try
        {
            IReadOnlyList<RuntimeEvent> sourceEvents;
            await using (var first = new GameAgentRuntimeBuilder(
                                     new RejectingHost())
                                 .UseFileJournal(sourcePath)
                                 .AddProvider(new CapturingFinalProvider())
                                 .WithRuntimeMemory(memory, new EmptyPolicy())
                                 .Build())
            {
                var outcome = await first.Runtime.RunAsync(Request(runId));
                Assert.Equal(RunStates.Completed, outcome.Run.State);
                sourceEvents = await first.SessionStore.ReadRunAsync(
                    runId,
                    CancellationToken.None);
            }

            var settlementIndex = sourceEvents
                .Select((item, index) => (item, index))
                .Single(
                    pair => pair.item.Kind
                            == RuntimeEventKinds.MemoryCommitSettled)
                .index;
            await using var crashStore = new FileSessionStore(crashPath);
            long revision = 0;
            for (var index = 0; index <= settlementIndex; index++)
            {
                var clone = ProtocolJson.DeserializeRuntimeEvent(
                    ProtocolJson.Serialize(sourceEvents[index]));
                var appended = await crashStore.AppendAtomicAsync(
                    clone,
                    revision,
                    CancellationToken.None);
                revision = appended.Revision;
            }

            var policy = new MustNotSelectPolicy();
            await using var recovered = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseDurableStore(crashStore, crashStore)
                .AddProvider(new CapturingFinalProvider())
                .WithRuntimeMemory(memory, policy)
                .Build();

            var resumed = await recovered.Runtime.ResumeAsync(runId);

            Assert.Equal(RunStates.Completed, resumed.Run.State);
            Assert.Equal(0, policy.SelectCallCount);
            var recoveredEvents = await crashStore.ReadRunAsync(
                runId,
                CancellationToken.None);
            Assert.Single(
                recoveredEvents,
                item => item.Kind == RuntimeEventKinds.MemoryCommitSettled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PolicyExceptionDoesNotRetainArbitraryInnerException()
    {
        const string canary = "CANARY_SECRET_MUST_NOT_ESCAPE";
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new ThrowingPolicy(canary),
            options: null);
        var request = Request("memory-policy-canary");

        var error = await Assert.ThrowsAsync<RuntimeMemoryIntegrationException>(
            () => loop.RecallAsync(
                    request.Run,
                    "turn-canary",
                    request.InitialTranscript,
                    Array.Empty<ContextCandidate>(),
                    maximumContextCandidates: 1,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            RuntimeMemoryIntegrationReasonCodes.PolicyError,
            error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("session")]
    [InlineData("future_revision")]
    [InlineData("timeline")]
    [InlineData("future_game_time")]
    [InlineData("foreign_observer")]
    [InlineData("all_perspectives")]
    public async Task RecallPolicyCannotEscapeTheCurrentGameCoordinate(
        string boundary)
    {
        var query = boundary switch
        {
            "world" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                worldId: "world-2"),
            "session" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                sessionId: "session-2"),
            "future_revision" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                maximumSaveRevision: 13),
            "timeline" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                timelineId: "timeline-fork"),
            "future_game_time" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                gameTime: new GameTimePoint(
                    "world-clock",
                    "timeline-main",
                    epoch: 2,
                    tick: 101)),
            "foreign_observer" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                observer: new GameEntityIdentity("npc-2", 1)),
            "all_perspectives" => new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("{}"),
                includeAllPerspectives: true),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var provider = new IgnoringQueryMemoryProvider(
            Array.Empty<MemorySearchResult>());
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { provider });
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new FixedRecallPolicy(query),
            options: null);
        var request = Request("memory-policy-boundary-" + boundary);
        GameContextEnvelope.Attach(request.Run, Coordinate());

        var error = await Assert.ThrowsAsync<RuntimeMemoryIntegrationException>(
            () => loop.RecallAsync(
                    request.Run,
                    "turn-boundary",
                    request.InitialTranscript,
                    Array.Empty<ContextCandidate>(),
                    maximumContextCandidates: 8,
                    CancellationToken.None)
                .AsTask());

        Assert.True(
            string.Equals(
                RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                error.ReasonCode,
                StringComparison.Ordinal),
            $"{boundary} returned reason code '{error.ReasonCode}'.");
        Assert.True(
            provider.SearchCallCount == 0,
            $"{boundary} reached the memory provider before rejection.");
    }

    [Fact]
    public async Task IgnoringProviderCannotLeakOtherWorldOrSessionIntoPrompt()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var provider = new IgnoringQueryMemoryProvider(
            new[]
            {
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "current-session",
                        "world-1",
                        "session-1",
                        "current session memory"),
                    score: 400),
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "world-global",
                        "world-1",
                        sessionId: null,
                        "world global memory"),
                    score: 300),
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "other-session",
                        "world-1",
                        "session-2",
                        "other session secret"),
                    score: 200),
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "other-world",
                        "world-2",
                        "session-1",
                        "other world secret"),
                    score: 100)
            });
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { provider });
        var policy = new FixedRecallPolicy(
            new MemoryQuery(
                "agent:agent-1",
                ProtocolJson.ParseElement("""{"topic":"bridge"}"""),
                requireCommittedProvenance: true));
        var model = new CapturingFinalProvider();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(model)
                .WithRuntimeMemory(memory, policy)
                .Build();
            var request = Request("memory-provider-boundary");
            GameContextEnvelope.Attach(request.Run, Coordinate());

            var outcome = await built.Runtime.RunAsync(request);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, provider.SearchCallCount);
            Assert.Equal("world-1", provider.LastQuery!.WorldId);
            Assert.Null(provider.LastQuery.SessionId);
            Assert.Equal(12, provider.LastQuery.MaximumSaveRevision);
            Assert.Equal("timeline-main", provider.LastQuery.TimelineId);
            Assert.True(
                Coordinate().Observer!.IsSameIncarnation(
                    provider.LastQuery.Observer));
            Assert.Equal(100, provider.LastQuery.GameTime!.Tick);
            Assert.Contains(
                "memory:untrusted-derived:current-session",
                model.SerializedPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "memory:untrusted-derived:world-global",
                model.SerializedPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "memory:untrusted-derived:other-session",
                model.SerializedPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "memory:untrusted-derived:other-world",
                model.SerializedPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "other session secret",
                model.SerializedPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "other world secret",
                model.SerializedPrompt,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableRunRequest Request(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = runId,
                AgentId = "agent-1",
                WorldId = "world-1",
                SessionId = "session-1",
                State = RunStates.Queued,
                CreatedAt = now,
                UpdatedAt = now
            },
            InitialTranscript = new[]
            {
                new NormalizedMessage
                {
                    MessageId = runId + "-user",
                    Role = NormalizedRoles.User,
                    CreatedAt = now,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText(
                            "Decide what happens next.")
                    }
                }
            }
        };
    }

    private static GameContextCoordinate Coordinate()
    {
        return new GameContextCoordinate(
            "world-1",
            "timeline-main",
            saveRevision: 12,
            observer: new GameEntityIdentity("npc-1", 2),
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-main",
                epoch: 2,
                tick: 100));
    }

    private static MemoryRecord BoundMemoryRecord(
        string memoryId,
        string worldId,
        string? sessionId,
        string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemoryRecord(
            memoryId,
            "agent:agent-1",
            ProtocolJson.ParseElement(
                $$"""{"text":"{{text}}"}"""),
            Array.Empty<string>(),
            importance: 50,
            now,
            now,
            provenance: new MemoryProvenance(
                worldId,
                sessionId,
                saveRevision: 10,
                sourceRunId: "source-" + memoryId,
                sourceEventId: "event-" + memoryId,
                committed: true,
                timelineId: "timeline-main"));
    }

    private static ToolDescriptor RememberTool()
    {
        return new ToolDescriptor
        {
            Name = "remember_action",
            Version = "1",
            Description = "Returns one authoritative host receipt.",
            ParametersSchema = ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "world" },
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            TimeoutMs = 1_000
        };
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "runtime-memory-integration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingPolicy : IRuntimeMemoryPolicy
    {
        private readonly bool _recall;
        private readonly string _version;
        private int _selectCalls;

        public RecordingPolicy(bool recall, string version = "1.0.0")
        {
            _recall = recall;
            _version = version;
        }

        public string PolicyId => "test-memory-policy";

        public string Version => _version;

        public List<RuntimeMemoryCommitContext> CommitContexts { get; } =
            new();

        public int SelectCallCount => Volatile.Read(ref _selectCalls);

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return !_recall
                ? null
                : new RuntimeMemoryRecallPlan(
                    new MemoryQuery(
                        "agent:" + context.AgentId,
                        ProtocolJson.ParseElement(
                            """{"bridge":"closed"}"""),
                        requiredTags: new[] { "bridge" },
                        worldId: context.WorldId,
                        sessionId: context.SessionId,
                        requireCommittedProvenance: true));
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            Interlocked.Increment(ref _selectCalls);
            CommitContexts.Add(context);
            var now = DateTimeOffset.UtcNow;
            if (context.Receipts.Count > 0)
            {
                return new[]
                {
                    MemoryMutation.Upsert(
                        Record(
                            "receipt-memory",
                            "receipt",
                            context,
                            now))
                };
            }

            if (context.AssistantOutput.HasValue)
            {
                return new[]
                {
                    MemoryMutation.Upsert(
                        Record(
                            "final-memory",
                            "final",
                            context,
                            now))
                };
            }

            return Array.Empty<MemoryMutation>();
        }

        private static MemoryRecord Record(
            string memoryId,
            string kind,
            RuntimeMemoryCommitContext context,
            DateTimeOffset now)
        {
            return new MemoryRecord(
                memoryId,
                "agent:" + context.AgentId,
                ProtocolJson.ParseElement(
                    $$"""{"remembered":"{{kind}}"}"""),
                new[] { kind },
                50,
                now,
                now,
                provenance: new MemoryProvenance(
                    context.WorldId,
                    context.SessionId,
                    context.Coordinate?.SaveRevision ?? 0,
                    context.RunId,
                    context.CommittedSourceEventIds[0],
                    committed: true,
                    timelineId: context.Coordinate?.TimelineId));
        }
    }

    private sealed class FixedRecallPolicy : IRuntimeMemoryPolicy
    {
        private readonly MemoryQuery _query;

        public FixedRecallPolicy(MemoryQuery query)
        {
            _query = query;
        }

        public string PolicyId => "fixed-recall-policy";

        public string Version => "1.0.0";

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return new RuntimeMemoryRecallPlan(_query);
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class IgnoringQueryMemoryProvider : IMemoryProvider
    {
        private readonly IReadOnlyList<MemorySearchResult> _results;
        private int _searchCalls;

        public IgnoringQueryMemoryProvider(
            IReadOnlyList<MemorySearchResult> results)
        {
            _results = results;
        }

        public string ProviderId => "ignoring-query";

        public int SearchCallCount => Volatile.Read(ref _searchCalls);

        public MemoryQuery? LastQuery { get; private set; }

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _searchCalls);
            LastQuery = query;
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(_results);
        }
    }

    private sealed class EmptyPolicy : IRuntimeMemoryPolicy
    {
        private int _selectCalls;

        public string PolicyId => "empty-memory-policy";

        public string Version => "1.0.0";

        public int SelectCallCount => Volatile.Read(ref _selectCalls);

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            Interlocked.Increment(ref _selectCalls);
            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class ThrowingPolicy : IRuntimeMemoryPolicy
    {
        private readonly string _canary;

        public ThrowingPolicy(string canary)
        {
            _canary = canary;
        }

        public string PolicyId => "throwing-memory-policy";

        public string Version => "1.0.0";

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            throw new InvalidOperationException(_canary);
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            throw new InvalidOperationException(_canary);
        }
    }

    private sealed class MustNotSelectPolicy : IRuntimeMemoryPolicy
    {
        private int _selectCalls;

        public string PolicyId => "empty-memory-policy";

        public string Version => "1.0.0";

        public int SelectCallCount => Volatile.Read(ref _selectCalls);

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            Interlocked.Increment(ref _selectCalls);
            throw new InvalidOperationException(
                "A durable settlement must prevent policy reinvocation.");
        }
    }

    private sealed class CapturingFinalProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "capturing-final";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public int CallCount => Volatile.Read(ref _callCount);

        public string SerializedPrompt { get; private set; } = string.Empty;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            SerializedPrompt = string.Join(
                "\n",
                request.Messages.Select(
                    message => NormalizedMessageJournalCodec
                        .Encode(message)
                        .GetRawText()));
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"done\""
            };
            await Task.Yield();
            yield return Usage(request.StreamAttemptId, 1);
            yield return Completed(request.StreamAttemptId, 2, "stop");
        }
    }

    private sealed class ToolThenFinalProvider : IStreamingModelProvider
    {
        private int _calls;

        public string ProviderId => "tool-then-final";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "remember-call",
                    ToolNameDelta = "remember_action",
                    ArgumentsJsonDelta = "{}"
                };
                await Task.Yield();
                yield return Usage(request.StreamAttemptId, 1);
                yield return Completed(
                    request.StreamAttemptId,
                    2,
                    "tool_calls");
                yield break;
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"done\""
            };
            await Task.Yield();
            yield return Usage(request.StreamAttemptId, 1);
            yield return Completed(request.StreamAttemptId, 2, "stop");
        }
    }

    private static ModelStreamEvent Usage(string streamAttemptId, long ordinal)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = streamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage
            {
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = "0"
            }
        };
    }

    private static ModelStreamEvent Completed(
        string streamAttemptId,
        long ordinal,
        string finishReason)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = streamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = finishReason
        };
    }

    private sealed class RejectingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No host action expected.");
        }
    }

    private sealed class SucceedingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"host":"committed"}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }

    private sealed class ApplyThenThrowStore :
        IIdempotentAtomicMemoryBatchStore
    {
        private readonly DeterministicMemoryStore _inner = new();
        private int _applyCalls;

        public string ProviderId => "apply-then-throw";

        public int ApplyCallCount => Volatile.Read(ref _applyCalls);

        public ValueTask UpsertAsync(
            MemoryRecord record,
            CancellationToken cancellationToken)
        {
            return _inner.UpsertAsync(record, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(
            string memoryId,
            CancellationToken cancellationToken)
        {
            return _inner.DeleteAsync(memoryId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            return _inner.SearchAsync(query, cancellationToken);
        }

        public async ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyAtomicBatchAsync(
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _applyCalls);
            var result = await _inner.ApplyAtomicBatchAsync(
                mutations,
                cancellationToken);
            if (call == 1)
            {
                throw new IOException(
                    "The store acknowledged internally before transport loss.");
            }

            return result;
        }

        public async ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyIdempotentAtomicBatchAsync(
                string commitId,
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _applyCalls);
            var result = await _inner.ApplyIdempotentAtomicBatchAsync(
                commitId,
                mutations,
                cancellationToken);
            if (call == 1)
            {
                throw new IOException(
                    "The store acknowledged internally before transport loss.");
            }

            return result;
        }
    }
}
