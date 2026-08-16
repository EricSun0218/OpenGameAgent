using OpenGameAgent.Models;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "154F655CE1BCD1148BB2F20C25D9EED633FC2DE9E86029169C8BE9E454580583";

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
