using System.Collections.Concurrent;
using GameAgent.Core;
using GameAgent.Hosting;
using GameAgent.Protocol;
using GameAgent.Remote.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GameAgent.Hosting.Tests;

public sealed class RemoteBridgeIntegrationTests
{
    [Fact]
    public async Task RealWebSocketBridgeSupportsConcurrentAndIdempotentGameActions()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddGameAgentHosting(configureRemoteActions: options =>
        {
            options.MaxConnections = 4;
            options.MaxPendingActionsPerConnection = 32;
        });
        builder.Services.AddSingleton<IRemoteTransportAuthorizer>(new FixedAuthorizer());
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGameAgentRemoteActionBridge();
        await app.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            var endpoint = new UriBuilder(address)
            {
                Scheme = "ws",
                Path = "/game-agent/v1/game-host"
            }.Uri;
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var localHost = new ConcurrentGameHost();
            var client = new RemoteGameHostClient(new RemoteGameHostClientOptions
            {
                Endpoint = endpoint,
                TenantId = "tenant",
                WorldId = "world",
                MaxConcurrentActions = 8
            });
            var clientTask = client.RunAsync(localHost, shutdown.Token);
            var broker = app.Services.GetRequiredService<RemoteActionBroker>();
            await WaitUntilAsync(() => broker.ConnectionCount == 1, shutdown.Token);
            var remoteHost = new RemoteGameHost(
                broker.CreateChannel(new RemoteTransportIdentity("tenant", "world")),
                new FixedClock());

            var duplicate = Request("duplicate");
            var duplicateResults = await Task.WhenAll(
                remoteHost.SubmitActionAsync(duplicate, shutdown.Token).AsTask(),
                remoteHost.SubmitActionAsync(duplicate, shutdown.Token).AsTask());
            Assert.All(duplicateResults, receipt => Assert.Equal(ReceiptStatuses.Succeeded, receipt.Status));
            Assert.Equal(1, localHost.Calls["duplicate"]);

            var parallel = Enumerable.Range(0, 8)
                .Select(index => remoteHost.SubmitActionAsync(Request("parallel-" + index), shutdown.Token).AsTask());
            Assert.All(await Task.WhenAll(parallel), receipt => Assert.Equal(ReceiptStatuses.Succeeded, receipt.Status));
            Assert.True(localHost.MaxActive > 1);

            shutdown.Cancel();
            await clientTask;
        }
        finally
        {
            await app.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RemoteDisconnectCancelsActiveLocalGameAction()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddGameAgentHosting();
        builder.Services.AddSingleton<IRemoteTransportAuthorizer>(new FixedAuthorizer());
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGameAgentRemoteActionBridge();
        await app.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            var endpoint = new UriBuilder(address)
            {
                Scheme = "ws",
                Path = "/game-agent/v1/game-host"
            }.Uri;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var localHost = new CancellableGameHost();
            var client = new RemoteGameHostClient(new RemoteGameHostClientOptions
            {
                Endpoint = endpoint,
                TenantId = "tenant",
                WorldId = "world"
            });
            var clientTask = client.RunAsync(localHost, timeout.Token);
            var broker = app.Services.GetRequiredService<RemoteActionBroker>();
            await WaitUntilAsync(() => broker.ConnectionCount == 1, timeout.Token);
            var channel = broker.CreateChannel(new RemoteTransportIdentity("tenant", "world"));
            var action = channel.SubmitAsync(Request("disconnect"), timeout.Token).AsTask();
            await localHost.Started.Task.WaitAsync(timeout.Token);

            await broker.DisposeAsync();

            await localHost.Cancelled.Task.WaitAsync(timeout.Token);
            await clientTask.WaitAsync(timeout.Token);
            await Assert.ThrowsAsync<RemoteActionOutcomeUnknownException>(async () => await action);
        }
        finally
        {
            await app.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static ActionRequest Request(string operationId) => new()
    {
        OperationId = operationId,
        RunId = "run",
        TurnId = "turn",
        ToolCallId = "call:" + operationId,
        AgentId = "agent",
        WorldId = "world",
        ActionName = "world.change",
        ActionVersion = "1",
        Arguments = ProtocolJson.ParseElement("{}"),
        RequestedAt = DateTimeOffset.UnixEpoch
    };

    private sealed class FixedAuthorizer : IRemoteTransportAuthorizer
    {
        public ValueTask<RemoteTransportIdentity?> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RemoteTransportIdentity?>(new RemoteTransportIdentity("tenant", "world"));
        }
    }

    private sealed class ConcurrentGameHost : IGameHost
    {
        private int _active;
        private int _maxActive;
        public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
        public int MaxActive => Volatile.Read(ref _maxActive);

        public async ValueTask<ActionReceipt> SubmitActionAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(request.OperationId, 1, static (_, value) => value + 1);
            var active = Interlocked.Increment(ref _active);
            while (active > Volatile.Read(ref _maxActive))
            {
                Interlocked.CompareExchange(ref _maxActive, active, Volatile.Read(ref _maxActive));
            }
            try
            {
                await Task.Delay(30, cancellationToken);
                return new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement("{\"ok\":true}"),
                    ReceivedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class CancellableGameHost : IGameHost
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The action should be cancelled when the transport disconnects.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class FixedClock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
