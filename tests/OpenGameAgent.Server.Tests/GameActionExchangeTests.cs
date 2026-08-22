using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenGameAgent.Client;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Server.Tests;

public sealed class GameActionExchangeTests
{
    [Fact]
    public async Task ExternalDeliveryIsDurableDeduplicatedAndCompletesDispatcher()
    {
        var journal = new InMemoryGameActionJournal();
        var dispatchExchange = new GameActionExchange(journal);
        var deliveryExchange = new GameActionExchange(journal);
        var dispatcher = new DurableGameActionDispatcher(journal, dispatchExchange);
        var intent = Intent("operation-1");

        var pendingExecution = dispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken).AsTask();
        var first = await WaitForDeliveryAsync(deliveryExchange, intent, TestContext.Current.CancellationToken);
        var second = Assert.Single(await deliveryExchange.ClaimPendingAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            10,
            TestContext.Current.CancellationToken));

        Assert.Equal(intent.OperationId, first.Intent.OperationId);
        Assert.Equal(first.Intent.OperationId, second.Intent.OperationId);
        Assert.True(first.RequiresReconciliation);
        Assert.True((await journal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken))!.Dispatched);

        var submitted = await deliveryExchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            intent.ExpectedRevision,
            intent.GenerationId,
            GameActionReceipt.Committed(intent, "{\"placed\":true}", 8),
            TestContext.Current.CancellationToken);
        var completed = await pendingExecution.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, submitted.Status);
        Assert.Equal(submitted.ResultJson, completed.ResultJson);
        Assert.Empty(await deliveryExchange.ClaimPendingAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            10,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartedExchangeOnlyReconcilesAPreviouslyDeliveredOperation()
    {
        using var directory = new TemporaryDirectory();
        var intent = Intent("operation-restart");
        var firstJournal = new Persistence.FileGameActionJournal(directory.Path);
        await firstJournal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await firstJournal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);

        var restartedJournal = new Persistence.FileGameActionJournal(directory.Path);
        var restartedExchange = new GameActionExchange(restartedJournal);
        var delivery = Assert.Single(await restartedExchange.ClaimPendingAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            10,
            TestContext.Current.CancellationToken));
        var beforeReceipt = await restartedExchange.ReconcileAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            delivery.Intent.OperationId,
            TestContext.Current.CancellationToken);

        Assert.Equal(GameActionExchangeStatus.Dispatched, beforeReceipt!.Status);
        Assert.True(beforeReceipt.RequiresReconciliation);

        await restartedExchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            intent.ExpectedRevision,
            intent.GenerationId,
            GameActionReceipt.Committed(intent, "{\"reconciled\":true}", 9),
            TestContext.Current.CancellationToken);

        var finalExchange = new GameActionExchange(new Persistence.FileGameActionJournal(directory.Path));
        var finalDispatcher = new DurableGameActionDispatcher(
            new Persistence.FileGameActionJournal(directory.Path),
            finalExchange);
        var recovered = await finalDispatcher.ExecuteAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionStatus.Committed, recovered.Status);
        Assert.Empty(await finalExchange.ClaimPendingAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId),
            10,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiptMustMatchEveryAuthoritativeIntentBinding()
    {
        var journal = new InMemoryGameActionJournal();
        var exchange = new GameActionExchange(journal);
        var intent = Intent("operation-bindings");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        var receipt = GameActionReceipt.Committed(intent, "{}", 8);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey("other", intent.ActorId), intent.ExpectedRevision, intent.GenerationId, receipt, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, "other"), intent.ExpectedRevision, intent.GenerationId, receipt, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId), intent.ExpectedRevision, "other-generation", receipt, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId), intent.ExpectedRevision + 1, intent.GenerationId, receipt, TestContext.Current.CancellationToken));
        var wrongMoment = new GameActionReceipt(
            intent.OperationId,
            GameActionStatus.Committed,
            "{}",
            new GameMoment("other-timeline", intent.Moment.Tick),
            8);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId), intent.ExpectedRevision, intent.GenerationId, wrongMoment, TestContext.Current.CancellationToken));
        var stale = GameActionReceipt.Committed(intent, "{}", 6);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await exchange.SubmitReceiptAsync(
            new GameSessionKey(intent.SessionId, intent.ActorId), intent.ExpectedRevision, intent.GenerationId, stale, TestContext.Current.CancellationToken));

        Assert.Null((await journal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken))!.Receipt);
    }

    [Fact]
    public async Task BodyCredentialMapsPrincipalButCannotOverrideSessionActorOwnership()
    {
        var innerJournal = new InMemoryGameActionJournal();
        var journal = new CountingJournal(innerJournal);
        var intent = Intent("operation-http");
        await innerJournal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await innerJournal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await using var app = await CreateAppAsync(
            journal,
            new PairingAuthenticator(),
            new OwnerAuthorizer());
        using var client = app.GetTestClient();

        using var allowed = await client.PostAsync(
            "/v1/actions/claim",
            JsonContent("pair-owner-a", intent.SessionId, intent.ActorId),
            TestContext.Current.CancellationToken);
        var allowedJson = await allowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        allowed.EnsureSuccessStatusCode();
        Assert.Contains(intent.OperationId, allowedJson, StringComparison.Ordinal);
        Assert.Contains("\"conflictKey\":\"world:resource\"", allowedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("pair-owner-a", allowedJson, StringComparison.Ordinal);

        var scansBeforeDenial = journal.ListPendingCalls;
        using var denied = await client.PostAsync(
            "/v1/actions/claim",
            JsonContent("pair-owner-a", "session-b", intent.ActorId),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(scansBeforeDenial, journal.ListPendingCalls);

        using var receiptResponse = await client.PostAsync(
            "/v1/actions/receipt",
            new StringContent(
                """
                {
                  "credential":"pair-owner-a",
                  "sessionId":"session-a",
                  "actorId":"actor-a",
                  "operationId":"operation-http",
                  "status":"committed",
                  "result":{"placed":true},
                  "timelineId":"world",
                  "tick":42,
                  "calendar":{"month":3},
                  "generationId":"save-generation-3",
                  "expectedRevision":7,
                  "stateRevision":8
                }
                """,
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
        receiptResponse.EnsureSuccessStatusCode();
        Assert.Equal(
            GameActionStatus.Committed,
            (await innerJournal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken))!.Receipt!.Status);

        using var reconcile = await client.PostAsync(
            "/v1/actions/reconcile",
            new StringContent(
                """
                {"credential":"pair-owner-a","sessionId":"session-a","actorId":"actor-a","operationId":"operation-http"}
                """,
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
        var reconcileJson = await reconcile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        reconcile.EnsureSuccessStatusCode();
        Assert.Contains("completed", reconcileJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BodyCredentialNeverEntersModelContextTranscriptOrResponse()
    {
        var provider = new CapturingProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var journal = new InMemoryGameActionJournal();
        await using var app = await CreateAppAsync(
            journal,
            new PairingAuthenticator(),
            new OwnerAuthorizer(),
            runtime);
        using var client = app.GetTestClient();
        var json = """
        {
          "credential":"pair-owner-a",
          "inputId":"credential-input",
          "sessionId":"session-a",
          "actorId":"actor-a",
          "type":"chat",
          "payload":{"text":"hello"},
          "timelineId":"world",
          "tick":1
        }
        """;

        using var response = await client.PostAsync(
            "/v1/run",
            new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain("pair-owner-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            provider.Requests.SelectMany(static request => request.Messages)
                .SelectMany(static message => message.Content)
                .OfType<TextContent>()
                .Select(static content => content.Text),
            static value => value.Contains("pair-owner-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActionStreamUsesPostBodyAuthenticationAndEmitsDurableIntent()
    {
        var journal = new InMemoryGameActionJournal();
        var intent = Intent("operation-stream");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await using var app = await CreateAppAsync(journal, new PairingAuthenticator(), new OwnerAuthorizer());
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/stream")
        {
            Content = JsonContent("pair-owner-a", intent.SessionId, intent.ActorId),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var eventLine = await reader.ReadLineAsync(timeout.Token);
        var dataLine = await reader.ReadLineAsync(timeout.Token);
        timeout.Cancel();

        Assert.Equal("event: action", eventLine);
        Assert.StartsWith("data: ", dataLine, StringComparison.Ordinal);
        Assert.Contains(intent.OperationId, dataLine, StringComparison.Ordinal);
        Assert.DoesNotContain("pair-owner-a", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedClientClaimsReconcilesAndSettlesExternalActions()
    {
        var journal = new InMemoryGameActionJournal();
        var intent = Intent("operation-typed-client");
        await journal.ReserveAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(
            GameActionDispatchClaimStatus.Claimed,
            (await journal.ClaimDispatchAsync(intent.OperationId, TestContext.Current.CancellationToken)).Status);
        await using var app = await CreateAppAsync(journal, new PairingAuthenticator(), new OwnerAuthorizer());
        using var http = app.GetTestClient();
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var key = new GameSessionKey(intent.SessionId, intent.ActorId);

        var claimed = Assert.Single(await client.ClaimActionsAsync(
            key,
            presentedCredential: "pair-owner-a",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(intent.OperationId, claimed.Intent.OperationId);
        Assert.Equal(intent.ConflictKey, claimed.Intent.ConflictKey);
        Assert.True(claimed.RequiresReconciliation);

        var dispatched = await client.ReconcileActionAsync(
            key,
            intent.OperationId,
            "pair-owner-a",
            TestContext.Current.CancellationToken);
        Assert.NotNull(dispatched);
        Assert.Equal("dispatched", dispatched.Status);

        var stored = await client.SubmitActionReceiptAsync(
            key,
            intent.ExpectedRevision,
            intent.GenerationId,
            GameActionReceipt.Committed(intent, "{\"placed\":true}", 8),
            "pair-owner-a",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, stored.Status);

        var completed = await client.ReconcileActionAsync(
            key,
            intent.OperationId,
            "pair-owner-a",
            TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.Equal("completed", completed.Status);
        Assert.False(completed.RequiresReconciliation);
        Assert.Equal(GameActionStatus.Committed, completed.Receipt!.Status);
    }

    private static GameActionIntent Intent(string operationId) => new(
        operationId,
        "input-1",
        "session-a",
        "actor-a",
        "place_block",
        "{\"x\":1}",
        new GameMoment("world", 42, "{\"month\":3}"),
        expectedRevision: 7,
        generationId: "save-generation-3",
        conflictKey: "world:resource");

    private static async Task<GameActionDelivery> WaitForDeliveryAsync(
        GameActionExchange exchange,
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var deliveries = await exchange.ClaimPendingAsync(
                new GameSessionKey(intent.SessionId, intent.ActorId),
                10,
                cancellationToken);
            if (deliveries.Count > 0)
            {
                return Assert.Single(deliveries);
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("The dispatcher did not publish the action delivery.");
    }

    private static StringContent JsonContent(string credential, string sessionId, string actorId) => new(
        $$"""{"credential":"{{credential}}","sessionId":"{{sessionId}}","actorId":"{{actorId}}","limit":10}""",
        Encoding.UTF8,
        "application/json");

    private static async Task<WebApplication> CreateAppAsync(
        IGameActionJournal journal,
        IGameAgentPresentedCredentialAuthenticator authenticator,
        IGameAgentOwnerAuthorizer authorizer,
        GameAgentRuntime? runtime = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(runtime ?? new GameAgentRuntime(
            new GameAgentRuntimeOptions(new CapturingProvider(), "test")));
        builder.Services.AddSingleton(journal);
        builder.Services.AddSingleton(new GameActionExchange(journal));
        builder.Services.AddSingleton(authenticator);
        builder.Services.AddSingleton(authorizer);
        var app = builder.Build();
        app.MapOpenGameAgent();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class PairingAuthenticator : IGameAgentPresentedCredentialAuthenticator
    {
        public ValueTask<ClaimsPrincipal?> AuthenticateAsync(
            GameAgentPresentedCredentialContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(context.Credential, "pair-owner-a", StringComparison.Ordinal))
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
            var subject = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            return new ValueTask<bool>(
                string.Equals(subject, "owner-a", StringComparison.Ordinal)
                && string.Equals(context.Key.SessionId, "session-a", StringComparison.Ordinal)
                && string.Equals(context.Key.ActorId, "actor-a", StringComparison.Ordinal));
        }
    }

    private sealed class CountingJournal : IGameActionJournal
    {
        private readonly IGameActionJournal _inner;

        public CountingJournal(IGameActionJournal inner)
        {
            _inner = inner;
        }

        public int ListPendingCalls { get; private set; }

        public ValueTask<GameActionJournalEntry> ReserveAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            _inner.ReserveAsync(intent, cancellationToken);

        public ValueTask<GameActionJournalEntry?> FindAsync(string operationId, CancellationToken cancellationToken) =>
            _inner.FindAsync(operationId, cancellationToken);

        public ValueTask<bool> MarkDispatchedAsync(string operationId, CancellationToken cancellationToken) =>
            _inner.MarkDispatchedAsync(operationId, cancellationToken);

        public ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken) =>
            _inner.SaveReceiptAsync(receipt, cancellationToken);

        public ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(int limit, CancellationToken cancellationToken)
        {
            ListPendingCalls++;
            return _inner.ListPendingAsync(limit, cancellationToken);
        }
    }

    private sealed class CapturingProvider : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                new ModelResponse(new AgentContent[] { new TextContent("ok") }, ModelStopReason.Pending),
                "ok");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(
                new ModelResponse(new AgentContent[] { new TextContent("ok") }, ModelStopReason.Stop));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oga-action-exchange-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
