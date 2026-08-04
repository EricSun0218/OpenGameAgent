using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Testing;

public sealed class FakeRuntimeClock : IRuntimeClock
{
    public FakeRuntimeClock(DateTimeOffset? initialValue = null)
    {
        UtcNow = initialValue ?? new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
    }
}

public sealed class SequentialIdGenerator : IRuntimeIdGenerator
{
    private readonly Dictionary<string, int> _sequences =
        new(StringComparer.Ordinal);

    public string NewId(string category)
    {
        lock (_sequences)
        {
            _sequences.TryGetValue(category, out var value);
            value++;
            _sequences[category] = value;
            return $"{category}-{value:D4}";
        }
    }
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly List<RuntimeEvent> _events = new();

    public IReadOnlyList<RuntimeEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    public ValueTask AppendAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_events)
        {
            _events.Add(runtimeEvent);
        }

        return default;
    }

    public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_events)
        {
            IReadOnlyList<RuntimeEvent> result = _events
                .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                .ToArray();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(result);
        }
    }
}

public sealed class ScriptedModelProvider : IModelProvider
{
    private readonly Queue<ModelResponse> _responses;
    private readonly List<ModelRequest> _requests = new();

    public ScriptedModelProvider(params ModelResponse[] responses)
    {
        _responses = new Queue<ModelResponse>(responses);
    }

    public int CallCount => _requests.Count;

    public IReadOnlyList<ModelRequest> Requests => _requests;

    public ValueTask<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The scripted provider has no response remaining.");
        }

        return new ValueTask<ModelResponse>(_responses.Dequeue());
    }
}

public sealed class FakeGameHost : IGameHost
{
    private readonly Func<ActionRequest, CancellationToken, ValueTask<ActionReceipt>> _handler;
    private readonly List<ActionRequest> _requests = new();

    public FakeGameHost(
        Func<ActionRequest, CancellationToken, ValueTask<ActionReceipt>> handler)
    {
        _handler = handler;
    }

    public int CallCount => _requests.Count;

    public IReadOnlyList<ActionRequest> Requests => _requests;

    public async ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }

    public static FakeGameHost Returning(
        Func<ActionRequest, ActionReceipt> handler)
    {
        return new FakeGameHost(
            (request, _) => new ValueTask<ActionReceipt>(handler(request)));
    }
}
