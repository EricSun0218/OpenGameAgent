using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.World;

namespace GameAgent.Tests;

public sealed class WorldAgentRuntimeBridgeTests
{
    [Fact]
    public void RunInputStopsEnumeratingAtItsContextLimit()
    {
        var yielded = 0;

        var exception = Assert.Throws<ArgumentException>(
            () => new WorldAgentRunInput(
                DateTimeOffset.UtcNow,
                new AgentBudget(),
                context: Infinite()));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal(512, yielded);

        IEnumerable<ContextCandidate> Infinite()
        {
            while (true)
            {
                yielded++;
                yield return new ContextCandidate(
                    "context-" + yielded,
                    "test",
                    Json("{}"));
            }
        }
    }

    [Fact]
    public async Task MaximumUserContextReservesMandatoryWorldJobSlot()
    {
        var context = Enumerable.Range(0, 511)
            .Select(
                index => new ContextCandidate(
                    "context-" + index,
                    "test",
                    Json("{}")))
            .ToArray();
        var runtime = new FakeRuntime
        {
            RunOutcome = Outcome(
                RunStates.Completed,
                Json("""{"optionId":"wait"}"""))
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory(context));
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);

        var result = await bridge.ExecuteAsync(job, job.Coordinate);

        Assert.Equal(WorldAgentJobStatus.Completed, result.Status);
        Assert.Equal(512, runtime.LastRequest!.Context.Count);
        Assert.Single(
            runtime.LastRequest.Context,
            candidate => candidate.Category == "world_agent_job");
        _ = Assert.Throws<ArgumentException>(
            () => new WorldAgentRunInput(
                DateTimeOffset.UnixEpoch,
                new AgentBudget(),
                context: context.Append(
                    new ContextCandidate(
                        "context-overflow",
                        "test",
                        Json("{}")))));
    }

    [Fact]
    public async Task UserContextBytesReserveMandatoryWorldJobEnvelope()
    {
        ContextCandidate[]? context = null;
        var lower = 60_000;
        var upper = 65_536;
        while (lower <= upper)
        {
            var candidateSize = lower + ((upper - lower) / 2);
            var candidateContext = Context(candidateSize);
            try
            {
                _ = new WorldAgentRunInput(
                    DateTimeOffset.UnixEpoch,
                    new AgentBudget(),
                    context: candidateContext);
                context = candidateContext;
                lower = candidateSize + 1;
            }
            catch (RuntimeContentLimitException)
            {
                upper = candidateSize - 1;
            }
        }

        Assert.NotNull(context);
        var runtime = new FakeRuntime();
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory(context!));
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            async () => await bridge.ExecuteAsync(job, job.Coordinate));

        Assert.Equal("durable_run_input_bytes_exceeded", error.LimitCode);
        Assert.Equal(0, runtime.RunCalls);

        static ContextCandidate[] Context(int stringBytes)
        {
            return Enumerable.Range(0, 4)
                .Select(
                    index => new ContextCandidate(
                        "near-byte-limit-" + index,
                        "test",
                        Json(
                            "{\"data\":\""
                            + new string('x', stringBytes)
                            + "\"}")))
                .ToArray();
        }
    }

    [Fact]
    public async Task ExecuteBindsStrictContractAndImmutableCoordinate()
    {
        var runtime = new FakeRuntime
        {
            RunOutcome = Outcome(
                RunStates.Completed,
                Json("{\"optionId\":\"wait\"}"))
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);

        var result = await bridge.ExecuteAsync(job, job.Coordinate);

        Assert.Equal(WorldAgentJobStatus.Completed, result.Status);
        Assert.True(result.IsAuthoritativeProposal);
        Assert.NotNull(runtime.LastRequest);
        Assert.NotNull(runtime.LastRequest!.FinalOutputContract);
        Assert.Equal(job.RunId, runtime.LastRequest.Run.RunId);
        Assert.Equal(job.JobId, runtime.LastRequest.Run.DecisionKey);
        Assert.True(
            runtime.LastRequest.Run.Extensions.ContainsKey(
                WorldAgentRuntimeBridge.JobExtensionName));
        Assert.True(
            GameContextEnvelope.TryRead(
                runtime.LastRequest.Run,
                out var coordinate));
        Assert.Equal(job.Coordinate.TimelineId, coordinate!.TimelineId);
        var requiredJobContext = Assert.Single(
            runtime.LastRequest.Context,
            candidate => candidate.Category == "world_agent_job");
        Assert.True(requiredJobContext.Required);
        Assert.False(requiredJobContext.CanDefer);
    }

    [Fact]
    public async Task InvalidOutputUsesOnlyDeclaredSchemaValidFallback()
    {
        var runtime = new FakeRuntime
        {
            RunOutcome = Outcome(
                RunStates.Completed,
                Json("{\"optionId\":\"invented\"}"))
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var job = SelectionJob(
            WorldAgentFailurePolicy.UseFallback,
            Json("{\"optionId\":\"wait\"}"));

        var result = await bridge.ExecuteAsync(job, job.Coordinate);

        Assert.Equal(WorldAgentJobStatus.Completed, result.Status);
        Assert.True(result.UsedFallback);
        Assert.Equal(
            "wait",
            result.Output!.Value.GetProperty("optionId").GetString());
    }

    [Fact]
    public void SemanticDigestBindsTheDeclaredFallback()
    {
        var first = SelectionJob(
            WorldAgentFailurePolicy.UseFallback,
            Json("{\"optionId\":\"wait\"}"));
        var second = SelectionJob(
            WorldAgentFailurePolicy.UseFallback,
            Json("{\"optionId\":\"leave\"}"));

        Assert.NotEqual(first.SemanticDigest, second.SemanticDigest);
        Assert.NotEqual(
            first.ToEnvelope()
                .GetProperty("fallbackOutputDigest")
                .GetString(),
            second.ToEnvelope()
                .GetProperty("fallbackOutputDigest")
                .GetString());
    }

    [Fact]
    public async Task ReconciliationNeverFallsBackOrRedispatches()
    {
        var runtime = new FakeRuntime
        {
            RunOutcome = Outcome(
                RunStates.Reconciling,
                finalOutput: null,
                pendingOperation: "operation-1")
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var job = SelectionJob(
            WorldAgentFailurePolicy.UseFallback,
            Json("{\"optionId\":\"wait\"}"));

        var result = await bridge.ExecuteAsync(job, job.Coordinate);

        Assert.Equal(
            WorldAgentJobStatus.ReconciliationRequired,
            result.Status);
        Assert.False(result.UsedFallback);
        Assert.Null(result.Output);
        Assert.Equal(1, runtime.RunCalls);
    }

    [Fact]
    public async Task StaleCoordinateFailsBeforeRuntimeAdmission()
    {
        var runtime = new FakeRuntime();
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);
        var stale = new GameContextCoordinate(
            job.Coordinate.WorldId,
            job.Coordinate.TimelineId,
            job.Coordinate.SaveRevision + 1,
            job.Coordinate.Observer,
            stateVersion: "state-new");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await bridge.ExecuteAsync(job, stale));
        Assert.Equal(0, runtime.RunCalls);
    }

    [Fact]
    public async Task ResumeUsesIdentityAndSemanticGuards()
    {
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);
        var runtime = new FakeRuntime
        {
            ResumeOutcome = OutcomeForJob(
                job,
                RunStates.Completed,
                Json("{\"optionId\":\"leave\"}"))
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());

        var result = await bridge.ResumeAsync(job, job.Coordinate);

        Assert.Equal(WorldAgentJobStatus.Completed, result.Status);
        Assert.NotNull(runtime.LastGuard);
        Assert.Equal(job.AgentId, runtime.LastGuard!.ExpectedAgentId);
        Assert.Equal(job.JobId, runtime.LastGuard.ExpectedDecisionKey);
        Assert.Equal(job.BatchId, runtime.LastGuard.ExpectedBatchId);
        Assert.Equal(
            WorldAgentRuntimeBridge.JobExtensionName,
            runtime.LastGuard.SemanticExtensionName);
        Assert.Equal(
            job.SemanticDigest,
            runtime.LastGuard.ExpectedSemanticExtensionSha256);
        Assert.NotNull(runtime.LastContinuation!.FinalOutputContract);
    }

    [Fact]
    public async Task ExecuteRejectsChangedReturnedRunIdentity()
    {
        var runtime = new ChangedIdentityRuntime();
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var job = SelectionJob(WorldAgentFailurePolicy.Fault);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await bridge.ExecuteAsync(job, job.Coordinate));
    }

    [Fact]
    public async Task NarrationIsAlwaysNonAuthoritativePresentation()
    {
        var runtime = new FakeRuntime
        {
            RunOutcome = Outcome(
                RunStates.Completed,
                Json("{\"text\":\"The gate closes.\"}"))
        };
        var bridge = new WorldAgentRuntimeBridge(
            runtime,
            new FixedInputFactory());
        var coordinate = Coordinate();
        var job = new WorldAgentJob(
            "narration-1",
            "run-narration-1",
            "narrator",
            "occurrence-1",
            WorldAgentJobKind.Narration,
            coordinate,
            Json("{\"outcome\":\"closed\"}"),
            "narration",
            "1",
            WorldAgentOutputSchemas.Narration(),
            WorldAgentFailurePolicy.Skip,
            new string('c', 64));

        var result = await bridge.ExecuteAsync(job, coordinate);

        Assert.Equal(WorldAgentJobStatus.Completed, result.Status);
        Assert.False(result.IsAuthoritativeProposal);
    }

    private static WorldAgentJob SelectionJob(
        WorldAgentFailurePolicy failurePolicy,
        JsonElement? fallback = null)
    {
        var coordinate = Coordinate();
        return new WorldAgentJob(
            "decision-1",
            "run-decision-1",
            "actor-agent",
            "occurrence-1",
            WorldAgentJobKind.Selection,
            coordinate,
            Json("{\"question\":\"Choose a route.\"}"),
            "route-selection",
            "1",
            WorldAgentOutputSchemas.Selection(
                new[] { "leave", "wait" }),
            failurePolicy,
            new string('a', 64),
            batchId: "batch-1",
            fallbackOutput: fallback);
    }

    private static GameContextCoordinate Coordinate()
    {
        return new GameContextCoordinate(
            "world-1",
            "timeline-1",
            7,
            new GameEntityIdentity("actor-1", 2),
            stateVersion: "state-7",
            causality: new GameCausalityStamp(
                "occurrence-1",
                "state-7"));
    }

    private static DurableRunOutcome Outcome(
        string state,
        JsonElement? finalOutput,
        string? pendingOperation = null)
    {
        var run = new AgentRun
        {
            RunId = "run-decision-1",
            AgentId = "actor-agent",
            WorldId = "world-1",
            State = state
        };
        if (pendingOperation is not null)
        {
            run.PendingOperationIds.Add(pendingOperation);
        }

        return new DurableRunOutcome
        {
            Run = run,
            FinalOutput = finalOutput
        };
    }

    private static DurableRunOutcome OutcomeForJob(
        WorldAgentJob job,
        string state,
        JsonElement? finalOutput)
    {
        var run = new AgentRun
        {
            RunId = job.RunId,
            AgentId = job.AgentId,
            WorldId = job.Coordinate.WorldId,
            Trigger = new AgentTrigger
            {
                Type = "manual",
                SourceId = job.OccurrenceId
            },
            DecisionKey = job.JobId,
            BatchId = job.BatchId,
            State = state,
            Budget = new AgentBudget(),
            Usage = new AgentUsage(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        GameContextEnvelope.Attach(run, job.Coordinate);
        return new DurableRunOutcome
        {
            Run = run,
            FinalOutput = finalOutput
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixedInputFactory : IWorldAgentRunInputFactory
    {
        private readonly IReadOnlyList<ContextCandidate> _context;

        public FixedInputFactory(
            IReadOnlyList<ContextCandidate>? context = null)
        {
            _context = context ?? Array.Empty<ContextCandidate>();
        }

        public ValueTask<WorldAgentRunInput> CreateAsync(
            WorldAgentJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldAgentRunInput>(
                new WorldAgentRunInput(
                    DateTimeOffset.UnixEpoch,
                    new AgentBudget
                    {
                        MaxTurns = 4,
                        MaxDurationMs = 10_000,
                        MaxTokens = 2_000,
                        MaxCostUsd = "1",
                        MaxActions = 4
                    },
                    context: _context));
        }
    }

    private sealed class FakeRuntime : IGuardedDurableAgentRuntime
    {
        public DurableRunOutcome? RunOutcome { get; set; }

        public DurableRunOutcome? ResumeOutcome { get; set; }

        public DurableRunRequest? LastRequest { get; private set; }

        public DurableRunContinuation? LastContinuation { get; private set; }

        public DurableRunResumeGuard? LastGuard { get; private set; }

        public int RunCalls { get; private set; }

        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCalls++;
            LastRequest = request;
            var outcome = RunOutcome
                          ?? throw new InvalidOperationException(
                              "No run outcome configured.");
            var returnedRun = outcome.Run;
            request.Run.State = returnedRun.State;
            request.Run.PendingOperationIds.Clear();
            foreach (var operationId in returnedRun.PendingOperationIds)
            {
                request.Run.PendingOperationIds.Add(operationId);
            }

            outcome.Run = request.Run;
            return new ValueTask<DurableRunOutcome>(outcome);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastContinuation = continuation;
            LastGuard = guard;
            var outcome = ResumeOutcome
                          ?? throw new InvalidOperationException(
                              "No resume outcome configured.");
            outcome.Run.RunId = runId;
            return new ValueTask<DurableRunOutcome>(outcome);
        }
    }

    private sealed class ChangedIdentityRuntime : IDurableAgentRuntime
    {
        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Run.AgentId = "different-agent";
            request.Run.State = RunStates.Completed;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = request.Run,
                    FinalOutput = Json("""{"optionId":"wait"}""")
                });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
