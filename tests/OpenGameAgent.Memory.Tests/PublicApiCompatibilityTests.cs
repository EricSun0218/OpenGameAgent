using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Memory.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "D2C8CFC19C641681EC3EB2629AFD0BD2199A9491D862FF7B61EC97A4DE08B2CD";

    [Fact]
    public void MemoryPublicApiMatchesTheApprovedStableSurface()
    {
        var assembly = typeof(VectorMemoryStore).Assembly;
        var surface = PublicApiSurface.Describe(assembly);
        var hash = PublicApiSurface.Hash(assembly);

        Assert.True(
            string.Equals(ApprovedApiHash, hash, StringComparison.Ordinal),
            $"The memory public API changed. Review the complete surface below, then update the approved hash intentionally.\nHash: {hash}\n\n{surface}");
    }
}
