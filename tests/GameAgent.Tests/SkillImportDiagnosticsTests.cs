using System.Text;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class SkillImportDiagnosticsTests
{
    [Fact]
    public void ImportsValidDocumentsInStableReferenceOrder()
    {
        var importer = new SkillManifestImporter();

        var result = importer.Import(
            new[]
            {
                Document("z.json", Manifest("zeta", "trusted")),
                Document("a.json", Manifest("alpha", "builtin"))
            });

        Assert.False(result.HasErrors);
        Assert.Equal(
            new[] { "alpha", "zeta" },
            result.Manifests.Select(item => item.SkillId));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ReportsInvalidJsonWithoutAbortingOtherDocuments()
    {
        var result = new SkillManifestImporter().Import(
            new[]
            {
                new SkillManifestDocument("bad.json", """{"unknown":true}"""),
                Document("good.json", Manifest("good", "trusted"))
            });

        Assert.True(result.HasErrors);
        Assert.Equal("good", Assert.Single(result.Manifests).SkillId);
        Assert.Equal(
            "skill_json_invalid",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ReportsDuplicateReferencesAndUntrustedStatus()
    {
        var result = new SkillManifestImporter().Import(
            new[]
            {
                Document("first.json", Manifest("shared", "untrusted")),
                Document("second.json", Manifest("shared", "trusted"))
            });

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "skill_reference_duplicate");
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "skill_untrusted_requires_policy"
                    && item.Severity
                    == SkillDiagnosticSeverities.Warning);
    }

    [Fact]
    public void DocumentLimitStopsEnumerationAfterTheOverflowItem()
    {
        var visited = 0;
        var importer = new SkillManifestImporter(
            new SkillManifestImportOptions
            {
                MaxDocuments = 2
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => importer.Import(Documents()));

        Assert.Equal(
            "skill_import_document_count_exceeded",
            error.LimitCode);
        Assert.Equal(3, visited);

        IEnumerable<SkillManifestDocument> Documents()
        {
            for (var index = 0; index < 100; index++)
            {
                visited++;
                yield return Document(
                    $"skill-{index}.json",
                    Manifest($"skill-{index}", "trusted"));
            }
        }
    }

    [Fact]
    public void AggregateUtf8LimitHasAStableFailureCode()
    {
        var first = Document(
            "first.json",
            Manifest("first", "trusted"));
        var second = Document(
            "second.json",
            Manifest("second", "trusted"));
        var firstBytes =
            Encoding.UTF8.GetByteCount(first.SourceId)
            + Encoding.UTF8.GetByteCount(first.Json);
        var importer = new SkillManifestImporter(
            new SkillManifestImportOptions
            {
                MaxAggregateUtf8Bytes = firstBytes
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => importer.Import(new[] { first, second }));

        Assert.Equal("skill_import_bytes_exceeded", error.LimitCode);
    }

    [Fact]
    public void RetainedManifestLimitHasAStableFailureCode()
    {
        var importer = new SkillManifestImporter(
            new SkillManifestImportOptions
            {
                MaxRetainedManifests = 1
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => importer.Import(
                new[]
                {
                    Document("one.json", Manifest("one", "trusted")),
                    Document("two.json", Manifest("two", "trusted"))
                }));

        Assert.Equal(
            "skill_import_manifest_count_exceeded",
            error.LimitCode);
    }

    [Fact]
    public void RetainedDiagnosticLimitHasAStableFailureCode()
    {
        var importer = new SkillManifestImporter(
            new SkillManifestImportOptions
            {
                MaxRetainedDiagnostics = 1
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => importer.Import(
                new[]
                {
                    new SkillManifestDocument(
                        "bad-one.json",
                        """{"unknown":true}"""),
                    new SkillManifestDocument(
                        "bad-two.json",
                        """{"unknown":true}""")
                }));

        Assert.Equal(
            "skill_import_diagnostic_count_exceeded",
            error.LimitCode);
    }

    private static SkillManifestDocument Document(
        string source,
        SkillManifest manifest)
    {
        return new SkillManifestDocument(
            source,
            ProtocolJson.Serialize(manifest));
    }

    private static SkillManifest Manifest(
        string id,
        string trust)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = "sha256:test",
            Description = "A test skill.",
            PromptFragments = new List<string> { "Follow the test rules." },
            CapabilityRequirements = ProtocolJson.ParseElement("{}"),
            ActivationPolicy = ProtocolJson.ParseElement("{}"),
            Trust = trust
        };
    }
}
