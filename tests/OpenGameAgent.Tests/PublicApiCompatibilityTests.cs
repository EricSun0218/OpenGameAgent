using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "E026D6B2B09FCF03E2C3FC8CB1493F87BA52A8DB9716B13139C29036A2B0104A";

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
