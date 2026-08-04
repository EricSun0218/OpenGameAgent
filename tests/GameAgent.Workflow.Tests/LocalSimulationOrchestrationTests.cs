using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class LocalSimulationOrchestrationTests
{
    [Fact]
    public async Task LocalStagesFanOutByRoleAndGamePolicyOwnsOptionalFallback()
    {
        var modelRuntime = new ParallelRoleRuntime();
        var adapter = new LocalRoleAdapter();
        var runner = new WorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(
                new IWorkflowStepExecutor[]
                {
                    new PlanExecutor(),
                    new WorkflowAgentStepExecutor(modelRuntime, adapter),
                    new FinalizeExecutor()
                }));

        using var testDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var result = await runner.ExecuteAsync(
            Compile(),
            new WorkflowRunRequest(
                "local-turn-42",
                "local-save-slot",
                WorkflowTestData.Json(
                    """
                    {
                      "tick": 42,
                      "pressure": -1.5
                    }
                    """)),
            testDeadline.Token);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(2, modelRuntime.MaxActive);
        Assert.Equal(
            new[] { "route-dialogue", "route-simulation" },
            modelRuntime.RouteIds.OrderBy(value => value));
        Assert.Equal(
            1.25,
            result.Output!.Value.GetProperty("score").GetDouble());
        Assert.Equal(
            1,
            result.Output.Value.GetProperty("fallbackCount").GetInt32());
        Assert.Equal(
            new[] { "primary", "weather" },
            result.Output.Value
                .GetProperty("orderedIds")
                .EnumerateArray()
                .Select(value => value.GetString()));

        var agentInstances = result.StageInstances
            .Where(instance =>
                instance.InstanceKind == WorkflowInstanceKind.ForeachItem)
            .OrderBy(instance => instance.ItemOrdinal)
            .ToArray();
        Assert.Equal(2, agentInstances.Length);
        Assert.Equal(2, modelRuntime.RunIds.Count);
        Assert.All(
            modelRuntime.RunIds,
            runId => Assert.StartsWith("wfa_", runId));
    }

    private static CompiledWorkflow Compile()
    {
        var turn = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "tick": { "type": "integer", "minimum": 0, "maximum": 1000000000 },
                "pressure": { "type": "number", "minimum": -100, "maximum": 100 }
              },
              "required": ["tick", "pressure"],
              "additionalProperties": false
            }
            """);
        var task = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "maxLength": 64 },
                "role": { "type": "string", "maxLength": 64 },
                "temperature": { "type": "number", "minimum": 0, "maximum": 2 },
                "weight": { "type": "number", "minimum": -100, "maximum": 100 },
                "optional": { "type": "boolean" }
              },
              "required": ["id", "role", "temperature", "weight", "optional"],
              "additionalProperties": false
            }
            """);
        var taskBatch = WorkflowTestData.Json(
            $$"""
            {
              "type": "object",
              "properties": {
                "tasks": {
                  "type": "array",
                  "items": {{task.GetRawText()}},
                  "maxItems": 8
                }
              },
              "required": ["tasks"],
              "additionalProperties": false
            }
            """);
        var decision = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "maxLength": 64 },
                "value": { "type": "number", "minimum": -100, "maximum": 100 },
                "usedFallback": { "type": "boolean" }
              },
              "required": ["id", "value", "usedFallback"],
              "additionalProperties": false
            }
            """);
        var decisions = WorkflowTestData.Json(
            $$"""
            {
              "type": "array",
              "items": {{decision.GetRawText()}},
              "maxItems": 8
            }
            """);
        var reduceInput = WorkflowTestData.Json(
            $$"""
            {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "stageId": { "type": "string", "maxLength": 128 },
                  "instanceId": { "type": "string", "maxLength": 80 },
                  "output": {{decisions.GetRawText()}}
                },
                "required": ["stageId", "instanceId", "output"],
                "additionalProperties": false
              },
              "maxItems": 1
            }
            """);
        var final = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "score": { "type": "number", "minimum": -1000, "maximum": 1000 },
                "fallbackCount": { "type": "integer", "minimum": 0, "maximum": 8 },
                "orderedIds": {
                  "type": "array",
                  "items": { "type": "string", "maxLength": 64 },
                  "maxItems": 8
                }
              },
              "required": ["score", "fallbackCount", "orderedIds"],
              "additionalProperties": false
            }
            """);

        var plan = WorkflowStageDefinition.CreateStep(
            "plan",
            new WorkflowStepReference(PlanExecutor.StepKind),
            turn,
            taskBatch);
        var fanOut = WorkflowStageDefinition.CreateForeach(
            "actors",
            new WorkflowForEachDefinition(
                new WorkflowStepReference(WorkflowAgentStepKinds.Run),
                "/tasks",
                "/id",
                8,
                task,
                decision),
            taskBatch,
            decisions,
            new[] { "plan" });
        var finalize = WorkflowStageDefinition.CreateReduce(
            "finalize",
            new WorkflowReduceDefinition(
                new WorkflowStepReference(FinalizeExecutor.StepKind)),
            reduceInput,
            final,
            new[] { "actors" });
        return new WorkflowCompiler().Compile(
            new WorkflowDefinition(
                "local-simulation",
                "v1",
                turn,
                final,
                "finalize",
                new[] { finalize, fanOut, plan },
                new WorkflowLimits(
                    maxParallelism: 2,
                    maxForeachItems: 8)));
    }

    private sealed class PlanExecutor : IWorkflowStepExecutor
    {
        public const string StepKind = "test/local-plan";

        public string Kind => StepKind;

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            Assert.Equal(-1.5, input.GetProperty("pressure").GetDouble());
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(
                    WorkflowTestData.Json(
                        """
                        {
                          "tasks": [
                            {
                              "id": "primary",
                              "role": "dialogue",
                              "temperature": 0.25,
                              "weight": 1.25,
                              "optional": false
                            },
                            {
                              "id": "weather",
                              "role": "simulation",
                              "temperature": 0.75,
                              "weight": -0.5,
                              "optional": true
                            }
                          ]
                        }
                        """)));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteAsync(context, input, cancellationToken);
    }

    private sealed class LocalRoleAdapter :
        IWorkflowAgentRunAdapter,
        IWorkflowAgentTerminalOutcomeProjector
    {
        public DurableRunRequest CreateRequest(
            WorkflowAgentInvocation invocation,
            JsonElement input)
        {
            var role = input.GetProperty("role").GetString()!;
            return new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = invocation.AgentRunId,
                    AgentId = role,
                    WorldId = "local-world",
                    Trigger = new AgentTrigger
                    {
                        Type = "local-workflow",
                        SourceId = invocation.WorkflowRunId
                    }
                },
                Inference = new ModelInferenceOptions
                {
                    Temperature = input
                        .GetProperty("temperature")
                        .GetDouble()
                },
                RoutePreference = new ProviderRoutePreference
                {
                    ProviderIds = new[] { "route-" + role }
                }
            };
        }

        public DurableRunContinuation? CreateContinuation(
            WorkflowAgentInvocation invocation,
            JsonElement input) => new();

        public DurableRunResumeGuard? CreateResumeGuard(
            WorkflowAgentInvocation invocation,
            JsonElement input) => new()
            {
                ExpectedAgentId = input.GetProperty("role").GetString()
            };

        public JsonElement ProjectOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome)
        {
            return Result(
                input.GetProperty("id").GetString()!,
                outcome.FinalOutput!.Value.GetProperty("value").GetDouble(),
                usedFallback: false);
        }

        public bool TryProjectTerminalOutcome(
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome,
            out JsonElement output)
        {
            if (input.GetProperty("optional").GetBoolean()
                && string.Equals(
                    outcome.Run.State,
                    RunStates.Failed,
                    StringComparison.Ordinal))
            {
                output = Result(
                    input.GetProperty("id").GetString()!,
                    value: 0,
                    usedFallback: true);
                return true;
            }

            output = default;
            return false;
        }

        private static JsonElement Result(
            string id,
            double value,
            bool usedFallback)
        {
            return CreateJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteNumber("value", value);
                writer.WriteBoolean("usedFallback", usedFallback);
                writer.WriteEndObject();
            });
        }
    }

    private sealed class ParallelRoleRuntime : IDurableAgentRuntime
    {
        private readonly TaskCompletionSource<bool> _bothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentBag<string> _routeIds = new();
        private readonly ConcurrentBag<string> _runIds = new();
        private int _active;
        private int _maxActive;

        public RuntimeControlPlane Controls { get; } = new();

        public int MaxActive => Volatile.Read(ref _maxActive);

        public IReadOnlyList<string> RouteIds => _routeIds.ToArray();

        public IReadOnlyList<string> RunIds => _runIds.ToArray();

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            _routeIds.Add(request.RoutePreference!.ProviderIds.Single());
            _runIds.Add(request.Run.RunId);
            if (active >= 2)
            {
                _bothStarted.TrySetResult(true);
            }

            try
            {
                await _bothStarted.Task.WaitAsync(cancellationToken);
                if (string.Equals(
                        request.Run.AgentId,
                        "simulation",
                        StringComparison.Ordinal))
                {
                    return Outcome(request.Run, RunStates.Failed);
                }

                return new DurableRunOutcome
                {
                    Run = CopyRun(request.Run, RunStates.Completed),
                    FinalOutput = CreateJson(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("value", 1.25);
                        writer.WriteEndObject();
                    })
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static DurableRunOutcome Outcome(AgentRun run, string state) =>
            new()
            {
                Run = CopyRun(run, state)
            };

        private static AgentRun CopyRun(AgentRun run, string state) => new()
        {
            RunId = run.RunId,
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            State = state
        };

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActive);
                if (value <= current
                    || Interlocked.CompareExchange(
                        ref _maxActive,
                        value,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FinalizeExecutor : IWorkflowStepExecutor
    {
        public const string StepKind = "test/local-finalize";

        public string Kind => StepKind;

        public ValueTask<WorkflowStepResult> ExecuteAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken)
        {
            var values = input[0]
                .GetProperty("output")
                .EnumerateArray()
                .ToArray();
            var output = CreateJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "score",
                    values.Sum(value =>
                        value.GetProperty("value").GetDouble()));
                writer.WriteNumber(
                    "fallbackCount",
                    values.Count(value =>
                        value.GetProperty("usedFallback").GetBoolean()));
                writer.WriteStartArray("orderedIds");
                foreach (var value in values)
                {
                    writer.WriteStringValue(
                        value.GetProperty("id").GetString());
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return new ValueTask<WorkflowStepResult>(
                WorkflowStepResult.Completed(output));
        }

        public ValueTask<WorkflowStepResult> RecoverAsync(
            WorkflowStepContext context,
            JsonElement input,
            CancellationToken cancellationToken) =>
            ExecuteAsync(context, input, cancellationToken);
    }

    private static JsonElement CreateJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
