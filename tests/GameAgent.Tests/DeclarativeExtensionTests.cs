using System.Text;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class DeclarativeExtensionTests
{
    [Fact]
    public void Closed_world_manifest_round_trips_declarative_bindings()
    {
        var manifest = Manifest("world.core", "1", Array.Empty<string>());
        manifest.SkillRefs = new[] { "npc.dialogue@1" };
        manifest.ToolSchemaRefs = new[] { "world.move@2" };
        manifest.WorkflowTemplateRefs = new[] { "monthly-evolution@1" };
        manifest.ProviderIds = new[] { "dialogue-fast" };
        manifest.ContextContributorIds = new[] { "actor-state" };
        manifest.Digest = DeclarativeExtensionCodec.ComputeDigest(manifest);

        var parsed = DeclarativeExtensionCodec.Parse(
            DeclarativeExtensionCodec.Serialize(manifest));

        Assert.Equal("world.move@2", Assert.Single(parsed.ToolSchemaRefs));
        Assert.Equal("monthly-evolution@1", Assert.Single(parsed.WorkflowTemplateRefs));
        Assert.Equal(manifest.Digest, parsed.Digest);
    }

    [Fact]
    public void Executable_or_unknown_declarations_are_rejected()
    {
        var json = """
            {
              "manifestVersion":"1",
              "namespace":"unsafe",
              "version":"1",
              "protocolVersion":"0.2",
              "digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "assembly":"payload.dll"
            }
            """;

        var exception = Assert.Throws<DeclarativeExtensionException>(
            () => DeclarativeExtensionCodec.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("extension_property_unknown", exception.ReasonCode);
    }

    [Fact]
    public void Digest_tampering_is_rejected()
    {
        var manifest = Manifest("world.core", "1", Array.Empty<string>());
        manifest.Digest = DeclarativeExtensionCodec.ComputeDigest(manifest);
        manifest.ProviderIds = new[] { "changed-after-signing" };

        var exception = Assert.Throws<DeclarativeExtensionException>(
            () => DeclarativeExtensionCodec.Serialize(manifest));

        Assert.Equal("extension_digest_mismatch", exception.ReasonCode);
    }

    [Fact]
    public void Missing_and_cyclic_dependencies_are_rejected()
    {
        var missing = Manifest("a", "1", new[] { "b@1" });
        missing.Digest = DeclarativeExtensionCodec.ComputeDigest(missing);
        var catalog = new DeclarativeExtensionCatalog();
        Assert.Equal(
            "extension_dependency_missing",
            Assert.Throws<DeclarativeExtensionException>(
                () => catalog.Replace(new[] { missing })).ReasonCode);

        var a = Manifest("a", "1", new[] { "b@1" });
        var b = Manifest("b", "1", new[] { "a@1" });
        a.Digest = DeclarativeExtensionCodec.ComputeDigest(a);
        b.Digest = DeclarativeExtensionCodec.ComputeDigest(b);
        Assert.Equal(
            "extension_dependency_cycle",
            Assert.Throws<DeclarativeExtensionException>(
                () => catalog.Replace(new[] { a, b })).ReasonCode);
    }

    [Fact]
    public void Catalog_snapshot_is_immutable_after_source_mutation()
    {
        var providerIds = new[] { "provider-a" };
        var manifest = Manifest("a", "1", Array.Empty<string>());
        manifest.ProviderIds = providerIds;
        manifest.Digest = DeclarativeExtensionCodec.ComputeDigest(manifest);
        var catalog = new DeclarativeExtensionCatalog();
        var captured = catalog.Replace(new[] { manifest });

        providerIds[0] = "provider-mutated";
        manifest.ProviderIds = new[] { "provider-replaced" };

        Assert.Equal("provider-a", Assert.Single(captured.Manifests).ProviderIds[0]);
        Assert.Equal("provider-a", Assert.Single(catalog.Current.Manifests).ProviderIds[0]);
    }

    [Fact]
    public void Ambiguous_identity_and_null_resource_are_rejected()
    {
        var ambiguous = Manifest("a@b", "1", Array.Empty<string>());
        Assert.Equal(
            "extension_namespace_invalid",
            Assert.Throws<DeclarativeExtensionException>(() =>
                DeclarativeExtensionCodec.ComputeDigest(ambiguous)).ReasonCode);

        var nullResource = Manifest("safe", "1", Array.Empty<string>());
        nullResource.Resources = new DeclarativeExtensionResource[] { null! };
        Assert.Equal(
            "extension_resource_invalid",
            Assert.Throws<DeclarativeExtensionException>(() =>
                DeclarativeExtensionCodec.ComputeDigest(nullResource)).ReasonCode);
    }

    private static DeclarativeExtensionManifest Manifest(
        string extensionNamespace,
        string version,
        IReadOnlyList<string> dependencies) => new()
        {
            Namespace = extensionNamespace,
            Version = version,
            Dependencies = dependencies,
            Digest = new string('0', 64)
        };
}
