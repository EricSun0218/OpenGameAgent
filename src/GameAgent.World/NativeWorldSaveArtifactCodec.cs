using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

internal enum NativeWorldSaveArtifactKind
{
    Settled = 0,
    Fork = 1
}

internal enum NativeWorldIssuedIncarnationEncoding
{
    None = 0,
    ObjectRecords = 1,
    ParallelArrays = 2,
    PackedLedger = 3
}

internal static class NativeWorldSaveArtifactCodec
{
    private const string MetadataKey =
        "game-agent.native-world-save-bridge.v1";
    private const string MetadataContract =
        "game-agent.native-world-save-metadata.v1";
    private const string ScheduleMetadataKey =
        "game-agent.native-world-schedules.v1";
    private const string ScheduleMetadataContract =
        "game-agent.native-world-schedule-metadata.v1";

    private static readonly HashSet<string> MetadataFields =
        Fields(
            "contract",
            "artifactKind",
            "packageDigest",
            "catalogDigest",
            "worldId",
            "timelineId",
            "timelineEpoch",
            "saveRevision",
            "stateVersion",
            "stateDigest",
            "incarnationDigest",
            "issuedIncarnationDigest",
            "recordDigest",
            "timelineDigest",
            "snapshotDigest",
            "incarnationCount",
            "issuedIncarnationCount",
            "transactionCount",
            "historyCount",
            "transactionCompleteness",
            "historyCompleteness",
            "parentSaveDigest");

    private static readonly HashSet<string> LegacyMetadataFields =
        Fields(
            "contract",
            "artifactKind",
            "packageDigest",
            "catalogDigest",
            "worldId",
            "timelineId",
            "timelineEpoch",
            "saveRevision",
            "stateVersion",
            "stateDigest",
            "incarnationDigest",
            "recordDigest",
            "timelineDigest",
            "snapshotDigest",
            "incarnationCount",
            "transactionCount",
            "historyCount",
            "transactionCompleteness",
            "historyCompleteness",
            "parentSaveDigest");

    private static readonly HashSet<string> IncarnationFields =
        Fields("kind", "entityId", "incarnation");

    private static readonly HashSet<string> IssuedIncarnationFields =
        Fields("kind", "entityId", "incarnation");

    private static readonly HashSet<string> IssuedIncarnationLedgerFields =
        Fields("kind", "entityIds", "incarnations");

    private static readonly HashSet<string>
        PackedIncarnationLedgerFields =
            Fields("kind", "encoding", "byteLength", "chunks");

    private static readonly HashSet<string> ReceiptFields =
        Fields(
            "kind",
            "request",
            "status",
            "outcomeCode",
            "resultingCoordinate",
            "resultingStateDigest",
            "effect",
            "eventInstanceId",
            "receiptId");

    private static readonly HashSet<string> RequestFields =
        Fields(
            "operationId",
            "commandId",
            "commandPayloadDigest",
            "requestFingerprint",
            "expectedCoordinate",
            "expectedIncarnations",
            "eventOccurrence");

    private static readonly HashSet<string> CoordinateFields =
        Fields(
            "worldId",
            "timelineId",
            "timelineEpoch",
            "saveRevision",
            "stateVersion",
            "catalogDigest");

    private static readonly HashSet<string> ExpectationFields =
        Fields("entityId", "incarnation");

    private static readonly HashSet<string> EffectFields =
        Fields(
            "applied",
            "outcomeCode",
            "hasTypedResult",
            "typedResultBase64");

    private static readonly HashSet<string> HistoryFields =
        Fields(
            "kind",
            "instanceId",
            "definition",
            "triggerId",
            "resolutionKey",
            "planFingerprint",
            "occurredAt",
            "parentInstanceId");

    private static readonly HashSet<string> NestedHistoryFields =
        Fields(
            "instanceId",
            "definition",
            "triggerId",
            "resolutionKey",
            "planFingerprint",
            "occurredAt",
            "parentInstanceId");

    private static readonly HashSet<string> DefinitionFields =
        Fields(
            "worldId",
            "timelineId",
            "timelineEpoch",
            "definitionId",
            "definitionVersion");

    private static readonly HashSet<string> TimeFields =
        Fields("clockId", "timelineId", "epoch", "tick");

    private static readonly HashSet<string> ScheduleRecordFields =
        Fields("kind", "recordBase64");

    private static readonly HashSet<string> ScheduleOperationFields =
        Fields("kind", "receiptBase64");

    private static readonly HashSet<string> ScheduleMetadataFields =
        Fields(
            "contract",
            "completeness",
            "scheduleCount",
            "operationCount",
            "scheduleDigest");

    public static WorldSaveDocument CreateDocument(
        ActivatedWorldPackage package,
        WorldAuthoritativeStoreCapture capture,
        NativeWorldSaveArtifactKind artifactKind,
        string? parentTimelineId,
        long? parentSaveRevision,
        string? parentSaveDigest,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = capture.Snapshot;
        ValidatePackageSnapshot(package, snapshot);
        ValidateScheduleClocks(
            package,
            capture,
            cancellationToken);
        if (snapshot.EntityIncarnations.Count
            > options.MaxEntityIncarnations)
        {
            throw Capacity(
                "The entity-incarnation collection exceeds the bridge limit.");
        }

        if (snapshot.IssuedEntityIncarnations.Count
            > options.MaxIssuedEntityIncarnations)
        {
            throw Capacity(
                "The issued-incarnation ledger exceeds the bridge limit.");
        }

        var eventLog = WriteRecords(
            capture,
            options,
            cancellationToken);
        var coordinate = snapshot.Coordinate;
        var incarnationDigest = ComputeIncarnationDigest(
            snapshot.EntityIncarnations);
        var issuedIncarnationDigest =
            ComputeIssuedIncarnationDigest(
                snapshot.IssuedEntityIncarnations);
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            eventLog,
            options.ArtifactLimits.MaxFileBytes,
            "eventLog");
        var timelineDigest = ComputeTimelineDigest(
            coordinate,
            parentTimelineId,
            parentSaveRevision,
            parentSaveDigest);
        var snapshotDigest = ComputeSnapshotDigest(
            coordinate,
            snapshot.StateDigest,
            incarnationDigest,
            issuedIncarnationDigest,
            timelineDigest);
        var metadata = WriteMetadata(
            package,
            capture,
            artifactKind,
            parentSaveDigest,
            incarnationDigest,
            issuedIncarnationDigest,
            recordDigest,
            timelineDigest,
            snapshotDigest,
            options.ArtifactLimits);
        var clocks = ReadClocks(package, snapshot);
        var empty = EmptyArray();
        var save = new WorldSaveDocument(
            package.SourcePackage.PackageId,
            package.SourcePackage.ContentVersion,
            package.SourcePackage.PackageDigest,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            clocks,
            snapshot.State,
            eventLog,
            empty,
            parentTimelineId,
            parentSaveRevision,
            pendingTransaction: null,
            trustedExtensions: null,
            extensionData: new Dictionary<string, JsonElement>(
                StringComparer.Ordinal)
            {
                [MetadataKey] = metadata,
                [ScheduleMetadataKey] = WriteScheduleMetadata(
                    capture,
                    options.ArtifactLimits)
            },
            limits: options.ArtifactLimits);

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = WorldSaveCodec.Write(
            save,
            options.ArtifactLimits);
        return WorldSaveCodec.Read(bytes, options.ArtifactLimits);
    }

    public static WorldAuthoritativeStoreCapture Read(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (save is null)
        {
            throw new ArgumentNullException(nameof(save));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        try
        {
            return ReadCore(
                package,
                save,
                options,
                cancellationToken);
        }
        catch (NativeWorldSaveBridgeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is WorldDataContractException
            or WorldScheduleStoreException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or FormatException
            or JsonException
            or KeyNotFoundException)
        {
            throw Invalid(
                "The portable world save contains malformed bridge data.",
                exception);
        }
    }

    private static WorldAuthoritativeStoreCapture ReadCore(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorldSaveDocument admitted;
        try
        {
            admitted = WorldSaveCodec.Read(
                WorldSaveCodec.Write(save, options.ArtifactLimits),
                options.ArtifactLimits);
            WorldSaveBinding.Validate(
                admitted,
                package.SourcePackage);
        }
        catch (WorldDataContractException exception)
        {
            throw new NativeWorldSaveBridgeException(
                exception.ReasonCode
                == WorldDataReasonCodes.PackageBindingMismatch
                    ? NativeWorldSaveBridgeReasonCodes.BindingMismatch
                    : NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
                "The portable world save failed admission.",
                exception);
        }

        if (!string.Equals(
                admitted.SaveDigest,
                save.SaveDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                admitted.WorldId,
                package.World.WorldId,
                StringComparison.Ordinal))
        {
            throw Binding(
                "The save does not bind the activated world.");
        }

        if (admitted.PendingTransaction.HasValue)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.PendingTransactions,
                "Version one cannot restore pending authoritative work.");
        }

        if (admitted.MemoryReferences.GetArrayLength() != 0
            || admitted.ExtensionData.Count is < 1 or > 2
            || !admitted.ExtensionData.TryGetValue(
                MetadataKey,
                out var metadata)
            || admitted.ExtensionData.Keys.Any(
                key => !string.Equals(
                           key,
                           MetadataKey,
                           StringComparison.Ordinal)
                       && !string.Equals(
                           key,
                           ScheduleMetadataKey,
                           StringComparison.Ordinal)))
        {
            throw Invalid(
                "The save contains unsupported bridge payloads.");
        }

        var hasIssuedMetadata = metadata.TryGetProperty(
            "issuedIncarnationDigest",
            out _);
        if (hasIssuedMetadata
            != metadata.TryGetProperty(
                "issuedIncarnationCount",
                out _))
        {
            throw Invalid(
                "The save metadata contains an incomplete issued-incarnation binding.");
        }

        RequireObject(
            metadata,
            hasIssuedMetadata
                ? MetadataFields
                : LegacyMetadataFields);
        if (!string.Equals(
                RequiredString(metadata, "contract", 128),
                MetadataContract,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(
                    metadata,
                    "transactionCompleteness",
                    32),
                "complete",
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(
                    metadata,
                    "historyCompleteness",
                    32),
                "complete",
                StringComparison.Ordinal))
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.IncompleteHistory,
                "The save does not claim complete settled history.");
        }

        var kind = ReadArtifactKind(metadata);
        ValidateParentBinding(admitted, metadata, kind);
        var timelineEpoch =
            RequiredInt64String(metadata, "timelineEpoch", 0);
        var stateVersion =
            RequiredInt64String(metadata, "stateVersion", 0);
        if (!string.Equals(
                RequiredString(metadata, "packageDigest", 64),
                package.SourcePackage.PackageDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(metadata, "catalogDigest", 64),
                package.CatalogDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(metadata, "worldId", 256),
                admitted.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(metadata, "timelineId", 256),
                admitted.TimelineId,
                StringComparison.Ordinal)
            || RequiredInt64String(
                metadata,
                "saveRevision",
                0) != admitted.SaveRevision
            || !string.Equals(
                stateVersion.ToString(CultureInfo.InvariantCulture),
                admitted.StateVersion,
                StringComparison.Ordinal))
        {
            throw Binding(
                "The save metadata does not match its package or timeline.");
        }

        var records = ReadRecords(
            admitted.EventLog,
            options,
            cancellationToken);
        var coordinate = new WorldAuthoritativeCoordinate(
            admitted.WorldId,
            admitted.TimelineId,
            timelineEpoch,
            admitted.SaveRevision,
            stateVersion,
            package.CatalogDigest);
        var snapshot = new WorldAuthoritativeStateSnapshot(
            coordinate,
            admitted.State,
            records.Incarnations,
            hasIssuedMetadata
                ? records.IssuedIncarnations
                : null);
        if (!hasIssuedMetadata
            && records.HasIssuedIncarnationRecords)
        {
            throw Invalid(
                "Legacy save metadata cannot carry an issued-incarnation ledger.");
        }
        var issuedEncoding = hasIssuedMetadata
            ? records.IssuedEncoding
              == NativeWorldIssuedIncarnationEncoding.None
                ? NativeWorldIssuedIncarnationEncoding.ObjectRecords
                : records.IssuedEncoding
            : NativeWorldIssuedIncarnationEncoding.None;
        var capture = new WorldAuthoritativeStoreCapture(
            snapshot,
            records.Receipts,
            records.History,
            options.MaxTransactionRecords,
            options.MaxHistoryRecords,
            records.Schedules,
            records.ScheduleOperations,
            options.MaxScheduleRecords,
            options.MaxScheduleOperations);
        ValidateScheduleClocks(
            package,
            capture,
            cancellationToken);

        ValidateMetadata(
            admitted,
            metadata,
            capture,
            options,
            hasIssuedMetadata);
        ValidateScheduleMetadata(
            admitted.ExtensionData.TryGetValue(
                ScheduleMetadataKey,
                out var scheduleMetadata)
                ? scheduleMetadata
                : (JsonElement?)null,
            capture);
        ValidateClocks(package, admitted, snapshot);
        var canonicalRecords = WriteRecords(
            capture,
            options,
            cancellationToken,
            issuedEncoding);
        if (!CanonicalBytes(
                canonicalRecords,
                options.ArtifactLimits)
            .AsSpan()
            .SequenceEqual(
                CanonicalBytes(
                    admitted.EventLog,
                    options.ArtifactLimits)))
        {
            throw Invalid(
                "The bridge record stream is not in deterministic order.");
        }

        return capture;
    }

    public static WorldAuthoritativeStoreCapture Fork(
        WorldAuthoritativeStoreCapture source,
        string forkTimelineId,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceCoordinate = source.Snapshot.Coordinate;
        if (sourceCoordinate.TimelineEpoch == long.MaxValue)
        {
            throw Capacity(
                "The source timeline epoch cannot be advanced.");
        }

        var forkEpoch = sourceCoordinate.TimelineEpoch + 1;
        var coordinate = new WorldAuthoritativeCoordinate(
            sourceCoordinate.WorldId,
            forkTimelineId,
            forkEpoch,
            saveRevision: 0,
            stateVersion: 0,
            sourceCoordinate.CatalogDigest);
        var snapshot = new WorldAuthoritativeStateSnapshot(
            coordinate,
            source.Snapshot.State,
            source.Snapshot.EntityIncarnations,
            source.Snapshot.IssuedEntityIncarnations);
        var history = new List<WorldEventHistoryRecord>(
            source.History.Count);
        foreach (var record in source.History)
        {
            cancellationToken.ThrowIfCancellationRequested();
            history.Add(Rehome(record, coordinate));
        }

        var scheduleScope = coordinate.Scope;
        var schedules = new List<WorldScheduleRecord>(
            source.Schedules.Count);
        foreach (var schedule in source.Schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            schedules.Add(schedule.Rehome(scheduleScope));
        }

        return new WorldAuthoritativeStoreCapture(
            snapshot,
            [],
            history,
            options.MaxTransactionRecords,
            options.MaxHistoryRecords,
            schedules,
            [],
            options.MaxScheduleRecords,
            options.MaxScheduleOperations);
    }

    public static void EnsureEquivalent(
        WorldAuthoritativeStoreCapture expected,
        WorldAuthoritativeStoreCapture actual,
        NativeWorldSaveBridgeOptions options)
    {
        var left = expected.Snapshot;
        var right = actual.Snapshot;
        if (!left.Coordinate.IsExactMatch(right.Coordinate)
            || !string.Equals(
                left.StateDigest,
                right.StateDigest,
                StringComparison.Ordinal)
            || left.EntityIncarnations.Count
            != right.EntityIncarnations.Count
            || left.EntityIncarnations.Any(
                pair => !right.EntityIncarnations.TryGetValue(
                                pair.Key,
                                out var value)
                            || value != pair.Value)
            || left.IssuedEntityIncarnations.Count
            != right.IssuedEntityIncarnations.Count
            || !left.IssuedEntityIncarnations
                .Select(
                    item => new
                    {
                        item.EntityId,
                        item.Incarnation
                    })
                .SequenceEqual(
                    right.IssuedEntityIncarnations.Select(
                        item => new
                        {
                            item.EntityId,
                            item.Incarnation
                        }))
            || !expected.Receipts.Select(item => item.ReceiptId)
                .SequenceEqual(
                    actual.Receipts.Select(item => item.ReceiptId),
                    StringComparer.Ordinal)
            || expected.History.Count != actual.History.Count
            || !expected.Schedules.Select(item => item.RecordDigest)
                .SequenceEqual(
                    actual.Schedules.Select(
                        item => item.RecordDigest),
                    StringComparer.Ordinal)
            || !expected.ScheduleOperations.Select(
                    item => item.ReceiptId)
                .SequenceEqual(
                    actual.ScheduleOperations.Select(
                        item => item.ReceiptId),
                    StringComparer.Ordinal))
        {
            throw Invalid(
                "The seeded store does not match the admitted save.");
        }

        for (var index = 0; index < expected.History.Count; index++)
        {
            if (!expected.History[index].IsEquivalentTo(
                    actual.History[index]))
            {
                throw Invalid(
                    "The seeded event history does not match the admitted save.");
            }
        }

        if (actual.Receipts.Count > options.MaxTransactionRecords
            || actual.History.Count > options.MaxHistoryRecords
            || actual.Snapshot.IssuedEntityIncarnations.Count
            > options.MaxIssuedEntityIncarnations
            || actual.Schedules.Count > options.MaxScheduleRecords
            || actual.ScheduleOperations.Count
            > options.MaxScheduleOperations)
        {
            throw Capacity(
                "The seeded store exceeds the bridge limits.");
        }
    }

    private static JsonElement WriteRecords(
        WorldAuthoritativeStoreCapture capture,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken,
        NativeWorldIssuedIncarnationEncoding issuedEncoding =
            NativeWorldIssuedIncarnationEncoding.PackedLedger)
    {
        var issuedRecordCount = issuedEncoding switch
        {
            NativeWorldIssuedIncarnationEncoding.None => 0,
            NativeWorldIssuedIncarnationEncoding.ObjectRecords =>
                capture.Snapshot.IssuedEntityIncarnations.Count,
            NativeWorldIssuedIncarnationEncoding.ParallelArrays => 1,
            NativeWorldIssuedIncarnationEncoding.PackedLedger => 1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(issuedEncoding))
        };
        var currentRecordCount =
            issuedEncoding
            == NativeWorldIssuedIncarnationEncoding.PackedLedger
                ? 0
                : capture.Snapshot.EntityIncarnations.Count;
        var total = checked(
            currentRecordCount
            + issuedRecordCount
            + capture.Receipts.Count
            + capture.History.Count
            + capture.Schedules.Count
            + capture.ScheduleOperations.Count);
        if (capture.Snapshot.EntityIncarnations.Count
                > options.MaxEntityIncarnations
            || capture.Snapshot.IssuedEntityIncarnations.Count
                > options.MaxIssuedEntityIncarnations
            || issuedEncoding
               == NativeWorldIssuedIncarnationEncoding.ParallelArrays
               && capture.Snapshot.IssuedEntityIncarnations.Count
               > options.ArtifactLimits.MaxJsonContainerItems
            || capture.Receipts.Count
                > options.MaxTransactionRecords
            || capture.History.Count > options.MaxHistoryRecords
            || capture.Schedules.Count
                > options.MaxScheduleRecords
            || capture.ScheduleOperations.Count
                > options.MaxScheduleOperations
            || total > options.ArtifactLimits.MaxJsonContainerItems)
        {
            throw Capacity(
                "The bridge record stream exceeds its item limit.");
        }

        return WriteJson(
            writer =>
            {
                writer.WriteStartArray();
                if (issuedEncoding
                    != NativeWorldIssuedIncarnationEncoding.PackedLedger)
                {
                    foreach (var pair in capture.Snapshot
                                 .EntityIncarnations
                                 .OrderBy(
                                     item => item.Key,
                                     StringComparer.Ordinal))
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString("kind", "incarnation");
                        writer.WriteString("entityId", pair.Key);
                        WriteInt64String(
                            writer,
                            "incarnation",
                            pair.Value);
                        writer.WriteEndObject();
                    }
                }

                if (issuedEncoding
                    == NativeWorldIssuedIncarnationEncoding.ObjectRecords)
                {
                    foreach (var item in
                             capture.Snapshot.IssuedEntityIncarnations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString(
                            "kind",
                            "issuedIncarnation");
                        writer.WriteString("entityId", item.EntityId);
                        WriteInt64String(
                            writer,
                            "incarnation",
                            item.Incarnation);
                        writer.WriteEndObject();
                    }
                }
                else if (issuedEncoding
                         == NativeWorldIssuedIncarnationEncoding
                             .ParallelArrays)
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "kind",
                        "issuedIncarnationLedger");
                    writer.WritePropertyName("entityIds");
                    writer.WriteStartArray();
                    foreach (var item in
                             capture.Snapshot.IssuedEntityIncarnations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStringValue(item.EntityId);
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("incarnations");
                    writer.WriteStartArray();
                    foreach (var item in
                             capture.Snapshot.IssuedEntityIncarnations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStringValue(
                            item.Incarnation.ToString(
                                CultureInfo.InvariantCulture));
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                else if (issuedEncoding
                         == NativeWorldIssuedIncarnationEncoding
                             .PackedLedger)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunks =
                        NativeWorldIncarnationLedgerCodec.Encode(
                            capture.Snapshot.EntityIncarnations,
                            capture.Snapshot
                                .IssuedEntityIncarnations,
                            out var byteLength);
                    if (chunks.Count
                            > options.ArtifactLimits
                                .MaxJsonContainerItems
                        || chunks.Any(
                            chunk => chunk.Length
                                     > options.ArtifactLimits
                                         .MaxJsonStringUtf8Bytes))
                    {
                        throw Capacity(
                            "The packed incarnation ledger exceeds the bridge JSON limits.");
                    }

                    writer.WriteStartObject();
                    writer.WriteString(
                        "kind",
                        "packedIncarnationLedger");
                    writer.WriteString("encoding", "base85-v1");
                    WriteInt64String(
                        writer,
                        "byteLength",
                        byteLength);
                    writer.WritePropertyName("chunks");
                    writer.WriteStartArray();
                    foreach (var chunk in chunks)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        writer.WriteStringValue(chunk);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                foreach (var receipt in capture.Receipts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteReceipt(writer, receipt);
                }

                foreach (var record in capture.History)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteHistory(writer, record, includeKind: true);
                }

                foreach (var schedule in capture.Schedules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    writer.WriteString("kind", "schedule");
                    writer.WriteString(
                        "recordBase64",
                        Convert.ToBase64String(
                            WriteScheduleRecord(
                                schedule,
                                options.ArtifactLimits)));
                    writer.WriteEndObject();
                }

                foreach (var operation in capture.ScheduleOperations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    writer.WriteString(
                        "kind",
                        "scheduleOperation");
                    writer.WriteString(
                        "receiptBase64",
                        Convert.ToBase64String(
                            WriteScheduleOperation(
                                operation,
                                options.ArtifactLimits)));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            },
            options.ArtifactLimits,
            "Bridge record stream exceeds its byte limit.");
    }

    private static BridgeRecords ReadRecords(
        JsonElement eventLog,
        NativeWorldSaveBridgeOptions options,
        CancellationToken cancellationToken)
    {
        if (eventLog.ValueKind != JsonValueKind.Array
            || eventLog.GetArrayLength()
            > options.ArtifactLimits.MaxJsonContainerItems)
        {
            throw Capacity(
                "The bridge record stream exceeds its item limit.");
        }

        var incarnations = new Dictionary<string, long>(
            StringComparer.Ordinal);
        var issuedIncarnations =
            new List<WorldIssuedEntityIncarnation>();
        var receipts = new List<WorldCommandReceipt>();
        var history = new List<WorldEventHistoryRecord>();
        var schedules = new List<WorldScheduleRecord>();
        var scheduleOperations =
            new List<WorldScheduleOperationReceipt>();
        var phase = 0;
        string? previousKey = null;
        string? previousIssuedEntityId = null;
        long previousIssuedIncarnation = -1;
        var issuedEncoding =
            NativeWorldIssuedIncarnationEncoding.None;
        foreach (var item in eventLog.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = RequiredString(item, "kind", 32);
            switch (kind)
            {
                case "incarnation":
                    if (phase != 0)
                    {
                        throw Invalid(
                            "Entity incarnations are out of order.");
                    }

                    if (incarnations.Count
                        >= options.MaxEntityIncarnations)
                    {
                        throw Capacity(
                            "Entity incarnations exceed the bridge limit.");
                    }

                    RequireObject(item, IncarnationFields);
                    var entityId =
                        RequiredString(item, "entityId", 192);
                    EnsureOrdered(previousKey, entityId);
                    previousKey = entityId;
                    if (!incarnations.TryAdd(
                            entityId,
                            RequiredInt64String(
                                item,
                                "incarnation",
                                0)))
                    {
                        throw Invalid(
                            "Entity incarnation identifiers must be unique.");
                    }

                    break;
                case "issuedIncarnation":
                    if (phase == 0)
                    {
                        phase = 1;
                        previousKey = null;
                    }

                    if (phase != 1)
                    {
                        throw Invalid(
                            "Issued entity incarnations are out of order.");
                    }

                    if (issuedEncoding
                        is NativeWorldIssuedIncarnationEncoding
                            .ParallelArrays
                            or NativeWorldIssuedIncarnationEncoding
                                .PackedLedger)
                    {
                        throw Invalid(
                            "Issued entity incarnation encodings cannot be mixed.");
                    }

                    issuedEncoding =
                        NativeWorldIssuedIncarnationEncoding.ObjectRecords;
                    if (issuedIncarnations.Count
                        >= options.MaxIssuedEntityIncarnations)
                    {
                        throw Capacity(
                            "Issued entity incarnations exceed the bridge limit.");
                    }

                    RequireObject(item, IssuedIncarnationFields);
                    var issuedEntityId =
                        RequiredString(item, "entityId", 192);
                    var issuedIncarnation =
                        RequiredInt64String(
                            item,
                            "incarnation",
                            0);
                    var entityComparison = previousIssuedEntityId is null
                        ? -1
                        : string.CompareOrdinal(
                            previousIssuedEntityId,
                            issuedEntityId);
                    if (entityComparison > 0
                        || (entityComparison == 0
                            && previousIssuedIncarnation
                            >= issuedIncarnation))
                    {
                        throw Invalid(
                            "Issued entity incarnations must be unique and deterministically ordered.");
                    }

                    previousIssuedEntityId = issuedEntityId;
                    previousIssuedIncarnation = issuedIncarnation;
                    issuedIncarnations.Add(
                        new WorldIssuedEntityIncarnation(
                            issuedEntityId,
                            issuedIncarnation));
                    break;
                case "issuedIncarnationLedger":
                    if (phase == 0)
                    {
                        phase = 1;
                        previousKey = null;
                    }

                    if (phase != 1
                        || issuedEncoding
                        != NativeWorldIssuedIncarnationEncoding.None)
                    {
                        throw Invalid(
                            "The issued-incarnation ledger is duplicated, mixed, or out of order.");
                    }

                    RequireObject(
                        item,
                        IssuedIncarnationLedgerFields);
                    var entityIds = RequiredProperty(
                        item,
                        "entityIds");
                    var issuedValues = RequiredProperty(
                        item,
                        "incarnations");
                    if (entityIds.ValueKind != JsonValueKind.Array
                        || issuedValues.ValueKind
                        != JsonValueKind.Array)
                    {
                        throw Invalid(
                            "The issued-incarnation ledger arrays are invalid.");
                    }

                    var issuedCount = entityIds.GetArrayLength();
                    if (issuedCount != issuedValues.GetArrayLength())
                    {
                        throw Invalid(
                            "The issued-incarnation ledger arrays have different lengths.");
                    }

                    if (issuedCount
                            > options.MaxIssuedEntityIncarnations
                        || issuedCount
                            > options.ArtifactLimits
                                .MaxJsonContainerItems)
                    {
                        throw Capacity(
                            "Issued entity incarnations exceed the bridge limit.");
                    }

                    var entityEnumerator =
                        entityIds.EnumerateArray();
                    var incarnationEnumerator =
                        issuedValues.EnumerateArray();
                    while (entityEnumerator.MoveNext()
                           && incarnationEnumerator.MoveNext())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var compactEntityId = RequiredStringValue(
                            entityEnumerator.Current,
                            192);
                        var compactIncarnation =
                            RequiredInt64StringValue(
                                incarnationEnumerator.Current,
                                0);
                        var compactComparison =
                            previousIssuedEntityId is null
                                ? -1
                                : string.CompareOrdinal(
                                    previousIssuedEntityId,
                                    compactEntityId);
                        if (compactComparison > 0
                            || (compactComparison == 0
                                && previousIssuedIncarnation
                                >= compactIncarnation))
                        {
                            throw Invalid(
                                "Issued entity incarnations must be unique and deterministically ordered.");
                        }

                        previousIssuedEntityId = compactEntityId;
                        previousIssuedIncarnation =
                            compactIncarnation;
                        issuedIncarnations.Add(
                            new WorldIssuedEntityIncarnation(
                                compactEntityId,
                                compactIncarnation));
                    }

                    issuedEncoding =
                        NativeWorldIssuedIncarnationEncoding
                            .ParallelArrays;
                    break;
                case "packedIncarnationLedger":
                    if (phase != 0
                        || incarnations.Count != 0
                        || issuedIncarnations.Count != 0
                        || issuedEncoding
                        != NativeWorldIssuedIncarnationEncoding.None)
                    {
                        throw Invalid(
                            "The packed incarnation ledger is duplicated, mixed, or out of order.");
                    }

                    RequireObject(
                        item,
                        PackedIncarnationLedgerFields);
                    if (!string.Equals(
                            RequiredString(
                                item,
                                "encoding",
                                32),
                            "base85-v1",
                            StringComparison.Ordinal))
                    {
                        throw Invalid(
                            "The packed incarnation ledger encoding is unsupported.");
                    }

                    var packedByteLength = RequiredInt64String(
                        item,
                        "byteLength",
                        1);
                    if (packedByteLength
                        > options.ArtifactLimits.MaxFileBytes
                        || packedByteLength > int.MaxValue)
                    {
                        throw Capacity(
                            "The packed incarnation ledger exceeds its byte limit.");
                    }

                    var packedChunksValue = RequiredProperty(
                        item,
                        "chunks");
                    if (packedChunksValue.ValueKind
                            != JsonValueKind.Array
                        || packedChunksValue.GetArrayLength() is < 1
                            or > 8)
                    {
                        throw Invalid(
                            "The packed incarnation ledger chunks are invalid.");
                    }

                    var packedChunks = new List<string>(
                        packedChunksValue.GetArrayLength());
                    foreach (var chunk in
                             packedChunksValue.EnumerateArray())
                    {
                        packedChunks.Add(
                            RequiredStringValue(
                                chunk,
                                options.ArtifactLimits
                                    .MaxJsonStringUtf8Bytes));
                    }

                    NativeWorldPackedIncarnationLedger packed;
                    try
                    {
                        packed =
                            NativeWorldIncarnationLedgerCodec.Decode(
                                packedChunks,
                                checked((int)packedByteLength),
                                options.MaxIssuedEntityIncarnations,
                                options.MaxEntityIncarnations);
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException
                        or ArgumentException
                        or OverflowException)
                    {
                        throw Invalid(
                            "The packed incarnation ledger is invalid.",
                            exception);
                    }

                    foreach (var pair in packed.Current)
                    {
                        incarnations.Add(pair.Key, pair.Value);
                    }

                    issuedIncarnations.AddRange(packed.Issued);
                    issuedEncoding =
                        NativeWorldIssuedIncarnationEncoding
                            .PackedLedger;
                    phase = 1;
                    break;
                case "receipt":
                    if (phase < 2)
                    {
                        phase = 2;
                        previousKey = null;
                    }

                    if (phase != 2)
                    {
                        throw Invalid(
                            "Transaction records are out of order.");
                    }

                    if (receipts.Count
                        >= options.MaxTransactionRecords)
                    {
                        throw Capacity(
                            "Transaction records exceed the bridge limit.");
                    }

                    RequireObject(item, ReceiptFields);
                    var receipt = ReadReceipt(
                        item,
                        options.ArtifactLimits);
                    var receiptKey =
                        receipt.Request.ExpectedCoordinate.Scope.StableKey
                        + "\n"
                        + receipt.OperationId;
                    EnsureOrdered(previousKey, receiptKey);
                    previousKey = receiptKey;
                    receipts.Add(receipt);
                    break;
                case "history":
                    if (phase < 3)
                    {
                        phase = 3;
                        previousKey = null;
                    }

                    if (phase != 3)
                    {
                        throw Invalid(
                            "Event history records are out of order.");
                    }

                    if (history.Count >= options.MaxHistoryRecords)
                    {
                        throw Capacity(
                            "Event history exceeds the bridge limit.");
                    }

                    RequireObject(item, HistoryFields);
                    var record = ReadHistory(item, includeKind: true);
                    EnsureOrdered(previousKey, record.InstanceId);
                    previousKey = record.InstanceId;
                    history.Add(record);
                    break;
                case "schedule":
                    if (phase < 4)
                    {
                        phase = 4;
                        previousKey = null;
                    }

                    if (phase != 4)
                    {
                        throw Invalid(
                            "Schedule records are out of order.");
                    }

                    if (schedules.Count
                        >= options.MaxScheduleRecords)
                    {
                        throw Capacity(
                            "Schedules exceed the bridge limit.");
                    }

                    RequireObject(item, ScheduleRecordFields);
                    var schedule = ReadScheduleRecord(
                        RequiredString(
                            item,
                            "recordBase64",
                            options.ArtifactLimits
                                .MaxJsonStringUtf8Bytes),
                        options.ArtifactLimits);
                    EnsureOrdered(
                        previousKey,
                        schedule.StableKey);
                    previousKey = schedule.StableKey;
                    schedules.Add(schedule);
                    break;
                case "scheduleOperation":
                    if (phase < 5)
                    {
                        phase = 5;
                        previousKey = null;
                    }

                    if (phase != 5)
                    {
                        throw Invalid(
                            "Schedule operation records are out of order.");
                    }

                    if (scheduleOperations.Count
                        >= options.MaxScheduleOperations)
                    {
                        throw Capacity(
                            "Schedule operations exceed the bridge limit.");
                    }

                    RequireObject(
                        item,
                        ScheduleOperationFields);
                    var scheduleOperation =
                        ReadScheduleOperation(
                            RequiredString(
                                item,
                                "receiptBase64",
                                options.ArtifactLimits
                                    .MaxJsonStringUtf8Bytes),
                            options.ArtifactLimits);
                    var scheduleOperationKey =
                        scheduleOperation.Scope.StableKey
                        + "\n"
                        + scheduleOperation.OperationId;
                    EnsureOrdered(
                        previousKey,
                        scheduleOperationKey);
                    previousKey = scheduleOperationKey;
                    scheduleOperations.Add(scheduleOperation);
                    break;
                default:
                    throw Invalid(
                        "The bridge record stream contains an unknown kind.");
            }
        }

        return new BridgeRecords(
            incarnations,
            new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
                issuedIncarnations),
            issuedEncoding,
            receipts,
            history,
            schedules,
            scheduleOperations);
    }

    private static void WriteReceipt(
        Utf8JsonWriter writer,
        WorldCommandReceipt receipt)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "receipt");
        writer.WritePropertyName("request");
        WriteRequest(writer, receipt.Request);
        WriteInt64String(writer, "status", (int)receipt.Status);
        writer.WriteString("outcomeCode", receipt.OutcomeCode);
        writer.WritePropertyName("resultingCoordinate");
        if (receipt.ResultingCoordinate is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteCoordinate(writer, receipt.ResultingCoordinate);
        }

        WriteOptionalString(
            writer,
            "resultingStateDigest",
            receipt.ResultingStateDigest);
        writer.WritePropertyName("effect");
        if (receipt.Effect is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteBoolean("applied", receipt.Effect.Applied);
            writer.WriteString(
                "outcomeCode",
                receipt.Effect.OutcomeCode);
            writer.WriteBoolean(
                "hasTypedResult",
                receipt.Effect.TypedResult.HasValue);
            writer.WritePropertyName("typedResultBase64");
            if (receipt.Effect.TypedResult.HasValue)
            {
                writer.WriteStringValue(
                    Convert.ToBase64String(
                        WriteCanonicalJson(
                            receipt.Effect.TypedResult.Value,
                            262_144)));
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        WriteOptionalString(
            writer,
            "eventInstanceId",
            receipt.EventInstanceId);
        writer.WriteString("receiptId", receipt.ReceiptId);
        writer.WriteEndObject();
    }

    private static WorldCommandReceipt ReadReceipt(
        JsonElement value,
        WorldPackageLimits limits)
    {
        var request = ReadRequest(
            RequiredObject(value, "request"));
        var rawStatus = RequiredInt64String(value, "status", 0);
        if (rawStatus > (int)WorldCommandReceiptStatus.Cancelled)
        {
            throw Invalid("A transaction status is unknown.");
        }

        WorldAuthoritativeCoordinate? resulting = null;
        var coordinateValue = RequiredProperty(
            value,
            "resultingCoordinate");
        if (coordinateValue.ValueKind == JsonValueKind.Object)
        {
            resulting = ReadCoordinate(coordinateValue);
        }
        else if (coordinateValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid(
                "A resulting coordinate has an invalid shape.");
        }

        WorldEffectReceipt? effect = null;
        var effectValue = RequiredProperty(value, "effect");
        if (effectValue.ValueKind == JsonValueKind.Object)
        {
            RequireObject(effectValue, EffectFields);
            var hasTypedResult = RequiredBoolean(
                effectValue,
                "hasTypedResult");
            JsonElement? typedResult = null;
            var encoded = RequiredProperty(
                effectValue,
                "typedResultBase64");
            if (hasTypedResult)
            {
                if (encoded.ValueKind != JsonValueKind.String)
                {
                    throw Invalid(
                        "A typed result is missing its encoded JSON.");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(
                        encoded.GetString() ?? string.Empty);
                }
                catch (FormatException exception)
                {
                    throw Invalid(
                        "A typed result is not valid base64.",
                        exception);
                }

                if (bytes.Length > 262_144)
                {
                    throw Capacity(
                        "A typed result exceeds its byte limit.");
                }

                using var document = WorldDataJson.Parse(
                    bytes,
                    limits,
                    "typedResult");
                typedResult = document.RootElement.Clone();
            }
            else if (encoded.ValueKind != JsonValueKind.Null)
            {
                throw Invalid(
                    "A typed result presence marker is inconsistent.");
            }

            effect = new WorldEffectReceipt(
                RequiredBoolean(effectValue, "applied"),
                RequiredString(effectValue, "outcomeCode", 96),
                typedResult);
        }
        else if (effectValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid("A transaction effect has an invalid shape.");
        }

        WorldCommandReceipt receipt;
        try
        {
            receipt = new WorldCommandReceipt(
                request,
                (WorldCommandReceiptStatus)rawStatus,
                RequiredString(value, "outcomeCode", 96),
                resulting,
                OptionalString(
                    value,
                    "resultingStateDigest",
                    64),
                effect,
                OptionalString(value, "eventInstanceId", 192));
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "A transaction receipt violates its invariants.",
                exception);
        }

        if (!string.Equals(
                receipt.ReceiptId,
                RequiredString(value, "receiptId", 128),
                StringComparison.Ordinal))
        {
            throw Invalid("A transaction receipt identity is invalid.");
        }

        return receipt;
    }

    private static void WriteRequest(
        Utf8JsonWriter writer,
        WorldTransactionRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("operationId", request.OperationId);
        writer.WriteString("commandId", request.CommandId);
        writer.WriteString(
            "commandPayloadDigest",
            request.CommandPayloadDigest);
        writer.WriteString(
            "requestFingerprint",
            request.RequestFingerprint);
        writer.WritePropertyName("expectedCoordinate");
        WriteCoordinate(writer, request.ExpectedCoordinate);
        writer.WritePropertyName("expectedIncarnations");
        writer.WriteStartArray();
        foreach (var item in request.ExpectedIncarnations)
        {
            writer.WriteStartObject();
            writer.WriteString("entityId", item.EntityId);
            WriteInt64String(
                writer,
                "incarnation",
                item.Incarnation);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("eventOccurrence");
        if (request.EventOccurrence is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteHistory(
                writer,
                request.EventOccurrence,
                includeKind: false);
        }

        writer.WriteEndObject();
    }

    private static WorldTransactionRequest ReadRequest(JsonElement value)
    {
        RequireObject(value, RequestFields);
        var expectationsValue = RequiredProperty(
            value,
            "expectedIncarnations");
        if (expectationsValue.ValueKind != JsonValueKind.Array
            || expectationsValue.GetArrayLength()
            > WorldValidation.MaximumParticipants)
        {
            throw Capacity(
                "Expected incarnations exceed their item limit.");
        }

        var expectations =
            new List<WorldEntityIncarnationExpectation>();
        string? previous = null;
        foreach (var item in expectationsValue.EnumerateArray())
        {
            RequireObject(item, ExpectationFields);
            var entityId = RequiredString(item, "entityId", 192);
            EnsureOrdered(previous, entityId);
            previous = entityId;
            expectations.Add(
                new WorldEntityIncarnationExpectation(
                    entityId,
                    RequiredInt64String(
                        item,
                        "incarnation",
                        0)));
        }

        WorldEventHistoryRecord? occurrence = null;
        var occurrenceValue = RequiredProperty(
            value,
            "eventOccurrence");
        if (occurrenceValue.ValueKind == JsonValueKind.Object)
        {
            occurrence = ReadHistory(
                occurrenceValue,
                includeKind: false);
        }
        else if (occurrenceValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid(
                "An event occurrence has an invalid shape.");
        }

        WorldTransactionRequest request;
        try
        {
            request = new WorldTransactionRequest(
                RequiredString(value, "operationId", 192),
                RequiredString(value, "commandId", 192),
                RequiredString(
                    value,
                    "commandPayloadDigest",
                    64),
                ReadCoordinate(
                    RequiredObject(
                        value,
                        "expectedCoordinate")),
                expectations,
                occurrence);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "A transaction request violates its invariants.",
                exception);
        }

        if (!string.Equals(
                request.RequestFingerprint,
                RequiredString(value, "requestFingerprint", 64),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "A transaction request fingerprint is invalid.");
        }

        return request;
    }

    private static void WriteCoordinate(
        Utf8JsonWriter writer,
        WorldAuthoritativeCoordinate coordinate)
    {
        writer.WriteStartObject();
        writer.WriteString("worldId", coordinate.WorldId);
        writer.WriteString("timelineId", coordinate.TimelineId);
        WriteInt64String(
            writer,
            "timelineEpoch",
            coordinate.TimelineEpoch);
        WriteInt64String(
            writer,
            "saveRevision",
            coordinate.SaveRevision);
        WriteInt64String(
            writer,
            "stateVersion",
            coordinate.StateVersion);
        writer.WriteString(
            "catalogDigest",
            coordinate.CatalogDigest);
        writer.WriteEndObject();
    }

    private static WorldAuthoritativeCoordinate ReadCoordinate(
        JsonElement value)
    {
        RequireObject(value, CoordinateFields);
        try
        {
            return new WorldAuthoritativeCoordinate(
                RequiredString(value, "worldId", 256),
                RequiredString(value, "timelineId", 256),
                RequiredInt64String(value, "timelineEpoch", 0),
                RequiredInt64String(value, "saveRevision", 0),
                RequiredInt64String(value, "stateVersion", 0),
                RequiredString(value, "catalogDigest", 64));
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "An authoritative coordinate is invalid.",
                exception);
        }
    }

    private static void WriteHistory(
        Utf8JsonWriter writer,
        WorldEventHistoryRecord record,
        bool includeKind)
    {
        writer.WriteStartObject();
        if (includeKind)
        {
            writer.WriteString("kind", "history");
        }

        writer.WriteString("instanceId", record.InstanceId);
        writer.WritePropertyName("definition");
        writer.WriteStartObject();
        writer.WriteString(
            "worldId",
            record.Definition.WorldId);
        writer.WriteString(
            "timelineId",
            record.Definition.TimelineId);
        WriteInt64String(
            writer,
            "timelineEpoch",
            record.Definition.TimelineEpoch);
        writer.WriteString(
            "definitionId",
            record.Definition.DefinitionId);
        writer.WriteString(
            "definitionVersion",
            record.Definition.DefinitionVersion);
        writer.WriteEndObject();
        writer.WriteString("triggerId", record.TriggerId);
        writer.WriteString(
            "resolutionKey",
            record.ResolutionKey);
        writer.WriteString(
            "planFingerprint",
            record.PlanFingerprint);
        writer.WritePropertyName("occurredAt");
        if (record.OccurredAt is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString(
                "clockId",
                record.OccurredAt.ClockId);
            writer.WriteString(
                "timelineId",
                record.OccurredAt.TimelineId);
            WriteInt64String(
                writer,
                "epoch",
                record.OccurredAt.Epoch);
            WriteInt64String(
                writer,
                "tick",
                record.OccurredAt.Tick);
            writer.WriteEndObject();
        }

        WriteOptionalString(
            writer,
            "parentInstanceId",
            record.ParentInstanceId);
        writer.WriteEndObject();
    }

    private static WorldEventHistoryRecord ReadHistory(
        JsonElement value,
        bool includeKind)
    {
        RequireObject(
            value,
            includeKind ? HistoryFields : NestedHistoryFields);
        if (includeKind
            && !string.Equals(
                RequiredString(value, "kind", 32),
                "history",
                StringComparison.Ordinal))
        {
            throw Invalid("An event record kind is invalid.");
        }

        var definitionValue = RequiredObject(
            value,
            "definition");
        RequireObject(definitionValue, DefinitionFields);
        var definition = new WorldEventDefinitionKey(
            RequiredString(definitionValue, "worldId", 256),
            RequiredString(definitionValue, "timelineId", 256),
            RequiredInt64String(
                definitionValue,
                "timelineEpoch",
                0),
            RequiredString(
                definitionValue,
                "definitionId",
                192),
            RequiredString(
                definitionValue,
                "definitionVersion",
                96));
        GameTimePoint? occurredAt = null;
        var timeValue = RequiredProperty(value, "occurredAt");
        if (timeValue.ValueKind == JsonValueKind.Object)
        {
            RequireObject(timeValue, TimeFields);
            occurredAt = new GameTimePoint(
                RequiredString(timeValue, "clockId", 192),
                RequiredString(timeValue, "timelineId", 256),
                RequiredInt64String(timeValue, "epoch", 0),
                RequiredInt64String(
                    timeValue,
                    "tick",
                    long.MinValue));
        }
        else if (timeValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid(
                "An event occurrence time has an invalid shape.");
        }

        try
        {
            return new WorldEventHistoryRecord(
                RequiredString(value, "instanceId", 192),
                definition,
                RequiredString(value, "triggerId", 192),
                RequiredString(value, "resolutionKey", 192),
                RequiredString(value, "planFingerprint", 128),
                occurredAt,
                OptionalString(
                    value,
                    "parentInstanceId",
                    192));
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "An event history record violates its invariants.",
                exception);
        }
    }

    private static JsonElement WriteMetadata(
        ActivatedWorldPackage package,
        WorldAuthoritativeStoreCapture capture,
        NativeWorldSaveArtifactKind artifactKind,
        string? parentSaveDigest,
        string incarnationDigest,
        string issuedIncarnationDigest,
        string recordDigest,
        string timelineDigest,
        string snapshotDigest,
        WorldPackageLimits limits)
    {
        var coordinate = capture.Snapshot.Coordinate;
        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("contract", MetadataContract);
                writer.WriteString(
                    "artifactKind",
                    artifactKind == NativeWorldSaveArtifactKind.Settled
                        ? "settled"
                        : "fork");
                writer.WriteString(
                    "packageDigest",
                    package.SourcePackage.PackageDigest);
                writer.WriteString(
                    "catalogDigest",
                    package.CatalogDigest);
                writer.WriteString("worldId", coordinate.WorldId);
                writer.WriteString(
                    "timelineId",
                    coordinate.TimelineId);
                WriteInt64String(
                    writer,
                    "timelineEpoch",
                    coordinate.TimelineEpoch);
                WriteInt64String(
                    writer,
                    "saveRevision",
                    coordinate.SaveRevision);
                WriteInt64String(
                    writer,
                    "stateVersion",
                    coordinate.StateVersion);
                writer.WriteString(
                    "stateDigest",
                    capture.Snapshot.StateDigest);
                writer.WriteString(
                    "incarnationDigest",
                    incarnationDigest);
                writer.WriteString(
                    "issuedIncarnationDigest",
                    issuedIncarnationDigest);
                writer.WriteString("recordDigest", recordDigest);
                writer.WriteString(
                    "timelineDigest",
                    timelineDigest);
                writer.WriteString(
                    "snapshotDigest",
                    snapshotDigest);
                WriteInt64String(
                    writer,
                    "incarnationCount",
                    capture.Snapshot.EntityIncarnations.Count);
                WriteInt64String(
                    writer,
                    "issuedIncarnationCount",
                    capture.Snapshot.IssuedEntityIncarnations.Count);
                WriteInt64String(
                    writer,
                    "transactionCount",
                    capture.Receipts.Count);
                WriteInt64String(
                    writer,
                    "historyCount",
                    capture.History.Count);
                writer.WriteString(
                    "transactionCompleteness",
                    "complete");
                writer.WriteString(
                    "historyCompleteness",
                    "complete");
                WriteOptionalString(
                    writer,
                    "parentSaveDigest",
                    parentSaveDigest);
                writer.WriteEndObject();
            },
            limits,
            "Bridge metadata exceeds its byte limit.");
    }

    private static JsonElement WriteScheduleMetadata(
        WorldAuthoritativeStoreCapture capture,
        WorldPackageLimits limits)
    {
        return WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "contract",
                    ScheduleMetadataContract);
                writer.WriteString("completeness", "complete");
                WriteInt64String(
                    writer,
                    "scheduleCount",
                    capture.Schedules.Count);
                WriteInt64String(
                    writer,
                    "operationCount",
                    capture.ScheduleOperations.Count);
                writer.WriteString(
                    "scheduleDigest",
                    ComputeScheduleDigest(capture));
                writer.WriteEndObject();
            },
            limits,
            "Schedule bridge metadata exceeds its byte limit.");
    }

    private static void ValidateScheduleMetadata(
        JsonElement? metadata,
        WorldAuthoritativeStoreCapture capture)
    {
        if (!metadata.HasValue)
        {
            if (capture.Schedules.Count != 0
                || capture.ScheduleOperations.Count != 0)
            {
                throw Invalid(
                    "Schedule records require schedule metadata.");
            }

            return;
        }

        RequireObject(
            metadata.Value,
            ScheduleMetadataFields);
        if (!string.Equals(
                RequiredString(
                    metadata.Value,
                    "contract",
                    128),
                ScheduleMetadataContract,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "The schedule metadata contract is unsupported.");
        }

        if (!string.Equals(
                RequiredString(
                    metadata.Value,
                    "completeness",
                    32),
                "complete",
                StringComparison.Ordinal))
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.IncompleteHistory,
                "The save does not claim complete schedule state.");
        }

        if (RequiredInt64String(
                metadata.Value,
                "scheduleCount",
                0) != capture.Schedules.Count
            || RequiredInt64String(
                metadata.Value,
                "operationCount",
                0) != capture.ScheduleOperations.Count
            || !string.Equals(
                RequiredString(
                    metadata.Value,
                    "scheduleDigest",
                    64),
                ComputeScheduleDigest(capture),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "Schedule counts or digest do not match the record stream.");
        }
    }

    private static void ValidateMetadata(
        WorldSaveDocument save,
        JsonElement metadata,
        WorldAuthoritativeStoreCapture capture,
        NativeWorldSaveBridgeOptions options,
        bool hasIssuedMetadata)
    {
        var snapshot = capture.Snapshot;
        var coordinate = snapshot.Coordinate;
        var incarnationDigest = ComputeIncarnationDigest(
            snapshot.EntityIncarnations);
        var issuedIncarnationDigest =
            ComputeIssuedIncarnationDigest(
                snapshot.IssuedEntityIncarnations);
        var recordDigest = WorldLargeCanonicalJsonDigest.Compute(
            save.EventLog,
            options.ArtifactLimits.MaxFileBytes,
            "eventLog");
        var parentSaveDigest = OptionalString(
            metadata,
            "parentSaveDigest",
            64);
        var timelineDigest = ComputeTimelineDigest(
            coordinate,
            save.ParentTimelineId,
            save.ParentSaveRevision,
            parentSaveDigest);
        var snapshotDigest = ComputeSnapshotDigest(
            coordinate,
            snapshot.StateDigest,
            incarnationDigest,
            hasIssuedMetadata
                ? issuedIncarnationDigest
                : null,
            timelineDigest);
        if (!string.Equals(
                RequiredString(metadata, "stateDigest", 64),
                snapshot.StateDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(
                    metadata,
                    "incarnationDigest",
                    64),
                incarnationDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(metadata, "recordDigest", 64),
                recordDigest,
                StringComparison.Ordinal)
            || hasIssuedMetadata
               && (!string.Equals(
                       RequiredString(
                           metadata,
                           "issuedIncarnationDigest",
                           64),
                       issuedIncarnationDigest,
                       StringComparison.Ordinal)
                   || RequiredInt64String(
                       metadata,
                       "issuedIncarnationCount",
                       0)
                   != snapshot.IssuedEntityIncarnations.Count)
            || !string.Equals(
                RequiredString(metadata, "timelineDigest", 64),
                timelineDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(metadata, "snapshotDigest", 64),
                snapshotDigest,
                StringComparison.Ordinal)
            || RequiredInt64String(
                metadata,
                "incarnationCount",
                0) != snapshot.EntityIncarnations.Count
            || RequiredInt64String(
                metadata,
                "transactionCount",
                0) != capture.Receipts.Count
            || RequiredInt64String(
                metadata,
                "historyCount",
                0) != capture.History.Count)
        {
            throw Invalid(
                "The save binding digests or completeness counts do not match.");
        }
    }

    private static void ValidateParentBinding(
        WorldSaveDocument save,
        JsonElement metadata,
        NativeWorldSaveArtifactKind kind)
    {
        var parentDigest = OptionalString(
            metadata,
            "parentSaveDigest",
            64);
        if (kind == NativeWorldSaveArtifactKind.Settled)
        {
            if (save.ParentTimelineId is not null
                || save.ParentSaveRevision.HasValue
                || parentDigest is not null)
            {
                throw Invalid(
                    "A settled capture cannot claim fork ancestry.");
            }

            return;
        }

        if (save.ParentTimelineId is null
            || !save.ParentSaveRevision.HasValue
            || parentDigest is null
            || !CanonicalJsonDigest.IsSha256(parentDigest))
        {
            throw Invalid(
                "A fork must bind its parent timeline, revision, and save digest.");
        }
    }

    private static NativeWorldSaveArtifactKind ReadArtifactKind(
        JsonElement metadata)
    {
        return RequiredString(
            metadata,
            "artifactKind",
            32) switch
        {
            "settled" => NativeWorldSaveArtifactKind.Settled,
            "fork" => NativeWorldSaveArtifactKind.Fork,
            _ => throw Invalid("The save artifact kind is unsupported.")
        };
    }

    private static IReadOnlyList<WorldClockSnapshot> ReadClocks(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var result = new List<WorldClockSnapshot>(
            package.Clocks.Count);
        foreach (var clock in package.Clocks)
        {
            if (!NativeWorldConditionEvaluator.TryResolve(
                    snapshot.State,
                    clock.StatePath,
                    out var value)
                || value.ValueKind != JsonValueKind.String
                || !NativeWorldConditionEvaluator
                    .TryParseCanonicalInt64(
                        value.GetString(),
                        out var tick))
            {
                throw Invalid(
                    "The authoritative snapshot does not contain a valid declared clock.");
            }

            result.Add(
                new WorldClockSnapshot(
                    clock.ClockId,
                    snapshot.Coordinate.TimelineEpoch,
                    tick));
        }

        return new ReadOnlyCollection<WorldClockSnapshot>(
            [.. result.OrderBy(item => item.ClockId, StringComparer.Ordinal)]);
    }

    private static void ValidateClocks(
        ActivatedWorldPackage package,
        WorldSaveDocument save,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var expected = ReadClocks(package, snapshot);
        if (expected.Count != save.Clocks.Count)
        {
            throw Binding(
                "The save clock collection does not match the package.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = save.Clocks[index];
            if (!string.Equals(
                    left.ClockId,
                    right.ClockId,
                    StringComparison.Ordinal)
                || left.Epoch != right.Epoch
                || left.Tick != right.Tick)
            {
                throw Binding(
                    "A save clock does not match authoritative state.");
            }
        }
    }

    private static void ValidateScheduleClocks(
        ActivatedWorldPackage package,
        WorldAuthoritativeStoreCapture capture,
        CancellationToken cancellationToken)
    {
        var declared = package.Clocks
            .Select(clock => clock.ClockId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var schedule in capture.Schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declared.Contains(schedule.DueAt.ClockId))
            {
                throw Binding(
                    "A schedule uses a clock that is not declared by the activated package.");
            }
        }
    }

    private static void ValidatePackageSnapshot(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        if (!string.Equals(
                coordinate.WorldId,
                package.World.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                coordinate.CatalogDigest,
                package.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw Binding(
                "The authoritative snapshot is not bound to the activated package.");
        }
    }

    private static WorldEventHistoryRecord Rehome(
        WorldEventHistoryRecord source,
        WorldAuthoritativeCoordinate coordinate)
    {
        var instanceId = ForkInstanceId(
            source.InstanceId,
            coordinate);
        var parentId = source.ParentInstanceId is null
            ? null
            : ForkInstanceId(
                source.ParentInstanceId,
                coordinate);
        var definition = new WorldEventDefinitionKey(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            source.Definition.DefinitionId,
            source.Definition.DefinitionVersion);
        var occurredAt = source.OccurredAt is null
            ? null
            : new GameTimePoint(
                source.OccurredAt.ClockId,
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                source.OccurredAt.Tick);
        return new WorldEventHistoryRecord(
            instanceId,
            definition,
            source.TriggerId,
            source.ResolutionKey,
            source.PlanFingerprint,
            occurredAt,
            parentId);
    }

    private static string ForkInstanceId(
        string sourceInstanceId,
        WorldAuthoritativeCoordinate coordinate)
    {
        var value = WorldValidation.ComposeStableKey(
            "native-world-fork-event-v1",
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch.ToString(
                CultureInfo.InvariantCulture),
            sourceInstanceId);
        return "fork-"
               + WorldDataDigest.Compute(
                   Encoding.UTF8.GetBytes(value));
    }

    private static string ComputeIncarnationDigest(
        IReadOnlyDictionary<string, long> incarnations)
    {
        var builder = new StringBuilder();
        builder.Append("native-world-incarnations-v1");
        foreach (var pair in incarnations.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            builder.Append(
                WorldValidation.ComposeStableKey(
                    pair.Key,
                    pair.Value.ToString(
                        CultureInfo.InvariantCulture)));
        }

        return WorldDataDigest.Compute(
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string ComputeIssuedIncarnationDigest(
        IReadOnlyList<WorldIssuedEntityIncarnation> incarnations)
    {
        var builder = new StringBuilder();
        builder.Append("native-world-issued-incarnations-v1");
        foreach (var item in incarnations)
        {
            builder.Append(
                WorldValidation.ComposeStableKey(
                    item.EntityId,
                    item.Incarnation.ToString(
                        CultureInfo.InvariantCulture)));
        }

        return WorldDataDigest.Compute(
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string ComputeScheduleDigest(
        WorldAuthoritativeStoreCapture capture)
    {
        var builder = new StringBuilder();
        builder.Append("native-world-schedules-v1");
        foreach (var schedule in capture.Schedules)
        {
            builder.Append(
                WorldValidation.ComposeStableKey(
                    schedule.StableKey,
                    schedule.RecordDigest));
        }

        foreach (var operation in capture.ScheduleOperations)
        {
            builder.Append(
                WorldValidation.ComposeStableKey(
                    operation.Scope.StableKey,
                    operation.OperationId,
                    operation.ReceiptId));
        }

        return WorldDataDigest.Compute(
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string ComputeTimelineDigest(
        WorldAuthoritativeCoordinate coordinate,
        string? parentTimelineId,
        long? parentSaveRevision,
        string? parentSaveDigest)
    {
        return DigestComponents(
            "native-world-timeline-v1",
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch.ToString(
                CultureInfo.InvariantCulture),
            parentTimelineId ?? "-",
            parentSaveRevision?.ToString(
                CultureInfo.InvariantCulture) ?? "-",
            parentSaveDigest ?? "-");
    }

    private static string ComputeSnapshotDigest(
        WorldAuthoritativeCoordinate coordinate,
        string stateDigest,
        string incarnationDigest,
        string? issuedIncarnationDigest,
        string timelineDigest)
    {
        return issuedIncarnationDigest is null
            ? DigestComponents(
                "native-world-snapshot-v1",
                timelineDigest,
                coordinate.SaveRevision.ToString(
                    CultureInfo.InvariantCulture),
                coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                coordinate.CatalogDigest,
                stateDigest,
                incarnationDigest)
            : DigestComponents(
                "native-world-snapshot-v2",
                timelineDigest,
                coordinate.SaveRevision.ToString(
                    CultureInfo.InvariantCulture),
                coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                coordinate.CatalogDigest,
                stateDigest,
                incarnationDigest,
                issuedIncarnationDigest);
    }

    private static string DigestComponents(params string[] values)
    {
        return WorldDataDigest.Compute(
            Encoding.UTF8.GetBytes(
                WorldValidation.ComposeStableKey(values)));
    }

    private static JsonElement EmptyArray()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    private static JsonElement WriteJson(
        Action<Utf8JsonWriter> write,
        WorldPackageLimits limits,
        string capacityMessage)
    {
        try
        {
            using var output = new MemoryStream();
            using var bounded = new WorldBoundedArchiveWriteStream(
                output,
                limits.MaxFileBytes,
                WorldDataReasonCodes.ByteLimitExceeded,
                capacityMessage);
            using (var writer = new Utf8JsonWriter(bounded))
            {
                write(writer);
            }

            using var document = WorldDataJson.Parse(
                output.ToArray(),
                limits,
                "bridgeArtifact");
            return document.RootElement.Clone();
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw Capacity(capacityMessage, exception);
        }
    }

    private static byte[] WriteCanonicalJson(
        JsonElement value,
        long maximumBytes)
    {
        using var output = new MemoryStream();
        using var bounded = new WorldBoundedArchiveWriteStream(
            output,
            maximumBytes,
            WorldDataReasonCodes.ByteLimitExceeded,
            "Typed result exceeds its byte limit.");
        using (var writer = new Utf8JsonWriter(bounded))
        {
            WorldDataJson.WriteCanonical(writer, value);
        }

        return output.ToArray();
    }

    private static byte[] WriteScheduleRecord(
        WorldScheduleRecord record,
        WorldPackageLimits limits)
    {
        return WriteScheduleValue(
            writer => WorldScheduleStoreCodec.WriteRecord(
                writer,
                record),
            limits);
    }

    private static byte[] WriteScheduleOperation(
        WorldScheduleOperationReceipt operation,
        WorldPackageLimits limits)
    {
        return WriteScheduleValue(
            writer => WorldScheduleStoreCodec.WriteReceipt(
                writer,
                operation),
            limits);
    }

    private static byte[] WriteScheduleValue(
        Action<Utf8JsonWriter> write,
        WorldPackageLimits limits)
    {
        try
        {
            using var output = new MemoryStream();
            using var bounded = new WorldBoundedArchiveWriteStream(
                output,
                limits.MaxFileBytes,
                WorldDataReasonCodes.ByteLimitExceeded,
                "A schedule record exceeds its byte limit.");
            using (var writer = new Utf8JsonWriter(bounded))
            {
                write(writer);
            }

            return output.ToArray();
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw Capacity(
                "A schedule record exceeds its byte limit.",
                exception);
        }
    }

    private static WorldScheduleRecord ReadScheduleRecord(
        string encoded,
        WorldPackageLimits limits)
    {
        var bytes = ReadScheduleBytes(encoded, limits);
        using var document = WorldDataJson.Parse(
            bytes,
            limits,
            "scheduleRecord");
        return WorldScheduleStoreCodec.ReadRecord(
            document.RootElement);
    }

    private static WorldScheduleOperationReceipt
        ReadScheduleOperation(
            string encoded,
            WorldPackageLimits limits)
    {
        var bytes = ReadScheduleBytes(encoded, limits);
        using var document = WorldDataJson.Parse(
            bytes,
            limits,
            "scheduleOperation");
        return WorldScheduleStoreCodec.ReadReceipt(
            document.RootElement);
    }

    private static byte[] ReadScheduleBytes(
        string encoded,
        WorldPackageLimits limits)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw Invalid(
                "A schedule record is not valid base64.",
                exception);
        }

        if (bytes.LongLength > limits.MaxFileBytes)
        {
            throw Capacity(
                "A schedule record exceeds its byte limit.");
        }

        return bytes;
    }

    private static byte[] CanonicalBytes(
        JsonElement value,
        WorldPackageLimits limits)
    {
        try
        {
            using var output = new MemoryStream();
            using var bounded = new WorldBoundedArchiveWriteStream(
                output,
                limits.MaxFileBytes,
                WorldDataReasonCodes.ByteLimitExceeded,
                "Bridge record stream exceeds its byte limit.");
            using (var writer = new Utf8JsonWriter(bounded))
            {
                WorldDataJson.WriteCanonical(writer, value);
            }

            return output.ToArray();
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw Capacity(
                "Bridge record stream exceeds its byte limit.",
                exception);
        }
    }

    private static void WriteInt64String(
        Utf8JsonWriter writer,
        string propertyName,
        long value)
    {
        writer.WriteString(
            propertyName,
            value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteOptionalString(
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

    private static void RequireObject(
        JsonElement value,
        ISet<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A bridge object has an invalid shape.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw Invalid(
                    "A bridge object contains unknown or duplicate fields.");
            }
        }

        if (seen.Count != fields.Count)
        {
            throw Invalid(
                "A bridge object is missing required fields.");
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw Invalid("A bridge field is missing.");
        }

        return value;
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A bridge object field is invalid.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes)
    {
        try
        {
            return WorldDataJson.RequiredString(
                parent,
                propertyName,
                maximumUtf8Bytes);
        }
        catch (WorldDataContractException exception)
        {
            throw Invalid("A bridge string field is invalid.", exception);
        }
    }

    private static string RequiredStringValue(
        JsonElement value,
        int maximumUtf8Bytes)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("A bridge string value is invalid.");
        }

        try
        {
            return WorldValidation.Required(
                value.GetString(),
                "value",
                maximumUtf8Bytes);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "A bridge string value is invalid.",
                exception);
        }
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes)
    {
        var value = RequiredProperty(parent, propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "A bridge optional string field is invalid.");
        }

        try
        {
            return WorldValidation.Required(
                value.GetString(),
                propertyName,
                maximumUtf8Bytes);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "A bridge optional string field is invalid.",
                exception);
        }
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("A bridge Boolean field is invalid.")
        };
    }

    private static long RequiredInt64String(
        JsonElement parent,
        string propertyName,
        long minimum)
    {
        var raw = RequiredString(parent, propertyName, 32);
        return ParseInt64String(raw, minimum);
    }

    private static long RequiredInt64StringValue(
        JsonElement value,
        long minimum)
    {
        return ParseInt64String(
            RequiredStringValue(value, 32),
            minimum);
    }

    private static long ParseInt64String(
        string raw,
        long minimum)
    {
        if (!long.TryParse(
                raw,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || !string.Equals(
                raw,
                value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "A bridge integer field is not canonical.");
        }

        return value;
    }

    private static void EnsureOrdered(
        string? previous,
        string current)
    {
        if (previous is not null
            && string.CompareOrdinal(previous, current) >= 0)
        {
            throw Invalid(
                "Bridge records must be unique and ordinally ordered.");
        }
    }

    private static HashSet<string> Fields(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static NativeWorldSaveBridgeException Binding(string message)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.BindingMismatch,
            message);
    }

    private static NativeWorldSaveBridgeException Invalid(
        string message,
        Exception? innerException = null)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            message,
            innerException);
    }

    private static NativeWorldSaveBridgeException Capacity(
        string message,
        Exception? innerException = null)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            message,
            innerException);
    }

    private sealed class BridgeRecords(
        IReadOnlyDictionary<string, long> incarnations,
        IReadOnlyList<WorldIssuedEntityIncarnation>
                issuedIncarnations,
        NativeWorldIssuedIncarnationEncoding issuedEncoding,
        IReadOnlyList<WorldCommandReceipt> receipts,
        IReadOnlyList<WorldEventHistoryRecord> history,
        IReadOnlyList<WorldScheduleRecord> schedules,
        IReadOnlyList<WorldScheduleOperationReceipt>
                scheduleOperations)
    {
        public IReadOnlyDictionary<string, long> Incarnations { get; } = incarnations;

        public IReadOnlyList<WorldIssuedEntityIncarnation>
            IssuedIncarnations
        { get; } = issuedIncarnations;

        public bool HasIssuedIncarnationRecords =>
            IssuedEncoding
            != NativeWorldIssuedIncarnationEncoding.None;

        public NativeWorldIssuedIncarnationEncoding IssuedEncoding
        { get; } = issuedEncoding;

        public IReadOnlyList<WorldCommandReceipt> Receipts { get; } = receipts;

        public IReadOnlyList<WorldEventHistoryRecord> History { get; } = history;

        public IReadOnlyList<WorldScheduleRecord> Schedules { get; } = schedules;

        public IReadOnlyList<WorldScheduleOperationReceipt>
            ScheduleOperations
        { get; } = scheduleOperations;
    }
}
