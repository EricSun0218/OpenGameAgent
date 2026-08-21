using System.Collections.Concurrent;
using OpenGameAgent;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class DurableActionConflictTests
{
    [Fact]
    public async Task SameConflictSerializesAcrossActorsWhileDifferentConflictsRemainParallel()
    {
        var operationIds = CreateOperationIdsOnDistinctStripes(3, 64);
        var firstId = operationIds[0];
        var secondId = operationIds[1];
        var independentId = operationIds[2];
        var journal = new InMemoryGameActionJournal();
        var handler = new ControlledHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler, conflictPollIntervalMilliseconds: 5);
        var first = dispatcher.ExecuteAsync(Intent(firstId, "session-a", "actor-a", "shared"), TestContext.Current.CancellationToken).AsTask();
        await handler.WaitStartedAsync(firstId, TestContext.Current.CancellationToken);

        var second = dispatcher.ExecuteAsync(Intent(secondId, "session-b", "actor-b", "shared"), TestContext.Current.CancellationToken).AsTask();
        await WaitUntilReservedAsync(journal, secondId, TestContext.Current.CancellationToken);
        await Task.Delay(75, TestContext.Current.CancellationToken);
        Assert.False(handler.HasStarted(secondId));
        Assert.Equal(1, handler.MaximumConcurrentExecutions);

        var independent = dispatcher.ExecuteAsync(Intent(independentId, "session-c", "actor-c", "other"), TestContext.Current.CancellationToken).AsTask();
        await handler.WaitStartedAsync(independentId, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.MaximumConcurrentExecutions);

        handler.Release(firstId);
        await handler.WaitStartedAsync(secondId, TestContext.Current.CancellationToken);
        handler.Release(secondId);
        handler.Release(independentId);

        Assert.Equal(GameActionStatus.Committed, (await first).Status);
        Assert.Equal(GameActionStatus.Committed, (await second).Status);
        Assert.Equal(GameActionStatus.Committed, (await independent).Status);
    }

    [Fact]
    public async Task UncertainConflictSurvivesCancellationUntilAuthoritativeReconciliation()
    {
        var journal = new InMemoryGameActionJournal();
        var handler = new RecoveringHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler, conflictPollIntervalMilliseconds: 5);
        var firstIntent = Intent("uncertain", "session-a", "actor-a", "shared");
        Assert.Equal(
            GameActionStatus.Uncertain,
            (await dispatcher.ExecuteAsync(firstIntent, TestContext.Current.CancellationToken)).Status);

        using var canceled = new CancellationTokenSource();
        var blockedIntent = Intent("blocked", "session-b", "actor-b", "shared");
        var blocked = dispatcher.ExecuteAsync(blockedIntent, canceled.Token).AsTask();
        await WaitUntilReservedAsync(journal, blockedIntent.OperationId, TestContext.Current.CancellationToken);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        var prepared = await journal.FindAsync(blockedIntent.OperationId, TestContext.Current.CancellationToken);
        Assert.NotNull(prepared);
        Assert.False(prepared.Dispatched);

        handler.Resolve(firstIntent.OperationId, GameActionStatus.Committed);
        Assert.Equal(
            GameActionStatus.Committed,
            (await dispatcher.ReconcileAsync(firstIntent.OperationId, TestContext.Current.CancellationToken)).Status);

        var retried = await dispatcher.ExecuteAsync(blockedIntent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, retried.Status);
        Assert.Equal(1, handler.ExecutionCount(blockedIntent.OperationId));
    }

    [Fact]
    public async Task GenerationScopesAreIndependentAndLateOldReceiptsCannotReleaseNewBlockers()
    {
        var journal = new InMemoryGameActionJournal();
        var handler = new RecoveringHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler, conflictPollIntervalMilliseconds: 5);
        var old = Intent("old", "session-old", "actor", "shared", generationId: "old-save");
        var current = Intent("current", "session-current", "actor-a", "shared", generationId: "new-save");
        Assert.Equal(GameActionStatus.Uncertain, (await dispatcher.ExecuteAsync(old, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(GameActionStatus.Uncertain, (await dispatcher.ExecuteAsync(current, TestContext.Current.CancellationToken)).Status);

        var follower = Intent("follower", "session-current", "actor-b", "shared", generationId: "new-save");
        var pending = dispatcher.ExecuteAsync(follower, TestContext.Current.CancellationToken).AsTask();
        await WaitUntilReservedAsync(journal, follower.OperationId, TestContext.Current.CancellationToken);

        handler.Resolve(old.OperationId, GameActionStatus.Committed);
        await dispatcher.ReconcileAsync(old.OperationId, TestContext.Current.CancellationToken);
        await Task.Delay(75, TestContext.Current.CancellationToken);
        Assert.False(handler.HasExecuted(follower.OperationId));

        handler.Resolve(current.OperationId, GameActionStatus.Rejected);
        await dispatcher.ReconcileAsync(current.OperationId, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, (await pending).Status);
    }

    [Fact]
    public async Task ResolvedConflictKeyIsCarriedIntoDurableIntentExactlyOnce()
    {
        var input = new GameInput(
            "session",
            "actor",
            "event",
            "{}",
            new GameMoment("world", 1),
            "input");
        var handler = new CapturingHandler();
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var resolutions = 0;
        var tool = GameActionTool.Create(
            input,
            "change_state",
            "Changes state.",
            "{\"type\":\"object\",\"additionalProperties\":false}",
            dispatcher,
            conflictKey: _ =>
            {
                resolutions++;
                return "world:resource";
            });

        var options = new OpenGameAgent.Kernel.AgentOptions(new SingleToolProvider(), "model");
        options.Tools.Add(tool);
        var agent = new OpenGameAgent.Kernel.Agent(options);
        await agent.RunAsync(OpenGameAgent.Kernel.AgentMessage.User("go"), TestContext.Current.CancellationToken);

        Assert.Equal(1, resolutions);
        Assert.NotNull(handler.Intent);
        Assert.Equal("world:resource", handler.Intent.ConflictKey);
    }

    [Fact]
    public async Task LegacyJournalRunsUnscopedActionsAndFailsClosedForConflictKeys()
    {
        var journal = new LegacyJournal();
        var handler = new RecoveringHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);

        var unscoped = new GameActionIntent(
            "legacy-unscoped",
            "input",
            "session",
            "actor",
            "change_state",
            "{}",
            new GameMoment("world", 1));
        Assert.Equal(
            GameActionStatus.Committed,
            (await dispatcher.ExecuteAsync(unscoped, TestContext.Current.CancellationToken)).Status);

        var scoped = Intent("legacy-scoped", "session", "actor", "world:resource");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.ExecuteAsync(scoped, TestContext.Current.CancellationToken));
        Assert.Contains("does not support durable conflict keys", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.ExecutionCount(scoped.OperationId));
    }

    private static GameActionIntent Intent(
        string operationId,
        string sessionId,
        string actorId,
        string conflictKey,
        string generationId = "save") => new(
            operationId,
            "input-" + operationId,
            sessionId,
            actorId,
            "change_state",
            "{}",
            new GameMoment("world", 10),
            generationId: generationId,
            conflictKey: conflictKey);

    private static async Task WaitUntilReservedAsync(
        IGameActionJournal journal,
        string operationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await journal.FindAsync(operationId, cancellationToken) is not null)
            {
                return;
            }

            await Task.Delay(5, cancellationToken);
        }

        throw new TimeoutException("The action was not reserved.");
    }

    private static IReadOnlyList<string> CreateOperationIdsOnDistinctStripes(int count, int stripeCount)
    {
        var operationIds = new List<string>(count);
        var usedStripes = new HashSet<int>();
        for (var candidate = 0; candidate < 10_000 && operationIds.Count < count; candidate++)
        {
            var operationId = "operation-" + candidate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hash = StringComparer.Ordinal.GetHashCode(operationId) & int.MaxValue;
            if (usedStripes.Add(hash % stripeCount))
            {
                operationIds.Add(operationId);
            }
        }

        if (operationIds.Count != count)
        {
            throw new InvalidOperationException("Unable to construct operation IDs on distinct dispatcher stripes.");
        }

        return operationIds;
    }

    private sealed class ControlledHandler : IGameActionHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _started = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _released = new(StringComparer.Ordinal);
        private int _active;
        private int _maximum;

        public int MaximumConcurrentExecutions => Volatile.Read(ref _maximum);

        public bool HasStarted(string operationId) =>
            _started.TryGetValue(operationId, out var source) && source.Task.IsCompleted;

        public async ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            Source(_started, intent.OperationId).TrySetResult(null);
            try
            {
                await Source(_released, intent.OperationId).Task.WaitAsync(cancellationToken);
                return GameActionReceipt.Committed(intent, "{}", 1);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameActionReceipt?>((GameActionReceipt?)null);
        }

        public Task WaitStartedAsync(string operationId, CancellationToken cancellationToken) =>
            Source(_started, operationId).Task.WaitAsync(cancellationToken);

        public void Release(string operationId) => Source(_released, operationId).TrySetResult(null);

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximum);
                if (current >= active || Interlocked.CompareExchange(ref _maximum, active, current) == current)
                {
                    return;
                }
            }
        }

        private static TaskCompletionSource<object?> Source(
            ConcurrentDictionary<string, TaskCompletionSource<object?>> sources,
            string operationId) => sources.GetOrAdd(
                operationId,
                static _ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class RecoveringHandler : IGameActionHandler
    {
        private readonly ConcurrentDictionary<string, GameActionStatus> _resolutions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _executions = new(StringComparer.Ordinal);

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executions.AddOrUpdate(intent.OperationId, 1, static (_, value) => value + 1);
            return new ValueTask<GameActionReceipt>(
                intent.OperationId is "uncertain" or "old" or "current"
                    ? GameActionReceipt.Uncertain(intent, "pending reconciliation")
                    : GameActionReceipt.Committed(intent, "{}", 1));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_resolutions.TryGetValue(intent.OperationId, out var status))
            {
                return new ValueTask<GameActionReceipt?>((GameActionReceipt?)null);
            }

            return new ValueTask<GameActionReceipt?>(new GameActionReceipt(
                intent.OperationId,
                status,
                "{}",
                intent.Moment,
                status == GameActionStatus.Committed ? 1 : null));
        }

        public void Resolve(string operationId, GameActionStatus status) => _resolutions[operationId] = status;

        public bool HasExecuted(string operationId) => _executions.ContainsKey(operationId);

        public int ExecutionCount(string operationId) =>
            _executions.TryGetValue(operationId, out var count) ? count : 0;
    }

    private sealed class CapturingHandler : IGameActionHandler
    {
        public GameActionIntent? Intent { get; private set; }

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent = intent;
            return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}", 1));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            new((GameActionReceipt?)null);
    }

    private sealed class LegacyJournal : IGameActionJournal
    {
        private readonly InMemoryGameActionJournal _inner = new();

        public ValueTask<GameActionJournalEntry> ReserveAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken) => _inner.ReserveAsync(intent, cancellationToken);

        public ValueTask<GameActionJournalEntry?> FindAsync(
            string operationId,
            CancellationToken cancellationToken) => _inner.FindAsync(operationId, cancellationToken);

        public ValueTask<bool> MarkDispatchedAsync(
            string operationId,
            CancellationToken cancellationToken) => _inner.MarkDispatchedAsync(operationId, cancellationToken);

        public ValueTask SaveReceiptAsync(
            GameActionReceipt receipt,
            CancellationToken cancellationToken) => _inner.SaveReceiptAsync(receipt, cancellationToken);

        public ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(
            int limit,
            CancellationToken cancellationToken) => _inner.ListPendingAsync(limit, cancellationToken);
    }

    private sealed class SingleToolProvider : OpenGameAgent.Kernel.IModelProvider
    {
        public async IAsyncEnumerable<OpenGameAgent.Kernel.ModelStreamEvent> StreamAsync(
            OpenGameAgent.Kernel.ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var hasToolResult = request.Messages.Any(message => message.Role == OpenGameAgent.Kernel.AgentRole.Tool);
            yield return OpenGameAgent.Kernel.ModelStreamEvent.Terminal(new OpenGameAgent.Kernel.ModelResponse(
                hasToolResult
                    ? new OpenGameAgent.Kernel.AgentContent[] { new OpenGameAgent.Kernel.TextContent("done") }
                    : new OpenGameAgent.Kernel.AgentContent[]
                    {
                        new OpenGameAgent.Kernel.ToolCallContent("call", "change_state", "{}"),
                    },
                hasToolResult
                    ? OpenGameAgent.Kernel.ModelStopReason.Stop
                    : OpenGameAgent.Kernel.ModelStopReason.ToolUse,
                new OpenGameAgent.Kernel.ModelUsage(1, 1)));
        }
    }
}
