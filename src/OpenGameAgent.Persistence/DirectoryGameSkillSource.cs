using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class DirectoryGameSkillSource : IGameSkillSource
{
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _root;
    private readonly int _maximumSkills;
    private readonly int _maximumManifestCharacters;
    private readonly int _maximumInstructionsCharacters;
    private readonly int _maximumScannedDirectories;

    public DirectoryGameSkillSource(
        string directory,
        int maximumSkills = 1_000,
        int maximumManifestCharacters = 100_000,
        int maximumInstructionsCharacters = 1_000_000,
        int maximumScannedDirectories = 10_000)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A skill directory is required.", nameof(directory));
        }

        if (maximumSkills < 0 || maximumSkills > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSkills));
        }

        if (maximumManifestCharacters < 2 || maximumManifestCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumManifestCharacters));
        }

        if (maximumInstructionsCharacters < 0 || maximumInstructionsCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInstructionsCharacters));
        }

        if (maximumScannedDirectories <= 0 || maximumScannedDirectories > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScannedDirectories));
        }

        _root = Path.GetFullPath(directory);
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Skill directory '{_root}' does not exist.");
        }

        _maximumSkills = maximumSkills;
        _maximumManifestCharacters = maximumManifestCharacters;
        _maximumInstructionsCharacters = maximumInstructionsCharacters;
        _maximumScannedDirectories = maximumScannedDirectories;
        _ = LoadManifests();
    }

    public ValueTask<IReadOnlyList<GameSkill>> SelectAsync(
        GameSkillQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var tools = new HashSet<string>(query.AvailableTools, StringComparer.Ordinal);
        var selected = LoadManifests()
            .Where(manifest => manifest.Document.InputTypes is null
                || manifest.Document.InputTypes.Count == 0
                || manifest.Document.InputTypes.Contains(query.Input.Type, StringComparer.Ordinal))
            .Where(manifest => (manifest.Document.ToolNames ?? new List<string>()).All(tools.Contains))
            .OrderByDescending(manifest => manifest.Document.Priority)
            .ThenBy(manifest => manifest.Document.Id, StringComparer.Ordinal)
            .Take(query.Limit)
            .Select(LoadSkill)
            .ToArray();
        return new ValueTask<IReadOnlyList<GameSkill>>(selected);
    }

    private IReadOnlyList<Manifest> LoadManifests()
    {
        var manifests = EnumerateSkillDescriptors(_maximumScannedDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(_maximumSkills + 1)
            .ToArray();
        if (manifests.Length > _maximumSkills)
        {
            throw new GameRuntimeLimitException(nameof(_maximumSkills), "The directory contains too many skills.");
        }

        var loaded = manifests.Select(path => LoadManifest(
            path,
            _maximumManifestCharacters,
            _maximumInstructionsCharacters)).ToArray();
        var duplicate = loaded.GroupBy(item => item.Document.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new PersistenceException($"Duplicate skill ID '{duplicate.Key}'.");
        }

        return loaded;
    }

    private GameSkill LoadSkill(Manifest manifest)
    {
        var instructions = manifest.IsMarkdown
            ? ReadMarkdownInstructions(
                manifest.InstructionsPath,
                _maximumManifestCharacters,
                _maximumInstructionsCharacters)
            : ReadBounded(manifest.InstructionsPath, _maximumInstructionsCharacters);
        return new GameSkill(
            manifest.Document.Id,
            manifest.Document.Name,
            manifest.Document.Description ?? string.Empty,
            instructions,
            manifest.Document.InputTypes,
            manifest.Document.ToolNames,
            manifest.Document.Priority,
            manifest.Document.Metadata);
    }

    private static Manifest LoadManifest(
        string manifestPath,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters)
    {
        return string.Equals(Path.GetFileName(manifestPath), "SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? LoadMarkdownSkill(manifestPath, maximumManifestCharacters, maximumInstructionsCharacters)
            : LoadJsonManifest(manifestPath, maximumManifestCharacters);
    }

    private static Manifest LoadJsonManifest(
        string manifestPath,
        int maximumManifestCharacters)
    {
        var manifestText = ReadBounded(manifestPath, maximumManifestCharacters);
        ManifestDocument manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestText, new JsonDocumentOptions { MaxDepth = 128 });
            EnsureManifestIsUnambiguous(document.RootElement, manifestPath);
            manifest = JsonSerializer.Deserialize<ManifestDocument>(
                document.RootElement.GetRawText(),
                ManifestSerializerOptions) ?? throw new PersistenceException("A skill manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new PersistenceException($"Skill manifest '{manifestPath}' contains invalid JSON.", exception);
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new PersistenceException($"Skill manifest '{manifestPath}' requires non-empty id and name values.");
        }

        ValidateIds(manifest.InputTypes, manifestPath, "inputTypes");
        ValidateIds(manifest.ToolNames, manifestPath, "toolNames");
        if (manifest.Metadata?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) == true)
        {
            throw new PersistenceException($"Skill manifest '{manifestPath}' contains invalid metadata.");
        }

        var instructionsFile = string.IsNullOrWhiteSpace(manifest.InstructionsFile)
            ? "instructions.md"
            : manifest.InstructionsFile;
        if (Path.GetFileName(instructionsFile) != instructionsFile)
        {
            throw new PersistenceException("A skill instructions file must be a file name inside its skill directory.");
        }

        var instructionsPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, instructionsFile);
        if (!File.Exists(instructionsPath))
        {
            throw new PersistenceException($"Skill instructions file '{instructionsPath}' does not exist.");
        }

        if ((File.GetAttributes(instructionsPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PersistenceException($"Skill file '{instructionsPath}' cannot be a symbolic link or reparse point.");
        }

        return new Manifest(manifest, instructionsPath);
    }

    private static void EnsureManifestIsUnambiguous(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new PersistenceException($"Skill manifest '{path}' contains duplicate JSON properties.");
                }

                EnsureManifestIsUnambiguous(property.Value, path);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureManifestIsUnambiguous(item, path);
            }
        }
    }

    private static Manifest LoadMarkdownSkill(
        string path,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters)
    {
        _ = maximumInstructionsCharacters;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PersistenceException($"Skill file '{path}' cannot be a symbolic link or reparse point.");
        }

        using var reader = new StreamReader(path);
        if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal))
        {
            throw new PersistenceException($"Skill file '{path}' requires YAML front matter.");
        }

        var metadata = new System.Text.StringBuilder();
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                throw new PersistenceException($"Skill file '{path}' has unterminated YAML front matter.");
            }

            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                break;
            }

            if (metadata.Length + line.Length + 1 > maximumManifestCharacters)
            {
                throw new GameRuntimeLimitException(nameof(maximumManifestCharacters), $"Skill metadata in '{path}' exceeds its configured character limit.");
            }

            metadata.AppendLine(line);
        }

        var values = ParseScalarFrontMatter(metadata.ToString(), path);
        var directoryName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        var name = values.TryGetValue("name", out var configuredName) && !string.IsNullOrWhiteSpace(configuredName)
            ? configuredName
            : directoryName;
        if (!values.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
        {
            throw new PersistenceException($"Skill file '{path}' requires a description.");
        }

        ValidatePortableSkillName(name, path);
        if (description.Length > 1_024)
        {
            throw new PersistenceException($"Skill file '{path}' has a description longer than 1024 characters.");
        }

        var document = new ManifestDocument
        {
            Id = name,
            Name = name,
            Description = description,
        };
        return new Manifest(document, path, isMarkdown: true);
    }

    private static string ReadMarkdownInstructions(
        string path,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters)
    {
        var maximumFileCharacters = checked(maximumManifestCharacters + maximumInstructionsCharacters + 16);
        var text = ReadBounded(path, maximumFileCharacters)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
        {
            throw new PersistenceException($"Skill file '{path}' has invalid YAML front matter.");
        }

        var instructions = text.Substring(end + 5).Trim();
        if (instructions.Length > maximumInstructionsCharacters)
        {
            throw new GameRuntimeLimitException(nameof(maximumInstructionsCharacters), $"Skill instructions in '{path}' exceed their configured character limit.");
        }

        return instructions;
    }

    private static IReadOnlyDictionary<string, string> ParseScalarFrontMatter(string text, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new PersistenceException($"Skill file '{path}' contains unsupported YAML metadata.");
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            if (value.Length >= 2
                && ((value.StartsWith('"') && value.EndsWith('"'))
                || (value.StartsWith('\'') && value.EndsWith('\'')))
               )
            {
                value = value.Substring(1, value.Length - 2);
            }

            if (!values.TryAdd(key, value))
            {
                throw new PersistenceException($"Skill file '{path}' contains duplicate YAML metadata '{key}'.");
            }
        }

        return values;
    }

    private IEnumerable<string> EnumerateSkillDescriptors(int maximumScannedDirectories)
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        var scanned = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            scanned++;
            if (scanned > maximumScannedDirectories)
            {
                throw new GameRuntimeLimitException(
                    nameof(maximumScannedDirectories),
                    "The skill directory tree exceeds its configured scan limit.");
            }

            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var json = Path.Combine(directory, "skill.json");
            var markdown = Path.Combine(directory, "SKILL.md");
            if (File.Exists(json))
            {
                yield return json;
                continue;
            }

            if (File.Exists(markdown))
            {
                yield return markdown;
                continue;
            }

            var children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return !name.StartsWith('.')
                        && !string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
                })
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .ToArray();
            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
    }

    private static void ValidatePortableSkillName(string name, string path)
    {
        if (name.Length > 64
            || name.StartsWith('-')
            || name.EndsWith('-')
            || name.Contains("--", StringComparison.Ordinal)
            || name.Any(character =>
                character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '-'))
        {
            throw new PersistenceException(
                $"Skill file '{path}' requires a lowercase name of at most 64 letters, digits, or single hyphens.");
        }
    }

    private static void ValidateIds(IReadOnlyCollection<string>? values, string path, string field)
    {
        if (values?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new PersistenceException($"Skill manifest '{path}' contains an invalid {field} value.");
        }
    }

    private static string ReadBounded(string path, int maximumCharacters)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PersistenceException($"Skill file '{path}' cannot be a symbolic link or reparse point.");
        }

        using var reader = new StreamReader(path);
        var buffer = new char[Math.Min(4096, Math.Max(1, maximumCharacters + 1))];
        var result = new System.Text.StringBuilder();
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

    private sealed class ManifestDocument
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? InstructionsFile { get; set; }

        public List<string>? InputTypes { get; set; }

        public List<string>? ToolNames { get; set; }

        public int Priority { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }
    }

    private sealed class Manifest
    {
        public Manifest(ManifestDocument document, string instructionsPath, bool isMarkdown = false)
        {
            Document = document;
            InstructionsPath = instructionsPath;
            IsMarkdown = isMarkdown;
        }

        public ManifestDocument Document { get; }

        public string InstructionsPath { get; }

        public bool IsMarkdown { get; }
    }
}
