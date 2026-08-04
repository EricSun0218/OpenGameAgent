using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed partial class ConversationContextView
{
    public const string CheckpointExtensionName =
        "conversationContextCheckpoint";

    /// <summary>
    /// Creates a bounded durable representation of this derived provider view.
    /// Original transcript bodies are not copied into the checkpoint. Only
    /// ordered message identifiers and the runtime-created summary, when one
    /// exists, are encoded.
    /// </summary>
    public JsonElement CreateCheckpoint(string runId)
    {
        return ConversationContextCheckpointCodec.Create(runId, this);
    }
}

public sealed partial class ConversationContextManager
{
    private const int MaximumRegisteredConversationCheckpoints = 4_096;

    private readonly object _checkpointGate = new();
    private readonly Dictionary<string, ConversationContextCheckpoint>
        _registeredCheckpoints = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a previously persisted derived-view checkpoint. The
    /// checkpoint is structurally and canonically integrity-checked before it
    /// is admitted. A later <see cref="PrepareAsync"/> call reuses it only
    /// when the run and exact admitted transcript digest match.
    /// </summary>
    public void RegisterCheckpoint(JsonElement checkpoint)
    {
        var admitted = ConversationContextCheckpointCodec.Decode(checkpoint);
        lock (_lifecycleGate)
        {
            if (_stopped != 0)
            {
                throw new ObjectDisposedException(
                    nameof(ConversationContextManager));
            }

            lock (_checkpointGate)
            {
                if (!_registeredCheckpoints.ContainsKey(admitted.RunId)
                    && _registeredCheckpoints.Count
                    >= MaximumRegisteredConversationCheckpoints)
                {
                    throw new RuntimeContentLimitException(
                        nameof(checkpoint),
                        "conversation_checkpoint_capacity_exceeded",
                        "The conversation checkpoint registry is full.");
                }

                _registeredCheckpoints[admitted.RunId] = admitted;
            }
        }
    }

    private bool TryRestoreRegisteredCheckpoint(
        string runId,
        string sourceDigest,
        IReadOnlyList<NormalizedMessage> transcript,
        HashSet<string> stableIds,
        int inputUtf8Bytes,
        out ConversationContextView restored)
    {
        ConversationContextCheckpoint? checkpoint;
        lock (_checkpointGate)
        {
            _registeredCheckpoints.TryGetValue(runId, out checkpoint);
            if (checkpoint is not null)
            {
                _registeredCheckpoints.Remove(runId);
            }
        }

        if (checkpoint is null)
        {
            restored = null!;
            return false;
        }

        if (!string.Equals(
                checkpoint.SourceDigest,
                sourceDigest,
                StringComparison.Ordinal))
        {
            restored = null!;
            return false;
        }

        try
        {
            restored = RestoreCheckpoint(
                checkpoint,
                transcript,
                stableIds,
                inputUtf8Bytes);
            return true;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            restored = null!;
            return false;
        }
    }

    private ConversationContextView RestoreCheckpoint(
        ConversationContextCheckpoint checkpoint,
        IReadOnlyList<NormalizedMessage> transcript,
        HashSet<string> stableIds,
        int inputUtf8Bytes)
    {
        if (checkpoint.Report.InputMessageCount != transcript.Count
            || checkpoint.Report.InputUtf8Bytes != inputUtf8Bytes)
        {
            throw new InvalidDataException(
                "The conversation checkpoint input evidence does not match.");
        }

        var transcriptById = new Dictionary<string, (int Index,
            NormalizedMessage Message)>(StringComparer.Ordinal);
        for (var index = 0; index < transcript.Count; index++)
        {
            var message = transcript[index];
            if (!transcriptById.TryAdd(
                    message.MessageId,
                    (index, message)))
            {
                throw new InvalidDataException(
                    "Conversation checkpoint restoration requires unique "
                    + "transcript message identifiers.");
            }
        }

        var summary = checkpoint.Summary is null
            ? null
            : NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(checkpoint.Summary));
        if (summary is not null
            && transcriptById.ContainsKey(summary.MessageId))
        {
            throw new InvalidDataException(
                "A derived summary identifier collides with the transcript.");
        }

        var retainedIndexes = new HashSet<int>();
        var reconstructed = new List<NormalizedMessage>(
            checkpoint.OutputMessageIds.Count);
        var seenOutputIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0;
             index < checkpoint.OutputMessageIds.Count;
             index++)
        {
            var messageId = checkpoint.OutputMessageIds[index];
            if (!seenOutputIds.Add(messageId))
            {
                throw new InvalidDataException(
                    "A conversation checkpoint has duplicate output "
                    + "message identifiers.");
            }

            if (summary is not null
                && string.Equals(
                    messageId,
                    summary.MessageId,
                    StringComparison.Ordinal))
            {
                reconstructed.Add(summary);
                continue;
            }

            if (!transcriptById.TryGetValue(messageId, out var source))
            {
                throw new InvalidDataException(
                    "A conversation checkpoint references a missing "
                    + "transcript message.");
            }

            retainedIndexes.Add(source.Index);
            reconstructed.Add(source.Message);
        }

        if ((summary is null) != !checkpoint.Report.Compacted
            || (summary is not null
                && !seenOutputIds.Contains(summary.MessageId)))
        {
            throw new InvalidDataException(
                "A conversation checkpoint has inconsistent summary "
                + "evidence.");
        }

        var atomicGroups = BuildAtomicGroups(transcript);
        var requiredIndexes = RequiredIndexes(
            transcript,
            stableIds,
            atomicGroups);
        if (requiredIndexes.Any(index => !retainedIndexes.Contains(index)))
        {
            throw new InvalidDataException(
                "A conversation checkpoint omits currently required "
                + "conversation context.");
        }

        var expectedOrder = BuildView(
            transcript,
            retainedIndexes,
            summary,
            stableIds);
        if (!expectedOrder
                .Select(message => message.MessageId)
                .SequenceEqual(
                    checkpoint.OutputMessageIds,
                    StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "A conversation checkpoint changed derived-view ordering.");
        }

        if (summary is not null)
        {
            ValidateSummaryLineage(
                checkpoint,
                transcript,
                retainedIndexes,
                summary);
        }

        if (reconstructed.Count > _options.MaxRequestMessages)
        {
            throw new RuntimeContentLimitException(
                nameof(checkpoint),
                "conversation_checkpoint_messages_exceeded",
                "The restored conversation view exceeds the current "
                + "message budget.");
        }

        var outputUtf8Bytes = Measure(reconstructed);
        if (outputUtf8Bytes > _options.MaxRequestUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                nameof(checkpoint),
                "conversation_checkpoint_bytes_exceeded",
                "The restored conversation view exceeds the current byte "
                + "budget.");
        }

        var viewDigest = Digest(reconstructed);
        if (checkpoint.Report.OutputMessageCount != reconstructed.Count
            || checkpoint.Report.OutputUtf8Bytes != outputUtf8Bytes
            || !string.Equals(
                checkpoint.ViewDigest,
                viewDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                checkpoint.Report.ViewDigest,
                viewDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A conversation checkpoint view digest or budget evidence "
                + "does not match.");
        }

        return new ConversationContextView(
            Snapshot(reconstructed),
            checkpoint.Report);
    }

    private void ValidateSummaryLineage(
        ConversationContextCheckpoint checkpoint,
        IReadOnlyList<NormalizedMessage> transcript,
        HashSet<int> retainedIndexes,
        NormalizedMessage summary)
    {
        var lineage = checkpoint.Lineage
                      ?? throw new InvalidDataException(
                          "A derived summary checkpoint is missing lineage.");
        var actualLineage = ConversationContextCheckpointCodec
            .ReadSummaryLineage(summary);
        if (lineage.SourceMessageCount != actualLineage.SourceMessageCount
            || !string.Equals(
                lineage.SourceDigest,
                actualLineage.SourceDigest,
                StringComparison.Ordinal)
            || !lineage.SourceMessageIds.SequenceEqual(
                actualLineage.SourceMessageIds,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "A derived summary checkpoint has invalid source lineage.");
        }

        ValidateDerivedSummary(
            checkpoint.RunId,
            "checkpoint-restore",
            Enumerable.Range(0, transcript.Count)
                .Where(index => !retainedIndexes.Contains(index))
                .Select(index => transcript[index])
                .ToArray(),
            summary,
            _options,
            CancellationToken.None);
    }

    internal static void ValidateDerivedSummary(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> source,
        NormalizedMessage summary,
        ConversationContextOptions options,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var compactionSource = SnapshotWithoutReasoning(source);
            var lineage = ConversationContextCheckpointCodec
                .ReadSummaryLineage(summary);
            var sourceDigest = Digest(compactionSource);
            if (lineage.SourceMessageCount != compactionSource.Count
                || !string.Equals(
                    lineage.SourceDigest,
                    sourceDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A derived summary has invalid source lineage.");
            }

            var sourceIds = compactionSource
                .Select(message => message.MessageId)
                .ToHashSet(StringComparer.Ordinal);
            if (lineage.SourceMessageIds.Any(id => !sourceIds.Contains(id)))
            {
                throw new InvalidDataException(
                    "A derived summary references a message outside its "
                    + "compaction source.");
            }

            var expectedCreatedAt = compactionSource.Count == 0
                ? DateTimeOffset.UnixEpoch
                : compactionSource.Max(message => message.CreatedAt);
            if (summary.CreatedAt != expectedCreatedAt)
            {
                throw new InvalidDataException(
                    "A derived summary changed its source time.");
            }

            var envelope = summary.Parts[0].Json!.Value;
            var request = new ConversationCompactionRequest(
                runId,
                turnId,
                compactionSource,
                sourceDigest,
                options.MaxSummaryUtf8Bytes,
                options,
                messagesAreAdmittedSnapshots: true);
            var result = new ConversationCompactionResult(
                envelope.GetProperty("summary").GetString()!,
                lineage.SourceMessageIds,
                lineage.SourceDigest);
            var analysis = ConversationSummaryQuality.Analyze(
                request,
                cancellationToken);
            if (!ConversationSummaryQuality.TryCreateAdmittedSummary(
                    request,
                    result,
                    analysis,
                    cancellationToken,
                    out var admitted,
                    out _)
                || !string.Equals(
                    NormalizedMessageJournalCodec.EncodeText(admitted!),
                    NormalizedMessageJournalCodec.EncodeText(summary),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A derived summary has invalid semantic quality evidence.");
            }
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                  and not OutOfMemoryException
                  and not StackOverflowException
                  and not InvalidDataException)
        {
            throw new InvalidDataException(
                "A derived summary is not admitted by the runtime.",
                exception);
        }
    }

    private void ClearRegisteredCheckpoints()
    {
        lock (_checkpointGate)
        {
            _registeredCheckpoints.Clear();
        }
    }

    internal int RegisteredCheckpointCount
    {
        get
        {
            lock (_checkpointGate)
            {
                return _registeredCheckpoints.Count;
            }
        }
    }
}

internal sealed class ConversationContextCheckpoint
{
    public ConversationContextCheckpoint(
        string runId,
        string sourceDigest,
        string viewDigest,
        IReadOnlyList<string> outputMessageIds,
        NormalizedMessage? summary,
        ConversationContextReport report,
        ConversationSummaryLineage? lineage)
    {
        RunId = runId;
        SourceDigest = sourceDigest;
        ViewDigest = viewDigest;
        OutputMessageIds = outputMessageIds;
        Summary = summary;
        Report = report;
        Lineage = lineage;
    }

    public string RunId { get; }

    public string SourceDigest { get; }

    public string ViewDigest { get; }

    public IReadOnlyList<string> OutputMessageIds { get; }

    public NormalizedMessage? Summary { get; }

    public ConversationContextReport Report { get; }

    public ConversationSummaryLineage? Lineage { get; }
}

internal sealed class ConversationSummaryLineage
{
    public ConversationSummaryLineage(
        string summaryMessageId,
        string sourceDigest,
        int sourceMessageCount,
        IReadOnlyList<string> sourceMessageIds)
    {
        SummaryMessageId = summaryMessageId;
        SourceDigest = sourceDigest;
        SourceMessageCount = sourceMessageCount;
        SourceMessageIds = sourceMessageIds;
    }

    public string SummaryMessageId { get; }

    public string SourceDigest { get; }

    public int SourceMessageCount { get; }

    public IReadOnlyList<string> SourceMessageIds { get; }
}

internal static class ConversationContextCheckpointCodec
{
    private const int SchemaVersion = 1;
    private const int MaximumOutputMessageIds =
        ProtocolLimits.MaxProtocolJsonContainerItems;
    private const int MaximumSourceMessageIds = 128;
    private const string SummaryContentType =
        "application/vnd.game-agent.conversation-summary+json";
    private const string SummaryAuthority = "historical-data";

    private static readonly JsonValueLimits CheckpointLimits = new(
        ProtocolLimits.MaxProtocolJsonUtf8Bytes,
        ProtocolLimits.MaxProtocolJsonDepth,
        ProtocolLimits.MaxProtocolJsonNodes,
        ProtocolLimits.MaxProtocolJsonStringUtf8Bytes,
        ProtocolLimits.MaxProtocolJsonContainerItems);

    public static JsonElement Create(
        string runId,
        ConversationContextView view)
    {
        runId = RuntimeGuard.RequiredUtf8(runId, 128, nameof(runId));
        if (view is null)
        {
            throw new ArgumentNullException(nameof(view));
        }

        var messages = SnapshotMessages(view.Messages);
        ValidateReportAgainstMessages(view.Report, messages);
        var outputIds = SnapshotOutputIds(messages);
        var summary = FindDerivedSummary(view.Report, messages);
        var lineage = summary is null
            ? null
            : ReadSummaryLineage(summary);

        var payload = WritePayload(
            runId,
            view.Report.SourceDigest,
            view.Report.ViewDigest,
            outputIds,
            summary,
            view.Report,
            lineage);
        ValidateJson(payload, nameof(view));
        var integrityDigest = CanonicalJsonDigest.ComputeSha256(payload);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
            writer.WriteString("integrityDigest", integrityDigest);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var checkpoint = document.RootElement.Clone();
        ValidateJson(checkpoint, nameof(view));
        return checkpoint;
    }

    public static ConversationContextCheckpoint Decode(JsonElement checkpoint)
    {
        ValidateJson(checkpoint, nameof(checkpoint));
        RequireObjectProperties(
            checkpoint,
            "version",
            "payload",
            "integrityDigest");
        var version = RequiredInt32(checkpoint, "version");
        if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                "The conversation checkpoint version is unsupported.");
        }

        var payload = RequiredObject(checkpoint, "payload");
        var integrityDigest = RequiredDigest(
            checkpoint,
            "integrityDigest");
        if (!string.Equals(
                CanonicalJsonDigest.ComputeSha256(payload),
                integrityDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation checkpoint integrity digest is invalid.");
        }

        RequireObjectProperties(
            payload,
            "runId",
            "sourceDigest",
            "viewDigest",
            "outputMessageIds",
            "derivedSummary",
            "report",
            "lineage");
        var runId = RequiredUtf8(payload, "runId", 128);
        var sourceDigest = RequiredDigest(payload, "sourceDigest");
        var viewDigest = RequiredDigest(payload, "viewDigest");
        var outputIds = ReadStringArray(
            payload,
            "outputMessageIds",
            MaximumOutputMessageIds,
            requireUnique: true);
        var summary = ReadOptionalSummary(payload);
        var report = ReadReport(
            RequiredObject(payload, "report"));
        var lineage = ReadOptionalLineage(payload);

        if (!string.Equals(
                report.SourceDigest,
                sourceDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                report.ViewDigest,
                viewDigest,
                StringComparison.Ordinal)
            || report.OutputMessageCount != outputIds.Length
            || report.Compacted != (summary is not null)
            || (summary is null) != (lineage is null))
        {
            throw new InvalidDataException(
                "The conversation checkpoint report is inconsistent.");
        }

        if (summary is not null)
        {
            var actualLineage = ReadSummaryLineage(summary);
            if (!LineageEquals(actualLineage, lineage!)
                || !outputIds.Contains(
                    summary.MessageId,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "The conversation checkpoint summary lineage is "
                    + "inconsistent.");
            }
        }

        ValidateReportShape(report);
        return new ConversationContextCheckpoint(
            runId,
            sourceDigest,
            viewDigest,
            new ReadOnlyCollection<string>(outputIds.ToArray()),
            summary,
            report,
            lineage);
    }

    private static JsonElement WritePayload(
        string runId,
        string sourceDigest,
        string viewDigest,
        IReadOnlyList<string> outputIds,
        NormalizedMessage? summary,
        ConversationContextReport report,
        ConversationSummaryLineage? lineage)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("runId", runId);
            writer.WriteString("sourceDigest", sourceDigest);
            writer.WriteString("viewDigest", viewDigest);
            writer.WriteStartArray("outputMessageIds");
            for (var index = 0; index < outputIds.Count; index++)
            {
                writer.WriteStringValue(outputIds[index]);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("derivedSummary");
            if (summary is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                NormalizedMessageJournalCodec.Encode(summary).WriteTo(writer);
            }
            writer.WritePropertyName("report");
            report.ToSnapshotExtension().WriteTo(writer);
            writer.WritePropertyName("lineage");
            if (lineage is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "summaryMessageId",
                    lineage.SummaryMessageId);
                writer.WriteString(
                    "sourceDigest",
                    lineage.SourceDigest);
                writer.WriteNumber(
                    "sourceMessageCount",
                    lineage.SourceMessageCount);
                writer.WriteStartArray("sourceMessageIds");
                for (var index = 0;
                     index < lineage.SourceMessageIds.Count;
                     index++)
                {
                    writer.WriteStringValue(
                        lineage.SourceMessageIds[index]);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void ValidateReportAgainstMessages(
        ConversationContextReport report,
        IReadOnlyList<NormalizedMessage> messages)
    {
        if (report is null)
        {
            throw new InvalidDataException(
                "A conversation view is missing its report.");
        }

        ValidateReportShape(report);
        var outputBytes = Measure(messages);
        var viewDigest = Digest(messages);
        if (report.OutputMessageCount != messages.Count
            || report.OutputUtf8Bytes != outputBytes
            || !string.Equals(
                report.ViewDigest,
                viewDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation view changed after it was prepared.");
        }
    }

    private static void ValidateReportShape(ConversationContextReport report)
    {
        if (report.InputMessageCount < 0
            || report.InputMessageCount > 65_536
            || report.OutputMessageCount < 0
            || report.OutputMessageCount > MaximumOutputMessageIds
            || report.DroppedMessageCount < 0
            || report.InputUtf8Bytes < 0
            || report.InputUtf8Bytes > 256 * 1_048_576
            || report.OutputUtf8Bytes < 0
            || report.OutputUtf8Bytes > 64 * 1_048_576
            || report.DroppedMessageCount
            != report.InputMessageCount
               - report.OutputMessageCount
               + (report.Compacted ? 1 : 0)
            || (report.Compacted
                && (report.CompactionFailed
                    || report.CompactionSkippedByCooldown))
            || (report.CompactionFailed
                && report.CompactionSkippedByCooldown)
            || !CanonicalJsonDigest.IsSha256(report.SourceDigest)
            || !CanonicalJsonDigest.IsSha256(report.ViewDigest))
        {
            throw new InvalidDataException(
                "The conversation checkpoint report is invalid.");
        }
    }

    private static NormalizedMessage? FindDerivedSummary(
        ConversationContextReport report,
        IReadOnlyList<NormalizedMessage> messages)
    {
        if (!report.Compacted)
        {
            return null;
        }

        NormalizedMessage? summary = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var candidate = messages[index];
            if (!LooksLikeDerivedSummary(candidate))
            {
                continue;
            }

            if (summary is not null)
            {
                throw new InvalidDataException(
                    "A conversation view contains multiple derived "
                    + "summaries.");
            }

            summary = candidate;
        }

        return summary
               ?? throw new InvalidDataException(
                   "A compacted conversation view is missing its derived "
                   + "summary.");
    }

    private static bool LooksLikeDerivedSummary(NormalizedMessage message)
    {
        return string.Equals(
                   message.Role,
                   NormalizedRoles.User,
                   StringComparison.Ordinal)
               && message.Parts.Count == 1
               && string.Equals(
                   message.Parts[0].Type,
                   NormalizedPartTypes.Json,
                   StringComparison.Ordinal)
               && message.Parts[0].Json is { } json
               && json.ValueKind == JsonValueKind.Object
               && json.TryGetProperty("contentType", out var contentType)
               && contentType.ValueKind == JsonValueKind.String
               && string.Equals(
                   contentType.GetString(),
                   SummaryContentType,
                   StringComparison.Ordinal);
    }

    internal static ConversationSummaryLineage ReadSummaryLineage(
        NormalizedMessage summary)
    {
        if (!LooksLikeDerivedSummary(summary))
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary is not a runtime "
                + "derived summary.");
        }

        if (summary.Parts[0].Text is not null
            || summary.Parts[0].ToolCallId is not null
            || summary.Parts[0].ToolName is not null
            || summary.Parts[0].ToolVersion is not null
            || summary.Parts[0].ToolEffect is not null
            || summary.Parts[0].ToolDescriptorDigest is not null)
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary contains invalid "
                + "authority-bearing fields.");
        }

        var envelope = summary.Parts[0].Json!.Value;
        RequireObjectProperties(
            envelope,
            "contentType",
            "authority",
            "sourceDigest",
            "sourceMessageCount",
            "sourceMessageIds",
            "summary");
        if (!string.Equals(
                RequiredString(envelope, "contentType"),
                SummaryContentType,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(envelope, "authority"),
                SummaryAuthority,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary has invalid "
                + "authority.");
        }

        var sourceDigest = RequiredDigest(envelope, "sourceDigest");
        var sourceMessageCount = RequiredInt32(
            envelope,
            "sourceMessageCount");
        if (sourceMessageCount < 0 || sourceMessageCount > 65_536)
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary count is invalid.");
        }

        var sourceIds = ReadStringArray(
            envelope,
            "sourceMessageIds",
            MaximumSourceMessageIds,
            requireUnique: true);
        if (sourceIds.Length > sourceMessageCount)
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary lineage is invalid.");
        }

        _ = RequiredUtf8(
            envelope,
            "summary",
            ProtocolLimits.MaxProtocolJsonStringUtf8Bytes,
            allowEmpty: false);
        var expectedId = "conversation-summary:"
                         + sourceDigest[..16];
        if (!string.Equals(
                summary.MessageId,
                expectedId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation checkpoint summary identifier is invalid.");
        }

        return new ConversationSummaryLineage(
            summary.MessageId,
            sourceDigest,
            sourceMessageCount,
            new ReadOnlyCollection<string>(sourceIds.ToArray()));
    }

    private static IReadOnlyList<NormalizedMessage> SnapshotMessages(
        IReadOnlyList<NormalizedMessage> messages)
    {
        if (messages is null)
        {
            throw new InvalidDataException(
                "A conversation view is missing its messages.");
        }

        var count = messages.Count;
        if (count < 0 || count > MaximumOutputMessageIds)
        {
            throw new RuntimeContentLimitException(
                nameof(messages),
                "conversation_checkpoint_messages_exceeded",
                "A conversation checkpoint cannot contain more than "
                + $"{MaximumOutputMessageIds} message identifiers.");
        }

        var result = new NormalizedMessage[count];
        for (var index = 0; index < count; index++)
        {
            NormalizedMessage message;
            try
            {
                message = messages[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new InvalidDataException(
                    "The conversation view count and indexed messages "
                    + "are inconsistent.",
                    exception);
            }

            result[index] = NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(message));
        }

        return new ReadOnlyCollection<NormalizedMessage>(result);
    }

    private static IReadOnlyList<string> SnapshotOutputIds(
        IReadOnlyList<NormalizedMessage> messages)
    {
        var result = new string[messages.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            var messageId = RuntimeGuard.RequiredUtf8(
                messages[index].MessageId,
                128,
                nameof(messages));
            if (!seen.Add(messageId))
            {
                throw new InvalidDataException(
                    "A conversation checkpoint requires unique output "
                    + "message identifiers.");
            }

            result[index] = messageId;
        }

        return new ReadOnlyCollection<string>(result);
    }

    private static NormalizedMessage? ReadOptionalSummary(JsonElement payload)
    {
        if (!payload.TryGetProperty("derivedSummary", out var value))
        {
            throw new InvalidDataException(
                "The conversation checkpoint is missing 'derivedSummary'.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var summary = NormalizedMessageJournalCodec.Decode(value);
        _ = ReadSummaryLineage(summary);
        return summary;
    }

    private static ConversationSummaryLineage? ReadOptionalLineage(
        JsonElement payload)
    {
        if (!payload.TryGetProperty("lineage", out var value))
        {
            throw new InvalidDataException(
                "The conversation checkpoint is missing 'lineage'.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObjectProperties(
            value,
            "summaryMessageId",
            "sourceDigest",
            "sourceMessageCount",
            "sourceMessageIds");
        var messageId = RequiredUtf8(
            value,
            "summaryMessageId",
            128);
        var sourceDigest = RequiredDigest(value, "sourceDigest");
        var count = RequiredInt32(value, "sourceMessageCount");
        if (count < 0 || count > 65_536)
        {
            throw new InvalidDataException(
                "The conversation checkpoint lineage count is invalid.");
        }

        var sourceIds = ReadStringArray(
            value,
            "sourceMessageIds",
            MaximumSourceMessageIds,
            requireUnique: true);
        return new ConversationSummaryLineage(
            messageId,
            sourceDigest,
            count,
            new ReadOnlyCollection<string>(sourceIds.ToArray()));
    }

    private static ConversationContextReport ReadReport(JsonElement value)
    {
        RequireObjectProperties(
            value,
            "inputMessageCount",
            "outputMessageCount",
            "droppedMessageCount",
            "inputUtf8Bytes",
            "outputUtf8Bytes",
            "compacted",
            "compactionFailed",
            "compactionSkippedByCooldown",
            "sourceDigest",
            "viewDigest");
        return new ConversationContextReport(
            RequiredInt32(value, "inputMessageCount"),
            RequiredInt32(value, "outputMessageCount"),
            RequiredInt32(value, "droppedMessageCount"),
            RequiredInt32(value, "inputUtf8Bytes"),
            RequiredInt32(value, "outputUtf8Bytes"),
            RequiredBoolean(value, "compacted"),
            RequiredBoolean(value, "compactionFailed"),
            RequiredBoolean(value, "compactionSkippedByCooldown"),
            RequiredDigest(value, "sourceDigest"),
            RequiredDigest(value, "viewDigest"));
    }

    private static bool LineageEquals(
        ConversationSummaryLineage left,
        ConversationSummaryLineage right)
    {
        return string.Equals(
                   left.SummaryMessageId,
                   right.SummaryMessageId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.SourceDigest,
                   right.SourceDigest,
                   StringComparison.Ordinal)
               && left.SourceMessageCount == right.SourceMessageCount
               && left.SourceMessageIds.SequenceEqual(
                   right.SourceMessageIds,
                   StringComparer.Ordinal);
    }

    private static string[] ReadStringArray(
        JsonElement parent,
        string propertyName,
        int maximumItems,
        bool requireUnique)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        var count = value.GetArrayLength();
        if (count > maximumItems)
        {
            throw new RuntimeContentLimitException(
                propertyName,
                "conversation_checkpoint_items_exceeded",
                $"Conversation checkpoint '{propertyName}' exceeds "
                + $"{maximumItems} items.");
        }

        var result = new string[count];
        var seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Conversation checkpoint '{propertyName}' contains "
                    + "a non-string value.");
            }

            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text)
                || Encoding.UTF8.GetByteCount(text) > 128)
            {
                throw new InvalidDataException(
                    $"Conversation checkpoint '{propertyName}' contains "
                    + "an invalid identifier.");
            }

            if (seen is not null && !seen.Add(text))
            {
                throw new InvalidDataException(
                    $"Conversation checkpoint '{propertyName}' contains "
                    + "a duplicate identifier.");
            }

            result[index++] = text;
        }

        return result;
    }

    private static string RequiredDigest(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredString(parent, propertyName);
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        return value;
    }

    private static string RequiredUtf8(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes,
        bool allowEmpty = false)
    {
        var value = RequiredString(parent, propertyName);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The conversation checkpoint is missing '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static int RequiredInt32(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        return result;
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True
                or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        return value.GetBoolean();
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The conversation checkpoint has invalid '{propertyName}'.");
        }

        return value;
    }

    private static void RequireObjectProperties(
        JsonElement value,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A conversation checkpoint value must be an object.");
        }

        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    "A conversation checkpoint contains an unknown or "
                    + "duplicate property.");
            }
        }

        if (seen.Count != allowed.Count)
        {
            throw new InvalidDataException(
                "A conversation checkpoint is missing a required property.");
        }
    }

    private static void ValidateJson(
        JsonElement value,
        string parameterName)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            CheckpointLimits,
            parameterName);
    }

    private static int Measure(IReadOnlyList<NormalizedMessage> messages)
    {
        var result = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            result = checked(
                result
                + Encoding.UTF8.GetByteCount(
                    NormalizedMessageJournalCodec.Encode(messages[index])
                        .GetRawText()));
        }

        return result;
    }

    private static string Digest(IReadOnlyList<NormalizedMessage> messages)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "conversation-context");
        digest.Add("count", messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            digest.Add(
                "message",
                NormalizedMessageJournalCodec.Encode(messages[index])
                    .GetRawText());
        }

        return digest.Finish();
    }
}
