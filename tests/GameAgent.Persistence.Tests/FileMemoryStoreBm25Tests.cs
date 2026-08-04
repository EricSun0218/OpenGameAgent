using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;

namespace GameAgent.Persistence.Tests;

public sealed class FileMemoryStoreBm25Tests
{
    [Fact]
    public async Task Bm25ModeRebuildsFromVerifiedLogWithDiagnostics()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var options = new FileMemoryStoreOptions
        {
            ProviderId = "ranked-file",
            SearchMode = FileMemorySearchMode.Bm25
        };
        try
        {
            await using (var store = new FileMemoryStore(path, options))
            {
                await store.UpsertAsync(
                    Record(
                        "diluted",
                        "\"apple river mountain village road\""),
                    TestContext.Current.CancellationToken);
                await store.UpsertAsync(
                    Record(
                        "frequent",
                        "\"apple apple apple orchard\""),
                    TestContext.Current.CancellationToken);
                for (var index = 0; index < 40; index++)
                {
                    await store.UpsertAsync(
                        Record(
                            "filler-" + index,
                            "\"unrelated memory\""),
                        TestContext.Current.CancellationToken);
                }

                Assert.Equal(FileMemorySearchMode.Bm25, store.SearchMode);
                Assert.Equal("ranked-file", store.ProviderId);
                Assert.Equal(
                    Bm25MemoryStore.IndexIdentity,
                    store.IndexDiagnostics.Identity);
                Assert.Equal(
                    DeterministicUnicodeTokenizer.Version,
                    store.IndexDiagnostics.TokenizerVersion);
                Assert.Equal(42, store.IndexDiagnostics.SourceRevision);
                Assert.Equal(
                    MemoryIndexStatus.Ready,
                    store.IndexDiagnostics.Status);
                Assert.Equal(
                    "frequent",
                    (await Search(store, "\"apple\""))[0]
                    .Record.MemoryId);
            }

            await using var recovered =
                new FileMemoryStore(path, options);
            Assert.Equal(42, recovered.Revision);
            Assert.Equal(
                recovered.Revision,
                recovered.IndexDiagnostics.SourceRevision);
            Assert.Equal(
                "frequent",
                (await Search(recovered, "\"apple\""))[0]
                .Record.MemoryId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Bm25BatchAndIdempotencyRemainConsistentAfterRestart()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var options = new FileMemoryStoreOptions
        {
            SearchMode = FileMemorySearchMode.Bm25
        };
        var mutations = new[]
        {
            MemoryMutation.Delete("old"),
            MemoryMutation.Upsert(Record("new", "\"new signal\""))
        };
        try
        {
            await using (var store = new FileMemoryStore(path, options))
            {
                await store.UpsertAsync(
                    Record("old", "\"old signal\""),
                    TestContext.Current.CancellationToken);
                var first = await store.ApplyIdempotentAtomicBatchAsync(
                    "commit-1",
                    mutations, cancellationToken: TestContext.Current.CancellationToken);
                var duplicate =
                    await store.ApplyIdempotentAtomicBatchAsync(
                        "commit-1",
                        mutations, cancellationToken: TestContext.Current.CancellationToken);

                Assert.All(first, result => Assert.True(result.Changed));
                Assert.All(
                    duplicate,
                    result => Assert.False(result.Changed));
                Assert.Empty(await Search(store, "\"old\""));
                Assert.Equal(
                    "new",
                    Assert.Single(await Search(store, "\"new\""))
                        .Record.MemoryId);
            }

            await using var recovered =
                new FileMemoryStore(path, options);
            var replayed = await recovered.ApplyIdempotentAtomicBatchAsync(
                "commit-1",
                mutations, cancellationToken: TestContext.Current.CancellationToken);
            Assert.All(replayed, result => Assert.False(result.Changed));
            Assert.Equal(
                "new",
                Assert.Single(await Search(recovered, "\"new\""))
                    .Record.MemoryId);
            Assert.Equal(
                recovered.Revision,
                recovered.IndexDiagnostics.SourceRevision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TornTailIsRemovedBeforeBm25IndexRebuild()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var options = new FileMemoryStoreOptions
        {
            SearchMode = FileMemorySearchMode.Bm25
        };
        try
        {
            await using (var store = new FileMemoryStore(path, options))
            {
                await store.UpsertAsync(
                    Record("kept", "\"verified signal\""),
                    TestContext.Current.CancellationToken);
            }

            var committedLength = new FileInfo(path).Length;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.Read))
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.Equal(
                committedLength + 3,
                new FileInfo(path).Length);
            await using var recovered =
                new FileMemoryStore(path, options);

            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                "kept",
                Assert.Single(
                        await Search(recovered, "\"verified\""))
                    .Record.MemoryId);
            Assert.Equal(
                MemoryIndexStatus.Ready,
                recovered.IndexDiagnostics.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptCommittedFrameIsRejectedBeforeBm25Rebuild()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var options = new FileMemoryStoreOptions
        {
            SearchMode = FileMemorySearchMode.Bm25
        };
        try
        {
            await using (var store = new FileMemoryStore(path, options))
            {
                await store.UpsertAsync(
                    Record("never-index", "\"verified signal\""),
                    TestContext.Current.CancellationToken);
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(bytes.Length > 12);
            bytes[12] ^= 0x01;
            await File.WriteAllBytesAsync(path, bytes, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(path, options));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Bm25LimitFailureDoesNotAppendOrFaultFileStore()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var options = new FileMemoryStoreOptions
        {
            SearchMode = FileMemorySearchMode.Bm25,
            Bm25Options = new Bm25MemoryStoreOptions(maxIndexTerms: 1)
        };
        try
        {
            await using var store = new FileMemoryStore(path, options);
            var upsertError =
                await Assert.ThrowsAsync<LexicalSearchLimitException>(
                    () => store.UpsertAsync(
                            Record("too-large", "\"a b\""),
                            TestContext.Current.CancellationToken)
                        .AsTask());
            Assert.Equal(
                LexicalSearchReasonCodes.IndexTermsExceeded,
                upsertError.ReasonCode);
            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Equal(
                MemoryIndexStatus.Ready,
                store.IndexDiagnostics.Status);

            await store.UpsertAsync(Record("kept", "\"a\""), TestContext.Current.CancellationToken);
            var committedLength = new FileInfo(path).Length;
            var batchError =
                await Assert.ThrowsAsync<LexicalSearchLimitException>(
                    () => store.ApplyAtomicBatchAsync(
                            new[]
                            {
                                MemoryMutation.Delete("kept"),
                                MemoryMutation.Upsert(
                                    Record("too-large", "\"b c\""))
                            }, cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
            Assert.Equal(
                LexicalSearchReasonCodes.IndexTermsExceeded,
                batchError.ReasonCode);
            Assert.Equal(1, store.Revision);
            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(
                "kept",
                Assert.Single(await Search(store, "\"a\""))
                    .Record.MemoryId);
            Assert.Equal(
                MemoryIndexStatus.Ready,
                store.IndexDiagnostics.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultSearchModeAndDisposedStatusRemainObservable()
    {
        var directory = TempDirectory();
        var path = System.IO.Path.Combine(directory, "memory.log");
        var store = new FileMemoryStore(path);
        try
        {
            Assert.Equal(
                FileMemorySearchMode.DeterministicLexical,
                store.SearchMode);
            Assert.Equal(
                "deterministic-lexical-memory",
                store.IndexDiagnostics.Identity);
            Assert.Equal(
                MemoryIndexStatus.Ready,
                store.IndexDiagnostics.Status);

            await store.DisposeAsync();
            Assert.Equal(
                MemoryIndexStatus.Disposed,
                store.IndexDiagnostics.Status);
        }
        finally
        {
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<MemorySearchResult[]> Search(
        IMemoryProvider store,
        string query)
    {
        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json(query),
                maxResults: 128,
                maxUtf8Bytes: 1_048_576),
            default);
        return results.ToArray();
    }

    private static MemoryRecord Record(string id, string content)
    {
        return new MemoryRecord(
            id,
            "scope",
            Json(content),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-bm25-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
