using OpenGameAgent.Kernel;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "27EA8E326C34C4BF77FFB1EC22D2F3A23D9E9FB75D90602BDC5AA9F154040A9F";

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
