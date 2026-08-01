using System.Runtime.CompilerServices;
using System.Text.Json;
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
            var turnStarted = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.TurnSnapshot);
            var turnSnapshot = ProtocolJson.DeserializeTurnSnapshot(
                turnStarted.Payload.GetRawText());
            var recallEvidence = turnSnapshot.Extensions["memoryRecall"];
            Assert.Equal(
                MemoryRankingModes.RawScore,
                recallEvidence.GetProperty("rankingMode").GetString());
            var candidateEvidence = Assert.Single(
                recallEvidence.GetProperty("candidateEvidence")
                    .EnumerateArray());
            Assert.Equal(
                recallEvidence.GetProperty("selectedIds")[0].GetString(),
                candidateEvidence.GetProperty("candidateId").GetString());
            Assert.Equal(
                1,
                candidateEvidence.GetProperty("providerCount").GetInt32());
            Assert.False(
                recallEvidence.GetProperty("candidateEvidenceTruncated")
                    .GetBoolean());
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
    [InlineData("bare")]
    [InlineData("foreign")]
    public async Task RuntimeManagedDeleteRequiresCurrentWorldExpectation(
        string deleteKind)
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        var protectedRecord = BoundMemoryRecord(
            "protected-memory",
            deleteKind == "foreign" ? "world-2" : "world-1",
            "session-1",
            "protected value");
        await store.UpsertAsync(protectedRecord, CancellationToken.None);
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var mutation = deleteKind == "foreign"
            ? MemoryMutation.Delete(protectedRecord)
            : MemoryMutation.Delete(protectedRecord.MemoryId);
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new CapturingFinalProvider())
                .WithRuntimeMemory(
                    memory,
                    new MutationPolicy(_ => new[] { mutation }))
                .Build();

            var outcome = await built.Runtime.RunAsync(
                Request("memory-delete-guard-" + deleteKind));

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                outcome.ErrorCode);
            var retained = await store.SearchAsync(
                new MemoryQuery(
                    protectedRecord.Scope,
                    ProtocolJson.ParseElement("{}"),
                    worldId: protectedRecord.Provenance!.WorldId,
                    sessionId: protectedRecord.Provenance.SessionId,
                    requireCommittedProvenance: true),
                CancellationToken.None);
            Assert.Equal(
                protectedRecord.MemoryId,
                Assert.Single(retained).Record.MemoryId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeManagedUpsertCannotReplaceAnotherWorldRecord()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        var protectedRecord = BoundMemoryRecord(
            "protected-memory",
            "world-2",
            "session-1",
            "foreign value");
        await store.UpsertAsync(protectedRecord, CancellationToken.None);
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new CapturingFinalProvider())
                .WithRuntimeMemory(
                    memory,
                    new MutationPolicy(
                        context =>
                        {
                            var now = DateTimeOffset.UtcNow;
                            return new[]
                            {
                                MemoryMutation.Upsert(
                                    new MemoryRecord(
                                        protectedRecord.MemoryId,
                                        protectedRecord.Scope,
                                        ProtocolJson.ParseElement(
                                            "{\"text\":\"replacement\"}"),
                                        Array.Empty<string>(),
                                        importance: 50,
                                        createdAt: now,
                                        updatedAt: now,
                                        provenance: new MemoryProvenance(
                                            context.WorldId,
                                            context.SessionId,
                                            saveRevision: 0,
                                            sourceRunId: context.RunId,
                                            sourceEventId:
                                                context.CommittedSourceEventIds[0],
                                            committed: true)))
                            };
                        }))
                .Build();

            var outcome = await built.Runtime.RunAsync(
                Request("memory-upsert-world-collision"));

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                RuntimeMemoryIntegrationReasonCodes.CommitFailed,
                outcome.ErrorCode);
            var retained = await store.SearchAsync(
                new MemoryQuery(
                    protectedRecord.Scope,
                    ProtocolJson.ParseElement("{}"),
                    worldId: "world-2",
                    sessionId: "session-1",
                    requireCommittedProvenance: true),
                CancellationToken.None);
            Assert.Contains(
                "foreign value",
                Assert.Single(retained).Record.Content.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeManagedBareUpsertCannotReplaceCurrentCoordinateRecord()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var store = new DeterministicMemoryStore();
        var protectedRecord = BoundMemoryRecord(
            "protected-current-memory",
            "world-1",
            "session-1",
            "current value",
            timelineEpoch: 2);
        await store.UpsertAsync(protectedRecord, CancellationToken.None);
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new CapturingFinalProvider())
                .WithRuntimeMemory(
                    memory,
                    new MutationPolicy(
                        context =>
                        {
                            var now = DateTimeOffset.UtcNow;
                            return new[]
                            {
                                MemoryMutation.Upsert(
                                    new MemoryRecord(
                                        protectedRecord.MemoryId,
                                        protectedRecord.Scope,
                                        ProtocolJson.ParseElement(
                                            "{\"text\":\"replacement\"}"),
                                        Array.Empty<string>(),
                                        importance: 50,
                                        createdAt: now,
                                        updatedAt: now,
                                        provenance: new MemoryProvenance(
                                            context.WorldId,
                                            context.SessionId,
                                            saveRevision:
                                                context.Coordinate!.SaveRevision,
                                            sourceRunId: context.RunId,
                                            sourceEventId:
                                                context.CommittedSourceEventIds[0],
                                            committed: true,
                                            timelineId:
                                                context.Coordinate.TimelineId,
                                            timelineEpoch:
                                                context.Coordinate.GameTime!.Epoch)))
                            };
                        }))
                .Build();
            var request = Request("memory-upsert-current-collision");
            GameContextEnvelope.Attach(request.Run, Coordinate());

            var outcome = await built.Runtime.RunAsync(request);

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                RuntimeMemoryIntegrationReasonCodes.CommitFailed,
                outcome.ErrorCode);
            var retained = await store.SearchAsync(
                new MemoryQuery(
                    protectedRecord.Scope,
                    ProtocolJson.ParseElement("{}"),
                    worldId: "world-1",
                    sessionId: "session-1",
                    requireCommittedProvenance: true,
                    timelineId: "timeline-main",
                    timelineEpoch: 2),
                CancellationToken.None);
            Assert.Contains(
                "current value",
                Assert.Single(retained).Record.Content.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("epoch")]
    [InlineData("observer")]
    [InlineData("observer_incarnation")]
    [InlineData("game_clock")]
    [InlineData("game_timeline")]
    [InlineData("game_epoch")]
    [InlineData("future_revision")]
    public async Task RuntimeManagedMutationMustMatchCurrentCoordinate(
        string boundary)
    {
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new MutationPolicy(
                context =>
                {
                    var coordinate = context.Coordinate!;
                    var observer = boundary switch
                    {
                        "observer" => new GameEntityIdentity("npc-9", 2),
                        "observer_incarnation" =>
                            new GameEntityIdentity("npc-1", 3),
                        _ => new GameEntityIdentity("npc-1", 2)
                    };
                    var coordinateTime = coordinate.GameTime!;
                    var timelineId = boundary is "timeline" or "game_timeline"
                        ? "timeline-fork"
                        : coordinate.TimelineId;
                    var epoch = boundary == "epoch"
                        ? 3
                        : coordinateTime.Epoch;
                    var gameClock = boundary == "game_clock"
                        ? "dream-clock"
                        : coordinateTime.ClockId;
                    var gameTimeline = boundary is "timeline" or "game_timeline"
                        ? "timeline-fork"
                        : coordinateTime.TimelineId;
                    var gameEpoch = boundary == "game_epoch"
                        ? 3
                        : coordinateTime.Epoch;
                    var now = DateTimeOffset.UtcNow;
                    return new[]
                    {
                        MemoryMutation.Upsert(
                            new MemoryRecord(
                                "coordinate-memory-" + boundary,
                                "agent:" + context.AgentId,
                                ProtocolJson.ParseElement(
                                    "{\"text\":\"coordinate\"}"),
                                Array.Empty<string>(),
                                50,
                                now,
                                now,
                                provenance: new MemoryProvenance(
                                    context.WorldId,
                                    context.SessionId,
                                    boundary == "future_revision"
                                        ? coordinate.SaveRevision + 1
                                        : coordinate.SaveRevision,
                                    context.RunId,
                                    context.CommittedSourceEventIds[0],
                                    committed: true,
                                    timelineId,
                                    new GameKnowledgePerspective(
                                        observer,
                                        "observation",
                                        new GameEntityIdentity("npc-2", 7)),
                                    epoch),
                                gameTimeWindow: new GameTimeWindow(
                                    validFrom: new GameTimePoint(
                                        gameClock,
                                        gameTimeline,
                                        gameEpoch,
                                        tick: 90))))
                    };
                }),
            options: null);
        var request = Request("memory-coordinate-write-" + boundary);
        GameContextEnvelope.Attach(request.Run, Coordinate());

        var error = Assert.Throws<RuntimeMemoryIntegrationException>(
            () => loop.PrepareCommit(
                request.Run,
                "turn-coordinate",
                new[] { "committed-event" },
                Array.Empty<ActionReceipt>(),
                request.InitialTranscript));

        Assert.Equal(
            RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
            error.ReasonCode);
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("epoch")]
    [InlineData("observer_incarnation")]
    [InlineData("game_clock")]
    [InlineData("future_revision")]
    public async Task RecoveryRevalidatesPreparedAuthorityAgainstCoordinate(
        string boundary)
    {
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new MutationPolicy(
                context =>
                {
                    var coordinate = context.Coordinate!;
                    var now = DateTimeOffset.UtcNow;
                    return new[]
                    {
                        MemoryMutation.Upsert(
                            new MemoryRecord(
                                "prepared-coordinate-memory",
                                "agent:" + context.AgentId,
                                ProtocolJson.ParseElement(
                                    "{\"text\":\"prepared\"}"),
                                Array.Empty<string>(),
                                50,
                                now,
                                now,
                                provenance: new MemoryProvenance(
                                    context.WorldId,
                                    context.SessionId,
                                    coordinate.SaveRevision,
                                    context.RunId,
                                    context.CommittedSourceEventIds[0],
                                    committed: true,
                                    timelineId: coordinate.TimelineId,
                                    perspective: new GameKnowledgePerspective(
                                        coordinate.Observer!,
                                        "observation",
                                        new GameEntityIdentity("npc-2", 7)),
                                    timelineEpoch:
                                        coordinate.GameTime!.Epoch),
                                gameTimeWindow: new GameTimeWindow(
                                    validFrom: new GameTimePoint(
                                        coordinate.GameTime.ClockId,
                                        coordinate.GameTime.TimelineId,
                                        coordinate.GameTime.Epoch,
                                        tick: 90))))
                    };
                }),
            options: null);
        var request = Request("memory-recovery-coordinate-" + boundary);
        GameContextEnvelope.Attach(request.Run, Coordinate());
        var prepared = loop.PrepareCommit(
            request.Run,
            "turn-coordinate",
            new[] { "committed-event" },
            Array.Empty<ActionReceipt>(),
            request.InitialTranscript);
        var current = boundary switch
        {
            "timeline" => CoordinateFor(
                timelineId: "timeline-fork",
                gameTimeTimelineId: "timeline-fork"),
            "epoch" => CoordinateFor(gameTimeEpoch: 3),
            "observer_incarnation" => CoordinateFor(
                observerIncarnation: 3),
            "game_clock" => CoordinateFor(gameTimeClockId: "dream-clock"),
            "future_revision" => CoordinateFor(saveRevision: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        GameContextEnvelope.Attach(request.Run, current);

        var error = Assert.Throws<RuntimeMemoryIntegrationException>(
            () => loop.ValidatePreparedForRun(
                prepared,
                request.Run,
                new[] { "committed-event" }));

        Assert.Equal(
            RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
            error.ReasonCode);
    }

    [Fact]
    public void PreparedCommitRoundTripsCompleteAuthorityEnvelope()
    {
        var original = SemanticMemoryRecord(
            "journal-authority-memory",
            "original",
            saveRevision: 5);
        var replacement = SemanticMemoryRecord(
            "journal-authority-memory",
            "replacement",
            saveRevision: 6);
        var mutations = new[]
        {
            MemoryMutation.Upsert(replacement, original)
        };
        var prepared = new PreparedRuntimeMemoryCommit(
            "memory-commit:test:0:turn-authority",
            "turn-authority",
            "policy",
            "1",
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(mutations),
            mutations);

        var decoded = RuntimeMemoryCommitJournalCodec.DecodePrepared(
            RuntimeMemoryCommitJournalCodec.EncodePrepared(prepared),
            prepared.TurnId,
            prepared.CommitId);

        var expectation = Assert.Single(decoded.Mutations).ExpectedRecord!;
        var authority = expectation.Authority;
        Assert.Equal(5, authority.SaveRevision);
        Assert.True(authority.Committed);
        Assert.Equal("timeline-main", authority.TimelineId);
        Assert.Equal(2, authority.TimelineEpoch);
        Assert.True(authority.HasPerspective);
        Assert.Equal("npc-1", authority.ObserverEntityId);
        Assert.Equal(2, authority.ObserverIncarnation);
        Assert.Equal("observation", authority.PerspectiveKind);
        Assert.True(authority.HasSource);
        Assert.Equal("npc-2", authority.SourceEntityId);
        Assert.Equal(7, authority.SourceIncarnation);
        Assert.True(authority.HasGameTimeWindow);
        Assert.Equal("world-clock", authority.GameTimeClockId);
        Assert.Equal("timeline-main", authority.GameTimeTimelineId);
        Assert.Equal(2, authority.GameTimeEpoch);
        Assert.Equal(
            original.MemoryId,
            expectation.MemoryId);
        Assert.Equal(
            MemoryRecordDigest.ComputeSha256(original),
            expectation.RecordDigest);
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("delete")]
    public async Task LegacyPreparedCommitReplaysHistoricalMutationSemantics(
        string kind)
    {
        var store = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var policy = new MutationPolicy(
            _ => Array.Empty<MemoryMutation>());
        var loop = new RuntimeMemoryAgentLoop(memory, policy, options: null);
        var request = Request("legacy-memory-" + kind);
        const string turnId = "turn-legacy";
        const string memoryId = "legacy-memory-id";
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(
            new MemoryRecord(
                memoryId,
                "old-scope",
                ProtocolJson.ParseElement("{\"value\":\"old\"}"),
                Array.Empty<string>(),
                50,
                now,
                now),
            CancellationToken.None);

        var mutations = string.Equals(kind, "upsert", StringComparison.Ordinal)
            ? new[]
            {
                MemoryMutation.Upsert(
                    new MemoryRecord(
                        memoryId,
                        "agent:agent-1",
                        ProtocolJson.ParseElement(
                            "{\"value\":\"replacement\"}"),
                        Array.Empty<string>(),
                        50,
                        now,
                        now,
                        provenance: new MemoryProvenance(
                            request.Run.WorldId,
                            request.Run.SessionId,
                            0,
                            request.Run.RunId,
                            "committed-event",
                            committed: true)))
            }
            : new[] { MemoryMutation.Delete(memoryId) };
        var prepared = new PreparedRuntimeMemoryCommit(
            RuntimeMemoryAgentLoop.CommitId(
                request.Run.RunId,
                request.Run.RuntimeGeneration,
                turnId),
            turnId,
            loop.PolicyId,
            loop.PolicyVersion,
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(mutations),
            mutations,
            PreparedRuntimeMemoryCommit.LegacyMutationContractVersion);
        var encoded = RuntimeMemoryCommitJournalCodec
            .EncodePrepared(prepared)
            .GetRawText();
        using var oldPayload = JsonDocument.Parse(
            encoded.Replace(
                "\"mutationContractVersion\":0,",
                string.Empty,
                StringComparison.Ordinal));
        var decoded = RuntimeMemoryCommitJournalCodec.DecodePrepared(
            oldPayload.RootElement,
            turnId,
            prepared.CommitId);

        Assert.Equal(
            PreparedRuntimeMemoryCommit.LegacyMutationContractVersion,
            decoded.MutationContractVersion);
        loop.ValidatePreparedForRun(
            decoded,
            request.Run,
            new[] { "committed-event" });
        await loop.ApplyPreparedAsync(decoded, CancellationToken.None);

        var oldScope = await store.SearchAsync(
            new MemoryQuery(
                "old-scope",
                ProtocolJson.ParseElement("{}")),
            CancellationToken.None);
        Assert.Empty(oldScope);
        if (string.Equals(kind, "upsert", StringComparison.Ordinal))
        {
            var replacement = await store.SearchAsync(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement("{}")),
                CancellationToken.None);
            Assert.Equal(
                "replacement",
                Assert.Single(replacement).Record.Content
                    .GetProperty("value")
                    .GetString());
        }
    }

    [Fact]
    public async Task LegacyPreparedCommitPreservesUnsupportedReplaySignal()
    {
        var store = new ApplyThenThrowStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new MutationPolicy(_ => Array.Empty<MemoryMutation>()),
            options: null);
        var now = DateTimeOffset.UtcNow;
        var mutations = new[]
        {
            MemoryMutation.Upsert(
                new MemoryRecord(
                    "legacy-unsupported-memory",
                    "agent:agent-1",
                    ProtocolJson.ParseElement("{\"value\":\"legacy\"}"),
                    Array.Empty<string>(),
                    50,
                    now,
                    now,
                    provenance: new MemoryProvenance(
                        "world-legacy",
                        "session-legacy",
                        0,
                        "run-legacy",
                        "event-legacy",
                        committed: true)))
        };
        var prepared = new PreparedRuntimeMemoryCommit(
            "memory-commit:legacy-unsupported:0:turn-legacy",
            "turn-legacy",
            loop.PolicyId,
            loop.PolicyVersion,
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(mutations),
            mutations,
            PreparedRuntimeMemoryCommit.LegacyMutationContractVersion);

        await Assert.ThrowsAsync<MemoryLegacyReplayNotSupportedException>(
            () => loop.ApplyPreparedAsync(prepared, CancellationToken.None)
                .AsTask());
        Assert.Equal(0, store.ApplyCallCount);
    }

    [Fact]
    public async Task ExplicitTimelineEpochSurvivesBindingWithoutGameTime()
    {
        var provider = new IgnoringQueryMemoryProvider(
            Array.Empty<MemorySearchResult>());
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { provider });
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new FixedRecallPolicy(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement("{}"),
                    timelineId: "timeline-main",
                    timelineEpoch: 2)),
            options: null);
        var request = Request("memory-explicit-epoch");

        _ = await loop.RecallAsync(
            request.Run,
            "turn-explicit-epoch",
            request.InitialTranscript,
            Array.Empty<ContextCandidate>(),
            maximumContextCandidates: 8,
            CancellationToken.None);

        Assert.NotNull(provider.LastQuery);
        Assert.Equal(2, provider.LastQuery.TimelineEpoch);
        Assert.True(provider.LastQuery.EnforceTimelineEpoch);
        Assert.Null(provider.LastQuery.GameTime);
    }

    [Fact]
    public async Task CoordinateTimelineEpochIsBoundWithoutBecomingExplicit()
    {
        var provider = new IgnoringQueryMemoryProvider(
            Array.Empty<MemorySearchResult>());
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { provider });
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new FixedRecallPolicy(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement("{}"))),
            options: null);
        var request = Request("memory-coordinate-epoch");
        GameContextEnvelope.Attach(request.Run, Coordinate());

        _ = await loop.RecallAsync(
            request.Run,
            "turn-coordinate-epoch",
            request.InitialTranscript,
            Array.Empty<ContextCandidate>(),
            maximumContextCandidates: 8,
            CancellationToken.None);

        Assert.NotNull(provider.LastQuery);
        Assert.Equal(2, provider.LastQuery.TimelineEpoch);
        Assert.False(provider.LastQuery.EnforceTimelineEpoch);
        Assert.Equal(2, provider.LastQuery.GameTime!.Epoch);
    }

    [Fact]
    public async Task ExplicitTimelineEpochFiltersProvenanceOnlyRecords()
    {
        var provider = new IgnoringQueryMemoryProvider(
            new[]
            {
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "current-epoch",
                        "world-1",
                        "session-1",
                        "current epoch memory",
                        timelineEpoch: 2),
                    score: 300),
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "stale-epoch",
                        "world-1",
                        "session-1",
                        "stale epoch memory",
                        timelineEpoch: 1),
                    score: 200),
                new MemorySearchResult(
                    BoundMemoryRecord(
                        "missing-epoch",
                        "world-1",
                        "session-1",
                        "missing epoch memory"),
                    score: 100)
            });
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { provider });
        var loop = new RuntimeMemoryAgentLoop(
            memory,
            new FixedRecallPolicy(
                new MemoryQuery(
                    "agent:agent-1",
                    ProtocolJson.ParseElement("{}"),
                    worldId: "world-1",
                    sessionId: "session-1",
                    requireCommittedProvenance: true,
                    timelineId: "timeline-main",
                    timelineEpoch: 2)),
            options: null);
        var request = Request("memory-provenance-only-epoch");

        var selection = await loop.RecallAsync(
            request.Run,
            "turn-provenance-only-epoch",
            request.InitialTranscript,
            Array.Empty<ContextCandidate>(),
            maximumContextCandidates: 8,
            CancellationToken.None);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Contains(
            "current epoch memory",
            candidate.Content!.Value.GetRawText(),
            StringComparison.Ordinal);
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
        return CoordinateFor();
    }

    private static GameContextCoordinate CoordinateFor(
        string timelineId = "timeline-main",
        long saveRevision = 12,
        long observerIncarnation = 2,
        string gameTimeClockId = "world-clock",
        string gameTimeTimelineId = "timeline-main",
        long gameTimeEpoch = 2)
    {
        return new GameContextCoordinate(
            "world-1",
            timelineId,
            saveRevision,
            observer: new GameEntityIdentity(
                "npc-1",
                observerIncarnation),
            gameTime: new GameTimePoint(
                gameTimeClockId,
                gameTimeTimelineId,
                gameTimeEpoch,
                tick: 100));
    }

    private static MemoryRecord BoundMemoryRecord(
        string memoryId,
        string worldId,
        string? sessionId,
        string text,
        long? timelineEpoch = null)
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
                timelineId: "timeline-main",
                timelineEpoch: timelineEpoch));
    }

    private static MemoryRecord SemanticMemoryRecord(
        string memoryId,
        string text,
        long saveRevision)
    {
        var now = DateTimeOffset.UnixEpoch;
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
                "world-1",
                "session-1",
                saveRevision,
                sourceRunId: "source-" + text,
                sourceEventId: "event-" + text,
                committed: true,
                timelineId: "timeline-main",
                perspective: new GameKnowledgePerspective(
                    new GameEntityIdentity("npc-1", 2),
                    "observation",
                    new GameEntityIdentity("npc-2", 7)),
                timelineEpoch: 2),
            gameTimeWindow: new GameTimeWindow(
                validFrom: new GameTimePoint(
                    "world-clock",
                    "timeline-main",
                    epoch: 2,
                    tick: 90)));
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

    private sealed class MutationPolicy : IRuntimeMemoryPolicy
    {
        private readonly Func<
            RuntimeMemoryCommitContext,
            IReadOnlyList<MemoryMutation>> _mutations;

        public MutationPolicy(
            Func<
                RuntimeMemoryCommitContext,
                IReadOnlyList<MemoryMutation>> mutations)
        {
            _mutations = mutations;
        }

        public string PolicyId => "mutation-policy";

        public string Version => "1.0.0";

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            return _mutations(context);
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
        IRuntimeAuthoritativeMemoryBatchStore
    {
        private readonly DeterministicMemoryStore _inner = new();
        private int _applyCalls;

        public string ProviderId => "apply-then-throw";

        public int ApplyCallCount => Volatile.Read(ref _applyCalls);

        public int RuntimeMutationContractVersion =>
            RuntimeMemoryMutationContract.CurrentVersion;

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
