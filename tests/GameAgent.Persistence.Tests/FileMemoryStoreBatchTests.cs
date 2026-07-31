using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class FileMemoryStoreBatchTests
{
    [Fact]
    public void BatchMutationResultReadsCountOnceAndNeverEnumerates()
    {
        var mutations =
            new IndexedOnlyMutationResults(
                new MemoryMutationResult(
                    MemoryMutationKind.Upsert,
                    "memory-1",
                    changed: true));

        var result = new MemoryStoreBatchMutationResult(
            revision: 1,
            mutations);

        Assert.Single(result.Mutations);
        Assert.True(result.Changed);
        Assert.Equal(1, mutations.CountReads);
        Assert.False(mutations.EnumeratorAccessed);
    }

    [Fact]
    public async Task MixedBatchCommitsAtOneRevisionAndRecovers()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 Capacity = 2
                             }))
            {
                await store.UpsertAsync(
                    Record("old", """{"version":1}"""),
                    CancellationToken.None);

                var result =
                    await store.ApplyAtomicBatchWithRevisionAsync(
                        new[]
                        {
                            MemoryMutation.Upsert(
                                Record("new-a", """{"version":2}""")),
                            MemoryMutation.Upsert(
                                Record("new-b", """{"version":3}""")),
                            MemoryMutation.Delete("old")
                        },
                        expectedRevision: 1);

                Assert.Equal(2, result.Revision);
                Assert.True(result.Changed);
                Assert.All(result.Mutations, item => Assert.True(item.Changed));
                Assert.Equal(
                    new[] { "new-a", "new-b" },
                    (await SearchAllAsync(store))
                    .Select(item => item.Record.MemoryId)
                    .OrderBy(item => item, StringComparer.Ordinal));
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(2, recovered.Revision);
            Assert.Equal(
                new[] { "new-a", "new-b" },
                (await SearchAllAsync(recovered))
                .Select(item => item.Record.MemoryId)
                .OrderBy(item => item, StringComparer.Ordinal));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task IdempotentBatchDeduplicatesAcrossRestartAndRejectsConflict()
    {
        var path = CreateMemoryPath();
        const string commitId = "runtime-memory-commit-1";
        var firstBatch = new[]
        {
            MemoryMutation.Upsert(Record("same", """{"version":1}"""))
        };
        try
        {
            long committedLength;
            await using (var store = new FileMemoryStore(path))
            {
                var first = await store.ApplyIdempotentAtomicBatchAsync(
                    commitId,
                    firstBatch);
                Assert.True(Assert.Single(first).Changed);
                Assert.Equal(1, store.Revision);
                committedLength = new FileInfo(path).Length;

                var duplicate =
                    await store.ApplyIdempotentAtomicBatchAsync(
                        commitId,
                        firstBatch);
                Assert.False(Assert.Single(duplicate).Changed);
                Assert.Equal(1, store.Revision);
                Assert.Equal(committedLength, new FileInfo(path).Length);
            }

            await using var recovered = new FileMemoryStore(path);
            var replay = await recovered.ApplyIdempotentAtomicBatchAsync(
                commitId,
                firstBatch);
            Assert.False(Assert.Single(replay).Changed);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(committedLength, new FileInfo(path).Length);

            var conflict =
                await Assert.ThrowsAsync<
                    MemoryBatchIdempotencyConflictException>(
                    () => recovered.ApplyIdempotentAtomicBatchAsync(
                            commitId,
                            new[]
                            {
                                MemoryMutation.Upsert(
                                    Record("same", """{"version":2}"""))
                            })
                        .AsTask());
            Assert.Equal(commitId, conflict.CommitId);
            Assert.Equal(
                MemoryBatchReasonCodes.IdempotencyConflict,
                conflict.ReasonCode);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(committedLength, new FileInfo(path).Length);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task DefaultFrameAdmitsRuntimeContentBudgetPlusMetadata()
    {
        var path = CreateMemoryPath();
        var mutations = Enumerable.Range(0, 8)
            .Select(
                index => MemoryMutation.Upsert(
                    Record(
                        "large-" + index,
                        JsonSerializer.Serialize(
                            new
                            {
                                text = new string('x', 65_400)
                            }))))
            .ToArray();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                var results =
                    await store.ApplyIdempotentAtomicBatchAsync(
                        "runtime-default-content-boundary",
                        mutations);
                Assert.Equal(mutations.Length, results.Count);
                Assert.Equal(1, store.Revision);
                Assert.True(
                    new FileInfo(path).Length
                    > 512L * 1_024);
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(8, (await SearchAllAsync(recovered)).Count);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task RecoveryRejectsIdempotentFrameWithForgedPayloadDigest()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                _ = await store.ApplyIdempotentAtomicBatchAsync(
                    "runtime-memory-commit-forged",
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("same", """{"version":1}"""))
                    });
            }

            var frame = await File.ReadAllBytesAsync(path);
            var payloadLength = BitConverter.ToInt32(frame, 4);
            var payload = frame
                .Skip(12)
                .Take(payloadLength)
                .ToArray();
            var text = Encoding.UTF8.GetString(payload);
            const string marker = "\"payloadDigest\":\"";
            var digestStart = text.IndexOf(marker, StringComparison.Ordinal)
                              + marker.Length;
            Assert.True(digestStart >= marker.Length);
            var prefixBytes = Encoding.UTF8.GetByteCount(
                text.Substring(0, digestStart));
            payload[prefixBytes] = payload[prefixBytes] == (byte)'0'
                ? (byte)'1'
                : (byte)'0';
            WriteUInt32(frame, 8, ComputeCrc32(payload));
            payload.CopyTo(frame, 12);
            await File.WriteAllBytesAsync(path, frame);

            Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(path));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task DuplicateIdIsRejectedBeforeBytesOrStateChange()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(path);

            var error =
                await Assert.ThrowsAsync<MemoryBatchValidationException>(
                    () => store.ApplyAtomicBatchWithRevisionAsync(
                            new[]
                            {
                                MemoryMutation.Upsert(
                                    Record("same", """{"version":1}""")),
                                MemoryMutation.Delete("same")
                            })
                        .AsTask());

            Assert.Equal(
                MemoryBatchReasonCodes.DuplicateMemoryId,
                error.ReasonCode);
            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Empty(await SearchAllAsync(store));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CancellationBeforeAdmissionLeavesFileEmpty()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.ApplyAtomicBatchWithRevisionAsync(
                        new[]
                        {
                            MemoryMutation.Upsert(
                                Record("new-a", """{"version":1}""")),
                            MemoryMutation.Upsert(
                                Record("new-b", """{"version":1}"""))
                        },
                        cancellationToken: cancellation.Token)
                    .AsTask());

            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Empty(await SearchAllAsync(store));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CancellationAfterWriteAdmissionDoesNotMakeCommitAmbiguous()
    {
        var path = CreateMemoryPath();
        try
        {
            using var cancellation = new CancellationTokenSource();
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 FaultInjector =
                                     new CancelAtWriteBoundaryFaultInjector(
                                         cancellation)
                             }))
            {
                var result =
                    await store.ApplyAtomicBatchWithRevisionAsync(
                        new[]
                        {
                            MemoryMutation.Upsert(
                                Record("new-a", """{"version":1}""")),
                            MemoryMutation.Upsert(
                                Record("new-b", """{"version":1}"""))
                        },
                        cancellationToken: cancellation.Token);

                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(1, result.Revision);
                Assert.Equal(1, store.Revision);
                Assert.Equal(2, (await SearchAllAsync(store)).Count);
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                new[] { "new-a", "new-b" },
                (await SearchAllAsync(recovered))
                .Select(item => item.Record.MemoryId)
                .OrderBy(item => item, StringComparer.Ordinal));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task BatchRevisionConflictWritesNothing()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(path);
            await store.UpsertAsync(
                Record("existing", """{"version":1}"""),
                CancellationToken.None);
            var committedLength = new FileInfo(path).Length;

            var error =
                await Assert.ThrowsAsync<
                    MemoryStoreRevisionConflictException>(
                    () => store.ApplyAtomicBatchWithRevisionAsync(
                            new[]
                            {
                                MemoryMutation.Delete("existing"),
                                MemoryMutation.Upsert(
                                    Record(
                                        "new",
                                        """{"version":2}"""))
                            },
                            expectedRevision: 0)
                        .AsTask());

            Assert.Equal(0, error.ExpectedRevision);
            Assert.Equal(1, error.ActualRevision);
            Assert.Equal(1, store.Revision);
            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(
                new[] { "existing" },
                (await SearchAllAsync(store))
                .Select(item => item.Record.MemoryId));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task TornBatchTailRecoversNoneOfItsMutations()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var seed = new FileMemoryStore(path))
            {
                await seed.UpsertAsync(
                    Record("existing", """{"version":1}"""),
                    CancellationToken.None);
            }

            var committedLength = new FileInfo(path).Length;
            await using (var faulted = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 FaultInjector =
                                     new PartialFrameFaultInjector()
                             }))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => faulted.ApplyAtomicBatchWithRevisionAsync(
                            new[]
                            {
                                MemoryMutation.Delete("existing"),
                                MemoryMutation.Upsert(
                                    Record(
                                        "new-a",
                                        """{"version":2}""")),
                                MemoryMutation.Upsert(
                                    Record(
                                        "new-b",
                                        """{"version":3}"""))
                            })
                        .AsTask());
                await Assert.ThrowsAsync<MemoryStoreFaultedException>(
                    () => faulted.GetRevisionAsync().AsTask());
            }

            Assert.True(new FileInfo(path).Length > committedLength);
            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                new[] { "existing" },
                (await SearchAllAsync(recovered))
                .Select(item => item.Record.MemoryId));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task ErrorAfterFlushRecoversTheWholeBatch()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var faulted = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 FaultInjector =
                                     new ThrowAfterFlushFaultInjector()
                             }))
            {
                await Assert.ThrowsAsync<InjectedMemoryStoreException>(
                    () => faulted.ApplyAtomicBatchWithRevisionAsync(
                            new[]
                            {
                                MemoryMutation.Upsert(
                                    Record(
                                        "new-a",
                                        """{"version":1}""")),
                                MemoryMutation.Upsert(
                                    Record(
                                        "new-b",
                                        """{"version":1}"""))
                            })
                        .AsTask());
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                new[] { "new-a", "new-b" },
                (await SearchAllAsync(recovered))
                .Select(item => item.Record.MemoryId)
                .OrderBy(item => item, StringComparer.Ordinal));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task EveryBatchCapacityLimitFailsBeforeCommit()
    {
        var cases = new[]
        {
            new CapacityCase(
                new FileMemoryStoreOptions
                {
                    MaxFramePayloadBytes = 1
                },
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes)),
            new CapacityCase(
                new FileMemoryStoreOptions
                {
                    MaxLogBytes = 1
                },
                nameof(FileMemoryStoreOptions.MaxLogBytes))
        };

        foreach (var capacityCase in cases)
        {
            var path = CreateMemoryPath();
            try
            {
                await using var store = new FileMemoryStore(
                    path,
                    capacityCase.Options);
                var error = await Assert.ThrowsAsync<
                    MemoryStoreCapacityExceededException>(
                    () => store.ApplyAtomicBatchWithRevisionAsync(
                            new[]
                            {
                                MemoryMutation.Upsert(
                                    Record("a", """{"value":1}""")),
                                MemoryMutation.Upsert(
                                    Record("b", """{"value":2}"""))
                            })
                        .AsTask());
                Assert.Equal(capacityCase.LimitName, error.LimitName);
                Assert.Equal(0, store.Revision);
                Assert.Equal(0, new FileInfo(path).Length);
            }
            finally
            {
                DeleteMemoryDirectory(path);
            }
        }

        var framePath = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(
                framePath,
                new FileMemoryStoreOptions
                {
                    MaxMutationFrames = 1
                });
            await store.ApplyAtomicBatchWithRevisionAsync(
                new[]
                {
                    MemoryMutation.Upsert(
                        Record("a", """{"value":1}""")),
                    MemoryMutation.Upsert(
                        Record("b", """{"value":2}"""))
                });
            var committedLength = new FileInfo(framePath).Length;

            var error = await Assert.ThrowsAsync<
                MemoryStoreCapacityExceededException>(
                () => store.ApplyAtomicBatchWithRevisionAsync(
                        new[]
                        {
                            MemoryMutation.Delete("a"),
                            MemoryMutation.Delete("b")
                        })
                    .AsTask());
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxMutationFrames),
                error.LimitName);
            Assert.Equal(1, store.Revision);
            Assert.Equal(committedLength, new FileInfo(framePath).Length);
            Assert.Equal(2, (await SearchAllAsync(store)).Count);
        }
        finally
        {
            DeleteMemoryDirectory(framePath);
        }

        var recordPath = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(
                recordPath,
                new FileMemoryStoreOptions { Capacity = 1 });
            var error = await Assert.ThrowsAsync<
                RuntimeContentLimitException>(
                () => store.ApplyAtomicBatchWithRevisionAsync(
                        new[]
                        {
                            MemoryMutation.Upsert(
                                Record("a", """{"value":1}""")),
                            MemoryMutation.Upsert(
                                Record("b", """{"value":2}"""))
                        })
                    .AsTask());
            Assert.Equal("memory_capacity_exceeded", error.LimitCode);
            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(recordPath).Length);
        }
        finally
        {
            DeleteMemoryDirectory(recordPath);
        }
    }

    [Fact]
    public async Task ReopenWithLowerLimitsFailsAsCapacityNotCorruption()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.ApplyAtomicBatchWithRevisionAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("a", """{"value":1}""")),
                        MemoryMutation.Upsert(
                            Record("b", """{"value":2}"""))
                    });
                await store.ApplyAtomicBatchWithRevisionAsync(
                    new[]
                    {
                        MemoryMutation.Delete("a"),
                        MemoryMutation.Upsert(
                            Record("c", """{"value":3}"""))
                    });
            }

            var originalLength = new FileInfo(path).Length;
            var payloadError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxFramePayloadBytes = 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                payloadError.LimitName);
            Assert.Equal(originalLength, new FileInfo(path).Length);

            var frameError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxMutationFrames = 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxMutationFrames),
                frameError.LimitName);
            Assert.Equal(originalLength, new FileInfo(path).Length);

            var logError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxLogBytes = originalLength - 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxLogBytes),
                logError.LimitName);
            Assert.Equal(originalLength, new FileInfo(path).Length);

            var recordError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            Capacity = 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.Capacity),
                recordError.LimitName);
            Assert.Equal(2, recordError.Attempted);
            Assert.Equal(originalLength, new FileInfo(path).Length);

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(2, recovered.Revision);
            Assert.Equal(
                new[] { "b", "c" },
                (await SearchAllAsync(recovered))
                .Select(item => item.Record.MemoryId)
                .OrderBy(item => item, StringComparer.Ordinal));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task NoOpDeletesDoNotConsumeARevisionOrFrame()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(path);
            var result = await store.ApplyAtomicBatchWithRevisionAsync(
                new[]
                {
                    MemoryMutation.Delete("missing-a"),
                    MemoryMutation.Delete("missing-b")
                },
                expectedRevision: 0);

            Assert.False(result.Changed);
            Assert.All(
                result.Mutations,
                mutation => Assert.False(mutation.Changed));
            Assert.Equal(0, result.Revision);
            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task RecoveredBatchReappliesAggregateContentBound()
    {
        var path = CreateMemoryPath();
        try
        {
            var payloadBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(payloadBuffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 1);
                writer.WriteNumber("revision", 1);
                writer.WriteString("operation", "batch");
                writer.WritePropertyName("mutations");
                writer.WriteStartArray();
                var largeValue = new string('x', 60_000);
                for (var index = 0; index < 70; index++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("operation", "upsert");
                    writer.WritePropertyName("record");
                    writer.WriteStartObject();
                    writer.WriteString("memoryId", $"large-{index}");
                    writer.WriteString("scope", "shared");
                    writer.WritePropertyName("content");
                    writer.WriteStartObject();
                    writer.WriteString("first", largeValue);
                    writer.WriteString("second", largeValue);
                    writer.WriteEndObject();
                    writer.WritePropertyName("tags");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                    writer.WriteNumber("importance", 50);
                    writer.WriteString(
                        "createdAt",
                        DateTimeOffset.UnixEpoch);
                    writer.WriteString(
                        "updatedAt",
                        DateTimeOffset.UnixEpoch);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var payload = payloadBuffer.WrittenSpan.ToArray();
            var frame = new byte[checked(12 + payload.Length + 4)];
            WriteUInt32(frame, 0, 0x314D4147);
            WriteUInt32(frame, 4, checked((uint)payload.Length));
            WriteUInt32(frame, 8, ComputeCrc32(payload));
            payload.CopyTo(frame.AsSpan(12));
            WriteUInt32(frame, 12 + payload.Length, 0x54494D43);
            await File.WriteAllBytesAsync(path, frame);

            var error = Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(
                    path,
                    new FileMemoryStoreOptions
                    {
                        Capacity = 100,
                        MaxFramePayloadBytes = payload.Length
                    }));
            Assert.Contains(
                MemoryBatchReasonCodes.AggregateContentBytesExceeded,
                error.Message,
                StringComparison.Ordinal);
            Assert.Equal(frame.Length, new FileInfo(path).Length);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    private static async Task<IReadOnlyList<MemorySearchResult>>
        SearchAllAsync(IMemoryProvider store)
    {
        return await store.SearchAsync(
            new MemoryQuery(
                "shared",
                Json("{}"),
                maxResults: 128,
                maxUtf8Bytes: 1_048_576),
            CancellationToken.None);
    }

    private static MemoryRecord Record(string id, string content)
    {
        var timestamp = new DateTimeOffset(
            2026,
            7,
            30,
            0,
            0,
            0,
            TimeSpan.Zero);
        return new MemoryRecord(
            id,
            "shared",
            Json(content),
            Array.Empty<string>(),
            50,
            timestamp,
            timestamp);
    }

    private static JsonElement Json(string value)
    {
        return ProtocolJson.ParseElement(value);
    }

    private sealed class IndexedOnlyMutationResults
        : IReadOnlyList<MemoryMutationResult>
    {
        private readonly MemoryMutationResult[] _items;

        public IndexedOnlyMutationResults(
            params MemoryMutationResult[] items)
        {
            _items = items;
        }

        public int CountReads { get; private set; }

        public bool EnumeratorAccessed { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return _items.Length;
            }
        }

        public MemoryMutationResult this[int index] => _items[index];

        public IEnumerator<MemoryMutationResult> GetEnumerator()
        {
            EnumeratorAccessed = true;
            throw new InvalidOperationException(
                "Enumeration is not supported.");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private static string CreateMemoryPath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-memory-batch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, "memories.gam");
    }

    private static void DeleteMemoryDirectory(string memoryPath)
    {
        var directory = System.IO.Path.GetDirectoryName(memoryPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteUInt32(
        byte[] buffer,
        int offset,
        uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ComputeCrc32(byte[] value)
    {
        const uint polynomial = 0xEDB88320;
        var checksum = uint.MaxValue;
        foreach (var item in value)
        {
            var current = (checksum ^ item) & 0xFF;
            for (var bit = 0; bit < 8; bit++)
            {
                current = (current & 1) == 0
                    ? current >> 1
                    : current >> 1 ^ polynomial;
            }

            checksum = current ^ checksum >> 8;
        }

        return ~checksum;
    }

    private sealed class PartialFrameFaultInjector : IJournalFaultInjector
    {
        public int GetWriteLength(int frameLength)
        {
            return Math.Max(1, frameLength / 2);
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }

    private sealed class ThrowAfterFlushFaultInjector : IJournalFaultInjector
    {
        public int GetWriteLength(int frameLength)
        {
            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
            if (stage == JournalWriteStage.AfterFlush)
            {
                throw new InjectedMemoryStoreException();
            }
        }
    }

    private sealed class CancelAtWriteBoundaryFaultInjector
        : IJournalFaultInjector
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelAtWriteBoundaryFaultInjector(
            CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int GetWriteLength(int frameLength)
        {
            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
            if (stage == JournalWriteStage.BeforeWrite)
            {
                _cancellation.Cancel();
            }
        }
    }

    private sealed class InjectedMemoryStoreException : IOException
    {
    }

    private sealed record CapacityCase(
        FileMemoryStoreOptions Options,
        string LimitName);
}
