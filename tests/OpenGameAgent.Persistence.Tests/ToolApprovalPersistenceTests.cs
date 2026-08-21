using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class ToolApprovalPersistenceTests
{
    [Fact]
    public async Task PendingApprovalRoundTripsAcrossStoreRestartAndOwnerIsIsolated()
    {
        using var directory = new TemporaryDirectory();
        var request = Request("approval-1");
        var first = new FileGameToolApprovalStore(directory.Path);
        await first.SaveAsync(
            new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch),
            null,
            TestContext.Current.CancellationToken);

        var restarted = new FileGameToolApprovalStore(directory.Path);
        var loaded = await restarted.ReadAsync(request.Owner, request.ApprovalId, TestContext.Current.CancellationToken);
        Assert.Equal(GameToolApprovalStatus.Pending, loaded!.Status);
        Assert.Equal("{\"value\":1}", loaded.Request.CanonicalArgumentsJson);
        Assert.Null(await restarted.ReadAsync(
            new GameSessionKey("other", "actor"),
            request.ApprovalId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevisionCasAllowsExactlyOneConcurrentDecision()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameToolApprovalStore(directory.Path);
        var request = Request("approval-cas");
        var pending = await store.SaveAsync(
            new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch),
            null,
            TestContext.Current.CancellationToken);
        var approved = new GameToolApprovalRecord(request, GameToolApprovalStatus.Approved, 1, DateTimeOffset.UnixEpoch, credentialDigest: "digest-a");
        var denied = new GameToolApprovalRecord(request, GameToolApprovalStatus.Denied, 1, DateTimeOffset.UnixEpoch, "denied");

        var results = await Task.WhenAll(
            TrySaveAsync(store, approved, pending.Revision),
            TrySaveAsync(store, denied, pending.Revision));

        Assert.Equal(1, results.Count(value => value));
        var final = await store.ReadAsync(request.Owner, request.ApprovalId, TestContext.Current.CancellationToken);
        Assert.Contains(final!.Status, new[] { GameToolApprovalStatus.Approved, GameToolApprovalStatus.Denied });
    }

    [Fact]
    public async Task CorruptApprovalDocumentFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameToolApprovalStore(directory.Path);
        var request = Request("approval-corrupt");
        await store.SaveAsync(
            new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch),
            null,
            TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.tool-approvals.json"));
        await File.WriteAllTextAsync(file, "{not-json", TestContext.Current.CancellationToken);

        var restarted = new FileGameToolApprovalStore(directory.Path);
        await Assert.ThrowsAsync<PersistenceException>(() => restarted
            .ListAsync(request.Owner, null, 10, TestContext.Current.CancellationToken)
            .AsTask());
    }

    [Fact]
    public async Task BoundedRetentionNeverReclaimsPendingApproval()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameToolApprovalStore(directory.Path, maximumRecordsPerOwner: 2);
        var terminal = Request("terminal");
        await store.SaveAsync(
            new GameToolApprovalRecord(terminal, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch),
            null,
            TestContext.Current.CancellationToken);
        await store.SaveAsync(
            new GameToolApprovalRecord(terminal, GameToolApprovalStatus.Denied, 1, DateTimeOffset.UnixEpoch.AddSeconds(1)),
            0,
            TestContext.Current.CancellationToken);
        var active = Request("active");
        await store.SaveAsync(
            new GameToolApprovalRecord(active, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch.AddSeconds(2)),
            null,
            TestContext.Current.CancellationToken);
        var newest = Request("newest");
        await store.SaveAsync(
            new GameToolApprovalRecord(newest, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UnixEpoch.AddSeconds(3)),
            null,
            TestContext.Current.CancellationToken);

        Assert.Null(await store.ReadAsync(terminal.Owner, terminal.ApprovalId, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.ReadAsync(active.Owner, active.ApprovalId, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.ReadAsync(newest.Owner, newest.ApprovalId, TestContext.Current.CancellationToken));
    }

    private static async Task<bool> TrySaveAsync(
        IGameToolApprovalStore store,
        GameToolApprovalRecord record,
        long expectedRevision)
    {
        try
        {
            await store.SaveAsync(record, expectedRevision, TestContext.Current.CancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static GameToolApprovalRequest Request(string id) => new(
        id,
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
        new GameToolApprovalWorldState("save", 2),
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oga-approval-" + Guid.NewGuid().ToString("N"));
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
