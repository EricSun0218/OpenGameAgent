using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class OAuthFlowTests
{
    [Fact]
    public async Task AuthorizationCodeUsesPkceStateAndProducesRefreshableCredential()
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK, """
            {"access_token":"access","refresh_token":"refresh","token_type":"Bearer","expires_in":3600,"scope":"models.read"}
            """));
        var options = new GameOAuthAuthorizationCodeOptions(
            new HttpClient(handler),
            new Uri("https://auth.example.test/authorize"),
            new Uri("https://auth.example.test/token"),
            "client",
            new Uri("http://127.0.0.1:1455/callback"));
        options.Scopes.Add("models.read");
        Uri? opened = null;
        var interaction = new GameAuthInteraction
        {
            OpenBrowserAsync = (uri, _) =>
            {
                opened = uri;
                return ValueTask.CompletedTask;
            },
            PromptAsync = (_, _, _) =>
            {
                var state = ParseQuery(opened!.Query)["state"];
                return new ValueTask<string>("http://127.0.0.1:1455/callback?code=authorization-code&state=" + Uri.EscapeDataString(state));
            },
        };

        var credential = await GameOAuth.LoginAuthorizationCodeAsync(
            options,
            interaction,
            TestContext.Current.CancellationToken);

        Assert.Equal(GameCredentialKind.OAuth, credential.Kind);
        Assert.Equal("access", credential.Secret);
        Assert.Equal("refresh", credential.Metadata["refresh_token"]);
        Assert.NotNull(credential.ExpiresAt);
        var authorizationQuery = ParseQuery(opened!.Query);
        var form = ParseForm(handler.Bodies.Single());
        Assert.Equal("S256", authorizationQuery["code_challenge_method"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("authorization-code", form["code"]);
        Assert.Equal(
            authorizationQuery["code_challenge"],
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
    }

    [Fact]
    public async Task DeviceCodeHonorsPendingAndSlowDownBeforeSuccess()
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, """
                {"device_code":"device","user_code":"ABCD","verification_uri":"https://auth.example.test/device","expires_in":600,"interval":0}
                """),
            _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"authorization_pending\"}"),
            _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"slow_down\"}"),
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"));
        var options = new GameOAuthDeviceCodeOptions(
            new HttpClient(handler),
            new Uri("https://auth.example.test/device/code"),
            new Uri("https://auth.example.test/token"),
            "client");
        var delays = new List<TimeSpan>();
        options.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        string? notification = null;
        Uri? opened = null;
        var interaction = new GameAuthInteraction
        {
            NotifyAsync = (message, _) =>
            {
                notification = message;
                return ValueTask.CompletedTask;
            },
            OpenBrowserAsync = (uri, _) =>
            {
                opened = uri;
                return ValueTask.CompletedTask;
            },
        };

        var credential = await GameOAuth.LoginDeviceCodeAsync(
            options,
            interaction,
            TestContext.Current.CancellationToken);

        Assert.Equal("access", credential.Secret);
        Assert.Contains("ABCD", notification, StringComparison.Ordinal);
        Assert.Equal("https://auth.example.test/device", opened!.AbsoluteUri);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5) }, delays);
        Assert.Equal(4, handler.Bodies.Count);
        Assert.Equal("device", ParseForm(handler.Bodies.Last())["device_code"]);
    }

    [Fact]
    public async Task RefreshPreservesRotatingRefreshTokenWhenResponseOmitsIt()
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"next\",\"expires_in\":120}"));
        var current = new GameCredential(
            GameCredentialKind.OAuth,
            "current",
            DateTimeOffset.UtcNow.AddMinutes(1),
            new Dictionary<string, string> { ["refresh_token"] = "refresh" });

        var next = await GameOAuth.RefreshAsync(
            new HttpClient(handler),
            new Uri("https://auth.example.test/token"),
            "client",
            current,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("next", next.Secret);
        Assert.Equal("refresh", next.Metadata["refresh_token"]);
        Assert.Equal("refresh", ParseForm(handler.Bodies.Single())["refresh_token"]);
    }

    [Fact]
    public async Task AuthorizationCodeRejectsMismatchedCallbackStateBeforeTokenExchange()
    {
        var handler = new QueueHandler(_ => throw new InvalidOperationException("must not send"));
        var options = new GameOAuthAuthorizationCodeOptions(
            new HttpClient(handler),
            new Uri("https://auth.example.test/authorize"),
            new Uri("https://auth.example.test/token"),
            "client",
            new Uri("http://127.0.0.1:1455/callback"));
        var interaction = new GameAuthInteraction
        {
            OpenBrowserAsync = (_, _) => ValueTask.CompletedTask,
            PromptAsync = (_, _, _) => new ValueTask<string>(
                "http://127.0.0.1:1455/callback?code=code&state=wrong"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await GameOAuth.LoginAuthorizationCodeAsync(
                options,
                interaction,
                TestContext.Current.CancellationToken));
        Assert.Empty(handler.Bodies);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static Dictionary<string, string> ParseQuery(string query) =>
        ParseEncoded(query.TrimStart('?'));

    private static Dictionary<string, string> ParseForm(string form) => ParseEncoded(form);

    private static Dictionary<string, string> ParseEncoded(string value) =>
        value.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(new[] { '=' }, 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                part => Uri.UnescapeDataString((part.Length == 2 ? part[1] : string.Empty).Replace('+', ' ')),
                StringComparer.Ordinal);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue()(request);
        }
    }
}
