using OpenGameAgent.Kernel;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "1B52D0DD45357353E20DBAF445CA817737BC52F18C8AAEBA1C4F335B9DAA3C63";

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
