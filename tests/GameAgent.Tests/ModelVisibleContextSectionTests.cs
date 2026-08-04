using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class ModelVisibleContextSectionTests
{
    [Fact]
    public async Task Committed_baseline_produces_small_verified_delta()
    {
        var store = new InMemoryModelContextSectionBaselineStore();
        var coordinator = new ModelContextSectionCoordinator(store);
        var contributor = new MutableContributor(
            "actor_state",
            1,
            Json("{\"health\":10,\"speed\":3.5,\"inventory\":[1,2]}"));

        var first = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ModelContextDisclosureModes.Full, first.Mode);
        await coordinator.CommitAsync(first, cancellationToken: TestContext.Current.CancellationToken);

        contributor.Advance(
            2,
            1,
            Json("{\"health\":9,\"speed\":3.5,\"inventory\":[1,2]}"));
        var second = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelContextDisclosureModes.MergePatch, second.Mode);
        Assert.Equal(9, second.Payload.GetProperty("health").GetInt32());
        Assert.False(second.Payload.TryGetProperty("inventory", out _));
        Assert.Equal(
            second.TargetDigest,
            CanonicalJsonDigest.ComputeSha256(
                JsonMergePatch.Apply(first.Payload, second.Payload)));
    }

    [Fact]
    public async Task Timeline_change_and_explicit_null_force_full_disclosure()
    {
        var store = new InMemoryModelContextSectionBaselineStore();
        var coordinator = new ModelContextSectionCoordinator(store);
        var contributor = new MutableContributor(
            "world_state",
            1,
            Json("{\"weather\":\"rain\",\"omen\":\"crow\"}"));
        var first = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));
        await coordinator.CommitAsync(first, cancellationToken: TestContext.Current.CancellationToken);

        contributor.TimelineId = "fork-2";
        contributor.Advance(
            2,
            1,
            Json("{\"weather\":\"rain\",\"omen\":null}"));
        var changed = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelContextDisclosureModes.Full, changed.Mode);
        Assert.Equal(JsonValueKind.Null, changed.Payload.GetProperty("omen").ValueKind);
    }

    [Fact]
    public async Task Large_section_set_only_discloses_changed_actor_delta()
    {
        var store = new InMemoryModelContextSectionBaselineStore();
        var coordinator = new ModelContextSectionCoordinator(store);
        var contributors = Enumerable.Range(0, 20)
            .Select(index => new MutableContributor(
                "section_" + index,
                1,
                Json("{\"actor\":" + index + ",\"blob\":\""
                     + new string((char)('a' + index % 26), 24_000)
                     + "\",\"value\":1.25}"),
                scopeKey: "actor-" + index))
            .ToArray();
        var initial = await coordinator.PrepareAsync(contributors, Request(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initial.Sum(item => item.PayloadUtf8Bytes) > 450_000);
        foreach (var disclosure in initial)
        {
            await coordinator.CommitAsync(disclosure, cancellationToken: TestContext.Current.CancellationToken);
        }

        contributors[7].Advance(
            2,
            1,
            Json("{\"actor\":7,\"blob\":\""
                 + new string('h', 24_000)
                 + "\",\"value\":2.75}"));
        var next = await coordinator.PrepareAsync(contributors, Request(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(19, next.Count(item =>
            item.Mode == ModelContextDisclosureModes.Unchanged));
        var delta = Assert.Single(
            next,
            item => item.Mode == ModelContextDisclosureModes.MergePatch);
        Assert.Equal(2.75d, delta.Payload.GetProperty("value").GetDouble());
        Assert.True(next.Sum(item => item.PayloadUtf8Bytes) < 256);
    }

    [Fact]
    public async Task Concurrent_commits_are_compare_and_swap_fenced()
    {
        var store = new InMemoryModelContextSectionBaselineStore();
        var coordinator = new ModelContextSectionCoordinator(store);
        var contributor = new MutableContributor(
            "actor_state",
            1,
            Json("{\"value\":1}"));
        var initial = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));
        await coordinator.CommitAsync(initial, cancellationToken: TestContext.Current.CancellationToken);

        contributor.Advance(2, 1, Json("{\"value\":2}"));
        var first = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));
        contributor.Advance(3, 1, Json("{\"value\":3}"));
        var second = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, Request(), cancellationToken: TestContext.Current.CancellationToken));

        await coordinator.CommitAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ModelContextSectionException>(
            async () => await coordinator.CommitAsync(second, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("context_section_baseline_conflict", exception.ReasonCode);
    }

    [Fact]
    public async Task Baselines_are_isolated_by_model_visible_session()
    {
        var coordinator = new ModelContextSectionCoordinator(
            new InMemoryModelContextSectionBaselineStore());
        var contributor = new MutableContributor(
            "world_state",
            1,
            Json("{\"weather\":\"rain\"}"));
        var firstRequest = Request();
        firstRequest.SessionId = "session-a";
        var first = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, firstRequest, cancellationToken: TestContext.Current.CancellationToken));
        await coordinator.CommitAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        var secondRequest = Request();
        secondRequest.SessionId = "session-b";

        var second = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, secondRequest, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelContextDisclosureModes.Full, second.Mode);
        Assert.NotEqual(first.BaselineKey, second.BaselineKey);
    }

    [Fact]
    public async Task Reused_session_identifiers_are_isolated_between_worlds()
    {
        var store = new InMemoryModelContextSectionBaselineStore();
        var coordinator = new ModelContextSectionCoordinator(store);
        var contributor = new MutableContributor(
            "world_state",
            1,
            Json("{\"weather\":\"rain\"}"));
        var firstRequest = Request();
        firstRequest.SessionId = "session-reused";
        firstRequest.WorldId = "world-a";
        var first = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, firstRequest, cancellationToken: TestContext.Current.CancellationToken));
        await coordinator.CommitAsync(first, cancellationToken: TestContext.Current.CancellationToken);

        var secondRequest = Request();
        secondRequest.SessionId = "session-reused";
        secondRequest.WorldId = "world-b";
        var second = Assert.Single(await coordinator.PrepareAsync(
            new[] { contributor }, secondRequest, cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotEqual(first.BaselineKey, second.BaselineKey);
        Assert.Equal(ModelContextDisclosureModes.Full, second.Mode);
    }

    [Fact]
    public async Task Length_ambiguous_section_identifiers_cannot_collide()
    {
        var coordinator = new ModelContextSectionCoordinator(
            new InMemoryModelContextSectionBaselineStore());
        var first = Assert.Single(await coordinator.PrepareAsync(
            new[]
            {
                new MutableContributor("c", 1, Json("{\"value\":1}"), "a:b")
            },
            Request(), cancellationToken: TestContext.Current.CancellationToken));
        var second = Assert.Single(await coordinator.PrepareAsync(
            new[]
            {
                new MutableContributor("b:c", 1, Json("{\"value\":2}"), "a")
            },
            Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotEqual(first.BaselineKey, second.BaselineKey);
    }

    private static ModelContextCaptureRequest Request() => new()
    {
        WorldId = "world-1",
        TimelineId = "main",
        ActorId = "actor-1",
        SessionId = "session-1",
        ModelCapabilitiesDigest = new string('a', 64)
    };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class MutableContributor : IModelContextSectionContributor
    {
        private JsonElement _content;
        private long _revision;
        private long? _expectedBaseRevision;
        private readonly string _scopeKey;

        public MutableContributor(
            string sectionId,
            long revision,
            JsonElement content,
            string scopeKey = "actor-1")
        {
            SectionId = sectionId;
            _revision = revision;
            _content = content.Clone();
            _scopeKey = scopeKey;
        }

        public string SectionId { get; }

        public string TimelineId { get; set; } = "main";

        public void Advance(
            long revision,
            long expectedBaseRevision,
            JsonElement content)
        {
            _revision = revision;
            _expectedBaseRevision = expectedBaseRevision;
            _content = content.Clone();
        }

        public ValueTask<ModelContextSectionSnapshot> CaptureAsync(
            ModelContextCaptureRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ModelContextSectionSnapshot>(
                new ModelContextSectionSnapshot
                {
                    SectionId = SectionId,
                    SchemaVersion = "1",
                    Scope = ModelContextSectionScopes.Actor,
                    ScopeKey = _scopeKey,
                    AuthorityId = "world-authority",
                    TimelineId = TimelineId,
                    IncarnationId = "incarnation-1",
                    ModelCapabilitiesDigest = request.ModelCapabilitiesDigest,
                    Revision = _revision,
                    ExpectedBaseRevision = _expectedBaseRevision,
                    RetainThroughCompaction = true,
                    Content = _content.Clone()
                });
        }
    }
}
