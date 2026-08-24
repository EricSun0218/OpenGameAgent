using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly int _maximumIgnoreCharacters;
    private readonly int _maximumDiagnostics;
    private readonly int _maximumDiagnosticCharacters;
    private readonly bool _continueOnError;
    private readonly bool _honorIgnoreFiles;
    private readonly string _source;
    private readonly string? _sourceScope;
    private IReadOnlyList<GameResourceDiagnostic> _diagnostics = Array.Empty<GameResourceDiagnostic>();

    public DirectoryGameSkillSource(
        string directory,
        int maximumSkills,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters,
        int maximumScannedDirectories)
        : this(
            directory,
            maximumSkills,
            maximumManifestCharacters,
            maximumInstructionsCharacters,
            maximumScannedDirectories,
            maximumIgnoreCharacters: 100_000,
            continueOnError: false,
            honorIgnoreFiles: true,
            source: "local",
            sourceScope: null)
    {
    }

    public DirectoryGameSkillSource(
        string directory,
        int maximumSkills = 1_000,
        int maximumManifestCharacters = 100_000,
        int maximumInstructionsCharacters = 1_000_000,
        int maximumScannedDirectories = 10_000,
        int maximumIgnoreCharacters = 100_000,
        bool continueOnError = false,
        bool honorIgnoreFiles = true,
        string source = "local",
        string? sourceScope = null,
        int maximumDiagnostics = 1_024,
        int maximumDiagnosticCharacters = 64_000)
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

        if (maximumIgnoreCharacters < 0 || maximumIgnoreCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIgnoreCharacters));
        }

        if (maximumDiagnostics < 0 || maximumDiagnostics > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDiagnostics));
        }

        if (maximumDiagnosticCharacters <= 0 || maximumDiagnosticCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDiagnosticCharacters));
        }

        _root = Path.GetFullPath(directory);
        if (!Directory.Exists(_root) && !continueOnError)
        {
            throw new DirectoryNotFoundException($"Skill directory '{_root}' does not exist.");
        }

        _maximumSkills = maximumSkills;
        _maximumManifestCharacters = maximumManifestCharacters;
        _maximumInstructionsCharacters = maximumInstructionsCharacters;
        _maximumScannedDirectories = maximumScannedDirectories;
        _maximumIgnoreCharacters = maximumIgnoreCharacters;
        _maximumDiagnostics = maximumDiagnostics;
        _maximumDiagnosticCharacters = maximumDiagnosticCharacters;
        _continueOnError = continueOnError;
        _honorIgnoreFiles = honorIgnoreFiles;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("A resource source is required.", nameof(source));
        }

        _source = source;
        _sourceScope = sourceScope;
        var scan = LoadManifests();
        SetDiagnostics(scan.Diagnostics);
    }

    public IReadOnlyList<GameResourceDiagnostic> Diagnostics => Volatile.Read(ref _diagnostics);

    public GameSkillDiscoveryResult Discover()
    {
        var scan = LoadManifests();
        var diagnostics = NewDiagnostics(scan.Diagnostics);
        var skills = new List<GameSkill>();
        foreach (var manifest in scan.Manifests)
        {
            if (TryLoadSkill(manifest, diagnostics, out var skill))
            {
                skills.Add(skill!);
            }
        }

        SetDiagnostics(diagnostics.Items);
        return new GameSkillDiscoveryResult(skills, diagnostics.Items);
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
        var scan = LoadManifests();
        var diagnostics = NewDiagnostics(scan.Diagnostics);
        var candidates = scan.Manifests
            .Where(manifest => !manifest.Document.DisableModelInvocation)
            .Where(manifest => manifest.Document.InputTypes is null
                || manifest.Document.InputTypes.Count == 0
                || manifest.Document.InputTypes.Contains(query.Input.Type, StringComparer.Ordinal))
            .Where(manifest => (manifest.Document.ToolNames ?? new List<string>()).All(tools.Contains))
            .OrderByDescending(manifest => manifest.Document.Priority)
            .ThenBy(manifest => manifest.Document.Id, StringComparer.Ordinal);
        var selected = new List<GameSkill>();
        var characters = 0L;
        foreach (var manifest in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count >= query.Limit)
            {
                break;
            }

            if (TryLoadSkill(manifest, diagnostics, out var skill))
            {
                if (characters + skill!.CharacterCount > query.MaximumCharacters)
                {
                    continue;
                }

                selected.Add(skill);
                characters += skill.CharacterCount;
            }
        }

        SetDiagnostics(diagnostics.Items);
        return new ValueTask<IReadOnlyList<GameSkill>>(Array.AsReadOnly(selected.ToArray()));
    }

    private ManifestScan LoadManifests()
    {
        var diagnostics = NewDiagnostics();
        var descriptors = EnumerateSkillDescriptors(diagnostics)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(_maximumSkills + 1)
            .ToArray();
        if (descriptors.Length > _maximumSkills)
        {
            var exception = new GameRuntimeLimitException(
                "maximumSkills",
                "The directory contains too many skills.");
            if (!_continueOnError)
            {
                throw exception;
            }

            diagnostics.Add(Warning(GameResourceDiagnosticCodes.LimitExceeded, exception.Message, _root));
            descriptors = descriptors.Take(_maximumSkills).ToArray();
        }

        var loaded = new List<Manifest>();
        foreach (var path in descriptors)
        {
            try
            {
                var manifest = LoadManifest(path, _maximumManifestCharacters, _maximumInstructionsCharacters);
                loaded.Add(manifest);
                if (manifest.IsMarkdown)
                {
                    var parentName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
                    if (!string.Equals(manifest.Document.Name, parentName, StringComparison.Ordinal))
                    {
                        diagnostics.Add(Warning(
                            GameResourceDiagnosticCodes.InvalidMetadata,
                            $"Skill name '{manifest.Document.Name}' does not match parent directory '{parentName}'.",
                            path));
                    }
                }
            }
            catch (Exception exception) when (IsResourceFailure(exception))
            {
                if (!_continueOnError && !IsLooseRootMarkdown(path))
                {
                    throw;
                }

                diagnostics.Add(Warning(
                    IsLooseRootMarkdown(path)
                        ? GameResourceDiagnosticCodes.InvalidMetadata
                        : DiagnosticCode(exception),
                    exception.Message,
                    path));
            }
        }

        var deduplicated = new List<Manifest>();
        foreach (var group in loaded.GroupBy(item => item.Document.Id, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(item => item.DescriptorPath, StringComparer.Ordinal).ToArray();
            deduplicated.Add(ordered[0]);
            if (ordered.Length <= 1)
            {
                continue;
            }

            var message = $"Duplicate skill ID '{group.Key}'.";
            if (!_continueOnError)
            {
                throw new PersistenceException(message);
            }

            foreach (var duplicate in ordered.Skip(1))
            {
                diagnostics.Add(Warning(GameResourceDiagnosticCodes.InvalidMetadata, message, duplicate.DescriptorPath));
            }
        }

        return new ManifestScan(
            deduplicated.OrderBy(value => value.DescriptorPath, StringComparer.Ordinal).ToArray(),
            diagnostics.Items);
    }

    private bool TryLoadSkill(
        Manifest manifest,
        GameResourceDiagnosticBuffer diagnostics,
        out GameSkill? skill)
    {
        try
        {
            skill = LoadSkill(manifest);
            return true;
        }
        catch (Exception exception) when (IsResourceFailure(exception))
        {
            if (!_continueOnError)
            {
                throw;
            }

            diagnostics.Add(Warning(DiagnosticCode(exception), exception.Message, manifest.InstructionsPath));
            skill = null;
            return false;
        }
    }

    private GameSkill LoadSkill(Manifest manifest)
    {
        var instructions = manifest.IsMarkdown
            ? ReadMarkdownInstructions(
                manifest.InstructionsPath,
                _maximumManifestCharacters,
                _maximumInstructionsCharacters)
            : GameResourceFileSupport.ReadBounded(
                manifest.InstructionsPath,
                _maximumInstructionsCharacters);
        return new GameSkill(
            manifest.Document.Id,
            manifest.Document.Name,
            manifest.Document.Description ?? string.Empty,
            instructions,
            manifest.Document.InputTypes,
            manifest.Document.ToolNames,
            manifest.Document.Priority,
            manifest.Document.Metadata,
            manifest.Document.DisableModelInvocation,
            GameResourceFileSupport.SourceInfo(
                _source,
                _sourceScope,
                _root,
                manifest.DescriptorPath));
    }

    private static Manifest LoadManifest(
        string manifestPath,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters)
    {
        return manifestPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? LoadMarkdownSkill(manifestPath, maximumManifestCharacters, maximumInstructionsCharacters)
            : LoadJsonManifest(manifestPath, maximumManifestCharacters);
    }

    private static Manifest LoadJsonManifest(string manifestPath, int maximumManifestCharacters)
    {
        var manifestText = GameResourceFileSupport.ReadBounded(manifestPath, maximumManifestCharacters);
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

        return new Manifest(manifest, manifestPath, instructionsPath);
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
        var maximumFileCharacters = checked(maximumManifestCharacters + maximumInstructionsCharacters + 16);
        var frontMatter = GameResourceFileSupport.ParseFrontMatter(
            GameResourceFileSupport.ReadBounded(path, maximumFileCharacters),
            path);
        if (!frontMatter.HasFrontMatter)
        {
            throw new PersistenceException($"Skill file '{path}' requires YAML front matter.");
        }

        if (frontMatter.MetadataCharacters > maximumManifestCharacters)
        {
            throw new GameRuntimeLimitException(
                nameof(maximumManifestCharacters),
                $"Skill metadata in '{path}' exceeds its configured character limit.");
        }

        var directoryName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        var name = frontMatter.Values.TryGetValue("name", out var configuredName)
                   && !string.IsNullOrWhiteSpace(configuredName)
            ? configuredName
            : directoryName;
        if (!frontMatter.Values.TryGetValue("description", out var description)
            || string.IsNullOrWhiteSpace(description))
        {
            throw new PersistenceException($"Skill file '{path}' requires a description.");
        }

        ValidatePortableSkillName(name, path);
        if (description.Length > 1_024)
        {
            throw new PersistenceException($"Skill file '{path}' has a description longer than 1024 characters.");
        }

        var disableModelInvocation = false;
        if (frontMatter.Values.TryGetValue("disable-model-invocation", out var configuredDisable)
            && !bool.TryParse(configuredDisable, out disableModelInvocation))
        {
            throw new PersistenceException(
                $"Skill file '{path}' requires disable-model-invocation to be true or false.");
        }

        var document = new ManifestDocument
        {
            Id = name,
            Name = name,
            Description = description,
            DisableModelInvocation = disableModelInvocation,
        };
        return new Manifest(document, path, path, isMarkdown: true);
    }

    private static string ReadMarkdownInstructions(
        string path,
        int maximumManifestCharacters,
        int maximumInstructionsCharacters)
    {
        var maximumFileCharacters = checked(maximumManifestCharacters + maximumInstructionsCharacters + 16);
        var frontMatter = GameResourceFileSupport.ParseFrontMatter(
            GameResourceFileSupport.ReadBounded(path, maximumFileCharacters),
            path);
        if (!frontMatter.HasFrontMatter)
        {
            throw new PersistenceException($"Skill file '{path}' has invalid YAML front matter.");
        }

        if (frontMatter.MetadataCharacters > maximumManifestCharacters)
        {
            throw new GameRuntimeLimitException(
                nameof(maximumManifestCharacters),
                $"Skill metadata in '{path}' exceeds its configured character limit.");
        }

        if (frontMatter.Body.Length > maximumInstructionsCharacters)
        {
            throw new GameRuntimeLimitException(
                nameof(maximumInstructionsCharacters),
                $"Skill instructions in '{path}' exceed their configured character limit.");
        }

        return frontMatter.Body;
    }

    private IEnumerable<string> EnumerateSkillDescriptors(GameResourceDiagnosticBuffer diagnostics)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        var ignore = new GameIgnoreMatcher(
            _root,
            _maximumIgnoreCharacters,
            _source,
            _sourceScope);
        var pending = new Stack<string>();
        pending.Push(_root);
        var scanned = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            scanned++;
            if (scanned > _maximumScannedDirectories)
            {
                var exception = new GameRuntimeLimitException(
                    "maximumScannedDirectories",
                    "The skill directory tree exceeds its configured scan limit.");
                if (!_continueOnError)
                {
                    throw exception;
                }

                diagnostics.Add(Warning(GameResourceDiagnosticCodes.LimitExceeded, exception.Message, directory));
                yield break;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!_continueOnError)
                {
                    throw;
                }

                diagnostics.Add(Warning(GameResourceDiagnosticCodes.FileInfoFailed, exception.Message, directory));
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Warning(
                    GameResourceDiagnosticCodes.UnsupportedEntry,
                    $"Skill directory '{directory}' is a symbolic link or reparse point and was skipped.",
                    directory));
                continue;
            }

            if (_honorIgnoreFiles)
            {
                ignore.AddRules(directory, diagnostics);
            }

            var json = Path.Combine(directory, "skill.json");
            var markdown = Path.Combine(directory, "SKILL.md");
            if (File.Exists(json) && !ignore.IsIgnored(json, isDirectory: false))
            {
                yield return json;
                continue;
            }

            if (File.Exists(markdown) && !ignore.IsIgnored(markdown, isDirectory: false))
            {
                yield return markdown;
                continue;
            }

            string[] children;
            string[] files;
            try
            {
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .ToArray();
                files = string.Equals(directory, _root, StringComparison.Ordinal)
                    ? Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!_continueOnError)
                {
                    throw;
                }

                diagnostics.Add(Warning(GameResourceDiagnosticCodes.ListFailed, exception.Message, directory));
                continue;
            }

            foreach (var file in files)
            {
                if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)
                    || ignore.IsIgnored(file, isDirectory: false))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(Warning(
                            GameResourceDiagnosticCodes.UnsupportedEntry,
                            $"Skill file '{file}' is a symbolic link or reparse point and was skipped.",
                            file));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (!_continueOnError)
                    {
                        throw;
                    }

                    diagnostics.Add(Warning(GameResourceDiagnosticCodes.FileInfoFailed, exception.Message, file));
                    continue;
                }

                yield return file;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal)
                    || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                    || ignore.IsIgnored(child, isDirectory: true))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(Warning(
                            GameResourceDiagnosticCodes.UnsupportedEntry,
                            $"Skill directory '{child}' is a symbolic link or reparse point and was skipped.",
                            child));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (!_continueOnError)
                    {
                        throw;
                    }

                    diagnostics.Add(Warning(GameResourceDiagnosticCodes.FileInfoFailed, exception.Message, child));
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private GameResourceDiagnostic Warning(string code, string message, string path) =>
        GameResourceFileSupport.Warning(code, message, path, _source, _sourceScope, _root);

    private bool IsLooseRootMarkdown(string path) =>
        string.Equals(Path.GetDirectoryName(path), _root, StringComparison.Ordinal)
        && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase);

    private void SetDiagnostics(IEnumerable<GameResourceDiagnostic> diagnostics) =>
        Volatile.Write(ref _diagnostics, Array.AsReadOnly(diagnostics.ToArray()));

    private GameResourceDiagnosticBuffer NewDiagnostics(
        IEnumerable<GameResourceDiagnostic>? initial = null) =>
        new(_maximumDiagnostics, _maximumDiagnosticCharacters, initial);

    private static bool IsResourceFailure(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or PersistenceException
        or GameRuntimeLimitException
        or OverflowException;

    private static string DiagnosticCode(Exception exception) => exception switch
    {
        GameRuntimeLimitException => GameResourceDiagnosticCodes.LimitExceeded,
        IOException or UnauthorizedAccessException => GameResourceDiagnosticCodes.ReadFailed,
        _ when exception.Message.Contains("YAML", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase) => GameResourceDiagnosticCodes.ParseFailed,
        _ => GameResourceDiagnosticCodes.InvalidMetadata,
    };

    private static void ValidatePortableSkillName(string name, string path)
    {
        if (name.Length > 64
            || name.StartsWith("-", StringComparison.Ordinal)
            || name.EndsWith("-", StringComparison.Ordinal)
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

        public bool DisableModelInvocation { get; set; }
    }

    private sealed class Manifest
    {
        public Manifest(
            ManifestDocument document,
            string descriptorPath,
            string instructionsPath,
            bool isMarkdown = false)
        {
            Document = document;
            DescriptorPath = descriptorPath;
            InstructionsPath = instructionsPath;
            IsMarkdown = isMarkdown;
        }

        public ManifestDocument Document { get; }

        public string DescriptorPath { get; }

        public string InstructionsPath { get; }

        public bool IsMarkdown { get; }
    }

    private sealed class ManifestScan
    {
        public ManifestScan(
            IReadOnlyList<Manifest> manifests,
            IReadOnlyList<GameResourceDiagnostic> diagnostics)
        {
            Manifests = manifests;
            Diagnostics = diagnostics;
        }

        public IReadOnlyList<Manifest> Manifests { get; }

        public IReadOnlyList<GameResourceDiagnostic> Diagnostics { get; }
    }
}
