using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class DurableAgentRuntimeTests
{
    private static readonly TimeSpan TestWaitTimeout =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DurableRuntimeRejectsContextLimitAboveJournalCapacity()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var contextCompiler = new ContextCompiler(
                new ContextCompilerOptions(maxCandidates: 513));

            var error = Assert.Throws<ArgumentException>(
                () => new DurableAgentRuntime(
                    new ProviderAttemptRunner(
                        new[]
                        {
                            new QueueStreamingProvider(
                                FinalResponse("\"unused\""))
                        },
                        new ProviderRetryPolicy(),
                        new SystemRuntimeDelay(),
                        ids),
                    new Host(
                        _ => throw new InvalidOperationException(
                            "No action should be dispatched.")),
                    journal,
                    new RunRecovery(store, store, journal),
                    new ToolCatalogRegistry(),
                    new SkillCatalogRegistry(),
                    contextCompiler,
                    new ToolBatchPlanner(),
                    new ToolBatchScheduler(),
                    clock,
                    ids));

            Assert.Equal("contextCompiler", error.ParamName);
            Assert.Contains("512", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedRunCollectionIsRejectedBeforeValidation()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                new Ids());
            var rejectedRun = Run(clock.UtcNow);
            rejectedRun.PendingOperationIds =
                Enumerable.Repeat("duplicate-operation", 2_049).ToList();

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(
                        new DurableRunRequest { Run = rejectedRun })
                    .AsTask());

            Assert.Equal("agent_run_items_exceeded", error.LimitCode);
            Assert.Empty(
                await store.ReadRunAsync(rejectedRun.RunId, default));

            var accepted = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });
            Assert.Equal(RunStates.Completed, accepted.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MisreportedInfiniteContextIsBoundedAndRunLeaseIsReleased()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                new Ids());
            var rejectedRun = Run(clock.UtcNow);
            var context = new ContextCandidate(
                "bounded-context",
                "world",
                ProtocolJson.ParseElement("""{"value":1}"""));

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = rejectedRun,
                            Context =
                                new MisreportedInfiniteReadOnlyList<
                                    ContextCandidate>(context)
                        })
                    .AsTask());

            Assert.Equal("context_candidate_count_exceeded", error.LimitCode);
            Assert.Empty(
                await store.ReadRunAsync(rejectedRun.RunId, default));

            var accepted = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });
            Assert.Equal(RunStates.Completed, accepted.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AggregateRunInputLimitFailsBeforeJournalAndReleasesLease()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                new Ids());
            var rejectedRun = Run(clock.UtcNow);
            var payload = ProtocolJson.ParseElement(
                JsonSerializer.Serialize(
                    new { value = new string('x', 60_000) }));
            var context = Enumerable.Range(0, 5)
                .Select(
                    index => new ContextCandidate(
                        "large-context-" + index,
                        "world",
                        payload))
                .ToArray();

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = rejectedRun,
                            Context = context
                        })
                    .AsTask());

            Assert.Equal(
                "durable_run_input_bytes_exceeded",
                error.LimitCode);
            Assert.Empty(
                await store.ReadRunAsync(rejectedRun.RunId, default));

            var accepted = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });
            Assert.Equal(RunStates.Completed, accepted.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResourceMetadataLimitFailsBeforeJournalAndReleasesLease()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                new Ids());
            var rejectedRun = Run(clock.UtcNow);
            var context = Enumerable.Range(
                    0,
                    DurableRunInputJournalCodec.MaxContextCandidates)
                .Select(
                    index => new ContextCandidate(
                        $"resource-{index}",
                        "world",
                        new ContextResourceReference(
                            "memory://" + new string('u', 330),
                            "x",
                            sizeBytes: long.MaxValue),
                        estimatedTokens: int.MaxValue,
                        expiresAt: DateTimeOffset.UnixEpoch,
                        provenance: "p"))
                .ToArray();

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = rejectedRun,
                            Context = context
                        })
                    .AsTask());

            Assert.Equal(
                "durable_run_input_bytes_exceeded",
                error.LimitCode);
            Assert.Empty(
                await store.ReadRunAsync(rejectedRun.RunId, default));

            var accepted = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });
            Assert.Equal(RunStates.Completed, accepted.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AggregateRunInputNodeLimitFailsBeforeJournalAndReleasesLease()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                new Ids());
            var rejectedRun = Run(clock.UtcNow);
            var content = ProtocolJson.ParseElement(
                "[" + string.Join(
                    ",",
                    Enumerable.Repeat("0", 2_000)) + "]");
            var context = Enumerable.Range(0, 5)
                .Select(
                    index => new ContextCandidate(
                        $"node-heavy-context-{index}",
                        "world",
                        content))
                .ToArray();

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = rejectedRun,
                            Context = context
                        })
                    .AsTask());

            Assert.Equal("json_nodes_exceeded", error.LimitCode);
            Assert.Empty(
                await store.ReadRunAsync(rejectedRun.RunId, default));

            var accepted = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });
            Assert.Equal(RunStates.Completed, accepted.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewRunRejectsNonFreshSnapshotsBeforeAnySideEffect()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider();
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids());
            var mutations = new Action<AgentRun>[]
            {
                run => run.State = RunStates.Completed,
                run => run.Revision = 1,
                run => run.CurrentTurnId = "turn-existing",
                run => run.PendingOperationIds.Add("operation-existing"),
                run => run.TerminalReason = "previous_terminal",
                run => run.CompletionIntent = CompletionIntents.Cancelled,
                run =>
                {
                    run.Usage.HasUnaccountedUsage = true;
                    run.Usage.UnaccountedProviderAttempts = 1;
                },
                run =>
                {
                    run.State = RunStates.Reconciling;
                    run.PendingOperationIds.Add("operation-uncertain");
                }
            };

            foreach (var mutate in mutations)
            {
                var run = Run(clock.UtcNow);
                mutate(run);

                var error = await Assert.ThrowsAsync<ArgumentException>(
                    () => runtime.RunAsync(
                            new DurableRunRequest { Run = run })
                        .AsTask());

                Assert.Equal("request", error.ParamName);
                Assert.Empty(
                    await store.ReadRunAsync(run.RunId, default));
            }

            Assert.Empty(provider.Requests);
            Assert.Equal(0, host.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewRunInitializationIsCommittedAsOneOrderedBatch()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            await using var store = new RecordingBatchStore(inner);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                store,
                provider,
                host,
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            var initial = new[]
            {
                InitialMessage("initial-1", "first", clock.UtcNow),
                InitialMessage("initial-2", "second", clock.UtcNow)
            };

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = run,
                    InitialTranscript = initial
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            var startBatch = Assert.Single(
                store.BatchKinds,
                kinds => kinds.Count > 0
                         && kinds[0] == RuntimeEventKinds.RunStarted);
            Assert.Equal(
                new[]
                {
                    RuntimeEventKinds.RunStarted,
                    RuntimeEventKinds.TranscriptMessage,
                    RuntimeEventKinds.TranscriptMessage,
                    RuntimeEventKinds.RunCheckpoint
                },
                startBatch);

            var events = await inner.ReadRunAsync(run.RunId, default);
            Assert.Equal(
                RunStates.Preparing,
                ProtocolJson.DeserializeAgentRun(
                        events[0].Payload.GetRawText())
                    .State);
            Assert.Equal("initial-1", NormalizedMessageJournalCodec
                .Decode(events[1].Payload)
                .MessageId);
            Assert.Equal("initial-2", NormalizedMessageJournalCodec
                .Decode(events[2].Payload)
                .MessageId);
            Assert.Equal(
                RunStates.Running,
                ProtocolJson.DeserializeAgentRun(
                        events[3].Payload.GetRawText())
                    .State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunStartCrashRetainsInitialContextAndActiveSkill()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 1;
            var skill = Skill(
                "start-recovery-skill",
                "START_RECOVERY_SKILL_INSTRUCTIONS");
            var context = new ContextCandidate(
                "start-recovery-context",
                "world_state",
                Json("""{"marker":"START_RECOVERY_CONTEXT"}"""),
                priority: 100,
                required: true);

            await using (var crashStore =
                         new RejectAfterRunStartBatchStore(inner))
            {
                var unusedProvider = new QueueStreamingProvider(
                    FinalResponse("\"must-not-run\""));
                await using var firstRuntime = CreateRuntimeWithSkills(
                    crashStore,
                    crashStore,
                    unusedProvider,
                    new Host(
                        _ => throw new InvalidOperationException(
                            "No action should be dispatched.")),
                    clock,
                    ids,
                    new[] { skill });

                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = run,
                        Context = new[] { context },
                        ActiveSkills = new[]
                        {
                            new SkillReference(
                                skill.SkillId,
                                skill.Version)
                        }
                    });

                Assert.True(crashStore.RunStartCommitted);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Empty(unusedProvider.Requests);
            }

            var afterCrash = await inner.ReadRunAsync(run.RunId, default);
            Assert.Equal(
                new[]
                {
                    RuntimeEventKinds.RunStarted,
                    RuntimeEventKinds.RunInputCaptured,
                    RuntimeEventKinds.RunCheckpoint
                },
                afterCrash.Select(item => item.Kind));
            Assert.DoesNotContain(
                afterCrash,
                item => item.Kind == RuntimeEventKinds.TurnStarted);

            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            await using var resumedRuntime = CreateRuntimeWithSkills(
                inner,
                inner,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                ids,
                new[] { skill });

            var resumed = await resumedRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, resumed.Run.State);
            var request = Assert.Single(provider.Requests);
            var contextItem = Assert.Single(
                Items(Assert.Single(ContextPayloads(request))));
            Assert.Equal(
                "START_RECOVERY_CONTEXT",
                contextItem
                    .GetProperty("content")
                    .GetProperty("marker")
                    .GetString());
            Assert.Contains(
                "START_RECOVERY_SKILL_INSTRUCTIONS",
                string.Join(
                    "\n",
                    request.Messages
                        .SelectMany(message => message.Parts)
                        .Where(part => part.Json.HasValue)
                        .Select(
                            part => part.Json!.Value.GetRawText())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidRunStartBatchResultDoesNotMutateOrPublish()
    {
        await using var store = new InvalidRunStartBatchStore();
        var clock = new Clock();
        var publisher = new RecordingEventPublisher();
        using var journal = new JournalCoordinator(
            store,
            store,
            clock,
            new Ids(),
            publisher);
        var run = Run(clock.UtcNow);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.CommitRunStartAsync(
                    run,
                    new[]
                    {
                        InitialMessage(
                            "initial-1",
                            "first",
                            clock.UtcNow)
                    },
                    default)
                .AsTask());

        Assert.Equal(RunStates.Queued, run.State);
        Assert.Equal(0, run.Revision);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task InvalidTurnPreparationBatchDoesNotMutateOrPublish()
    {
        await using var store = new InvalidRunStartBatchStore();
        var clock = new Clock();
        var publisher = new RecordingEventPublisher();
        using var journal = new JournalCoordinator(
            store,
            store,
            clock,
            new Ids(),
            publisher);
        var run = Run(clock.UtcNow);
        run.State = RunStates.Running;
        run.Revision = 5;
        const string turnId = "invalid-batch-turn";

        await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.CommitTurnPreparationAsync(
                    run,
                    turnId,
                    "invalid-batch-attempt",
                    Array.Empty<NormalizedMessage>(),
                    new TurnSnapshot
                    {
                        TurnId = turnId,
                        RunId = run.RunId,
                        RuntimeGeneration = run.RuntimeGeneration,
                        ProviderId = "test-provider",
                        ModelId = "test-model",
                        PromptLayoutVersion = "1",
                        StablePrefixHash = "stable",
                        DirectToolDigest = "direct",
                        ContextPolicyVersion = "1",
                        BudgetPolicyVersion = "1",
                        CreatedAt = clock.UtcNow
                    },
                    clock.UtcNow,
                    default)
                .AsTask());

        Assert.Equal(5, run.Revision);
        Assert.Equal(0, run.Usage.Turns);
        Assert.Null(run.CurrentTurnId);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task PartialLegacyInitializationFailsClosedWithoutProviderCall()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Preparing,
                RuntimeEventKinds.RunStarted);
            var first = InitialMessage(
                "initial-1",
                "committed",
                clock.UtcNow);
            _ = InitialMessage(
                "initial-2",
                "missing after crash",
                clock.UtcNow);
            await journal.AppendTranscriptAsync(
                run,
                first,
                "initial",
                attemptId: null,
                default);

            var provider = new QueueStreamingProvider(
                FinalResponse("\"must-not-run\""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids);

            var outcome = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal("runtime_failure", outcome.ErrorCode);
            Assert.Empty(provider.Requests);
            Assert.Equal(
                new[] { first.MessageId },
                outcome.Transcript.Select(message => message.MessageId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreProviderTurnIsAbandonedAndRefundedBeforeResume()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 1;
            const string abandonedTurnId = "turn-before-provider";
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            await journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                default);
            await journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.TurnStarted,
                next =>
                {
                    next.CurrentTurnId = abandonedTurnId;
                    next.Usage.Turns++;
                },
                abandonedTurnId,
                attemptId: "abandoned-attempt");
            await journal.AppendTranscriptAsync(
                run,
                InitialMessage(
                    "durable-turn-input",
                    "must-survive-safe-replay",
                    clock.UtcNow),
                abandonedTurnId,
                "abandoned-attempt",
                default);

            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids);

            var outcome = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, outcome.Run.Usage.Turns);
            Assert.Null(outcome.Run.CurrentTurnId);
            var request = Assert.Single(provider.Requests);
            Assert.Contains(
                request.Messages.SelectMany(message => message.Parts),
                part => part.Text?.Contains(
                    "must-survive-safe-replay",
                    StringComparison.Ordinal) == true);

            var events = await store.ReadRunAsync(run.RunId, default);
            var abandoned = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.TurnCompleted
                        && item.ReasonCode
                        == RunRecovery.ReplaySafeTurnAbandonedReason);
            Assert.Equal(abandonedTurnId, abandoned.TurnId);
            Assert.Equal(
                2,
                events.Count(item => item.Kind == RuntimeEventKinds.TurnStarted));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCrashRetainsRequiredContextAndActiveSkillOnResume()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 1;
            var skill = Skill(
                "recovery-skill",
                "RECOVERED_SKILL_INSTRUCTIONS");
            var context = new ContextCandidate(
                "required-recovery-context",
                "world_state",
                Json("""{"marker":"RECOVERED_REQUIRED_CONTEXT"}"""),
                priority: 100,
                required: true);

            await using (var crashStore =
                         new RejectAfterTurnSnapshotStore(inner))
            {
                var unusedProvider = new QueueStreamingProvider(
                    FinalResponse("\"must-not-run\""));
                await using var firstRuntime = CreateRuntimeWithSkills(
                    crashStore,
                    crashStore,
                    unusedProvider,
                    new Host(
                        _ => throw new InvalidOperationException(
                            "No action should be dispatched.")),
                    clock,
                    ids,
                    new[] { skill });

                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = run,
                        Context = new[] { context },
                        ActiveSkills = new[]
                        {
                            new SkillReference(
                                skill.SkillId,
                                skill.Version)
                        }
                    });

                Assert.True(crashStore.TurnSnapshotCommitted);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Empty(unusedProvider.Requests);
            }

            var preparedEvents = await inner.ReadRunAsync(run.RunId, default);
            var preparedSnapshot = Assert.Single(
                preparedEvents,
                item => item.Kind == RuntimeEventKinds.TurnSnapshot);
            var preparedTurn = preparedEvents
                .Where(
                    item => string.Equals(
                        item.TurnId,
                        preparedSnapshot.TurnId,
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                new[]
                {
                    RuntimeEventKinds.TurnStarted,
                    RuntimeEventKinds.TranscriptMessage,
                    RuntimeEventKinds.TranscriptMessage,
                    RuntimeEventKinds.TurnSnapshot
                },
                preparedTurn.Select(item => item.Kind));
            Assert.Equal(
                Enumerable.Range(
                        checked((int)preparedTurn[0].Sequence),
                        preparedTurn.Length)
                    .Select(value => (long)value),
                preparedTurn.Select(item => item.Sequence));

            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            await using var resumedRuntime = CreateRuntimeWithSkills(
                inner,
                inner,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                ids,
                new[] { skill });

            var resumed = await resumedRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, resumed.Run.State);
            Assert.Equal(1, resumed.Run.Usage.Turns);
            var request = Assert.Single(provider.Requests);
            var contextPayload = Assert.Single(ContextPayloads(request));
            var contextItem = Assert.Single(Items(contextPayload));
            Assert.Equal(
                "required-recovery-context",
                contextItem.GetProperty("id").GetString());
            Assert.Equal(
                "RECOVERED_REQUIRED_CONTEXT",
                contextItem
                    .GetProperty("content")
                    .GetProperty("marker")
                    .GetString());
            Assert.Contains(
                "RECOVERED_SKILL_INSTRUCTIONS",
                string.Join(
                    "\n",
                    request.Messages
                        .Where(
                            message => message.Role
                                       == NormalizedRoles.System)
                        .SelectMany(message => message.Parts)
                        .Where(part => part.Json.HasValue)
                        .Select(
                            part => part.Json!.Value.GetRawText())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CleanTurnBoundaryCrashInheritsDurableActiveSkill()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            var skill = Skill(
                "boundary-recovery-skill",
                "BOUNDARY_RECOVERY_SKILL_INSTRUCTIONS");
            var tool = Tool("read_state");

            await using (var crashStore =
                         new RejectAfterCleanTurnCompletedStore(inner))
            {
                var firstProvider = new QueueStreamingProvider(
                    ToolResponse(
                        "boundary-tool-call",
                        tool.Name,
                        """{"entityId":"npc-7"}"""));
                var firstHost = new Host(
                    request => new ValueTask<ActionReceipt>(
                        Receipt(
                            request,
                            ReceiptStatuses.Succeeded,
                            """{"ok":true}""",
                            clock.UtcNow)));
                await using var firstRuntime = CreateRuntimeWithSkills(
                    crashStore,
                    crashStore,
                    firstProvider,
                    firstHost,
                    clock,
                    ids,
                    new[] { skill },
                    tool);

                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = run,
                        ActiveSkills = new[]
                        {
                            new SkillReference(
                                skill.SkillId,
                                skill.Version)
                        }
                    });

                Assert.True(crashStore.CleanTurnCompleted);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Single(firstProvider.Requests);
                Assert.Equal(1, firstHost.CallCount);
            }

            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            await using var resumedRuntime = CreateRuntimeWithSkills(
                inner,
                inner,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched.")),
                clock,
                ids,
                new[] { skill },
                tool);

            var resumed = await resumedRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, resumed.Run.State);
            var request = Assert.Single(provider.Requests);
            Assert.Contains(
                "BOUNDARY_RECOVERY_SKILL_INSTRUCTIONS",
                string.Join(
                    "\n",
                    request.Messages
                        .SelectMany(message => message.Parts)
                        .Where(part => part.Json.HasValue)
                        .Select(
                            part => part.Json!.Value.GetRawText())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreProviderTurnRefundSurvivesAnotherRecovery()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 1;
            const string abandonedTurnId = "turn-before-second-crash";
            using var journal = new JournalCoordinator(
                inner,
                inner,
                clock,
                ids);
            await journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                default);
            await journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.TurnStarted,
                next =>
                {
                    next.CurrentTurnId = abandonedTurnId;
                    next.Usage.Turns++;
                },
                abandonedTurnId,
                attemptId: "abandoned-attempt");
            await journal.AppendTranscriptAsync(
                run,
                InitialMessage(
                    "durable-turn-input",
                    "must-survive-second-recovery",
                    clock.UtcNow),
                abandonedTurnId,
                "abandoned-attempt",
                default);

            await using (var crashingStore =
                         new ThrowAfterPreProviderAbandonStore(inner))
            {
                var firstProvider = new QueueStreamingProvider(
                    FinalResponse("\"must-not-run\""));
                var firstHost = new Host(
                    _ => throw new InvalidOperationException(
                        "No action should be dispatched."));
                await using var firstRuntime = CreateRuntime(
                    crashingStore,
                    crashingStore,
                    firstProvider,
                    firstHost,
                    clock,
                    ids);

                var interrupted = await firstRuntime.ResumeAsync(run.RunId);

                Assert.True(crashingStore.Triggered);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Empty(firstProvider.Requests);
            }

            var recoveredProvider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            var recoveredHost = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var recoveredRuntime = CreateRuntime(
                inner,
                recoveredProvider,
                recoveredHost,
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, recovered.Run.State);
            Assert.Equal(1, recovered.Run.Usage.Turns);
            Assert.Contains(
                Assert.Single(recoveredProvider.Requests)
                    .Messages
                    .SelectMany(message => message.Parts),
                part => part.Text?.Contains(
                    "must-survive-second-recovery",
                    StringComparison.Ordinal) == true);
            Assert.Single(
                await inner.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.TurnCompleted
                        && item.ReasonCode
                        == RunRecovery.ReplaySafeTurnAbandonedReason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KnownZeroDispatchTurnIsSafelyRefundedBeforeResume()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 1;
            const string turnId = "turn-with-settled-dispatch";
            const string providerAttemptId = "provider-attempt";
            const string streamAttemptId = "stream-attempt";
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            await journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                default);
            await journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.TurnStarted,
                next =>
                {
                    next.CurrentTurnId = turnId;
                    next.Usage.Turns++;
                },
                turnId,
                attemptId: "turn-attempt");
            await journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                _ => { },
                turnId,
                providerAttemptId,
                default,
                eventId: "provider-dispatch:" + streamAttemptId,
                streamAttemptId: streamAttemptId,
                providerId: "test-provider");
            await journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderDispatchKnownZero,
                _ => { },
                turnId,
                providerAttemptId,
                default,
                eventId: "provider-known-zero:" + streamAttemptId,
                streamAttemptId: streamAttemptId,
                providerId: "test-provider",
                reasonCode: "known_zero");

            var provider = new QueueStreamingProvider(
                FinalResponse("\"must-not-run\""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids);

            var outcome = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(1, outcome.Run.Usage.Turns);
            Assert.Single(provider.Requests);
            Assert.Contains(
                await store.ReadRunAsync(run.RunId, default),
                item => item.ReasonCode
                        == RunRecovery.ReplaySafeTurnAbandonedReason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunsTypedContextThroughJournaledToolLoopToFinal()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""),
                FinalResponse("""{"decision":"wait","ticks":2}"""));
            var hostSawJournal = false;
            var host = new Host(
                async request =>
                {
                    var events = await store.ReadRunAsync(request.RunId, default);
                    hostSawJournal = events.Any(
                        item => item.Kind == RuntimeEventKinds.ActionRequested
                                && item.Payload.GetProperty("operationId").GetString()
                                == request.OperationId);
                    return Receipt(
                        request,
                        ReceiptStatuses.Succeeded,
                        """{"hp":17,"visible":true}""",
                        clock.UtcNow);
                });
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids,
                Tool("read_state"));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow),
                    Context = new[]
                    {
                        new ContextCandidate(
                            "context-1",
                            "simulation_tick",
                            Json("""{"tick":91,"weather":{"id":3}}"""),
                            priority: 100,
                            required: true,
                            canDefer: false)
                    }
                });

            Assert.True(hostSawJournal);
            Assert.Equal(
                new[] { "entity:npc-7" },
                host.LastRequest!.ExpectedEffects);
            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("wait", outcome.FinalOutput!.Value
                .GetProperty("decision")
                .GetString());
            Assert.Equal(2, outcome.Run.Usage.Turns);
            Assert.Equal(1, outcome.Run.Usage.Actions);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(
                provider.Requests[0].Messages,
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "\"tick\":91",
                        StringComparison.Ordinal) == true));
            Assert.Contains(
                provider.Requests[1].Messages,
                message => message.Role == NormalizedRoles.Tool
                           && message.Parts[0].Json!.Value
                               .GetProperty("status")
                               .GetString() == ReceiptStatuses.Succeeded);

            var events = await store.ReadRunAsync(outcome.Run.RunId, default);
            Assert.True(
                Array.FindIndex(
                    events.ToArray(),
                    item => item.Kind == RuntimeEventKinds.ActionRequested)
                < Array.FindIndex(
                    events.ToArray(),
                    item => item.Kind == RuntimeEventKinds.ActionReceived));
            var completedTool = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ToolCompleted);
            Assert.StartsWith(
                "tool-result-event:",
                completedTool.EventId,
                StringComparison.Ordinal);
            Assert.Equal(
                2,
                events.Count(
                    item => item.Kind
                            == RuntimeEventKinds.ProviderResultCommitted));
            var toolAssistant = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage
                        && NormalizedMessageJournalCodec
                            .Decode(item.Payload)
                            .Parts.Any(
                                part => part.Type
                                        == NormalizedPartTypes.ToolCall));
            var toolResultMarker = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultCommitted
                        && item.TurnId == toolAssistant.TurnId);
            Assert.Equal(
                toolAssistant.Sequence + 1,
                toolResultMarker.Sequence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TimeAfterWriteAheadDoesNotRestartTheToolDeadline()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new IncrementingClock(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    0,
                    0,
                    TimeSpan.Zero),
                TimeSpan.FromMilliseconds(20));
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                ToolResponse(
                    "deadline-call",
                    "read_state",
                    """{"entityId":"npc-7"}"""),
                FinalResponse("""{"done":true}"""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "An expired action must not reach the host."));
            var tool = Tool("read_state");
            tool.TimeoutMs = 10;
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids,
                tool);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow)
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(0, host.CallCount);
            Assert.Equal(2, provider.Requests.Count);
            var events = await store.ReadRunAsync(
                outcome.Run.RunId,
                default);
            var receipt = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                "tool_deadline_expired",
                receipt.Payload.GetProperty("errorCode").GetString());
            Assert.Equal(
                ReceiptStatuses.Failed,
                receipt.Payload.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LateSuccessfulHostReceiptDoesNotOverrideToolTimeout()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new MutableClock(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    0,
                    0,
                    TimeSpan.Zero));
            var provider = new QueueStreamingProvider(
                ToolResponse(
                    "late-call",
                    "read_state",
                    """{"entityId":"npc-7"}"""));
            var host = new Host(
                request =>
                {
                    clock.Advance(TimeSpan.FromSeconds(2));
                    return new ValueTask<ActionReceipt>(
                        Receipt(
                            request,
                            ReceiptStatuses.Succeeded,
                            """{"late":true}""",
                            clock.UtcNow));
                });
            var tool = Tool("read_state");
            tool.Effect = ToolEffects.WorldCommand;
            tool.IdempotencyPolicy = ToolIdempotencyPolicies.Required;
            tool.TimeoutMs = 1_000;
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                tool);
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.True(
                string.Equals(
                    RunStates.Reconciling,
                    outcome.Run.State,
                    StringComparison.Ordinal),
                $"state={outcome.Run.State}; "
                + $"terminal={outcome.Run.TerminalReason}; "
                + $"error={outcome.ErrorCode}; "
                + $"message={outcome.SafeErrorMessage}; "
                + $"run={ProtocolJson.Serialize(outcome.Run)}");
            Assert.Equal(1, host.CallCount);
            Assert.Single(outcome.Run.PendingOperationIds);
            var events = await store.ReadRunAsync(run.RunId, default);
            var receipt = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                ReceiptStatuses.Unknown,
                receipt.Payload.GetProperty("status").GetString());
            Assert.Equal(
                "tool_timeout",
                receipt.Payload.GetProperty("errorCode").GetString());
            Assert.DoesNotContain(
                events,
                item => item.Kind == RuntimeEventKinds.ToolCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActionConstructionConsumesTheOriginalMonotonicDeadline()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new MutableClock(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    0,
                    0,
                    TimeSpan.Zero));
            var ids = new DeadlineConsumingIds(clock);
            var provider = new QueueStreamingProvider(
                ToolResponse(
                    "deadline-call",
                    "read_state",
                    """{"entityId":"npc-7"}"""),
                FinalResponse("""{"done":true}"""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "An expired action must not reach the host."));
            var tool = Tool("read_state");
            tool.TimeoutMs = 10;
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids,
                tool);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(0, host.CallCount);
            var events = await store.ReadRunAsync(
                outcome.Run.RunId,
                default);
            var receipt = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                "tool_deadline_expired",
                receipt.Payload.GetProperty("errorCode").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeferredContextIsSelectedOnTheNextTurn()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var deferred = new ContextCandidate(
                "deferred-context",
                "state",
                Json("""{"value":"carry me"}"""),
                priority: 0,
                canDefer: true);
            var context = SelectionFillers()
                .Append(deferred)
                .ToArray();
            var provider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""),
                FinalResponse("\"done\""));
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Succeeded,
                        """{"ok":true}""",
                        clock.UtcNow)));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                Tool("read_state"));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow),
                    Context = context
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(2, provider.Requests.Count);
            var firstContext = ContextPayloads(provider.Requests[0]).Last();
            Assert.Equal(
                new[] { deferred.Id },
                Strings(
                    firstContext
                        .GetProperty("budget")
                        .GetProperty("deferredIds")));
            Assert.DoesNotContain(
                Items(firstContext),
                item => item.GetProperty("id").GetString() == deferred.Id);

            var secondContext = ContextPayloads(provider.Requests[1]).Last();
            Assert.Contains(
                Items(secondContext),
                item => item.GetProperty("id").GetString() == deferred.Id);
            Assert.Contains(
                Strings(
                    secondContext
                        .GetProperty("budget")
                        .GetProperty("selectedIds")),
                id => id == deferred.Id);
            Assert.Empty(
                Strings(
                    secondContext
                        .GetProperty("budget")
                        .GetProperty("deferredIds")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeferredContextExpiredBeforeNextTurnIsReportedAsPruned()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new MutableClock(
                new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
            var deferred = new ContextCandidate(
                "expiring-context",
                "state",
                Json("""{"value":"short lived"}"""),
                priority: 0,
                canDefer: true,
                expiresAt: clock.UtcNow.AddSeconds(1));
            var provider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""),
                FinalResponse("\"done\""));
            var host = new Host(
                request =>
                {
                    clock.Advance(TimeSpan.FromSeconds(2));
                    return new ValueTask<ActionReceipt>(
                        Receipt(
                            request,
                            ReceiptStatuses.Succeeded,
                            """{"ok":true}""",
                            clock.UtcNow));
                });
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                Tool("read_state"));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow),
                    Context = SelectionFillers()
                        .Append(deferred)
                        .ToArray()
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            var secondContext = ContextPayloads(provider.Requests[1]).Last();
            Assert.Empty(Items(secondContext));
            Assert.Empty(
                Strings(
                    secondContext
                        .GetProperty("budget")
                        .GetProperty("deferredIds")));
            var pruned = Assert.Single(
                Items(
                    secondContext
                        .GetProperty("budget"),
                    "pruned"));
            Assert.Equal(deferred.Id, pruned.GetProperty("id").GetString());
            Assert.Equal(
                "expired",
                pruned.GetProperty("reasonCode").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeferredContextQueueIsBoundedDeterministically()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var deferred = Enumerable.Range(0, 129)
                .Select(
                    index => new ContextCandidate(
                        $"deferred-{index:D3}",
                        "state",
                        Json($$"""{"value":{{index}}}"""),
                        priority: 0,
                        canDefer: true))
                .ToArray();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"done\""));
            var host = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be dispatched."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids());

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow),
                    Context = SelectionFillers()
                        .Concat(deferred)
                        .ToArray()
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            var budget = ContextPayloads(provider.Requests[0])
                .Last()
                .GetProperty("budget");
            var retainedIds = Strings(budget.GetProperty("deferredIds"));
            Assert.Equal(128, retainedIds.Count);
            Assert.Equal("deferred-000", retainedIds[0]);
            Assert.Equal("deferred-127", retainedIds[^1]);
            var pruned = Assert.Single(Items(budget, "pruned"));
            Assert.Equal("deferred-128", pruned.GetProperty("id").GetString());
            Assert.Equal(
                "deferred_capacity",
                pruned.GetProperty("reasonCode").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContextThatNeverFitsIsPrunedAfterBoundedDeferrals()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var steps = Enumerable.Range(0, 7)
                .Select(
                    index => ToolResponse(
                        $"call-{index}",
                        "read_state",
                        """{"entityId":"npc-7"}"""))
                .Append(FinalResponse("\"done\""))
                .ToArray();
            var provider = new QueueStreamingProvider(steps);
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Succeeded,
                        """{"ok":true}""",
                        clock.UtcNow)));
            await using var runtime = CreateRuntimeWithOptions(
                store,
                provider,
                host,
                clock,
                new Ids(),
                new DurableAgentRuntimeOptions
                {
                    ModelId = "test-model",
                    MaxConcurrentProviderCalls = 2,
                    ToolLoopGuard = new SemanticToolLoopGuardOptions
                    {
                        Enabled = false
                    }
                },
                Tool("read_state"));
            var oversized = new ContextCandidate(
                "oversized-context",
                "state",
                Json("""{"value":"cannot fit"}"""),
                canDefer: true,
                estimatedTokens: 8_001);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(clock.UtcNow),
                    Context = new[] { oversized }
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(8, provider.Requests.Count);
            foreach (var request in provider.Requests.Take(7))
            {
                Assert.Equal(
                    new[] { oversized.Id },
                    Strings(
                        ContextPayloads(request)
                            .Last()
                            .GetProperty("budget")
                            .GetProperty("deferredIds")));
            }

            var finalBudget = ContextPayloads(provider.Requests[^1])
                .Last()
                .GetProperty("budget");
            Assert.Empty(Strings(finalBudget.GetProperty("deferredIds")));
            var pruned = Assert.Single(Items(finalBudget, "pruned"));
            Assert.Equal(oversized.Id, pruned.GetProperty("id").GetString());
            Assert.Equal(
                "deferred_turn_limit",
                pruned.GetProperty("reasonCode").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeSelectsDeferredContextWhenContinuationResuppliesIt()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var deferred = new ContextCandidate(
                "resupplied-context",
                "state",
                Json("""{"value":"resubmit after recovery"}"""),
                priority: 0,
                canDefer: true);
            var firstProvider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""));
            var firstHost = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Unknown,
                        result: null,
                        clock.UtcNow)));
            DurableRunOutcome first;
            await using (var firstRuntime = CreateRuntime(
                             store,
                             firstProvider,
                             firstHost,
                             clock,
                             ids,
                             Tool("read_state")))
            {
                first = await firstRuntime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = Run(clock.UtcNow),
                        Context = SelectionFillers()
                            .Append(deferred)
                            .ToArray()
                    });
            }

            Assert.Equal(RunStates.Reconciling, first.Run.State);
            var firstContext = ContextPayloads(firstProvider.Requests[0]).Last();
            Assert.DoesNotContain(
                Items(firstContext),
                item => item.GetProperty("id").GetString() == deferred.Id);
            Assert.Contains(
                Strings(
                    firstContext
                        .GetProperty("budget")
                        .GetProperty("deferredIds")),
                id => id == deferred.Id);

            var recoveredProvider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            var recoveredHost = new Host(
                _ => throw new InvalidOperationException(
                    "A reconciled operation must not be redispatched."));
            await using var recoveredRuntime = CreateRuntime(
                store,
                recoveredProvider,
                recoveredHost,
                clock,
                ids,
                Tool("read_state"));

            var recovered = await recoveredRuntime.ResumeAsync(
                first.Run.RunId,
                new DurableRunContinuation
                {
                    Context = new[] { deferred }
                },
                new Reconciler(clock.UtcNow));

            Assert.Equal(RunStates.Completed, recovered.Run.State);
            var resumedContext = ContextPayloads(
                    Assert.Single(recoveredProvider.Requests))
                .Last();
            Assert.Contains(
                Items(resumedContext),
                item => item.GetProperty("id").GetString() == deferred.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeDoesNotInventDeferredContextWithoutResupply()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var deferred = new ContextCandidate(
                "non-durable-context",
                "state",
                Json("""{"value":"live execution only"}"""),
                canDefer: true,
                estimatedTokens: 8_001);
            var firstProvider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""));
            var firstHost = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Unknown,
                        result: null,
                        clock.UtcNow)));
            DurableRunOutcome first;
            await using (var firstRuntime = CreateRuntime(
                             store,
                             firstProvider,
                             firstHost,
                             clock,
                             ids,
                             Tool("read_state")))
            {
                first = await firstRuntime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = Run(clock.UtcNow),
                        Context = new[] { deferred }
                    });
            }

            Assert.Equal(RunStates.Reconciling, first.Run.State);
            var initialPayload = Assert.Single(
                ContextPayloads(firstProvider.Requests[0]));
            Assert.Equal(
                new[] { deferred.Id },
                Strings(
                    initialPayload
                        .GetProperty("budget")
                        .GetProperty("deferredIds")));

            var recoveredProvider = new QueueStreamingProvider(
                FinalResponse("\"recovered\""));
            var recoveredHost = new Host(
                _ => throw new InvalidOperationException(
                    "A reconciled operation must not be redispatched."));
            await using var recoveredRuntime = CreateRuntime(
                store,
                recoveredProvider,
                recoveredHost,
                clock,
                ids,
                Tool("read_state"));

            var recovered = await recoveredRuntime.ResumeAsync(
                first.Run.RunId,
                reconciler: new Reconciler(clock.UtcNow));

            Assert.Equal(RunStates.Completed, recovered.Run.State);
            var recoveredRequest = Assert.Single(recoveredProvider.Requests);
            var historicalPayload = Assert.Single(
                ContextPayloads(recoveredRequest));
            Assert.Empty(Items(historicalPayload));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownActionRequiresReconciliationAndNeverRedispatches()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                ToolResponse("call-1", "read_state", """{"entityId":"npc-7"}"""),
                FinalResponse("recovered"));
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Unknown,
                        result: null,
                        clock.UtcNow)));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids,
                Tool("read_state"));

            var first = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });

            Assert.Equal(RunStates.Reconciling, first.Run.State);
            Assert.True(first.ReconciliationRequired);
            Assert.Equal(1, host.CallCount);

            var resumed = await runtime.ResumeAsync(
                first.Run.RunId,
                reconciler: new Reconciler(clock.UtcNow));

            Assert.True(
                string.Equals(
                    resumed.Run.State,
                    RunStates.Completed,
                    StringComparison.Ordinal),
                $"state={resumed.Run.State} code={resumed.ErrorCode} "
                + $"category={resumed.ErrorCategory} message={resumed.SafeErrorMessage}");
            Assert.Equal("recovered", resumed.FinalOutput!.Value.GetString());
            Assert.Equal(1, host.CallCount);
            Assert.Contains(
                provider.Requests.Last().Messages,
                message => message.Role == NormalizedRoles.Tool);
            Assert.Contains(
                await store.ReadRunAsync(first.Run.RunId, default),
                item => item.Kind == RuntimeEventKinds.ToolCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SteerAfterAccountedUsageCancelsStaleStreamAndStartsNewTurn()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var provider = new SteerStreamingProvider();
            var host = new Host(
                _ => throw new InvalidOperationException("No tool expected."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids);
            var run = Run(clock.UtcNow);

            var running = runtime.RunAsync(
                new DurableRunRequest { Run = run }).AsTask();
            await provider.FirstAttemptStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(
                runtime.Controls.TryPost(
                    run.RunId,
                    new RunControlCommand
                    {
                        CommandId = "steer-1",
                        Kind = RunControlKinds.Steer,
                        Observation = Observation(
                            run.WorldId,
                            clock.UtcNow,
                            """{"priorityTarget":"gate-3"}"""),
                        CreatedAt = clock.UtcNow
                    }));

            var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("steered-final", outcome.FinalOutput!.Value.GetString());
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(
                provider.Requests[1].Messages,
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "gate-3",
                        StringComparison.Ordinal) == true));
            Assert.DoesNotContain(
                outcome.Transcript.SelectMany(item => item.Parts),
                part => string.Equals(
                    part.Text,
                    "stale",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCompletionRacingSteerClosesTheDiscardedDispatch()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var provider = new CompletionSteerRaceProvider();
            var host = new Host(
                _ => throw new InvalidOperationException("No tool expected."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids);
            var run = Run(clock.UtcNow);

            var running = runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask();
            await provider.FirstUsageObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(
                runtime.Controls.TryPost(
                    run.RunId,
                    new RunControlCommand
                    {
                        CommandId = "completion-race-steer",
                        Kind = RunControlKinds.Steer,
                        Observation = Observation(
                            run.WorldId,
                            clock.UtcNow,
                            """{"priorityTarget":"gate-4"}"""),
                        CreatedAt = clock.UtcNow
                    }));
            provider.ReleaseFirstCompletion.TrySetResult();

            var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("steered-final", outcome.FinalOutput!.Value.GetString());
            Assert.Equal(2, provider.Requests.Count);
            var firstRequest = provider.Requests[0];
            var events = await store.ReadRunAsync(run.RunId, default);
            var dispatch = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ProviderDispatchStarted
                        && item.StreamAttemptId
                        == firstRequest.StreamAttemptId);
            var usage = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.BudgetUpdated
                        && item.StreamAttemptId
                        == firstRequest.StreamAttemptId);
            var discarded = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultDiscarded
                        && item.StreamAttemptId
                        == firstRequest.StreamAttemptId);
            Assert.Equal(dispatch.ProviderId, usage.ProviderId);
            Assert.Equal(dispatch.AttemptId, usage.AttemptId);
            Assert.Equal(dispatch.ProviderId, discarded.ProviderId);
            Assert.Equal(dispatch.AttemptId, discarded.AttemptId);
            Assert.True(usage.Sequence < discarded.Sequence);
            Assert.Contains(
                discarded.ReasonCode,
                new[]
                {
                    "provider_attempt_cancelled",
                    "provider_result_steered"
                });

            using var recoveryJournal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var loaded = await new RunRecovery(
                    store,
                    store,
                    recoveryJournal)
                .LoadAsync(run.RunId, default);
            Assert.NotNull(loaded);
            Assert.Empty(loaded!.UnsettledProviderDispatches);

            var resumed = await runtime.ResumeAsync(run.RunId);
            Assert.Equal(RunStates.Completed, resumed.Run.State);
            Assert.Null(resumed.ErrorCode);
            Assert.Equal(2, provider.Requests.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SteerBeforeUsageFailsClosedWithoutSecondProviderRequest()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new UnaccountedSteerProvider();
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);

            var running = runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask();
            await provider.FirstAttemptStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(
                runtime.Controls.TryPost(
                    run.RunId,
                    new RunControlCommand
                    {
                        CommandId = "steer-before-usage",
                        Kind = RunControlKinds.Steer,
                        Observation = Observation(
                            run.WorldId,
                            clock.UtcNow,
                            """{"priorityTarget":"gate-3"}"""),
                        CreatedAt = clock.UtcNow
                    }));

            var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                outcome.Run.TerminalReason);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                outcome.ErrorCode);
            Assert.Equal("billing", outcome.ErrorCategory);
            Assert.True(outcome.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownToolReturnsSafeModelErrorWithoutCallingHost()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                ToolResponse("call-1", "not_registered", """{"secret":"value"}"""),
                FinalResponse("replanned"));
            var host = new Host(
                _ => throw new InvalidOperationException("No tool expected."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids());

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(0, host.CallCount);
            var error = provider.Requests[1].Messages.Single(
                item => item.Role == NormalizedRoles.Tool);
            Assert.Equal(
                "unknown_tool",
                error.Parts[0].Json!.Value.GetProperty("code").GetString());
            Assert.DoesNotContain(
                "value",
                error.Parts[0].Json!.Value.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidToolArgumentsAreRejectedBeforeHostDispatch()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                ToolResponse(
                    "call-1",
                    "read_state",
                    """{"secret":"must-not-leak"}"""),
                FinalResponse("replanned"));
            var host = new Host(
                _ => throw new InvalidOperationException("No tool expected."));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                Tool("read_state"));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = Run(clock.UtcNow) });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(0, host.CallCount);
            var error = provider.Requests[1].Messages.Single(
                item => item.Role == NormalizedRoles.Tool);
            Assert.DoesNotContain(
                "must-not-leak",
                error.Parts[0].Json!.Value.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurationDeadlineStopsASlowProviderAsBudgetExhausted()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            await using var runtime = CreateRuntime(
                store,
                new SlowStreamingProvider(),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 75;

            var outcome = await runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Empty(outcome.Run.PendingOperationIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderTokenOvershootEndsRunWithoutReturningFinalOutput()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                FinalResponseWithUsage(
                    "\"must-not-return\"",
                    inputTokens: 160,
                    outputTokens: 50,
                    costUsd: "0.001"));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);
            var run = Run(clock.UtcNow);
            run.Budget.MaxTokens = 200;

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_tokens", outcome.Run.TerminalReason);
            Assert.Null(outcome.FinalOutput);
            Assert.Equal(210, outcome.Run.Usage.InputTokens
                             + outcome.Run.Usage.OutputTokens);
            Assert.DoesNotContain(
                outcome.Transcript,
                message => message.Role == NormalizedRoles.Assistant);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExactProviderUsageBoundaryCanComplete()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                FinalResponseWithUsage(
                    "\"ok\"",
                    inputTokens: 160,
                    outputTokens: 40,
                    costUsd: "0.001"));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);
            var run = Run(clock.UtcNow);
            run.Budget.MaxTokens = 200;
            run.Budget.MaxCostUsd = "0.001";

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("ok", outcome.FinalOutput!.Value.GetString());
            Assert.InRange(
                Assert.Single(provider.Requests).MaxOutputTokens!.Value,
                1,
                199);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCostOvershootEndsBudgetExhausted()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                FinalResponseWithUsage(
                    "\"must-not-return\"",
                    inputTokens: 1,
                    outputTokens: 1,
                    costUsd: "0.0011"));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);
            var run = Run(clock.UtcNow);
            run.Budget.MaxCostUsd = "0.001";

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_cost", outcome.Run.TerminalReason);
            Assert.Null(outcome.FinalOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LengthFinishFailsOnceAfterPersistingProviderUsage()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new LengthFinishProvider();
            await using var runtime = CreateRetryingRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal("provider_output_incomplete", outcome.ErrorCode);
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(10, outcome.Run.Usage.InputTokens);
            Assert.Equal(2, outcome.Run.Usage.OutputTokens);
            Assert.Equal("0.003", outcome.Run.Usage.CostUsd);
            Assert.Null(outcome.FinalOutput);
            Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.BudgetUpdated
                        && item.EventId.StartsWith(
                            "provider-usage:",
                            StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyProviderResponseRetriesAndPersistsEveryAttemptUsage()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                request => new[]
                {
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.Usage,
                        Usage = new ProviderUsage
                        {
                            InputTokens = 3,
                            OutputTokens = 2,
                            CostUsd = "0.001"
                        }
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    }
                },
                FinalResponseWithUsage(
                    "recovered",
                    inputTokens: 5,
                    outputTokens: 4,
                    costUsd: "0.002"));
            await using var runtime = CreateRetryingRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(8, outcome.Run.Usage.InputTokens);
            Assert.Equal(6, outcome.Run.Usage.OutputTokens);
            Assert.Equal("0.003", outcome.Run.Usage.CostUsd);
            Assert.Equal(
                "recovered",
                outcome.FinalOutput!.Value.GetString());
            Assert.Equal(
                2,
                (await store.ReadRunAsync(run.RunId, default)).Count(
                    item => item.Kind == RuntimeEventKinds.BudgetUpdated
                            && item.EventId.StartsWith(
                                "provider-usage:",
                                StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationCannotEraseObservedProviderUsage()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var intercept = new BudgetAppendInterceptStore(store);
            var clock = new Clock();
            await using var runtime = CreateRuntime(
                intercept,
                intercept,
                new QueueStreamingProvider(
                    FinalResponseWithUsage(
                        "\"must-not-return\"",
                        inputTokens: 7,
                        outputTokens: 3,
                        costUsd: "0.004")),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            using var cancellation = new CancellationTokenSource();

            var pending = runtime.RunAsync(
                    new DurableRunRequest { Run = run },
                    cancellation.Token)
                .AsTask();
            await intercept.ProviderUsageAppendStarted.Task.WaitAsync(
                TestWaitTimeout);
            cancellation.Cancel();
            intercept.ReleaseProviderUsageAppend.TrySetResult();

            var outcome = await pending.WaitAsync(TestWaitTimeout);

            Assert.Equal(RunStates.Cancelled, outcome.Run.State);
            Assert.Equal(7, outcome.Run.Usage.InputTokens);
            Assert.Equal(3, outcome.Run.Usage.OutputTokens);
            Assert.Equal("0.004", outcome.Run.Usage.CostUsd);
            var usageEvent = Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.BudgetUpdated
                        && item.EventId.StartsWith(
                            "provider-usage:",
                            StringComparison.Ordinal));
            var persisted = ProtocolJson.DeserializeAgentRun(
                usageEvent.Payload.GetRawText());
            Assert.Equal(7, persisted.Usage.InputTokens);
            Assert.Equal(3, persisted.Usage.OutputTokens);
            Assert.Equal("0.004", persisted.Usage.CostUsd);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationBeforeUsagePersistsUnaccountedUsageAcrossRecovery()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new CancellationBeforeUsageProvider();
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            using var cancellation = new CancellationTokenSource();

            var pending = runtime.RunAsync(
                    new DurableRunRequest { Run = run },
                    cancellation.Token)
                .AsTask();
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(RunStates.Cancelled, outcome.Run.State);
            Assert.True(outcome.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                outcome.Run.Usage.UnaccountedProviderAttempts);
            var uncertain = Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.StartsWith(
                "provider-usage-uncertain:",
                uncertain.EventId,
                StringComparison.Ordinal);

            var recovered = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Cancelled, recovered.Run.State);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                recovered.Run.Usage.UnaccountedProviderAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderSelfCancellationFailsRunAndPersistsUnknownUsage()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            await using var runtime = CreateRuntime(
                store,
                new SelfCancellingBeforeUsageProvider(),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.NotEqual(RunStates.Cancelled, outcome.Run.State);
            Assert.Equal("provider_usage_unknown", outcome.ErrorCode);
            Assert.True(outcome.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                outcome.Run.Usage.UnaccountedProviderAttempts);
            Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);

            var recovered = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                recovered.Run.Usage.UnaccountedProviderAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryFailsClosedAfterUncertainUsageCheckpointCrash()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var crashStore = new FailAfterUncertainUsageStore(store);
            var run = Run(clock.UtcNow);

            await using (var firstRuntime = CreateRuntimeWithOptions(
                       crashStore,
                       crashStore,
                       new SelfCancellingBeforeUsageProvider(),
                       new Host(
                           _ => throw new InvalidOperationException(
                               "No tool expected.")),
                       clock,
                       ids,
                       new DurableAgentRuntimeOptions
                       {
                           ModelId = "test-model",
                           MaxConcurrentProviderCalls = 2
                       }))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.Equal(RunStates.Running, interrupted.Run.State);
                Assert.True(interrupted.Run.Usage.HasUnaccountedUsage);
                Assert.True(crashStore.RejectedPostCheckpointAppend);
            }

            var uncertain = Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(
                "self-cancelling-before-usage",
                uncertain.ProviderId);
            Assert.Equal("provider_usage_unknown", uncertain.ReasonCode);
            Assert.False(string.IsNullOrWhiteSpace(uncertain.AttemptId));
            Assert.False(string.IsNullOrWhiteSpace(
                uncertain.StreamAttemptId));
            Assert.EndsWith(
                uncertain.StreamAttemptId,
                uncertain.EventId,
                StringComparison.Ordinal);

            var provider = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                recovered.Run.TerminalReason);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                recovered.ErrorCode);
            Assert.Equal("billing", recovered.ErrorCategory);
            Assert.Equal(0, provider.CallCount);
            Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.RunFailed
                        && item.Payload.GetProperty("terminalReason")
                            .GetString()
                            == "provider_usage_reconciliation_required");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalRecoveryClosesEveryOpenProviderDispatch()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);
            const string turnId = "terminal-recovery-turn";
            const string uncertainAttempt = "terminal-uncertain-attempt";
            const string uncertainStream = "terminal-uncertain-stream";
            const string billedAttempt = "terminal-billed-attempt";
            const string billedStream = "terminal-billed-stream";
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       clock,
                       ids))
            {
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                await journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    _ => { },
                    turnId,
                    uncertainAttempt,
                    eventId: "provider-dispatch:" + uncertainStream,
                    streamAttemptId: uncertainStream,
                    providerId: "terminal-provider-uncertain");
                await journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    _ => { },
                    turnId,
                    billedAttempt,
                    eventId: "provider-dispatch:" + billedStream,
                    streamAttemptId: billedStream,
                    providerId: "terminal-provider-billed");
                await journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.BudgetUpdated,
                    next =>
                    {
                        next.Usage.InputTokens = 11;
                        next.Usage.OutputTokens = 3;
                        next.Usage.CostUsd = "0.007";
                    },
                    turnId,
                    billedAttempt,
                    eventId: "provider-usage:" + billedStream,
                    streamAttemptId: billedStream,
                    providerId: "terminal-provider-billed");
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Failed,
                    RuntimeEventKinds.RunFailed,
                    terminalReason: "preexisting_terminal_failure",
                    completionIntent: CompletionIntents.Failed,
                    turnId: turnId);
            }

            var terminalKinds = new HashSet<string>(
                new[]
                {
                    RuntimeEventKinds.RunCompleted,
                    RuntimeEventKinds.RunFailed,
                    RuntimeEventKinds.RunCancelled,
                    RuntimeEventKinds.RunInterrupted,
                    RuntimeEventKinds.RunBudgetExhausted
                },
                StringComparer.Ordinal);
            var terminalEventsBefore = (await store.ReadRunAsync(
                    run.RunId,
                    default))
                .Count(item => terminalKinds.Contains(item.Kind));
            var provider = new NeverInvokedProvider();
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);

            var recovered = await runtime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "preexisting_terminal_failure",
                recovered.Run.TerminalReason);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                recovered.Run.Usage.UnaccountedProviderAttempts);
            Assert.Equal(11, recovered.Run.Usage.InputTokens);
            Assert.Equal(3, recovered.Run.Usage.OutputTokens);
            Assert.Equal("0.007", recovered.Run.Usage.CostUsd);
            Assert.Equal(0, provider.CallCount);

            var events = await store.ReadRunAsync(run.RunId, default);
            var uncertain = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(
                "terminal-provider-uncertain",
                uncertain.ProviderId);
            Assert.Equal(uncertainAttempt, uncertain.AttemptId);
            Assert.Equal(uncertainStream, uncertain.StreamAttemptId);
            Assert.Equal(
                "terminal_provider_dispatch_unknown",
                uncertain.ReasonCode);
            var discarded = Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultDiscarded);
            Assert.Equal("terminal-provider-billed", discarded.ProviderId);
            Assert.Equal(billedAttempt, discarded.AttemptId);
            Assert.Equal(billedStream, discarded.StreamAttemptId);
            Assert.Equal(
                "terminal_provider_result_recovery",
                discarded.ReasonCode);
            Assert.Equal(
                terminalEventsBefore,
                events.Count(
                    item => terminalKinds.Contains(item.Kind)));

            var loaded = await new RunRecovery(
                    store,
                    store,
                    new JournalCoordinator(
                        store,
                        store,
                        clock,
                        ids))
                .LoadAsync(run.RunId, default);
            Assert.NotNull(loaded);
            Assert.Empty(loaded!.UnsettledProviderDispatches);

            var eventCount = events.Count;
            var resumedAgain = await runtime.ResumeAsync(run.RunId);
            Assert.Equal(RunStates.Failed, resumedAgain.Run.State);
            Assert.Equal(
                eventCount,
                (await store.ReadRunAsync(run.RunId, default)).Count);
            Assert.Equal(0, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousFinalCompletionAckRecoversWithoutProviderReplay()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var clock = new Clock();
        var ids = new Ids();
        var run = Run(clock.UtcNow);
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                var ambiguousStore =
                    new AmbiguousFinalCompletionStore(store);
                var provider = new QueueStreamingProvider(
                    FinalResponseWithUsage(
                        "\"durable-final\"",
                        inputTokens: 5,
                        outputTokens: 2,
                        costUsd: "0.001"));
                await using var runtime = CreateRuntime(
                    ambiguousStore,
                    store,
                    provider,
                    new Host(
                        _ => throw new InvalidOperationException(
                            "No tool expected.")),
                    clock,
                    ids);

                var interrupted = await runtime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.True(ambiguousStore.FinalBatchCommitted);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Single(provider.Requests);
                var events = await store.ReadRunAsync(run.RunId, default);
                var transcriptEvent = Assert.Single(
                    events,
                    item => item.Kind == RuntimeEventKinds.TranscriptMessage
                            && NormalizedMessageJournalCodec
                                .Decode(item.Payload)
                                .Role == NormalizedRoles.Assistant);
                Assert.Equal(
                    "\"durable-final\"",
                    Assert.Single(
                        NormalizedMessageJournalCodec
                            .Decode(transcriptEvent.Payload)
                            .Parts,
                        part => part.Text is not null)
                        .Text);
                var resultEvent = Assert.Single(
                    events,
                    item => item.Kind
                            == RuntimeEventKinds.ProviderResultCommitted);
                var usageEvent = Assert.Single(
                    events,
                    item => item.Kind == RuntimeEventKinds.BudgetUpdated
                            && !string.IsNullOrWhiteSpace(
                                item.StreamAttemptId));
                Assert.Equal(usageEvent.ProviderId, resultEvent.ProviderId);
                Assert.Equal(usageEvent.AttemptId, resultEvent.AttemptId);
                Assert.Equal(
                    usageEvent.StreamAttemptId,
                    resultEvent.StreamAttemptId);
                Assert.Equal(
                    transcriptEvent.Sequence + 1,
                    resultEvent.Sequence);
                Assert.Equal(
                    "durable-final",
                    Assert.Single(
                            events,
                            item => item.Kind
                                    == RuntimeEventKinds.AssistantCompleted)
                        .Payload
                        .GetString());
                Assert.Single(
                    events,
                    item => item.Kind == RuntimeEventKinds.RunCompleted);
            }

            await using var recoveredStore = new FileSessionStore(path);
            var providerAfterRestart = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                recoveredStore,
                providerAfterRestart,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Completed, recovered.Run.State);
            Assert.Equal(
                "durable-final",
                recovered.FinalOutput!.Value.GetString());
            var assistant = Assert.Single(
                recovered.Transcript,
                message => message.Role == NormalizedRoles.Assistant);
            Assert.Equal(
                "\"durable-final\"",
                Assert.Single(
                    assistant.Parts,
                    part => part.Text is not null)
                    .Text);
            Assert.Equal(0, providerAfterRestart.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KnownZeroRetryDoesNotMarkUsageUncertain()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new KnownZeroRetryProvider();
            await using var runtime = CreateRetryingRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(2, provider.CallCount);
            Assert.False(outcome.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                0,
                outcome.Run.Usage.UnaccountedProviderAttempts);
            var events = await store.ReadRunAsync(run.RunId, default);
            Assert.DoesNotContain(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderDispatchKnownZero);
            Assert.Single(
                events,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultCommitted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderTokenCounterOverflowSaturatesAndExhaustsBudget()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                FinalResponseWithUsage(
                    "\"must-not-return\"",
                    inputTokens: 2_000,
                    outputTokens: 0,
                    costUsd: "0"));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);
            var run = Run(clock.UtcNow);
            run.Budget.MaxTokens = int.MaxValue;
            run.Usage.InputTokens = int.MaxValue - 1_000;

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_tokens", outcome.Run.TerminalReason);
            Assert.Equal(int.MaxValue, outcome.Run.Usage.InputTokens);
            Assert.Null(outcome.FinalOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitialTranscriptIsRejectedBeforeJournalWhenPromptIsTooLarge()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            await using var runtime = CreateRuntimeWithOptions(
                store,
                new QueueStreamingProvider(FinalResponse("\"unused\"")),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids,
                new DurableAgentRuntimeOptions
                {
                    ModelId = "test-model",
                    MaxPromptUtf8Bytes = 128
                });
            var run = Run(clock.UtcNow);
            var request = new DurableRunRequest
            {
                Run = run,
                InitialTranscript = new[]
                {
                    new NormalizedMessage
                    {
                        MessageId = "initial-1",
                        Role = NormalizedRoles.User,
                        CreatedAt = clock.UtcNow,
                        Parts = new List<NormalizedContentPart>
                        {
                            NormalizedContentPart.FromText(new string('x', 512))
                        }
                    }
                }
            };

            var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => runtime.RunAsync(request).AsTask());

            Assert.Equal("prompt_bytes_exceeded", error.LimitCode);
            Assert.Empty(await store.ReadRunAsync(run.RunId, default));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConstructorSnapshotsCallerOwnedRuntimeOptions()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"ok\""));
            var options = new DurableAgentRuntimeOptions
            {
                ModelId = "stable-model",
                MaxConcurrentProviderCalls = 2,
                MaxTranscriptMessages = 8,
                MaxPromptUtf8Bytes = 4_096,
                EstimatedPromptBytesPerToken = 4
            };
            await using var runtime = CreateRuntimeWithOptions(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids(),
                options);

            options.ModelId = "mutated-model";
            options.MaxConcurrentProviderCalls = 1;
            options.MaxTranscriptMessages = 1;
            options.MaxPromptUtf8Bytes = 1;
            options.EstimatedPromptBytesPerToken = 64;
            options.SkillDisclosureBudget = new SkillDisclosureBudget(
                maxCatalogItems: 0,
                maxCatalogUtf8Bytes: 0,
                maxActivatedSkills: 0,
                maxPromptFragments: 0,
                maxPromptUtf8Bytes: 0,
                maxReferences: 0);
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = run,
                    InitialTranscript = new[]
                    {
                        InitialMessage(
                            "options-message-1",
                            "first stable message",
                            clock.UtcNow),
                        InitialMessage(
                            "options-message-2",
                            "second stable message",
                            clock.UtcNow)
                    }
                });

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("ok", outcome.FinalOutput!.Value.GetString());
            Assert.Contains(
                provider.Requests[0].Messages,
                message => message.MessageId == "options-message-1");
            Assert.Contains(
                provider.Requests[0].Messages,
                message => message.MessageId == "options-message-2");
            var snapshot = Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.TurnSnapshot);
            Assert.Equal(
                "stable-model",
                snapshot.Payload.GetProperty("modelId").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunEntryDeepSnapshotsMutableRequestGraphBeforeWaiting()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var holderRun = Run(clock.UtcNow);
            holderRun.RunId = "snapshot-lane-holder";
            var provider = new LaneSnapshotProvider(holderRun.RunId);
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var holding = runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = holderRun,
                        LaneId = "snapshot-lane"
                    })
                .AsTask();
            await provider.HolderStarted.Task.WaitAsync(
                TestWaitTimeout);

            var originalRun = Run(clock.UtcNow);
            originalRun.RunId = "snapshot-original-run";
            var originalMessage = InitialMessage(
                "snapshot-message",
                "stable transcript value",
                clock.UtcNow);
            var transcript = new List<NormalizedMessage> { originalMessage };
            var context = new List<ContextCandidate>
            {
                new(
                    "snapshot-context",
                    "structured_input",
                    Json("""{"value":"stable context value"}"""),
                    priority: 10,
                    required: true,
                    canDefer: false)
            };
            var request = new DurableRunRequest
            {
                Run = originalRun,
                InitialTranscript = transcript,
                Context = context,
                LaneId = "snapshot-lane"
            };

            var pending = runtime.RunAsync(request).AsTask();
            request.Run.RunId = "mutated-run";
            request.Run.AgentId = "mutated-agent";
            request.Run.Budget.MaxTurns = 0;
            request.LaneId = "mutated-lane";
            originalMessage.MessageId = "mutated-message";
            originalMessage.Parts[0].Text = "mutated transcript value";
            transcript.Clear();
            context.Clear();
            request.InitialTranscript = Array.Empty<NormalizedMessage>();
            request.Context = Array.Empty<ContextCandidate>();

            Assert.Single(provider.Requests);
            provider.ReleaseHolder.TrySetResult();
            await holding.WaitAsync(TestWaitTimeout);
            var outcome = await pending.WaitAsync(TestWaitTimeout);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("snapshot-original-run", outcome.Run.RunId);
            Assert.Equal("agent-1", outcome.Run.AgentId);
            Assert.Equal(8, outcome.Run.Budget.MaxTurns);
            Assert.Equal(2, provider.Requests.Count);
            var providerRequest = provider.Requests[1];
            Assert.Equal("snapshot-original-run", providerRequest.RunId);
            Assert.Contains(
                providerRequest.Messages,
                message => message.MessageId == "snapshot-message"
                           && message.Parts.Any(
                               part => part.Text == "stable transcript value"));
            Assert.Contains(
                providerRequest.Messages,
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "stable context value",
                        StringComparison.Ordinal) == true));
            Assert.DoesNotContain(
                providerRequest.Messages.SelectMany(message => message.Parts),
                part => part.Text == "mutated transcript value");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeEntryDeepSnapshotsMutableContinuationBeforeWaiting()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var targetRun = Run(clock.UtcNow);
            targetRun.RunId = "continuation-target-run";
            var holderRun = Run(clock.UtcNow);
            holderRun.RunId = "continuation-lane-holder";
            var provider = new ContinuationSnapshotProvider(
                targetRun.RunId,
                holderRun.RunId);
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Unknown,
                        result: null,
                        clock.UtcNow)));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                Tool("read_state"));

            var initial = await runtime.RunAsync(
                new DurableRunRequest { Run = targetRun });
            Assert.Equal(RunStates.Reconciling, initial.Run.State);

            var holding = runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = holderRun,
                        LaneId = "continuation-lane"
                    })
                .AsTask();
            await provider.HolderStarted.Task.WaitAsync(
                TestWaitTimeout);

            var context = new List<ContextCandidate>
            {
                new(
                    "continuation-context",
                    "structured_input",
                    Json("""{"value":"stable continuation value"}"""),
                    priority: 10,
                    required: true,
                    canDefer: false)
            };
            var continuation = new DurableRunContinuation
            {
                Context = context,
                LaneId = "continuation-lane"
            };

            var pending = runtime.ResumeAsync(
                    targetRun.RunId,
                    continuation,
                    new Reconciler(clock.UtcNow))
                .AsTask();
            continuation.LaneId = "mutated-lane";
            context.Clear();
            continuation.Context = Array.Empty<ContextCandidate>();

            Assert.Equal(2, provider.Requests.Count);
            provider.ReleaseHolder.TrySetResult();
            await holding.WaitAsync(TestWaitTimeout);
            var outcome = await pending.WaitAsync(TestWaitTimeout);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal("resumed", outcome.FinalOutput!.Value.GetString());
            var resumedRequest = provider.Requests.Last(
                request => request.RunId == targetRun.RunId);
            Assert.Contains(
                resumedRequest.Messages,
                message => message.Parts.Any(
                    part => part.Json?.GetRawText().Contains(
                        "stable continuation value",
                        StringComparison.Ordinal) == true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PromptEstimateIsReservedBeforeProviderDispatch()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var provider = new QueueStreamingProvider(
                FinalResponse("\"unused\""));
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                ids);
            var run = Run(clock.UtcNow);
            run.Budget.MaxTokens = 1;

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_tokens", outcome.Run.TerminalReason);
            Assert.Empty(provider.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetryDoesNotDispatchAfterTurnOutputBudgetIsConsumed()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new BudgetExhaustingRetryProvider();
            await using var runtime = CreateRetryingRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            run.Budget.MaxTokens = 1_000;

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_tokens", outcome.Run.TerminalReason);
            var request = Assert.Single(provider.Requests);
            Assert.True(request.MaxOutputTokens.HasValue);
            Assert.Equal(
                request.MaxOutputTokens.Value,
                outcome.Run.Usage.InputTokens);
            Assert.Equal(0, outcome.Run.Usage.OutputTokens);
            Assert.Null(outcome.FinalOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurationDeadlineLeavesDispatchedSlowToolForReconciliation()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var host = new SlowHost();
            await using var runtime = CreateRuntime(
                store,
                new QueueStreamingProvider(
                    ToolResponse(
                        "call-1",
                        "read_state",
                        """{"entityId":"npc-7"}""")),
                host,
                clock,
                new Ids(),
                Tool("read_state"));
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 1_000;

            var execution = runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask();
            await host.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RunStates.Reconciling, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Single(outcome.Run.PendingOperationIds);
            var events = await store.ReadRunAsync(run.RunId, default);
            var receipt = events.Last(
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                ReceiptStatuses.Unknown,
                receipt.Payload.GetProperty("status").GetString());
            Assert.DoesNotContain(
                events,
                item => item.Kind == RuntimeEventKinds.ToolCompleted
                        || item.Kind == RuntimeEventKinds.ToolFailed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurationDeadlineIgnoresThrowingCancellationCallbacks()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new KnownZeroRetryProvider();
            var delay = new ThrowingCancellationDelay();
            await using var runtime = CreateRuntimeCore(
                store,
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids(),
                new DurableAgentRuntimeOptions
                {
                    ModelId = "test-model",
                    MaxConcurrentProviderCalls = 1
                },
                maxProviderAttempts: 2,
                delay);
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 500;

            var outcome = await runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Equal(1, provider.CallCount);
            Assert.True(delay.Started.Task.IsCompleted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurationDeadlineAndStopIgnoreBlockingCancellationCallbacks()
    {
        var directory = TempDirectory();
        var delay = new BlockingCancellationDelay();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var provider = new KnownZeroRetryProvider();
            await using var runtime = CreateRuntimeCore(
                store,
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids(),
                new DurableAgentRuntimeOptions
                {
                    ModelId = "test-model",
                    MaxConcurrentProviderCalls = 1
                },
                maxProviderAttempts: 2,
                delay);
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 200;

            var outcome = await runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            Assert.Equal(1, provider.CallCount);
            await delay.CallbackInvoked.Task.WaitAsync(
                TestWaitTimeout);
            await runtime.StopAsync().AsTask().WaitAsync(
                TestWaitTimeout);
        }
        finally
        {
            delay.Release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeadlineWithBlockingProviderCancellationStillStops()
    {
        var directory = TempDirectory();
        var provider = new BlockingCancellationProvider();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 1_000;

            var running = runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask();
            await provider.Started.Task.WaitAsync(TestWaitTimeout);
            var outcome = await running.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_duration", outcome.Run.TerminalReason);
            await provider.CallbackInvoked.Task.WaitAsync(
                TestWaitTimeout);
            await runtime.StopAsync().AsTask().WaitAsync(
                TestWaitTimeout);
        }
        finally
        {
            provider.Release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopDoesNotWaitForBlockingProviderCancellationCallback()
    {
        var directory = TempDirectory();
        var provider = new BlockingCancellationProvider();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            await using var runtime = CreateRuntime(
                store,
                provider,
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids());
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 10_000;
            var running = runtime.RunAsync(
                    new DurableRunRequest { Run = run })
                .AsTask();
            await provider.Started.Task.WaitAsync(TestWaitTimeout);

            await runtime.StopAsync().AsTask().WaitAsync(
                TestWaitTimeout);
            var outcome = await running.WaitAsync(TestWaitTimeout);

            Assert.Equal(RunStates.Cancelled, outcome.Run.State);
            await provider.CallbackInvoked.Task.WaitAsync(
                TestWaitTimeout);
        }
        finally
        {
            provider.Release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BlockingShutdownCallbacksConsumeOnlyBoundedCapacity()
    {
        var directory = TempDirectory();
        var callbackInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        BoundedCancellationDispatcher.CancellationDispatchReservation?
            dataPlaneReservation = null;
        DurableAgentRuntime? first = null;
        DurableAgentRuntime? second = null;
        BoundedCancellationDispatcher? dispatcher = null;
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            dispatcher = new BoundedCancellationDispatcher(capacity: 1);
            var dataPlaneDispatcher =
                new BoundedCancellationDispatcher(capacity: 1);
            Assert.True(
                dataPlaneDispatcher.TryReserve(out dataPlaneReservation));
            var options = new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 1
            };
            first = CreateRuntimeCoreWithSkills(
                store,
                store,
                new QueueStreamingProvider(FinalResponse("unused")),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids(),
                options,
                maxProviderAttempts: 1,
                new SystemRuntimeDelay(),
                Array.Empty<SkillManifest>(),
                Array.Empty<ToolDescriptor>(),
                cancellationDispatcher: dataPlaneDispatcher,
                shutdownCancellationDispatcher: dispatcher);
            second = CreateRuntimeCoreWithSkills(
                store,
                store,
                new QueueStreamingProvider(FinalResponse("unused")),
                new Host(
                    _ => throw new InvalidOperationException(
                        "No tool expected.")),
                clock,
                new Ids(),
                options,
                maxProviderAttempts: 1,
                new SystemRuntimeDelay(),
                Array.Empty<SkillManifest>(),
                Array.Empty<ToolDescriptor>(),
                cancellationDispatcher: dataPlaneDispatcher,
                shutdownCancellationDispatcher: dispatcher);
            var shutdownSource = Assert.IsType<CancellationTokenSource>(
                typeof(DurableAgentRuntime)
                    .GetField(
                        "_shutdownCancellation",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(first));
            registration = shutdownSource.Token.Register(
                () =>
                {
                    callbackInvoked.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                });

            await first.StopAsync().AsTask().WaitAsync(
                TestWaitTimeout);
            await callbackInvoked.Task.WaitAsync(TestWaitTimeout);
            Assert.Equal(1, dataPlaneDispatcher.ActiveReservations);
            Assert.Equal(1, dispatcher.ActiveReservations);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.StopAsync().AsTask());

            release.TrySetResult();
            registration.Dispose();
            Assert.True(
                await WaitUntilAsync(
                    () => dispatcher.ActiveReservations == 0));

            await second.StopAsync().AsTask().WaitAsync(
                TestWaitTimeout);
            Assert.True(
                await WaitUntilAsync(
                    () => dispatcher.ActiveReservations == 0));
        }
        finally
        {
            release.TrySetResult();
            registration.Dispose();
            dataPlaneReservation?.Dispose();
            if (dispatcher is not null)
            {
                _ = await WaitUntilAsync(
                    () => dispatcher.ActiveReservations == 0);
            }

            if (first is not null)
            {
                await first.StopAsync();
            }

            if (second is not null)
            {
                await second.StopAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurationDeadlineBoundsSlowOperationReconciliation()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                ToolResponse(
                    "call-1",
                    "read_state",
                    """{"entityId":"npc-7"}"""));
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Unknown,
                        result: null,
                        clock.UtcNow)));
            var ids = new Ids();
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                ids,
                Tool("read_state"));
            var run = Run(clock.UtcNow);
            run.Budget.MaxDurationMs = 5_000;
            var first = await runtime.RunAsync(
                new DurableRunRequest { Run = run });
            Assert.Equal(RunStates.Reconciling, first.Run.State);
            var nearDeadline = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(first.Run));
            nearDeadline.Revision = first.Run.Revision + 1;
            nearDeadline.Usage.DurationMs = 4_900;
            await new JournalCoordinator(
                    store,
                    store,
                    clock,
                    ids)
                .AppendDurableAsync(
                    first.Run,
                    RuntimeEventKinds.BudgetUpdated,
                    ProtocolJson.ToElement(nearDeadline),
                    turnId: first.Run.CurrentTurnId,
                    attemptId: "deadline-test");

            var resumed = await runtime.ResumeAsync(
                    run.RunId,
                    reconciler: new SlowReconciler())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(RunStates.Reconciling, resumed.Run.State);
            Assert.Equal("max_duration", resumed.Run.TerminalReason);
            Assert.Single(resumed.Run.PendingOperationIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SemanticToolLoopWarnsDurablyThenStopsBeforeAnotherDispatch()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                Enumerable.Range(0, 6)
                    .Select(index => ToolResponse(
                        "loop-call-" + index,
                        "read_state",
                        """{"entityId":"secret-argument"}"""))
                    .ToArray());
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Failed,
                        result: null,
                        clock.UtcNow)));
            await using var runtime = CreateRuntime(
                store,
                provider,
                host,
                clock,
                new Ids(),
                Tool("read_state"));
            var run = Run(clock.UtcNow);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal("tool_no_progress", outcome.Run.TerminalReason);
            Assert.Equal("tool_no_progress", outcome.ErrorCode);
            Assert.Equal("tool_loop", outcome.ErrorCategory);
            Assert.Equal(5, provider.Requests.Count);
            Assert.Equal(5, host.CallCount);
            Assert.Equal(5, outcome.Run.Usage.Turns);
            Assert.Equal(5, outcome.Run.Usage.Actions);

            var warning = Assert.Single(
                provider.Requests[3].Messages,
                SemanticToolLoopGuard.IsWarningMessage);
            var warningJson = Assert.Single(
                warning.Parts,
                part => part.Json.HasValue).Json!.Value;
            Assert.Equal(
                "tool_no_progress_warning",
                warningJson.GetProperty("reasonCode").GetString());
            Assert.Equal(2, warningJson.GetProperty(
                "repetitionCount").GetInt32());
            Assert.DoesNotContain(
                "secret-argument",
                warningJson.GetRawText(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "blocked",
                warningJson.GetRawText(),
                StringComparison.Ordinal);

            var durableToolCall = Assert.Single(
                outcome.Transcript
                    .SelectMany(message => message.Parts)
                    .Where(part =>
                        part.Type == NormalizedPartTypes.ToolCall)
                    .Take(1));
            Assert.Equal("1", durableToolCall.ToolVersion);
            Assert.Equal(ToolEffects.PureRead, durableToolCall.ToolEffect);
            Assert.False(string.IsNullOrWhiteSpace(
                durableToolCall.ToolDescriptorDigest));

            var guardDiagnostic =
                outcome.Run.Extensions["toolLoopGuard"].GetRawText();
            Assert.DoesNotContain(
                "secret-argument",
                guardDiagnostic,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "blocked",
                guardDiagnostic,
                StringComparison.Ordinal);

            var events = await store.ReadRunAsync(run.RunId, default);
            Assert.Contains(
                events,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage
                        && SemanticToolLoopGuard.IsWarningMessage(
                            NormalizedMessageJournalCodec.Decode(
                                item.Payload)));
            Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.RunFailed
                        && item.Payload.GetProperty("terminalReason")
                            .GetString() == "tool_no_progress");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SemanticToolLoopRebuildStopsAtCrashBoundaryWithoutReplay()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var inner = new FileSessionStore(path);
            var clock = new Clock();
            var ids = new Ids();
            var run = Run(clock.UtcNow);

            await using (var crashStore =
                         new RejectAfterNthCleanTurnCompletedStore(
                             inner,
                             targetCount: 5))
            {
                var provider = new QueueStreamingProvider(
                    Enumerable.Range(0, 6)
                        .Select(index => ToolResponse(
                            "crash-loop-call-" + index,
                            "read_state",
                            """{"entityId":"npc-7"}"""))
                        .ToArray());
                var host = new Host(
                    request => new ValueTask<ActionReceipt>(
                        Receipt(
                            request,
                            ReceiptStatuses.Failed,
                            result: null,
                            clock.UtcNow)));
                await using var runtime = CreateRuntime(
                    crashStore,
                    crashStore,
                    provider,
                    host,
                    clock,
                    ids,
                    Tool("read_state"));

                var interrupted = await runtime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.True(crashStore.Triggered);
                Assert.Equal("runtime_failure", interrupted.ErrorCode);
                Assert.Equal(5, provider.Requests.Count);
                Assert.Equal(5, host.CallCount);
            }

            var resumedProvider = new QueueStreamingProvider(
                FinalResponse("\"must-not-run\""));
            var resumedHost = new Host(
                _ => throw new InvalidOperationException(
                    "No action should be replayed."));
            await using var resumedRuntime = CreateRuntime(
                inner,
                resumedProvider,
                resumedHost,
                clock,
                ids);

            var resumed = await resumedRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, resumed.Run.State);
            Assert.Equal("tool_no_progress", resumed.Run.TerminalReason);
            Assert.Equal("tool_no_progress", resumed.ErrorCode);
            Assert.Empty(resumedProvider.Requests);
            Assert.Equal(0, resumedHost.CallCount);
            Assert.Equal(5, resumed.Run.Usage.Turns);
            Assert.Equal(5, resumed.Run.Usage.Actions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GuardFailOpenStillTerminatesAtOrdinaryTurnBudget()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "runtime.journal");
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var provider = new QueueStreamingProvider(
                Enumerable.Range(0, 4)
                    .Select(index => ToolResponse(
                        "oversize-loop-call-" + index,
                        "read_state",
                        """{"entityId":"npc-7"}"""))
                    .ToArray());
            var host = new Host(
                request => new ValueTask<ActionReceipt>(
                    Receipt(
                        request,
                        ReceiptStatuses.Succeeded,
                        """{"value":"this result exceeds the configured guard digest bound"}""",
                        clock.UtcNow)));
            await using var runtime = CreateRuntimeWithOptions(
                store,
                provider,
                host,
                clock,
                new Ids(),
                new DurableAgentRuntimeOptions
                {
                    ModelId = "test-model",
                    ToolLoopGuard = new SemanticToolLoopGuardOptions
                    {
                        MaxDigestJsonUtf8Bytes = 32
                    }
                },
                Tool("read_state"));
            var run = Run(clock.UtcNow);
            run.Budget.MaxTurns = 3;
            run.Budget.MaxActions = 4;

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = run });

            Assert.Equal(RunStates.BudgetExhausted, outcome.Run.State);
            Assert.Equal("max_turns", outcome.Run.TerminalReason);
            Assert.Equal(3, provider.Requests.Count);
            Assert.Equal(3, host.CallCount);
            Assert.DoesNotContain(
                provider.Requests.SelectMany(item => item.Messages),
                SemanticToolLoopGuard.IsWarningMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableAgentRuntime CreateRuntime(
        FileSessionStore store,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        params ToolDescriptor[] tools)
    {
        return CreateRuntime(
            store,
            store,
            provider,
            host,
            clock,
            ids,
            tools);
    }

    private static DurableAgentRuntime CreateRuntime(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeWithOptions(
            store,
            operations,
            provider,
            host,
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 2
            },
            tools);
    }

    private static DurableAgentRuntime CreateRuntimeWithOptions(
        FileSessionStore store,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions options,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeWithOptions(
            store,
            store,
            provider,
            host,
            clock,
            ids,
            options,
            tools);
    }

    private static DurableAgentRuntime CreateRuntimeWithOptions(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions options,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeCore(
            store,
            operations,
            provider,
            host,
            clock,
            ids,
            options,
            maxProviderAttempts: 1,
            new SystemRuntimeDelay(),
            tools);
    }

    private static DurableAgentRuntime CreateRetryingRuntime(
        FileSessionStore store,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeCore(
            store,
            store,
            provider,
            host,
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 2
            },
            maxProviderAttempts: 2,
            new SystemRuntimeDelay(),
            tools);
    }

    private static DurableAgentRuntime CreateRuntimeWithSkills(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        IReadOnlyList<SkillManifest> skills,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeCoreWithSkills(
            store,
            operations,
            provider,
            host,
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 2
            },
            maxProviderAttempts: 1,
            new SystemRuntimeDelay(),
            skills,
            tools);
    }

    private static DurableAgentRuntime CreateRuntimeCore(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions options,
        int maxProviderAttempts,
        IRuntimeDelay delay,
        params ToolDescriptor[] tools)
    {
        return CreateRuntimeCoreWithSkills(
            store,
            operations,
            provider,
            host,
            clock,
            ids,
            options,
            maxProviderAttempts,
            delay,
            Array.Empty<SkillManifest>(),
            tools);
    }

    private static DurableAgentRuntime CreateRuntimeCoreWithSkills(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IGameHost host,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions options,
        int maxProviderAttempts,
        IRuntimeDelay delay,
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<ToolDescriptor> tools,
        BoundedCancellationDispatcher? cancellationDispatcher = null,
        BoundedCancellationDispatcher? shutdownCancellationDispatcher = null)
    {
        var journal = new JournalCoordinator(store, operations, clock, ids);
        var toolRegistry = new ToolCatalogRegistry();
        toolRegistry.Replace(tools);
        var skillRegistry = new SkillCatalogRegistry();
        skillRegistry.Replace(skills);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = maxProviderAttempts,
                IdleTimeout = TimeSpan.FromSeconds(2),
                TotalTimeout = TimeSpan.FromSeconds(5)
            },
            delay,
            ids);
        return new DurableAgentRuntime(
            runner,
            host,
            journal,
            new RunRecovery(store, operations, journal),
            toolRegistry,
            skillRegistry,
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            options,
            controls: null,
            ownership: null,
            toolSafety: null,
            skillAdmissionPolicy: null,
            toolDisclosurePolicy: null,
            cancellationDispatcher
            ?? BoundedCancellationDispatcher.Shared,
            shutdownCancellationDispatcher
            ?? BoundedCancellationDispatcher.LifecycleShared);
    }

    private static AgentRun Run(DateTimeOffset now)
    {
        return new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            Budget = new AgentBudget
            {
                MaxTurns = 8,
                MaxDurationMs = 30_000,
                MaxTokens = 8_000,
                MaxActions = 8,
                MaxCostUsd = "1"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < TestWaitTimeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        return predicate();
    }

    private sealed class MisreportedInfiniteReadOnlyList<T>
        : IReadOnlyList<T>
    {
        private readonly T _item;

        public MisreportedInfiniteReadOnlyList(T item)
        {
            _item = item;
        }

        public int Count =>
            throw new InvalidOperationException("Count must not be read.");

        public T this[int index] => _item;

        public IEnumerator<T> GetEnumerator()
        {
            while (true)
            {
                yield return _item;
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static IReadOnlyList<ContextCandidate> SelectionFillers()
    {
        return Enumerable.Range(0, 128)
            .Select(
                index => new ContextCandidate(
                    $"selected-{index:D3}",
                    "state",
                    Json($$"""{"value":{{index}}}"""),
                    priority: 100,
                    canDefer: false))
            .ToArray();
    }

    private static IReadOnlyList<JsonElement> ContextPayloads(
        StreamingModelRequest request)
    {
        var result = new List<JsonElement>();
        foreach (var part in request.Messages.SelectMany(message => message.Parts))
        {
            if (!part.Json.HasValue
                || part.Json.Value.ValueKind != JsonValueKind.Object
                || !part.Json.Value.TryGetProperty(
                    "contentType",
                    out var contentType)
                || !string.Equals(
                    contentType.GetString(),
                    "application/vnd.game-agent.context+json",
                    StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(part.Json.Value.Clone());
        }

        return result;
    }

    private static IReadOnlyList<JsonElement> Items(
        JsonElement value,
        string propertyName = "items")
    {
        return value
            .GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
    }

    private static IReadOnlyList<string> Strings(JsonElement value)
    {
        return value
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static NormalizedMessage InitialMessage(
        string messageId,
        string text,
        DateTimeOffset createdAt)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.User,
            CreatedAt = createdAt,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText(text)
            }
        };
    }

    private static ObservationEnvelope Observation(
        string worldId,
        DateTimeOffset now,
        string json)
    {
        return new ObservationEnvelope
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            WorldId = worldId,
            Source = "test",
            Kind = "steer",
            Payload = Json(json),
            ObservedAt = now,
            Priority = 100
        };
    }

    private static ToolDescriptor Tool(string name)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = "Reads structured game state.",
            ParametersSchema = Json(
                """
                {
                  "type":"object",
                  "properties":{"entityId":{"type":"string"}},
                  "required":["entityId"],
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.PureRead,
            ConflictScopes = new List<string> { "entity:{entityId}" }
        };
    }

    private static SkillManifest Skill(string id, string prompt)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = "declared:" + id,
            Description = id + " description",
            PromptFragments = new List<string> { prompt },
            CapabilityRequirements = Json("{}"),
            ActivationPolicy = Json("{}"),
            Trust = "trusted"
        };
    }

    private static Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>
        ToolResponse(string callId, string name, string arguments)
    {
        return request => new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = callId,
                ToolNameDelta = name,
                ArgumentsJsonDelta = arguments
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "tool_calls"
            }
        };
    }

    private static Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>
        FinalResponse(string text)
    {
        return FinalResponseWithUsage(
            text,
            inputTokens: 20,
            outputTokens: 5,
            costUsd: "0.001");
    }

    private static Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>
        FinalResponseWithUsage(
            string text,
            int inputTokens,
            int outputTokens,
            string costUsd)
    {
        return request => new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = text
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CostUsd = costUsd
                }
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            }
        };
    }

    private static ActionReceipt Receipt(
        ActionRequest request,
        string status,
        string? result,
        DateTimeOffset now)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 0,
            Status = status,
            Result = result is null ? null : Json(result),
            ReceivedAt = now,
            CommittedAt = status == ReceiptStatuses.Unknown ? null : now
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class QueueStreamingProvider : IStreamingModelProvider
    {
        private readonly Queue<
            Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>> _steps;

        public QueueStreamingProvider(
            params Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>[] steps)
        {
            _steps = new Queue<
                Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>>(steps);
        }

        public string ProviderId => "test-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted response remains.");
            }

            foreach (var item in _steps.Dequeue()(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class LaneSnapshotProvider : IStreamingModelProvider
    {
        private readonly string _holderRunId;

        public LaneSnapshotProvider(string holderRunId)
        {
            _holderRunId = holderRunId;
        }

        public string ProviderId => "lane-snapshot-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource HolderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHolder { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (string.Equals(
                    request.RunId,
                    _holderRunId,
                    StringComparison.Ordinal))
            {
                HolderStarted.TrySetResult();
                await ReleaseHolder.Task.WaitAsync(cancellationToken);
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"ok\""
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            };
        }
    }

    private sealed class ContinuationSnapshotProvider :
        IStreamingModelProvider
    {
        private readonly string _targetRunId;
        private readonly string _holderRunId;
        private int _targetCalls;

        public ContinuationSnapshotProvider(
            string targetRunId,
            string holderRunId)
        {
            _targetRunId = targetRunId;
            _holderRunId = holderRunId;
        }

        public string ProviderId => "continuation-snapshot-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource HolderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHolder { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (string.Equals(
                    request.RunId,
                    _holderRunId,
                    StringComparison.Ordinal))
            {
                HolderStarted.TrySetResult();
                await ReleaseHolder.Task.WaitAsync(cancellationToken);
                foreach (var item in FinalEvents(request, "\"holder\""))
                {
                    yield return item;
                }

                yield break;
            }

            if (!string.Equals(
                    request.RunId,
                    _targetRunId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unexpected run reached the snapshot provider.");
            }

            if (Interlocked.Increment(ref _targetCalls) == 1)
            {
                foreach (var item in ToolResponse(
                             "continuation-tool-call",
                             "read_state",
                             """{"entityId":"npc-7"}""")(request))
                {
                    yield return item;
                }

                yield break;
            }

            foreach (var item in FinalEvents(request, "\"resumed\""))
            {
                yield return item;
            }
        }

        private static IEnumerable<ModelStreamEvent> FinalEvents(
            StreamingModelRequest request,
            string text)
        {
            return FinalResponseWithUsage(
                text,
                inputTokens: 0,
                outputTokens: 0,
                costUsd: "0")(request);
        }
    }

    private sealed class BudgetExhaustingRetryProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "budget-exhausting-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count != 1)
            {
                throw new InvalidOperationException(
                    "The exhausted turn must not dispatch another request.");
            }

            var tokens = request.MaxOutputTokens
                ?? throw new InvalidOperationException(
                    "The durable runtime must provide an output cap.");
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = tokens,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            };
            await Task.Yield();
            throw new ProviderException(
                "transient",
                "network",
                "Retry after accounted usage.",
                true);
        }
    }

    private sealed class LengthFinishProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "length-finish-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"partial\""
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 10,
                    OutputTokens = 2,
                    CostUsd = "0.003"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "length"
            };
            await Task.Yield();
        }
    }

    private sealed class CancellationBeforeUsageProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "cancellation-before-usage";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Started.TrySetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            yield break;
        }
    }

    private sealed class SelfCancellingBeforeUsageProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "self-cancelling-before-usage";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            await Task.Yield();
            throw new TaskCanceledException(
                "The provider cancelled its own request.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class NeverInvokedProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "never-invoked";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new InvalidOperationException(
                "Recovery must not start another provider request.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class KnownZeroRetryProvider :
        IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "known-zero-retry";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                await Task.Yield();
                throw new ProviderException(
                    "connect_failed",
                    "network",
                    "The request was never accepted.",
                    retryable: true,
                    usageKnownToBeZero: true);
            }

            foreach (var item in FinalResponseWithUsage(
                         "\"ok\"",
                         inputTokens: 1,
                         outputTokens: 1,
                         costUsd: "0")(request))
            {
                yield return item;
            }
        }
    }

    private sealed class ThrowingCancellationDelay : IRuntimeDelay
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException(
                    "Untrusted cancellation callback."));
            Started.TrySetResult();
            await cancellation;
        }
    }

    private sealed class BlockingCancellationDelay : IRuntimeDelay
    {
        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    Release.Task.GetAwaiter().GetResult();
                });
            await cancellation;
        }
    }

    private sealed class BlockingCancellationProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "blocking-cancellation";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    Release.Task.GetAwaiter().GetResult();
                });
            Started.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class SteerStreamingProvider : IStreamingModelProvider
    {
        public string ProviderId => "test-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 1,
                        OutputTokens = 0,
                        CostUsd = "0"
                    }
                };
                FirstAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "steered-final"
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            };
        }
    }

    private sealed class CompletionSteerRaceProvider :
        IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "completion-steer-race-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource FirstUsageObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(request);
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "stale"
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 20,
                        OutputTokens = 5,
                        CostUsd = "0.001"
                    }
                };
                FirstUsageObserved.TrySetResult();
                await ReleaseFirstCompletion.Task.ConfigureAwait(false);
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                };
                yield break;
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "steered-final"
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 1,
                    OutputTokens = 1,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            };
        }
    }

    private sealed class UnaccountedSteerProvider :
        IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "unaccounted-steer-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref _callCount);
            FirstAttemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SlowStreamingProvider : IStreamingModelProvider
    {
        public string ProviderId => "slow-provider";

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
            _ = request;
            _ = cancellationToken;
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            yield break;
        }
    }

    private sealed class AmbiguousFinalCompletionStore :
        IDurableSessionStore
    {
        private readonly FileSessionStore _inner;
        private int _finalBatchCommitted;

        public AmbiguousFinalCompletionStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool FinalBatchCommitted =>
            Volatile.Read(ref _finalBatchCommitted) != 0;

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = await _inner.AppendAtomicBatchAsync(
                    runtimeEvents,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsFinalCompletionBatch(runtimeEvents)
                && Interlocked.CompareExchange(
                    ref _finalBatchCommitted,
                    value: 1,
                    comparand: 0) == 0)
            {
                throw new IOException(
                    "The final completion committed but its acknowledgement was lost.");
            }

            return results;
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
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

        public ValueTask DisposeAsync() => default;

        private static bool IsFinalCompletionBatch(
            IReadOnlyList<RuntimeEvent> runtimeEvents)
        {
            return runtimeEvents.Count == 4
                   && runtimeEvents[0].Kind
                   == RuntimeEventKinds.TranscriptMessage
                   && runtimeEvents[1].Kind
                   == RuntimeEventKinds.ProviderResultCommitted
                   && runtimeEvents[2].Kind
                   == RuntimeEventKinds.AssistantCompleted
                   && runtimeEvents[3].Kind
                   == RuntimeEventKinds.RunCompleted;
        }
    }

    private sealed class BudgetAppendInterceptStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;

        public BudgetAppendInterceptStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource ProviderUsageAppendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseProviderUsageAppend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (runtimeEvent.Kind == RuntimeEventKinds.BudgetUpdated
                && runtimeEvent.EventId.StartsWith(
                    "provider-usage:",
                    StringComparison.Ordinal))
            {
                ProviderUsageAppendStarted.TrySetResult();
                await ReleaseProviderUsageAppend.Task.ConfigureAwait(false);
            }

            return await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
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

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
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
            return _inner.GetOperationAsync(operationId, cancellationToken);
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

    private sealed class FailAfterUncertainUsageStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _uncertainUsageCommitted;

        public FailAfterUncertainUsageStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool RejectedPostCheckpointAppend { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _uncertainUsageCommitted) != 0
                && runtimeEvent.Kind
                    != RuntimeEventKinds.ProviderUsageUncertain)
            {
                RejectedPostCheckpointAppend = true;
                throw new IOException("Simulated crash after checkpoint.");
            }

            var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind
                == RuntimeEventKinds.ProviderUsageUncertain)
            {
                Volatile.Write(ref _uncertainUsageCommitted, 1);
            }

            return result;
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _uncertainUsageCommitted) != 0)
            {
                RejectedPostCheckpointAppend = true;
                throw new IOException("Simulated crash after checkpoint.");
            }

            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
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
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
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

    private abstract class DelegatingSessionStore :
        IDurableSessionStore,
        IOperationLedger
    {
        protected DelegatingSessionStore(FileSessionStore inner)
        {
            Inner = inner;
        }

        protected FileSessionStore Inner { get; }

        public virtual ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return Inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public virtual ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return Inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public virtual ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            return Inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public virtual ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return Inner.ReadRunAsync(runId, cancellationToken);
        }

        public virtual ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return Inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public virtual ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return Inner.FlushAsync(cancellationToken);
        }

        public virtual ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return Inner.GetOperationAsync(operationId, cancellationToken);
        }

        public virtual ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return Inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public virtual ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return Inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public virtual ValueTask DisposeAsync()
        {
            return default;
        }
    }

    private sealed class RecordingBatchStore : DelegatingSessionStore
    {
        public RecordingBatchStore(FileSessionStore inner)
            : base(inner)
        {
        }

        public List<IReadOnlyList<string>> BatchKinds { get; } = new();

        public override async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            BatchKinds.Add(
                runtimeEvents.Select(item => item.Kind).ToArray());
            return await base.AppendAtomicBatchAsync(
                    runtimeEvents,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class RejectAfterRunStartBatchStore :
        DelegatingSessionStore
    {
        private int _runStartCommitted;

        public RejectAfterRunStartBatchStore(FileSessionStore inner)
            : base(inner)
        {
        }

        public bool RunStartCommitted =>
            Volatile.Read(ref _runStartCommitted) != 0;

        public override ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (RunStartCommitted)
            {
                throw new IOException(
                    "Simulated process loss after run initialization.");
            }

            return base.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public override async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            if (RunStartCommitted)
            {
                throw new IOException(
                    "Simulated process loss after run initialization.");
            }

            var result = await base.AppendAtomicBatchAsync(
                    runtimeEvents,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvents.Any(
                    item => item.Kind == RuntimeEventKinds.RunStarted))
            {
                Volatile.Write(ref _runStartCommitted, 1);
                throw new IOException(
                    "The run-start batch committed before process loss.");
            }

            return result;
        }
    }

    private sealed class RejectAfterTurnSnapshotStore :
        DelegatingSessionStore
    {
        private int _turnSnapshotCommitted;

        public RejectAfterTurnSnapshotStore(FileSessionStore inner)
            : base(inner)
        {
        }

        public bool TurnSnapshotCommitted =>
            Volatile.Read(ref _turnSnapshotCommitted) != 0;

        public override async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (TurnSnapshotCommitted)
            {
                throw new IOException(
                    "Simulated process loss after the turn snapshot.");
            }

            var result = await base.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.TurnSnapshot)
            {
                Volatile.Write(ref _turnSnapshotCommitted, 1);
                throw new IOException(
                    "The turn snapshot committed before process loss.");
            }

            return result;
        }

        public override async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            if (TurnSnapshotCommitted)
            {
                throw new IOException(
                    "Simulated process loss after the turn snapshot.");
            }

            var result = await base.AppendAtomicBatchAsync(
                    runtimeEvents,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvents.Any(
                    item => item.Kind == RuntimeEventKinds.TurnSnapshot))
            {
                Volatile.Write(ref _turnSnapshotCommitted, 1);
                throw new IOException(
                    "The turn-preparation batch committed before process "
                    + "loss.");
            }

            return result;
        }
    }

    private sealed class RejectAfterCleanTurnCompletedStore :
        DelegatingSessionStore
    {
        private int _cleanTurnCompleted;

        public RejectAfterCleanTurnCompletedStore(FileSessionStore inner)
            : base(inner)
        {
        }

        public bool CleanTurnCompleted =>
            Volatile.Read(ref _cleanTurnCompleted) != 0;

        public override async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (CleanTurnCompleted)
            {
                throw new IOException(
                    "Simulated process loss at a clean turn boundary.");
            }

            var result = await base.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.TurnCompleted
                && runtimeEvent.ReasonCode
                != RunRecovery.ReplaySafeTurnAbandonedReason)
            {
                Volatile.Write(ref _cleanTurnCompleted, 1);
                throw new IOException(
                    "The clean turn boundary committed before process loss.");
            }

            return result;
        }

        public override ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            if (CleanTurnCompleted)
            {
                throw new IOException(
                    "Simulated process loss at a clean turn boundary.");
            }

            return base.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }
    }

    private sealed class RejectAfterNthCleanTurnCompletedStore :
        DelegatingSessionStore
    {
        private readonly int _targetCount;
        private int _completedCount;
        private int _triggered;

        public RejectAfterNthCleanTurnCompletedStore(
            FileSessionStore inner,
            int targetCount)
            : base(inner)
        {
            _targetCount = targetCount;
        }

        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (Triggered)
            {
                throw new IOException(
                    "Simulated process loss at the selected turn boundary.");
            }

            var result = await base.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.TurnCompleted
                && runtimeEvent.ReasonCode
                != RunRecovery.ReplaySafeTurnAbandonedReason
                && Interlocked.Increment(ref _completedCount)
                == _targetCount)
            {
                Volatile.Write(ref _triggered, 1);
                throw new IOException(
                    "The selected turn boundary committed before process "
                    + "loss.");
            }

            return result;
        }

        public override ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            if (Triggered)
            {
                throw new IOException(
                    "Simulated process loss at the selected turn boundary.");
            }

            return base.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }
    }

    private sealed class ThrowAfterPreProviderAbandonStore :
        DelegatingSessionStore
    {
        private int _triggered;

        public ThrowAfterPreProviderAbandonStore(FileSessionStore inner)
            : base(inner)
        {
        }

        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            var result = await base.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.TurnCompleted
                && runtimeEvent.ReasonCode
                == RunRecovery.ReplaySafeTurnAbandonedReason
                && Interlocked.CompareExchange(
                    ref _triggered,
                    value: 1,
                    comparand: 0) == 0)
            {
                throw new IOException(
                    "The abandon checkpoint committed before the crash.");
            }

            return result;
        }
    }

    private sealed class InvalidRunStartBatchStore :
        IDurableSessionStore,
        IOperationLedger
    {
        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            IReadOnlyList<JournalAppendResult> result = runtimeEvents
                .Select(
                    (_, index) => new JournalAppendResult(
                        index,
                        index == runtimeEvents.Count - 1
                            ? index + 2
                            : index + 1,
                        wasDuplicate: false))
                .ToArray();
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(result);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return default;
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }

    private sealed class RecordingEventPublisher :
        INonBlockingRuntimeEventPublisher
    {
        public List<RuntimeEvent> Events { get; } = new();

        public void Publish(RuntimeEvent runtimeEvent)
        {
            Events.Add(runtimeEvent);
        }
    }

    private sealed class Host : IGameHost
    {
        private readonly Func<ActionRequest, ValueTask<ActionReceipt>> _handler;

        public Host(Func<ActionRequest, ValueTask<ActionReceipt>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public ActionRequest? LastRequest { get; private set; }

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return await _handler(request);
        }
    }

    private sealed class SlowHost : IGameHost
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            _started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class Reconciler : IGameOperationReconciler
    {
        private readonly DateTimeOffset _now;

        public Reconciler(DateTimeOffset now)
        {
            _now = now;
        }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = Receipt(
                request,
                ReceiptStatuses.Succeeded,
                """{"reconciled":true}""",
                _now);
            receipt.Revision = 1;
            return new ValueTask<ActionReceipt>(receipt);
        }
    }

    private sealed class SlowReconciler : IGameOperationReconciler
    {
        public async ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class MutableClock : IRuntimeClock
    {
        public MutableClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }

    private sealed class IncrementingClock : IRuntimeClock
    {
        private readonly object _sync = new();
        private readonly TimeSpan _step;
        private DateTimeOffset _now;

        public IncrementingClock(
            DateTimeOffset now,
            TimeSpan step)
        {
            _now = now;
            _step = step;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    var result = _now;
                    _now = _now.Add(_step);
                    return result;
                }
            }
        }
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }

    private sealed class DeadlineConsumingIds : IRuntimeIdGenerator
    {
        private readonly MutableClock _clock;
        private int _value;

        public DeadlineConsumingIds(MutableClock clock)
        {
            _clock = clock;
        }

        public string NewId(string category)
        {
            if (string.Equals(
                    category,
                    "operation",
                    StringComparison.Ordinal))
            {
                Thread.Sleep(50);
                _clock.Advance(TimeSpan.FromHours(-1));
            }

            return category + "-" + Interlocked.Increment(ref _value);
        }
    }
}
