using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "04A3378AD543FB002C0F766C5E12BE53D21515618D12228DF8D627D594CDBAE0";

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
