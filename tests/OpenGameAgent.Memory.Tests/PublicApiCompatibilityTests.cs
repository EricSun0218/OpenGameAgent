using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Memory.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "75338A3C83A8F0B44B5B2223DD8A05698FCA8EEFE7F8D6080D775B33E1731D83";

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
