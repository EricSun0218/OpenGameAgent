using OpenGameAgent.Models;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "2E7B2A1A0F19FF66AE6672B5B1F82E55BCC9F25149DCC90BF7A6814009D8B073";

    [Fact]
    public void ModelsPublicApiMatchesTheApprovedStableSurface()
    {
        var assembly = typeof(GameModelCatalog).Assembly;
        var surface = PublicApiSurface.Describe(assembly);
        var hash = PublicApiSurface.Hash(assembly);

        Assert.True(
            string.Equals(ApprovedApiHash, hash, StringComparison.Ordinal),
            $"The Models public API changed. Review the complete surface below, then update the approved hash intentionally.\nHash: {hash}\n\n{surface}");
    }
}
