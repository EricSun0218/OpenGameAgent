using System.Text.Json;
using GameAgent.Core;
using GameAgent.Hosting;
using GameAgent.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace GameAgent.Hosting.Tests;

public sealed class HostingTests
{
    [Fact]
    public void TransportRoundTripsAndRejectsUnknownFields()
    {
        var codec = new AgentTransportCodec();
        var source = Envelope("message-1");
        var restored = codec.Deserialize(codec.Serialize(source));

        Assert.Equal("message-1", restored.MessageId);
        Assert.Equal(AgentTransportMessageTypes.RuntimeEvent, restored.Type);
        Assert.Equal(7, restored.Payload.GetProperty("value").GetInt32());

        var json = "{\"version\":\"1\",\"messageId\":\"m\",\"type\":\"error\","
                   + "\"tenantId\":\"t\",\"worldId\":\"w\",\"sequence\":0,"
                   + "\"payload\":{},\"unexpected\":true}";
        var error = Assert.Throws<AgentTransportValidationException>(
            () => codec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Equal("envelope_json_invalid", error.Code);
    }

    [Fact]
    public void TransportEnforcesPayloadShape()
    {
        var codec = new AgentTransportCodec(
            new AgentTransportLimits { MaxPayloadDepth = 2, MaxPayloadNodes = 16 });
        var source = Envelope("message-2");
        source.Payload = Json("""{"outer":{"inner":1}}""");

        var error = Assert.Throws<AgentTransportValidationException>(() => codec.Serialize(source));
        Assert.Equal("payload_shape_exceeded", error.Code);
    }

    [Fact]
    public void RemoteTransportIdentityRejectsRouteSeparatorCharacters()
    {
        Assert.Throws<ArgumentException>(() => new RemoteTransportIdentity("tenant\nother", "world"));
        Assert.Throws<ArgumentException>(() => new RemoteTransportIdentity("tenant", "world/other"));
    }

    [Fact]
    public async Task TenantAdmissionIsolatedAndBounded()
    {
        await using var admission = new TenantAdmissionController(
            new TenantAdmissionOptions
            {
                MaxKnownTenants = 2,
                MaxConcurrentRuns = 2,
                MaxConcurrentRunsPerTenant = 1,
                MaxQueuedRunsPerTenant = 1
            });
        await using var first = await admission.AcquireAsync("tenant-a", cancellationToken: TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await admission.AcquireAsync("tenant-a", cancelled.Token));

        var snapshot = admission.GetSnapshot();
        Assert.Equal(1, snapshot.ActiveRuns);
        Assert.Equal(0, snapshot.WaitingRuns);
        Assert.Equal(1, snapshot.TenantCount);
    }

    [Fact]
    public async Task ZeroQueueAllowsImmediateWorkAndRejectsWaitingWork()
    {
        await using var admission = new TenantAdmissionController(
            new TenantAdmissionOptions
            {
                MaxKnownTenants = 1,
                MaxConcurrentRuns = 1,
                MaxConcurrentRunsPerTenant = 1,
                MaxQueuedRunsPerTenant = 0
            });
        await using var first = await admission.AcquireAsync("tenant", cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<TenantCapacityExceededException>(
            async () => await admission.AcquireAsync("tenant", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("max_queued_runs_per_tenant", error.ReasonCode);
    }

    [Fact]
    public async Task AdmissionShutdownCancelsQueuedRunsAndDrainsActiveLease()
    {
        var admission = new TenantAdmissionController(
            new TenantAdmissionOptions
            {
                MaxKnownTenants = 1,
                MaxConcurrentRuns = 1,
                MaxConcurrentRunsPerTenant = 1,
                MaxQueuedRunsPerTenant = 1,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(1)
            });
        var active = await admission.AcquireAsync("tenant", cancellationToken: TestContext.Current.CancellationToken);
        var queued = admission.AcquireAsync("tenant", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (admission.GetSnapshot().WaitingRuns != 1)
        {
            await Task.Delay(10, wait.Token);
        }

        var shutdown = admission.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        await active.DisposeAsync();
        await shutdown;
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await admission.AcquireAsync("tenant", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReplayBufferDetectsExpiredCursor()
    {
        var replay = new AgentEventReplayBuffer(
            new AgentEventReplayOptions { MaxRoutes = 1, CapacityPerRoute = 2 });
        replay.Publish("route", Envelope("m1"));
        replay.Publish("route", Envelope("m2"));
        replay.Publish("route", Envelope("m3"));

        Assert.Throws<AgentEventCursorExpiredException>(() => replay.ReadAfter("route", -1));
        var available = replay.ReadAfter("route", 0);
        Assert.Equal(new long[] { 1, 2 }, available.Select(value => value.Sequence));
    }

    [Fact]
    public void ReplayBufferOwnsImmutableSnapshots()
    {
        var replay = new AgentEventReplayBuffer();
        var source = Envelope("m1");
        var published = replay.Publish("route", source);
        source.MessageId = "mutated";
        published.MessageId = "also-mutated";
        var firstRead = Assert.Single(replay.ReadAfter("route", -1));
        firstRead.MessageId = "read-mutated";
        Assert.Equal("m1", Assert.Single(replay.ReadAfter("route", -1)).MessageId);
    }

    [Fact]
    public void TenantGuardsRateLimitKillAndRecoverFailedDependencies()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new TenantRateLimiter(new TenantRateLimitOptions
        {
            MaxKnownTenants = 1,
            TokensPerSecond = 1,
            BurstTokens = 2
        });
        Assert.True(limiter.TryAcquire("tenant", now).Allowed);
        Assert.True(limiter.TryAcquire("tenant", now).Allowed);
        Assert.False(limiter.TryAcquire("tenant", now).Allowed);
        Assert.True(limiter.TryAcquire("tenant", now.AddSeconds(1)).Allowed);

        var kill = new GameAgentKillSwitch();
        kill.BlockTenant("tenant");
        Assert.Equal("host_kill_switch",
            Assert.Throws<TenantCapacityExceededException>(() => kill.EnsureAllowed("tenant")).ReasonCode);
        Assert.True(kill.AllowTenant("tenant"));
        kill.EnsureAllowed("tenant");

        var breaker = new FailureCircuitBreaker(new FailureCircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(10)
        });
        breaker.RecordFailure("provider", now);
        Assert.True(breaker.TryEnter("provider", now));
        breaker.RecordFailure("provider", now);
        Assert.False(breaker.TryEnter("provider", now.AddSeconds(9)));
        Assert.True(breaker.TryEnter("provider", now.AddSeconds(10)));
        Assert.False(breaker.TryEnter("provider", now.AddSeconds(10)));
        Assert.True(breaker.TryEnter("provider", now.AddSeconds(20)));
        breaker.RecordSuccess("provider");
        Assert.True(breaker.TryEnter("provider", now));
    }

    [Fact]
    public void RateLimiterSaturatesUnrepresentableRetryDuration()
    {
        var limiter = new TenantRateLimiter(new TenantRateLimitOptions
        {
            TokensPerSecond = 0.000001,
            BurstTokens = 1_000_000
        });
        var now = DateTimeOffset.UnixEpoch;
        Assert.True(limiter.TryAcquire("tenant", now, 1_000_000).Allowed);

        var decision = limiter.TryAcquire("tenant", now, 1_000_000);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
    }

    [Fact]
    public async Task RemoteDisconnectBecomesUnknownReceipt()
    {
        var host = new RemoteGameHost(new UnknownChannel(), new FixedClock());
        var receipt = await host.SubmitActionAsync(Request(), CancellationToken.None);

        Assert.Equal(ReceiptStatuses.Unknown, receipt.Status);
        Assert.Equal("remote_outcome_unknown", receipt.ErrorCode);
        Assert.False(receipt.Retryable);
    }

    [Fact]
    public async Task HostingLifecyclePublishesReadiness()
    {
        var services = new ServiceCollection();
        services.AddGameAgentHosting();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<GameAgentHostingLifecycle>();
        var health = provider.GetRequiredService<GameAgentHostingHealthCheck>();

        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new HealthCheckContext(), cancellationToken: TestContext.Current.CancellationToken)).Status);
        await hosted.StartAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new HealthCheckContext(), cancellationToken: TestContext.Current.CancellationToken)).Status);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new HealthCheckContext(), cancellationToken: TestContext.Current.CancellationToken)).Status);
    }

    private static AgentTransportEnvelope Envelope(string id) => new()
    {
        MessageId = id,
        Type = AgentTransportMessageTypes.RuntimeEvent,
        TenantId = "tenant",
        WorldId = "world",
        RunId = "run",
        Payload = Json("""{"value":7}""")
    };

    private static ActionRequest Request() => new()
    {
        OperationId = "operation",
        RunId = "run",
        TurnId = "turn",
        ToolCallId = "call",
        AgentId = "agent",
        WorldId = "world",
        ActionName = "game.action",
        ActionVersion = "1",
        Arguments = Json("{}"),
        RequestedAt = DateTimeOffset.UnixEpoch
    };

    private static JsonElement Json(string value) => ProtocolJson.ParseElement(value);

    private sealed class UnknownChannel : IRemoteActionChannel
    {
        public ValueTask<ActionReceipt> SubmitAsync(ActionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ActionReceipt>(new RemoteActionOutcomeUnknownException("connection lost"));
    }

    private sealed class FixedClock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(10);
    }
}
