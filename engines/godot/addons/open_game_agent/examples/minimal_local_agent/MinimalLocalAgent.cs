#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Godot.Samples;

public partial class MinimalLocalAgent : Node
{
    public override async void _Ready()
    {
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new DeterministicProvider(),
            "sample-model"));
        var input = new GameInput(
            "sample-session",
            "guide",
            "player.interaction",
            "{\"message\":\"Hello from Godot\",\"nearbyObjects\":[\"campfire\"]}",
            new GameMoment("sample-world", 1),
            "godot-sample-1");

        var result = await runtime.RunAsync(input);
        GD.Print($"OpenGameAgent sample completed: {result.Status}");
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
