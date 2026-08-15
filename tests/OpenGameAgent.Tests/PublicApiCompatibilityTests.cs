using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "436EAE0B0E6367F7532232DD799B22DB6CA3C2A01EC0E9D4009EE9B157427AEB";

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
