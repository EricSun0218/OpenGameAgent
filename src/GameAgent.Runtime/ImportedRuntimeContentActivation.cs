using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Compatibility;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Runtime;

/// <summary>
/// Host-owned policy that accepts imported content as data and binds any
/// derived memories to one game scope. Acceptance never grants model, tool,
/// skill, extension, or system-prompt authority.
/// </summary>
public sealed class ImportedRuntimeActivationPolicy
{
    public ImportedRuntimeActivationPolicy(
        ImportedContentAcceptance acceptance,
        string worldId,
        string memoryScope,
        DateTimeOffset recordedAt,
        string? timelineId = null,
        string? sessionId = null,
        long saveRevision = 0,
        GameKnowledgePerspective? perspective = null,
        bool activateEmbeddedKnowledge = true,
        int maxPersonaUtf8Bytes = 65_536,
        int maxKnowledgeEntries = 4_096,
        int maxKnowledgeEntryUtf8Bytes = 131_072,
        int maxTotalKnowledgeUtf8Bytes = 8 * 1024 * 1024)
    {
        Acceptance = acceptance;
        WorldId = Required(worldId, 128, nameof(worldId));
        MemoryScope = Required(memoryScope, 256, nameof(memoryScope));
        RecordedAt = recordedAt;
        TimelineId = Optional(timelineId, 128, nameof(timelineId));
        SessionId = Optional(sessionId, 128, nameof(sessionId));
        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        if (maxPersonaUtf8Bytes is < 1 or > 131_072)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPersonaUtf8Bytes));
        }

        if (maxKnowledgeEntries is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxKnowledgeEntries));
        }

        if (maxKnowledgeEntryUtf8Bytes is < 1 or > 131_072)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxKnowledgeEntryUtf8Bytes));
        }

        if (maxTotalKnowledgeUtf8Bytes
            is < 1 or > MemoryBatchLimits.MaxAggregateContentUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalKnowledgeUtf8Bytes));
        }

        SaveRevision = saveRevision;
        Perspective = perspective;
        ActivateEmbeddedKnowledge = activateEmbeddedKnowledge;
        MaxPersonaUtf8Bytes = maxPersonaUtf8Bytes;
        MaxKnowledgeEntries = maxKnowledgeEntries;
        MaxKnowledgeEntryUtf8Bytes = maxKnowledgeEntryUtf8Bytes;
        MaxTotalKnowledgeUtf8Bytes = maxTotalKnowledgeUtf8Bytes;
    }

    public ImportedContentAcceptance Acceptance { get; }

    public string WorldId { get; }

    public string MemoryScope { get; }

    public DateTimeOffset RecordedAt { get; }

    public string? TimelineId { get; }

    public string? SessionId { get; }

    public long SaveRevision { get; }

    public GameKnowledgePerspective? Perspective { get; }

    public bool ActivateEmbeddedKnowledge { get; }

    public int MaxPersonaUtf8Bytes { get; }

    public int MaxKnowledgeEntries { get; }

    public int MaxKnowledgeEntryUtf8Bytes { get; }

    public int MaxTotalKnowledgeUtf8Bytes { get; }

    private static string Required(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }

    private static string? Optional(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        return value is null
            ? null
            : Required(value, maximumUtf8Bytes, parameterName);
    }

    private static UTF8Encoding StrictUtf8 { get; } =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

public sealed class ImportedRuntimeActivationDiagnostic
{
    internal ImportedRuntimeActivationDiagnostic(
        string code,
        CompatibilityDiagnosticSeverity severity,
        string path,
        string message)
    {
        Code = code;
        Severity = severity;
        Path = path;
        Message = message;
    }

    public string Code { get; }

    public CompatibilityDiagnosticSeverity Severity { get; }

    public string Path { get; }

    public string Message { get; }
}

/// <summary>
/// Host-projected, game-time-scoped text used only for deterministic literal
/// lore-key matching. Segments are ordered newest first. The runtime never
/// infers this projection from arbitrary game state or from wall-clock time.
/// </summary>
public sealed class ImportedLoreActivationContext
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public ImportedLoreActivationContext(
        string scopeId,
        string gameTimeId,
        IEnumerable<string> searchSegments,
        int? defaultScanDepth = null,
        bool defaultCaseSensitive = false,
        bool defaultMatchWholeWords = false,
        int maxSearchSegments = 256,
        int maxSegmentUtf8Bytes = 32_768,
        int maxTotalUtf8Bytes = 262_144)
    {
        ScopeId = Required(scopeId, 256, nameof(scopeId));
        GameTimeId = Required(gameTimeId, 256, nameof(gameTimeId));
        if (searchSegments is null)
        {
            throw new ArgumentNullException(nameof(searchSegments));
        }

        if (maxSearchSegments is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSearchSegments));
        }

        if (maxSegmentUtf8Bytes is < 1 or > 131_072)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSegmentUtf8Bytes));
        }

        if (maxTotalUtf8Bytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalUtf8Bytes));
        }

        if (defaultScanDepth is < 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultScanDepth));
        }

        var snapshot = new List<string>(
            Math.Min(
                maxSearchSegments,
                searchSegments is ICollection<string> collection
                    ? collection.Count
                    : 4));
        var totalBytes = 0;
        using var enumerator = searchSegments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (snapshot.Count == maxSearchSegments)
            {
                throw new RuntimeContentLimitException(
                    nameof(searchSegments),
                    "lore_activation_search_segment_count_exceeded",
                    "Lore activation search context exceeds the host "
                    + "segment limit.");
            }

            var segment = enumerator.Current
                          ?? throw new ArgumentException(
                              "Search segments cannot contain null.",
                              nameof(searchSegments));
            var bytes = StrictUtf8.GetByteCount(segment);
            if (bytes > maxSegmentUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(searchSegments),
                    "lore_activation_search_segment_bytes_exceeded",
                    "A lore activation search segment exceeds the host "
                    + "byte limit.");
            }

            totalBytes = checked(totalBytes + bytes);
            if (totalBytes > maxTotalUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(searchSegments),
                    "lore_activation_search_total_bytes_exceeded",
                    "Lore activation search context exceeds the host "
                    + "aggregate byte limit.");
            }

            snapshot.Add(segment);
        }

        SearchSegments = new ReadOnlyCollection<string>(
            snapshot.ToArray());
        DefaultScanDepth = defaultScanDepth;
        DefaultCaseSensitive = defaultCaseSensitive;
        DefaultMatchWholeWords = defaultMatchWholeWords;
        ContextDigest = ComputeDigest();
    }

    public string ScopeId { get; }

    /// <summary>
    /// Opaque, stable game-defined coordinate such as "month:42". It is not a
    /// chat-turn number or a wall-clock timestamp.
    /// </summary>
    public string GameTimeId { get; }

    public IReadOnlyList<string> SearchSegments { get; }

    /// <summary>
    /// Number of newest-first segments to scan. Null scans every supplied
    /// segment; zero scans none.
    /// </summary>
    public int? DefaultScanDepth { get; }

    public bool DefaultCaseSensitive { get; }

    public bool DefaultMatchWholeWords { get; }

    public string ContextDigest { get; }

    private string ComputeDigest()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("scopeId", ScopeId);
            writer.WriteString("gameTimeId", GameTimeId);
            WriteOptionalNumber(
                writer,
                "defaultScanDepth",
                DefaultScanDepth);
            writer.WriteBoolean(
                "defaultCaseSensitive",
                DefaultCaseSensitive);
            writer.WriteBoolean(
                "defaultMatchWholeWords",
                DefaultMatchWholeWords);
            writer.WritePropertyName("searchSegments");
            writer.WriteStartArray();
            foreach (var segment in SearchSegments)
            {
                writer.WriteStringValue(segment);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(output.ToArray());
        var result = new StringBuilder(64);
        foreach (var item in digest)
        {
            result.Append(
                item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static string Required(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string name,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}

public enum ImportedKnowledgeActivationDecision
{
    Disabled,
    Constant,
    KeywordMatched,
    KeywordContextRequired,
    PrimaryKeyNotMatched,
    SecondaryKeyRejected,
    MissingActivationKey,
    UnsupportedSemantics,
}

/// <summary>
/// One imported knowledge entry and its optional active memory form. Disabled
/// or unaddressable entries remain inspectable data but are not written to a
/// runtime memory store.
/// </summary>
public sealed class ImportedKnowledgeEntryActivation
{
    internal ImportedKnowledgeEntryActivation(
        string entryId,
        int insertionOrder,
        double? priority,
        bool enabled,
        bool alwaysActive,
        bool keyed,
        ImportedKnowledgeActivationDecision decision,
        MemoryRecord? memory)
    {
        EntryId = entryId;
        InsertionOrder = insertionOrder;
        Priority = priority;
        Enabled = enabled;
        AlwaysActive = alwaysActive;
        Keyed = keyed;
        Decision = decision;
        Memory = memory;
    }

    public string EntryId { get; }

    public int InsertionOrder { get; }

    public double? Priority { get; }

    public bool Enabled { get; }

    public bool AlwaysActive { get; }

    public bool Keyed { get; }

    public ImportedKnowledgeActivationDecision Decision { get; }

    public bool IsActive => Memory is not null;

    public MemoryRecord? Memory { get; }
}

public sealed class ImportedKnowledgeActivation
{
    private readonly IReadOnlyList<MemoryRecord> _memories;

    internal ImportedKnowledgeActivation(
        string sourceDigest,
        string activationDigest,
        IReadOnlyList<ImportedKnowledgeEntryActivation> entries,
        IReadOnlyList<MemoryRecord> memories,
        IReadOnlyList<ImportedRuntimeActivationDiagnostic> diagnostics,
        ImportedLoreActivationContext? activationContext)
    {
        SourceDigest = sourceDigest;
        ActivationDigest = activationDigest;
        Entries = entries;
        _memories = memories;
        Memories = memories;
        Diagnostics = diagnostics;
        ActivationContextDigest = activationContext?.ContextDigest;
        ActivationScopeId = activationContext?.ScopeId;
        GameTimeId = activationContext?.GameTimeId;
    }

    public string SourceDigest { get; }

    public string ActivationDigest { get; }

    public IReadOnlyList<ImportedKnowledgeEntryActivation> Entries { get; }

    public IReadOnlyList<MemoryRecord> Memories { get; }

    public IReadOnlyList<ImportedRuntimeActivationDiagnostic> Diagnostics
    {
        get;
    }

    public string? ActivationContextDigest { get; }

    public string? ActivationScopeId { get; }

    public string? GameTimeId { get; }

    /// <summary>
    /// Writes stable upserts to a host-selected store. Atomic and idempotent
    /// batch capabilities are used when the store exposes them; otherwise
    /// stable memory ids make a retry safe after a partial interruption.
    /// </summary>
    public async ValueTask WriteToAsync(
        IMemoryStore store,
        CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        if (_memories.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (_memories.Count <= MemoryBatchLimits.MaxMutations)
        {
            var mutations = _memories
                .Select(MemoryMutation.Upsert)
                .ToArray();
            if (store is IIdempotentAtomicMemoryBatchStore idempotent)
            {
                _ = await idempotent.ApplyIdempotentAtomicBatchAsync(
                        "import-" + ActivationDigest,
                        mutations,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (store is IAtomicMemoryBatchStore atomic)
            {
                _ = await atomic.ApplyAtomicBatchAsync(
                        mutations,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        foreach (var memory in _memories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpsertAsync(memory, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

public sealed class ImportedAgentActivation
{
    internal ImportedAgentActivation(
        string sourceDigest,
        AgentDefinition agentDefinition,
        ContextCandidate personaContext,
        ImportedKnowledgeActivation? embeddedKnowledge,
        IReadOnlyList<ImportedRuntimeActivationDiagnostic> diagnostics)
    {
        SourceDigest = sourceDigest;
        AgentDefinition = agentDefinition;
        PersonaContext = personaContext;
        EmbeddedKnowledge = embeddedKnowledge;
        Diagnostics = diagnostics;
    }

    public string SourceDigest { get; }

    /// <summary>
    /// Definition derived from identity data only. Its toolsets and skills are
    /// always empty; a trusted host may add permissions with
    /// <see cref="AgentProfileBuilder"/>.
    /// </summary>
    public AgentDefinition AgentDefinition { get; }

    /// <summary>
    /// Stable, deferable, non-authoritative persona data suitable for a
    /// <see cref="DurableRunRequest"/>.
    /// </summary>
    public ContextCandidate PersonaContext { get; }

    public ImportedKnowledgeActivation? EmbeddedKnowledge { get; }

    public IReadOnlyList<ImportedRuntimeActivationDiagnostic> Diagnostics
    {
        get;
    }
}

/// <summary>
/// Converts admitted compatibility data into bounded runtime artifacts. The
/// converter is deliberately policy-free about gameplay and deliberately
/// incapable of granting executable authority.
/// </summary>
public sealed class ImportedRuntimeContentActivator
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public ImportedAgentActivation ActivateCharacter(
        string contentId,
        CompatibilityImportResult<CharacterDefinition> import,
        ImportedRuntimeActivationPolicy policy,
        ImportedLoreActivationContext? loreActivationContext = null)
    {
        var character = EnsureAccepted(import, policy, nameof(import));
        var safeContentId = Required(contentId, 256, nameof(contentId));
        var diagnostics = CopyDiagnostics(import.Diagnostics);

        var sourceDigest = SourceDigest(
            import.SourceDigest,
            ComputeCharacterFallbackDigest(
                safeContentId,
                character,
                import.AdapterId,
                import.AdapterVersion),
            diagnostics,
            "$");
        var persona = WritePersona(
            safeContentId,
            character,
            import,
            sourceDigest);
        EnsureBytes(
            persona,
            policy.MaxPersonaUtf8Bytes,
            "imported_persona_byte_limit_exceeded");

        var identity = WriteIdentity(
            safeContentId,
            character,
            import,
            sourceDigest);
        var identityDigest = CanonicalJsonDigest.ComputeSha256(identity);
        var definition = new AgentDefinition
        {
            AgentDefinitionId =
                "imported-character-" + identityDigest[..32],
            Version = "1",
            Identity = identity,
            BehaviorPolicyRef = null,
            Toolsets = new List<string>(),
            Skills = new List<string>(),
            ContextPolicyRef = null,
            MemoryPolicyRef = null,
            ProviderPolicyRef = null,
            Budgets = ParseObject("{}")
        };
        ProtocolValidator.EnsureValid(definition);

        var context = new ContextCandidate(
            "imported-persona-" + identityDigest[..32],
            "imported_persona",
            persona,
            priority: 1_000,
            required: false,
            canDefer: true,
            provenance:
                "imported:untrusted_data:sha256:" + sourceDigest);

        ImportedKnowledgeActivation? embedded = null;
        if (policy.ActivateEmbeddedKnowledge
            && character.CharacterLoreBook is not null)
        {
            embedded = ActivateLoreBookCore(
                safeContentId + ".embedded",
                character.CharacterLoreBook,
                import.AdapterId,
                import.AdapterVersion,
                sourceDigest,
                policy,
                diagnosticsPrefix: "$.embeddedKnowledge",
                activationContext: loreActivationContext);
        }

        diagnostics.Add(
            Diagnostic(
                "imported_content_accepted_as_untrusted_data",
                CompatibilityDiagnosticSeverity.Info,
                "$",
                "The host accepted imported character data without "
                + "granting executable authority."));
        if (!string.IsNullOrEmpty(character.SystemPrompt)
            || !string.IsNullOrEmpty(
                character.PostHistoryInstructions))
        {
            diagnostics.Add(
                Diagnostic(
                    "source_instruction_fields_are_data",
                    CompatibilityDiagnosticSeverity.Info,
                    "$.authoredPersonaData",
                    "Source instruction-shaped fields remain untrusted "
                    + "persona data."));
        }

        if (embedded is not null)
        {
            diagnostics.AddRange(embedded.Diagnostics);
        }

        return new ImportedAgentActivation(
            sourceDigest,
            definition,
            context,
            embedded,
            SortDiagnostics(diagnostics));
    }

    public ImportedKnowledgeActivation ActivateLoreBook(
        string contentId,
        CompatibilityImportResult<LoreBookDefinition> import,
        ImportedRuntimeActivationPolicy policy,
        ImportedLoreActivationContext? activationContext = null)
    {
        var loreBook = EnsureAccepted(import, policy, nameof(import));
        var safeContentId = Required(contentId, 256, nameof(contentId));
        var diagnostics = CopyDiagnostics(import.Diagnostics);
        var sourceDigest = SourceDigest(
            import.SourceDigest,
            ComputeLoreFallbackDigest(
                safeContentId,
                loreBook,
                import.AdapterId,
                import.AdapterVersion),
            diagnostics,
            "$");
        return ActivateLoreBookCore(
            safeContentId,
            loreBook,
            import.AdapterId,
            import.AdapterVersion,
            sourceDigest,
            policy,
            diagnosticsPrefix: "$",
            initialDiagnostics: diagnostics,
            activationContext: activationContext);
    }

    private static ImportedKnowledgeActivation ActivateLoreBookCore(
        string contentId,
        LoreBookDefinition loreBook,
        string? adapterId,
        string? adapterVersion,
        string sourceDigest,
        ImportedRuntimeActivationPolicy policy,
        string diagnosticsPrefix,
        List<ImportedRuntimeActivationDiagnostic>? initialDiagnostics = null,
        ImportedLoreActivationContext? activationContext = null)
    {
        if (loreBook.Entries.Count > policy.MaxKnowledgeEntries)
        {
            throw new RuntimeContentLimitException(
                nameof(loreBook),
                "imported_knowledge_entry_count_exceeded",
                "Imported knowledge exceeds the host activation limit.");
        }

        var diagnostics =
            initialDiagnostics
            ?? new List<ImportedRuntimeActivationDiagnostic>();
        if (loreBook.RecursiveScanning == true)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_recursive_scanning_unsupported",
                    CompatibilityDiagnosticSeverity.Warning,
                    diagnosticsPrefix + ".recursiveScanning",
                    "Recursive lore activation is not evaluated in this "
                    + "compatibility phase. Only the supplied game context "
                    + "is scanned."));
        }

        if (loreBook.TokenBudget.HasValue)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_token_budget_not_enforced",
                    CompatibilityDiagnosticSeverity.Info,
                    diagnosticsPrefix + ".tokenBudget",
                    "The imported token budget remains metadata. The host "
                    + "context compiler owns the authoritative budget."));
        }

        var pending = new List<PendingEntry>(loreBook.Entries.Count);
        var totalBytes = 0;
        for (var index = 0; index < loreBook.Entries.Count; index++)
        {
            var entry = loreBook.Entries[index];
            EnsureFinite(entry.Priority, nameof(entry.Priority));
            EnsureFinite(
                entry.Activation.Probability,
                nameof(entry.Activation.Probability));
            var entryJson = WriteKnowledgeEntry(
                contentId,
                loreBook,
                entry,
                index,
                adapterId,
                adapterVersion,
                sourceDigest);
            var entryBytes = StrictUtf8.GetByteCount(entryJson.GetRawText());
            if (entryBytes > policy.MaxKnowledgeEntryUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(loreBook),
                    "imported_knowledge_entry_byte_limit_exceeded",
                    "An imported knowledge entry exceeds the host "
                    + "activation limit.");
            }

            var keyed = entry.Activation.PrimaryKeys.Any(
                static key => !string.IsNullOrWhiteSpace(key));
            var path = diagnosticsPrefix + ".entries[" + index
                .ToString(CultureInfo.InvariantCulture) + "]";
            var decision = EvaluateEntry(
                loreBook,
                entry,
                activationContext,
                path,
                diagnostics);
            var active =
                decision is ImportedKnowledgeActivationDecision.Constant
                    or ImportedKnowledgeActivationDecision.KeywordMatched;
            if (active)
            {
                totalBytes = checked(totalBytes + entryBytes);
                if (totalBytes > policy.MaxTotalKnowledgeUtf8Bytes)
                {
                    throw new RuntimeContentLimitException(
                        nameof(loreBook),
                        "imported_knowledge_total_byte_limit_exceeded",
                        "Imported knowledge exceeds the host aggregate "
                        + "activation limit.");
                }
            }

            var entryDigest = CanonicalJsonDigest.ComputeSha256(entryJson);
            var memoryIdentityDigest = ComputeMemoryIdentityDigest(
                entryDigest,
                policy);
            MemoryRecord? record = null;
            if (active)
            {
                record = new MemoryRecord(
                    "imported-lore-" + memoryIdentityDigest,
                    policy.MemoryScope,
                    entryJson,
                    Tags(entry),
                    Importance(entry.Priority),
                    policy.RecordedAt,
                    policy.RecordedAt,
                    expiresAt: null,
                    provenance: new MemoryProvenance(
                        policy.WorldId,
                        policy.SessionId,
                        policy.SaveRevision,
                        "import-activation-" + sourceDigest[..32],
                        "import-entry-" + memoryIdentityDigest[..32],
                        committed: true,
                        timelineId: policy.TimelineId,
                        perspective: policy.Perspective),
                    gameTimeWindow: null);
            }
            pending.Add(
                new PendingEntry(
                    new ImportedKnowledgeEntryActivation(
                        entryDigest,
                        entry.InsertionOrder,
                        entry.Priority,
                        entry.Activation.Enabled,
                        entry.Activation.AlwaysActive,
                        keyed,
                        decision,
                        record),
                    record));
        }

        pending.Sort(
            static (left, right) =>
            {
                var order = left.Activation.InsertionOrder.CompareTo(
                    right.Activation.InsertionOrder);
                if (order != 0)
                {
                    return order;
                }

                var priority = Nullable.Compare(
                    right.Activation.Priority,
                    left.Activation.Priority);
                return priority != 0
                    ? priority
                    : StringComparer.Ordinal.Compare(
                        left.Activation.EntryId,
                        right.Activation.EntryId);
            });

        var entries = pending.Select(item => item.Activation).ToArray();
        var memories = pending
            .Where(item => item.Memory is not null)
            .Select(item => item.Memory!)
            .ToArray();
        var activationDigest = ComputeActivationDigest(
            sourceDigest,
            policy,
            memories,
            activationContext);
        diagnostics.Add(
            Diagnostic(
                "imported_content_accepted_as_untrusted_data",
                CompatibilityDiagnosticSeverity.Info,
                diagnosticsPrefix,
                "The host accepted imported knowledge as scoped, "
                + "non-authoritative memory data."));
        diagnostics.Add(
            Diagnostic(
                "source_directives_are_metadata",
                CompatibilityDiagnosticSeverity.Info,
                diagnosticsPrefix + ".entries",
                "Source directives remain untrusted metadata and cannot "
                + "grant system, tool, skill, or extension authority."));

        return new ImportedKnowledgeActivation(
            sourceDigest,
            activationDigest,
            new ReadOnlyCollection<ImportedKnowledgeEntryActivation>(
                entries),
            new ReadOnlyCollection<MemoryRecord>(memories),
            SortDiagnostics(diagnostics),
            activationContext);
    }

    private static JsonElement WritePersona(
        string contentId,
        CharacterDefinition character,
        CompatibilityImportResult<CharacterDefinition> import,
        string? sourceDigest)
    {
        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "contract",
                    "game-agent.imported-persona-context.v1");
                WriteTrustBoundary(writer);
                writer.WriteString("contentId", contentId);
                WriteSource(
                    writer,
                    character.SourceFormat.ToString(),
                    character.SourceVersion,
                    import.AdapterId,
                    import.AdapterVersion,
                    sourceDigest);
                writer.WritePropertyName("identity");
                writer.WriteStartObject();
                writer.WriteString("name", character.Name);
                WriteOptional(writer, "nickname", character.Nickname);
                WriteStrings(writer, "tags", character.Tags);
                writer.WriteEndObject();
                writer.WritePropertyName("authoredPersonaData");
                writer.WriteStartObject();
                writer.WriteString("description", character.Description);
                writer.WriteString("personality", character.Personality);
                writer.WriteString("scenario", character.Scenario);
                writer.WriteString("firstMessage", character.FirstMessage);
                writer.WriteString(
                    "exampleMessages",
                    character.ExampleMessages);
                writer.WriteString(
                    "creatorNotes",
                    character.CreatorNotes);
                writer.WriteString(
                    "sourceSystemPrompt",
                    character.SystemPrompt);
                writer.WriteString(
                    "sourcePostHistoryInstructions",
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
                writer.WriteEndObject();
            });
    }

    private static JsonElement WriteIdentity(
        string contentId,
        CharacterDefinition character,
        CompatibilityImportResult<CharacterDefinition> import,
        string sourceDigest)
    {
        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "contract",
                    "game-agent.imported-character-identity.v1");
                WriteTrustBoundary(writer);
                writer.WriteString("contentId", contentId);
                writer.WriteString("sourceDigest", sourceDigest);
                WriteOptional(writer, "adapterId", import.AdapterId);
                WriteOptional(
                    writer,
                    "adapterVersion",
                    import.AdapterVersion);
                writer.WriteString(
                    "sourceFormat",
                    character.SourceFormat.ToString());
                writer.WriteString(
                    "sourceVersion",
                    character.SourceVersion);
                writer.WriteString("name", character.Name);
                WriteOptional(writer, "nickname", character.Nickname);
                WriteStrings(writer, "tags", character.Tags);
                writer.WriteEndObject();
            });
    }

    private static JsonElement WriteKnowledgeEntry(
        string contentId,
        LoreBookDefinition loreBook,
        LoreBookEntryDefinition entry,
        int sourceIndex,
        string? adapterId,
        string? adapterVersion,
        string sourceDigest)
    {
        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "contract",
                    "game-agent.imported-knowledge-entry.v1");
                WriteTrustBoundary(writer);
                writer.WriteString("contentId", contentId);
                WriteSource(
                    writer,
                    loreBook.SourceFormat.ToString(),
                    loreBook.SourceVersion,
                    adapterId,
                    adapterVersion,
                    sourceDigest);
                writer.WritePropertyName("book");
                writer.WriteStartObject();
                WriteOptional(writer, "name", loreBook.Name);
                WriteOptional(
                    writer,
                    "description",
                    loreBook.Description);
                WriteOptionalNumber(
                    writer,
                    "scanDepth",
                    loreBook.ScanDepth);
                WriteOptionalNumber(
                    writer,
                    "tokenBudget",
                    loreBook.TokenBudget);
                WriteOptionalBoolean(
                    writer,
                    "recursiveScanning",
                    loreBook.RecursiveScanning);
                writer.WriteEndObject();
                writer.WriteNumber("sourceIndex", sourceIndex);
                WriteOptional(writer, "identifier", entry.Identifier);
                writer.WriteString(
                    "identifierKind",
                    entry.IdentifierKind.ToString());
                WriteOptional(writer, "name", entry.Name);
                WriteOptional(writer, "comment", entry.Comment);
                writer.WriteString("content", entry.Content);
                writer.WriteNumber(
                    "insertionOrder",
                    entry.InsertionOrder);
                WriteOptionalDoubleString(
                    writer,
                    "priority",
                    entry.Priority);
                writer.WriteString(
                    "position",
                    entry.Position.ToString());
                writer.WriteString(
                    "sourcePosition",
                    entry.SourcePosition);
                writer.WritePropertyName("activation");
                writer.WriteStartObject();
                writer.WriteBoolean(
                    "enabled",
                    entry.Activation.Enabled);
                writer.WriteBoolean(
                    "alwaysActive",
                    entry.Activation.AlwaysActive);
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
                WriteOptionalDoubleString(
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
                writer.WritePropertyName("sourceDirectives");
                writer.WriteStartArray();
                foreach (var directive in entry.Directives)
                {
                    writer.WriteStartObject();
                    writer.WriteString("contentTrust", "untrusted_data");
                    writer.WriteString("authority", "none");
                    writer.WriteString("name", directive.Name);
                    writer.WriteString("value", directive.Value);
                    writer.WriteBoolean(
                        "isFallback",
                        directive.IsFallback);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
    }

    private static void WriteSource(
        Utf8JsonWriter writer,
        string sourceFormat,
        string sourceVersion,
        string? adapterId,
        string? adapterVersion,
        string? sourceDigest)
    {
        writer.WritePropertyName("source");
        writer.WriteStartObject();
        writer.WriteString("format", sourceFormat);
        writer.WriteString("version", sourceVersion);
        WriteOptional(writer, "adapterId", adapterId);
        WriteOptional(writer, "adapterVersion", adapterVersion);
        WriteOptional(writer, "sha256", sourceDigest);
        writer.WriteEndObject();
    }

    private static void WriteTrustBoundary(Utf8JsonWriter writer)
    {
        writer.WriteString("contentTrust", "untrusted_data");
        writer.WriteString("authority", "none");
    }

    private static ImportedKnowledgeActivationDecision EvaluateEntry(
        LoreBookDefinition loreBook,
        LoreBookEntryDefinition entry,
        ImportedLoreActivationContext? activationContext,
        string path,
        ICollection<ImportedRuntimeActivationDiagnostic> diagnostics)
    {
        if (!entry.Activation.Enabled)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_disabled",
                    CompatibilityDiagnosticSeverity.Info,
                    path,
                    "The disabled entry remains inspectable and was not "
                    + "activated."));
            return ImportedKnowledgeActivationDecision.Disabled;
        }

        var unsupported = false;
        if (entry.Activation.Probability.HasValue
            && entry.Activation.Probability.Value != 1d)
        {
            unsupported = true;
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_probability_unsupported",
                    CompatibilityDiagnosticSeverity.Warning,
                    path + ".activation.probability",
                    "Probabilistic activation is not evaluated in this "
                    + "compatibility phase. The entry fails closed."));
        }

        unsupported |= AddUnsupportedTurnDiagnostic(
            entry.Activation.StickyTurns,
            "knowledge_entry_sticky_turns_unsupported",
            path + ".activation.stickyTurns",
            "Sticky activation",
            diagnostics);
        unsupported |= AddUnsupportedTurnDiagnostic(
            entry.Activation.CooldownTurns,
            "knowledge_entry_cooldown_turns_unsupported",
            path + ".activation.cooldownTurns",
            "Cooldown activation",
            diagnostics);
        unsupported |= AddUnsupportedTurnDiagnostic(
            entry.Activation.DelayTurns,
            "knowledge_entry_delay_turns_unsupported",
            path + ".activation.delayTurns",
            "Delayed activation",
            diagnostics);

        if (entry.Activation.AlwaysActive)
        {
            return unsupported
                ? ImportedKnowledgeActivationDecision.UnsupportedSemantics
                : ImportedKnowledgeActivationDecision.Constant;
        }

        var primaryKeys = entry.Activation.PrimaryKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToArray();
        if (primaryKeys.Length == 0)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_has_no_activation_key",
                    CompatibilityDiagnosticSeverity.Info,
                    path + ".activation.primaryKeys",
                    "The entry remains inspectable but is neither constant "
                    + "nor associated with a usable primary key."));
            return unsupported
                ? ImportedKnowledgeActivationDecision.UnsupportedSemantics
                : ImportedKnowledgeActivationDecision.MissingActivationKey;
        }

        if (entry.Activation.MatchMode
            == LoreBookMatchMode.RegularExpression)
        {
            unsupported = true;
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_regular_expression_unsupported",
                    CompatibilityDiagnosticSeverity.Warning,
                    path + ".activation.matchMode",
                    "Regular-expression keys are preserved but not "
                    + "executed in this compatibility phase. The entry "
                    + "fails closed."));
        }

        if (activationContext is null)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_keyword_context_required",
                    CompatibilityDiagnosticSeverity.Info,
                    path + ".activation.primaryKeys",
                    "Keyed lore requires an explicit host-projected game "
                    + "context and was not activated."));
            return unsupported
                ? ImportedKnowledgeActivationDecision.UnsupportedSemantics
                : ImportedKnowledgeActivationDecision.KeywordContextRequired;
        }

        if (unsupported)
        {
            return ImportedKnowledgeActivationDecision.UnsupportedSemantics;
        }

        var scanDepth = entry.Activation.ScanDepth
                        ?? loreBook.ScanDepth
                        ?? activationContext.DefaultScanDepth;
        var segmentCount = scanDepth.HasValue
            ? Math.Min(scanDepth.Value, activationContext.SearchSegments.Count)
            : activationContext.SearchSegments.Count;
        var caseSensitive = entry.Activation.CaseSensitive
                            ?? activationContext.DefaultCaseSensitive;
        var matchWholeWords = entry.Activation.MatchWholeWords
                              ?? activationContext.DefaultMatchWholeWords;
        var primaryMatched = primaryKeys.Any(
            key => Matches(
                key,
                activationContext.SearchSegments,
                segmentCount,
                caseSensitive,
                matchWholeWords));
        if (!primaryMatched)
        {
            diagnostics.Add(
                Diagnostic(
                    "knowledge_entry_primary_key_not_matched",
                    CompatibilityDiagnosticSeverity.Info,
                    path + ".activation.primaryKeys",
                    "No literal primary key matched the bounded game "
                    + "context."));
            return ImportedKnowledgeActivationDecision.PrimaryKeyNotMatched;
        }

        var secondaryKeys = entry.Activation.SecondaryKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToArray();
        if (!entry.Activation.RequireSecondaryKey
            || secondaryKeys.Length == 0)
        {
            return ImportedKnowledgeActivationDecision.KeywordMatched;
        }

        var secondaryMatchCount = secondaryKeys.Count(
            key => Matches(
                key,
                activationContext.SearchSegments,
                segmentCount,
                caseSensitive,
                matchWholeWords));
        var secondaryAccepted = entry.Activation.SecondaryKeyLogic switch
        {
            LoreBookSecondaryKeyLogic.Any => secondaryMatchCount > 0,
            LoreBookSecondaryKeyLogic.All =>
                secondaryMatchCount == secondaryKeys.Length,
            LoreBookSecondaryKeyLogic.NotAny => secondaryMatchCount == 0,
            LoreBookSecondaryKeyLogic.NotAll =>
                secondaryMatchCount < secondaryKeys.Length,
            _ => false
        };
        if (secondaryAccepted)
        {
            return ImportedKnowledgeActivationDecision.KeywordMatched;
        }

        diagnostics.Add(
            Diagnostic(
                "knowledge_entry_secondary_key_rejected",
                CompatibilityDiagnosticSeverity.Info,
                path + ".activation.secondaryKeys",
                "The literal primary key matched, but the configured "
                + "secondary-key logic rejected the entry."));
        return ImportedKnowledgeActivationDecision.SecondaryKeyRejected;
    }

    private static bool AddUnsupportedTurnDiagnostic(
        int? turns,
        string code,
        string path,
        string feature,
        ICollection<ImportedRuntimeActivationDiagnostic> diagnostics)
    {
        if (!turns.HasValue || turns.Value == 0)
        {
            return false;
        }

        diagnostics.Add(
            Diagnostic(
                code,
                CompatibilityDiagnosticSeverity.Warning,
                path,
                feature + " requires durable game-time state and is not "
                + "evaluated in this compatibility phase. The entry fails "
                + "closed."));
        return true;
    }

    private static bool Matches(
        string key,
        IReadOnlyList<string> segments,
        int segmentCount,
        bool caseSensitive,
        bool matchWholeWords)
    {
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        for (var index = 0; index < segmentCount; index++)
        {
            var segment = segments[index];
            var matchIndex = 0;
            while (matchIndex <= segment.Length - key.Length)
            {
                matchIndex = segment.IndexOf(
                    key,
                    matchIndex,
                    comparison);
                if (matchIndex < 0)
                {
                    break;
                }

                if (!matchWholeWords
                    || HasWordBoundaries(
                        segment,
                        matchIndex,
                        key.Length))
                {
                    return true;
                }

                matchIndex++;
            }
        }

        return false;
    }

    private static bool HasWordBoundaries(
        string value,
        int start,
        int length)
    {
        var beforeIsWord = start > 0 && IsWordCharacter(value[start - 1]);
        var end = start + length;
        var afterIsWord =
            end < value.Length && IsWordCharacter(value[end]);
        return !beforeIsWord && !afterIsWord;
    }

    private static bool IsWordCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static string[] Tags(LoreBookEntryDefinition entry)
    {
        var tags = new List<string>
        {
            "imported",
            "knowledge",
            "untrusted_data",
            entry.Activation.AlwaysActive
                ? "activation:always"
                : "activation:keyed"
        };
        return tags.ToArray();
    }

    private static int Importance(double? priority)
    {
        if (!priority.HasValue)
        {
            return 50;
        }

        return (int)Math.Round(
            Math.Max(0d, Math.Min(100d, priority.Value)),
            MidpointRounding.AwayFromZero);
    }

    private static string ComputeActivationDigest(
        string sourceDigest,
        ImportedRuntimeActivationPolicy policy,
        IReadOnlyList<MemoryRecord> memories,
        ImportedLoreActivationContext? activationContext)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceDigest", sourceDigest);
            writer.WriteString("worldId", policy.WorldId);
            writer.WriteString("memoryScope", policy.MemoryScope);
            WriteOptional(writer, "timelineId", policy.TimelineId);
            WriteOptional(writer, "sessionId", policy.SessionId);
            WritePerspective(writer, policy.Perspective);
            writer.WriteNumber("saveRevision", policy.SaveRevision);
            WriteOptional(
                writer,
                "activationContextDigest",
                activationContext?.ContextDigest);
            writer.WriteString(
                "recordedAt",
                policy.RecordedAt.ToString(
                    "O",
                    CultureInfo.InvariantCulture));
            writer.WritePropertyName("memories");
            writer.WriteStartArray();
            foreach (var memory in memories)
            {
                writer.WriteStartObject();
                writer.WriteString("memoryId", memory.MemoryId);
                writer.WriteString(
                    "contentDigest",
                    CanonicalJsonDigest.ComputeSha256(memory.Content));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Sha256(output.ToArray());
    }

    private static string ComputeMemoryIdentityDigest(
        string entryDigest,
        ImportedRuntimeActivationPolicy policy)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("entryDigest", entryDigest);
            writer.WriteString("worldId", policy.WorldId);
            writer.WriteString("memoryScope", policy.MemoryScope);
            WriteOptional(writer, "timelineId", policy.TimelineId);
            WriteOptional(writer, "sessionId", policy.SessionId);
            WritePerspective(writer, policy.Perspective);
            writer.WriteEndObject();
        }

        return Sha256(output.ToArray());
    }

    private static void WritePerspective(
        Utf8JsonWriter writer,
        GameKnowledgePerspective? perspective)
    {
        writer.WritePropertyName("perspective");
        if (perspective is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(
            "observerId",
            perspective.Observer.EntityId);
        writer.WriteNumber(
            "observerIncarnation",
            perspective.Observer.Incarnation);
        writer.WriteString(
            "knowledgeKind",
            perspective.KnowledgeKind);
        writer.WritePropertyName("source");
        if (perspective.Source is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString(
                "entityId",
                perspective.Source.EntityId);
            writer.WriteNumber(
                "incarnation",
                perspective.Source.Incarnation);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static T EnsureAccepted<T>(
        CompatibilityImportResult<T>? import,
        ImportedRuntimeActivationPolicy? policy,
        string parameterName)
        where T : class
    {
        if (import is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (policy is null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        if (policy.Acceptance
            != ImportedContentAcceptance.AcceptAsUntrustedData)
        {
            throw new InvalidOperationException(
                "Runtime activation requires explicit host acceptance as "
                + "untrusted data.");
        }

        if (!import.Success || import.Value is null)
        {
            throw new ArgumentException(
                "Only a successful import can be activated.",
                parameterName);
        }

        return import.Value;
    }

    private static string SourceDigest(
        string? declared,
        string fallbackDigest,
        ICollection<ImportedRuntimeActivationDiagnostic> diagnostics,
        string path)
    {
        if (CanonicalJsonDigest.IsSha256(declared))
        {
            return declared!;
        }

        diagnostics.Add(
            Diagnostic(
                "source_digest_derived",
                CompatibilityDiagnosticSeverity.Warning,
                path,
                "The adapter supplied no valid source digest; a stable "
                + "digest was derived from admitted data."));
        return fallbackDigest;
    }

    private static string ComputeCharacterFallbackDigest(
        string contentId,
        CharacterDefinition character,
        string? adapterId,
        string? adapterVersion)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("contentId", contentId);
            writer.WritePropertyName("persona");
            WritePersonaForFallback(
                writer,
                character,
                adapterId,
                adapterVersion);
            writer.WritePropertyName("embeddedKnowledge");
            if (character.CharacterLoreBook is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteLoreForFallback(
                    writer,
                    contentId + ".embedded",
                    character.CharacterLoreBook,
                    adapterId,
                    adapterVersion);
            }

            writer.WriteEndObject();
        }

        return Sha256(output.ToArray());
    }

    private static string ComputeLoreFallbackDigest(
        string contentId,
        LoreBookDefinition loreBook,
        string? adapterId,
        string? adapterVersion)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            WriteLoreForFallback(
                writer,
                contentId,
                loreBook,
                adapterId,
                adapterVersion);
        }

        return Sha256(output.ToArray());
    }

    private static void WritePersonaForFallback(
        Utf8JsonWriter writer,
        CharacterDefinition character,
        string? adapterId,
        string? adapterVersion)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "sourceFormat",
            character.SourceFormat.ToString());
        writer.WriteString("sourceVersion", character.SourceVersion);
        WriteOptional(writer, "adapterId", adapterId);
        WriteOptional(writer, "adapterVersion", adapterVersion);
        writer.WriteString("name", character.Name);
        WriteOptional(writer, "nickname", character.Nickname);
        WriteStrings(writer, "tags", character.Tags);
        writer.WriteString("description", character.Description);
        writer.WriteString("personality", character.Personality);
        writer.WriteString("scenario", character.Scenario);
        writer.WriteString("firstMessage", character.FirstMessage);
        writer.WriteString(
            "exampleMessages",
            character.ExampleMessages);
        writer.WriteString("creatorNotes", character.CreatorNotes);
        writer.WriteString(
            "sourceSystemPrompt",
            character.SystemPrompt);
        writer.WriteString(
            "sourcePostHistoryInstructions",
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
    }

    private static void WriteLoreForFallback(
        Utf8JsonWriter writer,
        string contentId,
        LoreBookDefinition loreBook,
        string? adapterId,
        string? adapterVersion)
    {
        const string digestPlaceholder =
            "0000000000000000000000000000000000000000000000000000000000000000";
        writer.WriteStartObject();
        writer.WriteString("contentId", contentId);
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
        for (var index = 0; index < loreBook.Entries.Count; index++)
        {
            WriteKnowledgeEntry(
                    contentId,
                    loreBook,
                    loreBook.Entries[index],
                    index,
                    adapterId,
                    adapterVersion,
                    digestPlaceholder)
                .WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<ImportedRuntimeActivationDiagnostic> CopyDiagnostics(
        IReadOnlyList<CompatibilityDiagnostic> source)
    {
        const int maximumDiagnostics = 4_096;
        if (source.Count > maximumDiagnostics)
        {
            throw new RuntimeContentLimitException(
                nameof(source),
                "imported_diagnostic_count_exceeded",
                "Imported diagnostics exceed the activation limit.");
        }

        var result = new List<ImportedRuntimeActivationDiagnostic>(
            source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index]
                       ?? throw new ArgumentException(
                           "Imported diagnostics cannot contain null.",
                           nameof(source));
            result.Add(
                Diagnostic(
                    "adapter."
                    + Required(item.Code, 120, nameof(source)),
                    item.Severity,
                    Required(item.Path, 512, nameof(source)),
                    Required(item.Message, 2_048, nameof(source))));
        }

        return result;
    }

    private static IReadOnlyList<ImportedRuntimeActivationDiagnostic>
        SortDiagnostics(
            IEnumerable<ImportedRuntimeActivationDiagnostic> diagnostics)
    {
        return new ReadOnlyCollection<ImportedRuntimeActivationDiagnostic>(
            diagnostics
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Severity)
                .ThenBy(item => item.Message, StringComparer.Ordinal)
                .ToArray());
    }

    private static ImportedRuntimeActivationDiagnostic Diagnostic(
        string code,
        CompatibilityDiagnosticSeverity severity,
        string path,
        string message)
    {
        return new ImportedRuntimeActivationDiagnostic(
            code,
            severity,
            path,
            message);
    }

    private static JsonElement WriteJson(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void EnsureBytes(
        JsonElement value,
        int limit,
        string limitCode)
    {
        if (StrictUtf8.GetByteCount(value.GetRawText()) > limit)
        {
            throw new RuntimeContentLimitException(
                nameof(value),
                limitCode,
                "Imported runtime content exceeds the host activation "
                + "limit.");
        }
    }

    private static void EnsureFinite(double? value, string parameterName)
    {
        if (value.HasValue
            && (double.IsNaN(value.Value)
                || double.IsInfinity(value.Value)))
        {
            throw new ArgumentException(
                "Imported numeric metadata must be finite.",
                parameterName);
        }
    }

    private static string Required(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string name,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalBoolean(
        Utf8JsonWriter writer,
        string name,
        bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteBoolean(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string name,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteOptionalDoubleString(
        Utf8JsonWriter writer,
        string name,
        double? value)
    {
        if (value.HasValue)
        {
            writer.WriteString(
                name,
                value.Value.ToString("R", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string Sha256(byte[] value)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(value);
        var result = new StringBuilder(64);
        foreach (var item in digest)
        {
            result.Append(
                item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private sealed class PendingEntry
    {
        public PendingEntry(
            ImportedKnowledgeEntryActivation activation,
            MemoryRecord? memory)
        {
            Activation = activation;
            Memory = memory;
        }

        public ImportedKnowledgeEntryActivation Activation { get; }

        public MemoryRecord? Memory { get; }
    }
}
