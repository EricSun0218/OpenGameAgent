using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Compatibility;

public enum CompatibilitySourceFormat
{
    CharacterCardV2Json,
    CharacterCardV3Json,
    CharacterCardV2Png,
    CharacterCardV3Png,
    LoreBookV2Embedded,
    LoreBookV3Embedded,
    LoreBookV3Json,
    LoreBookObjectMapJson,
}

public enum CompatibilityDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum CompatibilityContentTrust
{
    UntrustedData,
}

public sealed class CompatibilityDiagnostic
{
    public CompatibilityDiagnostic(
        string code,
        CompatibilityDiagnosticSeverity severity,
        string path,
        string message)
    {
        Code = RequireText(code, nameof(code));
        Severity = severity;
        Path = RequireText(path, nameof(path));
        Message = RequireText(message, nameof(message));
    }

    public string Code { get; }

    public CompatibilityDiagnosticSeverity Severity { get; }

    public string Path { get; }

    public string Message { get; }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value;
    }
}

public sealed class CompatibilityImportResult<T>
    where T : class
{
    public CompatibilityImportResult(
        T? value,
        IReadOnlyList<CompatibilityDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public T? Value { get; }

    public IReadOnlyList<CompatibilityDiagnostic> Diagnostics { get; }

    public string? AdapterId { get; private set; }

    public string? AdapterVersion { get; private set; }

    /// <summary>
    /// Lowercase SHA-256 of the admitted source bytes. Oversized or
    /// non-encodable inputs do not receive a digest.
    /// </summary>
    public string? SourceDigest { get; private set; }

    public CompatibilityContentTrust ContentTrust => CompatibilityContentTrust.UntrustedData;

    public bool Success =>
        Value is not null
        && !Diagnostics.Any(static diagnostic =>
            diagnostic.Severity == CompatibilityDiagnosticSeverity.Error);

    internal CompatibilityImportResult<T> WithSourceMetadata(
        string adapterId,
        string adapterVersion,
        string? sourceDigest)
    {
        AdapterId = adapterId;
        AdapterVersion = adapterVersion;
        SourceDigest = sourceDigest;
        return this;
    }
}

public sealed class PreservedJsonFields
{
    internal PreservedJsonFields(
        IReadOnlyDictionary<string, JsonElement>? rootUnknownFields = null,
        IReadOnlyDictionary<string, JsonElement>? objectUnknownFields = null,
        IReadOnlyDictionary<string, JsonElement>? extensionFields = null)
    {
        RootUnknownFields = rootUnknownFields ?? EmptyJsonFields.Value;
        ObjectUnknownFields = objectUnknownFields ?? EmptyJsonFields.Value;
        ExtensionFields = extensionFields ?? EmptyJsonFields.Value;
    }

    public IReadOnlyDictionary<string, JsonElement> RootUnknownFields { get; }

    public IReadOnlyDictionary<string, JsonElement> ObjectUnknownFields { get; }

    public IReadOnlyDictionary<string, JsonElement> ExtensionFields { get; }

    private static class EmptyJsonFields
    {
        internal static readonly IReadOnlyDictionary<string, JsonElement> Value =
            new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
    }
}

public enum CharacterAssetLocationKind
{
    Default,
    Embedded,
    Data,
    Https,
    Http,
    Other,
}

public sealed class CharacterAssetReference
{
    internal CharacterAssetReference(
        string type,
        string uri,
        string name,
        string extension,
        CharacterAssetLocationKind locationKind,
        PreservedJsonFields preservedFields)
    {
        Type = type;
        Uri = uri;
        Name = name;
        Extension = extension;
        LocationKind = locationKind;
        PreservedFields = preservedFields;
    }

    public string Type { get; }

    public string Uri { get; }

    public string Name { get; }

    public string Extension { get; }

    public CharacterAssetLocationKind LocationKind { get; }

    public PreservedJsonFields PreservedFields { get; }
}

public sealed class CharacterDefinition
{
    internal CharacterDefinition(
        CompatibilitySourceFormat sourceFormat,
        string sourceVersion,
        string name,
        string description,
        string personality,
        string scenario,
        string firstMessage,
        string exampleMessages,
        string creatorNotes,
        string systemPrompt,
        string postHistoryInstructions,
        IReadOnlyList<string> alternateGreetings,
        IReadOnlyList<string> groupOnlyGreetings,
        IReadOnlyList<string> tags,
        string creator,
        string characterVersion,
        string? nickname,
        IReadOnlyDictionary<string, string> multilingualCreatorNotes,
        IReadOnlyList<string> sources,
        IReadOnlyList<CharacterAssetReference> assets,
        DateTimeOffset? createdAt,
        DateTimeOffset? modifiedAt,
        LoreBookDefinition? characterLoreBook,
        PreservedJsonFields preservedFields)
    {
        SourceFormat = sourceFormat;
        SourceVersion = sourceVersion;
        Name = name;
        Description = description;
        Personality = personality;
        Scenario = scenario;
        FirstMessage = firstMessage;
        ExampleMessages = exampleMessages;
        CreatorNotes = creatorNotes;
        SystemPrompt = systemPrompt;
        PostHistoryInstructions = postHistoryInstructions;
        AlternateGreetings = alternateGreetings;
        GroupOnlyGreetings = groupOnlyGreetings;
        Tags = tags;
        Creator = creator;
        CharacterVersion = characterVersion;
        Nickname = nickname;
        MultilingualCreatorNotes = multilingualCreatorNotes;
        Sources = sources;
        Assets = assets;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
        CharacterLoreBook = characterLoreBook;
        PreservedFields = preservedFields;
    }

    public CompatibilitySourceFormat SourceFormat { get; }

    public CompatibilityContentTrust ContentTrust => CompatibilityContentTrust.UntrustedData;

    public string SourceVersion { get; }

    public string Name { get; }

    public string Description { get; }

    public string Personality { get; }

    public string Scenario { get; }

    public string FirstMessage { get; }

    public string ExampleMessages { get; }

    public string CreatorNotes { get; }

    public string SystemPrompt { get; }

    public string PostHistoryInstructions { get; }

    public IReadOnlyList<string> AlternateGreetings { get; }

    public IReadOnlyList<string> GroupOnlyGreetings { get; }

    public IReadOnlyList<string> Tags { get; }

    public string Creator { get; }

    public string CharacterVersion { get; }

    public string? Nickname { get; }

    public IReadOnlyDictionary<string, string> MultilingualCreatorNotes { get; }

    public IReadOnlyList<string> Sources { get; }

    public IReadOnlyList<CharacterAssetReference> Assets { get; }

    public DateTimeOffset? CreatedAt { get; }

    public DateTimeOffset? ModifiedAt { get; }

    public LoreBookDefinition? CharacterLoreBook { get; }

    public PreservedJsonFields PreservedFields { get; }
}

public enum LoreBookMatchMode
{
    Literal,
    RegularExpression,
}

public enum LoreBookSecondaryKeyLogic
{
    Any,
    All,
    NotAny,
    NotAll,
}

public enum LoreBookPosition
{
    BeforeCharacter,
    AfterCharacter,
    AtDepth,
    Other,
}

public enum LoreBookEntryIdentifierKind
{
    None,
    String,
    Number,
}

public sealed class LoreBookDirective
{
    internal LoreBookDirective(string name, string value, bool isFallback)
    {
        Name = name;
        Value = value;
        IsFallback = isFallback;
    }

    public string Name { get; }

    public string Value { get; }

    public bool IsFallback { get; }
}

public sealed class LoreBookActivationDefinition
{
    internal LoreBookActivationDefinition(
        bool alwaysActive,
        bool enabled,
        LoreBookMatchMode matchMode,
        IReadOnlyList<string> primaryKeys,
        IReadOnlyList<string> secondaryKeys,
        bool requireSecondaryKey,
        LoreBookSecondaryKeyLogic secondaryKeyLogic,
        bool? caseSensitive,
        bool? matchWholeWords,
        int? scanDepth,
        double? probability,
        int? stickyTurns,
        int? cooldownTurns,
        int? delayTurns)
    {
        AlwaysActive = alwaysActive;
        Enabled = enabled;
        MatchMode = matchMode;
        PrimaryKeys = primaryKeys;
        SecondaryKeys = secondaryKeys;
        RequireSecondaryKey = requireSecondaryKey;
        SecondaryKeyLogic = secondaryKeyLogic;
        CaseSensitive = caseSensitive;
        MatchWholeWords = matchWholeWords;
        ScanDepth = scanDepth;
        Probability = probability;
        StickyTurns = stickyTurns;
        CooldownTurns = cooldownTurns;
        DelayTurns = delayTurns;
    }

    public bool AlwaysActive { get; }

    public bool Enabled { get; }

    public LoreBookMatchMode MatchMode { get; }

    public IReadOnlyList<string> PrimaryKeys { get; }

    public IReadOnlyList<string> SecondaryKeys { get; }

    public bool RequireSecondaryKey { get; }

    public LoreBookSecondaryKeyLogic SecondaryKeyLogic { get; }

    public bool? CaseSensitive { get; }

    public bool? MatchWholeWords { get; }

    public int? ScanDepth { get; }

    public double? Probability { get; }

    public int? StickyTurns { get; }

    public int? CooldownTurns { get; }

    public int? DelayTurns { get; }
}

public sealed class LoreBookEntryDefinition
{
    internal LoreBookEntryDefinition(
        string? identifier,
        LoreBookEntryIdentifierKind identifierKind,
        string? name,
        string? comment,
        string content,
        int insertionOrder,
        double? priority,
        LoreBookPosition position,
        string sourcePosition,
        LoreBookActivationDefinition activation,
        IReadOnlyList<LoreBookDirective> directives,
        PreservedJsonFields preservedFields)
    {
        Identifier = identifier;
        IdentifierKind = identifierKind;
        Name = name;
        Comment = comment;
        Content = content;
        InsertionOrder = insertionOrder;
        Priority = priority;
        Position = position;
        SourcePosition = sourcePosition;
        Activation = activation;
        Directives = directives;
        PreservedFields = preservedFields;
    }

    public string? Identifier { get; }

    public LoreBookEntryIdentifierKind IdentifierKind { get; }

    public string? Name { get; }

    public string? Comment { get; }

    public string Content { get; }

    public int InsertionOrder { get; }

    public double? Priority { get; }

    public LoreBookPosition Position { get; }

    public string SourcePosition { get; }

    public LoreBookActivationDefinition Activation { get; }

    public IReadOnlyList<LoreBookDirective> Directives { get; }

    public PreservedJsonFields PreservedFields { get; }
}

public sealed class LoreBookDefinition
{
    internal LoreBookDefinition(
        CompatibilitySourceFormat sourceFormat,
        string sourceVersion,
        string? name,
        string? description,
        int? scanDepth,
        int? tokenBudget,
        bool? recursiveScanning,
        IReadOnlyList<LoreBookEntryDefinition> entries,
        PreservedJsonFields preservedFields)
    {
        SourceFormat = sourceFormat;
        SourceVersion = sourceVersion;
        Name = name;
        Description = description;
        ScanDepth = scanDepth;
        TokenBudget = tokenBudget;
        RecursiveScanning = recursiveScanning;
        Entries = entries;
        PreservedFields = preservedFields;
    }

    public CompatibilitySourceFormat SourceFormat { get; }

    public CompatibilityContentTrust ContentTrust => CompatibilityContentTrust.UntrustedData;

    public string SourceVersion { get; }

    public string? Name { get; }

    public string? Description { get; }

    public int? ScanDepth { get; }

    public int? TokenBudget { get; }

    public bool? RecursiveScanning { get; }

    public IReadOnlyList<LoreBookEntryDefinition> Entries { get; }

    public PreservedJsonFields PreservedFields { get; }
}
