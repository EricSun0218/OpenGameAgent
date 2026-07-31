using System.Collections;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeTraceReplayTests
{
    [Fact]
    public void EmptyReplayFailsClosed()
    {
        var replay = new RecordedRuntimeReplayHarness().Replay(
            Array.Empty<RuntimeEvent>());

        Assert.False(replay.Passed);
        Assert.Contains(
            "trajectory_run_start_missing",
            replay.FailureCodes);
        Assert.Contains(
            "trajectory_terminal_missing",
            replay.FailureCodes);
    }

    [Fact]
    public void CostArithmeticIsExactBeyondDecimalRange()
    {
        const string left = "99999999999999999999999999999.95";
        const string right = "0.15";

        Assert.True(RuntimeTraceNumbers.TryAddCosts(
            left,
            right,
            out var sum));
        Assert.Equal("100000000000000000000000000000.1", sum);
        Assert.True(RuntimeTraceNumbers.TrySubtractCosts(
            sum,
            right,
            out var difference));
        Assert.Equal(left, difference);
        Assert.True(RuntimeTraceNumbers.TryCompareCosts(
            sum,
            left,
            out var comparison));
        Assert.True(comparison > 0);
        Assert.False(RuntimeTraceNumbers.IsCanonicalCost("01", out _));
    }

    [Fact]
    public void AnalyzerBoundsAndEnumeratesAnInfiniteSourceOnce()
    {
        var source = new CountingInfiniteEvents();
        var analyzer = new RuntimeTraceAnalyzer(
            new RuntimeTraceAnalysisOptions
            {
                MaxEvents = 3
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => analyzer.Analyze(source));

        Assert.Equal(
            "trace_analysis_event_count_exceeded",
            error.LimitCode);
        Assert.Equal(1, source.EnumeratorCount);
        Assert.Equal(4, source.MoveNextCount);
    }

    [Fact]
    public void AnalyzerRejectsAnOversizedEventBeforeSerialization()
    {
        var runtimeEvent = Event(
            "large",
            0,
            RuntimeEventKinds.RunStarted,
            ProtocolJson.ParseElement(
                $$"""{"value":"{{new string('x', 2_000)}}"}"""));
        var analyzer = new RuntimeTraceAnalyzer(
            new RuntimeTraceAnalysisOptions
            {
                MaxUtf8Bytes = 4_096,
                MaxEventUtf8Bytes = 1_024
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => analyzer.Analyze(new[] { runtimeEvent }));

        Assert.Equal(
            "trace_analysis_event_value_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void ReplayReportsStableCorrelationFailures()
    {
        var receipt = new ActionReceipt
        {
            OperationId = "operation",
            Revision = 1,
            Status = ReceiptStatuses.Succeeded,
            Retryable = false,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
        var orphanReceipt = Event(
            "receipt",
            1,
            RuntimeEventKinds.ActionReceived,
            ProtocolJson.ToElement(receipt));
        orphanReceipt.TurnId = "turn";
        var providerSettlement = Event(
            "provider-result",
            2,
            RuntimeEventKinds.ProviderResultCommitted);
        providerSettlement.AttemptId = "attempt";
        providerSettlement.StreamAttemptId = "stream";
        var events = new[]
        {
            Event("start", 0, RuntimeEventKinds.RunStarted),
            orphanReceipt,
            providerSettlement,
            Event("done", 3, RuntimeEventKinds.RunCompleted)
        };

        var replay = new RecordedRuntimeReplayHarness().Replay(events);

        Assert.False(replay.Passed);
        Assert.Contains(
            "trajectory_action_orphan_receipt",
            replay.FailureCodes);
        Assert.Contains(
            "trajectory_action_request_missing",
            replay.FailureCodes);
        Assert.Contains(
            "trajectory_provider_settlement_without_dispatch",
            replay.FailureCodes);
        Assert.Equal(
            replay.FailureCodes.OrderBy(
                item => item,
                StringComparer.Ordinal),
            replay.FailureCodes);
    }

    [Fact]
    public void RecordedReplayIsDeterministicAndSideEffectFree()
    {
        var events = CorrelatedEvents();
        var harness = new RecordedRuntimeReplayHarness();
        var analysis = new RuntimeTraceAnalyzer().Analyze(events);

        var first = harness.Replay(analysis);
        var second = harness.Replay(analysis);

        Assert.True(first.Passed, string.Join(", ", first.FailureCodes));
        Assert.Equal(first.TrajectoryDigest, second.TrajectoryDigest);
        Assert.Equal(first.ReplayDigest, second.ReplayDigest);
        Assert.Equal(1, first.ProviderAttemptsReplayed);
        Assert.Equal(1, first.HostActionsReplayed);
        Assert.Equal(events.Count, first.ClockSamplesReplayed);
        Assert.Equal(events.Count, first.IdentitiesReplayed);
        Assert.Equal(
            ReceiptStatuses.Succeeded,
            Assert.Single(first.HostRecords).ReceiptStatus);
        Assert.Equal(
            RuntimeEventKinds.ProviderResultCommitted,
            Assert.Single(first.ProviderRecords).TerminalKind);
        Assert.Equal(10, analysis.Trajectory.Usage.InputTokens);
        Assert.Equal(
            "0.02",
            Assert.Single(analysis.Trajectory.ProviderAttempts).CostUsd);
    }

    [Fact]
    public void BudgetOverrunRequiresTheBudgetTerminal()
    {
        var running = Run(
            revision: 1,
            state: RunStates.Running,
            actions: 2,
            maximumActions: 1);
        var completed = Run(
            revision: 2,
            state: RunStates.Completed,
            actions: 2,
            maximumActions: 1);
        var analysis = new RuntimeTraceAnalyzer().Analyze(
            new[]
            {
                Event(
                    "start",
                    0,
                    RuntimeEventKinds.RunStarted,
                    ProtocolJson.ToElement(running)),
                Event(
                    "done",
                    1,
                    RuntimeEventKinds.RunCompleted,
                    ProtocolJson.ToElement(completed))
            });

        Assert.False(analysis.Trajectory.BudgetCompliant);
        Assert.Contains(
            "trajectory_budget_exceeded_without_terminal",
            analysis.Trajectory.AssertionFailureCodes);
        var evaluation = new RuntimeScenarioEvaluator().Evaluate(
            analysis,
            new RuntimeScenarioExpectation
            {
                RequireBudgetCompliance = true
            });
        Assert.Contains(
            "scenario_budget_noncompliant",
            evaluation.FailureCodes);
    }

    [Fact]
    public void ActionReceiptRevisionsAdvanceWithoutDuplicateFailure()
    {
        var request = new ActionRequest
        {
            OperationId = "operation",
            RunId = "run",
            TurnId = "turn",
            ToolCallId = "tool-call",
            AgentId = "agent",
            WorldId = "world",
            ActionName = "move",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
        var pending = new ActionReceipt
        {
            OperationId = "operation",
            Revision = 1,
            Status = ReceiptStatuses.Unknown,
            Retryable = true,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
        var committed = new ActionReceipt
        {
            OperationId = "operation",
            Revision = 2,
            Status = ReceiptStatuses.Succeeded,
            Retryable = false,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
        var requestEvent = Event(
            "request",
            1,
            RuntimeEventKinds.ActionRequested,
            ProtocolJson.ToElement(request));
        requestEvent.TurnId = "turn";
        var pendingEvent = Event(
            "pending",
            2,
            RuntimeEventKinds.ActionReceived,
            ProtocolJson.ToElement(pending));
        pendingEvent.TurnId = "turn";
        var committedEvent = Event(
            "committed",
            3,
            RuntimeEventKinds.ActionReceived,
            ProtocolJson.ToElement(committed));
        committedEvent.TurnId = "turn";

        var analysis = new RuntimeTraceAnalyzer().Analyze(
            new[]
            {
                Event("start", 0, RuntimeEventKinds.RunStarted),
                requestEvent,
                pendingEvent,
                committedEvent,
                Event("done", 4, RuntimeEventKinds.RunCompleted)
            });
        var action = Assert.Single(analysis.Trajectory.Actions);

        Assert.Equal(2, action.ReceiptCount);
        Assert.Equal(2, action.ReceiptRevision);
        Assert.Equal(ReceiptStatuses.Succeeded, action.ReceiptStatus);
        Assert.DoesNotContain(
            "trajectory_action_receipt_revision_invalid",
            analysis.Trajectory.AssertionFailureCodes);
    }

    [Fact]
    public void JsonLinesBatchHasDeterministicResultsAndAggregate()
    {
        var firstEvents = new[]
        {
            Event("a-start", 0, RuntimeEventKinds.RunStarted),
            Event("a-done", 1, RuntimeEventKinds.RunCompleted)
        };
        var secondEvents = new[]
        {
            Event("b-start", 0, RuntimeEventKinds.RunStarted),
            Event("b-done", 1, RuntimeEventKinds.RunCompleted)
        };
        var input = ScenarioLine("one", firstEvents)
            + "\n"
            + ScenarioLine("two", secondEvents)
            + "\n";
        var runner = new RuntimeScenarioBatchRunner();

        var first = runner.RunJsonLines(input);
        var second = runner.RunJsonLines(input);

        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.JsonLines, second.JsonLines);
        Assert.Equal(2, first.Aggregate.ScenarioCount);
        Assert.Equal(2, first.Aggregate.PassedScenarios);
        Assert.Equal(0, first.Aggregate.FailedScenarios);
        Assert.Equal(0, first.Aggregate.ReplayPassedScenarios);
        Assert.Equal(2, first.Aggregate.ReplayFailedScenarios);
        Assert.Equal(4, first.Aggregate.EventCount);
        Assert.Equal(new[] { "one", "two" }, first.Results.Select(
            item => item.ScenarioId));
        Assert.Equal(3, first.JsonLines.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void BatchCanGateVerdictsOnReplayValidity()
    {
        var result = new RuntimeScenarioBatchRunner().Run(
            new[]
            {
                new RuntimeScenarioDefinition(
                    "replay-gated",
                    new[]
                    {
                        Event(
                            "start",
                            0,
                            RuntimeEventKinds.RunStarted),
                        Event(
                            "done",
                            1,
                            RuntimeEventKinds.RunCompleted)
                    },
                    new RuntimeScenarioExpectation
                    {
                        RequireValidReplay = true
                    })
            });
        var scenario = Assert.Single(result.Results);

        Assert.False(scenario.Passed);
        Assert.False(scenario.Replay.Passed);
        Assert.Contains(
            "trajectory_run_checkpoint_invalid",
            scenario.FailureCodes);
        Assert.Contains("\"replayPassed\":false", result.JsonLines);
    }

    [Fact]
    public void JsonLinesInputLimitHasAStableCode()
    {
        var runner = new RuntimeScenarioBatchRunner(
            new RuntimeScenarioJsonLinesOptions
            {
                MaxInputUtf8Bytes = 1_024,
                MaxLineUtf8Bytes = 1_024
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => runner.RunJsonLines(new string(' ', 1_025)));

        Assert.Equal(
            "scenario_jsonl_input_bytes_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void JsonLinesOutputLimitHasAStableCode()
    {
        var runner = new RuntimeScenarioBatchRunner(
            new RuntimeScenarioJsonLinesOptions
            {
                MaxOutputUtf8Bytes = 1_024
            });
        var scenario = new RuntimeScenarioDefinition(
            new string('s', 900),
            new[]
            {
                Event("start", 0, RuntimeEventKinds.RunStarted),
                Event("done", 1, RuntimeEventKinds.RunCompleted)
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => runner.Run(new[] { scenario }));

        Assert.Equal(
            "scenario_jsonl_output_bytes_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void BatchAggregateEventLimitHasAStableCode()
    {
        var runner = new RuntimeScenarioBatchRunner(
            new RuntimeScenarioJsonLinesOptions
            {
                MaxAggregateEvents = 3
            });
        var events = new[]
        {
            Event("start", 0, RuntimeEventKinds.RunStarted),
            Event("done", 1, RuntimeEventKinds.RunCompleted)
        };

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => runner.Run(
                new[]
                {
                    new RuntimeScenarioDefinition("one", events),
                    new RuntimeScenarioDefinition("two", events)
                }));

        Assert.Equal(
            "scenario_batch_event_count_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void BatchAggregateTraceByteLimitHasAStableCode()
    {
        var events = Enumerable.Range(0, 10)
            .Select(index => Event(
                "event-" + index,
                index,
                index == 0
                    ? RuntimeEventKinds.RunStarted
                    : RuntimeEventKinds.TurnStarted))
            .ToArray();
        var traceBytes = new RuntimeTraceAnalyzer()
            .Analyze(events)
            .MaterializedUtf8Bytes;
        var runner = new RuntimeScenarioBatchRunner(
            new RuntimeScenarioJsonLinesOptions
            {
                MaxAggregateTraceUtf8Bytes = checked(
                    (int)(traceBytes * 2L - 1L))
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => runner.Run(
                new[]
                {
                    new RuntimeScenarioDefinition("one", events),
                    new RuntimeScenarioDefinition("two", events)
                }));

        Assert.Equal(
            "scenario_batch_trace_bytes_exceeded",
            error.LimitCode);
    }

    private static IReadOnlyList<RuntimeEvent> CorrelatedEvents()
    {
        const string turnId = "turn";
        const string attemptId = "attempt";
        const string streamAttemptId = "stream";
        const string toolCallId = "tool-call";
        const string operationId = "operation";
        var arguments = ProtocolJson.ParseElement("""{"target":"gate"}""");
        var call = new ModelToolCall
        {
            ToolCallId = toolCallId,
            Name = "open_gate",
            Arguments = arguments
        };
        var assistant = NormalizedTranscript.AssistantToolCalls(
            "assistant-message",
            new[] { call },
            DateTimeOffset.UnixEpoch);
        var invocation = new ToolInvocation
        {
            ToolCallId = toolCallId,
            RunId = "run",
            TurnId = turnId,
            AttemptId = attemptId,
            ToolName = "open_gate",
            ToolVersion = "1",
            Arguments = arguments,
            Effect = ToolEffects.WorldCommand,
            Sequence = 0,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        var request = new ActionRequest
        {
            OperationId = operationId,
            RunId = "run",
            TurnId = turnId,
            ToolCallId = toolCallId,
            AgentId = "agent",
            WorldId = "world",
            ActionName = "open_gate",
            ActionVersion = "1",
            Arguments = arguments,
            RequestedAt = DateTimeOffset.UnixEpoch
        };
        var receipt = new ActionReceipt
        {
            OperationId = operationId,
            Revision = 1,
            Status = ReceiptStatuses.Succeeded,
            Result = ProtocolJson.ParseElement("""{"opened":true}"""),
            Retryable = false,
            CommittedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
        var toolResult = NormalizedTranscript.ToolResult(
            "tool-message",
            toolCallId,
            "open_gate",
            receipt,
            DateTimeOffset.UnixEpoch);
        var startRun = Run(
            revision: 1,
            state: RunStates.Running,
            actions: 0,
            maximumActions: 8);
        var turnRun = Run(
            revision: 2,
            state: RunStates.Running,
            actions: 0,
            maximumActions: 8);
        turnRun.CurrentTurnId = turnId;
        turnRun.Usage.Turns = 1;
        var turnStarted = Event(
            "turn-started",
            1,
            RuntimeEventKinds.TurnStarted,
            ProtocolJson.ToElement(turnRun));
        turnStarted.TurnId = turnId;
        turnStarted.AttemptId = attemptId;
        var dispatchRun = Run(
            revision: 3,
            state: RunStates.Running,
            actions: 0,
            maximumActions: 8);
        dispatchRun.CurrentTurnId = turnId;
        dispatchRun.Usage.Turns = 1;
        var providerStart = Event(
            "provider-start",
            2,
            RuntimeEventKinds.ProviderDispatchStarted,
            ProtocolJson.ToElement(dispatchRun));
        providerStart.TurnId = turnId;
        providerStart.AttemptId = attemptId;
        providerStart.StreamAttemptId = streamAttemptId;
        providerStart.ProviderId = "provider";
        providerStart.ModelId = "model";
        providerStart.TransportDialect = "responses";
        providerStart.ProviderCapabilityDigest = new string('a', 64);
        const string routePolicyVersion = "route-policy.v1";
        var routePolicyDigest = new string('b', 64);
        providerStart.ProviderRouteDigest =
            ProviderRouteIdentity.ComputeRouteDigest(
                providerStart.ProviderId,
                providerStart.ModelId,
                providerStart.TransportDialect,
                providerStart.ProviderCapabilityDigest,
                routePolicyVersion,
                routePolicyDigest);
        providerStart.Extensions[
                ProviderRouteJournalExtensions.PolicyVersion] =
            JsonArrayBuilder.String(routePolicyVersion);
        providerStart.Extensions[
                ProviderRouteJournalExtensions.PolicyDigest] =
            JsonArrayBuilder.String(routePolicyDigest);
        var usageRun = Run(
            revision: 4,
            state: RunStates.Running,
            actions: 0,
            maximumActions: 8);
        usageRun.CurrentTurnId = turnId;
        usageRun.Usage.Turns = 1;
        usageRun.Usage.InputTokens = 10;
        usageRun.Usage.OutputTokens = 5;
        usageRun.Usage.CostUsd = "0.02";
        var usageEvent = Event(
            "provider-usage",
            3,
            RuntimeEventKinds.BudgetUpdated,
            ProtocolJson.ToElement(usageRun));
        usageEvent.TurnId = turnId;
        usageEvent.AttemptId = attemptId;
        usageEvent.StreamAttemptId = streamAttemptId;
        usageEvent.ProviderId = "provider";
        var providerDoneRun = Run(
            revision: 6,
            state: RunStates.Running,
            actions: 0,
            maximumActions: 8);
        providerDoneRun.CurrentTurnId = turnId;
        providerDoneRun.Usage.Turns = 1;
        providerDoneRun.Usage.InputTokens = 10;
        providerDoneRun.Usage.OutputTokens = 5;
        providerDoneRun.Usage.CostUsd = "0.02";
        var providerDone = Event(
            "provider-done",
            5,
            RuntimeEventKinds.ProviderResultCommitted,
            ProtocolJson.ToElement(providerDoneRun));
        providerDone.TurnId = turnId;
        providerDone.AttemptId = attemptId;
        providerDone.StreamAttemptId = streamAttemptId;
        providerDone.ProviderId = "provider";
        var toolStarted = Event(
            "tool-started",
            6,
            RuntimeEventKinds.ToolStarted,
            ProtocolJson.ToElement(invocation));
        toolStarted.TurnId = turnId;
        toolStarted.AttemptId = attemptId;
        var actionRequested = Event(
            "action-requested",
            7,
            RuntimeEventKinds.ActionRequested,
            ProtocolJson.ToElement(request));
        actionRequested.TurnId = turnId;
        actionRequested.AttemptId = attemptId;
        var actionReceived = Event(
            "action-received",
            8,
            RuntimeEventKinds.ActionReceived,
            ProtocolJson.ToElement(receipt));
        actionReceived.TurnId = turnId;
        actionReceived.AttemptId = attemptId;
        var toolCompleted = Event(
            "tool-completed",
            9,
            RuntimeEventKinds.ToolCompleted,
            ProtocolJson.ToElement(receipt));
        toolCompleted.TurnId = turnId;
        toolCompleted.AttemptId = attemptId;
        var assistantEvent = Event(
            "assistant",
            4,
            RuntimeEventKinds.TranscriptMessage,
            NormalizedMessageJournalCodec.Encode(assistant));
        assistantEvent.TurnId = turnId;
        assistantEvent.AttemptId = attemptId;
        var toolMessageEvent = Event(
            "tool-message",
            10,
            RuntimeEventKinds.TranscriptMessage,
            NormalizedMessageJournalCodec.Encode(toolResult));
        toolMessageEvent.TurnId = turnId;
        toolMessageEvent.AttemptId = attemptId;
        var turnCompletedRun = Run(
            revision: 12,
            state: RunStates.Running,
            actions: 1,
            maximumActions: 8);
        turnCompletedRun.Usage.Turns = 1;
        turnCompletedRun.Usage.InputTokens = 10;
        turnCompletedRun.Usage.OutputTokens = 5;
        turnCompletedRun.Usage.CostUsd = "0.02";
        var turnCompleted = Event(
            "turn-completed",
            11,
            RuntimeEventKinds.TurnCompleted,
            ProtocolJson.ToElement(turnCompletedRun));
        turnCompleted.TurnId = turnId;
        turnCompleted.AttemptId = attemptId;
        var completedRun = Run(
            revision: 13,
            state: RunStates.Completed,
            actions: 1,
            maximumActions: 8);
        completedRun.Usage.Turns = 1;
        completedRun.Usage.InputTokens = 10;
        completedRun.Usage.OutputTokens = 5;
        completedRun.Usage.CostUsd = "0.02";
        return new[]
        {
            Event(
                "start",
                0,
                RuntimeEventKinds.RunStarted,
                ProtocolJson.ToElement(startRun)),
            turnStarted,
            providerStart,
            usageEvent,
            assistantEvent,
            providerDone,
            toolStarted,
            actionRequested,
            actionReceived,
            toolCompleted,
            toolMessageEvent,
            turnCompleted,
            Event(
                "done",
                12,
                RuntimeEventKinds.RunCompleted,
                ProtocolJson.ToElement(completedRun))
        };
    }

    private static AgentRun Run(
        long revision,
        string state,
        int actions,
        int maximumActions)
    {
        return new AgentRun
        {
            RunId = "run",
            AgentId = "agent",
            WorldId = "world",
            Trigger = new AgentTrigger
            {
                Type = "manual"
            },
            State = state,
            Revision = revision,
            RuntimeGeneration = 1,
            Budget = new AgentBudget
            {
                MaxTurns = 8,
                MaxDurationMs = 30_000,
                MaxTokens = 8_000,
                MaxCostUsd = "1",
                MaxActions = maximumActions
            },
            Usage = new AgentUsage
            {
                Actions = actions,
                CostUsd = "0",
                Availability = UsageAvailabilityStates.CostAvailable
            },
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static string ScenarioLine(
        string scenarioId,
        IEnumerable<RuntimeEvent> events)
    {
        return "{\"schema\":\"game-agent.scenario.v1\","
            + "\"scenarioId\":\""
            + scenarioId
            + "\",\"events\":["
            + string.Join(",", events.Select(ProtocolJson.Serialize))
            + "],\"expectation\":{\"terminalKind\":\"run.completed\"}}";
    }

    private static RuntimeEvent Event(
        string id,
        long sequence,
        string kind,
        JsonElement? payload = null)
    {
        return new RuntimeEvent
        {
            EventId = id,
            RunId = "run",
            Sequence = sequence,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Payload = payload ?? ProtocolJson.ParseElement("{}")
        };
    }

    private sealed class CountingInfiniteEvents :
        IEnumerable<RuntimeEvent>
    {
        public int EnumeratorCount { get; private set; }

        public int MoveNextCount { get; private set; }

        public IEnumerator<RuntimeEvent> GetEnumerator()
        {
            EnumeratorCount++;
            long sequence = 0;
            while (true)
            {
                MoveNextCount++;
                yield return Event(
                    "event-" + sequence,
                    sequence,
                    RuntimeEventKinds.RunCheckpoint);
                sequence++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
