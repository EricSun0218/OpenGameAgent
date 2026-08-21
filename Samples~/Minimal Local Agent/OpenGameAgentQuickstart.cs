#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;
using UnityEngine;

namespace OpenGameAgent.Unity.Samples
{

public sealed class OpenGameAgentQuickstart : MonoBehaviour
{
    private async void Start()
    {
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new DeterministicProvider(),
            "sample-model"));
        var input = new GameInput(
            "sample-session",
            "guide",
            "player.interaction",
            "{\"message\":\"Hello from Unity\",\"nearbyObjects\":[\"campfire\"]}",
            new GameMoment("sample-world", 1),
            "unity-sample-1");

        var result = await runtime.RunAsync(input, destroyCancellationToken);
        Debug.Log($"OpenGameAgent sample completed: {result.Status}");
        await runtime.DisposeAsync();
    }

    private sealed class DeterministicProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("Welcome to the world.") },
                ModelStopReason.Stop,
                new ModelUsage(5, 5)));
        }
    }
}

}
