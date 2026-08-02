using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class GeneratedPlanTests
{
    [Fact]
    public async Task Generated_command_graph_runs_independent_steps_in_parallel_and_preserves_floats()
    {
        var host = new ParallelHost();
        var workflow = Compiler().Compile(WorkflowTestData.Json(
            """
            {
              "planId":"npc-plan",
              "version":"1",
              "outputStepId":"finish",
              "steps":[
                {"id":"left","command":"act","arguments":{"amount":3.5}},
                {"id":"right","command":"act","arguments":{"amount":2.25}},
                {
                  "id":"finish",
                  "command":"join",
                  "arguments":{"amount":1.5},
                  "dependsOn":["left","right"],
                  "durationSeconds":0.125
                }
              ]
            }
            """));
        var runner = Runner(host);

        var result = await runner.ExecuteAsync(
            workflow,
            new WorkflowRunRequest("parallel-plan", "owner", WorkflowTestData.Json("{}")));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(1.5, result.Output!.Value.GetProperty("amount").GetDouble());
        Assert.True(host.MaxActive >= 2);
        Assert.Contains(3.5, host.Amounts);
        Assert.Contains(2.25, host.Amounts);
        Assert.Contains(0.125, host.Durations);
    }

    [Fact]
    public async Task Durable_receipt_prevents_side_effect_replay_after_interruption()
    {
        var host = new ReceiptThenCrashHost();
        var workflow = Compiler().Compile(WorkflowTestData.Json(
            """
            {
              "planId":"receipt-plan",
              "version":"1",
              "outputStepId":"only",
              "steps":[
                {"id":"only","command":"act","arguments":{"amount":3.5}}
              ]
            }
            """));
        var store = new InMemoryWorkflowRunStore();
        var runner = Runner(host, store);

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest("receipt-run", "owner-a", WorkflowTestData.Json("{}"))));
        var started = WorkflowIdentity.CreateRunId(
            workflow.DefinitionDigest,
            WorkflowIdentity.ComputeJsonDigest(WorkflowTestData.Json("{}")),
            "receipt-run");
        var recovered = await runner.RecoverAsync(workflow, started, "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal(1, host.SideEffectCalls);
        Assert.Equal(3.5, recovered.Output!.Value.GetProperty("amount").GetDouble());
    }

    [Fact]
    public async Task External_attention_interrupts_workflow_until_durable_resolution()
    {
        var attentionStore = new InMemoryExternalAttentionStore();
        var attention = new ExternalAttentionCoordinator(attentionStore);
        var host = new AttentionHost(attentionStore, attention);
        var workflow = Compiler().Compile(WorkflowTestData.Json(
            """
            {
              "planId":"attention-plan",
              "version":"1",
              "outputStepId":"ask",
              "steps":[
                {"id":"ask","command":"act","arguments":{"amount":3.5}}
              ]
            }
            """));
        var store = new InMemoryWorkflowRunStore();
        var runner = Runner(host, store);

        await Assert.ThrowsAsync<WorkflowExecutorInterruptedException>(
            async () => await runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest("attention-run", "owner-a", WorkflowTestData.Json("{}"))));
        var pending = Assert.Single(await attentionStore.ListPendingAsync(null, 10, default));
        await attention.ResolveAsync(
            pending.Request.RequestId,
            new ExternalAttentionResolution
            {
                ResolutionId = "player-choice-1",
                AuthorityId = "host",
                StateBindingDigest = new string('a', 64),
                Payload = WorkflowTestData.Json("{\"amount\":7.25}"),
                ResolvedAt = new GameTimePoint("calendar", "main", 0, 11)
            },
            pending.Revision);
        var runId = WorkflowIdentity.CreateRunId(
            workflow.DefinitionDigest,
            WorkflowIdentity.ComputeJsonDigest(WorkflowTestData.Json("{}")),
            "attention-run");

        var recovered = await runner.RecoverAsync(workflow, runId, "owner-b");

        Assert.Equal(WorkflowRunStatus.Completed, recovered.Status);
        Assert.Equal(7.25, recovered.Output!.Value.GetProperty("amount").GetDouble());
        Assert.Equal(1, host.RequestCalls);
    }

    [Fact]
    public async Task Generated_plan_can_fan_out_reduce_and_run_a_bounded_feedback_loop()
    {
        var host = new CompositeHost();
        var compiler = new GeneratedPlanCompiler(new[]
        {
            new GeneratedPlanCommandDescriptor(
                "seed_items",
                EmptyObjectSchema(),
                ItemCollectionSchema()),
            new GeneratedPlanCommandDescriptor(
                "scale_item",
                EmptyObjectSchema(),
                ItemSchema(),
                ItemSchema()),
            new GeneratedPlanCommandDescriptor(
                "reduce_items",
                EmptyObjectSchema(),
                CounterSchema()),
            new GeneratedPlanCommandDescriptor(
                "advance_counter",
                EmptyObjectSchema(),
                CounterSchema(),
                CounterSchema())
        });
        var workflow = compiler.Compile(WorkflowTestData.Json(
            """
            {
              "planId":"composite-plan",
              "version":"1",
              "outputStepId":"advance",
              "steps":[
                {
                  "id":"seed",
                  "command":"seed_items",
                  "arguments":{}
                },
                {
                  "id":"fanout",
                  "kind":"foreach",
                  "command":"scale_item",
                  "arguments":{},
                  "dependsOn":["seed"],
                  "sourcePointer":"/items",
                  "itemIdentityPointer":"/id",
                  "maxItems":4
                },
                {
                  "id":"sum",
                  "kind":"reduce",
                  "command":"reduce_items",
                  "arguments":{},
                  "dependsOn":["fanout"]
                },
                {
                  "id":"advance",
                  "kind":"loop",
                  "command":"advance_counter",
                  "arguments":{},
                  "dependsOn":["sum"],
                  "untilPointer":"/done",
                  "maxIterations":4
                }
              ]
            }
            """));

        var result = await Runner(host).ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "composite-run",
                "owner",
                WorkflowTestData.Json("{}")));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(8, result.Output!.Value.GetProperty("value").GetInt32());
        Assert.True(result.Output.Value.GetProperty("done").GetBoolean());
        Assert.Equal(new[] { "a", "b" }, host.ScaledIds.OrderBy(x => x).ToArray());
        Assert.Equal(2, result.Usage.ForeachItems);
        Assert.Equal(2, result.Usage.LoopIterations);
    }

    [Fact]
    public async Task Ordinary_command_can_consume_a_foreach_array()
    {
        var host = new CompositeHost();
        var compiler = new GeneratedPlanCompiler(new[]
        {
            new GeneratedPlanCommandDescriptor(
                "seed_items",
                EmptyObjectSchema(),
                ItemCollectionSchema()),
            new GeneratedPlanCommandDescriptor(
                "scale_item",
                EmptyObjectSchema(),
                ItemSchema(),
                ItemSchema()),
            new GeneratedPlanCommandDescriptor(
                "count_items",
                EmptyObjectSchema(),
                CounterSchema())
        });
        var workflow = compiler.Compile(WorkflowTestData.Json(
            """
            {
              "planId":"foreach-consumer-plan",
              "version":"1",
              "outputStepId":"count",
              "steps":[
                {"id":"seed","command":"seed_items","arguments":{}},
                {
                  "id":"fanout",
                  "kind":"foreach",
                  "command":"scale_item",
                  "arguments":{},
                  "dependsOn":["seed"],
                  "sourcePointer":"/items",
                  "itemIdentityPointer":"/id",
                  "maxItems":4
                },
                {
                  "id":"count",
                  "command":"count_items",
                  "arguments":{},
                  "dependsOn":["fanout"]
                }
              ]
            }
            """));

        var result = await Runner(host).ExecuteAsync(
            workflow,
            new WorkflowRunRequest(
                "foreach-consumer-run",
                "owner",
                WorkflowTestData.Json("{}")));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Output!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Generated_composite_plan_requires_host_schema_and_respects_expansion_budget()
    {
        var schema = CounterSchema();
        var missingInputSchema = new GeneratedPlanCompiler(new[]
        {
            new GeneratedPlanCommandDescriptor(
                "advance",
                EmptyObjectSchema(),
                schema)
        });
        var loop = WorkflowTestData.Json(
            """
            {
              "planId":"bounded-plan",
              "version":"1",
              "outputStepId":"advance",
              "steps":[{
                "id":"advance",
                "kind":"loop",
                "command":"advance",
                "arguments":{},
                "untilPointer":"/done",
                "maxIterations":2
              }]
            }
            """);

        var missing = Assert.Throws<GeneratedPlanAdmissionException>(
            () => missingInputSchema.Compile(loop));
        Assert.Equal("generated_plan_execution_schema_missing", missing.ReasonCode);

        var bounded = new GeneratedPlanCompiler(
            new[]
            {
                new GeneratedPlanCommandDescriptor(
                    "advance",
                    EmptyObjectSchema(),
                    schema,
                    schema)
            },
            new GeneratedPlanAdmissionOptions
            {
                MaxExpandedStageExecutions = 1
            });
        var exceeded = Assert.Throws<GeneratedPlanAdmissionException>(
            () => bounded.Compile(loop));
        Assert.Equal("generated_plan_execution_limit", exceeded.ReasonCode);
    }

    private static GeneratedPlanCompiler Compiler()
    {
        var arguments = WorkflowTestData.Json(
            """
            {
              "type":"object",
              "properties":{"amount":{"type":"number","minimum":-1000,"maximum":1000}},
              "required":["amount"],
              "additionalProperties":false
            }
            """);
        var result = arguments;
        return new GeneratedPlanCompiler(new[]
        {
            new GeneratedPlanCommandDescriptor("act", arguments, result),
            new GeneratedPlanCommandDescriptor("join", arguments, result)
        });
    }

    private static JsonElement EmptyObjectSchema() => WorkflowTestData.Json(
        """
        {"type":"object","properties":{},"additionalProperties":false}
        """);

    private static JsonElement ItemSchema() => WorkflowTestData.Json(
        """
        {
          "type":"object",
          "properties":{
            "id":{"type":"string","maxLength":32},
            "amount":{"type":"number","minimum":0,"maximum":100}
          },
          "required":["id","amount"],
          "additionalProperties":false
        }
        """);

    private static JsonElement ItemCollectionSchema() => WorkflowTestData.Json(
        """
        {
          "type":"object",
          "properties":{
            "items":{
              "type":"array",
              "items":{
                "type":"object",
                "properties":{
                  "id":{"type":"string","maxLength":32},
                  "amount":{"type":"number","minimum":0,"maximum":100}
                },
                "required":["id","amount"],
                "additionalProperties":false
              },
              "maxItems":4
            }
          },
          "required":["items"],
          "additionalProperties":false
        }
        """);

    private static JsonElement CounterSchema() => WorkflowTestData.Json(
        """
        {
          "type":"object",
          "properties":{
            "value":{"type":"integer","minimum":0,"maximum":1000},
            "done":{"type":"boolean"}
          },
          "required":["value","done"],
          "additionalProperties":false
        }
        """);

    private static WorkflowRunner Runner(
        IGeneratedPlanCommandHost host,
        IWorkflowRunStore? store = null) =>
        new(
            store ?? new InMemoryWorkflowRunStore(),
            new WorkflowStepExecutorRegistry(new IWorkflowStepExecutor[]
            {
                new GeneratedPlanStepExecutor(host)
            }));

    private sealed class ParallelHost : IGeneratedPlanCommandHost
    {
        private int _active;
        private int _maxActive;

        public int MaxActive => Volatile.Read(ref _maxActive);

        public ConcurrentBag<double> Amounts { get; } = new();

        public ConcurrentBag<double> Durations { get; } = new();

        public async ValueTask<GeneratedPlanCommandReceipt> ExecuteAsync(
            GeneratedPlanCommandRequest request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                Amounts.Add(request.Arguments.GetProperty("amount").GetDouble());
                if (request.DurationSeconds.HasValue)
                {
                    Durations.Add(request.DurationSeconds.Value);
                }

                await Task.Delay(40, cancellationToken);
                return new GeneratedPlanCommandReceipt
                {
                    Succeeded = true,
                    Result = request.Arguments.Clone()
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<GeneratedPlanCommandReceipt?> TryGetReceiptAsync(
            string executionId,
            CancellationToken cancellationToken) => default;

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActive);
                if (value <= current
                    || Interlocked.CompareExchange(ref _maxActive, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ReceiptThenCrashHost : IGeneratedPlanCommandHost
    {
        private readonly ConcurrentDictionary<string, GeneratedPlanCommandReceipt> _receipts =
            new(StringComparer.Ordinal);

        public int SideEffectCalls { get; private set; }

        public ValueTask<GeneratedPlanCommandReceipt> ExecuteAsync(
            GeneratedPlanCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SideEffectCalls++;
            _receipts[request.ExecutionId] = new GeneratedPlanCommandReceipt
            {
                Succeeded = true,
                Result = request.Arguments.Clone()
            };
            throw new WorkflowExecutorInterruptedException("simulated process loss");
        }

        public ValueTask<GeneratedPlanCommandReceipt?> TryGetReceiptAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _receipts.TryGetValue(executionId, out var receipt);
            return new ValueTask<GeneratedPlanCommandReceipt?>(receipt);
        }
    }

    private sealed class AttentionHost : IGeneratedPlanCommandHost
    {
        private readonly IExternalAttentionStore _store;
        private readonly ExternalAttentionCoordinator _coordinator;

        public AttentionHost(
            IExternalAttentionStore store,
            ExternalAttentionCoordinator coordinator)
        {
            _store = store;
            _coordinator = coordinator;
        }

        public int RequestCalls { get; private set; }

        public async ValueTask<GeneratedPlanCommandReceipt> ExecuteAsync(
            GeneratedPlanCommandRequest request,
            CancellationToken cancellationToken)
        {
            RequestCalls++;
            await _coordinator.RequestAsync(new ExternalAttentionRequest
            {
                RequestId = request.ExecutionId,
                Kind = "player_choice",
                WorldId = "world",
                WorkflowId = request.PlanId,
                AuthorityId = "host",
                StateBindingDigest = new string('a', 64),
                Payload = request.Arguments.Clone(),
                CreatedAt = new GameTimePoint("calendar", "main", 0, 10)
            }, cancellationToken);
            throw new WorkflowExecutorInterruptedException("waiting for external attention");
        }

        public async ValueTask<GeneratedPlanCommandReceipt?> TryGetReceiptAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            var record = await _store.TryGetAsync(executionId, cancellationToken);
            return record?.State == ExternalAttentionStates.Resolved
                ? new GeneratedPlanCommandReceipt
                {
                    Succeeded = true,
                    Result = record.Resolution!.Payload.Clone()
                }
                : null;
        }
    }

    private sealed class CompositeHost : IGeneratedPlanCommandHost
    {
        public ConcurrentBag<string> ScaledIds { get; } = new();

        public ValueTask<GeneratedPlanCommandReceipt> ExecuteAsync(
            GeneratedPlanCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement result;
            switch (request.Command)
            {
                case "seed_items":
                    result = WorkflowTestData.Json(
                        """{"items":[{"id":"a","amount":1},{"id":"b","amount":2}]}""");
                    break;
                case "scale_item":
                    var id = request.UpstreamInput.GetProperty("id").GetString()!;
                    ScaledIds.Add(id);
                    result = WorkflowTestData.Json(
                        $$"""{"id":"{{id}}","amount":{{request.UpstreamInput.GetProperty("amount").GetInt32() * 2}}}""");
                    break;
                case "reduce_items":
                    var output = request.UpstreamInput[0].GetProperty("output");
                    var sum = output.EnumerateArray()
                        .Sum(item => item.GetProperty("amount").GetInt32());
                    result = WorkflowTestData.Json(
                        $$"""{"value":{{sum}},"done":false}""");
                    break;
                case "advance_counter":
                    var value = request.UpstreamInput.GetProperty("value").GetInt32() + 1;
                    result = WorkflowTestData.Json(
                        $$"""{"value":{{value}},"done":{{(value >= 8 ? "true" : "false")}}}""");
                    break;
                case "count_items":
                    result = WorkflowTestData.Json(
                        $$"""{"value":{{request.UpstreamInput.GetArrayLength()}},"done":true}""");
                    break;
                default:
                    throw new InvalidOperationException(request.Command);
            }

            return new ValueTask<GeneratedPlanCommandReceipt>(
                new GeneratedPlanCommandReceipt
                {
                    Succeeded = true,
                    Result = result
                });
        }

        public ValueTask<GeneratedPlanCommandReceipt?> TryGetReceiptAsync(
            string executionId,
            CancellationToken cancellationToken) => default;
    }
}
