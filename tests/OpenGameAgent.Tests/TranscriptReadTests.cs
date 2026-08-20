using System.Text;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class TranscriptReadTests
{
    [Fact]
    public async Task ReadsStableBoundedPagesAndRejectsStaleRevisionCursors()
    {
        var key = new GameSessionKey("session", "actor");
        var store = new InMemoryGameSessionStore();
        await SaveAsync(store, key, 1, new[]
        {
            Message("one"), Message("two"), Message("three"),
        });
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new NeverProvider(), "test")
        {
            SessionStore = store,
        });

        var first = await runtime.ReadTranscriptAsync(
            key,
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal(new[] { "one", "two" }, first.Messages.Select(Text));
        Assert.Equal(3, first.TotalMessages);
        Assert.NotNull(first.NextCursor);

        var second = await runtime.ReadTranscriptAsync(
            key,
            pageSize: 2,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("three", Text(Assert.Single(second!.Messages)));
        Assert.Null(second.NextCursor);

        await SaveAsync(store, key, 2, new[] { Message("rewound") }, expectedRevision: 1);
        await Assert.ThrowsAsync<GameSessionTranscriptChangedException>(async () =>
            await runtime.ReadTranscriptAsync(
                key,
                pageSize: 2,
                cursor: first.NextCursor,
                cancellationToken: TestContext.Current.CancellationToken));

        var current = await runtime.ReadTranscriptAsync(
            key,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("rewound", Text(Assert.Single(current!.Messages)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task RejectsInvalidPageSizesBeforeTouchingTheStore(int pageSize)
    {
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new NeverProvider(), "test")
        {
            SessionStore = new ThrowingStore(),
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await runtime.ReadTranscriptAsync(
                new GameSessionKey("session", "actor"),
                pageSize,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("v1.-1.0")]
    [InlineData("v1.1.-1")]
    [InlineData("v2.1.0")]
    public async Task RejectsMalformedCursorsBeforeTouchingTheStore(string cursor)
    {
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new NeverProvider(), "test")
        {
            SessionStore = new ThrowingStore(),
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.ReadTranscriptAsync(
                new GameSessionKey("session", "actor"),
                cursor: cursor,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReducesPageCountToKeepTheSerializedWirePageBounded()
    {
        var key = new GameSessionKey("session", "actor");
        var store = new InMemoryGameSessionStore();
        var largeText = new string('x', 3_000_000);
        await SaveAsync(store, key, 1, new[]
        {
            Message(largeText), Message(largeText), Message(largeText),
        });
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new NeverProvider(), "test")
        {
            SessionStore = store,
        });

        var first = await runtime.ReadTranscriptAsync(
            key,
            pageSize: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(2, first.Messages.Count);
        Assert.NotNull(first.NextCursor);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(GameAgentWire.SerializeTranscriptPage(first)),
            1,
            GameAgentWire.MaximumTranscriptPageUtf8Bytes);

        var second = await runtime.ReadTranscriptAsync(
            key,
            pageSize: 3,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(second!.Messages);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task RejectsOneMessageThatCannotFitAWirePage()
    {
        var key = new GameSessionKey("session", "actor");
        var store = new InMemoryGameSessionStore();
        await SaveAsync(
            store,
            key,
            1,
            new[] { Message(new string('x', GameAgentWire.MaximumTranscriptPageUtf8Bytes)) });
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new NeverProvider(), "test")
        {
            SessionStore = store,
        });

        await Assert.ThrowsAsync<GameSessionTranscriptPageTooLargeException>(async () =>
            await runtime.ReadTranscriptAsync(
                key,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static AgentMessage Message(string text) =>
        new(AgentRole.User, new AgentContent[] { new TextContent(text) }, DateTimeOffset.UnixEpoch);

    private static string Text(AgentMessage message) =>
        Assert.IsType<TextContent>(Assert.Single(message.Content)).Text;

    private static async Task SaveAsync(
        IGameSessionStore store,
        GameSessionKey key,
        long revision,
        IReadOnlyList<AgentMessage> messages,
        long expectedRevision = 0)
    {
        var saved = await store.SaveAsync(
            new GameSessionSnapshot(key, revision, messages),
            expectedRevision,
            TestContext.Current.CancellationToken);
        Assert.True(saved.Saved);
    }

    private sealed class NeverProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The transcript read must not call the provider.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class ThrowingStore : IGameSessionStore
    {
        public ValueTask<GameSessionSnapshot?> LoadAsync(GameSessionKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The store must not be touched.");

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The store must not be touched.");
    }
}
