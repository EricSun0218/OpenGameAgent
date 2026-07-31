using System.Globalization;
using System.Text.Json;
using GameAgent.Compatibility;
using GameAgent.World;

namespace GameAgent.Runtime;

public enum ImportedContentAcceptance
{
    Reject = 0,
    AcceptAsUntrustedData = 1
}

/// <summary>
/// Explicitly maps successful compatibility imports into inert native package
/// content. It does not create entities, agents, events, tools, or skills.
/// </summary>
public sealed class NativeWorldImportComposer
{
    private readonly string _packageId;
    private readonly string _contentVersion;
    private readonly List<WorldPackageFile> _files = new();
    private readonly HashSet<string> _paths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _characterContentIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loreContentIds =
        new(StringComparer.Ordinal);
    private readonly List<ImportedAgentContentBinding> _bindings = new();

    public NativeWorldImportComposer(
        string packageId,
        string contentVersion)
    {
        _packageId = Required(packageId, nameof(packageId), 256);
        _contentVersion = Required(
            contentVersion,
            nameof(contentVersion),
            128);
    }

    public NativeWorldImportComposer AddCharacter(
        string contentId,
        CompatibilityImportResult<CharacterDefinition> import,
        ImportedContentAcceptance acceptance)
    {
        EnsureAccepted(import, acceptance, nameof(import));
        var id = ContentId(contentId, nameof(contentId));
        var contentFile = AddFile(
            "content/characters/" + id + ".json",
            ImportedWorldPackageContentCodec.CharacterMediaType,
            writer => ImportedWorldPackageContentCodec.WriteCharacter(
                writer,
                id,
                import.Value!));
        AddDiagnostics(
            "character-" + id,
            import.AdapterId,
            import.AdapterVersion,
            import.SourceDigest,
            ImportedWorldPackageContentCodec.ComputeCanonicalJsonDigest(
                contentFile.GetContentCopy()),
            import.Diagnostics);
        _characterContentIds.Add(id);
        return this;
    }

    public NativeWorldImportComposer AddLoreBook(
        string contentId,
        CompatibilityImportResult<LoreBookDefinition> import,
        ImportedContentAcceptance acceptance)
    {
        EnsureAccepted(import, acceptance, nameof(import));
        var id = ContentId(contentId, nameof(contentId));
        var contentFile = AddFile(
            "content/knowledge/" + id + ".json",
            ImportedWorldPackageContentCodec.KnowledgeMediaType,
            writer => ImportedWorldPackageContentCodec.WriteLoreBook(
                writer,
                id,
                import.Value!));
        AddDiagnostics(
            "knowledge-" + id,
            import.AdapterId,
            import.AdapterVersion,
            import.SourceDigest,
            ImportedWorldPackageContentCodec.ComputeCanonicalJsonDigest(
                contentFile.GetContentCopy()),
            import.Diagnostics);
        _loreContentIds.Add(id);
        return this;
    }

    /// <summary>
    /// Adds an inert, explicit agent-to-import binding. The descriptor grants
    /// no tools, skills, credentials, or provider authority.
    /// </summary>
    public NativeWorldImportComposer AddAgentBinding(
        string agentId,
        string? characterContentId,
        IEnumerable<string>? loreContentIds,
        ImportedContentAcceptance acceptance)
    {
        if (acceptance != ImportedContentAcceptance.AcceptAsUntrustedData)
        {
            throw new InvalidOperationException(
                "Imported content bindings require explicit untrusted-data "
                + "acceptance.");
        }

        var binding = ImportedAgentContentBinding.Create(
            agentId,
            characterContentId,
            loreContentIds);
        AddFile(
            "content/agent-bindings/" + binding.AgentId + ".json",
            ImportedWorldPackageContentCodec.AgentBindingMediaType,
            writer => ImportedWorldPackageContentCodec.WriteAgentBinding(
                writer,
                binding));
        _bindings.Add(binding);
        return this;
    }

    public WorldPackageDefinition Build(
        IEnumerable<WorldPackageExtensionRequirement>?
            requiredExtensions = null,
        IReadOnlyDictionary<string, JsonElement>? extensionData = null)
    {
        foreach (var binding in _bindings)
        {
            if (binding.CharacterContentId is not null
                && !_characterContentIds.Contains(
                    binding.CharacterContentId))
            {
                throw new InvalidOperationException(
                    "An agent binding references missing character "
                    + "content.");
            }

            if (binding.LoreContentIds.Any(
                    contentId => !_loreContentIds.Contains(contentId)))
            {
                throw new InvalidOperationException(
                    "An agent binding references missing lore content.");
            }
        }

        return new WorldPackageDefinition(
            _packageId,
            _contentVersion,
            _files,
            requiredExtensions,
            extensionData);
    }

    private void AddDiagnostics(
        string contentId,
        string? adapterId,
        string? adapterVersion,
        string? sourceDigest,
        string normalizedContentDigest,
        IReadOnlyList<CompatibilityDiagnostic> diagnostics)
    {
        AddFile(
            "imports/" + contentId + ".diagnostics.json",
            ImportedWorldPackageContentCodec.DiagnosticsMediaType,
            writer => ImportedWorldPackageContentCodec.WriteDiagnostics(
                writer,
                adapterId,
                adapterVersion,
                sourceDigest,
                normalizedContentDigest,
                diagnostics));
    }

    private WorldPackageFile AddFile(
        string path,
        string mediaType,
        Action<Utf8JsonWriter> write)
    {
        if (!_paths.Add(path))
        {
            throw new InvalidOperationException(
                "Imported content identifier collides with existing content.");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        var file = new WorldPackageFile(path, mediaType, output.ToArray());
        _files.Add(file);
        return file;
    }

    internal static void WriteCharacter(
        Utf8JsonWriter writer,
        string contentId,
        CharacterDefinition character)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "contract",
            "game-agent.imported-character.v1");
        writer.WriteString("contentTrust", "untrusted_data");
        writer.WriteString("contentId", contentId);
        writer.WriteString("sourceFormat", character.SourceFormat.ToString());
        writer.WriteString("sourceVersion", character.SourceVersion);
        writer.WritePropertyName("identity");
        writer.WriteStartObject();
        writer.WriteString("name", character.Name);
        WriteOptional(writer, "nickname", character.Nickname);
        WriteStrings(writer, "tags", character.Tags);
        writer.WriteEndObject();
        writer.WritePropertyName("authoredContext");
        writer.WriteStartObject();
        writer.WriteString("description", character.Description);
        writer.WriteString("personality", character.Personality);
        writer.WriteString("scenario", character.Scenario);
        writer.WriteString("firstMessage", character.FirstMessage);
        writer.WriteString("exampleMessages", character.ExampleMessages);
        writer.WriteString("creatorNotes", character.CreatorNotes);
        writer.WriteString("systemPrompt", character.SystemPrompt);
        writer.WriteString(
            "postHistoryInstructions",
            character.PostHistoryInstructions);
        WriteStrings(
            writer,
            "alternateGreetings",
            character.AlternateGreetings);
        WriteStrings(
            writer,
            "groupOnlyGreetings",
            character.GroupOnlyGreetings);
        writer.WriteEndObject();
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("creator", character.Creator);
        writer.WriteString(
            "characterVersion",
            character.CharacterVersion);
        WriteStrings(writer, "sources", character.Sources);
        WriteOptionalTimestamp(writer, "createdAt", character.CreatedAt);
        WriteOptionalTimestamp(writer, "modifiedAt", character.ModifiedAt);
        writer.WritePropertyName("multilingualCreatorNotes");
        writer.WriteStartObject();
        foreach (var pair in character.MultilingualCreatorNotes
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WritePropertyName("assets");
        writer.WriteStartArray();
        foreach (var asset in character.Assets)
        {
            writer.WriteStartObject();
            writer.WriteString("type", asset.Type);
            writer.WriteString("uri", asset.Uri);
            writer.WriteString("name", asset.Name);
            writer.WriteString("extension", asset.Extension);
            writer.WriteString(
                "locationKind",
                asset.LocationKind.ToString());
            WritePreserved(writer, asset.PreservedFields);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("embeddedKnowledge");
        if (character.CharacterLoreBook is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            WriteLoreBookBody(writer, character.CharacterLoreBook);
            writer.WriteEndObject();
        }

        WritePreserved(writer, character.PreservedFields);
        writer.WriteEndObject();
    }

    internal static void WriteLoreBook(
        Utf8JsonWriter writer,
        string contentId,
        LoreBookDefinition loreBook)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "contract",
            "game-agent.imported-knowledge.v1");
        writer.WriteString("contentTrust", "untrusted_data");
        writer.WriteString("contentId", contentId);
        WriteLoreBookBody(writer, loreBook);
        writer.WriteEndObject();
    }

    private static void WriteLoreBookBody(
        Utf8JsonWriter writer,
        LoreBookDefinition loreBook)
    {
        writer.WriteString("sourceFormat", loreBook.SourceFormat.ToString());
        writer.WriteString("sourceVersion", loreBook.SourceVersion);
        WriteOptional(writer, "name", loreBook.Name);
        WriteOptional(writer, "description", loreBook.Description);
        WriteOptionalNumber(writer, "scanDepth", loreBook.ScanDepth);
        WriteOptionalNumber(writer, "tokenBudget", loreBook.TokenBudget);
        WriteOptionalBoolean(
            writer,
            "recursiveScanning",
            loreBook.RecursiveScanning);
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (var entry in loreBook.Entries)
        {
            writer.WriteStartObject();
            WriteOptional(writer, "identifier", entry.Identifier);
            writer.WriteString(
                "identifierKind",
                entry.IdentifierKind.ToString());
            WriteOptional(writer, "name", entry.Name);
            WriteOptional(writer, "comment", entry.Comment);
            writer.WriteString("content", entry.Content);
            writer.WriteNumber("insertionOrder", entry.InsertionOrder);
            WriteOptionalDecimalString(writer, "priority", entry.Priority);
            writer.WriteString("position", entry.Position.ToString());
            writer.WriteString("sourcePosition", entry.SourcePosition);
            writer.WritePropertyName("activation");
            writer.WriteStartObject();
            writer.WriteBoolean(
                "alwaysActive",
                entry.Activation.AlwaysActive);
            writer.WriteBoolean("enabled", entry.Activation.Enabled);
            writer.WriteString(
                "matchMode",
                entry.Activation.MatchMode.ToString());
            WriteStrings(
                writer,
                "primaryKeys",
                entry.Activation.PrimaryKeys);
            WriteStrings(
                writer,
                "secondaryKeys",
                entry.Activation.SecondaryKeys);
            writer.WriteBoolean(
                "requireSecondaryKey",
                entry.Activation.RequireSecondaryKey);
            writer.WriteString(
                "secondaryKeyLogic",
                entry.Activation.SecondaryKeyLogic.ToString());
            WriteOptionalBoolean(
                writer,
                "caseSensitive",
                entry.Activation.CaseSensitive);
            WriteOptionalBoolean(
                writer,
                "matchWholeWords",
                entry.Activation.MatchWholeWords);
            WriteOptionalNumber(
                writer,
                "scanDepth",
                entry.Activation.ScanDepth);
            WriteOptionalDecimalString(
                writer,
                "probability",
                entry.Activation.Probability);
            WriteOptionalNumber(
                writer,
                "stickyTurns",
                entry.Activation.StickyTurns);
            WriteOptionalNumber(
                writer,
                "cooldownTurns",
                entry.Activation.CooldownTurns);
            WriteOptionalNumber(
                writer,
                "delayTurns",
                entry.Activation.DelayTurns);
            writer.WriteEndObject();
            writer.WritePropertyName("directives");
            writer.WriteStartArray();
            foreach (var directive in entry.Directives)
            {
                writer.WriteStartObject();
                writer.WriteString("name", directive.Name);
                writer.WriteString("value", directive.Value);
                writer.WriteBoolean("isFallback", directive.IsFallback);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WritePreserved(writer, entry.PreservedFields);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WritePreserved(writer, loreBook.PreservedFields);
    }

    private static void WritePreserved(
        Utf8JsonWriter writer,
        PreservedJsonFields fields)
    {
        writer.WritePropertyName("preservedSourceData");
        writer.WriteStartObject();
        WriteJsonMap(writer, "root", fields.RootUnknownFields);
        WriteJsonMap(writer, "object", fields.ObjectUnknownFields);
        WriteJsonMap(writer, "extensions", fields.ExtensionFields);
        writer.WriteEndObject();
    }

    private static void WriteJsonMap(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        foreach (var pair in values.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
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

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteOptionalBoolean(
        Utf8JsonWriter writer,
        string propertyName,
        bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteBoolean(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteOptionalDecimalString(
        Utf8JsonWriter writer,
        string propertyName,
        double? value)
    {
        if (!value.HasValue)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(
            propertyName,
            value.Value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteOptionalTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writer.WriteString(
                propertyName,
                value.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void EnsureAccepted<T>(
        CompatibilityImportResult<T>? import,
        ImportedContentAcceptance acceptance,
        string parameterName)
        where T : class
    {
        if (import is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!import.Success || import.Value is null)
        {
            throw new ArgumentException(
                "Only a successful import can be composed.",
                parameterName);
        }

        if (acceptance != ImportedContentAcceptance.AcceptAsUntrustedData)
        {
            throw new InvalidOperationException(
                "Imported content requires explicit untrusted-data "
                + "acceptance.");
        }
    }

    private static string ContentId(string? value, string parameterName)
    {
        var id = Required(value, parameterName, 128);
        if (id is "." or ".."
            || id.Any(
                character => character is not (
                    >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_'
                    or '.')))
        {
            throw new ArgumentException(
                "Content ID contains a non-portable character.",
                parameterName);
        }

        return id;
    }

    private static string Required(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }
}
