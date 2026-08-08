using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class ModelProviderExceptionTests
{
    [Fact]
    public void CombinedFailurePreservesRetryMetadataAndDiagnostics()
    {
        var diagnostic = new ModelDiagnostic(
            "provider_failure",
            "Structured metadata was returned.",
            ModelDiagnosticSeverity.Error,
            "{\"requestId\":\"request-1\"}");

        var failure = new ModelProviderException(
            "temporarily unavailable",
            new[] { diagnostic },
            isTransient: true,
            retryAfter: TimeSpan.FromSeconds(2),
            statusCode: 503);

        Assert.True(failure.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(2), failure.RetryAfter);
        Assert.Equal(503, failure.StatusCode);
        Assert.Same(diagnostic, Assert.Single(failure.Diagnostics));
        Assert.NotEqual(typeof(HttpRequestException), typeof(ModelProviderException).BaseType);
    }

    [Fact]
    public void CombinedFailureRejectsNegativeRetryDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelProviderException(
            "invalid",
            Array.Empty<ModelDiagnostic>(),
            isTransient: true,
            retryAfter: TimeSpan.FromMilliseconds(-1)));
    }
}
