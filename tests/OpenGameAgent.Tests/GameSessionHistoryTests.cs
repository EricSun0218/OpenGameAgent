using OpenGameAgent.Kernel;
using Xunit;

#pragma warning disable xUnit1051 // Individual operations are in-memory; cancellation behavior has a dedicated test.

namespace OpenGameAgent.Tests;

public sealed class GameSessionHistoryTests
{
    [Fact]
    public async Task SharedLogSupportsLanesBranchesRecordsFactsAndCursors()
    {
        var repository = new InMemoryGameSessionHistoryRepository();
        var history = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "session" });
        var root = await history.AppendEntryAsync("root", "turn", "{\"value\":1}", mutationId: "m1", expectedSequence: 0);
        await history.CreateLaneAsync("npc", root.Entry.Id, mutationId: "m2", expectedSequence: 1);
        var main = await history.AppendEntryAsync("main", "turn", "{\"value\":2.5}", mutationId: "m3", expectedSequence: 2);
        var npc = await history.AppendEntryAsync("npc", "turn", "{\"value\":3}", lane: "npc", mutationId: "m4", expectedSequence: 3);
        var record = await history.AppendRecordAsync("decision", "trace", "{\"why\":\"goal\"}", lane: "npc", mutationId: "m5");
        await history.SetNameAsync("Example", mutationId: "m6");
        await history.SetLabelAsync(npc.Entry.Id, "checkpoint", mutationId: "m7");

        Assert.Equal(1, root.Entry.Sequence);
        Assert.Equal(3, main.Entry.Sequence);
        Assert.Equal(4, npc.Entry.Sequence);
        Assert.Equal(5, record.Record.Sequence);
        Assert.Equal(new[] { "root", "main" }, (await history.FindBranchAsync(
            query: new GameHistoryBranchQuery { Order = GameHistoryOrder.OldestFirst })).Items.Select(entry => entry.Id));
        Assert.Equal(new[] { "root", "npc" }, (await history.FindBranchAsync(
            "npc",
            new GameHistoryBranchQuery { Order = GameHistoryOrder.OldestFirst })).Items.Select(entry => entry.Id));
        Assert.Equal(npc.Entry.Id, await history.View("npc").GetLeafEntryIdAsync());
        var npcTail = await history.View("npc").AppendEntryAsync("npc-tail", "turn", "{}");
        Assert.Equal(npc.Entry.Id, npcTail.Entry.ParentId);
        Assert.Equal("Example", await history.GetNameAsync());
        Assert.Equal("checkpoint", await history.GetLabelAsync("npc"));
        Assert.Equal(new[] { 3L, 4L }, (await history.FindEntriesAsync(new GameHistoryEntryQuery
        {
            Order = GameHistoryOrder.OldestFirst,
            CursorSequence = 1,
            Limit = 2,
        })).Items.Select(entry => entry.Sequence));
        Assert.Equal(new[] { 1L, 2L, 3L }, (await history.GetLogAsync(new GameHistoryLogQuery { Limit = 3 })).Items.Select(item => item.Sequence));
        Assert.Equal(3, (await history.GetLogAsync(new GameHistoryLogQuery { Limit = 3 })).NextSequence);
        Assert.Equal(8, (await history.GetStatsAsync()).LastSequence);
    }

    [Fact]
    public async Task MutationIdsAreIdempotentAndExpectedSequenceFailsClosed()
    {
        var history = await new InMemoryGameSessionHistoryRepository().CreateAsync(
            new GameHistoryCreateOptions { Id = "session" });

        var first = await history.AppendEntryAsync("entry", "event", "{}", mutationId: "stable", expectedSequence: 0);
        var retry = await history.AppendEntryAsync("entry", "event", "{}", mutationId: "stable", expectedSequence: 999);

        Assert.True(retry.Commit.Replayed);
        Assert.Equal(first.Entry.Sequence, retry.Entry.Sequence);
        await Assert.ThrowsAsync<GameHistoryConcurrencyException>(() =>
            history.AppendEntryAsync("next", "event", "{}", mutationId: "next-mutation", expectedSequence: 0));
        var mismatch = await Assert.ThrowsAsync<GameHistoryException>(() =>
            history.AppendEntryAsync("different", "event", "{}", mutationId: "stable"));
        Assert.Equal(GameHistoryErrorCode.Conflict, mismatch.Code);
        Assert.Equal(1, (await history.GetStatsAsync()).MutationCount);
    }

    [Fact]
    public async Task ForkCopiesASelectedBranchOrCompleteTreeWithoutOperationalRecords()
    {
        var repository = new InMemoryGameSessionHistoryRepository();
        var source = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "source" });
        var root = await source.AppendEntryAsync("root", "turn", "{}");
        var shared = await source.AppendEntryAsync("shared", "turn", "{}");
        await source.CreateLaneAsync("npc", shared.Entry.Id);
        var main = await source.AppendEntryAsync("main", "turn", "{}");
        var npc = await source.AppendEntryAsync("npc", "turn", "{}", lane: "npc");
        await source.AppendRecordAsync("run", "operation", "{}");
        await source.SetNameAsync("World A");
        await source.SetLabelAsync(shared.Entry.Id, "shared-label");
        await source.SetLabelAsync(npc.Entry.Id, "npc-label");

        var branch = await repository.ForkAsync("source", new GameHistoryForkOptions
        {
            Id = "branch",
            EntryId = main.Entry.Id,
            Position = GameHistoryForkPosition.At,
        });
        var tree = await repository.ForkAsync("source", new GameHistoryForkOptions
        {
            Id = "tree",
            Scope = GameHistoryForkScope.Tree,
        });

        Assert.Equal(new[] { "root", "shared", "main" }, (await branch.FindEntriesAsync(new GameHistoryEntryQuery
        {
            Order = GameHistoryOrder.OldestFirst,
        })).Items.Select(entry => entry.Id));
        Assert.Empty((await branch.FindRecordsAsync()).Items);
        Assert.Equal("shared-label", await branch.GetLabelAsync(shared.Entry.Id));
        Assert.Null(await branch.GetLabelAsync(npc.Entry.Id));
        Assert.Equal("World A", await branch.GetNameAsync());
        Assert.Equal(new[] { "main", "npc" }, (await tree.GetLanesAsync()).Select(lane => lane.Name));
        Assert.Equal(npc.Entry.Id, (await tree.GetLanesAsync()).Single(lane => lane.Name == "npc").LeafEntryId);
        Assert.Empty((await tree.FindRecordsAsync()).Items);
        Assert.Equal("source", (await branch.GetMetadataAsync()).ParentSessionId);
        Assert.Equal(root.Entry.ParentId, (await branch.GetEntryAsync("root"))!.ParentId);
    }

    [Fact]
    public async Task ContextProjectionIsComposableBoundedAndNotTextOnly()
    {
        var history = await new InMemoryGameSessionHistoryRepository().CreateAsync(
            new GameHistoryCreateOptions { Id = "session" });
        await history.AppendEntryAsync("old", "event", "{\"ignored\":true}");
        await history.AppendEntryAsync("checkpoint", "context_checkpoint", "{\"summary\":\"state\"}");
        await history.AppendEntryAsync("binary-input", "controller_input", "{\"axis\":0.75,\"buttons\":[1,0]}");

        var projection = await history.BuildContextAsync(
            "main",
            new GameHistoryContextOptions
            {
                EntryTransform = GameHistoryContextTransforms.AfterLatest("context_checkpoint"),
                EntryProjector = entry => entry.Type == "controller_input"
                    ? new[] { AgentMessage.UserJson(entry.PayloadJson) }
                    : Array.Empty<AgentMessage>(),
                StateProjector = entries => $"{{\"entryCount\":{entries.Count}}}",
            });

        Assert.Single(projection.Messages);
        var json = Assert.IsType<JsonContent>(Assert.Single(projection.Messages[0].Content));
        Assert.Contains("0.75", json.Json, StringComparison.Ordinal);
        Assert.Equal("{\"entryCount\":2}", projection.StateJson);
    }

    [Fact]
    public async Task RepositoryListSearchDeleteAndConcurrentWritesAreBounded()
    {
        var repository = new InMemoryGameSessionHistoryRepository(new GameHistoryLimits
        {
            MaxSessions = 20,
            MaxEntriesPerSession = 50,
            MaxRecordsPerSession = 10,
            MaxMutationsPerSession = 100,
            MaxLanesPerSession = 5,
            DefaultQueryResults = 2,
            MaxQueryResults = 10,
            MaxSearchResults = 5,
        });
        var first = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "a" });
        await repository.CreateAsync(new GameHistoryCreateOptions { Id = "b" });
        var writes = Enumerable.Range(0, 20).Select(index =>
            first.AppendEntryAsync($"entry-{index}", "event", $"{{\"search\":\"needle {index}\"}}"));
        var committed = await Task.WhenAll(writes);

        Assert.Equal(20, committed.Select(value => value.Entry.Sequence).Distinct().Count());
        var firstList = await repository.ListAsync(new GameHistoryListQuery { Limit = 1 });
        Assert.Single(firstList.Sessions);
        Assert.NotNull(firstList.NextSessionId);
        Assert.Single((await repository.ListAsync(new GameHistoryListQuery
        {
            Limit = 1,
            AfterSessionId = firstList.NextSessionId,
        })).Sessions);
        var search = await repository.SearchAsync(new GameHistorySearchQuery("needle") { Limit = 3 });
        Assert.Equal(3, search.Hits.Count);
        Assert.NotNull(search.NextCursor);
        Assert.NotEmpty((await repository.SearchAsync(new GameHistorySearchQuery("needle")
        {
            Limit = 3,
            Cursor = search.NextCursor,
        })).Hits);

        await repository.DeleteAsync("a");
        await repository.DeleteAsync("a");
        await Assert.ThrowsAsync<GameHistoryException>(() => repository.OpenAsync("a"));
    }

    [Fact]
    public async Task InvalidJsonLimitsQueriesAndCancellationDoNotMutateState()
    {
        var history = await new InMemoryGameSessionHistoryRepository(new GameHistoryLimits
        {
            MaxSessions = 2,
            MaxEntriesPerSession = 2,
            MaxRecordsPerSession = 0,
            MaxMutationsPerSession = 10,
            MaxLanesPerSession = 2,
            MaxPayloadCharacters = 16,
            DefaultQueryResults = 1,
            MaxQueryResults = 2,
        }).CreateAsync(new GameHistoryCreateOptions { Id = "session" });

        await Assert.ThrowsAsync<GameHistoryException>(() => history.AppendEntryAsync("bad", "event", "{not-json}"));
        await Assert.ThrowsAsync<GameHistoryException>(() => history.FindEntriesAsync(new GameHistoryEntryQuery { Limit = 3 }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            history.AppendEntryAsync("cancelled", "event", "{}", cancellationToken: cancellation.Token));
        Assert.Equal(0, (await history.GetStatsAsync()).MutationCount);
    }

    [Fact]
    public async Task SearchScanAndContextCallbacksHaveHardBounds()
    {
        var limits = new GameHistoryLimits
        {
            MaxSessions = 2,
            MaxEntriesPerSession = 10,
            MaxRecordsPerSession = 1,
            MaxMutationsPerSession = 20,
            MaxLanesPerSession = 2,
            DefaultQueryResults = 2,
            MaxQueryResults = 10,
            MaxSearchResults = 5,
            MaxSearchScannedEntries = 2,
        };
        var repository = new InMemoryGameSessionHistoryRepository(limits);
        var history = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "session" });
        await history.AppendEntryAsync("one", "event", "{}");
        await history.AppendEntryAsync("two", "event", "{}");
        await history.AppendEntryAsync("three", "event", "{\"match\":true}");

        var scanError = await Assert.ThrowsAsync<GameHistoryException>(() =>
            repository.SearchAsync(new GameHistorySearchQuery("match")));
        Assert.Equal(GameHistoryErrorCode.LimitExceeded, scanError.Code);
        var callbackError = await Assert.ThrowsAsync<GameHistoryException>(() => history.BuildContextAsync(
            new GameHistoryContextOptions
            {
                CallbackTimeout = TimeSpan.FromMilliseconds(20),
                EntryProjector = _ =>
                {
                    Thread.Sleep(200);
                    return Array.Empty<AgentMessage>();
                },
            }));
        Assert.Equal(GameHistoryErrorCode.LimitExceeded, callbackError.Code);
    }
}
