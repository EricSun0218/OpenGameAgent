using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Compatibility;

public sealed class CompatibilityImporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> CharacterRootFields = new(
        new[] { "spec", "spec_version", "data" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> CharacterDataFields = new(
        new[]
        {
            "name",
            "description",
            "personality",
            "scenario",
            "first_mes",
            "mes_example",
            "creator_notes",
            "system_prompt",
            "post_history_instructions",
            "alternate_greetings",
            "character_book",
            "tags",
            "creator",
            "character_version",
            "extensions",
            "assets",
            "nickname",
            "creator_notes_multilingual",
            "source",
            "group_only_greetings",
            "creation_date",
            "modification_date",
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> LoreBookFields = new(
        new[]
        {
            "name",
            "description",
            "scan_depth",
            "token_budget",
            "recursive_scanning",
            "extensions",
            "entries",
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> StandardLoreEntryFields = new(
        new[]
        {
            "keys",
            "secondary_keys",
            "content",
            "extensions",
            "enabled",
            "insertion_order",
            "case_sensitive",
            "use_regex",
            "constant",
            "name",
            "priority",
            "id",
            "comment",
            "selective",
            "position",
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> ObjectMapLoreEntryFields = new(
        new[]
        {
            "uid",
            "key",
            "keysecondary",
            "name",
            "comment",
            "content",
            "constant",
            "selective",
            "selectiveLogic",
            "order",
            "position",
            "disable",
            "probability",
            "useProbability",
            "scanDepth",
            "caseSensitive",
            "matchWholeWords",
            "sticky",
            "cooldown",
            "delay",
            "extensions",
            "enabled",
        },
        StringComparer.Ordinal);

    private readonly CompatibilityImportOptions _options;

    public CompatibilityImporter(CompatibilityImportOptions? options = null)
    {
        _options = options ?? new CompatibilityImportOptions();
    }

    public CompatibilityImportResult<CharacterDefinition> ImportCharacterCardJson(
        ReadOnlyMemory<byte> utf8Json)
    {
        return AttachSource(
            ImportCharacterCardJsonCore(utf8Json, isPng: false),
            "character-json",
            utf8Json);
    }

    public CompatibilityImportResult<CharacterDefinition> ImportCharacterCardJson(
        string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        var diagnostics = new List<CompatibilityDiagnostic>();
        var bytes = EncodeJsonString(json, diagnostics);
        return bytes is null
            ? Result<CharacterDefinition>(null, diagnostics)
                .WithSourceMetadata(
                    "game-agent.character-json",
                    "1",
                    sourceDigest: null)
            : ImportCharacterCardJson(bytes);
    }

    public CompatibilityImportResult<CharacterDefinition> ImportCharacterCardPng(
        ReadOnlyMemory<byte> png)
    {
        var diagnostics = new List<CompatibilityDiagnostic>();
        var payload = PngCharacterPayloadReader.Read(png, _options, diagnostics);
        if (payload is null || HasErrors(diagnostics))
        {
            return AttachSource(
                Result<CharacterDefinition>(null, diagnostics),
                "character-png",
                png);
        }

        var imported = ImportCharacterCardJsonCore(payload.Json, isPng: true);
        diagnostics.AddRange(imported.Diagnostics);
        if (imported.Value is not null)
        {
            var importedAsVersion3 =
                imported.Value.SourceFormat == CompatibilitySourceFormat.CharacterCardV3Png;
            if (payload.IsVersion3 != importedAsVersion3)
            {
                AddError(
                    diagnostics,
                    "png_payload_format_mismatch",
                    "$.spec",
                    "The PNG payload identifier does not match the embedded character format.");
            }
        }

        return AttachSource(
            Result(
                imported.Success && !HasErrors(diagnostics)
                    ? imported.Value
                    : null,
                diagnostics),
            "character-png",
            png);
    }

    public CompatibilityImportResult<LoreBookDefinition> ImportLoreBookJson(
        ReadOnlyMemory<byte> utf8Json)
    {
        return AttachSource(
            ImportLoreBookJsonCore(utf8Json),
            "knowledge-json",
            utf8Json);
    }

    private CompatibilityImportResult<LoreBookDefinition>
        ImportLoreBookJsonCore(
            ReadOnlyMemory<byte> utf8Json)
    {
        var diagnostics = new List<CompatibilityDiagnostic>();
        using var document = ParseJson(utf8Json, diagnostics);
        if (document is null || HasErrors(diagnostics))
        {
            return Result<LoreBookDefinition>(null, diagnostics);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            AddError(
                diagnostics,
                "invalid_root",
                "$",
                "A lore book must be a JSON object.");
            return Result<LoreBookDefinition>(null, diagnostics);
        }

        LoreBookDefinition? definition;
        if (TryGetString(root, "spec", out var specification))
        {
            if (!string.Equals(specification, "lorebook_v3", StringComparison.Ordinal))
            {
                AddError(
                    diagnostics,
                    "unsupported_format",
                    "$.spec",
                    "The lore book format is not supported.");
                return Result<LoreBookDefinition>(null, diagnostics);
            }

            if (!TryGetObject(root, "data", out var data))
            {
                AddError(
                    diagnostics,
                    "missing_required_field",
                    "$.data",
                    "The required lore book data object is missing or invalid.");
                return Result<LoreBookDefinition>(null, diagnostics);
            }

            var rootUnknown = ExtractUnknown(
                root,
                new HashSet<string>(
                    new[] { "spec", "data" },
                    StringComparer.Ordinal));
            definition = ParseStandardLoreBook(
                data,
                CompatibilitySourceFormat.LoreBookV3Json,
                "3.0",
                rootUnknown,
                "$.data",
                diagnostics);
        }
        else if (root.TryGetProperty("entries", out var entries)
                 && entries.ValueKind == JsonValueKind.Object)
        {
            definition = ParseObjectMapLoreBook(root, diagnostics);
        }
        else if (root.TryGetProperty("entries", out entries)
                 && entries.ValueKind == JsonValueKind.Array)
        {
            AddInfo(
                diagnostics,
                "unenveloped_lore_book",
                "$",
                "An unenveloped lore book object was imported.");
            definition = ParseStandardLoreBook(
                root,
                CompatibilitySourceFormat.LoreBookV3Json,
                "3.0",
                EmptyJsonDictionary(),
                "$",
                diagnostics);
        }
        else
        {
            AddError(
                diagnostics,
                "unsupported_format",
                "$",
                "The lore book format is not supported.");
            definition = null;
        }

        if (definition is not null && !HasErrors(diagnostics))
        {
            AddUntrustedContentDiagnostic(diagnostics);
        }

        return Result(
            definition is not null && !HasErrors(diagnostics) ? definition : null,
            diagnostics);
    }

    public CompatibilityImportResult<LoreBookDefinition> ImportLoreBookJson(
        string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        var diagnostics = new List<CompatibilityDiagnostic>();
        var bytes = EncodeJsonString(json, diagnostics);
        return bytes is null
            ? Result<LoreBookDefinition>(null, diagnostics)
                .WithSourceMetadata(
                    "game-agent.knowledge-json",
                    "1",
                    sourceDigest: null)
            : ImportLoreBookJson(bytes);
    }

    private CompatibilityImportResult<CharacterDefinition> ImportCharacterCardJsonCore(
        ReadOnlyMemory<byte> utf8Json,
        bool isPng)
    {
        var diagnostics = new List<CompatibilityDiagnostic>();
        using var document = ParseJson(utf8Json, diagnostics);
        if (document is null || HasErrors(diagnostics))
        {
            return Result<CharacterDefinition>(null, diagnostics);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            AddError(
                diagnostics,
                "invalid_root",
                "$",
                "A character card must be a JSON object.");
            return Result<CharacterDefinition>(null, diagnostics);
        }

        if (!TryGetString(root, "spec", out var specification))
        {
            AddError(
                diagnostics,
                "missing_required_field",
                "$.spec",
                "The required format identifier is missing or invalid.");
            return Result<CharacterDefinition>(null, diagnostics);
        }

        bool isVersion3;
        CompatibilitySourceFormat sourceFormat;
        string expectedVersion;
        if (string.Equals(specification, "chara_card_v2", StringComparison.Ordinal))
        {
            isVersion3 = false;
            sourceFormat = isPng
                ? CompatibilitySourceFormat.CharacterCardV2Png
                : CompatibilitySourceFormat.CharacterCardV2Json;
            expectedVersion = "2.0";
        }
        else if (string.Equals(specification, "chara_card_v3", StringComparison.Ordinal))
        {
            isVersion3 = true;
            sourceFormat = isPng
                ? CompatibilitySourceFormat.CharacterCardV3Png
                : CompatibilitySourceFormat.CharacterCardV3Json;
            expectedVersion = "3.0";
        }
        else
        {
            AddError(
                diagnostics,
                "unsupported_format",
                "$.spec",
                "The character card format is not supported.");
            return Result<CharacterDefinition>(null, diagnostics);
        }

        var sourceVersion = ReadString(
            root,
            "spec_version",
            "$.spec_version",
            diagnostics,
            required: true,
            defaultValue: expectedVersion);
        if (!string.Equals(sourceVersion, expectedVersion, StringComparison.Ordinal))
        {
            AddWarning(
                diagnostics,
                "unverified_version",
                "$.spec_version",
                "The format version is not the version verified by this importer; compatible fields were preserved.");
        }

        if (!TryGetObject(root, "data", out var data))
        {
            AddError(
                diagnostics,
                "missing_required_field",
                "$.data",
                "The required character data object is missing or invalid.");
            return Result<CharacterDefinition>(null, diagnostics);
        }

        var name = ReadString(data, "name", "$.data.name", diagnostics, true, string.Empty);
        var description = ReadString(
            data,
            "description",
            "$.data.description",
            diagnostics,
            true,
            string.Empty);
        var personality = ReadString(
            data,
            "personality",
            "$.data.personality",
            diagnostics,
            true,
            string.Empty);
        var scenario = ReadString(
            data,
            "scenario",
            "$.data.scenario",
            diagnostics,
            true,
            string.Empty);
        var firstMessage = ReadString(
            data,
            "first_mes",
            "$.data.first_mes",
            diagnostics,
            true,
            string.Empty);
        var exampleMessages = ReadString(
            data,
            "mes_example",
            "$.data.mes_example",
            diagnostics,
            true,
            string.Empty);
        var creatorNotes = ReadString(
            data,
            "creator_notes",
            "$.data.creator_notes",
            diagnostics,
            true,
            string.Empty);
        var systemPrompt = ReadString(
            data,
            "system_prompt",
            "$.data.system_prompt",
            diagnostics,
            true,
            string.Empty);
        var postHistoryInstructions = ReadString(
            data,
            "post_history_instructions",
            "$.data.post_history_instructions",
            diagnostics,
            true,
            string.Empty);
        var alternateGreetings = ReadStringArray(
            data,
            "alternate_greetings",
            "$.data.alternate_greetings",
            diagnostics,
            required: true);
        var groupOnlyGreetings = isVersion3
            ? ReadStringArray(
                data,
                "group_only_greetings",
                "$.data.group_only_greetings",
                diagnostics,
                required: true)
            : EmptyStrings();
        var tags = ReadStringArray(
            data,
            "tags",
            "$.data.tags",
            diagnostics,
            required: true);
        var creator = ReadString(
            data,
            "creator",
            "$.data.creator",
            diagnostics,
            true,
            string.Empty);
        var characterVersion = ReadString(
            data,
            "character_version",
            "$.data.character_version",
            diagnostics,
            true,
            string.Empty);
        var nickname = isVersion3
            ? ReadOptionalString(
                data,
                "nickname",
                "$.data.nickname",
                diagnostics)
            : null;
        var multilingualCreatorNotes = isVersion3
            ? ReadStringDictionary(
                data,
                "creator_notes_multilingual",
                "$.data.creator_notes_multilingual",
                diagnostics)
            : EmptyStringDictionary();
        var sources = isVersion3
            ? ReadStringArray(
                data,
                "source",
                "$.data.source",
                diagnostics,
                required: false)
            : EmptyStrings();
        var assets = isVersion3
            ? ParseAssets(data, diagnostics)
            : EmptyAssets();
        var createdAt = isVersion3
            ? ReadTimestamp(data, "creation_date", "$.data.creation_date", diagnostics)
            : null;
        var modifiedAt = isVersion3
            ? ReadTimestamp(data, "modification_date", "$.data.modification_date", diagnostics)
            : null;

        LoreBookDefinition? characterLoreBook = null;
        if (data.TryGetProperty("character_book", out var loreBookElement)
            && loreBookElement.ValueKind != JsonValueKind.Null)
        {
            if (loreBookElement.ValueKind != JsonValueKind.Object)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    "$.data.character_book",
                    "The character lore book must be an object.");
            }
            else
            {
                characterLoreBook = ParseStandardLoreBook(
                    loreBookElement,
                    isVersion3
                        ? CompatibilitySourceFormat.LoreBookV3Embedded
                        : CompatibilitySourceFormat.LoreBookV2Embedded,
                    isVersion3 ? "3.0" : "2.0",
                    EmptyJsonDictionary(),
                    "$.data.character_book",
                    diagnostics);
            }
        }

        var extensions = ReadExtensions(
            data,
            "extensions",
            "$.data.extensions",
            diagnostics);
        var rootUnknown = ExtractUnknown(root, CharacterRootFields);
        var dataUnknown = ExtractUnknown(data, CharacterDataFields);
        AddPreservationDiagnosticIfNeeded(
            diagnostics,
            "$",
            rootUnknown.Count + dataUnknown.Count);

        if (HasErrors(diagnostics))
        {
            return Result<CharacterDefinition>(null, diagnostics);
        }

        AddUntrustedContentDiagnostic(diagnostics);
        var definition = new CharacterDefinition(
            sourceFormat,
            sourceVersion,
            name,
            description,
            personality,
            scenario,
            firstMessage,
            exampleMessages,
            creatorNotes,
            systemPrompt,
            postHistoryInstructions,
            alternateGreetings,
            groupOnlyGreetings,
            tags,
            creator,
            characterVersion,
            nickname,
            multilingualCreatorNotes,
            sources,
            assets,
            createdAt,
            modifiedAt,
            characterLoreBook,
            new PreservedJsonFields(rootUnknown, dataUnknown, extensions));
        return Result<CharacterDefinition>(definition, diagnostics);
    }

    private LoreBookDefinition? ParseStandardLoreBook(
        JsonElement book,
        CompatibilitySourceFormat sourceFormat,
        string sourceVersion,
        IReadOnlyDictionary<string, JsonElement> rootUnknown,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!book.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            AddError(
                diagnostics,
                "missing_required_field",
                path + ".entries",
                "The required lore book entries array is missing or invalid.");
            return null;
        }

        if (entries.GetArrayLength() > _options.MaxLoreBookEntries)
        {
            AddError(
                diagnostics,
                "entry_limit_exceeded",
                path + ".entries",
                "The lore book exceeds the configured entry limit.");
            return null;
        }

        var parsedEntries = new List<LoreBookEntryDefinition>(entries.GetArrayLength());
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var entryPath = path + ".entries[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    entryPath,
                    "A lore book entry must be an object.");
                index++;
                continue;
            }

            var parsed = ParseStandardLoreEntry(
                entry,
                sourceVersion,
                entryPath,
                diagnostics);
            if (parsed is not null)
            {
                parsedEntries.Add(parsed);
            }

            index++;
        }

        var name = ReadOptionalString(book, "name", path + ".name", diagnostics);
        var description = ReadOptionalString(
            book,
            "description",
            path + ".description",
            diagnostics);
        var scanDepth = ReadOptionalNonNegativeInt(
            book,
            "scan_depth",
            path + ".scan_depth",
            diagnostics);
        var tokenBudget = ReadOptionalNonNegativeInt(
            book,
            "token_budget",
            path + ".token_budget",
            diagnostics);
        var recursiveScanning = ReadOptionalBoolean(
            book,
            "recursive_scanning",
            path + ".recursive_scanning",
            diagnostics);
        var extensions = ReadExtensions(
            book,
            "extensions",
            path + ".extensions",
            diagnostics);
        var objectUnknown = ExtractUnknown(book, LoreBookFields);
        AddPreservationDiagnosticIfNeeded(
            diagnostics,
            path,
            rootUnknown.Count + objectUnknown.Count);

        if (HasErrors(diagnostics))
        {
            return null;
        }

        return new LoreBookDefinition(
            sourceFormat,
            sourceVersion,
            name,
            description,
            scanDepth,
            tokenBudget,
            recursiveScanning,
            parsedEntries.AsReadOnly(),
            new PreservedJsonFields(rootUnknown, objectUnknown, extensions));
    }

    private LoreBookEntryDefinition? ParseStandardLoreEntry(
        JsonElement entry,
        string sourceVersion,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        var primaryKeys = ReadStringArray(
            entry,
            "keys",
            path + ".keys",
            diagnostics,
            required: true);
        var secondaryKeys = ReadStringArray(
            entry,
            "secondary_keys",
            path + ".secondary_keys",
            diagnostics,
            required: false);
        var content = ReadString(
            entry,
            "content",
            path + ".content",
            diagnostics,
            required: true,
            defaultValue: string.Empty);
        var enabled = ReadBoolean(
            entry,
            "enabled",
            path + ".enabled",
            diagnostics,
            required: true,
            defaultValue: true);
        var insertionOrder = ReadInt(
            entry,
            "insertion_order",
            path + ".insertion_order",
            diagnostics,
            required: true,
            defaultValue: 100);
        var caseSensitive = ReadOptionalBoolean(
            entry,
            "case_sensitive",
            path + ".case_sensitive",
            diagnostics);
        var useRegex = ReadBoolean(
            entry,
            "use_regex",
            path + ".use_regex",
            diagnostics,
            required: string.Equals(sourceVersion, "3.0", StringComparison.Ordinal),
            defaultValue: false);
        var alwaysActive = ReadBoolean(
            entry,
            "constant",
            path + ".constant",
            diagnostics,
            required: false,
            defaultValue: false);
        var requireSecondary = ReadBoolean(
            entry,
            "selective",
            path + ".selective",
            diagnostics,
            required: false,
            defaultValue: false);
        var name = ReadOptionalString(entry, "name", path + ".name", diagnostics);
        var comment = ReadOptionalString(entry, "comment", path + ".comment", diagnostics);
        var priority = ReadOptionalDouble(
            entry,
            "priority",
            path + ".priority",
            diagnostics);
        var identifier = ReadIdentifier(
            entry,
            "id",
            path + ".id",
            diagnostics,
            out var identifierKind);
        var extensions = ReadExtensions(
            entry,
            "extensions",
            path + ".extensions",
            diagnostics);

        var rawPosition = ReadRawPosition(entry, extensions, path, diagnostics);
        var position = MapPosition(rawPosition);
        var secondaryLogic = MapSecondaryLogic(
            ReadExtensionInt(extensions, "selectiveLogic")
            ?? ReadExtensionInt(extensions, "selective_logic"));
        var scanDepth = ReadExtensionNonNegativeInt(extensions, "scan_depth");
        caseSensitive ??= ReadExtensionBoolean(extensions, "case_sensitive");
        var matchWholeWords = ReadExtensionBoolean(extensions, "match_whole_words");
        var probability = ReadProbability(extensions, path, diagnostics);
        var sticky = ReadExtensionNonNegativeInt(extensions, "sticky");
        var cooldown = ReadExtensionNonNegativeInt(extensions, "cooldown");
        var delay = ReadExtensionNonNegativeInt(extensions, "delay");
        var directives = ParseDirectives(content, path, diagnostics);
        var unknown = ExtractUnknown(entry, StandardLoreEntryFields);
        AddPreservationDiagnosticIfNeeded(diagnostics, path, unknown.Count);

        if (HasErrors(diagnostics))
        {
            return null;
        }

        return new LoreBookEntryDefinition(
            identifier,
            identifierKind,
            name,
            comment,
            content,
            insertionOrder,
            priority,
            position,
            rawPosition,
            new LoreBookActivationDefinition(
                alwaysActive,
                enabled,
                useRegex
                    ? LoreBookMatchMode.RegularExpression
                    : LoreBookMatchMode.Literal,
                primaryKeys,
                secondaryKeys,
                requireSecondary,
                secondaryLogic,
                caseSensitive,
                matchWholeWords,
                scanDepth,
                probability,
                sticky,
                cooldown,
                delay),
            directives,
            new PreservedJsonFields(
                objectUnknownFields: unknown,
                extensionFields: extensions));
    }

    private LoreBookDefinition? ParseObjectMapLoreBook(
        JsonElement root,
        List<CompatibilityDiagnostic> diagnostics)
    {
        var entriesObject = root.GetProperty("entries");
        var entryCount = entriesObject.EnumerateObject().Count();
        if (entryCount > _options.MaxLoreBookEntries)
        {
            AddError(
                diagnostics,
                "entry_limit_exceeded",
                "$.entries",
                "The lore book exceeds the configured entry limit.");
            return null;
        }

        AddInfo(
            diagnostics,
            "object_map_lore_book",
            "$",
            "A compatible object-map lore book was imported.");

        var entries = new List<LoreBookEntryDefinition>(entryCount);
        var index = 0;
        foreach (var property in entriesObject.EnumerateObject())
        {
            var entryPath = "$.entries[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    entryPath,
                    "A lore book entry must be an object.");
                index++;
                continue;
            }

            var parsed = ParseObjectMapLoreEntry(
                property.Value,
                property.Name,
                entryPath,
                diagnostics);
            if (parsed is not null)
            {
                entries.Add(parsed);
            }

            index++;
        }

        var name = ReadOptionalString(root, "name", "$.name", diagnostics);
        var description = ReadOptionalString(
            root,
            "description",
            "$.description",
            diagnostics);
        var rootExtensions = ReadExtensions(
            root,
            "extensions",
            "$.extensions",
            diagnostics);
        var knownRoot = new HashSet<string>(
            new[] { "entries", "name", "description", "extensions" },
            StringComparer.Ordinal);
        var rootUnknown = ExtractUnknown(root, knownRoot);
        AddPreservationDiagnosticIfNeeded(diagnostics, "$", rootUnknown.Count);

        if (HasErrors(diagnostics))
        {
            return null;
        }

        return new LoreBookDefinition(
            CompatibilitySourceFormat.LoreBookObjectMapJson,
            "object-map-1",
            name,
            description,
            scanDepth: null,
            tokenBudget: null,
            recursiveScanning: null,
            entries.AsReadOnly(),
            new PreservedJsonFields(
                objectUnknownFields: rootUnknown,
                extensionFields: rootExtensions));
    }

    private LoreBookEntryDefinition? ParseObjectMapLoreEntry(
        JsonElement entry,
        string propertyIdentifier,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        var primaryKeys = ReadStringArray(
            entry,
            "key",
            path + ".key",
            diagnostics,
            required: false);
        var secondaryKeys = ReadStringArray(
            entry,
            "keysecondary",
            path + ".keysecondary",
            diagnostics,
            required: false);
        var content = ReadString(
            entry,
            "content",
            path + ".content",
            diagnostics,
            required: true,
            defaultValue: string.Empty);
        var alwaysActive = ReadBoolean(
            entry,
            "constant",
            path + ".constant",
            diagnostics,
            required: false,
            defaultValue: false);
        var requireSecondary = ReadBoolean(
            entry,
            "selective",
            path + ".selective",
            diagnostics,
            required: false,
            defaultValue: false);
        var disabled = ReadBoolean(
            entry,
            "disable",
            path + ".disable",
            diagnostics,
            required: false,
            defaultValue: false);
        var explicitlyEnabled = ReadOptionalBoolean(
            entry,
            "enabled",
            path + ".enabled",
            diagnostics);
        var insertionOrder = ReadInt(
            entry,
            "order",
            path + ".order",
            diagnostics,
            required: false,
            defaultValue: 100);
        var positionValue = ReadInt(
            entry,
            "position",
            path + ".position",
            diagnostics,
            required: false,
            defaultValue: 0);
        var rawPosition = positionValue.ToString(CultureInfo.InvariantCulture);
        var name = ReadOptionalString(entry, "name", path + ".name", diagnostics);
        var comment = ReadOptionalString(entry, "comment", path + ".comment", diagnostics);
        var caseSensitive = ReadOptionalBoolean(
            entry,
            "caseSensitive",
            path + ".caseSensitive",
            diagnostics);
        var matchWholeWords = ReadOptionalBoolean(
            entry,
            "matchWholeWords",
            path + ".matchWholeWords",
            diagnostics);
        var scanDepth = ReadOptionalNonNegativeInt(
            entry,
            "scanDepth",
            path + ".scanDepth",
            diagnostics);
        var probabilityEnabled = ReadOptionalBoolean(
            entry,
            "useProbability",
            path + ".useProbability",
            diagnostics) ?? true;
        var probabilityPercentage = ReadOptionalDouble(
            entry,
            "probability",
            path + ".probability",
            diagnostics);
        var probability = MapPercentageProbability(
            probabilityEnabled,
            probabilityPercentage,
            path + ".probability",
            diagnostics);
        var sticky = ReadOptionalNonNegativeInt(
            entry,
            "sticky",
            path + ".sticky",
            diagnostics);
        var cooldown = ReadOptionalNonNegativeInt(
            entry,
            "cooldown",
            path + ".cooldown",
            diagnostics);
        var delay = ReadOptionalNonNegativeInt(
            entry,
            "delay",
            path + ".delay",
            diagnostics);
        var secondaryLogic = MapSecondaryLogic(
            ReadOptionalInt(entry, "selectiveLogic", path + ".selectiveLogic", diagnostics));
        var extensions = ReadExtensions(
            entry,
            "extensions",
            path + ".extensions",
            diagnostics);
        var identifier = ReadIdentifier(
            entry,
            "uid",
            path + ".uid",
            diagnostics,
            out var identifierKind);
        if (identifier is null)
        {
            identifier = propertyIdentifier;
            identifierKind = long.TryParse(
                propertyIdentifier,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _)
                ? LoreBookEntryIdentifierKind.Number
                : LoreBookEntryIdentifierKind.String;
        }

        var directives = ParseDirectives(content, path, diagnostics);
        var unknown = ExtractUnknown(entry, ObjectMapLoreEntryFields);
        AddPreservationDiagnosticIfNeeded(diagnostics, path, unknown.Count);

        if (ContainsAdvancedObjectMapSemantics(entry))
        {
            AddWarning(
                diagnostics,
                "advanced_semantics_preserved",
                path,
                "Advanced lore activation metadata was preserved but requires an explicit host mapping.");
        }

        if (HasErrors(diagnostics))
        {
            return null;
        }

        return new LoreBookEntryDefinition(
            identifier,
            identifierKind,
            name,
            comment,
            content,
            insertionOrder,
            priority: null,
            MapPosition(rawPosition),
            rawPosition,
            new LoreBookActivationDefinition(
                alwaysActive,
                explicitlyEnabled ?? !disabled,
                LoreBookMatchMode.RegularExpression,
                primaryKeys,
                secondaryKeys,
                requireSecondary,
                secondaryLogic,
                caseSensitive,
                matchWholeWords,
                scanDepth,
                probability,
                sticky,
                cooldown,
                delay),
            directives,
            new PreservedJsonFields(
                objectUnknownFields: unknown,
                extensionFields: extensions));
    }

    private IReadOnlyList<CharacterAssetReference> ParseAssets(
        JsonElement data,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!data.TryGetProperty("assets", out var assets)
            || assets.ValueKind == JsonValueKind.Null)
        {
            return EmptyAssets();
        }

        if (assets.ValueKind != JsonValueKind.Array)
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                "$.data.assets",
                "The character assets field must be an array.");
            return EmptyAssets();
        }

        if (assets.GetArrayLength() > _options.MaxCollectionItems)
        {
            AddError(
                diagnostics,
                "collection_limit_exceeded",
                "$.data.assets",
                "The character assets array exceeds the configured item limit.");
            return EmptyAssets();
        }

        var result = new List<CharacterAssetReference>(assets.GetArrayLength());
        var hasRemote = false;
        var index = 0;
        var known = new HashSet<string>(
            new[] { "type", "uri", "name", "ext" },
            StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            var path = "$.data.assets[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (asset.ValueKind != JsonValueKind.Object)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    path,
                    "A character asset reference must be an object.");
                index++;
                continue;
            }

            var type = ReadString(
                asset,
                "type",
                path + ".type",
                diagnostics,
                required: true,
                defaultValue: string.Empty);
            var uri = ReadString(
                asset,
                "uri",
                path + ".uri",
                diagnostics,
                required: true,
                defaultValue: string.Empty);
            var name = ReadString(
                asset,
                "name",
                path + ".name",
                diagnostics,
                required: true,
                defaultValue: string.Empty);
            var extension = ReadString(
                asset,
                "ext",
                path + ".ext",
                diagnostics,
                required: true,
                defaultValue: string.Empty);
            var locationKind = ClassifyAssetLocation(uri);
            hasRemote |= locationKind is CharacterAssetLocationKind.Http
                or CharacterAssetLocationKind.Https;
            var unknown = ExtractUnknown(asset, known);
            result.Add(
                new CharacterAssetReference(
                    type,
                    uri,
                    name,
                    extension,
                    locationKind,
                    new PreservedJsonFields(objectUnknownFields: unknown)));
            index++;
        }

        if (hasRemote)
        {
            AddInfo(
                diagnostics,
                "remote_assets_not_fetched",
                "$.data.assets",
                "Remote asset references were preserved without being fetched.");
        }

        return result.AsReadOnly();
    }

    private JsonDocument? ParseJson(
        ReadOnlyMemory<byte> utf8Json,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (utf8Json.Length == 0)
        {
            AddError(diagnostics, "empty_input", "$", "The input is empty.");
            return null;
        }

        if (utf8Json.Length > _options.MaxInputBytes)
        {
            AddError(
                diagnostics,
                "input_too_large",
                "$",
                "The input exceeds the configured byte limit.");
            return null;
        }

        var bytes = utf8Json.ToArray();
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            AddError(
                diagnostics,
                "invalid_utf8",
                "$",
                "The input is not valid UTF-8.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxJsonDepth,
                });
        }
        catch (JsonException)
        {
            AddError(
                diagnostics,
                "invalid_json",
                "$",
                "The input is not valid JSON within the configured limits.");
            return null;
        }

        var nodeCount = 0;
        ValidateJsonTree(document.RootElement, "$", diagnostics, ref nodeCount);
        if (HasErrors(diagnostics))
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private void ValidateJsonTree(
        JsonElement element,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        ref int nodeCount)
    {
        nodeCount++;
        if (nodeCount > _options.MaxJsonNodes)
        {
            AddError(
                diagnostics,
                "node_limit_exceeded",
                path,
                "The JSON input exceeds the configured node limit.");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var count = 0;
                    foreach (var property in element.EnumerateObject())
                    {
                        count++;
                        if (count > _options.MaxCollectionItems)
                        {
                            AddError(
                                diagnostics,
                                "collection_limit_exceeded",
                                path,
                                "A JSON object exceeds the configured property limit.");
                            return;
                        }

                        if (property.Name.Length > _options.MaxStringCharacters)
                        {
                            AddError(
                                diagnostics,
                                "string_limit_exceeded",
                                path,
                                "A JSON property name exceeds the configured character limit.");
                            return;
                        }

                        if (!names.Add(property.Name))
                        {
                            AddError(
                                diagnostics,
                                "duplicate_property",
                                path,
                                "A JSON object contains a duplicate property name.");
                            return;
                        }

                        ValidateJsonTree(property.Value, path, diagnostics, ref nodeCount);
                        if (HasErrors(diagnostics))
                        {
                            return;
                        }
                    }

                    break;
                }

            case JsonValueKind.Array:
                {
                    var length = element.GetArrayLength();
                    if (length > _options.MaxCollectionItems)
                    {
                        AddError(
                            diagnostics,
                            "collection_limit_exceeded",
                            path,
                            "A JSON array exceeds the configured item limit.");
                        return;
                    }

                    foreach (var item in element.EnumerateArray())
                    {
                        ValidateJsonTree(item, path, diagnostics, ref nodeCount);
                        if (HasErrors(diagnostics))
                        {
                            return;
                        }
                    }

                    break;
                }

            case JsonValueKind.String:
                if ((element.GetString()?.Length ?? 0) > _options.MaxStringCharacters)
                {
                    AddError(
                        diagnostics,
                        "string_limit_exceeded",
                        path,
                        "A JSON string exceeds the configured character limit.");
                }

                break;
        }
    }

    private string ReadString(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        bool required,
        string defaultValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                AddWarning(
                    diagnostics,
                    "missing_required_field_defaulted",
                    path,
                    "A required string was missing and was defaulted to an empty value.");
            }

            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a string.");
            return defaultValue;
        }

        return value.GetString() ?? defaultValue;
    }

    private string? ReadOptionalString(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a string when present.");
            return null;
        }

        return value.GetString();
    }

    private IReadOnlyList<string> ReadStringArray(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        bool required)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                AddWarning(
                    diagnostics,
                    "missing_required_field_defaulted",
                    path,
                    "A required string array was missing and was defaulted to empty.");
            }

            return EmptyStrings();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be an array of strings.");
            return EmptyStrings();
        }

        var output = new List<string>(value.GetArrayLength());
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    "The array item must be a string.");
            }
            else
            {
                output.Add(item.GetString() ?? string.Empty);
            }

            index++;
        }

        return output.AsReadOnly();
    }

    private IReadOnlyDictionary<string, string> ReadStringDictionary(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return EmptyStringDictionary();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be an object of string values.");
            return EmptyStringDictionary();
        }

        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                AddError(
                    diagnostics,
                    "invalid_field_type",
                    path,
                    "Every value in the object must be a string.");
                continue;
            }

            output[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return new ReadOnlyDictionary<string, string>(output);
    }

    private bool ReadBoolean(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        bool required,
        bool defaultValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                AddWarning(
                    diagnostics,
                    "missing_required_field_defaulted",
                    path,
                    "A required boolean was missing and was defaulted.");
            }

            return defaultValue;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a boolean.");
            return defaultValue;
        }

        return value.GetBoolean();
    }

    private bool? ReadOptionalBoolean(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a boolean when present.");
            return null;
        }

        return value.GetBoolean();
    }

    private int ReadInt(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        bool required,
        int defaultValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                AddWarning(
                    diagnostics,
                    "missing_required_field_defaulted",
                    path,
                    "A required integer was missing and was defaulted.");
            }

            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var output))
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a 32-bit integer.");
            return defaultValue;
        }

        return output;
    }

    private int? ReadOptionalInt(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var output))
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a 32-bit integer when present.");
            return null;
        }

        return output;
    }

    private int? ReadOptionalNonNegativeInt(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        var value = ReadOptionalInt(parent, propertyName, path, diagnostics);
        if (value < 0)
        {
            AddWarning(
                diagnostics,
                "invalid_numeric_range",
                path,
                "A negative optional limit was ignored.");
            return null;
        }

        return value;
    }

    private double? ReadOptionalDouble(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var output)
            || double.IsNaN(output)
            || double.IsInfinity(output))
        {
            AddError(
                diagnostics,
                "invalid_field_type",
                path,
                "The field must be a finite number when present.");
            return null;
        }

        return output;
    }

    private DateTimeOffset? ReadTimestamp(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var seconds))
        {
            AddWarning(
                diagnostics,
                "invalid_timestamp",
                path,
                "An invalid timestamp was preserved but not mapped.");
            return null;
        }

        if (seconds == 0)
        {
            AddInfo(
                diagnostics,
                "unknown_timestamp_marker",
                path,
                "An explicit unknown timestamp marker was mapped to no timestamp.");
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            AddWarning(
                diagnostics,
                "invalid_timestamp",
                path,
                "An out-of-range timestamp was preserved but not mapped.");
            return null;
        }
    }

    private string? ReadIdentifier(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics,
        out LoreBookEntryIdentifierKind kind)
    {
        kind = LoreBookEntryIdentifierKind.None;
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            kind = LoreBookEntryIdentifierKind.String;
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            kind = LoreBookEntryIdentifierKind.Number;
            return value.GetRawText();
        }

        AddError(
            diagnostics,
            "invalid_field_type",
            path,
            "The entry identifier must be a string or number.");
        return null;
    }

    private IReadOnlyDictionary<string, JsonElement> ReadExtensions(
        JsonElement parent,
        string propertyName,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(propertyName, out var extensions)
            || extensions.ValueKind == JsonValueKind.Null)
        {
            return EmptyJsonDictionary();
        }

        if (extensions.ValueKind != JsonValueKind.Object)
        {
            AddWarning(
                diagnostics,
                "invalid_extensions_ignored",
                path,
                "A non-object extensions value was ignored.");
            return EmptyJsonDictionary();
        }

        var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in extensions.EnumerateObject())
        {
            output[property.Name] = property.Value.Clone();
        }

        return new ReadOnlyDictionary<string, JsonElement>(output);
    }

    private static IReadOnlyDictionary<string, JsonElement> ExtractUnknown(
        JsonElement value,
        ISet<string> knownFields)
    {
        var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!knownFields.Contains(property.Name))
            {
                output[property.Name] = property.Value.Clone();
            }
        }

        return new ReadOnlyDictionary<string, JsonElement>(output);
    }

    private IReadOnlyList<LoreBookDirective> ParseDirectives(
        string content,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (content.IndexOf("@@", StringComparison.Ordinal) < 0)
        {
            return Array.Empty<LoreBookDirective>();
        }

        var output = new List<LoreBookDirective>();
        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var fallback = line.StartsWith("@@@", StringComparison.Ordinal);
            var prefixLength = fallback ? 3 : 2;
            if (line.Length <= prefixLength
                || !line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            if (output.Count >= _options.MaxDirectivesPerEntry)
            {
                AddWarning(
                    diagnostics,
                    "directive_limit_exceeded",
                    path + ".content",
                    "Additional lore directives were preserved in content but not mapped.");
                break;
            }

            var separator = line.IndexOf(' ', prefixLength);
            var name = separator < 0
                ? line.Substring(prefixLength)
                : line.Substring(prefixLength, separator - prefixLength);
            if (name.Length == 0)
            {
                continue;
            }

            var value = separator < 0 ? string.Empty : line.Substring(separator + 1).Trim();
            output.Add(new LoreBookDirective(name, value, fallback));
        }

        return output.AsReadOnly();
    }

    private static string ReadRawPosition(
        JsonElement entry,
        IReadOnlyDictionary<string, JsonElement> extensions,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (entry.TryGetProperty("position", out var position)
            && position.ValueKind != JsonValueKind.Null)
        {
            if (position.ValueKind == JsonValueKind.String)
            {
                return position.GetString() ?? string.Empty;
            }

            if (position.ValueKind == JsonValueKind.Number)
            {
                return position.GetRawText();
            }

            AddWarning(
                diagnostics,
                "invalid_position_ignored",
                path + ".position",
                "An invalid lore position was not mapped.");
            return string.Empty;
        }

        if (extensions.TryGetValue("position", out var extensionPosition))
        {
            if (extensionPosition.ValueKind == JsonValueKind.String)
            {
                return extensionPosition.GetString() ?? string.Empty;
            }

            if (extensionPosition.ValueKind == JsonValueKind.Number)
            {
                return extensionPosition.GetRawText();
            }
        }

        return "after_char";
    }

    private static LoreBookPosition MapPosition(string rawPosition)
    {
        return rawPosition switch
        {
            "before_char" or "0" => LoreBookPosition.BeforeCharacter,
            "after_char" or "1" => LoreBookPosition.AfterCharacter,
            "at_depth" or "4" => LoreBookPosition.AtDepth,
            _ => LoreBookPosition.Other,
        };
    }

    private static LoreBookSecondaryKeyLogic MapSecondaryLogic(int? value)
    {
        return value switch
        {
            1 => LoreBookSecondaryKeyLogic.NotAll,
            2 => LoreBookSecondaryKeyLogic.NotAny,
            3 => LoreBookSecondaryKeyLogic.All,
            _ => LoreBookSecondaryKeyLogic.Any,
        };
    }

    private static double? ReadProbability(
        IReadOnlyDictionary<string, JsonElement> extensions,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        var enabled = ReadExtensionBoolean(extensions, "useProbability")
            ?? ReadExtensionBoolean(extensions, "use_probability")
            ?? true;
        if (!enabled)
        {
            return null;
        }

        if (!extensions.TryGetValue("probability", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var percentage)
            || double.IsNaN(percentage)
            || double.IsInfinity(percentage)
            || percentage < 0
            || percentage > 100)
        {
            AddWarning(
                diagnostics,
                "invalid_probability_preserved",
                path,
                "An invalid probability was preserved but not mapped.");
            return null;
        }

        return percentage / 100d;
    }

    private static double? MapPercentageProbability(
        bool enabled,
        double? percentage,
        string path,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (!enabled)
        {
            return null;
        }

        if (percentage is null)
        {
            return null;
        }

        if (percentage < 0 || percentage > 100)
        {
            AddWarning(
                diagnostics,
                "invalid_probability_preserved",
                path,
                "An invalid probability was preserved but not mapped.");
            return null;
        }

        return percentage.Value / 100d;
    }

    private static bool ContainsAdvancedObjectMapSemantics(JsonElement entry)
    {
        string[] fields =
        {
            "vectorized",
            "ignoreBudget",
            "excludeRecursion",
            "preventRecursion",
            "delayUntilRecursion",
            "outletName",
            "group",
            "groupOverride",
            "groupWeight",
            "useGroupScoring",
            "automationId",
            "role",
            "triggers",
        };

        foreach (var field in fields)
        {
            if (entry.TryGetProperty(field, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static int? ReadExtensionInt(
        IReadOnlyDictionary<string, JsonElement> extensions,
        string name)
    {
        return extensions.TryGetValue(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var output)
            ? output
            : null;
    }

    private static int? ReadExtensionNonNegativeInt(
        IReadOnlyDictionary<string, JsonElement> extensions,
        string name)
    {
        var value = ReadExtensionInt(extensions, name);
        return value >= 0 ? value : null;
    }

    private static bool? ReadExtensionBoolean(
        IReadOnlyDictionary<string, JsonElement> extensions,
        string name)
    {
        return extensions.TryGetValue(name, out var value)
               && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static CharacterAssetLocationKind ClassifyAssetLocation(string uri)
    {
        if (string.Equals(uri, "ccdefault:", StringComparison.Ordinal))
        {
            return CharacterAssetLocationKind.Default;
        }

        if (uri.StartsWith("embeded://", StringComparison.Ordinal)
            || uri.StartsWith("__asset:", StringComparison.Ordinal))
        {
            return CharacterAssetLocationKind.Embedded;
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterAssetLocationKind.Data;
        }

        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterAssetLocationKind.Https;
        }

        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterAssetLocationKind.Http;
        }

        return CharacterAssetLocationKind.Other;
    }

    private static bool TryGetString(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        if (parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private byte[]? EncodeJsonString(
        string json,
        List<CompatibilityDiagnostic> diagnostics)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException)
        {
            AddError(
                diagnostics,
                "invalid_utf16",
                "$",
                "The input string contains an invalid Unicode sequence.");
            return null;
        }
        catch (ArgumentException)
        {
            AddError(
                diagnostics,
                "input_too_large",
                "$",
                "The input exceeds the supported string size.");
            return null;
        }

        if (byteCount > _options.MaxInputBytes)
        {
            AddError(
                diagnostics,
                "input_too_large",
                "$",
                "The input exceeds the configured byte limit.");
            return null;
        }

        try
        {
            return StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException)
        {
            AddError(
                diagnostics,
                "invalid_utf16",
                "$",
                "The input string contains an invalid Unicode sequence.");
            return null;
        }
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        if (parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static IReadOnlyList<string> EmptyStrings()
    {
        return Array.Empty<string>();
    }

    private static IReadOnlyList<CharacterAssetReference> EmptyAssets()
    {
        return Array.Empty<CharacterAssetReference>();
    }

    private static IReadOnlyDictionary<string, string> EmptyStringDictionary()
    {
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyJsonDictionary()
    {
        return new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal));
    }

    private static bool HasErrors(IEnumerable<CompatibilityDiagnostic> diagnostics)
    {
        return diagnostics.Any(static diagnostic =>
            diagnostic.Severity == CompatibilityDiagnosticSeverity.Error);
    }

    private static CompatibilityImportResult<T> Result<T>(
        T? value,
        List<CompatibilityDiagnostic> diagnostics)
        where T : class
    {
        return new CompatibilityImportResult<T>(
            value,
            diagnostics.AsReadOnly());
    }

    private CompatibilityImportResult<T> AttachSource<T>(
        CompatibilityImportResult<T> result,
        string adapterKind,
        ReadOnlyMemory<byte> source)
        where T : class
    {
        string? digest = null;
        if (source.Length <= _options.MaxInputBytes)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(source.ToArray());
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                text.Append(value.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
            }

            digest = text.ToString();
        }

        return result.WithSourceMetadata(
            "game-agent." + adapterKind,
            "1",
            digest);
    }

    private static void AddPreservationDiagnosticIfNeeded(
        List<CompatibilityDiagnostic> diagnostics,
        string path,
        int count)
    {
        if (count > 0)
        {
            AddInfo(
                diagnostics,
                "unknown_fields_preserved",
                path,
                "Unknown JSON fields were preserved for a future compatible export.");
        }
    }

    private static void AddUntrustedContentDiagnostic(
        List<CompatibilityDiagnostic> diagnostics)
    {
        AddWarning(
            diagnostics,
            "untrusted_content_data_only",
            "$",
            "Imported instructions, patterns, directives, and links are untrusted data and were not activated.");
    }

    private static void AddInfo(
        List<CompatibilityDiagnostic> diagnostics,
        string code,
        string path,
        string message)
    {
        diagnostics.Add(
            new CompatibilityDiagnostic(
                code,
                CompatibilityDiagnosticSeverity.Info,
                path,
                message));
    }

    private static void AddWarning(
        List<CompatibilityDiagnostic> diagnostics,
        string code,
        string path,
        string message)
    {
        diagnostics.Add(
            new CompatibilityDiagnostic(
                code,
                CompatibilityDiagnosticSeverity.Warning,
                path,
                message));
    }

    private static void AddError(
        List<CompatibilityDiagnostic> diagnostics,
        string code,
        string path,
        string message)
    {
        diagnostics.Add(
            new CompatibilityDiagnostic(
                code,
                CompatibilityDiagnosticSeverity.Error,
                path,
                message));
    }
}
