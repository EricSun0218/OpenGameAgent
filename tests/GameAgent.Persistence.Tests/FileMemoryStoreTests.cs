using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class FileMemoryStoreTests
{
    [Fact]
    public async Task FullRecordRecoversAcrossRestartAndDeletePersists()
    {
        var path = CreateMemoryPath();
        var now = new DateTimeOffset(
            2026,
            7,
            30,
            4,
            0,
            0,
            TimeSpan.Zero);
        var observer = new GameEntityIdentity("npc-1", 2);
        var source = new GameEntityIdentity("npc-2", 7);
        var record = new MemoryRecord(
            "memory-1",
            "npc:npc-1",
            Json("""{"fact":"bridge closed","confidence":0.8}"""),
            new[] { "bridge", "warning" },
            91,
            now,
            now.AddMinutes(1),
            now.AddDays(2),
            new MemoryProvenance(
                "world-1",
                "save-slot-1",
                12,
                "run-1",
                "event-1",
                committed: true,
                "timeline-main",
                new GameKnowledgePerspective(
                    observer,
                    "report",
                    source)),
            new GameTimeWindow(
                new GameTimePoint(
                    "world-clock",
                    "timeline-main",
                    3,
                    100),
                new GameTimePoint(
                    "world-clock",
                    "timeline-main",
                    3,
                    200)));

        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                var result = await store.UpsertAtomicAsync(
                    record,
                    expectedRevision: 0);
                Assert.True(result.Changed);
                Assert.Equal(1, result.Revision);
            }

            await using (var recovered = new FileMemoryStore(path))
            {
                Assert.Equal(1, await recovered.GetRevisionAsync());
                var found = Assert.Single(
                    await recovered.SearchAsync(
                        new MemoryQuery(
                            "npc:npc-1",
                            Json("{}"),
                            now: now.AddHours(1),
                            worldId: "world-1",
                            sessionId: "save-slot-1",
                            maximumSaveRevision: 12,
                            requireCommittedProvenance: true,
                            timelineId: "timeline-main",
                            observer: observer,
                            gameTime: new GameTimePoint(
                                "world-clock",
                                "timeline-main",
                                3,
                                150)),
                        CancellationToken.None));

                Assert.Equal(91, found.Record.Importance);
                Assert.Equal(
                    "bridge closed",
                    found.Record.Content.GetProperty("fact").GetString());
                Assert.Equal(
                    new[] { "bridge", "warning" },
                    found.Record.Tags);
                Assert.Equal(
                    "report",
                    found.Record.Provenance!.Perspective!.KnowledgeKind);
                Assert.True(
                    found.Record.Provenance.Perspective.Observer
                        .IsSameIncarnation(observer));
                Assert.True(
                    found.Record.Provenance.Perspective.Source!
                        .IsSameIncarnation(source));
                Assert.Equal(
                    100,
                    found.Record.GameTimeWindow!.ValidFrom!.Tick);

                var deleted = await recovered.DeleteAtomicAsync(
                    "memory-1",
                    expectedRevision: 1);
                Assert.True(deleted.Changed);
                Assert.Equal(2, deleted.Revision);
            }

            await using var afterDelete = new FileMemoryStore(path);
            Assert.Equal(2, afterDelete.Revision);
            Assert.Empty(
                await afterDelete.SearchAsync(
                    new MemoryQuery("npc:npc-1", Json("{}")),
                    CancellationToken.None));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CjkSubstringRecallSurvivesRestart()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record(
                        "north-bridge",
                        """{"description":"北桥已经关闭，禁止通行。"}"""),
                    CancellationToken.None);
                await store.UpsertAsync(
                    Record(
                        "south-gate",
                        """{"description":"南门仍然开放。"}"""),
                    CancellationToken.None);
            }

            await using var recovered = new FileMemoryStore(path);
            var results = await recovered.SearchAsync(
                new MemoryQuery(
                    "shared",
                    Json("""{"search":"北桥"}""")),
                CancellationToken.None);

            Assert.Equal(
                new[] { "north-bridge" },
                results.Select(item => item.Record.MemoryId));
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task ConcurrentMutationsAreSerializedAndRecoverWithoutGaps()
    {
        var path = CreateMemoryPath();
        try
        {
            MemoryStoreMutationResult[] mutations;
            await using (var store = new FileMemoryStore(path))
            {
                mutations = await Task.WhenAll(
                    Enumerable.Range(0, 32)
                        .Select(
                            index => store.UpsertAtomicAsync(
                                    Record(
                                        $"memory-{index}",
                                        $$"""{"index":{{index}}}"""))
                                .AsTask()));

                Assert.Equal(
                    Enumerable.Range(1, 32).Select(value => (long)value),
                    mutations.Select(item => item.Revision).OrderBy(x => x));
                Assert.Equal(32, await store.GetRevisionAsync());
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(32, recovered.Revision);
            Assert.Equal(
                32,
                (await recovered.SearchAsync(
                    new MemoryQuery(
                        "shared",
                        Json("{}"),
                        maxResults: 128,
                        maxUtf8Bytes: 1_048_576),
                    CancellationToken.None)).Count);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CompareAndSwapAllowsOnlyOneConcurrentWriter()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(path);
            var first = CaptureAsync(
                store.UpsertAtomicAsync(
                        Record("memory-a", """{"value":"a"}"""),
                        expectedRevision: 0)
                    .AsTask());
            var second = CaptureAsync(
                store.UpsertAtomicAsync(
                        Record("memory-b", """{"value":"b"}"""),
                        expectedRevision: 0)
                    .AsTask());

            var outcomes = await Task.WhenAll(first, second);
            Assert.Single(outcomes, item => item.Result is not null);
            var conflict = Assert.Single(
                outcomes,
                item => item.Exception is not null).Exception;
            var revisionConflict =
                Assert.IsType<MemoryStoreRevisionConflictException>(conflict);
            Assert.Equal(0, revisionConflict.ExpectedRevision);
            Assert.Equal(1, revisionConflict.ActualRevision);
            Assert.Equal(1, store.Revision);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task TornFinalFrameIsTruncatedAndPriorMemoryRecovers()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record("committed", """{"state":"safe"}"""),
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
                    () => faulted.UpsertAsync(
                            Record("torn", """{"state":"uncertain"}"""),
                            CancellationToken.None)
                        .AsTask());
                await Assert.ThrowsAsync<MemoryStoreFaultedException>(
                    () => faulted.GetRevisionAsync().AsTask());
            }

            Assert.True(new FileInfo(path).Length > committedLength);
            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(committedLength, new FileInfo(path).Length);
            Assert.Equal(1, recovered.Revision);
            var found = await recovered.SearchAsync(
                new MemoryQuery("shared", Json("{}")),
                CancellationToken.None);
            Assert.Equal("committed", Assert.Single(found).Record.MemoryId);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task FailureAfterFlushRecoversTheWholeCommittedMutation()
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
                    () => faulted.UpsertAsync(
                            Record(
                                "committed-before-error",
                                """{"state":"committed"}"""),
                            CancellationToken.None)
                        .AsTask());
                await Assert.ThrowsAsync<MemoryStoreFaultedException>(
                    () => faulted.GetRevisionAsync().AsTask());
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                "committed-before-error",
                Assert.Single(
                        await recovered.SearchAsync(
                            new MemoryQuery("shared", Json("{}")),
                            CancellationToken.None))
                    .Record.MemoryId);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CommittedFrameCorruptionFailsClosed()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record("memory-1", """{"value":"intact"}"""),
                    CancellationToken.None);
            }

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length > 24);
            bytes[20] ^= 0x5A;
            await File.WriteAllBytesAsync(path, bytes);

            var error = Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(path));
            Assert.Equal(0, error.Offset);
            Assert.Contains(
                "checksum",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task ChecksummedButInvalidRecordFailsClosed()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record("memory-1", """{"value":"intact"}"""),
                    CancellationToken.None);
            }

            var bytes = await File.ReadAllBytesAsync(path);
            var payloadLength = ReadInt32(bytes, 4);
            var payload = Encoding.UTF8.GetString(
                bytes,
                12,
                payloadLength);
            const string valid = "\"importance\":50";
            const string invalid = "\"importance\":-1";
            Assert.Contains(valid, payload, StringComparison.Ordinal);
            payload = payload.Replace(
                valid,
                invalid,
                StringComparison.Ordinal);
            var invalidPayload = Encoding.UTF8.GetBytes(payload);
            Assert.Equal(payloadLength, invalidPayload.Length);
            Buffer.BlockCopy(
                invalidPayload,
                0,
                bytes,
                12,
                invalidPayload.Length);
            WriteUInt32(bytes, 8, ComputeCrc32(invalidPayload));
            await File.WriteAllBytesAsync(path, bytes);

            var error = Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(path));
            Assert.Equal(0, error.Offset);
            Assert.Contains(
                "importance",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task ExplicitNullForRequiredCollectionFailsClosed()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record("memory-1", """{"value":"intact"}"""),
                    CancellationToken.None);
            }

            var original = await File.ReadAllBytesAsync(path);
            var payloadLength = ReadInt32(original, 4);
            var payload = Encoding.UTF8.GetString(
                original,
                12,
                payloadLength);
            Assert.Contains("\"tags\":[]", payload, StringComparison.Ordinal);
            payload = payload.Replace(
                "\"tags\":[]",
                "\"tags\":null",
                StringComparison.Ordinal);
            var invalidPayload = Encoding.UTF8.GetBytes(payload);
            var invalidFrame = new byte[12 + invalidPayload.Length + 4];
            Buffer.BlockCopy(original, 0, invalidFrame, 0, 4);
            WriteUInt32(
                invalidFrame,
                4,
                checked((uint)invalidPayload.Length));
            WriteUInt32(
                invalidFrame,
                8,
                ComputeCrc32(invalidPayload));
            Buffer.BlockCopy(
                invalidPayload,
                0,
                invalidFrame,
                12,
                invalidPayload.Length);
            Buffer.BlockCopy(
                original,
                original.Length - 4,
                invalidFrame,
                invalidFrame.Length - 4,
                4);
            await File.WriteAllBytesAsync(path, invalidFrame);

            var error = Assert.Throws<MemoryStoreCorruptionException>(
                () => new FileMemoryStore(path));
            Assert.Contains(
                "tags",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task CapacityAllowsReplacementButRejectsNewRecord()
    {
        var path = CreateMemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions { Capacity = 1 }))
            {
                await store.UpsertAsync(
                    Record("memory-1", """{"version":1}"""),
                    CancellationToken.None);
                await store.UpsertAsync(
                    Record("memory-1", """{"version":2}"""),
                    CancellationToken.None);

                var error = await Assert.ThrowsAsync<
                    RuntimeContentLimitException>(
                    () => store.UpsertAsync(
                            Record("memory-2", """{"version":1}"""),
                            CancellationToken.None)
                        .AsTask());
                Assert.Equal("memory_capacity_exceeded", error.LimitCode);
                Assert.Equal(2, store.Revision);
            }

            await using var recovered = new FileMemoryStore(
                path,
                new FileMemoryStoreOptions { Capacity = 1 });
            var found = Assert.Single(
                await recovered.SearchAsync(
                    new MemoryQuery("shared", Json("{}")),
                    CancellationToken.None));
            Assert.Equal(
                2,
                found.Record.Content.GetProperty("version").GetInt32());
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task DefaultFrameLimitAdmitsNearMaximumRecordContent()
    {
        var path = CreateMemoryPath();
        var timestamp = new DateTimeOffset(
            2026,
            7,
            30,
            0,
            0,
            0,
            TimeSpan.Zero);
        var content = JsonSerializer.SerializeToElement(
            new
            {
                first = new string('a', 60_000),
                second = new string('b', 60_000)
            });
        var tags = Enumerable.Range(0, 64)
            .Select(index =>
                new string('t', 125) + index.ToString("D3"))
            .ToArray();
        var record = new MemoryRecord(
            new string('m', 128),
            new string('s', 256),
            content,
            tags,
            100,
            timestamp,
            timestamp,
            provenance: new MemoryProvenance(
                new string('w', 128),
                new string('q', 128),
                long.MaxValue,
                new string('r', 128),
                new string('e', 128),
                committed: true,
                new string('l', 128)));

        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(record, CancellationToken.None);
                Assert.Equal(1, store.Revision);
            }

            await using var recovered = new FileMemoryStore(path);
            var found = Assert.Single(
                await recovered.SearchAsync(
                    new MemoryQuery(
                        new string('s', 256),
                        Json("{}"),
                        maxUtf8Bytes: 1_048_576),
                    CancellationToken.None));
            Assert.Equal(120_000, checked(
                found.Record.Content.GetProperty("first")
                    .GetString()!.Length
                + found.Record.Content.GetProperty("second")
                    .GetString()!.Length));
            Assert.Equal(64, found.Record.Tags.Count);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public async Task PayloadAndMemoryIdBoundariesFailBeforeCommit()
    {
        var path = CreateMemoryPath();
        try
        {
            await using var store = new FileMemoryStore(
                path,
                new FileMemoryStoreOptions
                {
                    MaxFramePayloadBytes = 1
                });
            var error = await Assert.ThrowsAsync<
                MemoryStoreCapacityExceededException>(
                () => store.UpsertAsync(
                        Record(
                            new string('\u00e9', 64),
                            """{"value":1}"""),
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                error.LimitName);
            Assert.Equal(0, store.Revision);
            Assert.Equal(0, new FileInfo(path).Length);

            Assert.False(
                await store.DeleteAsync(
                    new string('\u00e9', 64),
                    CancellationToken.None));
            await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => store.DeleteAsync(
                        new string('\u00e9', 65),
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal(0, store.Revision);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    [Fact]
    public void ASecondWriterCannotOpenTheSameStore()
    {
        var path = CreateMemoryPath();
        try
        {
            using (var first = new FileMemoryStore(path))
            {
                Assert.Throws<IOException>(
                    () => new FileMemoryStore(path));
            }

            using var reopened = new FileMemoryStore(path);
            Assert.Equal(System.IO.Path.GetFullPath(path), reopened.Path);
        }
        finally
        {
            DeleteMemoryDirectory(path);
        }
    }

    private static async Task<MutationOutcome> CaptureAsync(
        Task<MemoryStoreMutationResult> task)
    {
        try
        {
            return new MutationOutcome(await task, null);
        }
        catch (Exception exception)
        {
            return new MutationOutcome(null, exception);
        }
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

    private static string CreateMemoryPath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-memory-tests",
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

    private static int ReadInt32(byte[] buffer, int offset)
    {
        return unchecked((int)(
            (uint)(
                buffer[offset]
                | buffer[offset + 1] << 8
                | buffer[offset + 2] << 16
                | buffer[offset + 3] << 24)));
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
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

    private sealed class InjectedMemoryStoreException : IOException
    {
    }

    private sealed record MutationOutcome(
        MemoryStoreMutationResult? Result,
        Exception? Exception);
}
