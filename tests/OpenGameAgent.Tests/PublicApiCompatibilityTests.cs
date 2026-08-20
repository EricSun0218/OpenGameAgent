using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "82A6A364A43A6DA627C51067EEF3FC62F88F4F7BC2C8FEFEF917F87A9A33377B";

    [Fact]
    public void RuntimePublicApiMatchesTheApprovedStableSurface()
    {
        var assembly = typeof(GameAgentRuntime).Assembly;
        var surface = PublicApiSurface.Describe(assembly);
        var hash = PublicApiSurface.Hash(assembly);

        Assert.True(
            string.Equals(ApprovedApiHash, hash, StringComparison.Ordinal),
            $"The runtime public API changed. Review the complete surface below, then update the approved hash intentionally.\nHash: {hash}\n\n{surface}");
    }
}
