using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class TaskPlanPersistenceTests
{
    [Fact]
    public async Task ChecklistRevisionAndAdvanceGuardSurviveProcessRestart()
    {
        using var directory = new TemporaryDirectory();
        var evidenceCalls = 0;
        GameTaskPlanEvidenceValidator validator = (request, _) =>
        {
            Interlocked.Increment(ref evidenceCalls);
            return new ValueTask<bool>(request.Reference == "receipt-1");
        };

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("create", "{\"action\":\"create\",\"planId\":\"persistent\",\"objective\":\"persist\",\"steps\":[\"one\",\"two\"]}")
                    : TextResponse("created")),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("create"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call switch
                {
                    1 => ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"persistent\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"receipt-1\"}}"),
                    2 => ToolCall("duplicate", "{\"action\":\"advance\",\"planId\":\"persistent\",\"expectedRevision\":2,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"receipt-1\"}}"),
                    _ => TextResponse("advanced"),
                }),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("advance"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var snapshot = await new FileGameSessionStore(directory.Path).LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(Assert.Single(snapshot!.ExtensionState).Value);
        Assert.Equal(2, document.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(
            new[] { "Completed", "InProgress" },
            document.RootElement.GetProperty("Steps").EnumerateArray()
                .Select(step => step.GetProperty("Status").GetString()).ToArray());
        Assert.Equal("advance", document.RootElement.GetProperty("LastAdvancedInputId").GetString());
        Assert.Equal(1, Volatile.Read(ref evidenceCalls));
    }

    private static GameInput Input(string inputId) =>
        new("session", "actor", "request", "{}", new GameMoment("world", 1), inputId);

    private static ModelResponse ToolCall(string id, string arguments) =>
        new(new AgentContent[] { new ToolCallContent(id, "manage_task_plan", arguments) }, ModelStopReason.ToolUse);

    private static ModelResponse TextResponse(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Func<int, ModelResponse> _response;
        private int _calls;

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGameAgent.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test root.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
