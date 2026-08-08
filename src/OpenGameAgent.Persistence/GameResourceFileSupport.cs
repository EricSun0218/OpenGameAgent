using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenGameAgent.Persistence;

internal static class GameResourceFileSupport
{
    public static string ReadBounded(string path, int maximumCharacters, bool rejectReparsePoint = true)
    {
        if (maximumCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        if (rejectReparsePoint && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PersistenceException($"Resource file '{path}' cannot be a symbolic link or reparse point.");
        }

        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(4096, Math.Max(1, maximumCharacters + 1))];
        var result = new StringBuilder();
        while (result.Length <= maximumCharacters)
        {
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, maximumCharacters + 1 - result.Length));
            if (read == 0)
            {
                return result.ToString();
            }

            result.Append(buffer, 0, read);
        }

        throw new GameRuntimeLimitException(nameof(maximumCharacters), $"File '{path}' exceeds its configured character limit.");
    }

    public static FrontMatter ParseFrontMatter(string content, string path)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new FrontMatter(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                normalized,
                hasFrontMatter: false,
                metadataCharacters: 0);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var bodyStart = end < 0 ? normalized.Length : end + 5;
        if (end < 0 && normalized.EndsWith("\n---", StringComparison.Ordinal))
        {
            end = normalized.Length - 4;
        }

        if (end < 0)
        {
            return new FrontMatter(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                normalized,
                hasFrontMatter: false,
                metadataCharacters: 0);
        }

        var values = ParseScalarFrontMatter(normalized.Substring(4, end - 4), path);
        return new FrontMatter(
            values,
            normalized.Substring(bodyStart).Trim(),
            hasFrontMatter: true,
            metadataCharacters: end - 4);
    }

    public static GameResourceSourceInfo SourceInfo(
        string source,
        string? scope,
        string basePath,
        string filePath) =>
        new(source, basePath, filePath, scope);

    public static GameResourceDiagnostic Warning(
        string code,
        string message,
        string path,
        string source,
        string? scope,
        string basePath) =>
        new(
            GameResourceDiagnosticSeverity.Warning,
            code,
            message,
            path,
            SourceInfo(source, scope, basePath, path));

    private static IReadOnlyDictionary<string, string> ParseScalarFrontMatter(string text, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new PersistenceException($"Resource file '{path}' contains unsupported YAML metadata.");
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            if (key.Length == 0)
            {
                throw new PersistenceException($"Resource file '{path}' contains an empty YAML metadata key.");
            }

            if (value.StartsWith("\"", StringComparison.Ordinal)
                || value.StartsWith("'", StringComparison.Ordinal))
            {
                var quote = value[0];
                if (value.Length < 2 || value[^1] != quote)
                {
                    throw new PersistenceException($"Resource file '{path}' contains unterminated quoted YAML metadata.");
                }

                value = value.Substring(1, value.Length - 2);
            }

            if (!values.TryAdd(key, value))
            {
                throw new PersistenceException($"Resource file '{path}' contains duplicate YAML metadata '{key}'.");
            }
        }

        return values;
    }
}

internal sealed class FrontMatter
{
    public FrontMatter(
        IReadOnlyDictionary<string, string> values,
        string body,
        bool hasFrontMatter,
        int metadataCharacters)
    {
        Values = values;
        Body = body;
        HasFrontMatter = hasFrontMatter;
        MetadataCharacters = metadataCharacters;
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public string Body { get; }

    public bool HasFrontMatter { get; }

    public int MetadataCharacters { get; }
}

internal sealed class GameResourceDiagnosticBuffer
{
    private readonly List<GameResourceDiagnostic> _values = new();
    private readonly int _maximumDiagnostics;
    private readonly int _maximumMessageCharacters;

    public GameResourceDiagnosticBuffer(
        int maximumDiagnostics,
        int maximumMessageCharacters,
        IEnumerable<GameResourceDiagnostic>? initial = null)
    {
        _maximumDiagnostics = maximumDiagnostics;
        _maximumMessageCharacters = maximumMessageCharacters;
        foreach (var diagnostic in initial ?? Array.Empty<GameResourceDiagnostic>())
        {
            Add(diagnostic);
        }
    }

    public IReadOnlyList<GameResourceDiagnostic> Items => _values;

    public void Add(GameResourceDiagnostic diagnostic)
    {
        if (diagnostic is null)
        {
            throw new ArgumentNullException(nameof(diagnostic));
        }

        if (_values.Count >= _maximumDiagnostics)
        {
            return;
        }

        if (diagnostic.Message.Length <= _maximumMessageCharacters)
        {
            _values.Add(diagnostic);
            return;
        }

        _values.Add(new GameResourceDiagnostic(
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message.Substring(0, _maximumMessageCharacters),
            diagnostic.Path,
            diagnostic.SourceInfo));
    }
}

internal sealed class GameIgnoreMatcher
{
    private static readonly string[] IgnoreFileNames = { ".gitignore", ".ignore", ".fdignore" };
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly List<IgnoreRule> _rules = new();
    private readonly string _root;
    private readonly int _maximumIgnoreCharacters;
    private readonly string _source;
    private readonly string? _scope;

    public GameIgnoreMatcher(
        string root,
        int maximumIgnoreCharacters,
        string source,
        string? scope)
    {
        _root = root;
        _maximumIgnoreCharacters = maximumIgnoreCharacters;
        _source = source;
        _scope = scope;
    }

    public void AddRules(string directory, GameResourceDiagnosticBuffer diagnostics)
    {
        var relativeDirectory = RelativePath(directory);
        foreach (var name in IgnoreFileNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            string content;
            try
            {
                content = GameResourceFileSupport.ReadBounded(path, _maximumIgnoreCharacters);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or PersistenceException
                                              or GameRuntimeLimitException)
            {
                diagnostics.Add(GameResourceFileSupport.Warning(
                    GameResourceDiagnosticCodes.ReadFailed,
                    exception.Message,
                    path,
                    _source,
                    _scope,
                    _root));
                continue;
            }

            foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || (line.StartsWith("#", StringComparison.Ordinal)
                                         && !line.StartsWith("\\#", StringComparison.Ordinal)))
                {
                    continue;
                }

                var negated = false;
                if (line.StartsWith("!", StringComparison.Ordinal))
                {
                    negated = true;
                    line = line.Substring(1);
                }
                else if (line.StartsWith("\\!", StringComparison.Ordinal))
                {
                    line = line.Substring(1);
                }

                if (line.StartsWith("\\#", StringComparison.Ordinal))
                {
                    line = line.Substring(1);
                }

                var anchored = line.StartsWith("/", StringComparison.Ordinal);
                if (anchored)
                {
                    line = line.Substring(1);
                }

                var directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
                line = line.TrimEnd('/');
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    _rules.Add(new IgnoreRule(
                        BuildRegex(relativeDirectory, line, anchored),
                        negated,
                        directoryOnly));
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(GameResourceFileSupport.Warning(
                        GameResourceDiagnosticCodes.ParseFailed,
                        exception.Message,
                        path,
                        _source,
                        _scope,
                        _root));
                }
            }
        }
    }

    public bool IsIgnored(string path, bool isDirectory)
    {
        var relative = RelativePath(path);
        var ignored = false;
        foreach (var rule in _rules)
        {
            try
            {
                var match = rule.Pattern.Match(relative);
                if (match.Success
                    && (!rule.DirectoryOnly || isDirectory || match.Groups["descendant"].Success))
                {
                    ignored = !rule.Negated;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }

        return ignored;
    }

    private static Regex BuildRegex(
        string relativeDirectory,
        string pattern,
        bool anchored)
    {
        var containsSlash = pattern.Contains('/', StringComparison.Ordinal);
        var prefix = relativeDirectory.Length == 0
            ? string.Empty
            : Regex.Escape(relativeDirectory + "/");
        var expression = new StringBuilder("^");
        expression.Append(prefix);
        if (!anchored && !containsSlash)
        {
            expression.Append("(?:.*/)?");
        }

        expression.Append(Glob(pattern));
        expression.Append("(?<descendant>/.*)?$");
        return new Regex(expression.ToString(), RegexOptions.CultureInvariant, MatchTimeout);
    }

    private static string Glob(string pattern)
    {
        var result = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\' && index + 1 < pattern.Length)
            {
                index++;
                result.Append(Regex.Escape(pattern[index].ToString()));
            }
            else if (character == '[')
            {
                var closing = pattern.IndexOf(']', index + 1);
                if (closing <= index + 1
                    || pattern.Substring(index + 1, closing - index - 1).Contains('/', StringComparison.Ordinal))
                {
                    result.Append("\\[");
                    continue;
                }

                var characterClass = pattern.Substring(index + 1, closing - index - 1);
                result.Append('[');
                var classIndex = 0;
                if (characterClass.StartsWith("!", StringComparison.Ordinal))
                {
                    result.Append('^');
                    classIndex = 1;
                }
                else if (characterClass.StartsWith("^", StringComparison.Ordinal))
                {
                    result.Append("\\^");
                    classIndex = 1;
                }

                for (; classIndex < characterClass.Length; classIndex++)
                {
                    var classCharacter = characterClass[classIndex];
                    if (classCharacter is '\\' or ']')
                    {
                        result.Append('\\');
                    }

                    result.Append(classCharacter);
                }

                result.Append(']');
                index = closing;
            }
            else if (character == '*')
            {
                var doubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
                if (doubleStar)
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        result.Append("(?:.*/)?");
                    }
                    else
                    {
                        result.Append(".*");
                    }
                }
                else
                {
                    result.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                result.Append("[^/]");
            }
            else
            {
                result.Append(Regex.Escape(character.ToString()));
            }
        }

        return result.ToString();
    }

    private string RelativePath(string path)
    {
        var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
        return string.Equals(relative, ".", StringComparison.Ordinal) ? string.Empty : relative.Trim('/');
    }

    private sealed class IgnoreRule
    {
        public IgnoreRule(Regex pattern, bool negated, bool directoryOnly)
        {
            Pattern = pattern;
            Negated = negated;
            DirectoryOnly = directoryOnly;
        }

        public Regex Pattern { get; }

        public bool Negated { get; }

        public bool DirectoryOnly { get; }
    }
}
