using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class ProviderConformanceTests
{
    [Fact]
    public async Task ValidNormalizedStreamPasses()
    {
        var report = await GameProviderConformance.RunAsync(new ValidProvider(), GameProviderConformanceFixtures.CreateTextRequest(), new GameProviderConformanceOptions { RequireProviderIdentity = true }, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(ModelStopReason.Stop, report.TerminalResponse?.StopReason);
        Assert.Equal(
            new[]
            {
                ModelStreamEventKind.Started,
                ModelStreamEventKind.TextStarted,
                ModelStreamEventKind.TextDelta,
                ModelStreamEventKind.TextEnded,
                ModelStreamEventKind.Completed,
            },
            report.EventKinds);
    }

    [Fact]
    public async Task InvalidOrderingAndSensitiveDiagnosticsFailClosed()
    {
        const string secret = "provider-secret";
        var report = await GameProviderConformance.RunAsync(new InvalidProvider(secret), GameProviderConformanceFixtures.CreateToolRequest(), new GameProviderConformanceOptions { ForbiddenValues = new[] { secret } }, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, value => value.Code == "stream.started-count");
        Assert.Contains(report.Diagnostics, value => value.Code == "response.sensitive-value");
        Assert.Contains(report.Diagnostics, value => value.Code == "stream.after-terminal");
    }

    [Fact]
    public async Task BlockingProviderMustObserveCancellation()
    {
        var report = await GameProviderConformance.RunCancellationProbeAsync(new BlockingProvider(), GameProviderConformanceFixtures.CreateTextRequest(), TimeSpan.FromMilliseconds(20), new GameProviderConformanceOptions { Timeout = TimeSpan.FromSeconds(2) }, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.True(report.CancellationObserved);
    }

    [Fact]
    public async Task NonCooperativeProviderIsBoundedAndFailsConformance()
    {
        var started = DateTimeOffset.UtcNow;
        var report = await GameProviderConformance.RunAsync(new NonCooperativeProvider(), GameProviderConformanceFixtures.CreateTextRequest(), new GameProviderConformanceOptions { Timeout = TimeSpan.FromMilliseconds(50) }, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, value => value.Code == "stream.timeout");
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProviderExceptionsRedactConfiguredSensitiveValues()
    {
        const string secret = "redact-this-value";
        var report = await GameProviderConformance.RunAsync(
            new ThrowingProvider(secret),
            GameProviderConformanceFixtures.CreateTextRequest(),
            new GameProviderConformanceOptions { ForbiddenValues = new[] { secret } },
            TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.DoesNotContain(report.Diagnostics, value => value.Message.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(report.Diagnostics, value => value.Message.Contains("[redacted]", StringComparison.Ordinal));
    }

    private sealed class ValidProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var empty = Pending();
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, empty);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.TextStarted, Pending(""));
            yield return ModelStreamEvent.Update(ModelStreamEventKind.TextDelta, Pending("ok"), delta: "ok");
            yield return ModelStreamEvent.Update(ModelStreamEventKind.TextEnded, Pending("ok"), content: "ok");
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("ok") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1),
                provider: "fixture",
                responseModel: "conformance-model"));
        }

        private static ModelResponse Pending(string? text = null) =>
            new(
                text is null ? Array.Empty<AgentContent>() : new AgentContent[] { new TextContent(text) },
                ModelStopReason.Pending);
    }

    private sealed class InvalidProvider : IModelProvider
    {
        private readonly string _secret;

        public InvalidProvider(string secret) => _secret = secret;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                Array.Empty<AgentContent>(),
                ModelStopReason.Error,
                errorMessage: "failed " + _secret));
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("late") },
                ModelStopReason.Stop));
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class NonCooperativeProvider : IModelProvider
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return new NonCooperativeEnumerable();
        }

        private sealed class NonCooperativeEnumerable : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly TaskCompletionSource<bool> _never =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ModelStreamEvent Current => null!;

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                _ = cancellationToken;
                return this;
            }

            public ValueTask<bool> MoveNextAsync() => new(_never.Task);

            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        private readonly string _secret;

        public ThrowingProvider(string secret) => _secret = secret;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException("transport exposed " + _secret);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
