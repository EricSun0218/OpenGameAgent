using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Kernel.Tests;

internal sealed class ScriptedProvider : IModelProvider
{
    private readonly Func<int, ModelRequest, CancellationToken, Task<ModelResponse>> _handler;
    private int _calls;

    public ScriptedProvider(Func<int, ModelRequest, CancellationToken, Task<ModelResponse>> handler)
    {
        _handler = handler;
    }

    public ConcurrentQueue<ModelRequest> Requests { get; } = new();

    public int CallCount => Volatile.Read(ref _calls);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var call = Interlocked.Increment(ref _calls);
        var response = await _handler(call, request, cancellationToken);
        yield return ModelStreamEvent.Terminal(response);
    }

    public static ScriptedProvider FromResponses(params ModelResponse[] responses)
    {
        return new ScriptedProvider((call, _, _) =>
        {
            if (call > responses.Length)
            {
                throw new InvalidOperationException("No scripted response is available.");
            }

            return Task.FromResult(responses[call - 1]);
        });
    }
}

internal sealed class StreamingProvider : IModelProvider
{
    private readonly Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> _stream;

    public StreamingProvider(Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> stream)
    {
        _stream = stream;
    }

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) =>
        _stream(request, cancellationToken);
}

internal sealed class TerminalThenDisposalFailureProvider : IModelProvider
{
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) =>
        new Stream();

    private sealed class Stream : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        private bool _emitted;

        public ModelStreamEvent Current { get; private set; } = null!;

        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_emitted)
            {
                return new ValueTask<bool>(false);
            }

            _emitted = true;
            Current = ModelStreamEvent.Terminal(Responses.Text("done"));
            return new ValueTask<bool>(true);
        }

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failed"));
    }
}

internal sealed class NonCooperativeProvider : IModelProvider
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        Started.TrySetResult();
        await Release.Task.ConfigureAwait(false);
        yield return ModelStreamEvent.Terminal(Responses.Text("late"));
    }
}

internal static class Responses
{
    public static ModelResponse Text(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    public static ModelResponse Tools(ModelStopReason reason, params ToolCallContent[] calls) =>
        new(calls, reason);

    public static AgentTool Tool(
        string name,
        Func<System.Text.Json.JsonElement, ToolExecutionContext, CancellationToken, ValueTask<ToolResult>> execute,
        ToolRisk risk = ToolRisk.ReadOnly,
        ToolExecutionMode? mode = null,
        string schema = "{\"type\":\"object\",\"additionalProperties\":false}",
        bool trackExactRepeats = true) =>
        new(
            new ToolDefinition(name, "Test tool " + name, schema),
            execute,
            risk,
            mode,
            trackExactRepeats: trackExactRepeats);

    public static ToolResult Result(string text, bool terminate = false) =>
        new(new AgentContent[] { new TextContent(text) }, terminate: terminate);
}
