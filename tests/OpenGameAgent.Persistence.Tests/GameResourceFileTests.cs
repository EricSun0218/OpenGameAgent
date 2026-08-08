using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class GameResourceFileTests
{
    [Fact]
    public async Task SkillDiscoveryHonorsIgnoreFilesPreservesSourceAndHidesExplicitOnlySkills()
    {
        using var directory = new TemporaryDirectory();
        await WriteAsync(
            Path.Combine(directory.Path, ".gitignore"),
            "*.md\n!visible/SKILL.md\n!explicit/SKILL.md\n!ignored/SKILL.md\n");
        await WriteAsync(
            Path.Combine(directory.Path, ".ignore"),
            "visible/SKILL.md/\n");
        await WriteAsync(
            Path.Combine(directory.Path, ".fdignore"),
            "[i]gnored/SKILL.md\n");
        await WriteSkillAsync(directory.Path, "visible", "Visible instructions.");
        await WriteSkillAsync(
            directory.Path,
            "explicit",
            "Explicit instructions.",
            "disable-model-invocation: true\n");
        await WriteSkillAsync(directory.Path, "ignored", "Ignored instructions.");
        var source = new DirectoryGameSkillSource(
            directory.Path,
            continueOnError: true,
            source: "package",
            sourceScope: "project");

        var discovered = source.Discover();
        var selected = await source.SelectAsync(
            new GameSkillQuery(
                new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
                Array.Empty<string>(),
                10),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "explicit", "visible" }, discovered.Skills.Select(skill => skill.SkillId).Order().ToArray());
        var explicitSkill = discovered.Skills.Single(skill => skill.SkillId == "explicit");
        Assert.True(explicitSkill.DisableModelInvocation);
        Assert.Equal("package", explicitSkill.SourceInfo!.Source);
        Assert.Equal("project", explicitSkill.SourceInfo.Scope);
        Assert.Equal(Path.GetFullPath(directory.Path), explicitSkill.SourceInfo.BasePath);
        Assert.EndsWith(
            Path.Combine("explicit", "SKILL.md"),
            explicitSkill.SourceInfo.FilePath,
            StringComparison.Ordinal);
        Assert.Equal("visible", Assert.Single(selected).SkillId);
        Assert.Empty(discovered.Diagnostics);
    }

    [Fact]
    public void TolerantMissingSkillRootIsEmptyWithoutDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "missing");

        var source = new DirectoryGameSkillSource(missing, continueOnError: true);
        var result = source.Discover();

        Assert.Empty(result.Skills);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task LooseRootMarkdownFailureIsDiagnosticWithoutBreakingStrictSkillLoading()
    {
        using var directory = new TemporaryDirectory();
        await WriteAsync(Path.Combine(directory.Path, "README.md"), "Repository notes only.");
        await WriteSkillAsync(directory.Path, "valid", "Valid instructions.");

        var source = new DirectoryGameSkillSource(directory.Path);
        var result = source.Discover();

        Assert.Equal("valid", Assert.Single(result.Skills).SkillId);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == GameResourceDiagnosticCodes.InvalidMetadata
            && diagnostic.Path.EndsWith("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TolerantSkillDiscoveryContinuesAfterInvalidFilesAndReportsStableDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        await WriteSkillAsync(directory.Path, "valid", "Valid instructions.");
        var brokenDirectory = Path.Combine(directory.Path, "broken");
        Directory.CreateDirectory(brokenDirectory);
        await WriteAsync(
            Path.Combine(brokenDirectory, "SKILL.md"),
            "---\nname: broken\n---\nMissing description.");
        var source = new DirectoryGameSkillSource(
            directory.Path,
            continueOnError: true,
            source: "local");

        var result = source.Discover();
        var selected = await source.SelectAsync(
            new GameSkillQuery(
                new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
                Array.Empty<string>(),
                10),
            TestContext.Current.CancellationToken);

        Assert.Equal("valid", Assert.Single(result.Skills).SkillId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("invalid_metadata", diagnostic.Code);
        Assert.Equal(GameResourceDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.EndsWith(Path.Combine("broken", "SKILL.md"), diagnostic.Path, StringComparison.Ordinal);
        Assert.Equal("local", diagnostic.SourceInfo!.Source);
        Assert.Equal("valid", Assert.Single(selected).SkillId);
        Assert.Single(source.Diagnostics);
    }

    [Fact]
    public async Task SkillDiscoveryLoadsOnlyDirectRootMarkdownAndStopsBelowSkillRoots()
    {
        using var directory = new TemporaryDirectory();
        await WriteAsync(
            Path.Combine(directory.Path, "root-skill.md"),
            "---\nname: root-skill\ndescription: Root skill.\n---\nRoot instructions.");
        var nestedLooseDirectory = Path.Combine(directory.Path, "loose");
        Directory.CreateDirectory(nestedLooseDirectory);
        await WriteAsync(
            Path.Combine(nestedLooseDirectory, "ignored.md"),
            "---\nname: ignored\ndescription: Nested loose markdown.\n---\nIgnored.");
        await WriteSkillAsync(directory.Path, "parent", "Parent instructions.");
        await WriteSkillAsync(
            Path.Combine(directory.Path, "parent"),
            "child",
            "Child instructions.");
        var source = new DirectoryGameSkillSource(directory.Path, continueOnError: true);

        var result = source.Discover();

        Assert.Equal(new[] { "parent", "root-skill" }, result.Skills.Select(skill => skill.SkillId).Order().ToArray());
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "invalid_metadata"
            && diagnostic.Path.EndsWith("root-skill.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromptTemplateLoaderLoadsDirectFilesMetadataArgumentsAndDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        var firstDirectory = Path.Combine(directory.Path, "first");
        var secondDirectory = Path.Combine(directory.Path, "second");
        Directory.CreateDirectory(Path.Combine(firstDirectory, "nested"));
        Directory.CreateDirectory(secondDirectory);
        await WriteAsync(
            Path.Combine(firstDirectory, "one.md"),
            "---\ndescription: One template\nargument-hint: <actor> <goal>\n---\nHello $1, pursue ${@:2}.");
        await WriteAsync(
            Path.Combine(firstDirectory, "nested", "ignored.md"),
            "Ignored nested template.");
        await WriteAsync(
            Path.Combine(secondDirectory, "two.md"),
            "First line description\nBody $ARGUMENTS");
        var broken = Path.Combine(directory.Path, "broken.md");
        await WriteAsync(
            broken,
            "---\ndescription: 'unterminated\n---\nBroken");
        var loader = new FileGamePromptTemplateLoader(
            new[] { firstDirectory, secondDirectory, broken, Path.Combine(directory.Path, "missing") },
            source: "package",
            sourceScope: "project");

        var result = loader.Load();

        Assert.Equal(new[] { "one", "two" }, result.PromptTemplates.Select(template => template.Name).ToArray());
        var one = result.PromptTemplates[0];
        Assert.Equal("One template", one.Description);
        Assert.Equal("<actor> <goal>", one.ArgumentHint);
        Assert.Equal("package", one.SourceInfo!.Source);
        Assert.Equal(firstDirectory, one.SourceInfo.BasePath);
        Assert.Equal(
            "Hello Mira, pursue restore village.",
            GamePromptTemplateFormatter.Format(one, new[] { "Mira", "restore", "village" }));
        Assert.Equal("First line description", result.PromptTemplates[1].Description);
        Assert.Equal("parse_failed", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task FileResourceDiagnosticsAreBoundedByCountAndMessageLength()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 3; index++)
        {
            var skillDirectory = Path.Combine(directory.Path, $"broken-{index}");
            Directory.CreateDirectory(skillDirectory);
            await WriteAsync(
                Path.Combine(skillDirectory, "SKILL.md"),
                "---\nname: 'unterminated\ndescription: Broken skill.\n---\nBroken");
        }

        var source = new DirectoryGameSkillSource(
            directory.Path,
            continueOnError: true,
            maximumDiagnostics: 2,
            maximumDiagnosticCharacters: 16);

        var skillResult = source.Discover();

        Assert.Equal(2, skillResult.Diagnostics.Count);
        Assert.All(skillResult.Diagnostics, diagnostic => Assert.True(diagnostic.Message.Length <= 16));

        var templatePaths = Enumerable.Range(0, 3)
            .Select(index => Path.Combine(directory.Path, $"template-{index}.md"))
            .ToArray();
        foreach (var path in templatePaths)
        {
            await WriteAsync(path, "---\ndescription: 'unterminated\n---\nBroken");
        }

        var loader = new FileGamePromptTemplateLoader(
            templatePaths,
            maximumDiagnostics: 2,
            maximumDiagnosticCharacters: 16);

        var templateResult = loader.Load();

        Assert.Equal(2, templateResult.Diagnostics.Count);
        Assert.All(templateResult.Diagnostics, diagnostic => Assert.True(diagnostic.Message.Length <= 16));
    }

    [Fact]
    public async Task PromptTemplateFrontMatterRequiresAnExactClosingDelimiter()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "literal.md");
        await WriteAsync(
            path,
            "---\ndescription: Metadata should remain literal.\n---not-a-delimiter\nBody");

        var result = new FileGamePromptTemplateLoader(path).Load();

        var template = Assert.Single(result.PromptTemplates);
        Assert.Empty(result.Diagnostics);
        Assert.StartsWith("---\n", template.Content, StringComparison.Ordinal);
        Assert.Equal("---", template.Description);
    }

    private static async Task WriteSkillAsync(
        string root,
        string name,
        string instructions,
        string extraFrontMatter = "")
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        await WriteAsync(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {name} skill.\n{extraFrontMatter}---\n{instructions}");
    }

    private static Task WriteAsync(string path, string content) =>
        File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "oga-resource-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
