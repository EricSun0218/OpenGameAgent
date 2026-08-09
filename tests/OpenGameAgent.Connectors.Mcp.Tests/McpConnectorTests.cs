using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Connectors.Mcp.Tests;

public sealed class McpConnectorTests
{
    [Fact]
    public async Task FailureIsolationKeepsOtherServersAvailable()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create((string value) => $"echo:{value}", new() { Name = "echo" }),
                ],
            });
        var serverTask = server.RunAsync(TestContext.Current.CancellationToken);
        var unavailable = new GameMcpServer(
            "unavailable",
            _ => throw new InvalidOperationException("server unavailable"));
        var available = new GameMcpServer(
            "available",
            async cancellationToken => await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken));
        var provider = new ScriptedProvider(_ =>
            new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new McpToolConnectorExtension(
                new[] { unavailable, available },
                continueOnServerFailure: true,
                exposure: GameMcpToolExposure.Direct))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains(Assert.Single(provider.Requests).Tools, tool => tool.Name == "available__echo");
        await runtime.DisposeAsync();
        await server.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task DiscoversAndCallsAStandardExternalTool()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create((string value) => $"echo:{value}", new() { Name = "echo" }),
                ],
            });
        var serverTask = server.RunAsync(TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider(call => call == 1
            ? new ModelResponse(
                new AgentContent[] { new ToolCallContent("external", "test__echo", "{\"value\":\"hello\"}") },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
        var connection = new GameMcpServer(
            "test",
            async cancellationToken => await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new McpToolConnectorExtension(
                new[] { connection },
                exposure: GameMcpToolExposure.Direct))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains(provider.Requests.First().Tools, tool => tool.Name == "test__echo");
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(toolMessage.Content)).Json;
        Assert.Contains("echo:hello", json);
        await runtime.DisposeAsync();
        await server.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task DefaultExposureConnectsLazilyAndDiscoversBeforeCalling()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create((string value) => $"echo:{value}", new() { Name = "echo" }),
                ],
            });
        var serverTask = server.RunAsync(TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider((call, request) =>
        {
            if (call == 1)
            {
                return new ModelResponse(
                    new AgentContent[]
                    {
                        new ToolCallContent(
                            "search",
                            "external_tools",
                            "{\"action\":\"search\",\"query\":\"echo\"}"),
                    },
                    ModelStopReason.ToolUse);
            }

            if (call == 2)
            {
                var search = Assert.IsType<JsonContent>(Assert.Single(
                    request.Messages.Last(message => message.Role == AgentRole.Tool).Content));
                using var document = System.Text.Json.JsonDocument.Parse(search.Json);
                var path = Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray())
                    .GetProperty("path")
                    .GetString();
                return new ModelResponse(
                    new AgentContent[]
                    {
                        new ToolCallContent(
                            "call",
                            "external_tools",
                            System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "call",
                                path,
                                arguments = new { value = "hello" },
                            })),
                    },
                    ModelStopReason.ToolUse);
            }

            return new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop);
        });
        var connectCount = 0;
        var connection = new GameMcpServer(
            "test",
            async cancellationToken =>
            {
                Interlocked.Increment(ref connectCount);
                return await McpClient.CreateAsync(
                    new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                    cancellationToken: cancellationToken);
            });
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new McpToolConnectorExtension(new[] { connection }))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, connectCount);
        Assert.Equal(new[] { "external_tools" }, provider.Requests.First().Tools.Select(tool => tool.Name));
        var toolMessage = provider.Requests.ElementAt(2).Messages.Last(message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(toolMessage.Content)).Json;
        Assert.Contains("echo:hello", json);
        await runtime.DisposeAsync();
        await server.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task OnDemandCallRejectsInvalidRemoteArgumentsBeforeExecution()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var executions = 0;
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create(
                        (string value) =>
                        {
                            Interlocked.Increment(ref executions);
                            return $"echo:{value}";
                        },
                        new() { Name = "echo" }),
                ],
            });
        var serverTask = server.RunAsync(TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider((call, _) => call == 1
            ? new ModelResponse(
                new AgentContent[]
                {
                    new ToolCallContent(
                        "invalid",
                        "external_tools",
                        "{\"action\":\"call\",\"path\":\"test__echo\",\"arguments\":{}}"),
                },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
        var connection = new GameMcpServer(
            "test",
            async cancellationToken => await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new McpToolConnectorExtension(new[] { connection }))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, executions);
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        var error = Assert.IsType<TextContent>(Assert.Single(toolMessage.Content)).Text;
        Assert.Contains("Invalid external tool arguments", error);
        await runtime.DisposeAsync();
        await server.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task LargeResultsUseBoundedArtifactIdsForLongGameIdentities()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection =
                [
                    McpServerTool.Create(() => new string('x', 2_048), new() { Name = "large" }),
                ],
            });
        var serverTask = server.RunAsync(TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider(call => call == 1
            ? new ModelResponse(
                new AgentContent[] { new ToolCallContent("external", "test__large", "{}") },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
        var connection = new GameMcpServer(
            "test",
            async cancellationToken => await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken));
        var artifacts = new InMemoryGameAgentArtifactStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new McpToolConnectorExtension(
                new[] { connection },
                maximumInlineResultCharacters: 1_024,
                artifactStore: artifacts,
                exposure: GameMcpToolExposure.Direct))
            .Build();
        var sessionId = new string('s', 400);
        var actorId = new string('a', 400);

        var result = await runtime.RunAsync(
            new GameInput(sessionId, actorId, "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        var reference = Assert.IsType<JsonContent>(Assert.Single(toolMessage.Content));
        using var document = System.Text.Json.JsonDocument.Parse(reference.Json);
        var artifactId = Assert.IsType<string>(document.RootElement.GetProperty("artifactId").GetString());
        Assert.StartsWith("mcp-", artifactId, StringComparison.Ordinal);
        Assert.True(artifactId.Length <= 512);
        var artifact = await artifacts.GetAsync(
            sessionId,
            actorId,
            artifactId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(artifact);
        Assert.True(artifact.Content.Length > 1_024);
        await runtime.DisposeAsync();
        await server.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public void StdioRejectsEmbeddedNullCharactersBeforeStartingAProcess()
    {
        Assert.Throws<ArgumentException>(() => GameMcpServer.Stdio("test", "tool\0name"));
        Assert.Throws<ArgumentException>(() => GameMcpServer.Stdio("test", "tool", new[] { "value\0suffix" }));
        Assert.Throws<ArgumentException>(() => GameMcpServer.Stdio("test", "tool", workingDirectory: "path\0suffix"));
    }

    [Fact]
    public async Task ThrowingConnectorCancellationCallbacksCannotBlockCleanup()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new GameMcpServer(
            "test",
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => throw new InvalidOperationException("callback failed"));
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            });
        var extension = new McpToolConnectorExtension(
            new[] { connection },
            exposure: GameMcpToolExposure.Direct);
        await using var runtime = new GameAgentBuilder(new ScriptedProvider(_ =>
                new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop)), "model")
            .UseExtension(extension)
            .Build();
        var run = runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = await Record.ExceptionAsync(async () => await extension.DisposeAsync());

        Assert.Null(exception);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Func<int, ModelRequest, ModelResponse> _response;
        private int _calls;

        public ScriptedProvider(Func<int, ModelResponse> response)
        {
            ArgumentNullException.ThrowIfNull(response);
            _response = (call, _) => response(call);
        }

        public ScriptedProvider(Func<int, ModelRequest, ModelResponse> response)
        {
            _response = response ?? throw new ArgumentNullException(nameof(response));
        }

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            yield return ModelStreamEvent.Terminal(_response(Interlocked.Increment(ref _calls), request));
            await Task.CompletedTask;
        }
    }
}
