using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class ConversationContextCheckpointTests
{
    [Fact]
    public async Task NewManagerRestoresExactDerivedViewWithoutCompactingAgain()
    {
        var source = CompactionTranscript();
        var firstCompactor = new CountingCompactor();
        var firstManager = Manager(firstCompactor);
        var first = await firstManager.PrepareAsync(
            "run-checkpoint",
            "turn-1",
            source,
            new[] { "stable" });

        Assert.True(first.Report.Compacted);
        Assert.Equal(1, firstCompactor.Calls);
        var checkpoint = first.CreateCheckpoint("run-checkpoint");

        var payload = checkpoint.GetProperty("payload");
        Assert.Equal(
            first.Messages.Select(message => message.MessageId),
            payload.GetProperty("outputMessageIds")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            JsonValueKind.Object,
            payload.GetProperty("derivedSummary").ValueKind);
        Assert.False(
            payload.TryGetProperty("transcript", out _));

        var restoredCompactor = new CountingCompactor();
        var restoredManager = Manager(restoredCompactor);
        restoredManager.RegisterCheckpoint(checkpoint);
        var restored = await restoredManager.PrepareAsync(
            "run-checkpoint",
            "turn-2",
            source,
            new[] { "stable" });

        Assert.Equal(0, restoredCompactor.Calls);
        Assert.Equal(
            first.Messages.Select(Encoded),
            restored.Messages.Select(Encoded));
        Assert.Equal(
            first.Report.SourceDigest,
            restored.Report.SourceDigest);
        Assert.Equal(
            first.Report.ViewDigest,
            restored.Report.ViewDigest);
        Assert.Equal(
            first.Report.OutputUtf8Bytes,
            restored.Report.OutputUtf8Bytes);
        restored.Messages[0].Role = NormalizedRoles.User;
        Assert.Equal(
            NormalizedRoles.Assistant,
            source[0].Role);
    }

    [Fact]
    public async Task TamperDuplicateMissingAndSourceMismatchFailClosed()
    {
        var source = CompactionTranscript();
        var prepared = await Manager(new CountingCompactor())
            .PrepareAsync(
                "run-tamper",
                "turn-1",
                source,
                new[] { "stable" });
        var checkpoint = prepared.CreateCheckpoint("run-tamper");

        var integrityTamper = Mutate(
            checkpoint,
            payload => payload["viewDigest"] = new string('0', 64),
            refreshIntegrity: false);
        Assert.Throws<InvalidDataException>(
            () => Manager(new CountingCompactor())
                .RegisterCheckpoint(integrityTamper));

        var duplicate = Mutate(
            checkpoint,
            payload =>
            {
                var ids = payload["outputMessageIds"]!.AsArray();
                ids[1] = ids[0]!.GetValue<string>();
            });
        Assert.Throws<InvalidDataException>(
            () => Manager(new CountingCompactor())
                .RegisterCheckpoint(duplicate));

        var invalidAudit = Mutate(
            checkpoint,
            payload =>
            {
                var summary = payload["derivedSummary"]!.AsObject();
                var part = summary["parts"]!.AsArray()[0]!.AsObject();
                var envelope = part["json"]!.AsObject();
                var contract = JsonNode.Parse(
                    envelope["summary"]!.GetValue<string>())!.AsObject();
                contract["audit"]!.AsObject()["envelopeUtf8Bytes"] = 1;
                envelope["summary"] = contract.ToJsonString();
            });
        var invalidAuditCompactor = new CountingCompactor();
        var invalidAuditManager = Manager(invalidAuditCompactor);
        invalidAuditManager.RegisterCheckpoint(invalidAudit);
        var invalidAuditRecomputed =
            await invalidAuditManager.PrepareAsync(
                "run-tamper",
                "turn-2",
                source,
                new[] { "stable" });
        Assert.Equal(1, invalidAuditCompactor.Calls);
        Assert.True(invalidAuditRecomputed.Report.Compacted);

        var missing = Mutate(
            checkpoint,
            payload =>
            {
                var ids = payload["outputMessageIds"]!.AsArray();
                ids[ids.Count - 1] = "missing-from-transcript";
            });
        var missingCompactor = new CountingCompactor();
        var missingManager = Manager(missingCompactor);
        missingManager.RegisterCheckpoint(missing);
        var missingRecomputed = await missingManager.PrepareAsync(
            "run-tamper",
            "turn-2",
            source,
            new[] { "stable" });
        Assert.Equal(1, missingCompactor.Calls);
        Assert.True(missingRecomputed.Report.Compacted);

        var changedSource = CompactionTranscript();
        changedSource[2].Parts[0].Text = "source changed";
        var mismatchCompactor = new CountingCompactor();
        var mismatchManager = Manager(mismatchCompactor);
        mismatchManager.RegisterCheckpoint(checkpoint);
        var mismatchRecomputed = await mismatchManager.PrepareAsync(
            "run-tamper",
            "turn-2",
            changedSource,
            new[] { "stable" });
        Assert.Equal(1, mismatchCompactor.Calls);
        Assert.NotEqual(
            prepared.Report.SourceDigest,
            mismatchRecomputed.Report.SourceDigest);
    }

    [Fact]
    public async Task CheckpointEncodingHonorsProtocolExtensionBoundaries()
    {
        var manager = LargeViewManager();
        var admitted = await manager.PrepareAsync(
            "run-near-limit",
            "turn-1",
            ManyMessages(1_800, 110));
        var checkpoint = admitted.CreateCheckpoint("run-near-limit");
        var bytes = Encoding.UTF8.GetByteCount(checkpoint.GetRawText());

        Assert.InRange(
            bytes,
            180_000,
            ProtocolLimits.MaxProtocolJsonUtf8Bytes);
        Assert.Equal(
            bytes,
            JsonValueInspector.ValidateAndMeasure(
                checkpoint,
                new JsonValueLimits(
                    ProtocolLimits.MaxProtocolJsonUtf8Bytes,
                    ProtocolLimits.MaxProtocolJsonDepth,
                    ProtocolLimits.MaxProtocolJsonNodes,
                    ProtocolLimits.MaxProtocolJsonStringUtf8Bytes,
                    ProtocolLimits.MaxProtocolJsonContainerItems),
                nameof(checkpoint)));

        var restoredCompactor = new CountingCompactor();
        var restoredManager = LargeViewManager(restoredCompactor);
        restoredManager.RegisterCheckpoint(checkpoint);
        var restored = await restoredManager.PrepareAsync(
            "run-near-limit",
            "turn-2",
            ManyMessages(1_800, 110));
        Assert.Equal(0, restoredCompactor.Calls);
        Assert.Equal(
            admitted.Report.ViewDigest,
            restored.Report.ViewDigest);

        var oversized = await LargeViewManager().PrepareAsync(
            "run-over-limit",
            "turn-1",
            ManyMessages(2_048, 128));
        var error = Assert.Throws<RuntimeContentLimitException>(
            () => oversized.CreateCheckpoint("run-over-limit"));
        Assert.Equal("json_bytes_exceeded", error.LimitCode);
    }

    [Fact]
    public async Task SuccessfulRestoreIsOneShotAndCannotExhaustRegistry()
    {
        var source = CompactionTranscript();
        var seed = await Manager(new CountingCompactor()).PrepareAsync(
            "run-seed",
            "turn-1",
            source,
            new[] { "stable" });
        var restoredCompactor = new CountingCompactor();
        var manager = Manager(restoredCompactor);

        for (var index = 0; index <= 4_096; index++)
        {
            var runId = $"run-consumed-{index}";
            manager.RegisterCheckpoint(seed.CreateCheckpoint(runId));
            var restored = await manager.PrepareAsync(
                runId,
                "turn-2",
                source,
                new[] { "stable" });

            Assert.Equal(seed.Report.ViewDigest, restored.Report.ViewDigest);
        }

        Assert.Equal(0, restoredCompactor.Calls);
        Assert.Equal(0, manager.RegisteredCheckpointCount);

        var recomputed = await manager.PrepareAsync(
            "run-consumed-0",
            "turn-3",
            source,
            new[] { "stable" });
        Assert.True(recomputed.Report.Compacted);
        Assert.Equal(1, restoredCompactor.Calls);
    }

    [Fact]
    public async Task StopClearsRegisteredCheckpoints()
    {
        var source = CompactionTranscript();
        var seed = await Manager(new CountingCompactor()).PrepareAsync(
            "run-stop-seed",
            "turn-1",
            source,
            new[] { "stable" });
        var manager = Manager(new CountingCompactor());
        for (var index = 0; index < 32; index++)
        {
            manager.RegisterCheckpoint(
                seed.CreateCheckpoint($"run-stop-{index}"));
        }

        Assert.Equal(32, manager.RegisteredCheckpointCount);
        Assert.True(await manager.StopAsync());
        Assert.Equal(0, manager.RegisteredCheckpointCount);
        Assert.Throws<ObjectDisposedException>(
            () => manager.RegisterCheckpoint(
                seed.CreateCheckpoint("run-after-stop")));
    }

    private static ConversationContextManager Manager(
        IConversationCompactor compactor)
    {
        return new ConversationContextManager(
            new ConversationContextOptions
            {
                MaxRequestMessages = 8,
                MaxRequestUtf8Bytes = 4_096,
                MaxInputMessages = 128,
                MaxInputUtf8Bytes = 1_048_576,
                MaxInputJsonNodes = 16_384,
                RecentMessagesToKeep = 2,
                MaxSummaryUtf8Bytes = 3_072,
                CompactionTimeout = TimeSpan.FromSeconds(1),
                FailureCooldown = TimeSpan.Zero
            },
            compactor,
            new FakeRuntimeClock());
    }

    private static ConversationContextManager LargeViewManager(
        IConversationCompactor? compactor = null)
    {
        return new ConversationContextManager(
            new ConversationContextOptions
            {
                MaxRequestMessages = 2_048,
                MaxRequestUtf8Bytes = 4 * 1_048_576,
                MaxInputMessages = 4_096,
                MaxInputUtf8Bytes = 8 * 1_048_576,
                MaxInputJsonNodes = 65_536,
                RecentMessagesToKeep = 32,
                MaxSummaryUtf8Bytes = 32_768,
                CompactionTimeout = TimeSpan.FromSeconds(1),
                FailureCooldown = TimeSpan.Zero
            },
            compactor ?? new CountingCompactor(),
            new FakeRuntimeClock());
    }

    private static List<NormalizedMessage> CompactionTranscript()
    {
        var result = new List<NormalizedMessage>
        {
            Message(
                "stable",
                NormalizedRoles.Assistant,
                "stable disclosure"),
            Message(
                "system",
                NormalizedRoles.System,
                "game rules")
        };
        for (var index = 0; index < 14; index++)
        {
            result.Add(
                Message(
                    "history-" + index,
                    index % 2 == 0
                        ? NormalizedRoles.User
                        : NormalizedRoles.Assistant,
                    new string((char)('a' + index), 180)));
        }

        result.Add(
            Message(
                "latest-user",
                NormalizedRoles.User,
                "latest command"));
        result.Add(
            Message(
                "latest-answer",
                NormalizedRoles.Assistant,
                "latest answer"));
        return result;
    }

    private static NormalizedMessage[] ManyMessages(
        int count,
        int idUtf8Bytes)
    {
        return Enumerable.Range(0, count)
            .Select(
                index => Message(
                    FixedId(index, idUtf8Bytes),
                    index % 2 == 0
                        ? NormalizedRoles.User
                        : NormalizedRoles.Assistant,
                    "x"))
            .ToArray();
    }

    private static string FixedId(int index, int utf8Bytes)
    {
        var prefix = $"message-{index:D4}-";
        return prefix + new string('x', utf8Bytes - prefix.Length);
    }

    private static NormalizedMessage Message(
        string id,
        string role,
        string text)
    {
        return new NormalizedMessage
        {
            MessageId = id,
            Role = role,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText(text)
            }
        };
    }

    private static string Encoded(NormalizedMessage message)
    {
        return NormalizedMessageJournalCodec.Encode(message).GetRawText();
    }

    private static JsonElement Mutate(
        JsonElement checkpoint,
        Action<JsonObject> mutation,
        bool refreshIntegrity = true)
    {
        var root = JsonNode.Parse(checkpoint.GetRawText())!.AsObject();
        var payload = root["payload"]!.AsObject();
        mutation(payload);
        if (refreshIntegrity)
        {
            var payloadJson = ProtocolJson.ParseElement(
                payload.ToJsonString());
            root["integrityDigest"] =
                CanonicalJsonDigest.ComputeSha256(payloadJson);
        }

        return ProtocolJson.ParseElement(root.ToJsonString());
    }

    private sealed class CountingCompactor : IConversationCompactor
    {
        private readonly ExtractiveConversationCompactor _inner = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ConversationCompactionResult> CompactAsync(
            ConversationCompactionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _inner.CompactAsync(request, cancellationToken);
        }
    }
}
