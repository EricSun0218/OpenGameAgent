using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class ToolApprovalExtensionTests
{
    [Fact]
    public async Task ConfirmOnceBlocksExecutorUntilOneTimeWorldBoundApprovalIsConsumed()
    {
        var provider = ToolProvider("{\"b\":2,\"a\":1}");
        var store = new InMemoryGameToolApprovalStore();
        var broker = new GameToolApprovalBroker(store);
        var world = new MutableWorldStateProvider();
        var executed = 0;
        await using var runtime = Runtime(provider, broker, world, () => Interlocked.Increment(ref executed));

        var run = runtime.RunAsync(Input("confirm"), TestContext.Current.CancellationToken);
        var pending = await WaitForPendingAsync(broker);
        Assert.Equal(0, Volatile.Read(ref executed));
        Assert.Equal("{\"a\":1,\"b\":2}", pending.Request.CanonicalArgumentsJson);

        await broker.RespondAsync(
            new GameToolApprovalResponse(pending.Request.Owner, pending.Request.ApprovalId, pending.Revision, GameToolApprovalResponseKind.Approve),
            TestContext.Current.CancellationToken);
        var result = await run;

        Assert.True(result.Succeeded);
        Assert.Equal(1, Volatile.Read(ref executed));
        var consumed = await store.ReadAsync(pending.Request.Owner, pending.Request.ApprovalId, TestContext.Current.CancellationToken);
        Assert.Equal(GameToolApprovalStatus.Consumed, consumed!.Status);
        Assert.Null(consumed.CredentialDigest);
    }

    [Fact]
    public async Task ApprovalBindsPostPolicyArgumentsAndWorldRevisionChangeFailsClosed()
    {
        var provider = ToolProvider("{\"a\":1,\"b\":2}");
        var store = new InMemoryGameToolApprovalStore();
        var broker = new GameToolApprovalBroker(store);
        var world = new MutableWorldStateProvider();
        var executed = 0;
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension("tools", "1", api => api.RegisterTool(Tool(() => Interlocked.Increment(ref executed))))
            .UseExtension(new ToolPolicyExtension(new[] { new RewritePolicy() }))
            .UseExtension(new ToolApprovalExtension(
                new[] { new GameToolApprovalRule("confirm-write", GameToolApprovalMode.ConfirmOnce, "write") },
                broker,
                world))
            .Build();

        var run = runtime.RunAsync(Input("world-change"), TestContext.Current.CancellationToken);
        var pending = await WaitForPendingAsync(broker);
        Assert.Equal("{\"a\":9,\"b\":2}", pending.Request.CanonicalArgumentsJson);
        world.Revision = 2;
        await broker.RespondAsync(
            new GameToolApprovalResponse(pending.Request.Owner, pending.Request.ApprovalId, pending.Revision, GameToolApprovalResponseKind.Approve),
            TestContext.Current.CancellationToken);

        var result = await run;
        Assert.True(result.Succeeded);
        Assert.Equal(0, Volatile.Read(ref executed));
        Assert.Contains(result.AgentResult!.NewMessages, message =>
            message.Role == AgentRole.Tool
            && message.IsError
            && Assert.IsType<TextContent>(Assert.Single(message.Content)).Text.Contains("world changed", StringComparison.Ordinal));
        Assert.Equal(
            GameToolApprovalStatus.Expired,
            (await store.ReadAsync(pending.Request.Owner, pending.Request.ApprovalId, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task ConcurrentApprovalResponsesProduceOnlyTheWinningCredential()
    {
        var store = new RacingApprovalStore();
        var broker = new GameToolApprovalBroker(store);
        var executed = 0;
        await using var runtime = Runtime(
            ToolProvider("{\"a\":1,\"b\":2}"),
            broker,
            new MutableWorldStateProvider(),
            () => Interlocked.Increment(ref executed));

        var run = runtime.RunAsync(Input("approval-race"), TestContext.Current.CancellationToken);
        var pending = await WaitForPendingAsync(broker);
        var approve = broker.RespondAsync(
            new GameToolApprovalResponse(
                pending.Request.Owner,
                pending.Request.ApprovalId,
                pending.Revision,
                GameToolApprovalResponseKind.Approve),
            TestContext.Current.CancellationToken).AsTask();
        var deny = broker.RespondAsync(
            new GameToolApprovalResponse(
                pending.Request.Owner,
                pending.Request.ApprovalId,
                pending.Revision,
                GameToolApprovalResponseKind.Deny),
            TestContext.Current.CancellationToken).AsTask();

        await Task.WhenAll(approve, deny);
        await run;
        var stored = await store.ReadAsync(
            pending.Request.Owner,
            pending.Request.ApprovalId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Contains(stored.Status, new[] { GameToolApprovalStatus.Consumed, GameToolApprovalStatus.Denied });
        Assert.Equal(stored.Status == GameToolApprovalStatus.Consumed ? 1 : 0, executed);
    }

    [Fact]
    public async Task RestartExpiresApprovedRecordWhosePlaintextCredentialWasLost()
    {
        var store = new InMemoryGameToolApprovalStore();
        var request = ApprovalRequest("orphaned-approval");
        await store.SaveAsync(
            new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, request.RequestedAt),
            null,
            TestContext.Current.CancellationToken);
        await store.SaveAsync(
            new GameToolApprovalRecord(
                request,
                GameToolApprovalStatus.Approved,
                1,
                request.RequestedAt,
                credentialDigest: "lost-process-local-credential"),
            0,
            TestContext.Current.CancellationToken);

        var restartedBroker = new GameToolApprovalBroker(store);
        Assert.Empty(await restartedBroker.ListPendingAsync(
            request.Owner,
            8,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            GameToolApprovalStatus.Expired,
            (await store.ReadAsync(request.Owner, request.ApprovalId, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task DisabledExplicitAndTaskModesUseOnlyHostAttestedScope()
    {
        await AssertModeAsync(GameToolApprovalMode.Disabled, new GameToolInvocationScope(), expectedExecutions: 0);
        await AssertModeAsync(GameToolApprovalMode.ExplicitOnly, new GameToolInvocationScope(), expectedExecutions: 0);
        await AssertModeAsync(
            GameToolApprovalMode.ExplicitOnly,
            new GameToolInvocationScope(explicitlyRequestedTools: new[] { "write" }),
            expectedExecutions: 1);
        await AssertModeAsync(GameToolApprovalMode.AllowedInTask, new GameToolInvocationScope(), expectedExecutions: 0);
        await AssertModeAsync(
            GameToolApprovalMode.AllowedInTask,
            new GameToolInvocationScope(taskId: "task-1", taskAllowedTools: new[] { "write" }),
            expectedExecutions: 1);
    }

    [Fact]
    public async Task DenialAndCancellationPersistAndNeverReachExecutor()
    {
        var store = new InMemoryGameToolApprovalStore();
        var broker = new GameToolApprovalBroker(store);
        var executed = 0;
        await using var runtime = Runtime(ToolProvider("{\"a\":1,\"b\":2}"), broker, new MutableWorldStateProvider(), () => executed++);

        var deniedRun = runtime.RunAsync(Input("denied"), TestContext.Current.CancellationToken);
        var denied = await WaitForPendingAsync(broker);
        await broker.RespondAsync(
            new GameToolApprovalResponse(denied.Request.Owner, denied.Request.ApprovalId, denied.Revision, GameToolApprovalResponseKind.Deny, "host rejected"),
            TestContext.Current.CancellationToken);
        await deniedRun;
        Assert.Equal(GameToolApprovalStatus.Denied,
            (await store.ReadAsync(denied.Request.Owner, denied.Request.ApprovalId, TestContext.Current.CancellationToken))!.Status);

        using var cancellation = new CancellationTokenSource();
        var cancelledRun = runtime.RunAsync(Input("cancelled"), cancellation.Token);
        var cancelled = await WaitForPendingAsync(broker);
        cancellation.Cancel();
        var cancelledResult = await cancelledRun;
        Assert.False(cancelledResult.Succeeded);
        Assert.Equal(GameToolApprovalStatus.Cancelled,
            (await store.ReadAsync(cancelled.Request.Owner, cancelled.Request.ApprovalId, TestContext.Current.CancellationToken))!.Status);
        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task ApprovalTraceContainsTimingButNoArgumentsDigestOrCredential()
    {
        var store = new InMemoryGameToolApprovalStore();
        var broker = new GameToolApprovalBroker(store);
        var sink = new InMemoryGameAgentTraceSink();
        await using var runtime = new GameAgentBuilder(ToolProvider("{\"a\":12345,\"b\":67890}"), "model")
            .UseExtension("tools", "1", api => api.RegisterTool(Tool(() => { })))
            .UseExtension(new ToolApprovalExtension(
                new[] { new GameToolApprovalRule("confirm-write", GameToolApprovalMode.ConfirmOnce, "write") },
                broker,
                new MutableWorldStateProvider()))
            .UseExtension(new GameAgentTracingExtension(sink))
            .Build();

        var run = runtime.RunAsync(Input("trace"), TestContext.Current.CancellationToken);
        var pending = await WaitForPendingAsync(broker);
        await broker.RespondAsync(
            new GameToolApprovalResponse(pending.Request.Owner, pending.Request.ApprovalId, pending.Revision, GameToolApprovalResponseKind.Approve),
            TestContext.Current.CancellationToken);
        await run;

        var approvalEntries = sink.Snapshot().Where(value => value.Kind.StartsWith("tool.approval.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, approvalEntries.Length);
        Assert.Contains("waitMilliseconds", approvalEntries[1].DetailsJson, StringComparison.Ordinal);
        Assert.All(approvalEntries, entry =>
        {
            Assert.DoesNotContain("12345", entry.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("arguments", entry.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", entry.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("digest", entry.DetailsJson, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task AssertModeAsync(
        GameToolApprovalMode mode,
        GameToolInvocationScope scope,
        int expectedExecutions)
    {
        var executed = 0;
        await using var runtime = new GameAgentBuilder(ToolProvider("{\"a\":1,\"b\":2}"), "model")
            .UseExtension("tools", "1", api => api.RegisterTool(Tool(() => executed++)))
            .UseExtension(new ToolApprovalExtension(
                new[] { new GameToolApprovalRule("rule", mode, "write") },
                new GameToolApprovalBroker(new InMemoryGameToolApprovalStore()),
                new MutableWorldStateProvider(),
                new FixedScopeProvider(scope)))
            .Build();
        var result = await runtime.RunAsync(Input(Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(expectedExecutions, executed);
    }

    private static GameAgentRuntime Runtime(
        IModelProvider provider,
        IGameToolApprovalBroker broker,
        IGameToolApprovalWorldStateProvider world,
        Action execute) =>
        new GameAgentBuilder(provider, "model")
            .UseExtension("tools", "1", api => api.RegisterTool(Tool(execute)))
            .UseExtension(new ToolApprovalExtension(
                new[] { new GameToolApprovalRule("confirm-write", GameToolApprovalMode.ConfirmOnce, "write") },
                broker,
                world))
            .Build();

    private static AgentTool Tool(Action execute) => new(
        new ToolDefinition(
            "write",
            "Write authoritative state.",
            "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}},\"required\":[\"a\",\"b\"],\"additionalProperties\":false}"),
        (_, _, _) =>
        {
            execute();
            return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("written") }));
        },
        ToolRisk.NonIdempotentWrite);

    private static IModelProvider ToolProvider(string arguments) => new ScriptedToolProvider(arguments);

    private static GameInput Input(string inputId) =>
        new("session", "actor", "chat", "{}", new GameMoment("timeline", 4), inputId);

    private static GameToolApprovalRequest ApprovalRequest(string approvalId)
    {
        var now = DateTimeOffset.UtcNow;
        return new GameToolApprovalRequest(
            approvalId,
            "policy",
            "session",
            "actor",
            "input",
            "run",
            1,
            "call",
            "write",
            ToolRisk.NonIdempotentWrite,
            "{\"value\":1}",
            "digest",
            new GameMoment("timeline", 1),
            new GameToolApprovalWorldState("save", 1),
            null,
            now,
            now.AddMinutes(1));
    }

    private static async Task<GameToolApprovalRecord> WaitForPendingAsync(IGameToolApprovalBroker broker)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pending = await broker.ListPendingAsync(
                new GameSessionKey("session", "actor"),
                8,
                TestContext.Current.CancellationToken);
            if (pending.Count > 0)
            {
                return Assert.Single(pending);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The approval request did not become pending.");
    }

    private sealed class MutableWorldStateProvider : IGameToolApprovalWorldStateProvider
    {
        public long Revision { get; set; } = 1;

        public ValueTask<GameToolApprovalWorldState> ReadAsync(GameInput input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameToolApprovalWorldState>(new GameToolApprovalWorldState("save-1", Revision));
        }
    }

    private sealed class RacingApprovalStore : IGameToolApprovalStore
    {
        private readonly InMemoryGameToolApprovalStore _inner = new();
        private readonly TaskCompletionSource<bool> _updatesReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _updates;

        public ValueTask<GameToolApprovalRecord?> ReadAsync(
            GameSessionKey owner,
            string approvalId,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(owner, approvalId, cancellationToken);

        public ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListAsync(
            GameSessionKey owner,
            GameToolApprovalStatus? status,
            int maximum,
            CancellationToken cancellationToken) =>
            _inner.ListAsync(owner, status, maximum, cancellationToken);

        public async ValueTask<GameToolApprovalRecord> SaveAsync(
            GameToolApprovalRecord record,
            long? expectedRevision,
            CancellationToken cancellationToken)
        {
            if (expectedRevision is not null)
            {
                if (Interlocked.Increment(ref _updates) == 2)
                {
                    _updatesReady.TrySetResult(true);
                }

                await _updatesReady.Task.WaitAsync(cancellationToken);
            }

            return await _inner.SaveAsync(record, expectedRevision, cancellationToken);
        }
    }

    private sealed class FixedScopeProvider : IGameToolInvocationScopeProvider
    {
        private readonly GameToolInvocationScope _scope;

        public FixedScopeProvider(GameToolInvocationScope scope) => _scope = scope;

        public ValueTask<GameToolInvocationScope> ResolveAsync(
            GameToolInvocationScopeContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameToolInvocationScope>(_scope);
        }
    }

    private sealed class RewritePolicy : IGameToolPolicy
    {
        public string Id => "rewrite";

        public ValueTask<GameToolPolicyDecision> EvaluateAsync(
            GameToolPolicyContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameToolPolicyDecision>(GameToolPolicyDecision.Rewrite("{\"b\":2,\"a\":9}"));
        }
    }

    private sealed class ScriptedToolProvider : IModelProvider
    {
        private readonly string _arguments;
        private int _calls;

        public ScriptedToolProvider(string arguments) => _arguments = arguments;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            yield return ModelStreamEvent.Terminal(call % 2 == 1
                ? new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call-" + call, "write", _arguments) },
                    ModelStopReason.ToolUse)
                : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
            await Task.CompletedTask;
        }
    }
}
