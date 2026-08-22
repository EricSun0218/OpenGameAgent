using OpenGameAgent.Models;
using OpenGameAgent.Testing;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "C85826ED696E1A3D661DBEF588C0B46CD0848488C7244AE968A086478F208905";

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
