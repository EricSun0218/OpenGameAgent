using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenGameAgent;

public sealed class GameResourceSourceInfo
{
    public GameResourceSourceInfo(
        string source,
        string basePath,
        string filePath,
        string? scope = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Source = GameJson.RequireId(source, nameof(source));
        BasePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Scope = scope;
        var copied = new Dictionary<string, string>(
            metadata ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);
        if (copied.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Resource source metadata requires non-empty keys and non-null values.", nameof(metadata));
        }

        Metadata = new ReadOnlyDictionary<string, string>(copied);
    }

    public string Source { get; }

    public string? Scope { get; }

    public string BasePath { get; }

    public string FilePath { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}

public enum GameResourceDiagnosticSeverity
{
    Warning,
}

public static class GameResourceDiagnosticCodes
{
    public const string FileInfoFailed = "file_info_failed";
    public const string ListFailed = "list_failed";
    public const string ReadFailed = "read_failed";
    public const string ParseFailed = "parse_failed";
    public const string InvalidMetadata = "invalid_metadata";
    public const string LimitExceeded = "limit_exceeded";
    public const string UnsupportedEntry = "unsupported_entry";
}

public sealed class GameResourceDiagnostic
{
    public GameResourceDiagnostic(
        GameResourceDiagnosticSeverity severity,
        string code,
        string message,
        string path,
        GameResourceSourceInfo? sourceInfo = null)
    {
        if (!Enum.IsDefined(typeof(GameResourceDiagnosticSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Code = GameJson.RequireId(code, nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        SourceInfo = sourceInfo;
    }

    public GameResourceDiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string Path { get; }

    public GameResourceSourceInfo? SourceInfo { get; }
}

public sealed class GameSkillDiscoveryResult
{
    public GameSkillDiscoveryResult(
        IEnumerable<GameSkill> skills,
        IEnumerable<GameResourceDiagnostic>? diagnostics = null)
    {
        Skills = Copy(skills, nameof(skills));
        Diagnostics = Copy(diagnostics ?? Array.Empty<GameResourceDiagnostic>(), nameof(diagnostics));
    }

    public IReadOnlyList<GameSkill> Skills { get; }

    public IReadOnlyList<GameResourceDiagnostic> Diagnostics { get; }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var copied = values.ToArray();
        if (copied.Any(value => value is null))
        {
            throw new ArgumentException("Resource result collections cannot contain null values.", parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}

public sealed class GamePromptTemplate
{
    public GamePromptTemplate(
        string name,
        string content,
        string? description = null,
        string? argumentHint = null,
        GameResourceSourceInfo? sourceInfo = null)
    {
        Name = GameJson.RequireId(name, nameof(name));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Description = description ?? string.Empty;
        ArgumentHint = argumentHint;
        SourceInfo = sourceInfo;
    }

    public string Name { get; }

    public string Description { get; }

    public string? ArgumentHint { get; }

    public string Content { get; }

    public GameResourceSourceInfo? SourceInfo { get; }
}

public sealed class GamePromptTemplateLoadResult
{
    public GamePromptTemplateLoadResult(
        IEnumerable<GamePromptTemplate> promptTemplates,
        IEnumerable<GameResourceDiagnostic>? diagnostics = null)
    {
        if (promptTemplates is null)
        {
            throw new ArgumentNullException(nameof(promptTemplates));
        }

        if (diagnostics is null)
        {
            diagnostics = Array.Empty<GameResourceDiagnostic>();
        }

        var copiedTemplates = promptTemplates.ToArray();
        var copiedDiagnostics = diagnostics.ToArray();
        if (copiedTemplates.Any(value => value is null) || copiedDiagnostics.Any(value => value is null))
        {
            throw new ArgumentException("Resource result collections cannot contain null values.");
        }

        PromptTemplates = Array.AsReadOnly(copiedTemplates);
        Diagnostics = Array.AsReadOnly(copiedDiagnostics);
    }

    public IReadOnlyList<GamePromptTemplate> PromptTemplates { get; }

    public IReadOnlyList<GameResourceDiagnostic> Diagnostics { get; }
}

public static class GamePromptTemplateFormatter
{
    private static readonly Regex Placeholder = new(
        @"\$\{@:(\d+)(?::(\d+))?\}|\$(ARGUMENTS|@|\d+)",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ParseArguments(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in value)
        {
            if (quote is not null)
            {
                if (character == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddCurrent(arguments, current);
            }
            else
            {
                current.Append(character);
            }
        }

        AddCurrent(arguments, current);
        return Array.AsReadOnly(arguments.ToArray());
    }

    public static string Substitute(string content, IReadOnlyList<string> arguments)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        if (arguments.Any(value => value is null))
        {
            throw new ArgumentException("Template arguments cannot contain null values.", nameof(arguments));
        }

        var all = string.Join(" ", arguments);
        return Placeholder.Replace(content, match =>
        {
            if (match.Groups[1].Success)
            {
                var start = ParseIndex(match.Groups[1].Value);
                if (start < 0)
                {
                    start = 0;
                }

                int? count = match.Groups[2].Success
                    ? ParseIndex(match.Groups[2].Value, subtractOne: false)
                    : null;
                if (count is not null && count.Value <= 0)
                {
                    return string.Empty;
                }

                return string.Join(
                    " ",
                    count is null
                        ? arguments.Skip(start)
                        : arguments.Skip(start).Take(count.Value));
            }

            var simple = match.Groups[3].Value;
            if (simple is "@" or "ARGUMENTS")
            {
                return all;
            }

            var index = ParseIndex(simple);
            return index >= 0 && index < arguments.Count ? arguments[index] : string.Empty;
        });
    }

    public static string Format(GamePromptTemplate template, IReadOnlyList<string>? arguments = null)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        return Substitute(template.Content, arguments ?? Array.Empty<string>());
    }

    private static int ParseIndex(string value, bool subtractOne = true)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return int.MaxValue;
        }

        return subtractOne ? parsed - 1 : parsed;
    }

    private static void AddCurrent(List<string> arguments, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        arguments.Add(current.ToString());
        current.Clear();
    }
}

public static class GameSkillFormatter
{
    public static string FormatInvocation(GameSkill skill, string? additionalInstructions = null)
    {
        if (skill is null)
        {
            throw new ArgumentNullException(nameof(skill));
        }

        var filePath = skill.SourceInfo?.FilePath ?? string.Empty;
        var basePath = skill.SourceInfo is null
            ? string.Empty
            : Path.GetDirectoryName(skill.SourceInfo.FilePath) ?? skill.SourceInfo.BasePath;
        var formatted = "<skill name=\""
            + EscapeXml(skill.Name)
            + "\" location=\""
            + EscapeXml(filePath)
            + "\">\nReferences are relative to "
            + EscapeXml(basePath)
            + ".\n\n"
            + skill.Instructions
            + "\n</skill>";
        return string.IsNullOrEmpty(additionalInstructions)
            ? formatted
            : formatted + "\n\n" + additionalInstructions;
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
