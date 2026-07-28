using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ContextCompilerTests
{
    [Fact]
    public void CompilerSelectsDeterministicallyAndReportsDeferredPrunedAndExternalizedItems()
    {
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var compiler = new ContextCompiler(
            new ContextCompilerOptions(
                maxCandidates: 8,
                maxSelectedItems: 3,
                maxEstimatedTokens: 6,
                maxUtf8Bytes: 8_192,
                estimatedBytesPerToken: 100));
        var request = new ContextCompilationRequest(
            "run-1",
            "turn-1",
            new ContextCandidate[]
            {
                new(
                    "optional-low",
                    "state",
                    Json("""{"value":"low"}"""),
                    priority: 1,
                    estimatedTokens: 3),
                new(
                    "expired",
                    "event",
                    Json("""{"value":"old"}"""),
                    priority: 100,
                    estimatedTokens: 1,
                    expiresAt: now),
                new(
                    "required-resource",
                    "lore",
                    new ContextResourceReference(
                        "game://lore/chapter-1",
                        "application/json",
                        "sha256:lore"),
                    required: true,
                    estimatedTokens: 2),
                new(
                    "optional-high",
                    "state",
                    Json("""{"value":"high"}"""),
                    priority: 10,
                    estimatedTokens: 3)
            },
            now);

        var compiled = compiler.Compile(request);

        Assert.Equal(
            new[] { "required-resource", "optional-high" },
            compiled.Selected.Select(item => item.Candidate.Id));
        Assert.Equal(new[] { "optional-low" }, compiled.BudgetReport.DeferredIds);
        var pruned = Assert.Single(compiled.BudgetReport.Pruned);
        Assert.Equal("expired", pruned.Id);
        Assert.Equal("expired", pruned.ReasonCode);
        Assert.Equal(
            "game://lore/chapter-1",
            Assert.Single(compiled.BudgetReport.Externalized).Uri);
        Assert.Equal(5, compiled.BudgetReport.EstimatedTokens);
        Assert.Equal(
            new[] { "deferred_budget", "expired" },
            compiled.BudgetReport.ReasonCodes);
    }

    [Fact]
    public void RequiredContextAndSkillDisclosureFailClosedWhenTheyDoNotFit()
    {
        var requiredCompiler = new ContextCompiler(
            new ContextCompilerOptions(
                maxCandidates: 2,
                maxSelectedItems: 1,
                maxEstimatedTokens: 2,
                maxUtf8Bytes: 2_048,
                estimatedBytesPerToken: 100));
        var requiredException = Assert.Throws<ContextBudgetExceededException>(
            () => requiredCompiler.Compile(
                new ContextCompilationRequest(
                    "run-2",
                    "turn-2",
                    new[]
                    {
                        new ContextCandidate(
                            "required",
                            "state",
                            Json("""{"important":true}"""),
                            required: true,
                            estimatedTokens: 3)
                    },
                    DateTimeOffset.UtcNow)));
        Assert.Equal("required_context_budget_exceeded", requiredException.BudgetCode);

        var skillRegistry = new SkillCatalogRegistry();
        var skillSnapshot = skillRegistry.Replace(
            new[]
            {
                new SkillManifest
                {
                    SkillId = "combat",
                    Version = "1.0.0",
                    Digest = "declared:combat",
                    Description = "Combat decisions.",
                    PromptFragments = new List<string> { "Never invent combat state." },
                    CapabilityRequirements = Json("{}"),
                    ActivationPolicy = Json("""{"mode":"explicit"}"""),
                    Trust = "trusted"
                }
            });
        var disclosure = skillSnapshot.CreateDisclosure(
            new[] { new SkillReference("combat", "1.0.0") });
        var skillCompiler = new ContextCompiler(
            new ContextCompilerOptions(
                maxCandidates: 1,
                maxSelectedItems: 1,
                maxEstimatedTokens: 100_000,
                maxUtf8Bytes: disclosure.EstimatedUtf8Bytes - 1));

        var skillException = Assert.Throws<ContextBudgetExceededException>(
            () => skillCompiler.Compile(
                new ContextCompilationRequest(
                    "run-3",
                    "turn-3",
                    Array.Empty<ContextCandidate>(),
                    DateTimeOffset.UtcNow,
                    disclosure)));
        Assert.Equal("skill_context_bytes_exceeded", skillException.BudgetCode);
    }

    [Fact]
    public void CompilerRejectsDeepUntrustedJsonBeforeSelection()
    {
        var compiler = new ContextCompiler(
            new ContextCompilerOptions(
                maxCandidates: 2,
                maxSelectedItems: 1,
                maxEstimatedTokens: 100,
                maxUtf8Bytes: 4_096,
                candidateJsonLimits: new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 2,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 1_024,
                    maxContainerItems: 32)));
        var exception = Assert.Throws<RuntimeContentLimitException>(
            () => compiler.Compile(
                new ContextCompilationRequest(
                    "run-4",
                    "turn-4",
                    new[]
                    {
                        new ContextCandidate(
                            "deep",
                            "untrusted",
                            Json("""{"a":{"b":{"c":1}}}"""))
                    },
                    DateTimeOffset.UtcNow)));

        Assert.Equal("json_depth_exceeded", exception.LimitCode);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
