using System.Text.Json;
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

public sealed class GameActionProgress
{
    public string Stage { get; set; } = string.Empty;

    public string? Message { get; set; }

    public long? Current { get; set; }

    public long? Total { get; set; }

    public JsonElement? Data { get; set; }

    internal GameActionProgress CloneValidated()
    {
        var stage = RuntimeGuard.RequiredUtf8(
            Stage,
            128,
            nameof(Stage));
        string? message = null;
        if (Message is not null)
        {
            message = RuntimeGuard.RequiredUtf8(
                Message,
                4_096,
                nameof(Message));
        }

        if (Current < 0 || Total < 0
            || Current.HasValue
               && Total.HasValue
               && Current.Value > Total.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Current),
                "Action progress counters are invalid.");
        }

        JsonElement? data = null;
        if (Data.HasValue)
        {
            _ = JsonValueInspector.ValidateAndMeasure(
                Data.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 65_536,
                    maxDepth: 16,
                    maxNodes: 4_096,
                    maxStringUtf8Bytes: 16_384,
                    maxContainerItems: 1_024),
                nameof(Data));
            data = Data.Value.Clone();
        }

        return new GameActionProgress
        {
            Stage = stage,
            Message = message,
            Current = Current,
            Total = Total,
            Data = data
        };
    }
}

public interface IGameActionProgressSink
{
    /// <summary>
    /// Reports bounded, non-authoritative presentation progress. Reports may
    /// be dropped and never replace the final ActionReceipt.
    /// </summary>
    void Report(GameActionProgress progress);
}

public interface IProgressReportingGameHost : IGameHost
{
    ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        IGameActionProgressSink progress,
        CancellationToken cancellationToken);
}
