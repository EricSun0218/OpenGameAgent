using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class ProviderTelemetryTests
{
    [Fact]
    public async Task RetryAndFallbackExposeBoundedAttemptDiagnosticsOnTerminalResponse()
    {
        var retry = new RetryingModelProvider(
            new FailOnceProvider(),
            maximumAttempts: 2,
            delay: _ => TimeSpan.Zero);
        var retried = await ReadTerminalAsync(retry);
        var retryDiagnostic = Assert.Single(retried.Diagnostics, value => value.Code == "oga.provider.retry");
        Assert.Contains("\"retries\":1", retryDiagnostic.DataJson, StringComparison.Ordinal);

        var fallback = new FallbackModelProvider(new IModelProvider[]
        {
            new AlwaysFailProvider(),
            new SuccessProvider(),
        });
        var resolved = await ReadTerminalAsync(fallback);
        var fallbackDiagnostic = Assert.Single(resolved.Diagnostics, value => value.Code == "oga.provider.fallback");
        Assert.Contains("\"fallbacks\":1", fallbackDiagnostic.DataJson, StringComparison.Ordinal);
    }

    private static async Task<ModelResponse> ReadTerminalAsync(IModelProvider provider)
    {
        var request = new ModelRequest(
            "model",
            string.Empty,
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            "session",
            "run",
            1);
        await foreach (var value in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            if (value.IsTerminal)
            {
                return value.Response!;
            }
        }

        throw new InvalidOperationException("No terminal response.");
    }

    private sealed class FailOnceProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new ModelProviderException("transient", isTransient: true);
            }

            yield return ModelStreamEvent.Terminal(Response());
            await Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            throw new ModelProviderException("transient", isTransient: true);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SuccessProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(Response());
            await Task.CompletedTask;
        }
    }

    private static ModelResponse Response() => new(
        new AgentContent[] { new TextContent("done") },
        ModelStopReason.Stop,
        new ModelUsage(1, 1),
        provider: "fake",
        responseModel: "model");
}
