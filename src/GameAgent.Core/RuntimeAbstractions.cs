using GameAgent.Protocol;

namespace GameAgent.Core;

public interface IRuntimeClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IRuntimeIdGenerator
{
    string NewId(string category);
}

public interface ISessionStore
{
    ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
        string runId,
        CancellationToken cancellationToken);
}

public interface IModelProvider
{
    ValueTask<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}

public interface IGameHost
{
    ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken);
}
