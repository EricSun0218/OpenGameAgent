using System.Text;
using Xunit;

#pragma warning disable xUnit1051 // File operations are bounded; lock cancellation has a dedicated test.

namespace OpenGameAgent.Persistence.Tests;

public sealed class FileGameSessionHistoryTests
{
    [Fact]
    public async Task RestartRoundTripsTreeRecordsFactsAndSharedSequence()
    {
        using var directory = new TemporaryDirectory();
        var repository = CreateRepository(directory.Path);
        var history = await repository.CreateAsync(new GameHistoryCreateOptions
        {
            Id = "session",
            MetadataJson = "{\"world\":\"one\"}",
        });
        var root = await history.AppendEntryAsync("root", "turn", "{\"x\":1.5}", mutationId: "m1");
        await history.CreateLaneAsync("npc", root.Entry.Id, mutationId: "m2");
        await history.AppendEntryAsync("npc", "turn", "{\"x\":2}", lane: "npc", mutationId: "m3");
        await history.AppendRecordAsync("record", "decision", "{\"ok\":true}", lane: "npc", mutationId: "m4");
        await history.SetNameAsync("World", mutationId: "m5");
        await history.SetLabelAsync("npc", "tip", mutationId: "m6");

        var reopened = await CreateRepository(directory.Path).OpenAsync("session");

        Assert.Equal(new[] { "root", "npc" }, (await reopened.FindEntriesAsync(new GameHistoryEntryQuery
        {
            Order = GameHistoryOrder.OldestFirst,
        })).Items.Select(entry => entry.Id));
        Assert.Equal("decision", Assert.Single((await reopened.FindRecordsAsync()).Items).Type);
        Assert.Equal("World", await reopened.GetNameAsync());
        Assert.Equal("tip", await reopened.GetLabelAsync("npc"));
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L, 6L }, (await reopened.GetLogAsync(new GameHistoryLogQuery
        {
            Limit = 20,
        })).Items.Select(item => item.Sequence));
        Assert.Contains("1.5", (await reopened.GetEntryAsync("root"))!.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentRepositoriesLinearizeWritesAndRetryStableMutations()
    {
        using var directory = new TemporaryDirectory();
        var firstRepository = CreateRepository(directory.Path);
        var secondRepository = CreateRepository(directory.Path);
        var first = await firstRepository.CreateAsync(new GameHistoryCreateOptions { Id = "session" });
        var second = await secondRepository.OpenAsync("session");
        var writes = Enumerable.Range(0, 24).Select(index =>
            (index % 2 == 0 ? first : second).AppendEntryAsync(
                $"entry-{index}",
                "event",
                $"{{\"index\":{index}}}",
                mutationId: $"mutation-{index}"));

        var results = await Task.WhenAll(writes);
        var reopened = await CreateRepository(directory.Path).OpenAsync("session");
        var entries = (await reopened.FindEntriesAsync(new GameHistoryEntryQuery
        {
            Order = GameHistoryOrder.OldestFirst,
            Limit = 100,
        })).Items;

        Assert.Equal(24, entries.Count);
        Assert.Equal(24, results.Select(result => result.Entry.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 24).Select(value => (long)value), entries.Select(entry => entry.Sequence));
        var retry = await second.AppendEntryAsync("entry-0", "event", "{\"index\":0}", mutationId: "mutation-0", expectedSequence: 0);
        Assert.True(retry.Commit.Replayed);
        var conflict = await Assert.ThrowsAsync<GameHistoryConcurrencyException>(() =>
            first.AppendEntryAsync("late", "event", "{}", mutationId: "late", expectedSequence: 1));
        Assert.Equal(24, conflict.ActualSequence);
    }

    [Fact]
    public async Task CrossRepositoryCreateAndMutationRacesPublishExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var firstRepository = CreateRepository(directory.Path);
        var secondRepository = CreateRepository(directory.Path);
        var creates = await Task.WhenAll(
            CaptureAsync(() => firstRepository.CreateAsync(new GameHistoryCreateOptions { Id = "session" })),
            CaptureAsync(() => secondRepository.CreateAsync(new GameHistoryCreateOptions { Id = "session" })));
        Assert.Single(creates, result => result.History is not null);
        Assert.Single(creates, result => result.Error?.Code == GameHistoryErrorCode.AlreadyExists);

        var first = await firstRepository.OpenAsync("session");
        var second = await secondRepository.OpenAsync("session");
        var commits = await Task.WhenAll(
            first.AppendEntryAsync("entry", "event", "{\"value\":1}", mutationId: "shared-mutation"),
            second.AppendEntryAsync("entry", "event", "{\"value\":1}", mutationId: "shared-mutation"));

        Assert.Single(commits, commit => !commit.Commit.Replayed);
        Assert.Single(commits, commit => commit.Commit.Replayed);
        Assert.Single((await (await CreateRepository(directory.Path).OpenAsync("session")).FindEntriesAsync()).Items);
    }

    [Fact]
    public async Task RepairsOnlyATornSyntaxTailAndRejectsCompleteOrMiddleCorruption()
    {
        using var directory = new TemporaryDirectory();
        var repository = CreateRepository(directory.Path);
        var history = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "torn" });
        await history.AppendEntryAsync("kept", "event", "{}");
        var path = SessionPath(directory.Path, "torn");
        await File.AppendAllTextAsync(path, "{\"Kind\":\"mutation\"", Encoding.UTF8);

        var repaired = await CreateRepository(directory.Path).OpenAsync("torn");
        Assert.Equal("kept", Assert.Single((await repaired.FindEntriesAsync()).Items).Id);
        Assert.EndsWith("\n", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        await repaired.AppendEntryAsync("after", "event", "{}");
        var verified = await CreateRepository(directory.Path).OpenAsync("torn");
        Assert.Equal(2, (await verified.FindEntriesAsync()).Items.Count);

        var unterminated = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "unterminated" });
        await unterminated.AppendEntryAsync("valid", "event", "{}");
        var unterminatedPath = SessionPath(directory.Path, "unterminated");
        await File.WriteAllTextAsync(unterminatedPath, (await File.ReadAllTextAsync(unterminatedPath)).TrimEnd('\r', '\n'));
        var reopenedUnterminated = await CreateRepository(directory.Path).OpenAsync("unterminated");
        Assert.Equal("valid", Assert.Single((await reopenedUnterminated.FindEntriesAsync()).Items).Id);
        Assert.EndsWith("\n", await File.ReadAllTextAsync(unterminatedPath), StringComparison.Ordinal);

        var complete = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "complete" });
        await complete.AppendEntryAsync("kept", "event", "{}");
        await File.AppendAllTextAsync(SessionPath(directory.Path, "complete"), "{\"Kind\":\"unknown\"}\n", Encoding.UTF8);
        var completeError = await Assert.ThrowsAsync<GameHistoryException>(() => CreateRepository(directory.Path).OpenAsync("complete"));
        Assert.Equal(GameHistoryErrorCode.CorruptStorage, completeError.Code);

        var semantic = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "semantic" });
        await semantic.AppendEntryAsync("kept", "event", "{}");
        var semanticPath = SessionPath(directory.Path, "semantic");
        const string invalidCompleteMutation = "{\"Kind\":\"mutation\",\"MutationId\":\"bad\",\"Sequence\":2,\"MutationKind\":\"Entry\"}";
        await File.AppendAllTextAsync(semanticPath, invalidCompleteMutation, Encoding.UTF8);
        var beforeRejectedOpen = await File.ReadAllTextAsync(semanticPath);
        var semanticError = await Assert.ThrowsAsync<GameHistoryException>(() => CreateRepository(directory.Path).OpenAsync("semantic"));
        Assert.Equal(GameHistoryErrorCode.CorruptStorage, semanticError.Code);
        Assert.Equal(beforeRejectedOpen, await File.ReadAllTextAsync(semanticPath));

        var middle = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "middle" });
        await middle.AppendEntryAsync("one", "event", "{}");
        await middle.AppendEntryAsync("two", "event", "{}");
        var middlePath = SessionPath(directory.Path, "middle");
        var lines = (await File.ReadAllLinesAsync(middlePath)).ToList();
        lines.Insert(2, "not-json");
        await File.WriteAllLinesAsync(middlePath, lines);
        var middleError = await Assert.ThrowsAsync<GameHistoryException>(() => CreateRepository(directory.Path).OpenAsync("middle"));
        Assert.Equal(GameHistoryErrorCode.CorruptStorage, middleError.Code);
    }

    [Fact]
    public async Task ForkPersistsSelectedTreeAndLeavesRecordsBehind()
    {
        using var directory = new TemporaryDirectory();
        var repository = CreateRepository(directory.Path);
        var source = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "source" });
        var root = await source.AppendEntryAsync("root", "turn", "{}");
        await source.CreateLaneAsync("npc", root.Entry.Id);
        var main = await source.AppendEntryAsync("main", "turn", "{}");
        await source.AppendEntryAsync("npc", "turn", "{}", lane: "npc");
        await source.AppendRecordAsync("record", "operation", "{}");
        await source.SetLabelAsync("root", "root-label");
        var sourceSequence = (await source.GetStatsAsync()).LastSequence;

        var branch = await repository.ForkAsync("source", new GameHistoryForkOptions
        {
            Id = "branch",
            EntryId = main.Entry.Id,
            ExpectedSourceSequence = sourceSequence,
        });
        var tree = await repository.ForkAsync("source", new GameHistoryForkOptions
        {
            Id = "tree",
            Scope = GameHistoryForkScope.Tree,
        });
        var reopenedTree = await CreateRepository(directory.Path).OpenAsync("tree");

        Assert.Equal(new[] { "root", "main" }, (await branch.FindEntriesAsync(new GameHistoryEntryQuery
        {
            Order = GameHistoryOrder.OldestFirst,
        })).Items.Select(entry => entry.Id));
        Assert.Empty((await branch.FindRecordsAsync()).Items);
        Assert.Equal("root-label", await branch.GetLabelAsync("root"));
        Assert.Equal(2, (await reopenedTree.GetLanesAsync()).Count);
        Assert.Empty((await reopenedTree.FindRecordsAsync()).Items);
        await Assert.ThrowsAsync<GameHistoryConcurrencyException>(() => repository.ForkAsync("source", new GameHistoryForkOptions
        {
            Id = "conflict",
            ExpectedSourceSequence = 0,
        }));
    }

    [Fact]
    public async Task WaitingForWriterLockIsCancellableWithoutACommit()
    {
        using var directory = new TemporaryDirectory();
        var repository = CreateRepository(directory.Path, lockTimeout: TimeSpan.FromSeconds(2));
        var history = await repository.CreateAsync(new GameHistoryCreateOptions { Id = "session" });
        var lockPath = SessionPath(directory.Path, "session") + ".lck";
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            history.AppendEntryAsync("cancelled", "event", "{}", mutationId: "cancelled", cancellationToken: cancellation.Token));
        held.Dispose();

        var reopened = await CreateRepository(directory.Path).OpenAsync("session");
        Assert.Empty((await reopened.FindEntriesAsync()).Items);
        Assert.Empty((await reopened.GetLogAsync()).Items);
    }

    [Fact]
    public async Task ListAndSearchPaginateAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var repository = CreateRepository(directory.Path);
        foreach (var id in new[] { "a", "b", "c" })
        {
            var history = await repository.CreateAsync(new GameHistoryCreateOptions { Id = id });
            await history.AppendEntryAsync($"entry-{id}", "world_event", $"{{\"text\":\"needle {id}\"}}");
        }

        var first = await repository.ListAsync(new GameHistoryListQuery { Limit = 2 });
        var second = await repository.ListAsync(new GameHistoryListQuery { Limit = 2, AfterSessionId = first.NextSessionId });
        var search = await CreateRepository(directory.Path).SearchAsync(new GameHistorySearchQuery("needle") { Limit = 2 });
        var continued = await CreateRepository(directory.Path).SearchAsync(new GameHistorySearchQuery("needle")
        {
            Limit = 2,
            Cursor = search.NextCursor,
        });

        Assert.Equal(2, first.Sessions.Count);
        Assert.Single(second.Sessions);
        Assert.Equal(2, search.Hits.Count);
        Assert.Single(continued.Hits);
    }

    private static FileGameSessionHistoryRepository CreateRepository(string root, TimeSpan? lockTimeout = null) =>
        new(new FileGameHistoryOptions(root)
        {
            LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5),
            LockRetryDelay = TimeSpan.FromMilliseconds(10),
            Limits = new GameHistoryLimits
            {
                MaxSessions = 100,
                MaxEntriesPerSession = 1_000,
                MaxRecordsPerSession = 1_000,
                MaxMutationsPerSession = 3_000,
                MaxLanesPerSession = 100,
                DefaultQueryResults = 100,
                MaxQueryResults = 1_000,
                MaxSearchResults = 100,
            },
        });

    private static string SessionPath(string root, string id) => Path.Combine(root, id + ".ogahistory.jsonl");

    private static async Task<(GameSessionHistory? History, GameHistoryException? Error)> CaptureAsync(
        Func<Task<GameSessionHistory>> operation)
    {
        try
        {
            return (await operation(), null);
        }
        catch (GameHistoryException exception)
        {
            return (null, exception);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "oga-history-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
