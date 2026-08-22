using OpenGameAgent.Kernel;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "9A0E4350F074DE89CCBA1B4E281D8D9BA000152951044A2CB1339C3670B08736";

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
