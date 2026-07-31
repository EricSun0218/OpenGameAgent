using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Compatibility;
using GameAgent.World;

namespace GameAgent.Runtime;

public static class ImportedWorldPackageContentReasonCodes
{
    public const string InvalidShape = "imported_package_content_invalid_shape";

    public const string UnknownFile = "imported_package_content_unknown_file";

    public const string MediaTypeMismatch =
        "imported_package_content_media_type_mismatch";

    public const string DuplicateContent =
        "imported_package_content_duplicate";

    public const string MissingDiagnostics =
        "imported_package_content_missing_diagnostics";

    public const string InvalidReference =
        "imported_package_content_invalid_reference";

    public const string LimitExceeded =
        "imported_package_content_limit_exceeded";

    public const string NormalizedContentDigestMismatch =
        "imported_package_content_normalized_digest_mismatch";
}

public sealed class ImportedWorldPackageContentException : Exception
{
    internal ImportedWorldPackageContentException(
        string reasonCode,
        string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// An inert package binding. It references imported data only and never grants
/// tools, skills, credentials, providers, or extension authority.
/// </summary>
public sealed class ImportedAgentContentBinding
{
    private ImportedAgentContentBinding(
        string agentId,
        string? characterContentId,
        IReadOnlyList<string> loreContentIds)
    {
        AgentId = agentId;
        CharacterContentId = characterContentId;
        LoreContentIds = loreContentIds;
    }

    public string AgentId { get; }

    public string? CharacterContentId { get; }

    public IReadOnlyList<string> LoreContentIds { get; }

    internal static ImportedAgentContentBinding Create(
        string? agentId,
        string? characterContentId,
        IEnumerable<string>? loreContentIds)
    {
        var admittedAgentId = ImportedWorldPackageContentCodec.PortableId(
            agentId,
            nameof(agentId));
        var admittedCharacterId = characterContentId is null
            ? null
            : ImportedWorldPackageContentCodec.PortableId(
                characterContentId,
                nameof(characterContentId));
        var lore = (loreContentIds ?? Array.Empty<string>())
            .Select(
                value => ImportedWorldPackageContentCodec.PortableId(
                    value,
                    nameof(loreContentIds)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < lore.Length; index++)
        {
            if (string.Equals(
                    lore[index - 1],
                    lore[index],
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Lore content identifiers must be unique.",
                    nameof(loreContentIds));
            }
        }

        if (admittedCharacterId is null && lore.Length == 0)
        {
            throw new ArgumentException(
                "An imported agent binding must reference character or "
                + "lore content.",
                nameof(characterContentId));
        }

        return new ImportedAgentContentBinding(
            admittedAgentId,
            admittedCharacterId,
            new ReadOnlyCollection<string>(lore));
    }
}

/// <summary>
/// Strictly rehydrated inert imports from a native package archive.
/// </summary>
public sealed class ImportedWorldPackageContent
{
    internal ImportedWorldPackageContent(
        IReadOnlyDictionary<
            string,
            CompatibilityImportResult<CharacterDefinition>> characters,
        IReadOnlyDictionary<
            string,
            CompatibilityImportResult<LoreBookDefinition>> loreBooks,
        IReadOnlyDictionary<string, ImportedAgentContentBinding> bindings)
    {
        Characters = characters;
        LoreBooks = loreBooks;
        AgentBindings = bindings;
    }

    public IReadOnlyDictionary<
        string,
        CompatibilityImportResult<CharacterDefinition>> Characters
    { get; }

    public IReadOnlyDictionary<
        string,
        CompatibilityImportResult<LoreBookDefinition>> LoreBooks
    { get; }

    public IReadOnlyDictionary<string, ImportedAgentContentBinding>
        AgentBindings
    { get; }
}

/// <summary>
/// Reads only the exact v1 imported-content contracts emitted by
/// <see cref="NativeWorldImportComposer"/>. Native world files remain outside
/// this reader's scope.
/// </summary>
public sealed class ImportedWorldPackageContentReader
{
    private const int HardMaximumImportedFiles = 128;
    private const long HardMaximumImportedFileBytes = 4L * 1_048_576;
    private const long HardMaximumImportedBytes = 16L * 1_048_576;

    private readonly int _maxImportedFiles;
    private readonly long _maxImportedFileBytes;
    private readonly long _maxImportedBytes;

    public ImportedWorldPackageContentReader(
        int maxImportedFiles = 32,
        long maxImportedFileBytes = HardMaximumImportedFileBytes,
        long maxImportedBytes = HardMaximumImportedBytes)
    {
        if (maxImportedFiles is < 1 or > HardMaximumImportedFiles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxImportedFiles));
        }

        if (maxImportedFileBytes is < 1
            or > HardMaximumImportedFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxImportedFileBytes));
        }

        if (maxImportedBytes < maxImportedFileBytes
            || maxImportedBytes > HardMaximumImportedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxImportedBytes));
        }

        _maxImportedFiles = maxImportedFiles;
        _maxImportedFileBytes = maxImportedFileBytes;
        _maxImportedBytes = maxImportedBytes;
    }

    public ImportedWorldPackageContent Read(
        WorldPackageDefinition package)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        try
        {
            return ReadCore(package);
        }
        catch (ImportedWorldPackageContentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.InvalidShape,
                "Imported package content contains an invalid bounded "
                + "value.");
        }
    }

    private ImportedWorldPackageContent ReadCore(
        WorldPackageDefinition package)
    {
        var characters =
            new Dictionary<string, CharacterDefinition>(
                StringComparer.Ordinal);
        var loreBooks =
            new Dictionary<string, LoreBookDefinition>(
                StringComparer.Ordinal);
        var diagnostics =
            new Dictionary<string, DiagnosticEnvelope>(
                StringComparer.Ordinal);
        var bindings =
            new Dictionary<string, ImportedAgentContentBinding>(
                StringComparer.Ordinal);
        var importedFileCount = 0;
        long importedBytes = 0;

        foreach (var file in package.Files)
        {
            var kind = ImportedWorldPackageContentCodec.FileKind(file.Path);
            if (kind == ImportedContentFileKind.None)
            {
                if (ImportedWorldPackageContentCodec.IsImportedMediaType(
                        file.MediaType))
                {
                    throw Error(
                        ImportedWorldPackageContentReasonCodes.UnknownFile,
                        "Imported content uses an unrecognized package "
                        + "path.");
                }

                continue;
            }

            importedFileCount++;
            importedBytes = checked(importedBytes + file.Length);
            if (importedFileCount > _maxImportedFiles
                || file.Length > _maxImportedFileBytes
                || importedBytes > _maxImportedBytes)
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes.LimitExceeded,
                    "Imported package content exceeds the reader limits.");
            }

            switch (kind)
            {
                case ImportedContentFileKind.Character:
                    {
                        RequireMediaType(
                            file,
                            ImportedWorldPackageContentCodec
                                .CharacterMediaType);
                        var decoded =
                            ImportedWorldPackageContentCodec.ReadCharacter(file);
                        AddUnique(
                            characters,
                            decoded.ContentId,
                            decoded.Value);
                        break;
                    }
                case ImportedContentFileKind.Knowledge:
                    {
                        RequireMediaType(
                            file,
                            ImportedWorldPackageContentCodec
                                .KnowledgeMediaType);
                        var decoded =
                            ImportedWorldPackageContentCodec.ReadLoreBook(file);
                        AddUnique(
                            loreBooks,
                            decoded.ContentId,
                            decoded.Value);
                        break;
                    }
                case ImportedContentFileKind.Diagnostics:
                    {
                        RequireMediaType(
                            file,
                            ImportedWorldPackageContentCodec
                                .DiagnosticsMediaType);
                        var key = ImportedWorldPackageContentCodec
                            .DiagnosticsKeyFromPath(file.Path);
                        AddUnique(
                            diagnostics,
                            key,
                            ImportedWorldPackageContentCodec.ReadDiagnostics(
                                file));
                        break;
                    }
                case ImportedContentFileKind.AgentBinding:
                    {
                        RequireMediaType(
                            file,
                            ImportedWorldPackageContentCodec
                                .AgentBindingMediaType);
                        var binding =
                            ImportedWorldPackageContentCodec.ReadAgentBinding(
                                file);
                        AddUnique(bindings, binding.AgentId, binding);
                        break;
                    }
                default:
                    throw Error(
                        ImportedWorldPackageContentReasonCodes.UnknownFile,
                        "Imported package content uses an unknown shape.");
            }
        }

        var admittedCharacters =
            new Dictionary<
                string,
                CompatibilityImportResult<CharacterDefinition>>(
                StringComparer.Ordinal);
        foreach (var pair in characters)
        {
            var key = "character-" + pair.Key;
            if (!diagnostics.Remove(key, out var metadata))
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes
                        .MissingDiagnostics,
                    "Imported character content has no matching "
                    + "diagnostics.");
            }

            RequireNormalizedContentDigest(
                metadata,
                ImportedWorldPackageContentCodec
                    .ComputeNormalizedCharacterDigest(
                        pair.Key,
                        pair.Value));
            admittedCharacters.Add(
                pair.Key,
                CreateImportResult(pair.Value, metadata));
        }

        var admittedLore =
            new Dictionary<
                string,
                CompatibilityImportResult<LoreBookDefinition>>(
                StringComparer.Ordinal);
        foreach (var pair in loreBooks)
        {
            var key = "knowledge-" + pair.Key;
            if (!diagnostics.Remove(key, out var metadata))
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes
                        .MissingDiagnostics,
                    "Imported lore content has no matching diagnostics.");
            }

            RequireNormalizedContentDigest(
                metadata,
                ImportedWorldPackageContentCodec
                    .ComputeNormalizedLoreBookDigest(
                        pair.Key,
                        pair.Value));
            admittedLore.Add(
                pair.Key,
                CreateImportResult(pair.Value, metadata));
        }

        if (diagnostics.Count != 0)
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.InvalidReference,
                "Imported diagnostics do not reference admitted content.");
        }

        foreach (var binding in bindings.Values)
        {
            if (binding.CharacterContentId is not null
                && !admittedCharacters.ContainsKey(
                    binding.CharacterContentId))
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes.InvalidReference,
                    "An imported agent binding references missing "
                    + "character content.");
            }

            if (binding.LoreContentIds.Any(
                    contentId => !admittedLore.ContainsKey(contentId)))
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes.InvalidReference,
                    "An imported agent binding references missing lore "
                    + "content.");
            }
        }

        return new ImportedWorldPackageContent(
            ReadOnly(admittedCharacters),
            ReadOnly(admittedLore),
            ReadOnly(bindings));
    }

    private static void RequireNormalizedContentDigest(
        DiagnosticEnvelope metadata,
        string actualDigest)
    {
        if (FixedTimeDigestEquals(
                metadata.NormalizedContentDigest,
                actualDigest))
        {
            return;
        }

        throw Error(
            ImportedWorldPackageContentReasonCodes
                .NormalizedContentDigestMismatch,
            "Imported package content does not match its normalized "
            + "content digest.");
    }

    private static bool FixedTimeDigestEquals(
        string expected,
        string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(
            expectedBytes,
            actualBytes);
    }

    private static CompatibilityImportResult<T> CreateImportResult<T>(
        T value,
        DiagnosticEnvelope metadata)
        where T : class
    {
        var result = new CompatibilityImportResult<T>(
            value,
            metadata.Diagnostics);
        if (metadata.AdapterId is not null)
        {
            result.WithSourceMetadata(
                metadata.AdapterId,
                metadata.AdapterVersion!,
                metadata.SourceDigest);
        }

        if (!result.Success)
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.InvalidShape,
                "Rehydrated imported content contains an error "
                + "diagnostic.");
        }

        return result;
    }

    private static IReadOnlyDictionary<string, T> ReadOnly<T>(
        IDictionary<string, T> values)
    {
        return new ReadOnlyDictionary<string, T>(
            values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal));
    }

    private static void AddUnique<T>(
        IDictionary<string, T> values,
        string key,
        T value)
    {
        if (!values.TryAdd(key, value))
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.DuplicateContent,
                "Imported package content contains a duplicate "
                + "identifier.");
        }
    }

    private static void RequireMediaType(
        WorldPackageFile file,
        string expected)
    {
        if (!string.Equals(
                file.MediaType,
                expected,
                StringComparison.Ordinal))
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.MediaTypeMismatch,
                "Imported package content has an unexpected media type.");
        }
    }

    private static ImportedWorldPackageContentException Error(
        string reasonCode,
        string message)
    {
        return new ImportedWorldPackageContentException(
            reasonCode,
            message);
    }
}

internal enum ImportedContentFileKind
{
    None = 0,
    Character = 1,
    Knowledge = 2,
    Diagnostics = 3,
    AgentBinding = 4,
}

internal sealed class DiagnosticEnvelope
{
    internal DiagnosticEnvelope(
        string? adapterId,
        string? adapterVersion,
        string? sourceDigest,
        string normalizedContentDigest,
        IReadOnlyList<CompatibilityDiagnostic> diagnostics)
    {
        AdapterId = adapterId;
        AdapterVersion = adapterVersion;
        SourceDigest = sourceDigest;
        NormalizedContentDigest = normalizedContentDigest;
        Diagnostics = diagnostics;
    }

    internal string? AdapterId { get; }

    internal string? AdapterVersion { get; }

    internal string? SourceDigest { get; }

    internal string NormalizedContentDigest { get; }

    internal IReadOnlyList<CompatibilityDiagnostic> Diagnostics { get; }
}

internal sealed class DecodedImportedContent<T>
{
    internal DecodedImportedContent(string contentId, T value)
    {
        ContentId = contentId;
        Value = value;
    }

    internal string ContentId { get; }

    internal T Value { get; }
}

internal static class ImportedWorldPackageContentCodec
{
    internal const string CharacterMediaType =
        "application/vnd.game-agent.imported-character+json";

    internal const string KnowledgeMediaType =
        "application/vnd.game-agent.imported-knowledge+json";

    internal const string DiagnosticsMediaType =
        "application/vnd.game-agent.import-diagnostics+json";

    internal const string AgentBindingMediaType =
        "application/vnd.game-agent.imported-agent-binding+json";

    private const string CharacterContract =
        "game-agent.imported-character.v1";

    private const string KnowledgeContract =
        "game-agent.imported-knowledge.v1";

    private const string DiagnosticsContract =
        "game-agent.import-diagnostics.v2";

    private const string AgentBindingContract =
        "game-agent.imported-agent-binding.v1";

    private const int MaximumJsonNodes = 100_000;
    private const int MaximumContainerItems = 50_000;
    private const int MaximumStringUtf8Bytes = 1_048_576;
    private const int MaximumDiagnostics = 4_096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void WriteCharacter(
        Utf8JsonWriter writer,
        string contentId,
        CharacterDefinition character)
    {
        NativeWorldImportComposer.WriteCharacter(
            writer,
            contentId,
            character);
    }

    internal static void WriteLoreBook(
        Utf8JsonWriter writer,
        string contentId,
        LoreBookDefinition loreBook)
    {
        NativeWorldImportComposer.WriteLoreBook(
            writer,
            contentId,
            loreBook);
    }

    internal static void WriteDiagnostics(
        Utf8JsonWriter writer,
        string? adapterId,
        string? adapterVersion,
        string? sourceDigest,
        string normalizedContentDigest,
        IReadOnlyList<CompatibilityDiagnostic> diagnostics)
    {
        writer.WriteStartObject();
        writer.WriteString("contract", DiagnosticsContract);
        writer.WriteString("contentTrust", "untrusted_data");
        WriteOptional(writer, "adapterId", adapterId);
        WriteOptional(writer, "adapterVersion", adapterVersion);
        WriteOptional(writer, "sourceDigest", sourceDigest);
        writer.WriteString(
            "normalizedContentDigest",
            normalizedContentDigest);
        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString(
                "severity",
                diagnostic.Severity.ToString());
            writer.WriteString("path", diagnostic.Path);
            writer.WriteString("message", diagnostic.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static void WriteAgentBinding(
        Utf8JsonWriter writer,
        ImportedAgentContentBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("contract", AgentBindingContract);
        writer.WriteString("contentTrust", "untrusted_data");
        writer.WriteString("agentId", binding.AgentId);
        WriteOptional(
            writer,
            "characterContentId",
            binding.CharacterContentId);
        writer.WritePropertyName("loreContentIds");
        writer.WriteStartArray();
        foreach (var contentId in binding.LoreContentIds)
        {
            writer.WriteStringValue(contentId);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static string ComputeNormalizedCharacterDigest(
        string contentId,
        CharacterDefinition character)
    {
        return ComputeNormalizedContentDigest(
            writer => WriteCharacter(writer, contentId, character));
    }

    internal static string ComputeNormalizedLoreBookDigest(
        string contentId,
        LoreBookDefinition loreBook)
    {
        return ComputeNormalizedContentDigest(
            writer => WriteLoreBook(writer, contentId, loreBook));
    }

    internal static string ComputeCanonicalJsonDigest(byte[] json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        var nodes = 0;
        ValidateJson(document.RootElement, ref nodes);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(canonical.ToArray());
        var result = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            result.Append(
                value.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static string ComputeNormalizedContentDigest(
        Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        return ComputeCanonicalJsonDigest(output.ToArray());
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    value.GetRawText(),
                    skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Shape();
        }
    }

    internal static ImportedContentFileKind FileKind(string path)
    {
        if (IsJsonChild(path, "content/characters/"))
        {
            return ImportedContentFileKind.Character;
        }

        if (IsJsonChild(path, "content/knowledge/"))
        {
            return ImportedContentFileKind.Knowledge;
        }

        if (IsJsonChild(path, "content/agent-bindings/"))
        {
            return ImportedContentFileKind.AgentBinding;
        }

        if (path.StartsWith("imports/", StringComparison.Ordinal))
        {
            if (!path.EndsWith(
                ".diagnostics.json",
                    StringComparison.Ordinal)
                || path.IndexOf(
                    '/',
                    "imports/".Length) >= 0)
            {
                throw Error(
                    ImportedWorldPackageContentReasonCodes.UnknownFile,
                    "The imports namespace contains an unknown file.");
            }

            return ImportedContentFileKind.Diagnostics;
        }

        if (path.StartsWith(
                "content/characters/",
                StringComparison.Ordinal)
            || path.StartsWith(
                "content/knowledge/",
                StringComparison.Ordinal)
            || path.StartsWith(
                "content/agent-bindings/",
                StringComparison.Ordinal))
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.UnknownFile,
                "An imported-content namespace contains an unknown file.");
        }

        return ImportedContentFileKind.None;
    }

    internal static bool IsImportedMediaType(string mediaType)
    {
        return string.Equals(
                   mediaType,
                   CharacterMediaType,
                   StringComparison.Ordinal)
               || string.Equals(
                   mediaType,
                   KnowledgeMediaType,
                   StringComparison.Ordinal)
               || string.Equals(
                   mediaType,
                   DiagnosticsMediaType,
                   StringComparison.Ordinal)
               || string.Equals(
                   mediaType,
                   AgentBindingMediaType,
                   StringComparison.Ordinal);
    }

    internal static string DiagnosticsKeyFromPath(string path)
    {
        const string prefix = "imports/";
        const string suffix = ".diagnostics.json";
        var key = path.Substring(
            prefix.Length,
            path.Length - prefix.Length - suffix.Length);
        if (!(key.StartsWith("character-", StringComparison.Ordinal)
              || key.StartsWith("knowledge-", StringComparison.Ordinal)))
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.UnknownFile,
                "An imported diagnostics file has an unknown identity.");
        }

        var separator = key.IndexOf('-', StringComparison.Ordinal);
        try
        {
            PortableId(key.Substring(separator + 1), nameof(path));
        }
        catch (ArgumentException)
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.InvalidReference,
                "An imported diagnostics path contains an invalid "
                + "content identity.");
        }

        return key;
    }

    internal static DecodedImportedContent<CharacterDefinition>
        ReadCharacter(WorldPackageFile file)
    {
        using var document = Parse(file);
        var root = document.RootElement;
        ExactObject(
            root,
            "$",
            "contract",
            "contentTrust",
            "contentId",
            "sourceFormat",
            "sourceVersion",
            "identity",
            "authoredContext",
            "provenance",
            "assets",
            "embeddedKnowledge",
            "preservedSourceData");
        RequireLiteral(root, "contract", CharacterContract, "$");
        RequireLiteral(root, "contentTrust", "untrusted_data", "$");
        var contentId = PortableId(
            RequiredString(root, "contentId", "$", 128),
            "contentId");
        RequirePathIdentity(
            file.Path,
            "content/characters/",
            contentId);
        var sourceFormat = RequiredEnum<CompatibilitySourceFormat>(
            root,
            "sourceFormat",
            "$");
        if (sourceFormat is not (
                CompatibilitySourceFormat.CharacterCardV2Json
                or CompatibilitySourceFormat.CharacterCardV3Json
                or CompatibilitySourceFormat.CharacterCardV2Png
                or CompatibilitySourceFormat.CharacterCardV3Png))
        {
            throw Shape();
        }

        var identity = RequiredObject(root, "identity", "$");
        ExactObject(identity, "$.identity", "name", "nickname", "tags");
        var context = RequiredObject(root, "authoredContext", "$");
        ExactObject(
            context,
            "$.authoredContext",
            "description",
            "personality",
            "scenario",
            "firstMessage",
            "exampleMessages",
            "creatorNotes",
            "systemPrompt",
            "postHistoryInstructions",
            "alternateGreetings",
            "groupOnlyGreetings");
        var provenance = RequiredObject(root, "provenance", "$");
        ExactObject(
            provenance,
            "$.provenance",
            "creator",
            "characterVersion",
            "sources",
            "createdAt",
            "modifiedAt",
            "multilingualCreatorNotes");

        var assetsElement = RequiredArray(root, "assets", "$");
        var assets = new List<CharacterAssetReference>();
        foreach (var asset in assetsElement.EnumerateArray())
        {
            ExactObject(
                asset,
                "$.assets[]",
                "type",
                "uri",
                "name",
                "extension",
                "locationKind",
                "preservedSourceData");
            assets.Add(
                new CharacterAssetReference(
                    RequiredString(
                        asset,
                        "type",
                        "$.assets[]",
                        8_192),
                    RequiredString(
                        asset,
                        "uri",
                        "$.assets[]",
                        MaximumStringUtf8Bytes),
                    RequiredString(
                        asset,
                        "name",
                        "$.assets[]",
                        8_192),
                    RequiredString(
                        asset,
                        "extension",
                        "$.assets[]",
                        256),
                    RequiredEnum<CharacterAssetLocationKind>(
                        asset,
                        "locationKind",
                        "$.assets[]"),
                    ReadPreserved(
                        RequiredObject(
                            asset,
                            "preservedSourceData",
                            "$.assets[]"),
                        "$.assets[].preservedSourceData")));
        }

        LoreBookDefinition? embedded = null;
        var embeddedElement = RequiredProperty(
            root,
            "embeddedKnowledge",
            "$");
        if (embeddedElement.ValueKind != JsonValueKind.Null)
        {
            embedded = ReadLoreBookBody(
                embeddedElement,
                "$.embeddedKnowledge");
        }

        var value = new CharacterDefinition(
            sourceFormat,
            RequiredString(root, "sourceVersion", "$", 128),
            RequiredString(identity, "name", "$.identity", 65_536),
            RequiredString(
                context,
                "description",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "personality",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "scenario",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "firstMessage",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "exampleMessages",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "creatorNotes",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "systemPrompt",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            RequiredString(
                context,
                "postHistoryInstructions",
                "$.authoredContext",
                MaximumStringUtf8Bytes),
            StringArray(
                context,
                "alternateGreetings",
                "$.authoredContext"),
            StringArray(
                context,
                "groupOnlyGreetings",
                "$.authoredContext"),
            StringArray(identity, "tags", "$.identity"),
            RequiredString(
                provenance,
                "creator",
                "$.provenance",
                65_536),
            RequiredString(
                provenance,
                "characterVersion",
                "$.provenance",
                8_192),
            NullableString(identity, "nickname", "$.identity", 65_536),
            StringMap(
                RequiredObject(
                    provenance,
                    "multilingualCreatorNotes",
                    "$.provenance"),
                "$.provenance.multilingualCreatorNotes"),
            StringArray(provenance, "sources", "$.provenance"),
            new ReadOnlyCollection<CharacterAssetReference>(
                assets.ToArray()),
            NullableTimestamp(
                provenance,
                "createdAt",
                "$.provenance"),
            NullableTimestamp(
                provenance,
                "modifiedAt",
                "$.provenance"),
            embedded,
            ReadPreserved(
                RequiredObject(root, "preservedSourceData", "$"),
                "$.preservedSourceData"));
        return new DecodedImportedContent<CharacterDefinition>(
            contentId,
            value);
    }

    internal static DecodedImportedContent<LoreBookDefinition>
        ReadLoreBook(WorldPackageFile file)
    {
        using var document = Parse(file);
        var root = document.RootElement;
        ExactObject(
            root,
            "$",
            "contract",
            "contentTrust",
            "contentId",
            "sourceFormat",
            "sourceVersion",
            "name",
            "description",
            "scanDepth",
            "tokenBudget",
            "recursiveScanning",
            "entries",
            "preservedSourceData");
        RequireLiteral(root, "contract", KnowledgeContract, "$");
        RequireLiteral(root, "contentTrust", "untrusted_data", "$");
        var contentId = PortableId(
            RequiredString(root, "contentId", "$", 128),
            "contentId");
        RequirePathIdentity(
            file.Path,
            "content/knowledge/",
            contentId);
        return new DecodedImportedContent<LoreBookDefinition>(
            contentId,
            ReadLoreBookBody(root, "$", validateShape: false));
    }

    internal static DiagnosticEnvelope ReadDiagnostics(
        WorldPackageFile file)
    {
        using var document = Parse(file);
        var root = document.RootElement;
        ExactObject(
            root,
            "$",
            "contract",
            "contentTrust",
            "adapterId",
            "adapterVersion",
            "sourceDigest",
            "normalizedContentDigest",
            "diagnostics");
        RequireLiteral(root, "contract", DiagnosticsContract, "$");
        RequireLiteral(root, "contentTrust", "untrusted_data", "$");
        var adapterId = NullableString(root, "adapterId", "$", 256);
        var adapterVersion = NullableString(
            root,
            "adapterVersion",
            "$",
            128);
        if ((adapterId is null) != (adapterVersion is null))
        {
            throw Shape();
        }

        var sourceDigest = NullableString(
            root,
            "sourceDigest",
            "$",
            64);
        if (sourceDigest is not null && !IsSha256(sourceDigest))
        {
            throw Shape();
        }

        var normalizedContentDigest = RequiredString(
            root,
            "normalizedContentDigest",
            "$",
            64);
        if (!IsSha256(normalizedContentDigest))
        {
            throw Shape();
        }

        var array = RequiredArray(root, "diagnostics", "$");
        if (array.GetArrayLength() > MaximumDiagnostics)
        {
            throw Limit();
        }

        var diagnostics = new List<CompatibilityDiagnostic>(
            array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            ExactObject(
                item,
                "$.diagnostics[]",
                "code",
                "severity",
                "path",
                "message");
            diagnostics.Add(
                new CompatibilityDiagnostic(
                    RequiredNonEmptyString(
                        item,
                        "code",
                        "$.diagnostics[]",
                        120),
                    RequiredEnum<CompatibilityDiagnosticSeverity>(
                        item,
                        "severity",
                        "$.diagnostics[]"),
                    RequiredNonEmptyString(
                        item,
                        "path",
                        "$.diagnostics[]",
                        512),
                    RequiredNonEmptyString(
                        item,
                        "message",
                        "$.diagnostics[]",
                        2_048)));
        }

        return new DiagnosticEnvelope(
            adapterId,
            adapterVersion,
            sourceDigest,
            normalizedContentDigest,
            new ReadOnlyCollection<CompatibilityDiagnostic>(
                diagnostics.ToArray()));
    }

    internal static ImportedAgentContentBinding ReadAgentBinding(
        WorldPackageFile file)
    {
        using var document = Parse(file);
        var root = document.RootElement;
        ExactObject(
            root,
            "$",
            "contract",
            "contentTrust",
            "agentId",
            "characterContentId",
            "loreContentIds");
        RequireLiteral(root, "contract", AgentBindingContract, "$");
        RequireLiteral(root, "contentTrust", "untrusted_data", "$");
        var binding = ImportedAgentContentBinding.Create(
            RequiredString(root, "agentId", "$", 128),
            NullableString(root, "characterContentId", "$", 128),
            StringArray(root, "loreContentIds", "$"));
        RequirePathIdentity(
            file.Path,
            "content/agent-bindings/",
            binding.AgentId);
        return binding;
    }

    internal static string PortableId(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException(
                "A bounded non-empty portable identifier is required.",
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

    private static LoreBookDefinition ReadLoreBookBody(
        JsonElement root,
        string path,
        bool validateShape = true)
    {
        if (validateShape)
        {
            ExactObject(
                root,
                path,
                "sourceFormat",
                "sourceVersion",
                "name",
                "description",
                "scanDepth",
                "tokenBudget",
                "recursiveScanning",
                "entries",
                "preservedSourceData");
        }
        var sourceFormat = RequiredEnum<CompatibilitySourceFormat>(
            root,
            "sourceFormat",
            path);
        if (sourceFormat is not (
                CompatibilitySourceFormat.LoreBookV2Embedded
                or CompatibilitySourceFormat.LoreBookV3Embedded
                or CompatibilitySourceFormat.LoreBookV3Json
                or CompatibilitySourceFormat.LoreBookObjectMapJson))
        {
            throw Shape();
        }

        var entriesElement = RequiredArray(root, "entries", path);
        var entries = new List<LoreBookEntryDefinition>(
            entriesElement.GetArrayLength());
        foreach (var item in entriesElement.EnumerateArray())
        {
            var itemPath = path + ".entries[]";
            ExactObject(
                item,
                itemPath,
                "identifier",
                "identifierKind",
                "name",
                "comment",
                "content",
                "insertionOrder",
                "priority",
                "position",
                "sourcePosition",
                "activation",
                "directives",
                "preservedSourceData");
            var activationElement = RequiredObject(
                item,
                "activation",
                itemPath);
            ExactObject(
                activationElement,
                itemPath + ".activation",
                "alwaysActive",
                "enabled",
                "matchMode",
                "primaryKeys",
                "secondaryKeys",
                "requireSecondaryKey",
                "secondaryKeyLogic",
                "caseSensitive",
                "matchWholeWords",
                "scanDepth",
                "probability",
                "stickyTurns",
                "cooldownTurns",
                "delayTurns");
            var directivesElement = RequiredArray(
                item,
                "directives",
                itemPath);
            var directives = new List<LoreBookDirective>(
                directivesElement.GetArrayLength());
            foreach (var directive in directivesElement.EnumerateArray())
            {
                ExactObject(
                    directive,
                    itemPath + ".directives[]",
                    "name",
                    "value",
                    "isFallback");
                directives.Add(
                    new LoreBookDirective(
                        RequiredString(
                            directive,
                            "name",
                            itemPath + ".directives[]",
                            8_192),
                        RequiredString(
                            directive,
                            "value",
                            itemPath + ".directives[]",
                            MaximumStringUtf8Bytes),
                        RequiredBoolean(
                            directive,
                            "isFallback",
                            itemPath + ".directives[]")));
            }

            entries.Add(
                new LoreBookEntryDefinition(
                    NullableString(item, "identifier", itemPath, 8_192),
                    RequiredEnum<LoreBookEntryIdentifierKind>(
                        item,
                        "identifierKind",
                        itemPath),
                    NullableString(item, "name", itemPath, 65_536),
                    NullableString(item, "comment", itemPath, 65_536),
                    RequiredString(
                        item,
                        "content",
                        itemPath,
                        MaximumStringUtf8Bytes),
                    RequiredInt(item, "insertionOrder", itemPath),
                    NullableDoubleString(item, "priority", itemPath),
                    RequiredEnum<LoreBookPosition>(
                        item,
                        "position",
                        itemPath),
                    RequiredString(
                        item,
                        "sourcePosition",
                        itemPath,
                        8_192),
                    new LoreBookActivationDefinition(
                        RequiredBoolean(
                            activationElement,
                            "alwaysActive",
                            itemPath + ".activation"),
                        RequiredBoolean(
                            activationElement,
                            "enabled",
                            itemPath + ".activation"),
                        RequiredEnum<LoreBookMatchMode>(
                            activationElement,
                            "matchMode",
                            itemPath + ".activation"),
                        StringArray(
                            activationElement,
                            "primaryKeys",
                            itemPath + ".activation"),
                        StringArray(
                            activationElement,
                            "secondaryKeys",
                            itemPath + ".activation"),
                        RequiredBoolean(
                            activationElement,
                            "requireSecondaryKey",
                            itemPath + ".activation"),
                        RequiredEnum<LoreBookSecondaryKeyLogic>(
                            activationElement,
                            "secondaryKeyLogic",
                            itemPath + ".activation"),
                        NullableBoolean(
                            activationElement,
                            "caseSensitive",
                            itemPath + ".activation"),
                        NullableBoolean(
                            activationElement,
                            "matchWholeWords",
                            itemPath + ".activation"),
                        NullableInt(
                            activationElement,
                            "scanDepth",
                            itemPath + ".activation"),
                        NullableDoubleString(
                            activationElement,
                            "probability",
                            itemPath + ".activation"),
                        NullableInt(
                            activationElement,
                            "stickyTurns",
                            itemPath + ".activation"),
                        NullableInt(
                            activationElement,
                            "cooldownTurns",
                            itemPath + ".activation"),
                        NullableInt(
                            activationElement,
                            "delayTurns",
                            itemPath + ".activation")),
                    new ReadOnlyCollection<LoreBookDirective>(
                        directives.ToArray()),
                    ReadPreserved(
                        RequiredObject(
                            item,
                            "preservedSourceData",
                            itemPath),
                        itemPath + ".preservedSourceData")));
        }

        return new LoreBookDefinition(
            sourceFormat,
            RequiredString(root, "sourceVersion", path, 128),
            NullableString(root, "name", path, 65_536),
            NullableString(root, "description", path, 65_536),
            NullableInt(root, "scanDepth", path),
            NullableInt(root, "tokenBudget", path),
            NullableBoolean(root, "recursiveScanning", path),
            new ReadOnlyCollection<LoreBookEntryDefinition>(
                entries.ToArray()),
            ReadPreserved(
                RequiredObject(root, "preservedSourceData", path),
                path + ".preservedSourceData"));
    }

    private static PreservedJsonFields ReadPreserved(
        JsonElement root,
        string path)
    {
        ExactObject(root, path, "root", "object", "extensions");
        return new PreservedJsonFields(
            JsonMap(RequiredObject(root, "root", path), path + ".root"),
            JsonMap(
                RequiredObject(root, "object", path),
                path + ".object"),
            JsonMap(
                RequiredObject(root, "extensions", path),
                path + ".extensions"));
    }

    private static IReadOnlyDictionary<string, JsonElement> JsonMap(
        JsonElement root,
        string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Shape();
        }

        var result = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
            {
                throw Shape();
            }

            EnsureString(property.Name, MaximumStringUtf8Bytes);
        }

        return new ReadOnlyDictionary<string, JsonElement>(result);
    }

    private static IReadOnlyDictionary<string, string> StringMap(
        JsonElement root,
        string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Shape();
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            EnsureString(property.Name, MaximumStringUtf8Bytes);
            if (property.Value.ValueKind != JsonValueKind.String
                || !result.TryAdd(
                    property.Name,
                    property.Value.GetString()!))
            {
                throw Shape();
            }
        }

        _ = path;
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static JsonDocument Parse(WorldPackageFile file)
    {
        try
        {
            var json = StrictUtf8.GetString(file.GetContentCopy());
            var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            var nodes = 0;
            ValidateJson(document.RootElement, ref nodes);
            return document;
        }
        catch (ImportedWorldPackageContentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException
            or JsonException
            or InvalidOperationException
            or OverflowException)
        {
            throw Shape();
        }
    }

    private static void ValidateJson(JsonElement value, ref int nodes)
    {
        nodes++;
        if (nodes > MaximumJsonNodes)
        {
            throw Limit();
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var count = 0;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        count++;
                        if (count > MaximumContainerItems
                            || !names.Add(property.Name))
                        {
                            throw count > MaximumContainerItems
                                ? Limit()
                                : Shape();
                        }

                        EnsureString(
                            property.Name,
                            MaximumStringUtf8Bytes);
                        ValidateJson(property.Value, ref nodes);
                    }

                    break;
                }
            case JsonValueKind.Array:
                {
                    var count = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        count++;
                        if (count > MaximumContainerItems)
                        {
                            throw Limit();
                        }

                        ValidateJson(item, ref nodes);
                    }

                    break;
                }
            case JsonValueKind.String:
                EnsureString(
                    value.GetString()!,
                    MaximumStringUtf8Bytes);
                break;
        }
    }

    private static void ExactObject(
        JsonElement value,
        string path,
        params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Shape();
        }

        var expected = new HashSet<string>(
            properties,
            StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Remove(property.Name))
            {
                throw Shape();
            }
        }

        if (expected.Count != 0)
        {
            throw Shape();
        }

        _ = path;
    }

    private static JsonElement RequiredProperty(
        JsonElement root,
        string propertyName,
        string path)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var value))
        {
            throw Shape();
        }

        _ = path;
        return value;
    }

    private static JsonElement RequiredObject(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Shape();
        }

        return value;
    }

    private static JsonElement RequiredArray(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Shape();
        }

        return value;
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName,
        string path,
        int maximumUtf8Bytes)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Shape();
        }

        var result = value.GetString()!;
        EnsureString(result, maximumUtf8Bytes);
        return result;
    }

    private static string RequiredNonEmptyString(
        JsonElement root,
        string propertyName,
        string path,
        int maximumUtf8Bytes)
    {
        var value = RequiredString(
            root,
            propertyName,
            path,
            maximumUtf8Bytes);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Shape();
        }

        return value;
    }

    private static string? NullableString(
        JsonElement root,
        string propertyName,
        string path,
        int maximumUtf8Bytes)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Shape();
        }

        var result = value.GetString()!;
        EnsureString(result, maximumUtf8Bytes);
        return result;
    }

    private static IReadOnlyList<string> StringArray(
        JsonElement root,
        string propertyName,
        string path)
    {
        var array = RequiredArray(root, propertyName, path);
        var result = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Shape();
            }

            var text = item.GetString()!;
            EnsureString(text, MaximumStringUtf8Bytes);
            result.Add(text);
        }

        return new ReadOnlyCollection<string>(result.ToArray());
    }

    private static int RequiredInt(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static int? NullableInt(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static bool RequiredBoolean(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw Shape();
        }

        return value.GetBoolean();
    }

    private static bool? NullableBoolean(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw Shape();
        }

        return value.GetBoolean();
    }

    private static double? NullableDoubleString(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result)
            || double.IsNaN(result)
            || double.IsInfinity(result))
        {
            throw Shape();
        }

        return result;
    }

    private static DateTimeOffset? NullableTimestamp(
        JsonElement root,
        string propertyName,
        string path)
    {
        var value = RequiredProperty(root, propertyName, path);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                value.GetString(),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static T RequiredEnum<T>(
        JsonElement root,
        string propertyName,
        string path)
        where T : struct
    {
        var text = RequiredString(
            root,
            propertyName,
            path,
            128);
        if (!Enum.TryParse<T>(
                text,
                ignoreCase: false,
                out var result)
            || !Enum.IsDefined(typeof(T), result))
        {
            throw Shape();
        }

        return result;
    }

    private static void RequireLiteral(
        JsonElement root,
        string propertyName,
        string expected,
        string path)
    {
        if (!string.Equals(
                RequiredString(
                    root,
                    propertyName,
                    path,
                    256),
                expected,
                StringComparison.Ordinal))
        {
            throw Shape();
        }
    }

    private static void RequirePathIdentity(
        string path,
        string prefix,
        string contentId)
    {
        if (!string.Equals(
                path,
                prefix + contentId + ".json",
                StringComparison.Ordinal))
        {
            throw Error(
                ImportedWorldPackageContentReasonCodes.InvalidReference,
                "Imported content identity does not match its package "
                + "path.");
        }
    }

    private static bool IsJsonChild(string path, string prefix)
    {
        return path.StartsWith(prefix, StringComparison.Ordinal)
               && path.EndsWith(".json", StringComparison.Ordinal)
               && path.IndexOf('/', prefix.Length) < 0;
    }

    private static void EnsureString(string value, int maximumUtf8Bytes)
    {
        if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw Limit();
        }
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64
               && value.All(
                   character => character is >= '0' and <= '9'
                       or >= 'a' and <= 'f');
    }

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static ImportedWorldPackageContentException Shape()
    {
        return Error(
            ImportedWorldPackageContentReasonCodes.InvalidShape,
            "Imported package content does not match the strict v1 "
            + "contract.");
    }

    private static ImportedWorldPackageContentException Limit()
    {
        return Error(
            ImportedWorldPackageContentReasonCodes.LimitExceeded,
            "Imported package content exceeds the strict codec limits.");
    }

    private static ImportedWorldPackageContentException Error(
        string reasonCode,
        string message)
    {
        return new ImportedWorldPackageContentException(
            reasonCode,
            message);
    }
}
