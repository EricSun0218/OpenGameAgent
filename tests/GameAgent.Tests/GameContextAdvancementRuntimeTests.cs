using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Tests;

public sealed class GameContextAdvancementRuntimeTests
{
    [Fact]
    public async Task ReceiptAdvancesNextActionRequestAndDurableRun()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var host = new AdvancingHost();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(journalPath)
                .AddProvider(new TwoActionsThenFinalProvider())
                .WithTools(new[] { ActionTool() })
                .Build();
            var request = RunRequest("coordinate-next-request");

            var outcome = await built.Runtime.RunAsync(request);
            var events = await built.SessionStore.ReadRunAsync(
                request.Run.RunId,
                CancellationToken.None);

            Assert.True(
                string.Equals(
                    RunStates.Completed,
                    outcome.Run.State,
                    StringComparison.Ordinal),
                $"state={outcome.Run.State}; pending="
                + string.Join(",", outcome.Run.PendingOperationIds)
                + "; hostRequests="
                + host.Requests.Count
                + "; events="
                + string.Join(",", events.Select(item => item.Kind)));
            Assert.Equal(2, host.Requests.Count);
            Assert.Equal(
                "state-1",
                host.Requests[0].BasedOnStateVersion);
            Assert.Equal(
                "state-2",
                host.Requests[1].BasedOnStateVersion);
            Assert.True(
                GameContextEnvelope.TryRead(
                    outcome.Run,
                    out var resulting));
            Assert.Equal("state-2", resulting!.StateVersion);
            Assert.Equal("session-1", resulting.SessionId);

            var advancement = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.GameContextAdvanced);
            var firstReceiptSequence = events
                .Where(
                    item => item.Kind
                            == RuntimeEventKinds.ActionReceived)
                .Min(item => item.Sequence);
            var secondRequestSequence = events
                .Where(
                    item => item.Kind
                            == RuntimeEventKinds.ActionRequested)
                .Max(item => item.Sequence);
            Assert.True(firstReceiptSequence < advancement.Sequence);
            Assert.True(advancement.Sequence < secondRequestSequence);
            var checkpoint = ProtocolJson.DeserializeAgentRun(
                advancement.Payload.GetRawText());
            Assert.True(
                GameContextEnvelope.TryRead(
                    checkpoint,
                    out var checkpointCoordinate));
            Assert.Equal(
                "state-2",
                checkpointCoordinate!.StateVersion);
            var advancingReceipt = events
                .Where(
                    item => item.Kind
                            == RuntimeEventKinds.ActionReceived)
                .Select(
                    item => ProtocolJson.DeserializeActionReceipt(
                        item.Payload.GetRawText()))
                .Single(
                    item => GameContextReceiptEnvelope.TryReadResulting(
                        item,
                        out _));
            Assert.True(
                GameContextReceiptEnvelope.TryReadResulting(
                    advancingReceipt,
                    out var durableResulting));
            Assert.Equal("session-1", durableResulting!.SessionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryAfterAdvancementDoesNotReplayHostAction()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var inner = new FileSessionStore(journalPath);
        var store = new BlockTurnCompletionAfterAdvancementStore(inner);
        var host = new AdvancingHost();
        var provider = new TwoActionsThenFinalProvider();
        BuiltGameAgentRuntime? first = null;
        BuiltGameAgentRuntime? second = null;
        Task<DurableRunOutcome>? firstRun = null;
        try
        {
            first = new GameAgentRuntimeBuilder(host)
                .UseDurableStore(store, store)
                .AddProvider(provider)
                .WithTools(new[] { ActionTool() })
                .Build();
            var request = RunRequest("coordinate-crash-window");
            firstRun = first.Runtime.RunAsync(request).AsTask();
            await store.WaitUntilBlockedAsync().WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Single(host.Requests);
            second = new GameAgentRuntimeBuilder(host)
                .UseDurableStore(store, store)
                .AddProvider(provider)
                .WithTools(new[] { ActionTool() })
                .Build();

            var recovered = await second.Runtime.ResumeAsync(
                request.Run.RunId);

            Assert.Equal(RunStates.Completed, recovered.Run.State);
            Assert.Equal(2, host.Requests.Count);
            Assert.Equal(
                "state-2",
                host.Requests[1].BasedOnStateVersion);
            Assert.NotEqual(
                host.Requests[0].OperationId,
                host.Requests[1].OperationId);
            Assert.True(
                GameContextEnvelope.TryRead(
                    recovered.Run,
                    out var coordinate));
            Assert.Equal("state-2", coordinate!.StateVersion);
            Assert.Equal("session-1", coordinate.SessionId);
        }
        finally
        {
            store.Release();
            if (firstRun is not null)
            {
                try
                {
                    _ = await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // The suspended process image resumes against a newer
                    // journal revision and is expected to lose ownership.
                }
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            if (first is not null)
            {
                await first.DisposeAsync();
            }

            await inner.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownReceiptAdvancesOnlyAfterTerminalReconciliation()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var provider = new OneActionThenFinalProvider();
        var reconciler = new AdvancingReconciler();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new UnknownHost())
                .UseFileJournal(journalPath)
                .AddProvider(provider)
                .WithTools(new[] { ActionTool() })
                .Build();
            var request = RunRequest("coordinate-reconcile");
            var originalCoordinate = request.Run.Extensions[
                    GameContextEnvelope.ExtensionName]
                .GetRawText();

            var pending = await built.Runtime.RunAsync(request);

            Assert.Equal(RunStates.Reconciling, pending.Run.State);
            Assert.Single(pending.Run.PendingOperationIds);
            Assert.Equal(
                originalCoordinate,
                pending.Run.Extensions[
                        GameContextEnvelope.ExtensionName]
                    .GetRawText());
            var beforeReconcile =
                await built.SessionStore.ReadRunAsync(
                    request.Run.RunId,
                    CancellationToken.None);
            Assert.DoesNotContain(
                beforeReconcile,
                item => item.Kind
                        == RuntimeEventKinds.GameContextAdvanced);

            var outcome = await built.Runtime.ResumeAsync(
                request.Run.RunId,
                reconciler: reconciler);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, reconciler.QueryCount);
            Assert.True(
                GameContextEnvelope.TryRead(
                    outcome.Run,
                    out var resulting));
            Assert.Equal("state-2", resulting!.StateVersion);
            var afterReconcile =
                await built.SessionStore.ReadRunAsync(
                    request.Run.RunId,
                    CancellationToken.None);
            Assert.Single(
                afterReconcile,
                item => item.Kind
                        == RuntimeEventKinds.GameContextAdvanced);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AdvancementPrecedesMemoryPolicyAndNextRecall()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var memoryStore = new DeterministicMemoryStore();
        await using var memory = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { memoryStore },
            memoryStore);
        var policy = new CoordinateCapturingMemoryPolicy();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new AdvancingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new OneActionThenFinalProvider())
                .WithTools(new[] { ActionTool() })
                .WithRuntimeMemory(memory, policy)
                .Build();

            var outcome = await built.Runtime.RunAsync(
                RunRequest("coordinate-memory"));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(
                "state-2",
                Assert.Single(policy.ReceiptCommitCoordinates)!
                    .StateVersion);
            Assert.Equal("state-1", policy.RecallCoordinates[0]!.StateVersion);
            Assert.Contains(
                policy.RecallCoordinates.Skip(1),
                coordinate => coordinate?.StateVersion == "state-2");

            var events = await built.SessionStore.ReadRunAsync(
                outcome.Run.RunId,
                CancellationToken.None);
            var advancement = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.GameContextAdvanced);
            Assert.Equal(
                "state-2",
                policy.ReceiptCommitCoordinates[0]!.StateVersion);
            Assert.True(advancement.Sequence > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentNpcAdvancementKeepsIncarnationsIsolated()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        var host = new PerActorAdvancingHost();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(journalPath)
                .AddProvider(new OneActionThenFinalProvider())
                .WithTools(new[] { ActionTool() })
                .Build();
            var first = ActorRunRequest(
                "npc-a-run",
                "session-a",
                "npc-a",
                incarnation: 3);
            var second = ActorRunRequest(
                "npc-b-run",
                "session-b",
                "npc-b",
                incarnation: 9);

            var outcomes = await Task.WhenAll(
                built.Runtime.RunAsync(first).AsTask(),
                built.Runtime.RunAsync(second).AsTask());

            Assert.All(
                outcomes,
                outcome => Assert.Equal(
                    RunStates.Completed,
                    outcome.Run.State));
            Assert.True(
                GameContextEnvelope.TryRead(
                    outcomes[0].Run,
                    out var firstCoordinate));
            Assert.True(
                GameContextEnvelope.TryRead(
                    outcomes[1].Run,
                    out var secondCoordinate));
            Assert.Equal("npc-a", firstCoordinate!.Observer!.EntityId);
            Assert.Equal(3, firstCoordinate.Observer.Incarnation);
            Assert.Equal("session-a", firstCoordinate.SessionId);
            Assert.Equal(
                "npc-a-state-2",
                firstCoordinate.StateVersion);
            Assert.Equal("npc-b", secondCoordinate!.Observer!.EntityId);
            Assert.Equal(9, secondCoordinate.Observer.Incarnation);
            Assert.Equal("session-b", secondCoordinate.SessionId);
            Assert.Equal(
                "npc-b-state-2",
                secondCoordinate.StateVersion);
            Assert.Equal(2, host.RequestCoordinates.Count);
            Assert.Contains(
                host.RequestCoordinates,
                coordinate => coordinate.Observer?.EntityId == "npc-a"
                              && coordinate.Observer.Incarnation == 3);
            Assert.Contains(
                host.RequestCoordinates,
                coordinate => coordinate.Observer?.EntityId == "npc-b"
                              && coordinate.Observer.Incarnation == 9);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("checkpoint_evidence")]
    [InlineData("receipt_extension")]
    public async Task RecoveryRejectsTamperedAdvancementEvidence(
        string mutation)
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        try
        {
            IReadOnlyList<RuntimeEvent> captured;
            RunJournalCursor cursor;
            await using (var built = new GameAgentRuntimeBuilder(
                                   new AdvancingHost())
                               .UseFileJournal(journalPath)
                               .AddProvider(
                                   new OneActionThenFinalProvider())
                               .WithTools(new[] { ActionTool() })
                               .Build())
            {
                var outcome = await built.Runtime.RunAsync(
                    RunRequest("coordinate-tamper-" + mutation));
                Assert.Equal(RunStates.Completed, outcome.Run.State);
                captured = (await built.SessionStore.ReadRunAsync(
                        outcome.Run.RunId,
                        CancellationToken.None))
                    .Select(CloneEvent)
                    .ToArray();
                cursor = await built.SessionStore.GetRunCursorAsync(
                    outcome.Run.RunId,
                    CancellationToken.None);
            }

            if (string.Equals(
                    mutation,
                    "checkpoint_evidence",
                    StringComparison.Ordinal))
            {
                var advancement = Assert.Single(
                    captured,
                    item => item.Kind
                            == RuntimeEventKinds.GameContextAdvanced);
                advancement.Extensions[
                        GameContextAdvancementJournalCodec
                            .ResultingExtensionName] =
                    GameContextEnvelope.ToJson(
                        Coordinate("state-3", 3));
            }
            else
            {
                var receiptEvent = captured.First(
                    item => item.Kind
                            == RuntimeEventKinds.ActionReceived);
                var receipt = ProtocolJson.DeserializeActionReceipt(
                    receiptEvent.Payload.GetRawText());
                GameContextReceiptEnvelope.AttachResulting(
                    receipt,
                    Coordinate("state-3", 3));
                receiptEvent.Payload = ProtocolJson.ToElement(receipt);
            }

            await using var store = new StaticRecoveryStore(
                captured,
                cursor);
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            var recovery = new RunRecovery(store, store, journal);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => recovery.LoadAsync(
                        cursor.RunId,
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal(0, store.AppendCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLiveTransitionReturnsStableGameContextFailure()
    {
        var directory = TempDirectory();
        var journalPath = Path.Combine(directory, "runtime.journal");
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new EscapingAdvancingHost())
                .UseFileJournal(journalPath)
                .AddProvider(new OneActionThenFinalProvider())
                .WithTools(new[] { ActionTool() })
                .Build();

            var outcome = await built.Runtime.RunAsync(
                RunRequest("coordinate-live-invalid"));

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                outcome.ErrorCode);
            Assert.Equal("game_context", outcome.ErrorCategory);
            Assert.Equal(
                "The authoritative game-context transition was rejected.",
                outcome.SafeErrorMessage);
            Assert.DoesNotContain(
                "world-other",
                outcome.SafeErrorMessage,
                StringComparison.Ordinal);
            var events = await built.SessionStore.ReadRunAsync(
                outcome.Run.RunId,
                CancellationToken.None);
            Assert.DoesNotContain(
                events,
                item => item.Kind
                        == RuntimeEventKinds.GameContextAdvanced);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableRunRequest RunRequest(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun
        {
            RunId = runId,
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
        GameContextEnvelope.Attach(
            run,
            Coordinate("state-1", 1));
        return new DurableRunRequest
        {
            Run = run,
            InitialTranscript = new[]
            {
                new NormalizedMessage
                {
                    MessageId = runId + "-user",
                    Role = NormalizedRoles.User,
                    CreatedAt = now,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("Act twice.")
                    }
                }
            }
        };
    }

    private static DurableRunRequest ActorRunRequest(
        string runId,
        string sessionId,
        string observerId,
        long incarnation)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun
        {
            RunId = runId,
            AgentId = observerId,
            WorldId = "world-1",
            SessionId = sessionId,
            State = RunStates.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
        GameContextEnvelope.Attach(
            run,
            new GameContextCoordinate(
                "world-1",
                "timeline-1",
                1,
                new GameEntityIdentity(observerId, incarnation),
                stateVersion: observerId + "-state-1",
                gameTime: new GameTimePoint(
                    "world-clock",
                    "timeline-1",
                    1,
                    1),
                sessionId: sessionId));
        return new DurableRunRequest
        {
            Run = run,
            InitialTranscript = new[]
            {
                new NormalizedMessage
                {
                    MessageId = runId + "-user",
                    Role = NormalizedRoles.User,
                    CreatedAt = now,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("Act once.")
                    }
                }
            }
        };
    }

    private static GameContextCoordinate Coordinate(
        string stateVersion,
        long saveRevision,
        string? sessionId = "session-1")
    {
        return new GameContextCoordinate(
            "world-1",
            "timeline-1",
            saveRevision,
            new GameEntityIdentity("npc-1", 1),
            stateVersion: stateVersion,
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-1",
                1,
                saveRevision),
            sessionId: sessionId);
    }

    private static RuntimeEvent CloneEvent(RuntimeEvent runtimeEvent)
    {
        return ProtocolJson.DeserializeRuntimeEvent(
            ProtocolJson.Serialize(runtimeEvent));
    }

    private static ToolDescriptor ActionTool()
    {
        return new ToolDescriptor
        {
            Name = "game_action",
            Version = "1",
            Description = "Applies one authoritative game action.",
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
            "game-context-advancement",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AdvancingHost : IGameHost
    {
        private readonly object _sync = new();
        private readonly List<ActionRequest> _requests = new();

        public IReadOnlyList<ActionRequest> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index;
            lock (_sync)
            {
                _requests.Add(
                    ProtocolJson.DeserializeActionRequest(
                        ProtocolJson.Serialize(request)));
                index = _requests.Count;
            }

            var now = DateTimeOffset.UtcNow;
            var receipt = new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement(
                    $$"""{"action":{{index}}}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
            if (index == 1)
            {
                GameContextReceiptEnvelope.AttachResulting(
                    receipt,
                    Coordinate("state-2", 2, sessionId: null));
            }

            return new ValueTask<ActionReceipt>(receipt);
        }
    }

    private sealed class UnknownHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Unknown,
                    ReceivedAt = DateTimeOffset.UtcNow
                });
        }
    }

    private sealed class AdvancingReconciler : IGameOperationReconciler
    {
        private int _queryCount;

        public int QueryCount => Volatile.Read(ref _queryCount);

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _queryCount);
            var now = DateTimeOffset.UtcNow;
            var receipt = new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 2,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement(
                    """{"reconciled":true}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
            GameContextReceiptEnvelope.AttachResulting(
                receipt,
                Coordinate("state-2", 2));
            return new ValueTask<ActionReceipt>(receipt);
        }
    }

    private sealed class EscapingAdvancingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var receipt = new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement("""{"escaped":true}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
            GameContextReceiptEnvelope.AttachResulting(
                receipt,
                new GameContextCoordinate(
                    "world-other",
                    "timeline-1",
                    2,
                    new GameEntityIdentity("npc-1", 1),
                    stateVersion: "state-2",
                    gameTime: new GameTimePoint(
                        "world-clock",
                        "timeline-1",
                        1,
                        2),
                    sessionId: "session-1"));
            return new ValueTask<ActionReceipt>(receipt);
        }
    }

    private sealed class PerActorAdvancingHost : IGameHost
    {
        private readonly ConcurrentBag<GameContextCoordinate>
            _requestCoordinates = new();

        public IReadOnlyCollection<GameContextCoordinate>
            RequestCoordinates => _requestCoordinates.ToArray();

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(
                request.Extensions.TryGetValue(
                    GameContextEnvelope.ExtensionName,
                    out var sourceJson));
            Assert.True(
                GameContextEnvelope.TryRead(
                    sourceJson,
                    out var source));
            _requestCoordinates.Add(source!);
            var now = DateTimeOffset.UtcNow;
            var receipt = new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement("""{"acted":true}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
            GameContextReceiptEnvelope.AttachResulting(
                receipt,
                new GameContextCoordinate(
                    source!.WorldId,
                    source.TimelineId,
                    checked(source.SaveRevision + 1),
                    source.Observer,
                    source.SceneId,
                    source.RegionId,
                    source.Observer!.EntityId + "-state-2",
                    new GameTimePoint(
                        source.GameTime!.ClockId,
                        source.GameTime.TimelineId,
                        source.GameTime.Epoch,
                        checked(source.GameTime.Tick + 1)),
                    sessionId: source.SessionId));
            return new ValueTask<ActionReceipt>(receipt);
        }
    }

    private sealed class CoordinateCapturingMemoryPolicy :
        IRuntimeMemoryPolicy
    {
        public string PolicyId => "coordinate-capture";

        public string Version => "1";

        public List<GameContextCoordinate?> RecallCoordinates { get; } =
            new();

        public List<GameContextCoordinate?> CommitCoordinates { get; } =
            new();

        public List<GameContextCoordinate?> ReceiptCommitCoordinates
        { get; } = new();

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            RecallCoordinates.Add(context.Coordinate);
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            CommitCoordinates.Add(context.Coordinate);
            if (context.Receipts.Count > 0)
            {
                ReceiptCommitCoordinates.Add(context.Coordinate);
            }

            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class OneActionThenFinalProvider :
        IStreamingModelProvider
    {
        private readonly ConcurrentDictionary<string, int> _calls =
            new(StringComparer.Ordinal);

        public string ProviderId => "one-action-provider";

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
            var call = _calls.AddOrUpdate(
                request.RunId,
                1,
                static (_, current) => checked(current + 1));
            if (call == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "game-call-" + request.RunId,
                    ToolNameDelta = "game_action",
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

        private static ModelStreamEvent Usage(
            string streamAttemptId,
            long ordinal)
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
    }

    private sealed class TwoActionsThenFinalProvider :
        IStreamingModelProvider
    {
        private int _calls;

        public string ProviderId => "coordinate-provider";

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
            if (call <= 2)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "game-call-" + call,
                    ToolNameDelta = "game_action",
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

        private static ModelStreamEvent Usage(
            string streamAttemptId,
            long ordinal)
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
    }

    private sealed class StaticRecoveryStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly IReadOnlyList<RuntimeEvent> _events;
        private readonly RunJournalCursor _cursor;

        public StaticRecoveryStore(
            IReadOnlyList<RuntimeEvent> events,
            RunJournalCursor cursor)
        {
            _events = events;
            _cursor = cursor;
        }

        public int AppendCount { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCount++;
            return default;
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCount++;
            var sequence = expectedRunRevision ?? _cursor.Revision;
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    sequence,
                    checked(sequence + 1),
                    wasDuplicate: false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCount += runtimeEvents.Count;
            var sequence = expectedRunRevision ?? _cursor.Revision;
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            checked(sequence + index),
                            checked(sequence + index + 1),
                            wasDuplicate: false))
                    .ToArray());
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(_events);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(_cursor);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<OperationLedgerEntry?>(
                result: null);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<OperationLedgerEntry> pending =
                Array.Empty<OperationLedgerEntry>();
            return new ValueTask<IReadOnlyList<OperationLedgerEntry>>(
                pending);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }

    private sealed class BlockTurnCompletionAfterAdvancementStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _advancementCommitted;
        private int _blockClaimed;

        public BlockTurnCompletionAfterAdvancementStore(
            FileSessionStore inner)
        {
            _inner = inner;
        }

        public Task WaitUntilBlockedAsync()
        {
            return _blocked.Task;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (runtimeEvent.Kind
                == RuntimeEventKinds.GameContextAdvanced)
            {
                var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken);
                Volatile.Write(ref _advancementCommitted, 1);
                return result;
            }

            if (runtimeEvent.Kind == RuntimeEventKinds.TurnCompleted
                && Volatile.Read(ref _advancementCommitted) == 1
                && Interlocked.CompareExchange(
                    ref _blockClaimed,
                    1,
                    0) == 0)
            {
                _blocked.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
            }

            return await _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(
                operationId,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(
                runId,
                cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }
}
