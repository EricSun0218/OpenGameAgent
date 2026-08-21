using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenGameAgent.Client;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Server.Tests;

public sealed class ToolApprovalServerTests
{
    [Fact]
    public async Task ApprovalEndpointsAuthorizeOwnerBeforeTouchingBrokerAndNeverExposeCredentialDigest()
    {
        var store = new InMemoryGameToolApprovalStore();
        var request = Request();
        await store.SaveAsync(
            new GameToolApprovalRecord(request, GameToolApprovalStatus.Pending, 0, DateTimeOffset.UtcNow),
            null,
            TestContext.Current.CancellationToken);
        var broker = new CountingBroker(new GameToolApprovalBroker(store));
        var waiting = broker.WaitForDecisionAsync(request, TestContext.Current.CancellationToken).AsTask();
        await using var app = await CreateAppAsync(broker);
        using var http = app.GetTestClient();
        var remote = new ServerGameAgentClient(new ServerGameAgentClientOptions(http, new Uri("http://localhost/"))
        {
            AllowInsecureHttp = true,
        });

        var pending = Assert.Single(await remote.ListPendingToolApprovalsAsync(
            request.Owner,
            presentedCredential: "pair-owner-a",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("write", pending.ToolName);
        Assert.Equal("{\"value\":1}", pending.ArgumentsJson);

        using var denied = await http.PostAsync(
            "/v1/approvals/pending",
            Json("pair-owner-a", "session-b", "actor-a"),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, broker.ListCalls);

        var approved = await remote.RespondToToolApprovalAsync(
            request.Owner,
            request.ApprovalId,
            pending.Revision,
            approve: true,
            presentedCredential: "pair-owner-a",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Approved", approved.Status);
        Assert.DoesNotContain("credential", approved.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pair-owner-a", approved.Json, StringComparison.Ordinal);
        Assert.Equal(GameToolApprovalStatus.Approved, (await waiting).Record.Status);
    }

    private static async Task<WebApplication> CreateAppAsync(IGameToolApprovalBroker broker)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(broker);
        builder.Services.AddSingleton<IGameToolApprovalBroker>(broker);
        builder.Services.AddSingleton<IGameAgentPresentedCredentialAuthenticator, PairingAuthenticator>();
        builder.Services.AddSingleton<IGameAgentOwnerAuthorizer, OwnerAuthorizer>();
        builder.Services.AddSingleton(new GameAgentRuntime(new GameAgentRuntimeOptions(new TextProvider(), "test")));
        var app = builder.Build();
        app.MapOpenGameAgent();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static StringContent Json(string credential, string session, string actor) => new(
        $"{{\"credential\":\"{credential}\",\"sessionId\":\"{session}\",\"actorId\":\"{actor}\",\"limit\":8}}",
        Encoding.UTF8,
        "application/json");

    private static GameToolApprovalRequest Request()
    {
        var now = DateTimeOffset.UtcNow;
        return new GameToolApprovalRequest(
            "approval-http",
            "policy",
            "session-a",
            "actor-a",
            "input",
            "run",
            1,
            "call",
            "write",
            ToolRisk.NonIdempotentWrite,
            "{\"value\":1}",
            "digest",
            new GameMoment("world", 1),
            new GameToolApprovalWorldState("save", 4),
            null,
            now,
            now.AddMinutes(5));
    }

    private sealed class PairingAuthenticator : IGameAgentPresentedCredentialAuthenticator
    {
        public ValueTask<ClaimsPrincipal?> AuthenticateAsync(
            GameAgentPresentedCredentialContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Credential != "pair-owner-a")
            {
                return new ValueTask<ClaimsPrincipal?>((ClaimsPrincipal?)null);
            }

            return new ValueTask<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "owner-a") },
                "pairing")));
        }
    }

    private sealed class OwnerAuthorizer : IGameAgentOwnerAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(
            GameAgentAuthorizationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(
                context.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == "owner-a"
                && context.Key.Equals(new GameSessionKey("session-a", "actor-a")));
        }
    }

    private sealed class CountingBroker : IGameToolApprovalBroker
    {
        private readonly IGameToolApprovalBroker _inner;
        public CountingBroker(IGameToolApprovalBroker inner) => _inner = inner;
        public int ListCalls { get; private set; }
        public ValueTask<GameToolApprovalWaitResult> WaitForDecisionAsync(GameToolApprovalRequest request, CancellationToken cancellationToken) =>
            _inner.WaitForDecisionAsync(request, cancellationToken);
        public ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListPendingAsync(GameSessionKey owner, int maximum, CancellationToken cancellationToken)
        {
            ListCalls++;
            return _inner.ListPendingAsync(owner, maximum, cancellationToken);
        }
        public ValueTask<GameToolApprovalRecord> RespondAsync(GameToolApprovalResponse response, CancellationToken cancellationToken) =>
            _inner.RespondAsync(response, cancellationToken);
        public ValueTask<GameToolApprovalRecord> ConsumeAsync(GameToolApprovalRequest request, string credential, long expectedRevision, CancellationToken cancellationToken) =>
            _inner.ConsumeAsync(request, credential, expectedRevision, cancellationToken);
        public ValueTask<GameToolApprovalRecord> InvalidateAsync(GameToolApprovalRequest request, long expectedRevision, string reason, CancellationToken cancellationToken) =>
            _inner.InvalidateAsync(request, expectedRevision, reason, cancellationToken);
        public ValueTask<GameToolApprovalRecord> CancelAsync(GameToolApprovalRequest request, long expectedRevision, string reason, CancellationToken cancellationToken) =>
            _inner.CancelAsync(request, expectedRevision, reason, cancellationToken);
    }

    private sealed class TextProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(new ModelResponse(new AgentContent[] { new TextContent("ok") }, ModelStopReason.Stop));
            await Task.CompletedTask;
        }
    }
}
