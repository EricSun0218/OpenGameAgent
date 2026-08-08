using Xunit;

namespace OpenGameAgent.Tests;

public sealed class GameResourceTests
{
    [Fact]
    public void PromptTemplateFormatterParsesQuotesAndSubstitutesAllSupportedFormsOnce()
    {
        var arguments = GamePromptTemplateFormatter.ParseArguments(
            "command \"first value\" 'second value'\nthird");
        var template = new GamePromptTemplate(
            "example",
            "$1|$2|${@:2}|${@:2:2}|$@|$ARGUMENTS");

        var result = GamePromptTemplateFormatter.Format(template, arguments);

        Assert.Equal(
            "command|first value|first value second value third|first value second value|command first value second value third|command first value second value third",
            result);
        Assert.Equal(
            "$ARGUMENTS $1",
            GamePromptTemplateFormatter.Substitute("$1 $2", new[] { "$ARGUMENTS", "$1" }));
    }

    [Fact]
    public void PromptTemplateFormatterHandlesMissingIndicesSlicesUnicodeAndEmptyQuotes()
    {
        var arguments = new[] { "日本語", "🎉", "café" };

        Assert.Equal("||日本語 🎉 café|🎉 café|", GamePromptTemplateFormatter.Substitute(
            "$0|$9|${@:0}|${@:2}|${@:2:0}",
            arguments));
        Assert.Equal("日本語.5", GamePromptTemplateFormatter.Substitute("$1.5", arguments));
        Assert.Equal(
            new[] { " " },
            GamePromptTemplateFormatter.ParseArguments("\"\" \" \"").ToArray());
        Assert.Equal(
            new[] { "line1\nline2", "second" },
            GamePromptTemplateFormatter.ParseArguments("\"line1\nline2\" second").ToArray());
    }

    [Fact]
    public async Task DisabledSkillsRemainDiscoverableButAreExcludedFromAutomaticSelection()
    {
        var visible = new GameSkill("visible", "Visible", "", "Visible instructions.");
        var explicitOnly = new GameSkill(
            "explicit",
            "Explicit",
            "",
            "Explicit instructions.",
            disableModelInvocation: true);
        var source = new InMemoryGameSkillSource(new[] { visible, explicitOnly });
        var query = new GameSkillQuery(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            Array.Empty<string>(),
            10);

        var selected = await source.SelectAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal("visible", Assert.Single(selected).SkillId);
        Assert.True(explicitOnly.DisableModelInvocation);
    }

    [Fact]
    public void SkillInvocationIncludesEscapedProvenanceAndRelativeReferenceBase()
    {
        var source = new GameResourceSourceInfo(
            "package",
            "C:\\game\\skills",
            "C:\\game\\skills\\inspect\\SKILL.md",
            "project");
        var skill = new GameSkill(
            "inspect",
            "inspect&verify",
            "Inspect",
            "Use inspection tools.",
            sourceInfo: source);

        var invocation = GameSkillFormatter.FormatInvocation(skill, "Check errors.");

        Assert.Contains("inspect&amp;verify", invocation, StringComparison.Ordinal);
        Assert.Contains("C:\\game\\skills\\inspect", invocation, StringComparison.Ordinal);
        Assert.EndsWith("</skill>\n\nCheck errors.", invocation, StringComparison.Ordinal);
    }
}
