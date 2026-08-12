using OpenGameAgent.Models;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "8ECA4E20BBEE409CC73F57431CE89D23F5C5C99B2D98C38A31FF7BB8ADBD4F01";

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
