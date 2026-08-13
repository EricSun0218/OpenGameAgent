using OpenGameAgent.Kernel;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "7EC92B92D13B764CB0D3D3E8F71985220DBD6CF8C9D5DFC44C65C3D940CEEBBD";

    [Fact]
    public void KernelPublicApiMatchesTheApprovedStableSurface()
    {
        var surface = PublicApiSurface.Describe(typeof(Agent).Assembly);
        var hash = PublicApiSurface.Hash(typeof(Agent).Assembly);

        Assert.True(
            string.Equals(ApprovedApiHash, hash, StringComparison.Ordinal),
            $"The Kernel public API changed. Review the complete surface below, then update the approved hash intentionally.\nHash: {hash}\n\n{surface}");
    }
}
