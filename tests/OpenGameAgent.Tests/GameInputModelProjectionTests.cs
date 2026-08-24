using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class GameInputModelProjectionTests
{
    [Fact]
    public async Task DefaultProjectionPreservesCanonicalCoordinatesForCompatibility()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = QuickChatPolicy(),
        });
        var input = Input();

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var message = Assert.Single(provider.Requests).Messages.Last();
        var json = Assert.IsType<JsonContent>(message.Content[0]).Json;
        Assert.Equal(
            "{\"InputId\":\"input-1\",\"Type\":\"chat\",\"ActorId\":\"canonical-actor\",\"TimelineId\":\"canonical-timeline\",\"Tick\":42,\"Calendar\":{\"day\":7},\"Payload\":{\"question\":\"hello\"}}",
            json);
        using var payload = JsonDocument.Parse(json);
        Assert.Equal("canonical-actor", payload.RootElement.GetProperty("ActorId").GetString());
        Assert.Equal("canonical-timeline", payload.RootElement.GetProperty("TimelineId").GetString());
        Assert.Equal(42, payload.RootElement.GetProperty("Tick").GetInt64());
        Assert.Equal(7, payload.RootElement.GetProperty("Calendar").GetProperty("day").GetInt32());
        Assert.Equal("canonical-actor", message.Metadata["game.actor_id"]);
        Assert.Equal("canonical-timeline", message.Metadata["game.timeline_id"]);
        Assert.Equal("42", message.Metadata["game.tick"]);
    }

    [Fact]
    public async Task SuppressedCoordinatesNeverReachTheModelRequest()
    {
        var provider = new RecordingProvider(_ => Text("ok"));
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = QuickChatPolicy(),
            InputModelProjection = static _ => GameInputModelProjection.SuppressCoordinates,
        });
        var input = new GameInput(
            "canonical-session",
            "canonical-actor",
            "chat",
            "{\"question\":\"hello\"}",
            new GameMoment("canonical-timeline", 42, "{\"day\":7}"),
            "input-1",
            new Dictionary<string, string>
            {
                ["game.actor_id"] = "spoofed-actor",
                ["game.timeline_id"] = "spoofed-timeline",
                ["game.tick"] = "999",
                ["host.visible"] = "yes",
            });

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(provider.Requests);
        var message = request.Messages.Last();
        using var payload = JsonDocument.Parse(Assert.IsType<JsonContent>(message.Content[0]).Json);
        Assert.False(payload.RootElement.TryGetProperty("ActorId", out _));
        Assert.False(payload.RootElement.TryGetProperty("TimelineId", out _));
        Assert.False(payload.RootElement.TryGetProperty("Tick", out _));
        Assert.False(payload.RootElement.TryGetProperty("Calendar", out _));
        Assert.False(message.Metadata.ContainsKey("game.actor_id"));
        Assert.False(message.Metadata.ContainsKey("game.timeline_id"));
        Assert.False(message.Metadata.ContainsKey("game.tick"));
        Assert.Equal("yes", message.Metadata["host.visible"]);
        Assert.Equal("canonical-session", request.SessionId);
        Assert.Equal("hello", payload.RootElement.GetProperty("Payload").GetProperty("question").GetString());
    }

    [Fact]
    public async Task OpaqueProjectionDoesNotChangeCanonicalRuntimeAuthority()
    {
        var provider = new RecordingProvider(call => call == 1
            ? Tools(new ToolCallContent("inspect-1", "inspect", "{}"))
            : Text("done"));
        var store = new InMemoryGameSessionStore();
        GameInput? toolInput = null;
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = store,
            InputModelProjection = static _ => new GameInputModelProjection(
                "opaque-npc-7",
                new GameMoment("opaque-era", 3)),
            ToolProvider = (input, _) =>
            {
                toolInput = input;
                return new ValueTask<IReadOnlyList<AgentTool>>(new[]
                {
                    new AgentTool(
                        new ToolDefinition("inspect", "Inspect authoritative state.", "{\"type\":\"object\"}"),
                        (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                            new AgentContent[] { new TextContent("ok") }))),
                });
            },
        });
        var input = Input();

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Same(input, toolInput);
        var request = provider.Requests.First();
        var message = request.Messages.Last();
        using var payload = JsonDocument.Parse(Assert.IsType<JsonContent>(message.Content[0]).Json);
        Assert.Equal("opaque-npc-7", payload.RootElement.GetProperty("ActorId").GetString());
        Assert.Equal("opaque-era", payload.RootElement.GetProperty("TimelineId").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("Tick").GetInt64());

        var snapshot = await store.LoadAsync(
            new GameSessionKey("canonical-session", "canonical-actor"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal("canonical-actor", snapshot.Key.ActorId);
        Assert.Equal(new GameMoment("canonical-timeline", 42, "{\"day\":7}"), snapshot.LastMoment);
    }

    private static GameInput Input() => new(
        "canonical-session",
        "canonical-actor",
        "chat",
        "{\"question\":\"hello\"}",
        new GameMoment("canonical-timeline", 42, "{\"day\":7}"),
        "input-1");

    private static AutomaticGameRoutePolicy QuickChatPolicy() => new(
        new Dictionary<string, GameRouteDecision>
        {
            ["chat"] = GameRouteDecision.Quick("typed"),
        });

    private static ModelResponse Text(string text) => new(
        new AgentContent[] { new TextContent(text) },
        ModelStopReason.Stop,
        new ModelUsage(1, 1));

    private static ModelResponse Tools(params ToolCallContent[] calls) => new(
        calls,
        ModelStopReason.ToolUse,
        new ModelUsage(1, 1));

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly Func<int, ModelResponse> _handler;
        private int _calls;

        public RecordingProvider(Func<int, ModelResponse> handler)
        {
            _handler = handler;
        }

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(_handler(Interlocked.Increment(ref _calls)));
        }
    }
}
