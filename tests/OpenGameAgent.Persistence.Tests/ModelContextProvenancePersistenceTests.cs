using OpenGameAgent.Extensions;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class ModelContextProvenancePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oga-provenance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTripsAcrossRestartAndDeduplicatesStableEntryIds()
    {
        var key = new GameSessionKey("session", "actor");
        var entry = new GameModelContextProvenanceEntry(
            "entry-1",
            key,
            "input",
            "run",
            1,
            "model-request",
            "{\"model\":\"local\"}",
            DateTimeOffset.Parse("2026-08-23T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind));
        var first = new FileGameModelContextProvenanceStore(_root);
        await first.AppendAsync(entry, TestContext.Current.CancellationToken);
        await first.AppendAsync(entry, TestContext.Current.CancellationToken);

        var restarted = new FileGameModelContextProvenanceStore(_root);
        var loaded = await restarted.ListAsync(key, "input", 10, TestContext.Current.CancellationToken);

        var value = Assert.Single(loaded);
        Assert.Equal(entry.EntryId, value.EntryId);
        Assert.Equal(entry.DetailsJson, value.DetailsJson);
        await Assert.ThrowsAsync<PersistenceException>(() => restarted.AppendAsync(
            new GameModelContextProvenanceEntry(
                "entry-1",
                key,
                "input",
                "run",
                1,
                "model-request",
                "{\"model\":\"different\"}",
                entry.OperationalTimestamp),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task CorruptionAndStorageIdentityMismatchFailClosed()
    {
        var key = new GameSessionKey("session", "actor");
        var store = new FileGameModelContextProvenanceStore(_root);
        await store.AppendAsync(
            new GameModelContextProvenanceEntry(
                "entry",
                key,
                "input",
                "run",
                1,
                "model-request",
                "{}",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(_root, "*.jsonl", SearchOption.AllDirectories));
        var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            file,
            json.Replace("\"actorId\":\"actor\"", "\"actorId\":\"other\"", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(() => store.ListAsync(
            key,
            null,
            10,
            TestContext.Current.CancellationToken).AsTask());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
