using GameAgent.Core;

namespace GameAgent.Godot;

internal sealed class GodotMultiActorLifecycle :
    IMultiActorDecisionLifecycle
{
    private readonly GameAgentRuntimeNode _node;

    internal GodotMultiActorLifecycle(GameAgentRuntimeNode node)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
    }

    public ValueTask BatchStartedAsync(
        MultiActorBatchManifest manifest,
        CancellationToken cancellationToken) =>
        _node.PublishBatchStartedLifecycleAsync(
            manifest,
            cancellationToken);

    public ValueTask ActorFinishedAsync(
        string batchId,
        MultiActorRunResult result,
        CancellationToken cancellationToken) =>
        _node.PublishActorFinishedLifecycleAsync(
            batchId,
            result,
            cancellationToken);

    public ValueTask BatchAbortedAsync(
        string batchId,
        string reasonCode,
        CancellationToken cancellationToken) =>
        _node.PublishBatchAbortedLifecycleAsync(
            batchId,
            reasonCode,
            cancellationToken);
}
