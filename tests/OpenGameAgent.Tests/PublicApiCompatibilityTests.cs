using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "8464EAE1DDE42264F872DFF64EE6F6E3E255BF5131E307ABC198A9FB8D7E0D71";

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
