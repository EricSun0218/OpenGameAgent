using System;
using System.IO;
using GameAgent.Core;
using GameAgent.Godot;
using GameAgent.Providers.Anthropic;
using GameAgent.Providers.Native;
using GameAgent.Providers.OpenAICompatible;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.Workflow;

namespace PackageConsumer;

public partial class Smoke : global::Godot.Node
{
    public override void _Ready()
    {
        try
        {
            var runtime = GetNode<GameAgentRuntimeNode>(
                "/root/GameAgentRuntime");
            _ = typeof(AnthropicMessagesStreamingProvider);
            _ = typeof(OpenAiResponsesStreamingProvider);
            _ = typeof(GeminiInteractionsStreamingProvider);
            _ = typeof(WorkflowCompiler);
            var host = new GodotMainThreadGameHost(
                runtime.Dispatcher,
                new SystemRuntimeClock());
            var journalPath = Path.Combine(
                global::Godot.ProjectSettings.GlobalizePath("res://"),
                $"package-smoke-{Guid.NewGuid():N}.journal");
            var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(journalPath)
                .UseOpenAiCompatibleProvider(
                    new OpenAiCompatibleProviderOptions
                    {
                        ProviderId = "package-smoke-provider",
                        BaseUri = new Uri("https://example.invalid"),
                        Model = "package-smoke-model",
                        ThinkingMode = null,
                        ReasoningEffort = null
                    },
                    new StaticBearerTokenSource(
                        "package-smoke-not-a-real-secret"))
                .WithTools(Array.Empty<ToolDescriptor>())
                .PublishEventsTo(runtime.Typed.EventPublisher)
                .Build();
            runtime.Typed.ConfigureDurable(built);

            var status = runtime.get_runtime_status();
            if (status["backend"].AsString() != "durable")
            {
                throw new InvalidOperationException(
                    "The packaged composition builder did not configure the durable backend.");
            }

            global::Godot.GD.Print("PACKAGED_CONSUMER_PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            global::Godot.GD.PushError(
                $"PACKAGED_CONSUMER_FAIL {exception}");
            GetTree().Quit(1);
        }
    }
}
