using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class ExtensionRuntimeTests
{
    [Fact]
    public async Task BuilderUsesExtensionProviderPromptAndLifecycleEvents()
    {
        var primary = new CaptureProvider();
        var alternate = new CaptureProvider();
        var events = new ConcurrentQueue<string>();
        await using var runtime = new GameAgentBuilder(primary, "primary-model")
            .UseInstructions("base")
            .UseModelSelector((input, _) => new ValueTask<GameModelSelection?>(new GameModelSelection(
                input.Metadata["agent.model"],
                input.Metadata["agent.provider"])))
            .UseExtension(
                "test.provider",
                "1.0.0",
                api =>
                {
                    api.RegisterModelProvider("alternate", alternate);
                    api.RegisterPromptFragment("guidance", "extension guidance");
                    api.On(GameAgentExtensionEvents.SessionLoaded, (_, _, _) =>
                    {
                        events.Enqueue("loaded");
                        return ValueTask.CompletedTask;
                    });
                    api.On(GameAgentExtensionEvents.RunCompleted, (_, _, _) =>
                    {
                        events.Enqueue("completed");
                        return ValueTask.CompletedTask;
                    });
                })
            .Build();

        var result = await runtime.RunAsync(
            Input(
                "chat",
                "first",
                new Dictionary<string, string> { ["agent.provider"] = "alternate", ["agent.model"] = "fast" }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(primary.Requests);
        var request = Assert.Single(alternate.Requests);
        Assert.Equal("fast", request.Model);
        Assert.Contains("base", request.SystemPrompt);
        Assert.Contains("extension guidance", request.SystemPrompt);
        Assert.Equal(new[] { "loaded", "completed" }, events.ToArray());
    }

    [Fact]
    public void BuilderTransfersOwnershipOnItsFirstConstructionAttempt()
    {
        var builder = new GameAgentBuilder(new CaptureProvider(), "model")
            .Configure(options => options.Model = " ");

        Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Configure(options => options.Model = "model"));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void RuntimeConstructionDisposesConfiguredExtensionsAndPreservesTheOriginalFailure()
    {
        var probe = new ConstructionProbeExtension(throwOnDispose: true);
        var builder = new GameAgentBuilder(new CaptureProvider(), "model")
            .Configure(options => options.Workflows.Add(new ProbeWorkflow("duplicate")))
            .UseExtension(probe);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("Duplicate workflow", error.Message, StringComparison.Ordinal);
        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task InputMetadataCannotOverrideModelWithoutAnExplicitHostSelector()
    {
        var primary = new CaptureProvider();
        var alternate = new CaptureProvider();
        await using var runtime = new GameAgentBuilder(primary, "primary-model")
            .UseExtension("test.provider", "1.0.0", api => api.RegisterModelProvider("alternate", alternate))
            .Build();

        var result = await runtime.RunAsync(
            Input(
                "chat",
                "first",
                new Dictionary<string, string> { ["agent.provider"] = "alternate", ["agent.model"] = "expensive" }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(alternate.Requests);
        Assert.Equal("primary-model", Assert.Single(primary.Requests).Model);
    }

    [Fact]
    public async Task ExtensionStateIsNamespacedPersistedAndNotInjectedAutomatically()
    {
        var provider = new CaptureProvider();
        var store = new InMemoryGameSessionStore();
        var loadedCounts = new ConcurrentQueue<int>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(
                "test.state",
                "1.0.0",
                api =>
                {
                    api.On(GameAgentExtensionEvents.SessionLoaded, (_, context, _) =>
                    {
                        var value = context.State.Get("count");
                        loadedCounts.Enqueue(value is null ? 0 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                        return ValueTask.CompletedTask;
                    });
                    api.On(GameAgentExtensionEvents.SessionSaving, (_, context, _) =>
                    {
                        var current = context.State.Get("count");
                        var count = current is null
                            ? 0
                            : int.Parse(current, System.Globalization.CultureInfo.InvariantCulture);
                        context.State.Set("count", (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        return ValueTask.CompletedTask;
                    });
                })
            .Build();

        await runtime.RunAsync(Input("chat", "one"), TestContext.Current.CancellationToken);
        await runtime.RunAsync(Input("chat", "two"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 0, 1 }, loadedCounts.ToArray());
        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.Equal("2", Assert.Single(snapshot!.ExtensionState).Value);
        Assert.DoesNotContain("test.state", provider.Requests.Last().SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("count", provider.Requests.Last().SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedRunStateIsInvalidatedAfterLifecycleCompletion()
    {
        var provider = new CaptureProvider();
        GameAgentExtensionRunContext? captured = null;
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension("capture", "1", api => api.On(
                GameAgentExtensionEvents.SessionLoaded,
                (_, context, _) =>
                {
                    captured = context;
                    context.State.Set("during", "true");
                    return ValueTask.CompletedTask;
                }))
            .Build();

        var result = await runtime.RunAsync(Input("chat", "lease"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(captured);
        Assert.False(captured.IsActive);
        Assert.Throws<ObjectDisposedException>(() => captured.State.Set("late", "true"));
        Assert.Throws<ObjectDisposedException>(() => captured.State.Get("during"));
    }

    [Fact]
    public async Task RuntimeShutdownWaitsForActorLanesBeforeDisposingExtensions()
    {
        var provider = new BlockingProvider();
        var extension = new ShutdownProbeExtension(() => provider.Stopped);
        var options = new GameAgentRuntimeOptions(provider, "model");
        options.Extensions.Add(extension);
        var runtime = new GameAgentRuntime(options);
        var active = runtime.RunAsync(Input("chat", "active"), TestContext.Current.CancellationToken);
        await provider.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queued = runtime.RunAsync(Input("chat", "queued"), TestContext.Current.CancellationToken);

        await runtime.DisposeAsync();

        Assert.True(provider.Stopped);
        Assert.True(extension.Disposed);
        Assert.True(extension.ProviderWasStoppedAtDispose);
        Assert.Equal(1, provider.CallCount);
        var settled = await active;
        Assert.Equal(AgentRunStatus.Aborted, settled.AgentResult?.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
    }

    [Fact]
    public async Task ExtensionChannelSettlementIsSafeDuringShutdown()
    {
        var extension = new ShutdownPublishingExtension();
        var runtime = new GameAgentBuilder(new CaptureProvider(), "model")
            .UseExtension(extension)
            .Build();

        await runtime.DisposeAsync();

        Assert.True(extension.Published);
    }

    [Fact]
    public void SynchronousRuntimeDisposeDoesNotDeadlockOnAnEngineSynchronizationContext()
    {
        var extension = new YieldingDisposeExtension();
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                using var runtime = new GameAgentBuilder(new CaptureProvider(), "model")
                    .UseExtension(extension)
                    .Build();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "Synchronous disposal deadlocked.");
        Assert.Null(failure);
        Assert.True(extension.Disposed);
    }

    [Fact]
    public void DuplicateResourcesFailWithAnAttributedConflict()
    {
        var provider = new CaptureProvider();
        var builder = new GameAgentBuilder(provider, "model")
            .UseExtension("first", "1", api => api.RegisterPromptFragment("same", "one"))
            .UseExtension("second", "1", api => api.RegisterPromptFragment("same", "two"));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("first", exception.Message);
        Assert.Contains("same", exception.Message);
    }

    [Fact]
    public async Task DisposedDynamicRegistrationIsAbsentFromFutureRuns()
    {
        var provider = new CaptureProvider();
        var extension = new DynamicToolExtension();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(extension)
            .Build();
        Assert.Contains(runtime.ExtensionResources, resource => resource.Name == "temporary");

        extension.Registration!.Dispose();
        await runtime.RunAsync(Input("chat", "run"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(runtime.ExtensionResources, resource => resource.Name == "temporary");
        Assert.Empty(Assert.Single(provider.Requests).Tools);
    }

    [Fact]
    public async Task ToolVisibilityPoliciesFilterCollectedDefinitionsBeforeEachModelRequest()
    {
        var provider = new CaptureProvider();
        var observed = new ConcurrentQueue<(string InputId, string ToolName, string SourceId, ToolRisk Risk)>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.tools",
                "1",
                api =>
                {
                    api.RegisterTool(new AgentTool(
                        new ToolDefinition("inspect", "Inspect state.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                        (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                            new AgentContent[] { new TextContent("inspected") }))));
                    api.RegisterTool(new AgentTool(
                        new ToolDefinition("change", "Change state.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                        (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                            new AgentContent[] { new TextContent("changed") })),
                        ToolRisk.IdempotentWrite));
                })
            .UseExtension(
                "host.tool-settings",
                "1",
                api => api.RegisterToolVisibilityPolicy(
                    "input-tool-settings",
                    (context, _) =>
                    {
                        observed.Enqueue((
                            context.Input.InputId,
                            context.Tool.Name,
                            context.ToolSourceId,
                            context.Risk));
                        using var document = JsonDocument.Parse(context.Input.PayloadJson);
                        var visible = !document.RootElement.TryGetProperty("disabledTools", out var disabled)
                                      || !disabled.EnumerateArray().Any(
                                          value => string.Equals(value.GetString(), context.Tool.Name, StringComparison.Ordinal));
                        return new ValueTask<bool>(visible);
                    }))
            .Build();

        await runtime.RunAsync(Input("chat", "all-tools"), TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput(
                "session",
                "actor",
                "chat",
                "{\"disabledTools\":[\"change\"]}",
                new GameMoment("world", 2),
                "write-hidden"),
            TestContext.Current.CancellationToken);

        var requests = provider.Requests.ToArray();
        Assert.Equal(
            new[] { "change", "inspect" },
            requests[0].Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("inspect", Assert.Single(requests[1].Tools).Name);
        Assert.Contains(observed, value =>
            value.InputId == "write-hidden"
            && value.ToolName == "change"
            && value.SourceId == "game.tools"
            && value.Risk == ToolRisk.IdempotentWrite);
    }

    [Fact]
    public async Task ToolVisibilityPolicyFailuresStopBeforeProviderDispatch()
    {
        var provider = new CaptureProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.tools",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition("inspect", "Inspect state.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent("inspected") })))))
            .UseExtension(
                "broken.tool-settings",
                "1",
                api => api.RegisterToolVisibilityPolicy(
                    "broken",
                    (_, _) => throw new InvalidOperationException("settings unavailable")))
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(Input("chat", "visibility-failure"), TestContext.Current.CancellationToken));

        Assert.Empty(provider.Requests);
        Assert.Contains("settings unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HigherPriorityRouteRuleWinsDeterministically()
    {
        var provider = new CaptureProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "routes",
                "1",
                api =>
                {
                    api.RegisterRouteRule(
                        "low",
                        (_, _, _, _) => new ValueTask<GameRouteDecision?>(GameRouteDecision.Agent("low")),
                        priority: 0);
                    api.RegisterRouteRule(
                        "high",
                        (_, _, _, _) => new ValueTask<GameRouteDecision?>(GameRouteDecision.Quick("high")),
                        priority: 10);
                })
            .Build();

        var result = await runtime.RunAsync(Input("event", "route"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal("high", result.Route.Reason);
    }

    [Fact]
    public async Task EventHandlerFailureIsIsolatedAndDiagnosed()
    {
        var provider = new CaptureProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "broken.event",
                "1",
                api => api.On<GameAgentSessionEvent>(
                    GameAgentExtensionEvents.SessionLoaded,
                    (_, _, _) => throw new InvalidOperationException("broken")))
            .Build();

        var result = await runtime.RunAsync(Input("chat", "safe"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var diagnostic = Assert.Single(runtime.ExtensionDiagnostics);
        Assert.Equal("extension.event_handler_failed", diagnostic.Code);
        Assert.Equal("broken.event", diagnostic.ExtensionId);
    }

    [Fact]
    public async Task ExtensionDiagnosticsAreBoundedAndMessagesAreTruncated()
    {
        var provider = new CaptureProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .Configure(options =>
            {
                options.Limits.MaxExtensionDiagnostics = 2;
                options.Limits.MaxExtensionDiagnosticCharacters = 16;
            })
            .UseExtension(
                "broken.event",
                "1",
                api => api.On<GameAgentSessionEvent>(
                    GameAgentExtensionEvents.SessionLoaded,
                    (_, _, _) => throw new InvalidOperationException(new string('x', 1_000))))
            .Build();

        await runtime.RunAsync(Input("chat", "one"), TestContext.Current.CancellationToken);
        await runtime.RunAsync(Input("chat", "two"), TestContext.Current.CancellationToken);
        await runtime.RunAsync(Input("chat", "three"), TestContext.Current.CancellationToken);

        Assert.Equal(2, runtime.ExtensionDiagnostics.Count);
        Assert.All(runtime.ExtensionDiagnostics, diagnostic => Assert.Equal(16, diagnostic.Message.Length));
    }

    [Fact]
    public void ExtensionAndResourceRegistrationLimitsFailClosed()
    {
        var noExtensions = new GameAgentBuilder(new CaptureProvider(), "model")
            .Configure(options => options.Limits.MaxExtensions = 0)
            .UseExtension("one", "1", _ => { });
        Assert.Throws<GameRuntimeLimitException>(() => noExtensions.Build());

        var oneResource = new GameAgentBuilder(new CaptureProvider(), "model")
            .Configure(options => options.Limits.MaxExtensionResources = 1)
            .UseExtension("one", "1", api =>
            {
                api.RegisterPromptFragment("first", "one");
                api.RegisterPromptFragment("second", "two");
            });
        Assert.Throws<GameRuntimeLimitException>(() => oneResource.Build());
    }

    [Fact]
    public async Task BeforeToolHooksComposeRevalidatedArgumentsAndCannotBypassLaterPolicy()
    {
        var provider = new ToolCallingProvider();
        var executions = 0;
        var policySawValue = 0;
        await using var runtime = new GameAgentBuilder(provider, "model")
            .Configure(options => options.ToolProvider = (_, _) =>
                new ValueTask<IReadOnlyList<AgentTool>>(new[]
                {
                    new AgentTool(
                        new ToolDefinition(
                            "change",
                            "Change a value.",
                            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}"),
                        (_, _, _) =>
                        {
                            executions++;
                            return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("changed") }));
                        },
                        ToolRisk.IdempotentWrite),
                }))
            .UseExtension("rewrite", "1", api => api.RegisterAgentHooks(
                "rewrite",
                _ => new AgentHooks
                {
                    BeforeToolCallAsync = (_, _) => new ValueTask<ToolCallDecision?>(
                        ToolCallDecision.Allow("{\"value\":2}")),
                },
                priority: 10))
            .UseExtension("policy", "1", api => api.RegisterAgentHooks(
                "policy",
                _ => new AgentHooks
                {
                    BeforeToolCallAsync = (context, _) =>
                    {
                        policySawValue = context.Arguments.GetProperty("value").GetInt32();
                        return new ValueTask<ToolCallDecision?>(ToolCallDecision.Block("denied"));
                    },
                }))
            .Build();

        var result = await runtime.RunAsync(Input("chat", "hooks"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, policySawValue);
        Assert.Equal(0, executions);
        Assert.Contains(result.AgentResult!.NewMessages, message =>
            message.Role == AgentRole.Tool
            && message.IsError
            && Assert.IsType<TextContent>(Assert.Single(message.Content)).Text == "denied");
    }

    private static GameInput Input(
        string type,
        string inputId,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            "session",
            "actor",
            type,
            "{}",
            new GameMoment("world", 1),
            inputId,
            metadata);

    private sealed class DynamicToolExtension : IGameAgentExtension
    {
        public GameAgentExtensionDescriptor Descriptor { get; } = new("dynamic", "1");

        public IGameAgentExtensionRegistration? Registration { get; private set; }

        public void Configure(GameAgentExtensionApi api)
        {
            Registration = api.RegisterTool(new AgentTool(
                new ToolDefinition("temporary", "Temporary test tool.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                (_, _, _) => new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("ok") }))));
        }
    }

    private sealed class ProbeWorkflow : IGameWorkflow
    {
        public ProbeWorkflow(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public ValueTask<GameWorkflowResult> RunAsync(
            GameWorkflowContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameWorkflowResult>(new GameWorkflowResult(
                new[]
                {
                    new AgentMessage(
                        AgentRole.Assistant,
                        new AgentContent[] { new TextContent("ok") },
                        DateTimeOffset.UnixEpoch,
                        model: "workflow",
                        stopReason: ModelStopReason.Stop),
                },
                succeeded: true));
        }
    }

    private sealed class ConstructionProbeExtension : IGameAgentExtension, IDisposable
    {
        private readonly bool _throwOnDispose;

        public ConstructionProbeExtension(bool throwOnDispose)
        {
            _throwOnDispose = throwOnDispose;
        }

        public GameAgentExtensionDescriptor Descriptor { get; } = new("construction-probe", "1");

        public bool Disposed { get; private set; }

        public void Configure(GameAgentExtensionApi api) =>
            api.RegisterWorkflow(new ProbeWorkflow("duplicate"));

        public void Dispose()
        {
            Disposed = true;
            if (_throwOnDispose)
            {
                throw new InvalidOperationException("cleanup failed");
            }
        }
    }

    private sealed class CaptureProvider : IModelProvider
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("ok") },
                ModelStopReason.Stop));
            await Task.CompletedTask;
        }
    }

    private sealed class ToolCallingProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            var response = Interlocked.Increment(ref _calls) == 1
                ? new ModelResponse(
                    new AgentContent[] { new ToolCallContent("change-1", "change", "{\"value\":1}") },
                    ModelStopReason.ToolUse)
                : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop);
            yield return ModelStreamEvent.Terminal(response);
            await Task.CompletedTask;
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        private int _calls;
        private int _stopped;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _calls);

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Interlocked.Exchange(ref _stopped, 1);
            }

            yield break;
        }
    }

    private sealed class ShutdownProbeExtension : IGameAgentExtension, IAsyncDisposable
    {
        private readonly Func<bool> _providerStopped;

        public ShutdownProbeExtension(Func<bool> providerStopped)
        {
            _providerStopped = providerStopped;
        }

        public GameAgentExtensionDescriptor Descriptor { get; } = new("shutdown-probe", "1");

        public bool Disposed { get; private set; }

        public bool ProviderWasStoppedAtDispose { get; private set; }

        public void Configure(GameAgentExtensionApi api)
        {
            _ = api;
        }

        public ValueTask DisposeAsync()
        {
            ProviderWasStoppedAtDispose = _providerStopped();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class YieldingDisposeExtension : IGameAgentExtension, IAsyncDisposable
    {
        public GameAgentExtensionDescriptor Descriptor { get; } = new("yielding-dispose", "1");

        public bool Disposed { get; private set; }

        public void Configure(GameAgentExtensionApi api)
        {
            _ = api;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
        }
    }

    private sealed class ShutdownPublishingExtension : IGameAgentExtension, IAsyncDisposable
    {
        private static readonly GameAgentExtensionChannel<string> Settlement = new("shutdown-settlement");
        private GameAgentExtensionApi? _api;

        public GameAgentExtensionDescriptor Descriptor { get; } = new("shutdown-publishing", "1");

        public bool Published { get; private set; }

        public void Configure(GameAgentExtensionApi api)
        {
            _api = api;
        }

        public async ValueTask DisposeAsync()
        {
            await _api!.PublishAsync(Settlement, "settled");
            Published = true;
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _ = callback;
            _ = state;
        }
    }
}
