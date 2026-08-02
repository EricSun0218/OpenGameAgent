using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class ExternalAttentionTests
{
    [Fact]
    public async Task Concurrent_identical_requests_are_idempotent()
    {
        var store = new InMemoryExternalAttentionStore();
        var coordinator = new ExternalAttentionCoordinator(store);
        var request = Request();
        request.RequestId = "request-concurrent";

        var records = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => coordinator.RequestAsync(request).AsTask()));

        Assert.All(records, record => Assert.Equal(1, record.Revision));
        Assert.Single(await store.ListPendingAsync("world-1", 10, default));
    }

    [Fact]
    public async Task Resolution_is_exactly_once_and_bound_to_world_state()
    {
        var store = new InMemoryExternalAttentionStore();
        var coordinator = new ExternalAttentionCoordinator(store);
        var created = await coordinator.RequestAsync(Request());
        var resolution = Resolution("answer-1", "yes");

        var resolved = await coordinator.ResolveAsync(
            "choice-1",
            resolution,
            created.Revision);
        var replay = await coordinator.ResolveAsync(
            "choice-1",
            resolution,
            created.Revision);

        Assert.Equal(ExternalAttentionStates.Resolved, resolved.State);
        Assert.Equal(resolved.ResolutionDigest, replay.ResolutionDigest);
        var conflict = await Assert.ThrowsAsync<ExternalAttentionException>(
            async () => await coordinator.ResolveAsync(
                "choice-1",
                Resolution("answer-2", "no"),
                resolved.Revision));
        Assert.Equal("external_attention_resolution_conflict", conflict.ReasonCode);
    }

    [Fact]
    public async Task Concurrent_identical_resolutions_and_closures_are_idempotent()
    {
        var store = new InMemoryExternalAttentionStore();
        var coordinator = new ExternalAttentionCoordinator(store);
        var resolvedRequest = Request();
        resolvedRequest.RequestId = "resolve-concurrent";
        var created = await coordinator.RequestAsync(resolvedRequest);

        var resolved = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.ResolveAsync(
                resolvedRequest.RequestId,
                Resolution("answer-concurrent", "yes"),
                created.Revision).AsTask()));

        Assert.All(resolved, record =>
            Assert.Equal(ExternalAttentionStates.Resolved, record.State));
        Assert.Single(resolved.Select(record => record.ResolutionDigest).Distinct());

        var closedRequest = Request();
        closedRequest.RequestId = "close-concurrent";
        created = await coordinator.RequestAsync(closedRequest);
        var closed = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.CloseAsync(
                closedRequest.RequestId,
                ExternalAttentionStates.Cancelled,
                created.Revision).AsTask()));

        Assert.All(closed, record =>
            Assert.Equal(ExternalAttentionStates.Cancelled, record.State));
        Assert.All(closed, record => Assert.Equal(2, record.Revision));
    }

    [Fact]
    public async Task Waiting_requests_do_not_consume_runtime_capacity()
    {
        var store = new InMemoryExternalAttentionStore(maximumRecords: 256);
        var coordinator = new ExternalAttentionCoordinator(store);
        for (var index = 0; index < 100; index++)
        {
            var request = Request();
            request.RequestId = "choice-" + index;
            request.ActorId = "npc-" + index;
            await coordinator.RequestAsync(request);
        }

        var pending = await store.ListPendingAsync("world-1", 128, default);
        Assert.Equal(100, pending.Count);
        Assert.All(pending, record =>
            Assert.Equal(ExternalAttentionStates.Pending, record.State));
    }

    [Fact]
    public async Task Resolution_rejects_stale_binding_and_game_time_expiry()
    {
        var coordinator = new ExternalAttentionCoordinator(
            new InMemoryExternalAttentionStore());
        var request = Request();
        request.ExpiresAt = Time(20);
        var created = await coordinator.RequestAsync(request);

        var stale = Resolution("answer", "yes");
        stale.StateBindingDigest = new string('b', 64);
        var binding = await Assert.ThrowsAsync<ExternalAttentionException>(
            async () => await coordinator.ResolveAsync(
                request.RequestId,
                stale,
                created.Revision));
        Assert.Equal("external_attention_binding_mismatch", binding.ReasonCode);

        var expired = Resolution("answer", "yes");
        expired.ResolvedAt = Time(20);
        var time = await Assert.ThrowsAsync<ExternalAttentionException>(
            async () => await coordinator.ResolveAsync(
                request.RequestId,
                expired,
                created.Revision));
        Assert.Equal("external_attention_expired", time.ReasonCode);
    }

    [Fact]
    public void Capability_evidence_matches_exact_target_actor_and_game_time()
    {
        var evidence = new ScopedCapabilityEvidence
        {
            CapabilityId = "grant-1",
            OperationKind = "inventory.transfer",
            Target = "inventory:merchant-1",
            Scope = "single_operation",
            AuthorityId = "world-authority",
            WorldId = "world-1",
            ActorId = "merchant-1",
            StateBindingDigest = new string('a', 64),
            ExpiresAt = Time(20)
        };

        Assert.True(ScopedCapabilityEvidenceValidator.Matches(
            evidence,
            "inventory.transfer",
            "inventory:merchant-1",
            "world-1",
            "merchant-1",
            new string('a', 64),
            Time(19)));
        Assert.False(ScopedCapabilityEvidenceValidator.Matches(
            evidence,
            "inventory.transfer",
            "inventory:merchant-1",
            "world-1",
            "thief-1",
            new string('a', 64),
            Time(19)));
    }

    private static ExternalAttentionRequest Request() => new()
    {
        RequestId = "choice-1",
        Kind = "choose_dialogue_option",
        WorldId = "world-1",
        RunId = "run-1",
        ActorId = "npc-1",
        AuthorityId = "world-authority",
        StateBindingDigest = new string('a', 64),
        Payload = Json("{\"options\":[\"yes\",\"no\"]}"),
        CreatedAt = Time(10)
    };

    private static ExternalAttentionResolution Resolution(
        string resolutionId,
        string answer) => new()
        {
            ResolutionId = resolutionId,
            AuthorityId = "world-authority",
            StateBindingDigest = new string('a', 64),
            Payload = Json("{\"answer\":\"" + answer + "\"}"),
            ResolvedAt = Time(11)
        };

    private static GameTimePoint Time(long tick) =>
        new("world-month", "main", 0, tick);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
