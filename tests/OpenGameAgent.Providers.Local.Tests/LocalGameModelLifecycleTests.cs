using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace OpenGameAgent.Providers.Local.Tests;

public sealed class LocalGameModelLifecycleTests
{
    [Fact]
    public async Task AcquisitionFailsClosedBeforeBackendWithoutHostAuthorization()
    {
        var backend = new FakeBackend();
        using var lifecycle = new LocalGameModelLifecycle(backend);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await lifecycle.AcquireAsync(
                new LocalGameModelAcquisitionRequest("model"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, backend.AcquireCalls);
    }

    [Fact]
    public async Task AuthorizedAcquisitionIsBoundedAndReportsMonotonicProgress()
    {
        var backend = new FakeBackend();
        using var lifecycle = new LocalGameModelLifecycle(
            backend,
            new LocalGameModelLifecycleOptions
            {
                AuthorizeAcquisitionAsync = (_, _) => new ValueTask<bool>(true),
            });
        var progress = new List<LocalGameModelOperationProgress>();

        await lifecycle.AcquireAsync(
            new LocalGameModelAcquisitionRequest("model", "trusted-source"),
            (value, _) =>
            {
                progress.Add(value);
                return default;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.AcquireCalls);
        Assert.Equal(2, progress.Count);
        Assert.Equal(0.5, progress[0].Ratio);
        Assert.Equal(1, progress[1].Ratio);
        Assert.Single(progress.Select(value => value.OperationId).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidInventoryAndRegressingProgressFailClosed()
    {
        var backend = new FakeBackend
        {
            Inventory = new[]
            {
                new LocalGameModelInventoryItem("duplicate", LocalGameModelRuntimeState.Ready),
                new LocalGameModelInventoryItem("duplicate", LocalGameModelRuntimeState.Unloaded),
            },
            RegressProgress = true,
        };
        using var lifecycle = new LocalGameModelLifecycle(
            backend,
            new LocalGameModelLifecycleOptions
            {
                AuthorizeAcquisitionAsync = (_, _) => new ValueTask<bool>(true),
            });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await lifecycle.ReadInventoryAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await lifecycle.AcquireAsync(
                new LocalGameModelAcquisitionRequest("model"),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OllamaBackendMergesInventoryAndUsesExplicitLifecycleEndpoints()
    {
        var handler = new OllamaHandler();
        var backend = new OllamaGameModelLifecycleBackend(
            new OllamaGameModelLifecycleOptions(new HttpClient(handler)));
        using var lifecycle = new LocalGameModelLifecycle(
            backend,
            new LocalGameModelLifecycleOptions
            {
                AuthorizeAcquisitionAsync = (_, _) => new ValueTask<bool>(true),
            });

        var inventory = await lifecycle.ReadInventoryAsync(
            refresh: true,
            cancellationToken: TestContext.Current.CancellationToken);
        await lifecycle.LoadAsync("ready-model", TestContext.Current.CancellationToken);
        await lifecycle.UnloadAsync("ready-model", TestContext.Current.CancellationToken);
        var progress = new List<LocalGameModelOperationProgress>();
        await lifecycle.AcquireAsync(
            new LocalGameModelAcquisitionRequest("new-model"),
            (value, _) =>
            {
                progress.Add(value);
                return default;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalGameModelRuntimeState.Ready, inventory.Single(value => value.ModelId == "ready-model").State);
        Assert.Equal(LocalGameModelRuntimeState.Unloaded, inventory.Single(value => value.ModelId == "cold-model").State);
        Assert.Equal(2, handler.Bodies.Count(value => value.Contains("\"model\":\"ready-model\"", StringComparison.Ordinal)));
        Assert.Contains(handler.Bodies, value => value.Contains("\"keep_alive\":\"5m\"", StringComparison.Ordinal));
        Assert.Contains(handler.Bodies, value => value.Contains("\"keep_alive\":\"0\"", StringComparison.Ordinal));
        Assert.Equal("success", progress.Last().Stage);
        Assert.Equal(1, progress.Last().Ratio);
    }

    private sealed class FakeBackend : ILocalGameModelLifecycleBackend
    {
        public IReadOnlyList<LocalGameModelInventoryItem> Inventory { get; set; } =
            Array.Empty<LocalGameModelInventoryItem>();
        public bool RegressProgress { get; set; }
        public int AcquireCalls { get; private set; }

        public ValueTask<IReadOnlyList<LocalGameModelInventoryItem>> ReadInventoryAsync(
            bool refresh,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<LocalGameModelInventoryItem>>(Inventory);
        }

        public ValueTask WarmupAsync(string modelId, CancellationToken cancellationToken) => default;
        public ValueTask LoadAsync(string modelId, CancellationToken cancellationToken) => default;
        public ValueTask UnloadAsync(string modelId, CancellationToken cancellationToken) => default;

        public async IAsyncEnumerable<LocalGameModelOperationProgress> AcquireAsync(
            string operationId,
            LocalGameModelAcquisitionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            AcquireCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new LocalGameModelOperationProgress(
                operationId,
                request.ModelId,
                LocalGameModelOperationKind.Acquire,
                "download",
                50,
                100);
            yield return new LocalGameModelOperationProgress(
                operationId,
                request.ModelId,
                LocalGameModelOperationKind.Acquire,
                "complete",
                RegressProgress ? 40 : 100,
                100);
        }
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json("""
                    {"models":[
                      {"name":"ready-model","size":100,"digest":"sha-ready"},
                      {"name":"cold-model","size":200,"digest":"sha-cold"}
                    ]}
                    """),
                "/api/ps" => Json("""{"models":[{"name":"ready-model"}]}"""),
                "/api/generate" => Json("{}"),
                "/api/pull" => Ndjson(
                    "{\"status\":\"downloading\",\"completed\":50,\"total\":100}\n"
                    + "{\"status\":\"success\",\"completed\":100,\"total\":100}\n"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Ndjson(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
        };
    }
}
