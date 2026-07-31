using System.Text;
using GameAgent.Compatibility;
using GameAgent.Runtime;
using GameAgent.World;
using Godot;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

/// <summary>
/// Editor-safe bridge for validating authored native-world files and building
/// deterministic package archives. Authored JSON remains inert data.
/// </summary>
[Tool]
[GlobalClass]
public partial class GodotWorldAuthoringBridge : RefCounted
{
    private static readonly WorldPackageLimits AuthoringLimits = new(
        maxFiles: 7,
        maxFileBytes: 4L * 1_048_576,
        maxExpandedBytes: 16L * 1_048_576,
        maxCompressedBytes: 16L * 1_048_576,
        maxJsonNodes: 100_000,
        maxJsonStringUtf8Bytes: 1_048_576,
        maxJsonContainerItems: 50_000);

    private static readonly WorldPackageLimits BoundAuthoringLimits = new(
        maxFiles: 12,
        maxFileBytes: 4L * 1_048_576,
        maxExpandedBytes: 24L * 1_048_576,
        maxCompressedBytes: 24L * 1_048_576,
        maxJsonNodes: 100_000,
        maxJsonStringUtf8Bytes: 1_048_576,
        maxJsonContainerItems: 50_000);

    private static readonly CompatibilityImportOptions ImportOptions = new(
        maxInputBytes: 4 * 1_048_576,
        maxDecodedPayloadBytes: 4 * 1_048_576,
        maxJsonDepth: 64,
        maxJsonNodes: 100_000,
        maxLoreBookEntries: 4_096,
        maxCollectionItems: 4_096,
        maxStringCharacters: 1_000_000,
        maxPngChunks: 4_096,
        maxPngChunkBytes: 4 * 1_048_576,
        maxDirectivesPerEntry: 128);

    private static readonly string[] AuthoredFiles =
    {
        "world.json",
        "clocks.json",
        "numerics.json",
        "events.json",
        "interactions.json",
        "agents.json",
        "knowledge.json"
    };

    private static readonly IReadOnlyDictionary<string, string>
        StarterFiles = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["world.json"] =
                """
                {
                  "contract": "game-agent.world-definition.v1",
                  "worldId": "starter-world",
                  "defaultTimelineId": "main",
                  "entityStateRootPath": "/entities",
                  "relationshipRootPath": "/relationships",
                  "initialState": {
                    "entities": {
                      "npc_a": {"tags": ["npc"], "affinity": "500"},
                      "npc_b": {"tags": ["npc"], "affinity": "500"}
                    },
                    "relationships": {}
                  },
                  "entityIncarnations": {"npc_a": "1", "npc_b": "1"},
                  "catalogs": {
                    "clocks": "clocks.json",
                    "numerics": "numerics.json",
                    "events": "events.json",
                    "interactions": "interactions.json",
                    "agents": "agents.json",
                    "knowledge": "knowledge.json"
                  }
                }
                """,
            ["clocks.json"] =
                """
                {
                  "contract": "game-agent.world-clocks.v1",
                  "clocks": [
                    {
                      "clockId": "calendar.month",
                      "statePath": "/time/month",
                      "initialTick": "0"
                    }
                  ]
                }
                """,
            ["numerics.json"] =
                """
                {
                  "contract": "game-agent.world-numerics.v1",
                  "schemas": [
                    {
                      "schemaId": "affinity.points",
                      "scale": 0,
                      "unitId": "affinity-point",
                      "minimum": "0",
                      "maximum": "1000",
                      "defaultValue": "0"
                    }
                  ]
                }
                """,
            ["events.json"] =
                """
                {
                  "contract": "game-agent.world-events.v1",
                  "events": [
                    {
                      "definitionId": "monthly-affinity",
                      "version": "1",
                      "priority": 10,
                      "trigger": {
                        "kind": "clock",
                        "clockId": "calendar.month",
                        "everyTicks": "1"
                      },
                      "selector": {
                        "kind": "entity",
                        "entityId": "npc_a",
                        "incarnation": "1",
                        "role": "subject"
                      },
                      "condition": {"kind": "always"},
                      "effects": [
                        {
                          "kind": "numeric",
                          "effectId": "monthly-affinity-change",
                          "entity": "subject",
                          "path": "/affinity",
                          "resourceKey": "entity:npc_a:affinity",
                          "schemaId": "affinity.points",
                          "operation": "add",
                          "value": "1"
                        }
                      ]
                    }
                  ]
                }
                """,
            ["interactions.json"] =
                """
                {
                  "contract": "game-agent.world-interactions.v1",
                  "interactions": [
                    {
                      "interactionId": "greet",
                      "version": "1",
                      "contentRevision": "1",
                      "priority": 10,
                      "parameterSchemaId": "greet.input",
                      "parameterSchemaVersion": "1",
                      "parameterSchema": {
                        "type": "object",
                        "required": ["tone"],
                        "properties": {
                          "tone": {
                            "type": "string",
                            "enum": ["warm", "formal"]
                          }
                        },
                        "additionalProperties": false
                      },
                      "target": {
                        "schemaId": "world.entity",
                        "minimumTargets": 1,
                        "maximumTargets": 1
                      },
                      "channelIds": ["local"],
                      "tags": ["social"],
                      "requiredCapabilities": [],
                      "availability": {"kind": "tag", "tag": "npc"},
                      "effects": [
                        {
                          "kind": "set",
                          "effectId": "record-greeting",
                          "entity": "target:0",
                          "path": "/greeted",
                          "resourceKey": "entity:npc_b:greeted",
                          "value": true
                        }
                      ],
                      "presentation": {"label": "Greet"}
                    }
                  ]
                }
                """,
            ["agents.json"] =
                """
                {
                  "contract": "game-agent.world-agents.v1",
                  "agents": [
                    {
                      "id": "npc_a",
                      "version": "1",
                      "data": {
                        "entityId": "npc_a",
                        "displayName": "NPC A",
                        "persona": "Replace this authored persona.",
                        "goals": ["Replace this authored goal."]
                      }
                    },
                    {
                      "id": "npc_b",
                      "version": "1",
                      "data": {
                        "entityId": "npc_b",
                        "displayName": "NPC B",
                        "persona": "Replace this authored persona.",
                        "goals": ["Replace this authored goal."]
                      }
                    }
                  ]
                }
                """,
            ["knowledge.json"] =
                """
                {
                  "contract": "game-agent.world-knowledge.v1",
                  "knowledge": [
                    {
                      "id": "starter-knowledge",
                      "version": "1",
                      "data": {
                        "title": "Starter knowledge",
                        "content": "Replace this authored world knowledge.",
                        "tags": ["starter"],
                        "visibleTo": ["npc_a", "npc_b"]
                      }
                    }
                  ]
                }
                """
        };

    public GodotDictionary validate_world_source(
        string sourceDirectory,
        string packageId,
        string contentVersion)
    {
        try
        {
            var source = ReadSource(
                sourceDirectory,
                packageId,
                contentVersion);
            var compilation = new NativeWorldPackageCompiler(
                    limits: AuthoringLimits)
                .Compile(source);
            return CompilationStatus(source, compilation);
        }
        catch (Exception exception) when (IsExpectedAuthoringFailure(exception))
        {
            return FailureStatus(exception);
        }
    }

    public GodotDictionary validate_imports(
        string characterCardPath,
        string characterContentId,
        string loreBookPath,
        string loreContentId)
    {
        try
        {
            var imports = ReadImports(
                characterCardPath,
                characterContentId,
                loreBookPath,
                loreContentId);
            return ImportStatus(imports);
        }
        catch (Exception exception) when (IsExpectedAuthoringFailure(exception))
        {
            return FailureStatus(exception);
        }
    }

    public GodotDictionary build_world_package_file(
        string sourceDirectory,
        string packageId,
        string contentVersion,
        string destinationPath)
    {
        try
        {
            var sourcePath = RequiredFullPath(
                sourceDirectory,
                nameof(sourceDirectory));
            var source = ReadSource(
                sourcePath,
                packageId,
                contentVersion);
            var compilation = new NativeWorldPackageCompiler(
                    limits: AuthoringLimits)
                .Compile(source);
            var status = CompilationStatus(source, compilation);
            if (!compilation.Succeeded)
            {
                return status;
            }

            using var stream = new MemoryStream();
            WorldPackageArchive.Write(
                stream,
                source,
                AuthoringLimits);
            var destination = RequiredFullPath(
                destinationPath,
                nameof(destinationPath));
            ValidatePackageDestination(sourcePath, destination);
            WriteAtomic(destination, stream.ToArray());
            status["output_path"] = destination;
            status["archive_bytes"] = checked((long)stream.Length);
            return status;
        }
        catch (Exception exception) when (IsExpectedAuthoringFailure(exception))
        {
            return FailureStatus(exception);
        }
    }

    public GodotDictionary build_bound_world_package_file(
        string sourceDirectory,
        string packageId,
        string contentVersion,
        string destinationPath,
        string characterCardPath,
        string characterContentId,
        string loreBookPath,
        string loreContentId,
        string agentId,
        bool acceptImportsAsUntrustedData)
    {
        try
        {
            var sourcePath = RequiredFullPath(
                sourceDirectory,
                nameof(sourceDirectory));
            var nativeSource = ReadSource(
                sourcePath,
                packageId,
                contentVersion);
            var imports = ReadImports(
                characterCardPath,
                characterContentId,
                loreBookPath,
                loreContentId);
            if (!imports.Succeeded)
            {
                return ImportStatus(imports);
            }

            if (imports.HasContent
                && !acceptImportsAsUntrustedData)
            {
                return SingleDiagnosticStatus(
                    "import_acceptance_required",
                    "error",
                    "$imports/acceptance",
                    "Imported content must be explicitly accepted as "
                    + "untrusted data before packaging.",
                    "Imported content was not packaged.");
            }

            var composedFiles = new List<WorldPackageFile>(
                BoundAuthoringLimits.MaxFiles);
            composedFiles.AddRange(nativeSource.Files);
            if (imports.HasContent)
            {
                var composer = new NativeWorldImportComposer(
                    packageId,
                    contentVersion);
                if (imports.Character is not null)
                {
                    composer.AddCharacter(
                        imports.CharacterContentId!,
                        imports.Character,
                        ImportedContentAcceptance.AcceptAsUntrustedData);
                }

                if (imports.LoreBook is not null)
                {
                    composer.AddLoreBook(
                        imports.LoreContentId!,
                        imports.LoreBook,
                        ImportedContentAcceptance.AcceptAsUntrustedData);
                }

                composer.AddAgentBinding(
                    RequiredPortableId(agentId, nameof(agentId)),
                    imports.CharacterContentId,
                    imports.LoreBook is null
                        ? Array.Empty<string>()
                        : new[] { imports.LoreContentId! },
                    ImportedContentAcceptance.AcceptAsUntrustedData);
                composedFiles.AddRange(composer.Build().Files);
            }

            var composed = new WorldPackageDefinition(
                packageId,
                contentVersion,
                composedFiles,
                nativeSource.RequiredExtensions,
                nativeSource.ExtensionData);
            var compilation = new NativeWorldPackageCompiler(
                    limits: BoundAuthoringLimits)
                .Compile(composed);
            var status = CompilationStatus(composed, compilation);
            AppendImportDiagnostics(status, imports);
            if (!compilation.Succeeded)
            {
                return status;
            }

            using var stream = new MemoryStream();
            WorldPackageArchive.Write(
                stream,
                composed,
                BoundAuthoringLimits);
            stream.Position = 0;
            var restored = WorldPackageArchive.Read(
                stream,
                BoundAuthoringLimits);
            var restoredImports =
                new ImportedWorldPackageContentReader(
                        maxImportedFiles: 5,
                        maxImportedFileBytes: 4L * 1_048_576,
                        maxImportedBytes: 16L * 1_048_576)
                    .Read(restored);
            if (imports.HasContent)
            {
                var binding = restoredImports.AgentBindings[
                    RequiredPortableId(agentId, nameof(agentId))];
                if (imports.Character is not null
                    && binding.CharacterContentId is null)
                {
                    throw new InvalidOperationException(
                        "The bound character did not survive package "
                        + "rehydration.");
                }
            }

            var destination = RequiredFullPath(
                destinationPath,
                nameof(destinationPath));
            ValidatePackageDestination(sourcePath, destination);
            WriteAtomic(destination, stream.ToArray());
            status["output_path"] = destination;
            status["archive_bytes"] = checked((long)stream.Length);
            status["imported_character_count"] =
                restoredImports.Characters.Count;
            status["imported_lore_count"] =
                restoredImports.LoreBooks.Count;
            status["agent_binding_count"] =
                restoredImports.AgentBindings.Count;
            status["message"] = imports.HasContent
                ? "Bound native-world package build succeeded."
                : "Native-world package build succeeded.";
            return status;
        }
        catch (Exception exception) when (IsExpectedAuthoringFailure(exception))
        {
            return FailureStatus(exception);
        }
    }

    public GodotDictionary create_starter_world(string targetDirectory)
    {
        try
        {
            var target = RequiredFullPath(
                targetDirectory,
                nameof(targetDirectory));
            if (File.Exists(target))
            {
                throw new IOException(
                    "The starter target is an existing file.");
            }

            var targetExisted = Directory.Exists(target);
            if (Directory.Exists(target))
            {
                RejectReparsePoint(target);
                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    throw new IOException(
                        "The starter target directory must be empty.");
                }
            }

            var parent = Path.GetDirectoryName(target)
                         ?? throw new IOException(
                             "The starter target has no parent directory.");
            Directory.CreateDirectory(parent);
            RejectReparsePathChain(parent);
            var staging = target
                          + ".starter."
                          + Guid.NewGuid().ToString("N")
                          + ".tmp";
            var published = false;
            try
            {
                Directory.CreateDirectory(staging);
                RejectReparsePoint(staging);
                foreach (var file in StarterFiles)
                {
                    var destination = Path.GetFullPath(
                        Path.Combine(staging, file.Key));
                    EnsureDirectChild(staging, destination);
                    using var stream = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        System.IO.FileAccess.Write,
                        FileShare.None,
                        bufferSize: 16_384,
                        FileOptions.WriteThrough);
                    var bytes = new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false)
                        .GetBytes(
                            file.Value + System.Environment.NewLine);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (targetExisted)
                {
                    if (Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        throw new IOException(
                            "The starter target changed while it was "
                            + "being prepared.");
                    }

                    Directory.Delete(target, recursive: false);
                }

                try
                {
                    Directory.Move(staging, target);
                    published = true;
                }
                catch
                {
                    if (targetExisted
                        && !Directory.Exists(target)
                        && !File.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                    }

                    throw;
                }
            }
            finally
            {
                if (!published && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }

            return new GodotDictionary
            {
                ["success"] = true,
                ["source_path"] = target,
                ["file_count"] = StarterFiles.Count,
                ["message"] =
                    "Created a native-world starter. Edit the JSON, then "
                    + "validate it before building."
            };
        }
        catch (Exception exception) when (IsExpectedAuthoringFailure(exception))
        {
            return FailureStatus(exception);
        }
    }

    private static WorldPackageDefinition ReadSource(
        string sourceDirectory,
        string packageId,
        string contentVersion)
    {
        var source = RequiredFullPath(
            sourceDirectory,
            nameof(sourceDirectory));
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                "The native-world source directory does not exist.");
        }

        RejectReparsePathChain(source);
        var expectedFiles = new HashSet<string>(
            AuthoredFiles,
            StringComparer.Ordinal);
        var sourceEntries = Directory
            .EnumerateFileSystemEntries(source)
            .Take(AuthoredFiles.Length + 2)
            .ToArray();
        if (sourceEntries.Length > AuthoredFiles.Length)
        {
            throw new IOException(
                "The native-world source contains more than seven "
                + "contract files.");
        }

        foreach (var entry in sourceEntries)
        {
            RejectReparsePoint(entry);
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry)
                || !expectedFiles.Contains(name))
            {
                throw new IOException(
                    "The native-world source contains an unknown entry: "
                    + name);
            }
        }

        var files = new List<WorldPackageFile>(AuthoredFiles.Length);
        long expandedBytes = 0;
        foreach (var fileName in AuthoredFiles)
        {
            var path = Path.GetFullPath(Path.Combine(source, fileName));
            EnsureDirectChild(source, path);
            if (!File.Exists(path))
            {
                continue;
            }

            RejectReparsePoint(path);
            var maximumRemaining =
                AuthoringLimits.MaxExpandedBytes - expandedBytes;
            if (maximumRemaining < 0)
            {
                throw new IOException(
                    "Native-world authored files exceed package limits.");
            }

            var content = ReadBoundedFile(
                path,
                Math.Min(
                    AuthoringLimits.MaxFileBytes,
                    maximumRemaining));
            expandedBytes += content.LongLength;
            files.Add(
                new WorldPackageFile(
                    fileName,
                    "application/json",
                    content));
        }

        return new WorldPackageDefinition(
            packageId,
            contentVersion,
            files);
    }

    private static ImportedAuthoringInputs ReadImports(
        string characterCardPath,
        string characterContentId,
        string loreBookPath,
        string loreContentId)
    {
        var importer = new CompatibilityImporter(ImportOptions);
        CompatibilityImportResult<CharacterDefinition>? character = null;
        CompatibilityImportResult<LoreBookDefinition>? loreBook = null;
        string? admittedCharacterId = null;
        string? admittedLoreId = null;

        if (!string.IsNullOrWhiteSpace(characterCardPath))
        {
            admittedCharacterId = RequiredPortableId(
                characterContentId,
                nameof(characterContentId));
            var path = RequireImportFile(
                characterCardPath,
                nameof(characterCardPath),
                ".json",
                ".png");
            var bytes = ReadBoundedImportFile(path);
            character = string.Equals(
                Path.GetExtension(path),
                ".png",
                StringComparison.OrdinalIgnoreCase)
                ? importer.ImportCharacterCardPng(bytes)
                : importer.ImportCharacterCardJson(bytes);
        }
        else if (!string.IsNullOrWhiteSpace(characterContentId))
        {
            throw new ArgumentException(
                "Character content ID requires a character-card path.",
                nameof(characterContentId));
        }

        if (!string.IsNullOrWhiteSpace(loreBookPath))
        {
            admittedLoreId = RequiredPortableId(
                loreContentId,
                nameof(loreContentId));
            var path = RequireImportFile(
                loreBookPath,
                nameof(loreBookPath),
                ".json");
            loreBook = importer.ImportLoreBookJson(
                ReadBoundedImportFile(path));
        }
        else if (!string.IsNullOrWhiteSpace(loreContentId))
        {
            throw new ArgumentException(
                "Lore content ID requires a lore-book path.",
                nameof(loreContentId));
        }

        return new ImportedAuthoringInputs(
            character,
            admittedCharacterId,
            loreBook,
            admittedLoreId);
    }

    private static GodotDictionary ImportStatus(
        ImportedAuthoringInputs imports)
    {
        var diagnostics = new GodotArray();
        AddImportDiagnostics(diagnostics, "character", imports.Character);
        AddImportDiagnostics(diagnostics, "lore", imports.LoreBook);
        return new GodotDictionary
        {
            ["success"] = imports.Succeeded,
            ["has_imports"] = imports.HasContent,
            ["has_warnings"] = imports.HasWarnings,
            ["character_bound"] = imports.Character is not null,
            ["lore_bound"] = imports.LoreBook is not null,
            ["character_content_id"] =
                imports.CharacterContentId ?? string.Empty,
            ["lore_content_id"] =
                imports.LoreContentId ?? string.Empty,
            ["diagnostics"] = diagnostics,
            ["message"] = !imports.HasContent
                ? "No imported content is bound."
                : imports.Succeeded
                    ? "Imported content validation succeeded."
                    : "Imported content validation failed."
        };
    }

    private static void AppendImportDiagnostics(
        GodotDictionary status,
        ImportedAuthoringInputs imports)
    {
        var diagnostics = status["diagnostics"].AsGodotArray();
        AddImportDiagnostics(diagnostics, "character", imports.Character);
        AddImportDiagnostics(diagnostics, "lore", imports.LoreBook);
        status["has_import_warnings"] = imports.HasWarnings;
        status["character_bound"] = imports.Character is not null;
        status["lore_bound"] = imports.LoreBook is not null;
    }

    private static void AddImportDiagnostics<T>(
        GodotArray target,
        string kind,
        CompatibilityImportResult<T>? import)
        where T : class
    {
        if (import is null)
        {
            return;
        }

        const int maximumDiagnostics = 256;
        var count = 0;
        foreach (var diagnostic in import.Diagnostics)
        {
            if (count == maximumDiagnostics)
            {
                target.Add(
                    new GodotDictionary
                    {
                        ["code"] = "import_diagnostics_truncated",
                        ["severity"] = "warning",
                        ["path"] = "$imports/" + kind,
                        ["message"] =
                            "Additional import diagnostics were omitted."
                    });
                return;
            }

            target.Add(
                new GodotDictionary
                {
                    ["code"] = "import." + diagnostic.Code,
                    ["severity"] =
                        diagnostic.Severity.ToString().ToLowerInvariant(),
                    ["path"] = "$imports/" + kind + diagnostic.Path,
                    ["message"] = BoundedDiagnosticText(
                        diagnostic.Message,
                        2_048)
                });
            count++;
        }
    }

    private static GodotDictionary SingleDiagnosticStatus(
        string code,
        string severity,
        string path,
        string diagnosticMessage,
        string message)
    {
        return new GodotDictionary
        {
            ["success"] = false,
            ["diagnostics"] = new GodotArray
            {
                new GodotDictionary
                {
                    ["code"] = code,
                    ["severity"] = severity,
                    ["path"] = path,
                    ["message"] = diagnosticMessage
                }
            },
            ["message"] = message
        };
    }

    private static GodotDictionary CompilationStatus(
        WorldPackageDefinition source,
        NativeWorldPackageCompilation compilation)
    {
        var diagnostics = new GodotArray();
        const int maximumDiagnostics = 256;
        foreach (var diagnostic in compilation.Diagnostics.Take(
                     maximumDiagnostics))
        {
            diagnostics.Add(
                new GodotDictionary
                {
                    ["code"] = BoundedDiagnosticText(
                        diagnostic.Code,
                        120),
                    ["severity"] =
                        diagnostic.Severity.ToString().ToLowerInvariant(),
                    ["path"] = BoundedDiagnosticText(
                        diagnostic.Path,
                        512),
                    ["message"] = BoundedDiagnosticText(
                        diagnostic.Message,
                        2_048)
                });
        }

        if (compilation.Diagnostics.Count > maximumDiagnostics)
        {
            diagnostics.Add(
                new GodotDictionary
                {
                    ["code"] = "world_diagnostics_truncated",
                    ["severity"] = "warning",
                    ["path"] = "$package",
                    ["message"] =
                        "Additional world diagnostics were omitted."
                });
        }

        return new GodotDictionary
        {
            ["success"] = compilation.Succeeded,
            ["package_id"] = source.PackageId,
            ["content_version"] = source.ContentVersion,
            ["package_digest"] = source.PackageDigest,
            ["catalog_digest"] =
                compilation.Package?.CatalogDigest ?? string.Empty,
            ["world_id"] =
                compilation.Package?.World.WorldId ?? string.Empty,
            ["file_count"] = source.Files.Count,
            ["diagnostics"] = diagnostics,
            ["message"] = compilation.Succeeded
                ? "Native-world validation succeeded."
                : "Native-world validation failed."
        };
    }

    private static GodotDictionary FailureStatus(Exception exception)
    {
        var code = exception switch
        {
            ImportedWorldPackageContentException imported =>
                imported.ReasonCode,
            WorldDataContractException world => world.ReasonCode,
            UnauthorizedAccessException =>
                "world_authoring_access_denied",
            IOException => "world_authoring_filesystem_invalid",
            _ => "world_authoring_input_invalid"
        };
        const string diagnosticMessage =
            "Authoring input was rejected by a bounded validation rule.";
        return SingleDiagnosticStatus(
            code,
            "error",
            "$authoring",
            diagnosticMessage,
            "Authoring validation failed.");
    }

    private static string BoundedDiagnosticText(
        string value,
        int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        return value.Substring(0, maximumCharacters);
    }

    private static string RequiredFullPath(
        string path,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A filesystem path is required.",
                parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static string RequireImportFile(
        string path,
        string parameterName,
        params string[] allowedExtensions)
    {
        var fullPath = RequiredFullPath(path, parameterName);
        if (!File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "An imported-content path is not a regular file.");
        }

        RejectReparsePathChain(fullPath);
        var extension = Path.GetExtension(fullPath);
        if (!allowedExtensions.Any(
                allowed => string.Equals(
                    extension,
                    allowed,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException(
                "An imported-content file has an unsupported extension.");
        }

        return fullPath;
    }

    private static string RequiredPortableId(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException(
                "A bounded non-empty identifier is required.",
                parameterName);
        }

        if (value is "." or ".."
            || value.Any(
                character => character is not (
                    >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_'
                    or '.')))
        {
            throw new ArgumentException(
                "The identifier contains a non-portable character.",
                parameterName);
        }

        return value;
    }

    private static byte[] ReadBoundedImportFile(string path)
    {
        const long maximumBytes = 4L * 1_048_576;
        var bytes = ReadBoundedFile(path, maximumBytes);
        RejectReparsePathChain(path);
        return bytes;
    }

    private static byte[] ReadBoundedFile(
        string path,
        long maximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new IOException(
                "Native-world authored files exceed package limits.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "An authored file changed while it was being read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException(
                "An authored file changed while it was being read.");
        }

        return bytes;
    }

    private static void ValidatePackageDestination(
        string source,
        string destination)
    {
        if (!string.Equals(
                Path.GetExtension(destination),
                ".gaworld",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "Native-world package output must use the .gaworld "
                + "extension.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedSource = Path.GetFullPath(source).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedDestination = Path.GetFullPath(destination);
        var prefix = normalizedSource + Path.DirectorySeparatorChar;
        if (string.Equals(
                normalizedSource,
                normalizedDestination,
                comparison)
            || normalizedDestination.StartsWith(prefix, comparison))
        {
            throw new IOException(
                "Package output must be outside the authored source "
                + "directory.");
        }

        var parent = Path.GetDirectoryName(normalizedDestination)
                     ?? throw new IOException(
                         "The package destination has no parent directory.");
        RejectReparsePathChain(parent);
    }

    private static void EnsureDirectChild(string parent, string child)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var expected = normalizedParent + Path.DirectorySeparatorChar;
        if (!child.StartsWith(
                expected,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !string.Equals(
                Path.GetDirectoryName(child),
                normalizedParent,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new IOException(
                "An authored file escaped its source directory.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Native-world authoring paths cannot be filesystem links.");
        }
    }

    private static void RejectReparsePathChain(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(
                    parent,
                    current,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return;
            }

            current = parent;
        }
    }

    private static void WriteAtomic(string destination, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new IOException(
                         "The package destination has no parent directory.");
        RejectReparsePathChain(parent);
        Directory.CreateDirectory(parent);
        RejectReparsePathChain(parent);
        if (File.Exists(destination))
        {
            RejectReparsePoint(destination);
        }

        var temporary = destination
                        + "."
                        + Guid.NewGuid().ToString("N")
                        + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       System.IO.FileAccess.Write,
                       FileShare.None,
                       bufferSize: 65_536,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Replace(
                    temporary,
                    destination,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // The admitted destination is already unchanged or published.
            }
        }
    }

    private static bool IsExpectedAuthoringFailure(Exception exception)
    {
        return exception is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or WorldDataContractException
            or ImportedWorldPackageContentException;
    }

    private sealed class ImportedAuthoringInputs
    {
        public ImportedAuthoringInputs(
            CompatibilityImportResult<CharacterDefinition>? character,
            string? characterContentId,
            CompatibilityImportResult<LoreBookDefinition>? loreBook,
            string? loreContentId)
        {
            Character = character;
            CharacterContentId = characterContentId;
            LoreBook = loreBook;
            LoreContentId = loreContentId;
        }

        public CompatibilityImportResult<CharacterDefinition>? Character
        {
            get;
        }

        public string? CharacterContentId { get; }

        public CompatibilityImportResult<LoreBookDefinition>? LoreBook
        {
            get;
        }

        public string? LoreContentId { get; }

        public bool HasContent => Character is not null || LoreBook is not null;

        public bool Succeeded =>
            (Character is null || Character.Success)
            && (LoreBook is null || LoreBook.Success);

        public bool HasWarnings =>
            HasSeverity(
                Character,
                CompatibilityDiagnosticSeverity.Warning)
            || HasSeverity(
                LoreBook,
                CompatibilityDiagnosticSeverity.Warning);

        private static bool HasSeverity<T>(
            CompatibilityImportResult<T>? import,
            CompatibilityDiagnosticSeverity severity)
            where T : class
        {
            return import?.Diagnostics.Any(
                diagnostic => diagnostic.Severity == severity) == true;
        }
    }
}
