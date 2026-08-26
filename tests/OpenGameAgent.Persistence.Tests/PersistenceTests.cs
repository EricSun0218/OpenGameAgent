using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenGameAgent.Attachments;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SessionRoundTripsEveryCanonicalContentKindAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var usage = new ModelUsage(10, 4, 3, 2);
        var messages = new AgentMessage[]
        {
            new(
                AgentRole.User,
                new AgentContent[]
                {
                    new TextContent("hello", "text-signature", AgentTextPhase.Commentary),
                    new JsonContent("{\"value\":1.25}"),
                    new ResourceContent("game://asset/1", "application/game-object", "object"),
                    new ImageAttachmentContent(new GameImageAttachment(
                        "sha256:0123456789abcdef",
                        GameImageMediaTypes.Png,
                        123,
                        32,
                        16,
                        "frame.png")),
                    new BinaryContent(AgentMediaKind.Audio, "AQID", "audio/pcm", "voice.pcm"),
                },
                DateTimeOffset.UnixEpoch,
                metadata: new Dictionary<string, string> { ["kind"] = "input" }),
            new(
                AgentRole.Assistant,
                new AgentContent[]
                {
                    new ReasoningContent("plan", "signature", redacted: true),
                    new ToolCallContent("call", "move", "{\"x\":2.5}", "tool-thought", "world"),
                },
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                model: "model",
                stopReason: ModelStopReason.ToolUse,
                usage: new ModelUsage(
                    10,
                    4,
                    3,
                    2,
                    reasoningTokens: 2,
                    cacheWriteOneHourTokens: 1,
                    cost: new ModelCost(0.1, 0.2, 0.03, 0.04, isKnown: true)),
                provider: "provider",
                api: "responses",
                responseModel: "resolved-model",
                responseId: "response-1",
                rawStopReason: "tool_calls",
                endTurn: false,
                diagnostics: new[]
                {
                    new ModelDiagnostic("compat", "provider normalized", ModelDiagnosticSeverity.Warning, "{\"field\":\"stop\"}"),
                }),
            new(
                AgentRole.Tool,
                new AgentContent[] { new JsonContent("{\"committed\":true}") },
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                toolCallId: "call",
                toolName: "move",
                detailsJson: "{\"revision\":7}",
                usage: new ModelUsage(2, 1),
                addedToolNames: new[] { "inspect" }),
            new(
                AgentRole.Assistant,
                new AgentContent[] { new TextContent("queued") },
                DateTimeOffset.UnixEpoch.AddSeconds(3),
                model: "model",
                stopReason: ModelStopReason.Deferred,
                provider: "provider",
                api: "responses",
                deferred: new DeferredModelHandle(
                    "provider",
                    "model",
                    "responses",
                    "deferred-1",
                    DateTimeOffset.UnixEpoch.AddHours(1),
                    250,
                    "{\"queue\":2}")),
        };
        var store = new FileGameSessionStore(directory.Path);
        var saved = await store.SaveAsync(
            new GameSessionSnapshot(key, 1, messages, new[] { "input" }, new GameMoment("world", 12, "{\"month\":3}")),
            0,
            TestContext.Current.CancellationToken);

        var restarted = new FileGameSessionStore(directory.Path);
        var loaded = await restarted.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.True(saved.Saved);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Messages.Count);
        var text = Assert.IsType<TextContent>(loaded.Messages[0].Content[0]);
        Assert.Equal("text-signature", text.Signature);
        Assert.Equal(AgentTextPhase.Commentary, text.Phase);
        Assert.Equal("{\"value\":1.25}", Assert.IsType<JsonContent>(loaded.Messages[0].Content[1]).Json);
        var image = Assert.IsType<ImageAttachmentContent>(loaded.Messages[0].Content[3]).Attachment;
        Assert.Equal("sha256:0123456789abcdef", image.AttachmentId);
        Assert.Equal(32, image.Width);
        Assert.Equal("frame.png", image.Name);
        var binary = Assert.IsType<BinaryContent>(loaded.Messages[0].Content[4]);
        Assert.Equal(AgentMediaKind.Audio, binary.MediaKind);
        Assert.Equal("AQID", binary.Data);
        Assert.Equal("signature", Assert.IsType<ReasoningContent>(loaded.Messages[1].Content[0]).Signature);
        Assert.True(Assert.IsType<ReasoningContent>(loaded.Messages[1].Content[0]).Redacted);
        var toolCall = Assert.IsType<ToolCallContent>(loaded.Messages[1].Content[1]);
        Assert.Equal("tool-thought", toolCall.ThoughtSignature);
        Assert.Equal("world", toolCall.Namespace);
        Assert.Equal(10, loaded.Messages[1].Usage!.InputTokens);
        Assert.Equal(2, loaded.Messages[1].Usage!.ReasoningTokens);
        Assert.Equal(0.37, loaded.Messages[1].Usage!.Cost.Total, precision: 10);
        Assert.True(loaded.Messages[1].Usage!.Cost.IsKnown);
        Assert.Equal("provider", loaded.Messages[1].Provider);
        Assert.Equal("responses", loaded.Messages[1].Api);
        Assert.Equal("resolved-model", loaded.Messages[1].ResponseModel);
        Assert.Equal("response-1", loaded.Messages[1].ResponseId);
        Assert.Equal("tool_calls", loaded.Messages[1].RawStopReason);
        Assert.False(loaded.Messages[1].EndTurn);
        Assert.Equal("compat", Assert.Single(loaded.Messages[1].Diagnostics).Code);
        Assert.Equal("{\"revision\":7}", loaded.Messages[2].DetailsJson);
        Assert.Equal(3, loaded.Messages[2].Usage!.TotalTokens);
        Assert.Equal("inspect", Assert.Single(loaded.Messages[2].AddedToolNames));
        Assert.Equal("deferred-1", loaded.Messages[3].Deferred!.Id);
        Assert.Equal(250, loaded.Messages[3].Deferred!.PollAfterMilliseconds);
        Assert.Equal(12, loaded.LastMoment!.Value.Tick);
    }

    [Fact]
    public async Task SessionUsageLedgerAndCostsSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var records = new[]
        {
            new GameSessionUsageRecord(
                "run-one-assistant",
                GameSessionUsageCause.Assistant,
                new ModelUsage(
                    12,
                    4,
                    3,
                    2,
                    reasoningTokens: 2,
                    cacheWriteOneHourTokens: 1,
                    cost: new ModelCost(0.12, 0.08, 0.003, 0.004)),
                "run-one",
                "input-one"),
            new GameSessionUsageRecord(
                "run-one-compaction",
                GameSessionUsageCause.Compaction,
                new ModelUsage(6, 2, cost: new ModelCost(0.06, 0.04)),
                "run-one",
                "input-one",
                "{\"removed\":8}"),
        };
        var store = new FileGameSessionStore(directory.Path);
        await store.SaveAsync(
            new GameSessionSnapshot(
                key,
                1,
                usageLedger: new GameSessionUsageLedger(records)),
            0,
            TestContext.Current.CancellationToken);

        var restarted = new FileGameSessionStore(directory.Path);
        var loaded = await restarted.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.UsageLedger.Records.Count);
        Assert.Equal(29, loaded.UsageLedger.Stats.TotalTokens);
        Assert.Equal(8, loaded.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        Assert.Equal(0.307, loaded.UsageLedger.Stats.CostTotal, precision: 10);
        Assert.Equal("{\"removed\":8}", loaded.UsageLedger.Records[1].DetailsJson);
        Assert.Equal(2, loaded.UsageLedger.Records[0].Usage.ReasoningTokens);
        Assert.Equal(1, loaded.UsageLedger.Records[0].Usage.CacheWriteOneHourTokens);
        Assert.True(loaded.UsageLedger.Records[0].Usage.Cost.IsKnown);
        Assert.True(loaded.UsageLedger.Stats.Total.CostKnown);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.session.json"));
        Assert.Equal(4, JsonNode.Parse(await File.ReadAllTextAsync(
            file,
            TestContext.Current.CancellationToken))!["FormatVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnknownUsageCostRemainsUnknownAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("unknown-cost", "actor");
        var store = new FileGameSessionStore(directory.Path);
        var saved = await store.SaveAsync(
            new GameSessionSnapshot(
                key,
                1,
                usageLedger: new GameSessionUsageLedger(new[]
                {
                    new GameSessionUsageRecord(
                        "unknown-cost-record",
                        GameSessionUsageCause.Assistant,
                        new ModelUsage(3, 1)),
                })),
            0,
            TestContext.Current.CancellationToken);

        var loaded = await new FileGameSessionStore(directory.Path)
            .LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.True(saved.Saved);
        Assert.NotNull(loaded);
        Assert.False(Assert.Single(loaded.UsageLedger.Records).Usage.Cost.IsKnown);
        Assert.False(loaded.UsageLedger.Stats.Total.CostKnown);
        Assert.Null(loaded.UsageLedger.Stats.Total.CostTotalIfKnown);
    }

    [Fact]
    public async Task BoundedUsageLedgerTotalsSurviveEvictionAndRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var records = Enumerable.Range(0, 1_000)
            .Select(index => new GameSessionUsageRecord(
                "record-" + index,
                index % 2 == 0 ? GameSessionUsageCause.Assistant : GameSessionUsageCause.Compaction,
                new ModelUsage(2, 1, cost: new ModelCost(input: 0.02, output: 0.01))))
            .ToArray();
        var store = new FileGameSessionStore(directory.Path);
        await store.SaveAsync(
            new GameSessionSnapshot(
                key,
                1,
                usageLedger: new GameSessionUsageLedger(records, recentRecordCapacity: 3)),
            0,
            TestContext.Current.CancellationToken);

        var restarted = new FileGameSessionStore(directory.Path);
        var loaded = await restarted.LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.UsageLedger.Records.Count);
        Assert.Equal(1_000, loaded.UsageLedger.TotalRecordCount);
        Assert.Equal(3_000, loaded.UsageLedger.Stats.TotalTokens);
        Assert.Equal(30, loaded.UsageLedger.Stats.CostTotal, precision: 10);
        Assert.Equal(1_500, loaded.UsageLedger.Stats.ForCause(GameSessionUsageCause.Assistant).TotalTokens);
        Assert.Equal(1_500, loaded.UsageLedger.Stats.ForCause(GameSessionUsageCause.Compaction).TotalTokens);
        Assert.Equal(new[] { "record-997", "record-998", "record-999" },
            loaded.UsageLedger.Records.Select(record => record.RecordId));
        Assert.True(new FileInfo(Assert.Single(Directory.GetFiles(directory.Path, "*.session.json"))).Length < 16_384);

        var truncatedTotals = GameSessionUsageLedger.Restore(
            loaded.UsageLedger.Records,
            new Dictionary<GameSessionUsageCause, GameSessionUsageTotals>
            {
                [GameSessionUsageCause.Assistant] = new GameSessionUsageTotals(
                    2, 1, 0, 0, 0, 0, 0.02, 0.01, 0, 0),
                [GameSessionUsageCause.Compaction] = new GameSessionUsageTotals(
                    4, 2, 0, 0, 0, 0, 0.04, 0.02, 0, 0),
            },
            loaded.UsageLedger.TotalRecordCount,
            loaded.UsageLedger.RecentRecordCapacity);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await restarted.SaveAsync(
                new GameSessionSnapshot(key, 2, usageLedger: truncatedTotals),
                1,
                TestContext.Current.CancellationToken));

        var appended = loaded.UsageLedger.Append(new[]
        {
            new GameSessionUsageRecord(
                "record-1000",
                GameSessionUsageCause.Tool,
                new ModelUsage(4, 2)),
        });
        Assert.True((await restarted.SaveAsync(
            new GameSessionSnapshot(key, 2, usageLedger: appended),
            1,
            TestContext.Current.CancellationToken)).Saved);
        var final = await new FileGameSessionStore(directory.Path)
            .LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(final);
        Assert.Equal(3, final.UsageLedger.Records.Count);
        Assert.Equal(1_001, final.UsageLedger.TotalRecordCount);
        Assert.Equal(3_006, final.UsageLedger.Stats.TotalTokens);
        Assert.Equal(new[] { "record-998", "record-999", "record-1000" },
            final.UsageLedger.Records.Select(record => record.RecordId));
    }

    [Fact]
    public async Task SessionStoreRejectsUsageLedgerRemovalOrRewrite()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var record = new GameSessionUsageRecord(
            "stable-usage",
            GameSessionUsageCause.Assistant,
            new ModelUsage(2, 1));
        var store = new FileGameSessionStore(directory.Path);
        await store.SaveAsync(
            new GameSessionSnapshot(
                key,
                1,
                usageLedger: new GameSessionUsageLedger(new[] { record })),
            0,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(
                new GameSessionSnapshot(key, 2),
                1,
                TestContext.Current.CancellationToken));
        var rewritten = new GameSessionUsageRecord(
            record.RecordId,
            record.Cause,
            new ModelUsage(9, 9));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(
                new GameSessionSnapshot(
                    key,
                    2,
                    usageLedger: new GameSessionUsageLedger(new[] { rewritten })),
                1,
                TestContext.Current.CancellationToken));

        var loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(3, loaded.UsageLedger.Stats.TotalTokens);
    }

    [Fact]
    public async Task NonCurrentPersistenceFormatsAreRejectedInsteadOfSilentlyMigrated()
    {
        using (var sessionDirectory = new TemporaryDirectory())
        {
            var key = new GameSessionKey("session", "actor");
            var store = new FileGameSessionStore(sessionDirectory.Path);
            await store.SaveAsync(
                new GameSessionSnapshot(key, 1),
                0,
                TestContext.Current.CancellationToken);
            var path = Assert.Single(Directory.GetFiles(sessionDirectory.Path, "*.session.json"));
            await SetFormatVersionAsync(path, 3);

            await Assert.ThrowsAsync<PersistenceException>(async () =>
                await store.LoadAsync(key, TestContext.Current.CancellationToken));
        }

        using (var actionDirectory = new TemporaryDirectory())
        {
            var intent = Intent("pre-release-action");
            var journal = new FileGameActionJournal(actionDirectory.Path);
            await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
            var path = Assert.Single(Directory.GetFiles(actionDirectory.Path, "*.action.json"));
            await SetFormatVersionAsync(path, 1);

            await Assert.ThrowsAsync<PersistenceException>(async () =>
                await journal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken));
        }

    }

    [Fact]
    public async Task SessionSaveUsesOptimisticRevisionAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var first = new FileGameSessionStore(directory.Path);
        await first.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);
        var second = new FileGameSessionStore(directory.Path);

        var stale = await second.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);

        Assert.False(stale.Saved);
        Assert.Equal(1, stale.Current.Revision);

    }

    [Fact]
    public async Task IndependentSessionStoresPreserveCompareAndSwapUnderConcurrentWriters()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        var first = new FileGameSessionStore(directory.Path);
        var second = new FileGameSessionStore(directory.Path);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<GameSessionSaveResult> SaveAsync(FileGameSessionStore store, string inputId)
        {
            await start.Task.WaitAsync(TestContext.Current.CancellationToken);
            return await store.SaveAsync(
                new GameSessionSnapshot(key, 1, processedInputIds: new[] { inputId }),
                0,
                TestContext.Current.CancellationToken);
        }

        var writes = new[] { SaveAsync(first, "one"), SaveAsync(second, "two") };
        start.SetResult();
        var results = await Task.WhenAll(writes);

        Assert.Single(results, result => result.Saved);
        Assert.Single(results, result => !result.Saved);
        Assert.All(results, result => Assert.Equal(1, result.Current.Revision));
        var loaded = await new FileGameSessionStore(directory.Path)
            .LoadAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Single(loaded.ProcessedInputIds);
    }

    [Fact]
    public async Task SessionStorageIdentityCannotCollideThroughIdentifierDelimiters()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSessionStore(directory.Path);
        var firstKey = new GameSessionKey("a\nb", "c");
        var secondKey = new GameSessionKey("a", "b\nc");

        await store.SaveAsync(new GameSessionSnapshot(firstKey, 1), 0, TestContext.Current.CancellationToken);
        await store.SaveAsync(new GameSessionSnapshot(secondKey, 1), 0, TestContext.Current.CancellationToken);

        Assert.Equal(firstKey, (await store.LoadAsync(firstKey, TestContext.Current.CancellationToken))!.Key);
        Assert.Equal(secondKey, (await store.LoadAsync(secondKey, TestContext.Current.CancellationToken))!.Key);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.session.json").Length);
    }

    [Fact]
    public async Task PendingActionAndReceiptSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("operation");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);

        var restarted = new FileGameActionJournal(directory.Path);
        var pending = await restarted.ListPendingAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await restarted.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await restarted.SaveReceiptAsync(
            GameActionReceipt.Committed(intent, "{\"value\":4.75}", 2),
            TestContext.Current.CancellationToken);
        var finalRestart = new FileGameActionJournal(directory.Path);
        var entry = await finalRestart.FindAsync("operation", TestContext.Current.CancellationToken);

        Assert.Single(pending);
        Assert.Equal(GameActionStatus.Committed, entry!.Receipt!.Status);
        Assert.Equal(2, entry.Receipt.StateRevision);
        Assert.Empty(await finalRestart.ListPendingAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActionGenerationBindingSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var baseline = Intent("generation-operation");
        var intent = new GameActionIntent(
            baseline.OperationId,
            baseline.InputId,
            baseline.SessionId,
            baseline.ActorId,
            baseline.Action,
            baseline.ArgumentsJson,
            baseline.Moment,
            baseline.ExpectedRevision,
            "save-generation-4");
        await new FileGameActionJournal(directory.Path).ReserveAsync(
            intent,
            TestContext.Current.CancellationToken);

        var restored = await new FileGameActionJournal(directory.Path).FindAsync(
            intent.OperationId,
            TestContext.Current.CancellationToken);

        Assert.Equal("save-generation-4", restored!.Intent.GenerationId);
        var conflicting = new GameActionIntent(
            intent.OperationId,
            intent.InputId,
            intent.SessionId,
            intent.ActorId,
            intent.Action,
            intent.ArgumentsJson,
            intent.Moment,
            intent.ExpectedRevision,
            "different-generation");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FileGameActionJournal(directory.Path).ReserveAsync(
                conflicting,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndependentFileDispatchersNeverExecuteTheSameOperationTwice()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("concurrent-operation");
        var executeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executeCount = 0;
        var recoverCount = 0;
        var handler = new CallbackActionHandler(
            async (candidate, cancellationToken) =>
            {
                Interlocked.Increment(ref executeCount);
                executeEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return GameActionReceipt.Committed(candidate, "{\"executed\":true}");
            },
            async (candidate, cancellationToken) =>
            {
                Interlocked.Increment(ref recoverCount);
                await release.Task.WaitAsync(cancellationToken);
                return GameActionReceipt.Committed(candidate, "{\"executed\":true}");
            });
        var first = new DurableGameActionDispatcher(new FileGameActionJournal(directory.Path), handler);
        var second = new DurableGameActionDispatcher(new FileGameActionJournal(directory.Path), handler);

        var executions = new[]
        {
            first.ExecuteAsync(intent, TestContext.Current.CancellationToken).AsTask(),
            second.ExecuteAsync(intent, TestContext.Current.CancellationToken).AsTask(),
        };
        await executeEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        release.SetResult();
        var receipts = await Task.WhenAll(executions);

        Assert.All(receipts, receipt => Assert.Equal(GameActionStatus.Committed, receipt.Status));
        Assert.Equal(1, executeCount);
        Assert.InRange(recoverCount, 0, 1);
    }

    [Fact]
    public async Task ZeroPendingActionLimitReturnsNoEntries()
    {
        using var directory = new TemporaryDirectory();
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(Intent("operation"), TestContext.Current.CancellationToken);

        var pending = await journal.ListPendingAsync(0, TestContext.Current.CancellationToken);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task FinalReceiptCannotBeChanged()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("operation");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await journal.SaveReceiptAsync(
            GameActionReceipt.Committed(intent, "{\"value\":1}"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await journal.SaveReceiptAsync(
                GameActionReceipt.Committed(intent, "{\"value\":2}"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiptMustMatchTheReservedIntentMoment()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("operation");
        var journals = new IGameActionJournal[]
        {
            new InMemoryGameActionJournal(),
            new FileGameActionJournal(directory.Path),
        };

        foreach (var journal in journals)
        {
            await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
            Assert.Equal(GameActionDispatchClaimStatus.Claimed,
                (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
            var mismatched = new GameActionReceipt(
                intent.OperationId,
                GameActionStatus.Committed,
                "{}",
                new GameMoment("world", intent.Moment.Tick + 1));

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await journal.SaveReceiptAsync(mismatched, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CorruptReceiptIdentityIsRejectedWhenJournalIsRead()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("operation");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await journal.SaveReceiptAsync(GameActionReceipt.Committed(intent, "{}"), TestContext.Current.CancellationToken);
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.action.json"));
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(text)!.AsObject();
        document["Receipt"]!["OperationId"] = "different";
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await new FileGameActionJournal(directory.Path).FindAsync(
                intent.OperationId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparedFileActionExecutesAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("prepared-operation");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        var executeCount = 0;
        var recoverCount = 0;
        var dispatcher = new DurableGameActionDispatcher(
            new FileGameActionJournal(directory.Path),
            new CallbackActionHandler(
                (candidate, _) =>
                {
                    executeCount++;
                    return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(candidate, "{}"));
                },
                (_, _) =>
                {
                    recoverCount++;
                    return new ValueTask<GameActionReceipt?>((GameActionReceipt?)null);
                }));

        var receipt = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        Assert.Equal(1, executeCount);
        Assert.Equal(0, recoverCount);
    }

    [Fact]
    public async Task DispatchedFileActionRecoversAfterRestartWithoutReplay()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("dispatched-operation");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        var executeCount = 0;
        var recoverCount = 0;
        var dispatcher = new DurableGameActionDispatcher(
            new FileGameActionJournal(directory.Path),
            new CallbackActionHandler(
                (candidate, _) =>
                {
                    executeCount++;
                    return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(candidate, "{}"));
                },
                (candidate, _) =>
                {
                    recoverCount++;
                    return new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(candidate, "{\"recovered\":true}"));
                }));

        var receipt = await dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, receipt.Status);
        Assert.Equal(0, executeCount);
        Assert.Equal(1, recoverCount);
    }

    [Fact]
    public async Task CorruptSessionIsReportedInsteadOfSilentlyReset()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSessionStore(directory.Path);
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.session.json"));
        await File.WriteAllTextAsync(file, "{broken", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.LoadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DuplicatePersistencePropertiesAreRejectedInsteadOfUsingLastValue()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSessionStore(directory.Path);
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.session.json"));
        var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            file,
            text.Replace("\"Revision\":1", "\"Revision\":0,\"Revision\":1", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.LoadAsync(key, TestContext.Current.CancellationToken));

        Assert.Contains("duplicate JSON property", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticallyCorruptPersistenceIsReportedAsPersistenceFailure()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameSessionStore(directory.Path);
        var key = new GameSessionKey("session", "actor");
        await store.SaveAsync(new GameSessionSnapshot(key, 1), 0, TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.session.json"));
        var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            file,
            text.Replace("\"Revision\":1", "\"Revision\":-1", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.LoadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemorySearchSurvivesRestartAndUsesGameTime()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path);
        await store.AppendAsync(
            new GameMemory(
                "memory",
                "session",
                "npc",
                "personal",
                GameMemoryKind.Relationship,
                "{\"trust\":0.875}",
                new GameMoment("world", 7),
                0.7,
                "friend",
                new[] { "ally" },
                expiresAt: new GameMoment("world", 20),
                metadata: new Dictionary<string, string> { ["incarnation"] = "npc-v2" }),
            TestContext.Current.CancellationToken);

        var restarted = new FileGameMemoryStore(directory.Path);
        var found = await restarted.SearchAsync(
            new GameMemoryQuery("session", 5, ownerId: "npc", tags: new[] { "ally" }, atOrBefore: new GameMoment("world", 8)),
            TestContext.Current.CancellationToken);

        var memory = Assert.Single(found);
        Assert.Equal(7, memory.Moment.Tick);
        Assert.Contains("0.875", memory.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(20, memory.ExpiresAt!.Value.Tick);
        Assert.Equal("npc-v2", memory.Metadata["incarnation"]);
    }

    [Fact]
    public async Task FileMemoryIdentifiersAreScopedToTheirGameSessionAndOwner()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path);
        await store.AppendAsync(
            new GameMemory("shared-id", "session-a", "npc", "personal", GameMemoryKind.Fact, "{\"value\":1}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory("shared-id", "session-b", "npc", "personal", GameMemoryKind.Fact, "{\"value\":2}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory("shared-id", "session-a", "other-npc", "personal", GameMemoryKind.Fact, "{\"value\":3}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        var restarted = new FileGameMemoryStore(directory.Path);
        var first = await restarted.SearchAsync(
            new GameMemoryQuery("session-a", 1, ownerId: "npc", atOrBefore: new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        var second = await restarted.SearchAsync(
            new GameMemoryQuery("session-b", 1, atOrBefore: new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        Assert.Contains("1", Assert.Single(first).PayloadJson, StringComparison.Ordinal);
        Assert.Contains("2", Assert.Single(second).PayloadJson, StringComparison.Ordinal);
        Assert.Equal(
            2,
            (await restarted.SearchAsync(
                new GameMemoryQuery("session-a", 2, atOrBefore: new GameMoment("world", 1)),
                TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task FileMemoryStoreMigratesFlatFilesOnceWithoutLosingIdentityOrRestartability()
    {
        using var directory = new TemporaryDirectory();
        await WriteLegacyMemoryAsync(directory.Path, "session-a", "owner-a", "one", "alpha");
        await WriteLegacyMemoryAsync(directory.Path, "session-b", "owner-b", "two", "beta");
        var store = new FileGameMemoryStore(directory.Path);

        var migration = await store.MigrateLegacyLayoutAsync(TestContext.Current.CancellationToken);
        var result = await store.SearchAsync(
            new GameMemoryQuery("session-a", 8, ownerId: "owner-a", text: "alpha"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, migration.MigratedEntries);
        Assert.Equal(2, migration.PartitionedEntries);
        Assert.Equal("one", Assert.Single(result).MemoryId);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.memory.json", SearchOption.TopDirectoryOnly));
        Assert.Equal(
            "two",
            Assert.Single(await new FileGameMemoryStore(directory.Path).SearchAsync(
                new GameMemoryQuery("session-b", 8, ownerId: "owner-b", text: "beta"),
                TestContext.Current.CancellationToken)).MemoryId);
        Assert.False((await new FileGameMemoryStore(directory.Path)
            .MigrateLegacyLayoutAsync(TestContext.Current.CancellationToken)).PerformedWork);
    }

    [Fact]
    public async Task FileMemoryStoreResumesAnInterruptedLegacyMigration()
    {
        using var directory = new TemporaryDirectory();
        await WriteLegacyMemoryAsync(directory.Path, "session-a", "owner-a", "one", "alpha");
        await WriteLegacyMemoryAsync(directory.Path, "session-b", "owner-b", "two", "beta");

        var firstLegacyPath = System.IO.Path.Combine(
            directory.Path,
            HashString("session-a\nowner-a\none") + ".memory.json");
        var interruptedTargetDirectory = System.IO.Path.Combine(
            directory.Path,
            ".memory-v2",
            "s-" + HashString("session-a"),
            "o-" + HashString("owner-a"));
        Directory.CreateDirectory(interruptedTargetDirectory);
        File.Move(
            firstLegacyPath,
            System.IO.Path.Combine(interruptedTargetDirectory, HashString("one") + ".memory.json"));

        var restarted = new FileGameMemoryStore(directory.Path);
        var migration = await restarted.MigrateLegacyLayoutAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, migration.MigratedEntries);
        Assert.Equal(2, migration.PartitionedEntries);
        Assert.Equal(
            "one",
            Assert.Single(await restarted.SearchAsync(
                new GameMemoryQuery("session-a", 8, ownerId: "owner-a", text: "alpha"),
                TestContext.Current.CancellationToken)).MemoryId);
        Assert.Equal(
            "two",
            Assert.Single(await new FileGameMemoryStore(directory.Path).SearchAsync(
                new GameMemoryQuery("session-b", 8, ownerId: "owner-b", text: "beta"),
                TestContext.Current.CancellationToken)).MemoryId);
    }

    [Fact]
    public async Task FileMemorySearchScansOnlyTheRequestedOwnerPartition()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path, maximumEntries: 1_000);
        for (var index = 0; index < 120; index++)
        {
            await store.AppendAsync(
                new GameMemory(
                    "unrelated-" + index,
                    "session-" + (index % 6),
                    "owner-" + (index % 12),
                    "fact",
                    GameMemoryKind.Fact,
                    "{\"value\":" + index + "}",
                    new GameMoment("world", index),
                    searchableText: "unrelated"),
                TestContext.Current.CancellationToken);
        }

        for (var index = 0; index < 3; index++)
        {
            await store.AppendAsync(
                new GameMemory(
                    "target-" + index,
                    "target-session",
                    "target-owner",
                    "fact",
                    GameMemoryKind.Fact,
                    "{\"target\":true}",
                    new GameMoment("world", index),
                    searchableText: "target"),
                TestContext.Current.CancellationToken);
        }

        var snapshot = await new FileGameMemoryStore(directory.Path, maximumEntries: 1_000).SearchSnapshotAsync(
            new GameMemoryQuery("target-session", 8, ownerId: "target-owner", text: "target"),
            maximumSnapshotEntries: 1_000,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.Memories.Count);
        var authoritative = Assert.Single(
            snapshot.Stages,
            stage => stage.Stage == GameMemorySearchStageKind.AuthoritativeSnapshot);
        Assert.Equal(3, authoritative.ScannedCount);
        Assert.DoesNotContain(snapshot.AuthoritativeMemories, memory => memory.SessionId != "target-session");
        Assert.DoesNotContain(snapshot.AuthoritativeMemories, memory => memory.OwnerId != "target-owner");
    }

    [Fact]
    public async Task FileMemoryPartitionRejectsCorruptionAndConcurrentWritersSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var first = new FileGameMemoryStore(directory.Path, maximumEntries: 1_000);
        var second = new FileGameMemoryStore(directory.Path, maximumEntries: 1_000);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            (index % 2 == 0 ? first : second).AppendAsync(
                    new GameMemory(
                        "memory-" + index,
                        "session",
                        "owner",
                        "fact",
                        GameMemoryKind.Fact,
                        "{\"value\":" + index + "}",
                        new GameMoment("world", index),
                        searchableText: "value"),
                    TestContext.Current.CancellationToken)
                .AsTask()));

        var restarted = new FileGameMemoryStore(directory.Path, maximumEntries: 1_000);
        Assert.Equal(
            40,
            (await restarted.SearchSnapshotAsync(
                new GameMemoryQuery("session", 40, ownerId: "owner"),
                1_000,
                TestContext.Current.CancellationToken)).AuthoritativeMemories.Count);

        var target = Directory.GetFiles(directory.Path, "*.memory.json", SearchOption.AllDirectories).First();
        await File.WriteAllTextAsync(target, "{broken", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await new FileGameMemoryStore(directory.Path, maximumEntries: 1_000).SearchAsync(
                new GameMemoryQuery("session", 40, ownerId: "owner"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMemoryPendingAddIsRecoveredOnceAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path, maximumEntries: 100);
        await store.AppendAsync(
            new GameMemory(
                "one",
                "session",
                "owner",
                "fact",
                GameMemoryKind.Fact,
                "{\"value\":1}",
                new GameMoment("world", 1),
                searchableText: "one"),
            TestContext.Current.CancellationToken);
        var layoutDirectory = System.IO.Path.Combine(directory.Path, ".memory-v2");
        var sessionHash = HashString("session");
        var ownerHash = HashString("owner");
        var memoryHash = HashString("two");
        var ownerDirectory = System.IO.Path.Combine(layoutDirectory, "s-" + sessionHash, "o-" + ownerHash);
        Directory.CreateDirectory(ownerDirectory);
        await WriteMemoryDocumentAsync(
            System.IO.Path.Combine(ownerDirectory, memoryHash + ".memory.json"),
            "session",
            "owner",
            "two",
            "two");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(layoutDirectory, "pending-add.json"),
            JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                ExpectedEntryCount = 1,
                SessionHash = sessionHash,
                OwnerHash = ownerHash,
                MemoryHash = memoryHash,
            }),
            TestContext.Current.CancellationToken);

        var restarted = new FileGameMemoryStore(directory.Path, maximumEntries: 100);
        var recovered = await restarted.SearchSnapshotAsync(
            new GameMemoryQuery("session", 8, ownerId: "owner"),
            100,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, recovered.AuthoritativeMemories.Count);
        Assert.False(File.Exists(System.IO.Path.Combine(layoutDirectory, "pending-add.json")));
        Assert.Equal(
            2,
            (await new FileGameMemoryStore(directory.Path, maximumEntries: 100).SearchSnapshotAsync(
                new GameMemoryQuery("session", 8, ownerId: "owner"),
                100,
                TestContext.Current.CancellationToken)).AuthoritativeMemories.Count);
    }

    [Fact]
    public async Task FileMemoryCancellationDoesNotCreateAnEntry()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.AppendAsync(
                new GameMemory(
                    "cancelled",
                    "session",
                    "owner",
                    "fact",
                    GameMemoryKind.Fact,
                    "{}",
                    new GameMoment("world", 1)),
                cancellation.Token));

        Assert.Empty(await new FileGameMemoryStore(directory.Path).SearchAsync(
            new GameMemoryQuery("session", 8, ownerId: "owner"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMemoryLayoutCorruptionFailsClosedAfterWarmup()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileGameMemoryStore(directory.Path);
        await store.AppendAsync(
            new GameMemory(
                "memory",
                "session",
                "owner",
                "fact",
                GameMemoryKind.Fact,
                "{}",
                new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory.Path, ".memory-v2", "layout.json"),
            "{broken",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await store.SearchAsync(
                new GameMemoryQuery("session", 8, ownerId: "owner"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FileMemoryRejectsReparseStorageWhenThePlatformAllowsCreatingIt()
    {
        using var directory = new TemporaryDirectory();
        var real = System.IO.Path.Combine(directory.Path, "real");
        var link = System.IO.Path.Combine(directory.Path, "link");
        Directory.CreateDirectory(real);
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<PersistenceException>(() => new FileGameMemoryStore(link));
    }

    [Fact]
    public async Task DirectorySkillSourceLoadsDeclarativeInstructionsOnly()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "building");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "skill.json"),
            "{\"id\":\"building\",\"name\":\"Building\",\"description\":\"Build safely\",\"inputTypes\":[\"build\"],\"toolNames\":[\"place\"],\"priority\":5}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "instructions.md"),
            "Plan before placing blocks.",
            TestContext.Current.CancellationToken);
        var source = new DirectoryGameSkillSource(directory.Path);
        var input = new GameInput("session", "actor", "build", "{}", new GameMoment("world", 1));

        var selected = await source.SelectAsync(
            new GameSkillQuery(input, new[] { "place" }, 5),
            TestContext.Current.CancellationToken);

        var skill = Assert.Single(selected);
        Assert.Equal("building", skill.SkillId);
        Assert.Equal("Plan before placing blocks.", skill.Instructions);
    }

    [Fact]
    public async Task DirectorySkillSourceLoadsSkillMarkdownWithoutASeparateManifest()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "building");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: building\ndescription: Build structures from validated plans.\n---\nInspect the region before placing anything.",
            TestContext.Current.CancellationToken);
        var source = new DirectoryGameSkillSource(directory.Path);
        var input = new GameInput("session", "actor", "build", "{}", new GameMoment("world", 1));

        var selected = await source.SelectAsync(
            new GameSkillQuery(input, Array.Empty<string>(), 5),
            TestContext.Current.CancellationToken);

        var skill = Assert.Single(selected);
        Assert.Equal("building", skill.SkillId);
        Assert.Equal("Build structures from validated plans.", skill.Description);
        Assert.Equal("Inspect the region before placing anything.", skill.Instructions);
    }

    [Fact]
    public async Task DirectorySkillSourceDiscoversNestedSkillRootsAndStopsBelowEachRoot()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "packs", "building");
        var ignoredNested = System.IO.Path.Combine(skillDirectory, "nested");
        Directory.CreateDirectory(ignoredNested);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: building\ndescription: Build from a validated plan.\n---\nUse the construction tools.",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(ignoredNested, "SKILL.md"),
            "---\nname: hidden-child\ndescription: Must not be discovered below another skill root.\n---\nIgnored.",
            TestContext.Current.CancellationToken);
        var source = new DirectoryGameSkillSource(directory.Path);

        var selected = await source.SelectAsync(
            new GameSkillQuery(
                new GameInput("session", "actor", "build", "{}", new GameMoment("world", 1)),
                Array.Empty<string>(),
                10),
            TestContext.Current.CancellationToken);

        Assert.Equal("building", Assert.Single(selected).SkillId);
    }

    [Fact]
    public async Task DirectorySkillSourceRejectsNonPortableMarkdownSkillNames()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "invalid");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: Invalid Name\ndescription: Invalid portable name.\n---\nInstructions.",
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<PersistenceException>(() => new DirectoryGameSkillSource(directory.Path));

        Assert.Contains("lowercase name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectorySkillSourceBoundsDescriptorFreeDirectoryScanning()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "one", "two", "three"));

        var exception = Assert.Throws<GameRuntimeLimitException>(() =>
            new DirectoryGameSkillSource(directory.Path, maximumScannedDirectories: 2));

        Assert.Equal("maximumScannedDirectories", exception.Limit);
    }

    [Fact]
    public async Task DirectorySkillSourceRejectsDuplicateFrontMatterKeys()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "building");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: first\nname: second\ndescription: Duplicate metadata.\n---\nInstructions.",
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<PersistenceException>(() => new DirectoryGameSkillSource(directory.Path));

        Assert.Contains("duplicate YAML metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectorySkillSourceRejectsCaseInsensitiveDuplicateJsonProperties()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "building");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "skill.json"),
            "{\"id\":\"first\",\"ID\":\"second\",\"name\":\"Building\"}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "instructions.md"),
            "Instructions.",
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<PersistenceException>(() => new DirectoryGameSkillSource(directory.Path));

        Assert.Contains("duplicate JSON properties", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectorySkillSourceUsesAFreshBoundedSnapshotPerSelection()
    {
        using var directory = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(directory.Path, "dialogue");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skillDirectory, "skill.json"),
            "{\"id\":\"dialogue\",\"name\":\"Dialogue\",\"inputTypes\":[\"chat\"]}",
            TestContext.Current.CancellationToken);
        var instructionsPath = System.IO.Path.Combine(skillDirectory, "instructions.md");
        await File.WriteAllTextAsync(instructionsPath, "First version.", TestContext.Current.CancellationToken);
        var source = new DirectoryGameSkillSource(directory.Path);
        var query = new GameSkillQuery(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            Array.Empty<string>(),
            1);

        var first = Assert.Single(await source.SelectAsync(query, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(instructionsPath, "Second version.", TestContext.Current.CancellationToken);
        var second = Assert.Single(await source.SelectAsync(query, TestContext.Current.CancellationToken));

        Assert.Equal("First version.", first.Instructions);
        Assert.Equal("Second version.", second.Instructions);
    }

    [Fact]
    public async Task DirectorySkillSourceLoadsInstructionsOnlyAfterManifestSelection()
    {
        using var directory = new TemporaryDirectory();
        var selectedDirectory = System.IO.Path.Combine(directory.Path, "selected");
        var ignoredDirectory = System.IO.Path.Combine(directory.Path, "ignored");
        Directory.CreateDirectory(selectedDirectory);
        Directory.CreateDirectory(ignoredDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(selectedDirectory, "skill.json"),
            "{\"id\":\"selected\",\"name\":\"Selected\",\"inputTypes\":[\"chat\"]}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(selectedDirectory, "instructions.md"),
            "Use the selected instructions.",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(ignoredDirectory, "skill.json"),
            "{\"id\":\"ignored\",\"name\":\"Ignored\",\"inputTypes\":[\"combat\"]}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(ignoredDirectory, "instructions.md"),
            new string('x', 256),
            TestContext.Current.CancellationToken);
        var source = new DirectoryGameSkillSource(directory.Path, maximumInstructionsCharacters: 64);

        var selected = await source.SelectAsync(
            new GameSkillQuery(
                new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
                Array.Empty<string>(),
                1),
            TestContext.Current.CancellationToken);

        Assert.Equal("selected", Assert.Single(selected).SkillId);
    }

    [Fact]
    public async Task MailboxLeaseAndGameMomentSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path);
        var message = new GameMailboxMessage(
            "message",
            "session",
            "npc",
            "observation",
            "{\"distance\":1.75}",
            new GameMoment("world", 42, "{\"month\":5}"),
            senderId: "player",
            correlationId: "event");
        Assert.True(await mailbox.EnqueueAsync(message, TestContext.Current.CancellationToken));

        var firstNow = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = Assert.Single(await mailbox.ClaimAsync(
            "session",
            "npc",
            1,
            firstNow,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        var restarted = new FileGameMailbox(directory.Path);
        Assert.Empty(await restarted.ClaimAsync(
            "session",
            "npc",
            1,
            firstNow.AddSeconds(30),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));

        var reclaimed = Assert.Single(await restarted.ClaimAsync(
            "session",
            "npc",
            1,
            firstNow.AddMinutes(2),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal(42, reclaimed.Message.Moment.Tick);
        Assert.Contains("1.75", reclaimed.Message.PayloadJson, StringComparison.Ordinal);
        await restarted.CompleteAsync(
            reclaimed.Message.MessageId,
            reclaimed.LeaseToken,
            TestContext.Current.CancellationToken);

        var finalRestart = new FileGameMailbox(directory.Path);
        Assert.Empty(await finalRestart.ClaimAsync(
            "session",
            "npc",
            1,
            firstNow.AddDays(1),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await finalRestart.CompleteAsync(
                first.Message.MessageId,
                first.LeaseToken,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMailboxPendingStatusIsReadOnlyAndSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path);
        var recipient = new GameMailboxRecipientKey("session", "npc");
        var other = new GameMailboxRecipientKey("session", "other");
        var missing = new GameMailboxRecipientKey("missing", "npc");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("mail-a", "session", "npc", "event", "{\"secret\":1}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("mail-b", "session", "npc", "event", "{\"secret\":2}", new GameMoment("world", 2)),
            TestContext.Current.CancellationToken);
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("mail-c", "session", "other", "event", "{}", new GameMoment("world", 3)),
            TestContext.Current.CancellationToken);

        var requested = new[] { recipient, other, missing };
        var initial = await mailbox.GetPendingStatusAsync(
            requested,
            now,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, initial[0].ReadyCount);
        Assert.Equal(1, initial[1].ReadyCount);
        Assert.Equal(0, initial[2].IncompleteCount);

        var delivery = Assert.Single(await mailbox.ClaimAsync(
            recipient.SessionId,
            recipient.RecipientId,
            1,
            now,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, delivery.Attempt);

        var restarted = new FileGameMailbox(directory.Path);
        var activeLease = await restarted.GetPendingStatusAsync(
            requested,
            now.AddSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, activeLease[0].ReadyCount);
        Assert.Equal(1, activeLease[0].LeasedCount);
        Assert.Equal(2, activeLease[0].IncompleteCount);
        Assert.Equal(1, activeLease[1].ReadyCount);

        await restarted.AbandonAsync(
            delivery.Message.MessageId,
            delivery.LeaseToken,
            TestContext.Current.CancellationToken);
        var abandoned = Assert.Single(await restarted.GetPendingStatusAsync(
            new[] { recipient },
            now,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, abandoned.ReadyCount);
        Assert.Equal(0, abandoned.LeasedCount);

        var claimed = await restarted.ClaimAsync(
            recipient.SessionId,
            recipient.RecipientId,
            2,
            now,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, claimed.Count);
        Assert.Contains(claimed, item => item.Message.MessageId == delivery.Message.MessageId && item.Attempt == 2);
        Assert.Contains(claimed, item => item.Message.MessageId != delivery.Message.MessageId && item.Attempt == 1);
        foreach (var item in claimed)
        {
            await restarted.CompleteAsync(
                item.Message.MessageId,
                item.LeaseToken,
                TestContext.Current.CancellationToken);
        }

        var completed = Assert.Single(await new FileGameMailbox(directory.Path).GetPendingStatusAsync(
            new[] { recipient },
            now,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, completed.IncompleteCount);
    }

    [Fact]
    public async Task FileMailboxPendingBatchReturnsEveryRequestedRecipient()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path, capacity: 512);
        var recipients = Enumerable.Range(0, 128)
            .Select(index => new GameMailboxRecipientKey("session", "npc-" + index))
            .ToArray();
        for (var index = 0; index < recipients.Length; index++)
        {
            await mailbox.EnqueueAsync(
                new GameMailboxMessage(
                    "mail-" + index,
                    recipients[index].SessionId,
                    recipients[index].RecipientId,
                    "event",
                    "{}",
                    new GameMoment("world", index)),
                TestContext.Current.CancellationToken);
        }

        var statuses = await mailbox.GetPendingStatusAsync(
            recipients,
            DateTimeOffset.UnixEpoch,
            TestContext.Current.CancellationToken);

        Assert.Equal(recipients.Length, statuses.Count);
        Assert.All(statuses, status =>
        {
            Assert.Equal(1, status.ReadyCount);
            Assert.Equal(0, status.LeasedCount);
            Assert.Equal(1, status.IncompleteCount);
        });
        Assert.Equal(recipients, statuses.Select(status => status.Recipient));
    }

    [Fact]
    public async Task FileMailboxPendingStatusRejectsCorruptState()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path);
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("mail", "session", "npc", "event", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.mailbox.json"));
        await File.WriteAllTextAsync(path, "{\"formatVersion\":1", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await mailbox.GetPendingStatusAsync(
                new[] { new GameMailboxRecipientKey("session", "npc") },
                DateTimeOffset.UnixEpoch,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndependentMailboxWorkersCannotClaimTheSameMessageLease()
    {
        using var directory = new TemporaryDirectory();
        var writer = new FileGameMailbox(directory.Path);
        await writer.EnqueueAsync(
            new GameMailboxMessage("message", "session", "npc", "signal", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);
        var first = new FileGameMailbox(directory.Path);
        var second = new FileGameMailbox(directory.Path);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var claims = await Task.WhenAll(
            first.ClaimAsync(
                "session",
                "npc",
                1,
                now,
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken).AsTask(),
            second.ClaimAsync(
                "session",
                "npc",
                1,
                now,
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, claims.Sum(claim => claim.Count));
    }

    [Fact]
    public async Task MailboxDeduplicatesEquivalentMessagesAndRejectsIdentityReuse()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path);
        var message = new GameMailboxMessage(
            "message",
            "session",
            "npc",
            "signal",
            "{\"value\":2.5}",
            new GameMoment("world", 3));

        Assert.True(await mailbox.EnqueueAsync(message, TestContext.Current.CancellationToken));
        Assert.False(await mailbox.EnqueueAsync(message, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mailbox.EnqueueAsync(
                new GameMailboxMessage(
                    "message",
                    "session",
                    "npc",
                    "signal",
                    "{\"value\":9.5}",
                    new GameMoment("world", 3)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMailboxRejectsOverflowingOperationalLeaseWithoutWritingPartialState()
    {
        using var directory = new TemporaryDirectory();
        var mailbox = new FileGameMailbox(directory.Path);
        await mailbox.EnqueueAsync(
            new GameMailboxMessage("message", "session", "npc", "signal", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await mailbox.ClaimAsync(
                "session",
                "npc",
                1,
                DateTimeOffset.MaxValue,
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));
        var claimed = Assert.Single(await mailbox.ClaimAsync(
            "session",
            "npc",
            1,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));

        Assert.Equal(1, claimed.Attempt);
    }

    [Fact]
    public async Task DelegationStateSurvivesRestartAndRejectsStaleRevision()
    {
        using var directory = new TemporaryDirectory();
        var input = new GameInput(
            "session",
            "actor",
            "delegate",
            "{\"source\":\"test\"}",
            new GameMoment("world", 4),
            "parent-input",
            new Dictionary<string, string> { ["trusted.scope"] = "bounded" });
        var request = new GameAgentDelegateRequest(
            "delegation",
            input,
            "{\"task\":\"inspect\"}",
            1,
            7,
            inheritContext: true,
            new[] { AgentMessage.User("context", DateTimeOffset.UnixEpoch) },
            GameExecutionScope.Restricted(new[] { GameExecutionCapabilities.PersistentPlanning }));
        var pending = new GameAgentDelegationRecord(
            "delegation",
            "session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{\"task\":\"inspect\"}",
            1,
            new GameMoment("world", 4),
            request: request);
        var store = new FileGameAgentDelegationStore(directory.Path);
        Assert.True((await store.SaveAsync(pending, 0, TestContext.Current.CancellationToken)).Saved);

        var restarted = new FileGameAgentDelegationStore(directory.Path);
        var loaded = await restarted.LoadAsync("session", "actor", "delegation", TestContext.Current.CancellationToken);
        var stale = await restarted.SaveAsync(pending, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("{\"task\":\"inspect\"}", loaded.TaskJson);
        Assert.Equal(4, loaded.CreatedAt.Tick);
        Assert.NotNull(loaded.Request);
        Assert.Equal("parent-input", loaded.Request.ParentInput.InputId);
        Assert.True(loaded.Request.InheritContext);
        Assert.Single(loaded.Request.ParentMessages);
        Assert.True(loaded.Request.ExecutionScope.Allows(GameExecutionCapabilities.PersistentPlanning));
        Assert.Single(await restarted.ListRecoverableAsync(
            DateTimeOffset.UtcNow,
            10,
            TestContext.Current.CancellationToken));
        Assert.False(stale.Saved);
        Assert.Equal(1, stale.Current.Revision);

        var otherSession = new GameAgentDelegationRecord(
            "delegation",
            "other-session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{\"task\":\"other\"}",
            1,
            new GameMoment("world", 4));
        Assert.True((await restarted.SaveAsync(otherSession, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.Equal(
            "{\"task\":\"other\"}",
            (await restarted.LoadAsync("other-session", "actor", "delegation", TestContext.Current.CancellationToken))!.TaskJson);
    }

    [Fact]
    public async Task LargeArtifactSurvivesRestartWithoutChangingFloatingPointJson()
    {
        using var directory = new TemporaryDirectory();
        var artifact = new GameAgentArtifact(
            "artifact",
            "session",
            "actor",
            "application/json",
            "{\"position\":1.75}",
            new GameMoment("world", 8));
        var store = new FileGameAgentArtifactStore(directory.Path);
        await store.PutAsync(artifact, TestContext.Current.CancellationToken);

        var restarted = new FileGameAgentArtifactStore(directory.Path);
        var loaded = await restarted.GetAsync("session", "actor", "artifact", TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("{\"position\":1.75}", loaded.Content);
        Assert.Equal(8, loaded.CreatedAt.Tick);
        var otherSession = new GameAgentArtifact(
            "artifact",
            "other-session",
            "actor",
            "application/json",
            "{\"position\":2.5}",
            new GameMoment("world", 8));
        await restarted.PutAsync(otherSession, TestContext.Current.CancellationToken);
        Assert.Equal(
            "{\"position\":2.5}",
            (await restarted.GetAsync("other-session", "actor", "artifact", TestContext.Current.CancellationToken))!.Content);
        await restarted.PutAsync(artifact, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await restarted.PutAsync(
                new GameAgentArtifact(
                    "artifact",
                    "session",
                    "actor",
                    "application/json",
                    "{\"position\":9.25}",
                    new GameMoment("world", 8)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActionConflictClaimSurvivesRestartAndReleasesOnlyAfterFinalReceipt()
    {
        using var directory = new TemporaryDirectory();
        var first = ConflictIntent("first", "session-a", "actor-a");
        var second = ConflictIntent("second", "session-b", "actor-b");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(first, TestContext.Current.CancellationToken);
        await journal.ReserveAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(
            GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(first.OperationId, TestContext.Current.CancellationToken)).Status);

        var restarted = new FileGameActionJournal(directory.Path);
        var blocked = await restarted.ClaimDispatchAsync(second.OperationId, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Blocked, blocked.Status);
        Assert.Equal(first.OperationId, blocked.BlockingOperationId);

        await restarted.SaveReceiptAsync(
            GameActionReceipt.Committed(first, "{}", 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GameActionDispatchClaimStatus.Claimed,
            (await restarted.ClaimDispatchAsync(second.OperationId, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task ConcurrentFileJournalsAtomicallyClaimOneConflictOwner()
    {
        using var directory = new TemporaryDirectory();
        var firstJournal = new FileGameActionJournal(directory.Path);
        var secondJournal = new FileGameActionJournal(directory.Path);
        var first = ConflictIntent("first", "session-a", "actor-a");
        var second = ConflictIntent("second", "session-b", "actor-b");
        await firstJournal.ReserveAsync(first, TestContext.Current.CancellationToken);
        await secondJournal.ReserveAsync(second, TestContext.Current.CancellationToken);

        var claims = await Task.WhenAll(
            firstJournal.ClaimDispatchAsync(first.OperationId, TestContext.Current.CancellationToken).AsTask(),
            secondJournal.ClaimDispatchAsync(second.OperationId, TestContext.Current.CancellationToken).AsTask());

        Assert.Single(claims, claim => claim.Status == GameActionDispatchClaimStatus.Claimed);
        Assert.Single(claims, claim => claim.Status == GameActionDispatchClaimStatus.Blocked);
    }

    [Fact]
    public async Task ActionConflictStateRejectsCorruptionAndReadsPreviousIntentFormatAsUnscoped()
    {
        using var directory = new TemporaryDirectory();
        var first = ConflictIntent("first", "session-a", "actor-a");
        var second = ConflictIntent("second", "session-b", "actor-b");
        var journal = new FileGameActionJournal(directory.Path);
        await journal.ReserveAsync(first, TestContext.Current.CancellationToken);
        var firstPath = Assert.Single(Directory.GetFiles(directory.Path, "*.action.json"));
        var previous = JsonNode.Parse(await File.ReadAllTextAsync(
            firstPath,
            TestContext.Current.CancellationToken))!.AsObject();
        previous["FormatVersion"] = 2;
        previous["Intent"]!.AsObject().Remove("ConflictKey");
        await File.WriteAllTextAsync(
            firstPath,
            previous.ToJsonString(),
            TestContext.Current.CancellationToken);
        Assert.Null((await new FileGameActionJournal(directory.Path).FindAsync(
            first.OperationId,
            TestContext.Current.CancellationToken))!.Intent.ConflictKey);

        using var corruptDirectory = new TemporaryDirectory();
        var corruptJournal = new FileGameActionJournal(corruptDirectory.Path);
        await corruptJournal.ReserveAsync(first, TestContext.Current.CancellationToken);
        await corruptJournal.ReserveAsync(second, TestContext.Current.CancellationToken);
        await corruptJournal.ClaimDispatchAsync(first.OperationId, TestContext.Current.CancellationToken);
        var conflictPath = Assert.Single(Directory.GetFiles(corruptDirectory.Path, "*.action-conflict.json"));
        await File.WriteAllTextAsync(conflictPath, "{broken", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PersistenceException>(async () =>
            await new FileGameActionJournal(corruptDirectory.Path).ClaimDispatchAsync(
                second.OperationId,
                TestContext.Current.CancellationToken));
    }

    private static GameActionIntent Intent(string operationId) =>
        new(operationId, "input", "session", "actor", "move", "{\"x\":1.5}", new GameMoment("world", 4));

    private static GameActionIntent ConflictIntent(string operationId, string sessionId, string actorId) =>
        new(
            operationId,
            "input-" + operationId,
            sessionId,
            actorId,
            "move",
            "{\"x\":1.5}",
            new GameMoment("world", 4),
            generationId: "save",
            conflictKey: "world:resource");

    private static async Task SetFormatVersionAsync(string path, int formatVersion)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken))!.AsObject();
        document["FormatVersion"] = formatVersion;
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(),
            TestContext.Current.CancellationToken);
    }

    private static async Task WriteLegacyMemoryAsync(
        string directory,
        string sessionId,
        string ownerId,
        string memoryId,
        string searchableText)
    {
        var path = System.IO.Path.Combine(
            directory,
            HashString(sessionId + "\n" + ownerId + "\n" + memoryId) + ".memory.json");
        await WriteMemoryDocumentAsync(path, sessionId, ownerId, memoryId, searchableText);
    }

    private static async Task WriteMemoryDocumentAsync(
        string path,
        string sessionId,
        string ownerId,
        string memoryId,
        string searchableText)
    {
        var document = new
        {
            FormatVersion = 1,
            MemoryId = memoryId,
            SessionId = sessionId,
            OwnerId = ownerId,
            Scope = "fact",
            Kind = nameof(GameMemoryKind.Fact),
            PayloadJson = JsonSerializer.Serialize(new { text = searchableText }),
            Moment = new { TimelineId = "world", Tick = 1L, CalendarJson = (string?)null },
            Importance = 0.5,
            SearchableText = searchableText,
            Tags = Array.Empty<string>(),
            SourceInputId = (string?)null,
            ExpiresAt = (object?)null,
            Metadata = new Dictionary<string, string>(),
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document),
            TestContext.Current.CancellationToken);
    }

    private static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class CallbackActionHandler : IGameActionHandler
    {
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>> _execute;
        private readonly Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>> _recover;

        public CallbackActionHandler(
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt>> execute,
            Func<GameActionIntent, CancellationToken, ValueTask<GameActionReceipt?>> recover)
        {
            _execute = execute;
            _recover = recover;
        }

        public ValueTask<GameActionReceipt> ExecuteAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken) => _execute(intent, cancellationToken);

        public ValueTask<GameActionReceipt?> RecoverAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken) => _recover(intent, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGameAgent.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGameAgent.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test root.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
