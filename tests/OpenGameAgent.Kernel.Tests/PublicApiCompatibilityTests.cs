using OpenGameAgent.Kernel;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "B3ED54CA04B927F02A163944DB93FC7812DF1F7A2E6E347C6A66D2A2A3831364";

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
