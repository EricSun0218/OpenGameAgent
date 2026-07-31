using GameAgent.Core;
using GameAgent.Godot;
using GameAgent.Providers.Anthropic;
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

            using var authoring = new GodotWorldAuthoringBridge();
            var authoredWorldPath = global::Godot.ProjectSettings.GlobalizePath(
                "res://addons/game_agent_runtime/authoring/examples/interactive-smoke");
            var validation = authoring.validate_world_source(
                authoredWorldPath,
                "package-smoke-world",
                "1");
            if (!validation["success"].AsBool())
            {
                throw new InvalidOperationException(
                    "The packaged authoring bridge could not validate its example.");
            }

            var packagePath = global::Godot.ProjectSettings.GlobalizePath(
                $"user://package-smoke-{Guid.NewGuid():N}.gaworld");
            try
            {
                var package = authoring.build_world_package_file(
                    authoredWorldPath,
                    "package-smoke-world",
                    "1",
                    packagePath);
                if (!package["success"].AsBool()
                    || !File.Exists(packagePath)
                    || new FileInfo(packagePath).Length == 0)
                {
                    throw new InvalidOperationException(
                        "The packaged authoring bridge could not build an archive.");
                }
            }
            finally
            {
                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                }
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
