using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class TaskPlanExtensionTests
{
    [Fact]
    public async Task OrderedChecklistAdvancesOncePerInputAndPublishesScopedChanges()
    {
        var store = new InMemoryGameSessionStore();
        var changes = new ConcurrentQueue<GameTaskPlanChanged>();
        var provider = new ScriptedProvider(call => call switch
        {
            1 => ToolCall("create", "{\"action\":\"create\",\"planId\":\"build\",\"objective\":\"finish work\",\"steps\":[\"prepare\",\"execute\",\"verify\"]}"),
            2 => ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"build\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"operation-1\"}}"),
            3 => ToolCall("duplicate", "{\"action\":\"advance\",\"planId\":\"build\",\"expectedRevision\":2,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"operation-1\"}}"),
            _ => TextResponse("done"),
        });
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(
                "task-plan.listener",
                "1",
                api => api.Subscribe(TaskPlanExtension.PlanChanged, (change, _) =>
                {
                    changes.Enqueue(change);
                    return ValueTask.CompletedTask;
                }))
            .Build();

        var result = await runtime.RunAsync(Input("input-1"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        using var document = await ReadOnlyPlanAsync(store, "session", "actor");
        Assert.Equal(2, document.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(
            new[] { "Completed", "InProgress", "Pending" },
            Statuses(document.RootElement));
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.Equal(new GameSessionKey("session", "actor"), change.Session);
            Assert.Equal("input-1", change.InputId);
        });
    }

    [Fact]
    public async Task ForgedEvidenceFailsClosedWithoutChangingRevision()
    {
        var store = new InMemoryGameSessionStore();
        await RunAsync(
            store,
            new TaskPlanExtension((_, _) => new ValueTask<bool>(true)),
            Input("create"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"proof\",\"objective\":\"prove work\",\"steps\":[\"act\",\"verify\"]}"),
            TextResponse("created"));
        await RunAsync(
            store,
            new TaskPlanExtension((request, _) =>
                new ValueTask<bool>(request.Reference == "trusted")),
            Input("forged"),
            ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"proof\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"invented\"}}"),
            TextResponse("rejected"));

        using var document = await ReadOnlyPlanAsync(store, "session", "actor");
        Assert.Equal(1, document.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(new[] { "InProgress", "Pending" }, Statuses(document.RootElement));
    }

    [Fact]
    public async Task ReplaceRemainingPreservesCompletedPrefix()
    {
        var store = new InMemoryGameSessionStore();
        var extension = new TaskPlanExtension((_, _) => new ValueTask<bool>(true));
        await RunAsync(
            store,
            extension,
            Input("create"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"adaptive\",\"objective\":\"adapt\",\"steps\":[\"first\",\"obsolete\",\"later\"]}"),
            TextResponse("created"));
        await RunAsync(
            store,
            extension,
            Input("advance"),
            ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"adaptive\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"observation\",\"reference\":\"world-2\"}}"),
            TextResponse("advanced"));
        await RunAsync(
            store,
            extension,
            Input("replan"),
            ToolCall("replace", "{\"action\":\"replace_remaining\",\"planId\":\"adaptive\",\"expectedRevision\":2,\"steps\":[\"new second\",\"new third\"]}"),
            TextResponse("replanned"));

        using var document = await ReadOnlyPlanAsync(store, "session", "actor");
        var steps = document.RootElement.GetProperty("Steps").EnumerateArray().ToArray();
        Assert.Equal(new[] { "first", "new second", "new third" },
            steps.Select(step => step.GetProperty("Text").GetString()).ToArray());
        Assert.Equal(new[] { "Completed", "InProgress", "Pending" }, Statuses(document.RootElement));
        Assert.Equal("step-1", steps[0].GetProperty("Id").GetString());
    }

    [Fact]
    public async Task ConcurrentMutationsUseSessionCas()
    {
        var store = new InMemoryGameSessionStore();
        await RunAsync(
            store,
            new TaskPlanExtension((_, _) => new ValueTask<bool>(true)),
            Input("seed"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"shared\",\"objective\":\"shared work\",\"steps\":[\"one\",\"two\"]}"),
            TextResponse("created"));

        var gate = new ConcurrentRunGate(2);
        var response = ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"shared\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"valid\"}}");
        await using var left = new GameAgentBuilder(new FirstCallBarrierProvider(gate, response), "model")
            .UseSessionStore(store)
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .Build();
        await using var right = new GameAgentBuilder(new FirstCallBarrierProvider(gate, response), "model")
            .UseSessionStore(store)
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .Build();

        var results = await Task.WhenAll(
            left.RunAsync(Input("left"), TestContext.Current.CancellationToken),
            right.RunAsync(Input("right"), TestContext.Current.CancellationToken));

        // The committed checklist below is authoritative even if a later usage settlement makes
        // both concurrent callers conservatively report a session conflict.
        Assert.Contains(results, result => result.Status == GameAgentRunStatus.SessionConflict);
        Assert.All(
            results,
            result => Assert.True(
                result.Status is GameAgentRunStatus.Completed or GameAgentRunStatus.SessionConflict,
                $"Unexpected concurrent run status '{result.Status}'."));
        using var document = await ReadOnlyPlanAsync(store, "session", "actor");
        Assert.Equal(2, document.RootElement.GetProperty("Revision").GetInt64());
    }

    [Fact]
    public async Task StateIsIsolatedBySessionAndActor()
    {
        var store = new InMemoryGameSessionStore();
        foreach (var scope in new[]
        {
            (Session: "owner-a", Actor: "actor-a"),
            (Session: "owner-a", Actor: "actor-b"),
            (Session: "owner-b", Actor: "actor-a"),
        })
        {
            await RunAsync(
                store,
                new TaskPlanExtension((_, _) => new ValueTask<bool>(true)),
                Input("create-" + scope.Session + "-" + scope.Actor, scope.Session, scope.Actor),
                ToolCall("create", "{\"action\":\"create\",\"planId\":\"same-id\",\"objective\":\"scoped\",\"steps\":[\"one\"]}"),
                TextResponse("created"));
        }

        foreach (var scope in new[]
        {
            (Session: "owner-a", Actor: "actor-a"),
            (Session: "owner-a", Actor: "actor-b"),
            (Session: "owner-b", Actor: "actor-a"),
        })
        {
            using var document = await ReadOnlyPlanAsync(store, scope.Session, scope.Actor);
            Assert.Equal("same-id", document.RootElement.GetProperty("Id").GetString());
        }
    }

    [Fact]
    public async Task TerminalRetentionDoesNotConsumeActiveCapacity()
    {
        var store = new InMemoryGameSessionStore();
        var options = new TaskPlanOptions
        {
            MaximumActivePlans = 1,
            MaximumRetainedTerminalPlans = 1,
        };
        for (var index = 1; index <= 3; index++)
        {
            var id = "plan-" + index;
            await RunAsync(
                store,
                new TaskPlanExtension((_, _) => new ValueTask<bool>(true), options),
                Input("create-" + index),
                ToolCall("create", $"{{\"action\":\"create\",\"planId\":\"{id}\",\"objective\":\"work\",\"steps\":[\"one\"]}}"),
                TextResponse("created"));
            await RunAsync(
                store,
                new TaskPlanExtension((_, _) => new ValueTask<bool>(true), options),
                Input("finish-" + index),
                ToolCall("advance", $"{{\"action\":\"advance\",\"planId\":\"{id}\",\"expectedRevision\":1,\"evidence\":{{\"kind\":\"receipt\",\"reference\":\"op-{index}\"}}}}"),
                TextResponse("finished"));
        }

        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.Single(snapshot!.ExtensionState);
        using var retained = JsonDocument.Parse(snapshot.ExtensionState.Single().Value);
        Assert.Equal("plan-3", retained.RootElement.GetProperty("Id").GetString());
        Assert.Equal("Completed", retained.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task ActivePlanIsExposedAsPendingWorkOnLaterInputs()
    {
        var store = new InMemoryGameSessionStore();
        var pending = new ConcurrentQueue<bool>();
        await RunAsync(
            store,
            new TaskPlanExtension((_, _) => new ValueTask<bool>(true)),
            Input("create"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"pending\",\"objective\":\"continue later\",\"steps\":[\"one\",\"two\"]}"),
            TextResponse("created"));

        await using var runtime = new GameAgentBuilder(new ScriptedProvider(new[] { TextResponse("observed") }), "model")
            .UseSessionStore(store)
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(
                "pending-work.observer",
                "1",
                api => api.RegisterRouteRule(
                    "capture",
                    (_, _, hasPendingWork, _) =>
                    {
                        pending.Enqueue(hasPendingWork);
                        return new ValueTask<GameRouteDecision?>(GameRouteDecision.Agent("captured"));
                    },
                    priority: 1_000))
            .Build();

        var result = await runtime.RunAsync(Input("later"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(Assert.Single(pending));
    }

    [Fact]
    public async Task FailAndCancelProduceBoundedTerminalChecklists()
    {
        var store = new InMemoryGameSessionStore();
        var extension = new TaskPlanExtension((_, _) => new ValueTask<bool>(true));
        await RunAsync(
            store,
            extension,
            Input("create-failed"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"failed\",\"objective\":\"work\",\"steps\":[\"one\",\"two\"]}"),
            TextResponse("created"));
        await RunAsync(
            store,
            extension,
            Input("fail"),
            ToolCall("fail", "{\"action\":\"fail\",\"planId\":\"failed\",\"expectedRevision\":1,\"reason\":\"blocked\"}"),
            TextResponse("failed"));
        await RunAsync(
            store,
            extension,
            Input("create-cancelled"),
            ToolCall("create", "{\"action\":\"create\",\"planId\":\"cancelled\",\"objective\":\"work\",\"steps\":[\"one\"]}"),
            TextResponse("created"));
        await RunAsync(
            store,
            extension,
            Input("cancel"),
            ToolCall("cancel", "{\"action\":\"cancel\",\"planId\":\"cancelled\",\"expectedRevision\":1}"),
            TextResponse("cancelled"));

        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        var plans = snapshot!.ExtensionState.Values
            .Select(value => JsonDocument.Parse(value))
            .ToArray();
        try
        {
            Assert.Contains(plans, plan =>
                plan.RootElement.GetProperty("Status").GetString() == "Failed"
                && plan.RootElement.GetProperty("Error").GetString() == "blocked");
            Assert.Contains(plans, plan =>
                plan.RootElement.GetProperty("Status").GetString() == "Cancelled");
            Assert.All(
                plans,
                plan => Assert.DoesNotContain(
                    plan.RootElement.GetProperty("Steps").EnumerateArray(),
                    step => step.GetProperty("Status").GetString() == "InProgress"));
        }
        finally
        {
            foreach (var plan in plans)
            {
                plan.Dispose();
            }
        }
    }

    private static async Task RunAsync(
        IGameSessionStore store,
        TaskPlanExtension extension,
        GameInput input,
        params ModelResponse[] responses)
    {
        await using var runtime = new GameAgentBuilder(new ScriptedProvider(responses), "model")
            .UseSessionStore(store)
            .UseExtension(extension)
            .Build();
        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
    }

    private static GameInput Input(
        string inputId,
        string sessionId = "session",
        string actorId = "actor") =>
        new(sessionId, actorId, "request", "{}", new GameMoment("world", 1), inputId);

    private static async Task<JsonDocument> ReadOnlyPlanAsync(
        IGameSessionStore store,
        string sessionId,
        string actorId)
    {
        var snapshot = await store.LoadAsync(
            new GameSessionKey(sessionId, actorId),
            TestContext.Current.CancellationToken);
        return JsonDocument.Parse(Assert.Single(snapshot!.ExtensionState).Value);
    }

    private static string[] Statuses(JsonElement plan) =>
        plan.GetProperty("Steps")
            .EnumerateArray()
            .Select(step => step.GetProperty("Status").GetString()!)
            .ToArray();

    private static ModelResponse ToolCall(string id, string arguments) =>
        new(new AgentContent[] { new ToolCallContent(id, "manage_task_plan", arguments) }, ModelStopReason.ToolUse);

    private static ModelResponse TextResponse(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Func<int, ModelResponse> _response;
        private int _calls;

        public ScriptedProvider(IReadOnlyList<ModelResponse> responses)
        {
            _response = call => call <= responses.Count ? responses[call - 1] : TextResponse("done");
        }

        public ScriptedProvider(Func<int, ModelResponse> response)
        {
            _response = response;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(_response(Interlocked.Increment(ref _calls)));
            await Task.CompletedTask;
        }
    }

    private sealed class ConcurrentRunGate
    {
        private readonly int _participants;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public ConcurrentRunGate(int participants)
        {
            _participants = participants;
        }

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == _participants)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FirstCallBarrierProvider : IModelProvider
    {
        private readonly ConcurrentRunGate _gate;
        private readonly ModelResponse _first;
        private int _calls;

        public FirstCallBarrierProvider(ConcurrentRunGate gate, ModelResponse first)
        {
            _gate = gate;
            _first = first;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                await _gate.ArriveAsync(cancellationToken);
                yield return ModelStreamEvent.Terminal(_first);
            }
            else
            {
                yield return ModelStreamEvent.Terminal(TextResponse("done"));
            }
        }
    }
}
