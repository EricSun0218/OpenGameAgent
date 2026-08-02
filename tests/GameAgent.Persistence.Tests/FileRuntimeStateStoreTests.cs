using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;

namespace GameAgent.Persistence.Tests;

public sealed class FileRuntimeStateStoreTests
{
    [Fact]
    public async Task All_runtime_state_stores_recover_committed_state()
    {
        var root = TempDirectory();
        try
        {
            var baselinePath = Path.Combine(root, "context.state");
            await using (var store = new FileModelContextSectionBaselineStore(baselinePath))
            {
                await store.PutAsync(Baseline("actor:one", 3.5), null, default);
            }

            await using (var recovered = new FileModelContextSectionBaselineStore(baselinePath))
            {
                var baseline = await recovered.TryGetAsync("actor:one", default);
                Assert.Equal(3.5, baseline!.Content.GetProperty("trust").GetDouble());
            }

            var attentionPath = Path.Combine(root, "attention.state");
            await using (var store = new FileExternalAttentionStore(attentionPath))
            {
                var coordinator = new ExternalAttentionCoordinator(store);
                await coordinator.RequestAsync(Attention("ask-1"));
            }

            await using (var recovered = new FileExternalAttentionStore(attentionPath))
            {
                var pending = await recovered.ListPendingAsync("world", 10, default);
                Assert.Equal("ask-1", Assert.Single(pending).Request.RequestId);
            }

            var triggerPath = Path.Combine(root, "trigger.state");
            await using (var store = new FileGameTriggerStateStore(triggerPath))
            {
                await store.PutAsync(new GameTriggerState
                {
                    StateKey = "month:world",
                    TriggerId = "month",
                    ScopeKey = "world",
                    LastOccurrenceSequence = 12,
                    Revision = 1
                }, null, default);
            }

            await using (var recovered = new FileGameTriggerStateStore(triggerPath))
            {
                Assert.Equal(12, (await recovered.TryGetAsync("month:world", default))!
                    .LastOccurrenceSequence);
            }

            var budgetPath = Path.Combine(root, "budget.state");
            await using (var store = new FileHierarchicalBudgetStore(budgetPath))
            {
                var ledger = new HierarchicalBudgetLedger(store);
                await ledger.DefineScopeAsync(new HierarchicalBudgetScope
                {
                    ScopeId = "world",
                    Kind = BudgetScopeKinds.World,
                    Limit = new HierarchicalBudgetLimit { MaxOutputTokens = 100 }
                });
                await ledger.ChargeAsync(new HierarchicalBudgetCharge
                {
                    ChargeId = "charge-1",
                    ScopeId = "world",
                    Amount = new HierarchicalBudgetAmount { OutputTokens = 25 }
                });
            }

            await using (var recovered = new FileHierarchicalBudgetStore(budgetPath))
            {
                var state = await recovered.ReadAsync(default);
                Assert.Equal(25, Assert.Single(state.Scopes).Used.OutputTokens);
                Assert.Single(state.Charges);
            }

            var graphPath = Path.Combine(root, "graph.state");
            await using (var store = new FilePersistentAgentGraphStore(graphPath))
            {
                var graph = new PersistentAgentGraph(store);
                await graph.RegisterAsync(Node("npc"));
                await graph.EnqueueAsync("npc", Mail("message", 2.75));
            }

            await using (var recovered = new FilePersistentAgentGraphStore(graphPath))
            {
                var node = await new PersistentAgentGraph(recovered).TryGetAsync("npc");
                Assert.Equal(2.75, Assert.Single(node!.Mailbox)
                    .Payload.GetProperty("value").GetDouble());
            }

            var distillationPath = Path.Combine(root, "distillation.state");
            await using (var store = new FileMemoryDistillationStore(distillationPath))
            {
                await store.PutAsync(Distilled("distill-1"), null, default);
            }

            await using (var recovered = new FileMemoryDistillationStore(distillationPath))
            {
                var record = await recovered.TryGetAsync("distill-1", default);
                Assert.Equal(3.5, record!.Content.GetProperty("weight").GetDouble());
                Assert.Single(record.Citations);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Torn_tail_is_truncated_to_last_fully_flushed_frame()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "graph.state");
        try
        {
            await using (var store = new FilePersistentAgentGraphStore(path))
            {
                await new PersistentAgentGraph(store).RegisterAsync(Node("npc"));
            }

            var committedLength = new FileInfo(path).Length;
            await using (var tail = new FileStream(
                             path,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.None))
            {
                await tail.WriteAsync(new byte[] { 0x47, 0x41, 0x53 });
                await tail.FlushAsync();
            }
            Assert.True(new FileInfo(path).Length > committedLength);

            await using var recovered = new FilePersistentAgentGraphStore(path);
            Assert.NotNull(await new PersistentAgentGraph(recovered).TryGetAsync("npc"));
            Assert.Equal(committedLength, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Interior_corruption_is_rejected_instead_of_silently_rolled_back()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "graph.state");
        try
        {
            await using (var store = new FilePersistentAgentGraphStore(path))
            {
                var graph = new PersistentAgentGraph(store);
                await graph.RegisterAsync(Node("a"));
                await graph.RegisterAsync(Node("b"));
            }

            var bytes = await File.ReadAllBytesAsync(path);
            bytes[20] ^= 0x5A;
            await File.WriteAllBytesAsync(path, bytes);

            var exception = Assert.Throws<DurableLocalStateFileException>(
                () => new FilePersistentAgentGraphStore(path));
            Assert.Equal("durable_state_corrupt", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fully_written_latest_frame_with_bad_checksum_is_rejected()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "graph.state");
        try
        {
            await using (var store = new FilePersistentAgentGraphStore(path))
            {
                await new PersistentAgentGraph(store).RegisterAsync(Node("npc"));
            }

            var bytes = await File.ReadAllBytesAsync(path);
            bytes[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(path, bytes);

            var exception = Assert.Throws<DurableLocalStateFileException>(
                () => new FilePersistentAgentGraphStore(path));
            Assert.Equal("durable_state_corrupt", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_is_idempotent_while_new_operations_fail_closed()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "graph.state");
        try
        {
            var store = new FilePersistentAgentGraphStore(path);
            await store.DisposeAsync();
            await store.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await store.ReadAsync(default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Second_writer_for_same_state_path_is_rejected()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "graph.state");
        try
        {
            await using var first = new FilePersistentAgentGraphStore(path);
            Assert.Throws<IOException>(() => new FilePersistentAgentGraphStore(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ModelContextSectionBaseline Baseline(string key, double trust) =>
        new()
        {
            BaselineKey = key,
            ViewKey = "session:test",
            SectionId = "actor",
            SchemaVersion = "1",
            Scope = ModelContextSectionScopes.Actor,
            ScopeKey = "one",
            ModelCapabilitiesDigest = new string('a', 64),
            Revision = 1,
            Content = Json($"{{\"trust\":{trust.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"),
            ContentDigest = new string('b', 64)
        };

    private static ExternalAttentionRequest Attention(string id) => new()
    {
        RequestId = id,
        Kind = "player_choice",
        WorldId = "world",
        AuthorityId = "host",
        StateBindingDigest = new string('c', 64),
        Payload = Json("{\"choice\":3.5}"),
        CreatedAt = new GameTimePoint("calendar", "main", 0, 10)
    };

    private static PersistentAgentNode Node(string id) => new()
    {
        AgentId = id,
        WorldId = "world",
        HistoryId = "history-" + id,
        ContextInheritancePolicy = AgentContextInheritancePolicies.Empty
    };

    private static AgentMailboxMessage Mail(string id, double value) => new()
    {
        MessageId = id,
        Kind = "observation",
        OrderingKey = 1,
        Payload = Json($"{{\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")
    };

    private static DistilledMemoryRecord Distilled(string id) => new()
    {
        DistillationId = id,
        MemoryId = "memory-1",
        Scope = "actor:npc",
        Content = Json("{\"weight\":3.5}"),
        Citations = new[]
        {
            new MemoryEvidenceCitation
            {
                MemoryId = "source-1",
                ContentDigest = new string('d', 64)
            }
        },
        Salience = 75,
        Confidence = 80,
        CreatedAt = new GameTimePoint("calendar", "main", 0, 10),
        Revision = 1
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "game-agent-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
