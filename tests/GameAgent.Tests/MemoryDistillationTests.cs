using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class MemoryDistillationTests
{
    [Fact]
    public async Task Distillation_requires_exact_source_citations_and_preserves_float_content()
    {
        var source = Source("event-1", "{\"affinity\":3.5}");
        var distiller = new FakeDistiller(source, citationDigest: null);
        var coordinator = new MemoryDistillationCoordinator(
            new InMemoryMemoryDistillationStore(),
            distiller);

        var result = await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3.5, result.Content.GetProperty("affinity").GetDouble());
        Assert.Equal(source.MemoryId, Assert.Single(result.Citations).MemoryId);
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public async Task Distillation_rejects_fabricated_evidence_digest()
    {
        var source = Source("event-1", "{\"fact\":true}");
        var coordinator = new MemoryDistillationCoordinator(
            new InMemoryMemoryDistillationStore(),
            new FakeDistiller(source, new string('f', 64)));

        var exception = await Assert.ThrowsAsync<MemoryDistillationException>(
            async () => await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("memory_distillation_evidence_invalid", exception.ReasonCode);
    }

    [Fact]
    public async Task Recall_and_retention_use_game_time_not_wall_clock()
    {
        var source = Source("event-1", "{\"fact\":true}");
        var coordinator = new MemoryDistillationCoordinator(
            new InMemoryMemoryDistillationStore(),
            new FakeDistiller(source, null));
        await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken);

        var used = await coordinator.RecordRecallAsync(
            "distill-1",
            new GameTimePoint("calendar", "main", 0, 11), cancellationToken: TestContext.Current.CancellationToken);
        var before = await coordinator.RetireDueAsync(
            "actor:npc",
            new GameTimePoint("calendar", "main", 0, 19), cancellationToken: TestContext.Current.CancellationToken);
        var due = await coordinator.RetireDueAsync(
            "actor:npc",
            new GameTimePoint("calendar", "main", 0, 20), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, used.UsageCount);
        Assert.Empty(before);
        Assert.Equal(DistilledMemoryStates.Retired, Assert.Single(due).State);
    }

    [Fact]
    public async Task Concurrent_recalls_are_retried_without_losing_usage()
    {
        var source = Source("event-1", "{\"fact\":true}");
        var store = new InMemoryMemoryDistillationStore();
        var coordinator = new MemoryDistillationCoordinator(
            store,
            new FakeDistiller(source, null));
        await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.RecordRecallAsync(
                "distill-1",
                new GameTimePoint("calendar", "main", 0, 11)).AsTask()));

        var current = await store.TryGetAsync("distill-1", TestContext.Current.CancellationToken);
        Assert.Equal(16, current!.UsageCount);
    }

    [Fact]
    public async Task Concurrent_identical_distillations_are_idempotent()
    {
        const int callers = 16;
        var source = Source("event-1", "{\"fact\":true}");
        var store = new InMemoryMemoryDistillationStore();
        var coordinator = new MemoryDistillationCoordinator(
            store,
            new GatedDistiller(new FakeDistiller(source, null), callers));

        var records = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            coordinator.DistillAsync(Request(source)).AsTask()));

        Assert.All(records, record => Assert.Equal(1, record.Revision));
        Assert.Single(records.Select(record => record.DistillationId).Distinct());
        Assert.NotNull(await store.TryGetAsync("distill-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retention_query_does_not_starve_due_low_salience_records()
    {
        var store = new InMemoryMemoryDistillationStore();
        for (var index = 0; index < 300; index++)
        {
            await store.PutAsync(new DistilledMemoryRecord
            {
                DistillationId = $"future-{index:D3}",
                MemoryId = $"memory-{index:D3}",
                Scope = "actor:npc",
                Content = Json("{\"future\":true}"),
                Salience = 100,
                Confidence = 100,
                CreatedAt = new GameTimePoint("calendar", "main", 0, 1),
                RetainUntil = new GameTimePoint("calendar", "main", 0, 100),
                Revision = 1
            }, null, TestContext.Current.CancellationToken);
        }

        await store.PutAsync(new DistilledMemoryRecord
        {
            DistillationId = "due",
            MemoryId = "due-memory",
            Scope = "actor:npc",
            Content = Json("{\"due\":true}"),
            Salience = 0,
            Confidence = 50,
            CreatedAt = new GameTimePoint("calendar", "main", 0, 1),
            RetainUntil = new GameTimePoint("calendar", "main", 0, 2),
            Revision = 1
        }, null, TestContext.Current.CancellationToken);
        var coordinator = new MemoryDistillationCoordinator(store);

        var retired = await coordinator.RetireDueAsync(
            "actor:npc",
            new GameTimePoint("calendar", "main", 0, 2),
            maximumCount: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("due", Assert.Single(retired).DistillationId);
    }

    [Fact]
    public async Task Distillation_identity_includes_citation_provenance()
    {
        var source = Source("event-1", "{\"fact\":true}");
        var distiller = new FakeDistiller(source, null) { SourceEventId = "source-event-1" };
        var coordinator = new MemoryDistillationCoordinator(
            new InMemoryMemoryDistillationStore(),
            distiller);
        await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken);
        distiller.SourceEventId = "source-event-2";

        var exception = await Assert.ThrowsAsync<MemoryDistillationException>(
            async () => await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("memory_distillation_identity_conflict", exception.ReasonCode);
    }

    [Fact]
    public async Task Distiller_cannot_forge_prior_usage_or_state()
    {
        var source = Source("event-1", "{\"fact\":true}");
        var distiller = new FakeDistiller(source, null)
        {
            Mutate = record =>
            {
                record.UsageCount = 9;
                record.State = DistilledMemoryStates.Polluted;
            }
        };
        var coordinator = new MemoryDistillationCoordinator(
            new InMemoryMemoryDistillationStore(),
            distiller);

        var exception = await Assert.ThrowsAsync<MemoryDistillationException>(
            async () => await coordinator.DistillAsync(Request(source), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("memory_distillation_evidence_invalid", exception.ReasonCode);
    }

    private static MemoryDistillationRequest Request(MemoryRecord source) => new()
    {
        DistillationId = "distill-1",
        TargetMemoryId = "summary-1",
        Scope = "actor:npc",
        Sources = new[] { source },
        GameTime = new GameTimePoint("calendar", "main", 0, 10),
        Instructions = Json("{\"mode\":\"episode\"}")
    };

    private static MemoryRecord Source(string id, string content) => new(
        id,
        "actor:npc",
        Json(content),
        tags: new[] { "event" },
        importance: 50,
        createdAt: DateTimeOffset.UnixEpoch,
        updatedAt: DateTimeOffset.UnixEpoch);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeDistiller : IMemoryDistiller
    {
        private readonly MemoryRecord _source;
        private readonly string? _citationDigest;

        public FakeDistiller(MemoryRecord source, string? citationDigest)
        {
            _source = source;
            _citationDigest = citationDigest;
        }

        public string DistillerId => "fake";

        public string? SourceEventId { get; set; }

        public Action<DistilledMemoryRecord>? Mutate { get; set; }

        public ValueTask<DistilledMemoryRecord> DistillAsync(
            MemoryDistillationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new DistilledMemoryRecord
            {
                DistillationId = request.DistillationId,
                MemoryId = request.TargetMemoryId,
                Scope = request.Scope,
                Content = _source.Content.Clone(),
                Citations = new[]
                {
                    new MemoryEvidenceCitation
                    {
                        MemoryId = _source.MemoryId,
                        ContentDigest = _citationDigest
                            ?? CanonicalJsonDigest.ComputeSha256(_source.Content),
                        SourceEventId = SourceEventId
                    }
                },
                Salience = 80,
                Confidence = 90,
                CreatedAt = request.GameTime,
                RetainUntil = new GameTimePoint("calendar", "main", 0, 20)
            };
            Mutate?.Invoke(record);
            return new ValueTask<DistilledMemoryRecord>(record);
        }
    }

    private sealed class GatedDistiller : IMemoryDistiller
    {
        private readonly IMemoryDistiller _inner;
        private readonly int _expectedCalls;
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public GatedDistiller(IMemoryDistiller inner, int expectedCalls)
        {
            _inner = inner;
            _expectedCalls = expectedCalls;
        }

        public string DistillerId => "gated";

        public async ValueTask<DistilledMemoryRecord> DistillAsync(
            MemoryDistillationRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == _expectedCalls)
            {
                _release.TrySetResult(true);
            }

            await _release.Task.WaitAsync(cancellationToken);
            return await _inner.DistillAsync(request, cancellationToken);
        }
    }
}
