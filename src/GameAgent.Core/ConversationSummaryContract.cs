using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Stable names used by the structured summary emitted by
/// <see cref="ExtractiveConversationCompactor"/>. The contract is encoded as
/// JSON in <see cref="ConversationCompactionResult.SummaryText"/> and remains
/// untrusted historical data after admission.
/// </summary>
public static class ConversationSummaryContract
{
    public const string CurrentVersion = "conversation-summary.v1";

    public const string LatestPendingAskItemKind = "latest-pending-ask";

    public const string ExplicitConstraintItemKind = "explicit-constraint";

    public const string UnfinishedIntentOrCommitmentItemKind =
        "unfinished-intent-or-commitment";

    public const string ExactIdentifierItemKind = "exact-identifier";

    public const string RepresentativeItemKind = "representative";
}

internal static class ConversationSummaryQuality
{
    private const int MaximumSummaryItems = 128;
    private const int MaximumRepresentativeItems = 64;
    private const int MaximumConstraintsToPreserve = 4;
    private const int MaximumIntentsToPreserve = 4;
    private const int MaximumIdentifiersToPreserve = 8;
    private const int CriticalSnippetUtf8Bytes = 128;
    private const int CoverageExcerptUtf8Bytes = 64;
    private const int RepresentativeExcerptUtf8Bytes = 256;
    private const int MinimumPartialExcerptUtf8Bytes = 24;
    private const int MinimumReclaimUtf8Bytes = 128;
    private const int MinimumReclaimPermille = 100;

    private static readonly string[] ConstraintMarkers =
    {
        "must not",
        "must",
        "do not",
        "don't",
        "never",
        "always",
        "required",
        "requirement",
        "constraint",
        "cannot",
        "can't",
        "禁止",
        "必须",
        "不得",
        "不能",
        "务必",
        "始终",
        "约束",
        "规则"
    };

    private static readonly string[] IntentMarkers =
    {
        "i will",
        "we will",
        "will ",
        "plan to",
        "todo",
        "to do",
        "next step",
        "not yet",
        "still need",
        "need to",
        "pending",
        "promise",
        "committed",
        "follow up",
        "shall ",
        "计划",
        "待办",
        "承诺",
        "尚未",
        "未完成",
        "下一步",
        "稍后",
        "仍需",
        "需要"
    };

    internal static ConversationCompactionResult CreateDeterministicResult(
        ConversationCompactionRequest request,
        SummaryAnalysis? analysis,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        analysis ??= Analyze(request, cancellationToken);
        var maximumBytes = MaximumAdmissibleSummaryBytes(
            request,
            analysis.SourceUtf8Bytes);
        var selected = analysis.RequiredItems.ToList();
        var result = CreateMeasuredResult(
            request,
            analysis,
            selected,
            out var resultBytes);
        if (resultBytes > maximumBytes)
        {
            throw new RuntimeContentLimitException(
                nameof(request),
                "conversation_summary_quality_budget_too_small",
                "The summary budget cannot preserve required semantic "
                + "anchors while making useful progress "
                + $"(required={resultBytes}, admissible={maximumBytes}, "
                + $"source={analysis.SourceUtf8Bytes}).");
        }

        var availableOptionalItems = Math.Min(
            analysis.OptionalItems.Count,
            MaximumSummaryItems - selected.Count);
        var low = 0;
        var high = availableOptionalItems;
        while (low < high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = low + ((high - low + 1) / 2);
            var candidateItems = new List<SummaryItem>(
                selected.Count + middle);
            candidateItems.AddRange(selected);
            for (var index = 0; index < middle; index++)
            {
                candidateItems.Add(analysis.OptionalItems[index]);
            }

            var candidate = CreateMeasuredResult(
                request,
                analysis,
                candidateItems,
                out var candidateBytes);
            if (candidateBytes <= maximumBytes)
            {
                result = candidate;
                resultBytes = candidateBytes;
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (var index = 0; index < low; index++)
        {
            selected.Add(analysis.OptionalItems[index]);
        }
        if (low < availableOptionalItems)
        {
            var optional = analysis.OptionalItems[low];
            var shortened = FindLongestFittingExcerpt(
                request,
                analysis,
                selected,
                optional,
                maximumBytes,
                cancellationToken);
            if (shortened is not null)
            {
                selected.Add(shortened);
                result = CreateMeasuredResult(
                    request,
                    analysis,
                    selected,
                    out resultBytes);
            }
        }

        if (resultBytes > request.MaxSummaryUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The exact conversation summary byte calculation diverged.");
        }

        result.QualityAnalysis = analysis;
        return result;
    }

    internal static SummaryAnalysis Analyze(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var messages = request.AdmittedMessages;
        var coverageIndexes = EvenlySpacedIndexes(
            messages.Count,
            messages.Count > 128 ? 5 : Math.Min(messages.Count, 3));
        var representativeIndexes = EvenlySpacedIndexes(
            messages.Count,
            Math.Min(messages.Count, MaximumRepresentativeItems));
        var interestingIndexes = coverageIndexes
            .Concat(representativeIndexes)
            .ToHashSet();
        var representatives = new Dictionary<int, SummaryItem>();
        var constraints = new List<SummaryItem>();
        var intents = new List<SummaryItem>();
        var identifiers = new List<SummaryItem>();
        SummaryItem? latestPendingAsk = null;
        var detectedConstraints = 0;
        var detectedIntents = 0;
        var detectedIdentifiers = 0;
        var sourceUtf8Bytes = 0;

        for (var messageIndex = 0;
             messageIndex < messages.Count;
             messageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[messageIndex];
            sourceUtf8Bytes = checked(
                sourceUtf8Bytes
                + Encoding.UTF8.GetByteCount(
                    NormalizedMessageJournalCodec.EncodeText(message)));

            var representative = interestingIndexes.Contains(messageIndex)
                || string.Equals(
                    message.Role,
                    NormalizedRoles.User,
                    StringComparison.Ordinal)
                ? RepresentativeExcerpt(
                    message,
                    RepresentativeExcerptUtf8Bytes)
                : string.Empty;
            if (interestingIndexes.Contains(messageIndex)
                && representative.Length > 0)
            {
                representatives[messageIndex] = new SummaryItem(
                    ConversationSummaryContract.RepresentativeItemKind,
                    messageIndex,
                    message.MessageId,
                    message.Role,
                    representative);
            }

            if (string.Equals(
                    message.Role,
                    NormalizedRoles.User,
                    StringComparison.Ordinal)
                && representative.Length > 0)
            {
                latestPendingAsk = new SummaryItem(
                    ConversationSummaryContract.LatestPendingAskItemKind,
                    messageIndex,
                    message.MessageId,
                    message.Role,
                    TruncateUtf8(
                        representative,
                        CriticalSnippetUtf8Bytes));
            }

            SummaryItem? constraint = null;
            SummaryItem? intent = null;
            var identifiersInMessage = 0;
            foreach (var part in message.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = SummaryContent(part);
                if (string.IsNullOrEmpty(value))
                {
                    AddStructuredIdentifiers(
                        part,
                        message,
                        messageIndex,
                        identifiers,
                        ref detectedIdentifiers);
                    continue;
                }

                if (constraint is null
                    && TryFindMarker(
                        value,
                        ConstraintMarkers,
                        cancellationToken,
                        out var constraintIndex))
                {
                    constraint = new SummaryItem(
                        ConversationSummaryContract
                            .ExplicitConstraintItemKind,
                        messageIndex,
                        message.MessageId,
                        message.Role,
                        CriticalSnippet(
                            value,
                            constraintIndex,
                            CriticalSnippetUtf8Bytes));
                }

                if (intent is null
                    && TryFindMarker(
                        value,
                        IntentMarkers,
                        cancellationToken,
                        out var intentIndex))
                {
                    intent = new SummaryItem(
                        ConversationSummaryContract
                            .UnfinishedIntentOrCommitmentItemKind,
                        messageIndex,
                        message.MessageId,
                        message.Role,
                        CriticalSnippet(
                            value,
                            intentIndex,
                            CriticalSnippetUtf8Bytes));
                }

                ScanExactIdentifiers(
                    value,
                    message,
                    messageIndex,
                    identifiers,
                    ref detectedIdentifiers,
                    ref identifiersInMessage,
                    cancellationToken);
                AddStructuredIdentifiers(
                    part,
                    message,
                    messageIndex,
                    identifiers,
                    ref detectedIdentifiers);
            }

            if (constraint is not null)
            {
                detectedConstraints = IncrementSaturating(detectedConstraints);
                AddRolling(
                    constraints,
                    constraint,
                    MaximumConstraintsToPreserve);
            }
            if (intent is not null)
            {
                detectedIntents = IncrementSaturating(detectedIntents);
                AddRolling(
                    intents,
                    intent,
                    MaximumIntentsToPreserve);
            }
        }

        var coverage = new List<SummaryItem>();
        foreach (var index in coverageIndexes)
        {
            if (!representatives.TryGetValue(index, out var item))
            {
                continue;
            }

            coverage.Add(
                item.WithContent(
                    TruncateUtf8(
                        item.Content,
                        CoverageExcerptUtf8Bytes)));
        }

        var required = new List<SummaryItem>();
        if (latestPendingAsk is not null)
        {
            required.Add(latestPendingAsk);
        }
        required.AddRange(constraints);
        required.AddRange(intents);
        required.AddRange(identifiers);
        required.AddRange(coverage);
        required = required
            .Distinct(SummaryItemComparer.Instance)
            .OrderBy(item => item.SourceIndex)
            .ThenBy(item => KindOrder(item.Kind))
            .ToList();

        var optional = representativeIndexes
            .Where(index => !coverageIndexes.Contains(index))
            .Select(
                index => representatives.TryGetValue(index, out var item)
                    ? item
                    : null)
            .Where(item => item is not null)
            .Cast<SummaryItem>()
            .Where(item => !required.Contains(
                item,
                SummaryItemComparer.Instance))
            .ToArray();

        return new SummaryAnalysis(
            messages,
            sourceUtf8Bytes,
            latestPendingAsk is null ? 0 : 1,
            detectedConstraints,
            detectedIntents,
            detectedIdentifiers,
            new ReadOnlyCollection<SummaryItem>(required.ToArray()),
            new ReadOnlyCollection<SummaryItem>(coverage.ToArray()),
            new ReadOnlyCollection<SummaryItem>(optional));
    }

    internal static bool TryCreateAdmittedSummary(
        ConversationCompactionRequest request,
        ConversationCompactionResult result,
        SummaryAnalysis analysis,
        CancellationToken cancellationToken,
        out NormalizedMessage? summary,
        out string rejectionCode)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NormalizedMessage candidate;
        try
        {
            candidate = ConversationSummaryEnvelope.Create(request, result);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                  and not OutOfMemoryException
                  and not StackOverflowException)
        {
            summary = null;
            rejectionCode = "conversation_summary_invalid_lineage";
            return false;
        }

        var summaryBytes = Measure(candidate);
        if (summaryBytes > request.MaxSummaryUtf8Bytes)
        {
            summary = null;
            rejectionCode = "conversation_summary_too_large";
            return false;
        }

        if (!HasSufficientSemanticQuality(
                request,
                result,
                analysis,
                summaryBytes,
                cancellationToken))
        {
            summary = null;
            rejectionCode = "conversation_summary_quality_insufficient";
            return false;
        }

        var reclaimed = analysis.SourceUtf8Bytes - summaryBytes;
        if (reclaimed <= 0)
        {
            summary = null;
            rejectionCode = "conversation_summary_no_progress";
            return false;
        }

        if (reclaimed < MinimumRequiredReclaim(analysis.SourceUtf8Bytes))
        {
            summary = null;
            rejectionCode = "conversation_summary_low_reclaim";
            return false;
        }

        summary = NormalizedMessageJournalCodec.CloneValidated(candidate);
        rejectionCode = string.Empty;
        return true;
    }

    private static SummaryItem? FindLongestFittingExcerpt(
        ConversationCompactionRequest request,
        SummaryAnalysis analysis,
        List<SummaryItem> selected,
        SummaryItem optional,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var low = MinimumPartialExcerptUtf8Bytes;
        var high = Encoding.UTF8.GetByteCount(optional.Content);
        SummaryItem? best = null;
        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = low + ((high - low) / 2);
            var content = TruncateUtf8(optional.Content, middle);
            if (content.Length == 0)
            {
                high = middle - 1;
                continue;
            }

            var shortened = optional.WithContent(content);
            selected.Add(shortened);
            _ = CreateMeasuredResult(
                request,
                analysis,
                selected,
                out var measured);
            selected.RemoveAt(selected.Count - 1);
            if (measured <= maximumBytes)
            {
                best = shortened;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static ConversationCompactionResult CreateMeasuredResult(
        ConversationCompactionRequest request,
        SummaryAnalysis analysis,
        IReadOnlyList<SummaryItem> items,
        out int envelopeUtf8Bytes)
    {
        var sourceIds = DistinctSourceIds(items);
        var envelopeBytes = 0;
        var reclaimedBytes = analysis.SourceUtf8Bytes;
        var reclaimPermille = analysis.SourceUtf8Bytes == 0 ? 0 : 1_000;
        ConversationCompactionResult? result = null;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            result = new ConversationCompactionResult(
                RenderContract(
                    analysis,
                    items,
                    sourceIds.Count,
                    envelopeBytes,
                    reclaimedBytes,
                    reclaimPermille),
                sourceIds,
                request.SourceDigest);
            var measured = Measure(
                ConversationSummaryEnvelope.Create(request, result));
            var measuredReclaimed = analysis.SourceUtf8Bytes - measured;
            var measuredPermille = ReclaimPermille(
                analysis.SourceUtf8Bytes,
                measuredReclaimed);
            if (measured == envelopeBytes
                && measuredReclaimed == reclaimedBytes
                && measuredPermille == reclaimPermille)
            {
                envelopeUtf8Bytes = measured;
                return result;
            }

            envelopeBytes = measured;
            reclaimedBytes = measuredReclaimed;
            reclaimPermille = measuredPermille;
        }

        throw new InvalidOperationException(
            "Conversation summary audit metrics did not converge.");
    }

    private static string RenderContract(
        SummaryAnalysis analysis,
        IReadOnlyList<SummaryItem> items,
        int distinctSourceMessageCount,
        int envelopeUtf8Bytes,
        int reclaimedUtf8Bytes,
        int reclaimPermille)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contractVersion",
                ConversationSummaryContract.CurrentVersion);
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind);
                writer.WriteString("messageId", item.MessageId);
                writer.WriteString("role", item.Role);
                writer.WriteString("content", item.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("audit");
            writer.WriteNumber(
                "sourceMessageCount",
                analysis.Messages.Count);
            writer.WriteNumber(
                "scannedMessageCount",
                analysis.Messages.Count);
            writer.WriteNumber(
                "sourceUtf8Bytes",
                analysis.SourceUtf8Bytes);
            writer.WriteStartObject("detected");
            WriteCounts(
                writer,
                analysis.DetectedLatestPendingAsks,
                analysis.DetectedConstraints,
                analysis.DetectedIntents,
                analysis.DetectedIdentifiers,
                analysis.CoverageItems.Count);
            writer.WriteEndObject();
            writer.WriteStartObject("preserved");
            WriteCounts(
                writer,
                CountKind(
                    items,
                    ConversationSummaryContract.LatestPendingAskItemKind),
                CountKind(
                    items,
                    ConversationSummaryContract.ExplicitConstraintItemKind),
                CountKind(
                    items,
                    ConversationSummaryContract
                        .UnfinishedIntentOrCommitmentItemKind),
                CountKind(
                    items,
                    ConversationSummaryContract.ExactIdentifierItemKind),
                CountKind(
                    items,
                    ConversationSummaryContract.RepresentativeItemKind));
            writer.WriteEndObject();
            writer.WriteNumber(
                "omittedSourceMessageCount",
                analysis.Messages.Count - distinctSourceMessageCount);
            writer.WriteString(
                "envelopeUtf8Bytes",
                FormatAuditMetric(envelopeUtf8Bytes));
            writer.WriteString(
                "reclaimedUtf8Bytes",
                FormatAuditMetric(reclaimedUtf8Bytes));
            writer.WriteString(
                "reclaimPermille",
                FormatAuditMetric(reclaimPermille));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCounts(
        Utf8JsonWriter writer,
        int latestPendingAsk,
        int explicitConstraint,
        int unfinishedIntentOrCommitment,
        int exactIdentifier,
        int representative)
    {
        writer.WriteNumber("latestPendingAsk", latestPendingAsk);
        writer.WriteNumber("explicitConstraint", explicitConstraint);
        writer.WriteNumber(
            "unfinishedIntentOrCommitment",
            unfinishedIntentOrCommitment);
        writer.WriteNumber("exactIdentifier", exactIdentifier);
        writer.WriteNumber("representative", representative);
    }

    private static bool HasSufficientSemanticQuality(
        ConversationCompactionRequest request,
        ConversationCompactionResult result,
        SummaryAnalysis analysis,
        int envelopeUtf8Bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < result.SummaryText.Length
               && char.IsWhiteSpace(
                   result.SummaryText[firstNonWhitespace]))
        {
            firstNonWhitespace++;
        }

        if (firstNonWhitespace >= result.SummaryText.Length
            || result.SummaryText[firstNonWhitespace] != '{')
        {
            return HasLegacyCoverage(result, analysis);
        }

        try
        {
            using var document = JsonDocument.Parse(
                result.SummaryText,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(
                    "contractVersion",
                    out _))
            {
                return HasLegacyCoverage(result, analysis);
            }

            return ValidateTypedContract(
                request,
                result,
                analysis,
                envelopeUtf8Bytes,
                root,
                cancellationToken);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasLegacyCoverage(
        ConversationCompactionResult result,
        SummaryAnalysis analysis)
    {
        var sourceIds = result.SourceMessageIds.ToHashSet(
            StringComparer.Ordinal);
        foreach (var required in analysis.RequiredItems)
        {
            if (!sourceIds.Contains(required.MessageId)
                || result.SummaryText.IndexOf(
                    required.Content,
                    StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return analysis.RequiredItems.Count > 0;
    }

    private static bool ValidateTypedContract(
        ConversationCompactionRequest request,
        ConversationCompactionResult result,
        SummaryAnalysis analysis,
        int envelopeUtf8Bytes,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!HasExactProperties(
                root,
                "contractVersion",
                "items",
                "audit")
            || !TryGetString(
                root,
                "contractVersion",
                out var version)
            || !string.Equals(
                version,
                ConversationSummaryContract.CurrentVersion,
                StringComparison.Ordinal)
            || !root.TryGetProperty("items", out var itemsValue)
            || itemsValue.ValueKind != JsonValueKind.Array
            || itemsValue.GetArrayLength() > MaximumSummaryItems)
        {
            return false;
        }

        var sourceById = analysis.Messages.ToDictionary(
            message => message.MessageId,
            StringComparer.Ordinal);
        var items = new List<SummaryItem>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemValue in itemsValue.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExactProperties(
                    itemValue,
                    "kind",
                    "messageId",
                    "role",
                    "content")
                || !TryGetString(itemValue, "kind", out var kind)
                || !TryGetString(itemValue, "messageId", out var messageId)
                || !TryGetString(itemValue, "role", out var role)
                || !TryGetString(itemValue, "content", out var content)
                || string.IsNullOrEmpty(content)
                || Encoding.UTF8.GetByteCount(content)
                > RepresentativeExcerptUtf8Bytes
                || !sourceById.TryGetValue(messageId, out var source)
                || !string.Equals(
                    source.Role,
                    role,
                    StringComparison.Ordinal)
                || !IsKnownKind(kind))
            {
                return false;
            }

            var item = new SummaryItem(
                kind,
                SourceIndex(analysis.Messages, messageId),
                messageId,
                role,
                content);
            var key = string.Concat(
                kind,
                "\u001f",
                messageId,
                "\u001f",
                content);
            if (!seenItems.Add(key)
                || !IsSourceSupportedItem(item, source, analysis))
            {
                return false;
            }
            items.Add(item);
        }

        foreach (var required in analysis.RequiredItems)
        {
            if (!items.Contains(required, SummaryItemComparer.Instance))
            {
                return false;
            }
        }

        if (!result.SourceMessageIds.SequenceEqual(
                DistinctSourceIds(items),
                StringComparer.Ordinal)
            || !root.TryGetProperty("audit", out var audit)
            || !ValidateAudit(
                audit,
                analysis,
                items,
                result.SourceMessageIds.Count,
                envelopeUtf8Bytes))
        {
            return false;
        }

        _ = request;
        return true;
    }

    private static bool ValidateAudit(
        JsonElement audit,
        SummaryAnalysis analysis,
        IReadOnlyList<SummaryItem> items,
        int distinctSourceMessageCount,
        int envelopeUtf8Bytes)
    {
        if (!HasExactProperties(
                audit,
                "sourceMessageCount",
                "scannedMessageCount",
                "sourceUtf8Bytes",
                "detected",
                "preserved",
                "omittedSourceMessageCount",
                "envelopeUtf8Bytes",
                "reclaimedUtf8Bytes",
                "reclaimPermille")
            || !TryGetInt32(
                audit,
                "sourceMessageCount",
                out var sourceCount)
            || !TryGetInt32(
                audit,
                "scannedMessageCount",
                out var scannedCount)
            || !TryGetInt32(
                audit,
                "sourceUtf8Bytes",
                out var sourceBytes)
            || !TryGetInt32(
                audit,
                "omittedSourceMessageCount",
                out var omittedCount)
            || !TryGetAuditMetric(
                audit,
                "envelopeUtf8Bytes",
                out var auditedEnvelopeBytes)
            || !TryGetAuditMetric(
                audit,
                "reclaimedUtf8Bytes",
                out var reclaimedBytes)
            || !TryGetAuditMetric(
                audit,
                "reclaimPermille",
                out var auditedPermille)
            || !audit.TryGetProperty("detected", out var detected)
            || !audit.TryGetProperty("preserved", out var preserved))
        {
            return false;
        }

        var actualReclaimed = analysis.SourceUtf8Bytes - envelopeUtf8Bytes;
        return sourceCount == analysis.Messages.Count
               && scannedCount == analysis.Messages.Count
               && sourceBytes == analysis.SourceUtf8Bytes
               && omittedCount
               == analysis.Messages.Count - distinctSourceMessageCount
               && auditedEnvelopeBytes == envelopeUtf8Bytes
               && reclaimedBytes == actualReclaimed
               && auditedPermille
               == ReclaimPermille(
                   analysis.SourceUtf8Bytes,
                   actualReclaimed)
               && ValidateCounts(
                   detected,
                   analysis.DetectedLatestPendingAsks,
                   analysis.DetectedConstraints,
                   analysis.DetectedIntents,
                   analysis.DetectedIdentifiers,
                   analysis.CoverageItems.Count)
               && ValidateCounts(
                   preserved,
                   CountKind(
                       items,
                       ConversationSummaryContract
                           .LatestPendingAskItemKind),
                   CountKind(
                       items,
                       ConversationSummaryContract
                           .ExplicitConstraintItemKind),
                   CountKind(
                       items,
                       ConversationSummaryContract
                           .UnfinishedIntentOrCommitmentItemKind),
                   CountKind(
                       items,
                       ConversationSummaryContract
                           .ExactIdentifierItemKind),
                   CountKind(
                       items,
                       ConversationSummaryContract.RepresentativeItemKind));
    }

    private static bool ValidateCounts(
        JsonElement value,
        int latestPendingAsk,
        int explicitConstraint,
        int unfinishedIntentOrCommitment,
        int exactIdentifier,
        int representative)
    {
        return HasExactProperties(
                   value,
                   "latestPendingAsk",
                   "explicitConstraint",
                   "unfinishedIntentOrCommitment",
                   "exactIdentifier",
                   "representative")
               && TryGetInt32(
                   value,
                   "latestPendingAsk",
                   out var actualAsk)
               && actualAsk == latestPendingAsk
               && TryGetInt32(
                   value,
                   "explicitConstraint",
                   out var actualConstraint)
               && actualConstraint == explicitConstraint
               && TryGetInt32(
                   value,
                   "unfinishedIntentOrCommitment",
                   out var actualIntent)
               && actualIntent == unfinishedIntentOrCommitment
               && TryGetInt32(
                   value,
                   "exactIdentifier",
                   out var actualIdentifier)
               && actualIdentifier == exactIdentifier
               && TryGetInt32(
                   value,
                   "representative",
                   out var actualRepresentative)
               && actualRepresentative == representative;
    }

    private static bool IsSourceSupportedItem(
        SummaryItem item,
        NormalizedMessage source,
        SummaryAnalysis analysis)
    {
        if (string.Equals(
                item.Kind,
                ConversationSummaryContract.RepresentativeItemKind,
                StringComparison.Ordinal))
        {
            return RepresentativeExcerpt(
                    source,
                    RepresentativeExcerptUtf8Bytes)
                .StartsWith(item.Content, StringComparison.Ordinal);
        }

        return analysis.RequiredItems.Contains(
            item,
            SummaryItemComparer.Instance);
    }

    private static int SourceIndex(
        IReadOnlyList<NormalizedMessage> messages,
        string messageId)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(
                    messages[index].MessageId,
                    messageId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasExactProperties(
        JsonElement value,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.Count == allowed.Count;
    }

    private static bool TryGetString(
        JsonElement value,
        string propertyName,
        out string result)
    {
        if (value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString()!;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryGetInt32(
        JsonElement value,
        string propertyName,
        out int result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out result);
    }

    private static bool TryGetAuditMetric(
        JsonElement value,
        string propertyName,
        out int result)
    {
        result = 0;
        if (!TryGetString(value, propertyName, out var text)
            || text.Length != 11
            || text[0] is not ('+' or '-'))
        {
            return false;
        }

        return int.TryParse(
            text,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string FormatAuditMetric(int value)
    {
        var magnitude = Math.Abs((long)value).ToString(
            "D10",
            CultureInfo.InvariantCulture);
        return string.Concat(value < 0 ? "-" : "+", magnitude);
    }

    private static IReadOnlyList<string> DistinctSourceIds(
        IReadOnlyList<SummaryItem> items)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(item.MessageId))
            {
                result.Add(item.MessageId);
            }
        }

        if (result.Count > MaximumSummaryItems)
        {
            throw new RuntimeContentLimitException(
                nameof(items),
                "conversation_summary_source_ids_exceeded",
                "A conversation summary references too many source "
                + "messages.");
        }

        return new ReadOnlyCollection<string>(result.ToArray());
    }

    private static int MaximumAdmissibleSummaryBytes(
        ConversationCompactionRequest request,
        int sourceUtf8Bytes)
    {
        return Math.Min(
            request.MaxSummaryUtf8Bytes,
            Math.Max(0, sourceUtf8Bytes - MinimumRequiredReclaim(
                sourceUtf8Bytes)));
    }

    private static int MinimumRequiredReclaim(int sourceUtf8Bytes)
    {
        var proportional = (int)Math.Min(
            int.MaxValue,
            ((long)sourceUtf8Bytes * MinimumReclaimPermille + 999) / 1_000);
        return Math.Max(MinimumReclaimUtf8Bytes, proportional);
    }

    private static int ReclaimPermille(
        int sourceUtf8Bytes,
        int reclaimedUtf8Bytes)
    {
        if (sourceUtf8Bytes == 0)
        {
            return 0;
        }

        var value = (long)reclaimedUtf8Bytes * 1_000 / sourceUtf8Bytes;
        return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
    }

    private static int Measure(NormalizedMessage message)
    {
        return Encoding.UTF8.GetByteCount(
            NormalizedMessageJournalCodec.EncodeText(message));
    }

    private static int CountKind(
        IReadOnlyList<SummaryItem> items,
        string kind)
    {
        var result = 0;
        foreach (var item in items)
        {
            if (string.Equals(
                    item.Kind,
                    kind,
                    StringComparison.Ordinal))
            {
                result++;
            }
        }
        return result;
    }

    private static bool IsKnownKind(string kind)
    {
        return string.Equals(
                   kind,
                   ConversationSummaryContract.LatestPendingAskItemKind,
                   StringComparison.Ordinal)
               || string.Equals(
                   kind,
                   ConversationSummaryContract.ExplicitConstraintItemKind,
                   StringComparison.Ordinal)
               || string.Equals(
                   kind,
                   ConversationSummaryContract
                       .UnfinishedIntentOrCommitmentItemKind,
                   StringComparison.Ordinal)
               || string.Equals(
                   kind,
                   ConversationSummaryContract.ExactIdentifierItemKind,
                   StringComparison.Ordinal)
               || string.Equals(
                   kind,
                   ConversationSummaryContract.RepresentativeItemKind,
                   StringComparison.Ordinal);
    }

    private static int KindOrder(string kind)
    {
        if (string.Equals(
                kind,
                ConversationSummaryContract.LatestPendingAskItemKind,
                StringComparison.Ordinal))
        {
            return 0;
        }
        if (string.Equals(
                kind,
                ConversationSummaryContract.ExplicitConstraintItemKind,
                StringComparison.Ordinal))
        {
            return 1;
        }
        if (string.Equals(
                kind,
                ConversationSummaryContract
                    .UnfinishedIntentOrCommitmentItemKind,
                StringComparison.Ordinal))
        {
            return 2;
        }
        if (string.Equals(
                kind,
                ConversationSummaryContract.ExactIdentifierItemKind,
                StringComparison.Ordinal))
        {
            return 3;
        }
        return 4;
    }

    private static IReadOnlyList<int> EvenlySpacedIndexes(
        int count,
        int slots)
    {
        if (count <= 0 || slots <= 0)
        {
            return Array.Empty<int>();
        }
        if (slots == 1)
        {
            return new[] { count / 2 };
        }

        var result = new List<int>(slots);
        for (var slot = 0; slot < slots; slot++)
        {
            var index = (int)((long)slot * (count - 1) / (slots - 1));
            if (result.Count == 0 || result[^1] != index)
            {
                result.Add(index);
            }
        }
        return new ReadOnlyCollection<int>(result.ToArray());
    }

    private static string RepresentativeExcerpt(
        NormalizedMessage message,
        int maximumUtf8Bytes)
    {
        var result = new StringBuilder();
        foreach (var part in message.Parts)
        {
            var value = SummaryContent(part);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var prefix = result.Length == 0 ? string.Empty : " ";
            var available = maximumUtf8Bytes
                            - Encoding.UTF8.GetByteCount(result.ToString())
                            - Encoding.UTF8.GetByteCount(prefix);
            if (available <= 0)
            {
                break;
            }

            result.Append(prefix);
            result.Append(TruncateUtf8(value, available));
            if (Encoding.UTF8.GetByteCount(result.ToString())
                >= maximumUtf8Bytes)
            {
                break;
            }
        }

        return result.ToString();
    }

    private static string? SummaryContent(NormalizedContentPart part)
    {
        return part.Type switch
        {
            NormalizedPartTypes.Text => part.Text,
            NormalizedPartTypes.Reasoning => null,
            NormalizedPartTypes.Json or NormalizedPartTypes.ToolResult =>
                part.Json?.GetRawText(),
            NormalizedPartTypes.ToolCall =>
                string.Concat(
                    part.ToolName,
                    "(",
                    part.Json?.GetRawText(),
                    ")"),
            _ => null
        };
    }

    private static bool TryFindMarker(
        string value,
        IReadOnlyList<string> markers,
        CancellationToken cancellationToken,
        out int markerIndex)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4_095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (var marker in markers)
            {
                if (value.Length - index < marker.Length
                    || !CharactersEqualIgnoreCase(
                        value[index],
                        marker[0])
                    || !value.AsSpan(index, marker.Length)
                        .Equals(
                            marker.AsSpan(),
                            StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                markerIndex = index;
                return true;
            }
        }

        markerIndex = -1;
        return false;
    }

    private static bool CharactersEqualIgnoreCase(char left, char right)
    {
        return left == right
               || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }

    private static string CriticalSnippet(
        string value,
        int markerIndex,
        int maximumUtf8Bytes)
    {
        var start = Math.Max(0, markerIndex - 32);
        if (start > 0
            && char.IsLowSurrogate(value[start])
            && char.IsHighSurrogate(value[start - 1]))
        {
            start--;
        }
        return TruncateUtf8(value[start..], maximumUtf8Bytes);
    }

    private static void ScanExactIdentifiers(
        string value,
        NormalizedMessage message,
        int messageIndex,
        List<SummaryItem> selected,
        ref int detected,
        ref int identifiersInMessage,
        CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < value.Length)
        {
            if ((index & 4_095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!IsIdentifierTokenCharacter(value[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length
                   && IsIdentifierTokenCharacter(value[index]))
            {
                index++;
            }

            var end = index;
            while (end > start
                   && IsIdentifierTrailingPunctuation(value[end - 1]))
            {
                end--;
            }
            if (end <= start)
            {
                continue;
            }

            var token = value.AsSpan(start, end - start);
            if (!LooksLikeExactIdentifier(token))
            {
                continue;
            }

            detected = IncrementSaturating(detected);
            if (identifiersInMessage >= 32)
            {
                continue;
            }
            identifiersInMessage++;
            AddRolling(
                selected,
                new SummaryItem(
                    ConversationSummaryContract.ExactIdentifierItemKind,
                    messageIndex,
                    message.MessageId,
                    message.Role,
                    token.ToString()),
                MaximumIdentifiersToPreserve,
                deduplicateByContent: true);
        }
    }

    private static void AddStructuredIdentifiers(
        NormalizedContentPart part,
        NormalizedMessage message,
        int messageIndex,
        List<SummaryItem> selected,
        ref int detected)
    {
        AddStructuredIdentifier(
            part.ToolCallId,
            message,
            messageIndex,
            selected,
            ref detected);
        AddStructuredIdentifier(
            part.ToolDescriptorDigest,
            message,
            messageIndex,
            selected,
            ref detected);
    }

    private static void AddStructuredIdentifier(
        string? value,
        NormalizedMessage message,
        int messageIndex,
        List<SummaryItem> selected,
        ref int detected)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > 128)
        {
            return;
        }

        detected = IncrementSaturating(detected);
        AddRolling(
            selected,
            new SummaryItem(
                ConversationSummaryContract.ExactIdentifierItemKind,
                messageIndex,
                message.MessageId,
                message.Role,
                value),
            MaximumIdentifiersToPreserve,
            deduplicateByContent: true);
    }

    private static bool LooksLikeExactIdentifier(ReadOnlySpan<char> value)
    {
        if (value.Length < 4 || Encoding.UTF8.GetByteCount(value) > 128)
        {
            return false;
        }

        if (value.StartsWith(
                "https://".AsSpan(),
                StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(
                "http://".AsSpan(),
                StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(
                "/".AsSpan(),
                StringComparison.Ordinal)
            || (value.Length > 2
                && char.IsLetter(value[0])
                && value[1] == ':'
                && (value[2] == '\\' || value[2] == '/')))
        {
            return true;
        }

        var hasLetter = false;
        var hasDigit = false;
        var hasSeparator = false;
        var allHex = value.Length is 32 or 40 or 64;
        foreach (var character in value)
        {
            hasLetter |= char.IsLetter(character);
            hasDigit |= char.IsDigit(character);
            hasSeparator |= character is '-' or '_' or ':' or '/'
                or '\\' or '.' or '=' or '@' or '#';
            allHex &= Uri.IsHexDigit(character);
        }

        return allHex || (hasLetter && hasDigit && hasSeparator);
    }

    private static bool IsIdentifierTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value)
               || value is '-' or '_' or ':' or '/' or '\\' or '.'
                   or '=' or '@' or '#';
    }

    private static bool IsIdentifierTrailingPunctuation(char value)
    {
        return value is '.' or ':' or '=' or '@' or '#';
    }

    private static void AddRolling(
        List<SummaryItem> values,
        SummaryItem value,
        int maximumItems,
        bool deduplicateByContent = false)
    {
        if (deduplicateByContent)
        {
            values.RemoveAll(
                item => string.Equals(
                    item.Content,
                    value.Content,
                    StringComparison.Ordinal));
        }
        values.Add(value);
        if (values.Count > maximumItems)
        {
            values.RemoveAt(0);
        }
    }

    private static int IncrementSaturating(int value)
    {
        return value == int.MaxValue ? value : value + 1;
    }

    private static string TruncateUtf8(string value, int maximumUtf8Bytes)
    {
        if (maximumUtf8Bytes <= 0)
        {
            return string.Empty;
        }
        if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes)
        {
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value, 0, middle)
                <= maximumUtf8Bytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0
            && low < value.Length
            && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }
        return value[..low];
    }

    internal sealed class SummaryAnalysis
    {
        public SummaryAnalysis(
            IReadOnlyList<NormalizedMessage> messages,
            int sourceUtf8Bytes,
            int detectedLatestPendingAsks,
            int detectedConstraints,
            int detectedIntents,
            int detectedIdentifiers,
            IReadOnlyList<SummaryItem> requiredItems,
            IReadOnlyList<SummaryItem> coverageItems,
            IReadOnlyList<SummaryItem> optionalItems)
        {
            Messages = messages;
            SourceUtf8Bytes = sourceUtf8Bytes;
            DetectedLatestPendingAsks = detectedLatestPendingAsks;
            DetectedConstraints = detectedConstraints;
            DetectedIntents = detectedIntents;
            DetectedIdentifiers = detectedIdentifiers;
            RequiredItems = requiredItems;
            CoverageItems = coverageItems;
            OptionalItems = optionalItems;
        }

        public IReadOnlyList<NormalizedMessage> Messages { get; }

        public int SourceUtf8Bytes { get; }

        public int DetectedLatestPendingAsks { get; }

        public int DetectedConstraints { get; }

        public int DetectedIntents { get; }

        public int DetectedIdentifiers { get; }

        public IReadOnlyList<SummaryItem> RequiredItems { get; }

        public IReadOnlyList<SummaryItem> CoverageItems { get; }

        public IReadOnlyList<SummaryItem> OptionalItems { get; }
    }

    internal sealed class SummaryItem
    {
        public SummaryItem(
            string kind,
            int sourceIndex,
            string messageId,
            string role,
            string content)
        {
            Kind = kind;
            SourceIndex = sourceIndex;
            MessageId = messageId;
            Role = role;
            Content = content;
        }

        public string Kind { get; }

        public int SourceIndex { get; }

        public string MessageId { get; }

        public string Role { get; }

        public string Content { get; }

        public SummaryItem WithContent(string content)
        {
            return new SummaryItem(
                Kind,
                SourceIndex,
                MessageId,
                Role,
                content);
        }
    }

    private sealed class SummaryItemComparer :
        IEqualityComparer<SummaryItem>
    {
        public static SummaryItemComparer Instance { get; } = new();

        public bool Equals(SummaryItem? left, SummaryItem? right)
        {
            return ReferenceEquals(left, right)
                   || (left is not null
                       && right is not null
                       && string.Equals(
                           left.Kind,
                           right.Kind,
                           StringComparison.Ordinal)
                       && string.Equals(
                           left.MessageId,
                           right.MessageId,
                           StringComparison.Ordinal)
                       && string.Equals(
                           left.Role,
                           right.Role,
                           StringComparison.Ordinal)
                       && string.Equals(
                           left.Content,
                           right.Content,
                           StringComparison.Ordinal));
        }

        public int GetHashCode(SummaryItem value)
        {
            var hash = new HashCode();
            hash.Add(value.Kind, StringComparer.Ordinal);
            hash.Add(value.MessageId, StringComparer.Ordinal);
            hash.Add(value.Role, StringComparer.Ordinal);
            hash.Add(value.Content, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
