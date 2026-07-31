using System.Collections.ObjectModel;

namespace GameAgent.World;

internal interface IWorldAuthoritativeStoreCaptureSource
{
    ValueTask<WorldAuthoritativeStoreCapture> CaptureSettledAsync(
        WorldTimelineAddress address,
        long timelineEpoch,
        int maximumTransactionRecords,
        int maximumHistoryRecords,
        int maximumScheduleRecords,
        int maximumScheduleOperations,
        CancellationToken cancellationToken);
}

internal interface IWorldAuthoritativeReceiptSource
{
    ValueTask<WorldCommandReceipt?> ReadReceiptAsync(
        WorldTimelineAddress address,
        long timelineEpoch,
        string receiptId,
        int maximumTransactionRecords,
        CancellationToken cancellationToken);
}

internal sealed class WorldAuthoritativeStoreCapture
{
    public WorldAuthoritativeStoreCapture(
        WorldAuthoritativeStateSnapshot snapshot,
        IEnumerable<WorldCommandReceipt> receipts,
        IEnumerable<WorldEventHistoryRecord> history,
        int maximumTransactionRecords,
        int maximumHistoryRecords,
        IEnumerable<WorldScheduleRecord>? schedules = null,
        IEnumerable<WorldScheduleOperationReceipt>?
            scheduleOperations = null,
        int maximumScheduleRecords = 4_096,
        int maximumScheduleOperations = 16_384)
    {
        Snapshot = snapshot
                   ?? throw new ArgumentNullException(nameof(snapshot));
        var receiptArray = WorldValidation.MaterializeBounded(
                receipts
                ?? throw new ArgumentNullException(nameof(receipts)),
                maximumTransactionRecords,
                () => Capacity(
                    "The transaction history exceeds the bridge limit."))
            .Select(
                item => item
                        ?? throw Invalid(
                            "Transaction history contains a null record."))
            .OrderBy(
                item => item.Request.ExpectedCoordinate.Scope.StableKey,
                StringComparer.Ordinal)
            .ThenBy(item => item.OperationId, StringComparer.Ordinal)
            .ToArray();
        var historyArray = WorldValidation.MaterializeBounded(
                history
                ?? throw new ArgumentNullException(nameof(history)),
                maximumHistoryRecords,
                () => Capacity(
                    "The event history exceeds the bridge limit."))
            .Select(
                item => item
                        ?? throw Invalid(
                            "Event history contains a null record."))
            .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var scheduleArray = WorldValidation.MaterializeBounded(
                schedules ?? Array.Empty<WorldScheduleRecord>(),
                maximumScheduleRecords,
                () => Capacity(
                    "The schedule collection exceeds the bridge limit."))
            .Select(
                item => item
                        ?? throw Invalid(
                            "The schedule collection contains a null record."))
            .OrderBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();
        var scheduleOperationArray =
            WorldValidation.MaterializeBounded(
                    scheduleOperations
                    ?? Array.Empty<WorldScheduleOperationReceipt>(),
                    maximumScheduleOperations,
                    () => Capacity(
                        "The schedule operation history exceeds the bridge limit."))
                .Select(
                    item => item
                            ?? throw Invalid(
                                "The schedule operation history contains a null record."))
                .OrderBy(
                    item => item.Scope.StableKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.OperationId,
                    StringComparer.Ordinal)
                .ToArray();

        ValidateReceipts(receiptArray);
        ValidateHistory(historyArray);
        ValidateAppliedHistory(receiptArray, historyArray);
        ValidateSchedules(scheduleArray, scheduleOperationArray);
        Receipts = new ReadOnlyCollection<WorldCommandReceipt>(
            receiptArray);
        History = new ReadOnlyCollection<WorldEventHistoryRecord>(
            historyArray);
        Schedules = new ReadOnlyCollection<WorldScheduleRecord>(
            scheduleArray);
        ScheduleOperations =
            new ReadOnlyCollection<WorldScheduleOperationReceipt>(
                scheduleOperationArray);
    }

    public WorldAuthoritativeStateSnapshot Snapshot { get; }

    public IReadOnlyList<WorldCommandReceipt> Receipts { get; }

    public IReadOnlyList<WorldEventHistoryRecord> History { get; }

    public IReadOnlyList<WorldScheduleRecord> Schedules { get; }

    public IReadOnlyList<WorldScheduleOperationReceipt>
        ScheduleOperations
    { get; }

    private void ValidateReceipts(
        IReadOnlyList<WorldCommandReceipt> receipts)
    {
        var coordinate = Snapshot.Coordinate;
        var operations = new HashSet<string>(StringComparer.Ordinal);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            var request = receipt.Request;
            if (!SameScope(
                    request.ExpectedCoordinate,
                    coordinate)
                || !operations.Add(request.ScopedOperationKey)
                || !commands.Add(request.ScopedCommandKey))
            {
                throw Invalid(
                    "Transaction history crosses scopes or duplicates an identity.");
            }

            var resulting = receipt.ResultingCoordinate;
            if (resulting is not null
                && (!SameScope(resulting, coordinate)
                    || !string.Equals(
                        resulting.CatalogDigest,
                        coordinate.CatalogDigest,
                        StringComparison.Ordinal)
                    || resulting.SaveRevision
                    > coordinate.SaveRevision
                    || resulting.StateVersion
                    > coordinate.StateVersion))
            {
                throw Invalid(
                    "A transaction receipt is outside the captured authoritative fence.");
            }
        }
    }

    private void ValidateHistory(
        IReadOnlyList<WorldEventHistoryRecord> history)
    {
        var coordinate = Snapshot.Coordinate;
        var instances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in history)
        {
            if (!instances.Add(record.InstanceId)
                || !string.Equals(
                    record.Definition.WorldId,
                    coordinate.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    record.Definition.TimelineId,
                    coordinate.TimelineId,
                    StringComparison.Ordinal)
                || record.Definition.TimelineEpoch
                != coordinate.TimelineEpoch
                || record.OccurredAt is not null
                && (!string.Equals(
                        record.OccurredAt.TimelineId,
                        coordinate.TimelineId,
                        StringComparison.Ordinal)
                    || record.OccurredAt.Epoch
                    != coordinate.TimelineEpoch))
            {
                throw Invalid(
                    "Event history crosses scopes or duplicates an instance.");
            }
        }
    }

    private static void ValidateAppliedHistory(
        IReadOnlyList<WorldCommandReceipt> receipts,
        IReadOnlyList<WorldEventHistoryRecord> history)
    {
        var byInstance = history.ToDictionary(
            item => item.InstanceId,
            StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            if (receipt.Status != WorldCommandReceiptStatus.Applied)
            {
                continue;
            }

            var occurrence = receipt.Request.EventOccurrence;
            if (occurrence is null
                || !byInstance.TryGetValue(
                    occurrence.InstanceId,
                    out var recorded)
                || !recorded.IsEquivalentTo(occurrence))
            {
                throw Invalid(
                    "Applied transaction history is missing its committed event.");
            }
        }
    }

    private void ValidateSchedules(
        IReadOnlyList<WorldScheduleRecord> schedules,
        IReadOnlyList<WorldScheduleOperationReceipt> operations)
    {
        var scope = Snapshot.Coordinate.Scope;
        var scheduleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in schedules)
        {
            if (!schedule.Scope.IsSameAs(scope)
                || !scheduleIds.Add(schedule.ScheduleId))
            {
                throw Invalid(
                    "Schedules cross scopes or duplicate an identity.");
            }
        }

        var operationsById =
            new Dictionary<string, WorldScheduleOperationReceipt>(
                StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (!operation.Scope.IsSameAs(scope)
                || !operationsById.TryAdd(
                    operation.OperationId,
                    operation))
            {
                throw Invalid(
                    "Schedule operations cross scopes or duplicate an identity.");
            }
        }

        foreach (var schedule in schedules)
        {
            if (schedule.Claim is not null
                && (!operationsById.TryGetValue(
                        schedule.Claim.OperationId,
                        out var operation)
                    || !operation.EstablishesClaim(schedule)))
            {
                throw Invalid(
                    "An active schedule claim lacks its establishing operation receipt.");
            }
        }
    }

    private static bool SameScope(
        WorldAuthoritativeCoordinate left,
        WorldAuthoritativeCoordinate right)
    {
        return string.Equals(
                   left.WorldId,
                   right.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.TimelineId,
                   right.TimelineId,
                   StringComparison.Ordinal)
               && left.TimelineEpoch == right.TimelineEpoch;
    }

    private static NativeWorldSaveBridgeException Invalid(string message)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.InvalidArtifact,
            message);
    }

    private static NativeWorldSaveBridgeException Capacity(string message)
    {
        return new NativeWorldSaveBridgeException(
            NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
            message);
    }
}
