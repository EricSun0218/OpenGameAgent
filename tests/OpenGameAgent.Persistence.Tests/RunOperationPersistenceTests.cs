using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class RunOperationPersistenceTests
{
    [Fact]
    public async Task FileJournalRecoversDispatchAndReplaysTerminalResultAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent(ToolReplayPolicy.Recoverable);
        var first = new FileGameRunOperationJournal(directory.Path);
        var claimed = await first.ClaimToolAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameRunToolClaimStatus.Execute, claimed.Status);
        Assert.Equal(1, claimed.Entry.DispatchAttempts);

        var restarted = new FileGameRunOperationJournal(directory.Path);
        var recover = await restarted.ClaimToolAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameRunToolClaimStatus.Recover, recover.Status);
        var result = new ToolResult(
            new AgentContent[] { new TextContent("ok", "signature", AgentTextPhase.FinalAnswer) },
            detailsJson: "{\"receipt\":1}",
            usage: new ModelUsage(2, 1, cost: new ModelCost(0.01, 0.02, isKnown: true)),
            addedToolNames: new[] { "next" });
        await restarted.CompleteToolAsync(intent.OperationId, result, TestContext.Current.CancellationToken);

        var replayed = await new FileGameRunOperationJournal(directory.Path)
            .ClaimToolAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameRunToolClaimStatus.Replay, replayed.Status);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(replayed.Entry.Result!.Content)).Text);
        Assert.Equal("signature", Assert.IsType<TextContent>(Assert.Single(replayed.Entry.Result.Content)).Signature);
        Assert.Equal(0.03, replayed.Entry.Result.Usage!.Cost.Total, precision: 10);
        Assert.Equal("next", Assert.Single(replayed.Entry.Result.AddedToolNames));
    }

    [Fact]
    public async Task FileJournalFailsClosedForNonReplayableDispatchAndCorruptIdentity()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent(ToolReplayPolicy.Never);
        var journal = new FileGameRunOperationJournal(directory.Path);
        await journal.ClaimToolAsync(intent, TestContext.Current.CancellationToken);

        var blocked = await new FileGameRunOperationJournal(directory.Path)
            .ClaimToolAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameRunToolClaimStatus.Blocked, blocked.Status);

        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.run-tool.json"));
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            path,
            text.Replace(intent.InputId, "different-input", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await new FileGameRunOperationJournal(directory.Path)
                .FindToolAsync(intent.OperationId, TestContext.Current.CancellationToken));
    }

    private static GameRunToolIntent Intent(ToolReplayPolicy policy) => new(
        GameRunToolOperationIds.CreateV1("session", "actor", "input", 1, 0, "ordinary", "{\"x\":1}"),
        new GameSessionKey("session", "actor"),
        "input",
        1,
        0,
        "ordinary",
        "{\"x\":1}",
        policy == ToolReplayPolicy.Never ? ToolRisk.NonIdempotentWrite : ToolRisk.IdempotentWrite,
        policy);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oga-run-operations-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
