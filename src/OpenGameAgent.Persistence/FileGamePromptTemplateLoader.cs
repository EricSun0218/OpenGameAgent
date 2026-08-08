using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenGameAgent.Persistence;

public sealed class FileGamePromptTemplateLoader
{
    private readonly IReadOnlyList<string> _paths;
    private readonly int _maximumTemplates;
    private readonly int _maximumTemplateCharacters;
    private readonly int _maximumDiagnostics;
    private readonly int _maximumDiagnosticCharacters;
    private readonly string _source;
    private readonly string? _sourceScope;

    public FileGamePromptTemplateLoader(
        string path,
        int maximumTemplates = 1_000,
        int maximumTemplateCharacters = 1_000_000,
        string source = "local",
        string? sourceScope = null,
        int maximumDiagnostics = 1_024,
        int maximumDiagnosticCharacters = 64_000)
        : this(
            new[] { path ?? throw new ArgumentNullException(nameof(path)) },
            maximumTemplates,
            maximumTemplateCharacters,
            source,
            sourceScope,
            maximumDiagnostics,
            maximumDiagnosticCharacters)
    {
    }

    public FileGamePromptTemplateLoader(
        IEnumerable<string> paths,
        int maximumTemplates = 1_000,
        int maximumTemplateCharacters = 1_000_000,
        string source = "local",
        string? sourceScope = null,
        int maximumDiagnostics = 1_024,
        int maximumDiagnosticCharacters = 64_000)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        if (maximumTemplates < 0 || maximumTemplates > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTemplates));
        }

        if (maximumTemplateCharacters < 0 || maximumTemplateCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTemplateCharacters));
        }

        if (maximumDiagnostics < 0 || maximumDiagnostics > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDiagnostics));
        }

        if (maximumDiagnosticCharacters <= 0 || maximumDiagnosticCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDiagnosticCharacters));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("A resource source is required.", nameof(source));
        }

        var copied = paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Prompt template paths cannot be empty.", nameof(paths));
            }

            return Path.GetFullPath(path);
        }).ToArray();
        if (copied.Length > 10_000)
        {
            throw new ArgumentException("Too many prompt template paths were configured.", nameof(paths));
        }

        _paths = Array.AsReadOnly(copied);
        _maximumTemplates = maximumTemplates;
        _maximumTemplateCharacters = maximumTemplateCharacters;
        _maximumDiagnostics = maximumDiagnostics;
        _maximumDiagnosticCharacters = maximumDiagnosticCharacters;
        _source = source;
        _sourceScope = sourceScope;
    }

    public GamePromptTemplateLoadResult Load()
    {
        var templates = new List<GamePromptTemplate>();
        var diagnostics = new GameResourceDiagnosticBuffer(
            _maximumDiagnostics,
            _maximumDiagnosticCharacters);
        foreach (var path in _paths)
        {
            if (templates.Count >= _maximumTemplates)
            {
                diagnostics.Add(Warning(
                    GameResourceDiagnosticCodes.LimitExceeded,
                    "The configured prompt template count limit was reached.",
                    path,
                    Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path));
                break;
            }

            if (Directory.Exists(path))
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(Warning(
                            GameResourceDiagnosticCodes.UnsupportedEntry,
                            $"Prompt template directory '{path}' is a symbolic link or reparse point and was skipped.",
                            path,
                            path));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Warning(
                        GameResourceDiagnosticCodes.FileInfoFailed,
                        exception.Message,
                        path,
                        path));
                    continue;
                }

                LoadDirectory(path, templates, diagnostics);
            }
            else if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                TryLoadFile(path, Path.GetDirectoryName(path)!, templates, diagnostics);
            }
        }

        return new GamePromptTemplateLoadResult(templates, diagnostics.Items);
    }

    private void LoadDirectory(
        string directory,
        List<GamePromptTemplate> templates,
        GameResourceDiagnosticBuffer diagnostics)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Warning(GameResourceDiagnosticCodes.ListFailed, exception.Message, directory, directory));
            return;
        }

        foreach (var file in files)
        {
            if (templates.Count >= _maximumTemplates)
            {
                diagnostics.Add(Warning(
                    GameResourceDiagnosticCodes.LimitExceeded,
                    "The configured prompt template count limit was reached.",
                    file,
                    directory));
                return;
            }

            TryLoadFile(file, directory, templates, diagnostics);
        }
    }

    private void TryLoadFile(
        string path,
        string basePath,
        List<GamePromptTemplate> templates,
        GameResourceDiagnosticBuffer diagnostics)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Warning(
                    GameResourceDiagnosticCodes.UnsupportedEntry,
                    $"Prompt template '{path}' is a symbolic link or reparse point and was skipped.",
                    path,
                    basePath));
                return;
            }

            var frontMatter = GameResourceFileSupport.ParseFrontMatter(
                GameResourceFileSupport.ReadBounded(path, _maximumTemplateCharacters),
                path);
            var description = frontMatter.Values.TryGetValue("description", out var configuredDescription)
                ? configuredDescription
                : string.Empty;
            if (description.Length == 0)
            {
                var firstLine = frontMatter.Body.Split('\n').FirstOrDefault(line => line.Trim().Length > 0);
                if (firstLine is not null)
                {
                    description = firstLine.Length <= 60
                        ? firstLine
                        : firstLine.Substring(0, 60) + "...";
                }
            }

            frontMatter.Values.TryGetValue("argument-hint", out var argumentHint);
            templates.Add(new GamePromptTemplate(
                Path.GetFileNameWithoutExtension(path),
                frontMatter.Body,
                description,
                argumentHint,
                GameResourceFileSupport.SourceInfo(
                    _source,
                    _sourceScope,
                    basePath,
                    path)));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PersistenceException
                                          or GameRuntimeLimitException)
        {
            var code = exception switch
            {
                GameRuntimeLimitException => GameResourceDiagnosticCodes.LimitExceeded,
                IOException or UnauthorizedAccessException => GameResourceDiagnosticCodes.ReadFailed,
                _ => GameResourceDiagnosticCodes.ParseFailed,
            };
            diagnostics.Add(Warning(code, exception.Message, path, basePath));
        }
    }

    private GameResourceDiagnostic Warning(
        string code,
        string message,
        string path,
        string basePath) =>
        GameResourceFileSupport.Warning(
            code,
            message,
            path,
            _source,
            _sourceScope,
            basePath);
}
