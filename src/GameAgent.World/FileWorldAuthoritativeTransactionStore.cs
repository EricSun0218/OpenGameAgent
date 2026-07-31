using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class FileWorldAuthoritativeStoreReasonCodes
{
    public const string Corrupt = "world_store_corrupt";
    public const string ByteLimitExceeded = "world_store_byte_limit_exceeded";
    public const string CapacityExceeded = "world_store_capacity_exceeded";
    public const string LockTimeout = "world_store_lock_timeout";
    public const string AtomicReplaceFailed =
        "world_store_atomic_replace_failed";
}

public sealed class FileWorldAuthoritativeStoreException(
    string reasonCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ReasonCode { get; } = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
}

/// <summary>
/// Hard bounds for the local authoritative snapshot. The store never trims
/// authoritative records implicitly; a host must archive or migrate them.
/// </summary>
public sealed class FileWorldAuthoritativeTransactionStoreOptions
{
    public FileWorldAuthoritativeTransactionStoreOptions(
        int maxStates = 128,
        int maxOperations = 4_096,
        int maxHistoryRecords = 16_384,
        long maxFileBytes = 32L * 1024 * 1024,
        TimeSpan? lockTimeout = null,
        WorldScheduleStoreOptions? schedules = null,
        int maxIssuedEntityIncarnations = 65_536)
    {
        MaxStates = InRange(maxStates, 1, 4_096, nameof(maxStates));
        MaxOperations = InRange(
            maxOperations,
            1,
            100_000,
            nameof(maxOperations));
        MaxHistoryRecords = InRange(
            maxHistoryRecords,
            1,
            100_000,
            nameof(maxHistoryRecords));
        MaxIssuedEntityIncarnations = InRange(
            maxIssuedEntityIncarnations,
            1,
            WorldAuthoritativeStateSnapshot
                .MaximumIssuedIncarnationCount,
            nameof(maxIssuedEntityIncarnations));
        if (maxFileBytes is < 1 or > WorldPackageLimits.HardMaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        var effectiveTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        MaxFileBytes = maxFileBytes;
        LockTimeout = effectiveTimeout;
        Schedules = schedules ?? new WorldScheduleStoreOptions();
    }

    public int MaxStates { get; }

    public int MaxOperations { get; }

    public int MaxHistoryRecords { get; }

    public int MaxIssuedEntityIncarnations { get; }

    public long MaxFileBytes { get; }

    public TimeSpan LockTimeout { get; }

    public WorldScheduleStoreOptions Schedules { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

/// <summary>
/// Portable local-file baseline for one or more authoritative timelines.
/// Every mutation rewrites one bounded, checksummed snapshot and publishes it
/// with an atomic same-directory file replacement. Pending ownership is
/// durable before BeginAsync returns.
/// </summary>
public sealed class FileWorldAuthoritativeTransactionStore
    : IWorldAuthoritativeTransactionStore,
      IWorldEventHistory,
      IWorldScheduleStore,
      IWorldAuthoritativeStoreCaptureSource,
      IWorldAuthoritativeReceiptSource
{
    private const string Contract =
        "game-agent.world-authoritative-store.v1";

    private static readonly HashSet<string> RootFields =
        new(
            ["contract", "contentDigest", "payload"],
            StringComparer.Ordinal);

    private static readonly HashSet<string> PayloadFields =
        new(
            [
                "generation",
                "states",
                "pending",
                "receipts",
                "history",
                "schedules",
                "scheduleOperations"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> StateFields =
        new(
            [
                "coordinate",
                "stateDigest",
                "state",
                "incarnations",
                "issuedIncarnations",
                "packedIncarnations"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> CoordinateFields =
        new(
            [
                "worldId",
                "timelineId",
                "timelineEpoch",
                "saveRevision",
                "stateVersion",
                "catalogDigest"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> IncarnationFields =
        new(["entityId", "incarnation"], StringComparer.Ordinal);

    private static readonly HashSet<string> PackedIncarnationFields =
        new(
            ["encoding", "byteLength", "chunks"],
            StringComparer.Ordinal);

    private static readonly HashSet<string> PendingFields =
        new(["ownerToken", "request"], StringComparer.Ordinal);

    private static readonly HashSet<string> RequestFields =
        new(
            [
                "operationId",
                "commandId",
                "commandPayloadDigest",
                "requestFingerprint",
                "expectedCoordinate",
                "expectedIncarnations",
                "eventOccurrence"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> ReceiptFields =
        new(
            [
                "request",
                "status",
                "outcomeCode",
                "resultingCoordinate",
                "resultingStateDigest",
                "effect",
                "eventInstanceId",
                "receiptId"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> EffectFields =
        new(["applied", "outcomeCode", "typedResult"], StringComparer.Ordinal);

    private static readonly HashSet<string> HistoryFields =
        new(
            [
                "instanceId",
                "definition",
                "triggerId",
                "resolutionKey",
                "planFingerprint",
                "occurredAt",
                "parentInstanceId"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> DefinitionFields =
        new(
            [
                "worldId",
                "timelineId",
                "timelineEpoch",
                "definitionId",
                "definitionVersion"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> TimeFields =
        new(
            ["clockId", "timelineId", "epoch", "tick"],
            StringComparer.Ordinal);

    private readonly string _path;
    private readonly string _lockPath;
    private readonly FileWorldAuthoritativeTransactionStoreOptions _options;
    private readonly WorldPackageLimits _jsonLimits;

    public FileWorldAuthoritativeTransactionStore(
        string path,
        IEnumerable<WorldAuthoritativeStateSnapshot>? initialStates = null,
        FileWorldAuthoritativeTransactionStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A store path is required.",
                nameof(path));
        }

        _options =
            options ?? new FileWorldAuthoritativeTransactionStoreOptions();
        _path = System.IO.Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        _jsonLimits = new WorldPackageLimits(
            maxFileBytes: _options.MaxFileBytes,
            maxExpandedBytes: _options.MaxFileBytes,
            maxCompressedBytes: _options.MaxFileBytes,
            maxJsonDepth: 64,
            maxJsonNodes: 2_000_000,
            maxJsonStringUtf8Bytes: 4 * 1024 * 1024,
            maxJsonContainerItems: 1_000_000);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "The store path must have a parent directory.",
                nameof(path));
        }

        Directory.CreateDirectory(directory);
        using var lease = AcquireLock(CancellationToken.None);
        RemoveStaleNextImage();
        if (File.Exists(_path))
        {
            _ = Load();
            return;
        }

        if (initialStates is null)
        {
            throw new FileNotFoundException(
                "The authoritative store does not exist and no initial state was supplied.",
                _path);
        }

        var boundedStates = WorldValidation.MaterializeBounded(
            initialStates,
            _options.MaxStates,
            nameof(initialStates));
        var model = new StoreModel();
        foreach (var state in boundedStates)
        {
            if (state is null)
            {
                throw new ArgumentException(
                    "Initial states cannot contain null entries.",
                    nameof(initialStates));
            }

            if (!model.States.TryAdd(
                    state.Coordinate.Address.StableKey,
                    state))
            {
                throw new ArgumentException(
                    "Initial states must be unique by world and timeline.",
                    nameof(initialStates));
            }
        }

        if (model.States.Count == 0)
        {
            throw new ArgumentException(
                "At least one initial state is required.",
                nameof(initialStates));
        }

        EnsureCapacity(model);
        Persist(model);
    }

    public string Path => _path;

    internal bool SchedulesUseOnlyDeclaredClocks(
        WorldTimelineAddress address,
        long timelineEpoch,
        IEnumerable<string> declaredClockIds,
        CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        if (declaredClockIds is null)
        {
            throw new ArgumentNullException(nameof(declaredClockIds));
        }

        var declared = WorldValidation.MaterializeBounded(
                declaredClockIds,
                WorldValidation.MaximumCatalogDefinitions,
                nameof(declaredClockIds))
            .ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        foreach (var schedule in model.Schedules.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(
                    schedule.Scope.WorldId,
                    address.WorldId,
                    StringComparison.Ordinal)
                && string.Equals(
                    schedule.Scope.TimelineId,
                    address.TimelineId,
                    StringComparison.Ordinal)
                && schedule.Scope.TimelineEpoch == timelineEpoch
                && !declared.Contains(schedule.DueAt.ClockId))
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
        WorldTimelineAddress address,
        CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        model.States.TryGetValue(address.StableKey, out var snapshot);
        return new ValueTask<WorldAuthoritativeStateSnapshot?>(snapshot);
    }

    public ValueTask<WorldScheduleMutationResult> ExecuteAsync(
        WorldScheduleCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        cancellationToken.ThrowIfCancellationRequested();
        var result = WorldScheduleStoreLogic.Execute(
            model.Schedules,
            model.ScheduleOperations,
            model.States,
            command,
            _options.Schedules);
        if (!result.IsReplay && result.Receipt is not null)
        {
            EnsureCapacity(model);
            cancellationToken.ThrowIfCancellationRequested();
            Persist(model);
        }

        return new ValueTask<WorldScheduleMutationResult>(result);
    }

    public ValueTask<WorldScheduleRecord?> FindAsync(
        WorldTransactionScope scope,
        string scheduleId,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalized = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        model.Schedules.TryGetValue(
            WorldValidation.ComposeStableKey(
                scope.StableKey,
                normalized),
            out var schedule);
        return new ValueTask<WorldScheduleRecord?>(schedule);
    }

    public ValueTask<WorldScheduleDuePage> QueryDueAsync(
        WorldScheduleDueQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        return new ValueTask<WorldScheduleDuePage>(
            WorldScheduleStoreLogic.QueryDue(
                model.Schedules.Values,
                query,
                cancellationToken));
    }

    ValueTask<WorldAuthoritativeStoreCapture>
        IWorldAuthoritativeStoreCaptureSource.CaptureSettledAsync(
            WorldTimelineAddress address,
            long timelineEpoch,
            int maximumTransactionRecords,
            int maximumHistoryRecords,
            int maximumScheduleRecords,
            int maximumScheduleOperations,
            CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (!model.States.TryGetValue(
                address.StableKey,
                out var snapshot)
            || snapshot.Coordinate.TimelineEpoch != timelineEpoch)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.BindingMismatch,
                "The requested authoritative timeline does not exist.");
        }

        if (model.Pending.Count + model.Receipts.Count
            > maximumTransactionRecords
            || model.History.Count > maximumHistoryRecords
            || model.Schedules.Count > maximumScheduleRecords
            || model.ScheduleOperations.Count
            > maximumScheduleOperations)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
                "The authoritative store exceeds the capture scan limit.");
        }

        var coordinate = snapshot.Coordinate;
        foreach (var pending in model.Pending.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SameScope(
                    pending.Request.ExpectedCoordinate,
                    coordinate))
            {
                throw new NativeWorldSaveBridgeException(
                    NativeWorldSaveBridgeReasonCodes.PendingTransactions,
                    "Settled capture rejects pending authoritative work.");
            }
        }

        var receipts = new List<WorldCommandReceipt>();
        foreach (var receipt in model.Receipts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SameScope(
                    receipt.Request.ExpectedCoordinate,
                    coordinate))
            {
                receipts.Add(receipt);
            }
        }

        var history = new List<WorldEventHistoryRecord>();
        foreach (var record in model.History.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(
                    record.Definition.WorldId,
                    coordinate.WorldId,
                    StringComparison.Ordinal)
                && string.Equals(
                    record.Definition.TimelineId,
                    coordinate.TimelineId,
                    StringComparison.Ordinal)
                && record.Definition.TimelineEpoch
                == coordinate.TimelineEpoch)
            {
                history.Add(record);
            }
        }

        var schedules = new List<WorldScheduleRecord>();
        foreach (var schedule in model.Schedules.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schedule.Scope.IsSameAs(coordinate.Scope))
            {
                schedules.Add(schedule);
            }
        }

        var scheduleOperations =
            new List<WorldScheduleOperationReceipt>();
        foreach (var operation in model.ScheduleOperations.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Scope.IsSameAs(coordinate.Scope))
            {
                scheduleOperations.Add(operation);
            }
        }

        return new ValueTask<WorldAuthoritativeStoreCapture>(
            new WorldAuthoritativeStoreCapture(
                snapshot,
                receipts,
                history,
                maximumTransactionRecords,
                maximumHistoryRecords,
                schedules,
                scheduleOperations,
                maximumScheduleRecords,
                maximumScheduleOperations));
    }

    internal void ReplaceWithSettledCapture(
        WorldAuthoritativeStoreCapture capture,
        CancellationToken cancellationToken)
    {
        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var existing = Load();
        if (existing.States.Count != 1
            || existing.Pending.Count != 0
            || existing.Receipts.Count != 0
            || existing.History.Count != 0
            || existing.Schedules.Count != 0
            || existing.ScheduleOperations.Count != 0)
        {
            throw new InvalidOperationException(
                "Only a fresh authoritative store can be seeded.");
        }

        var model = new StoreModel
        {
            Generation = existing.Generation
        };
        model.States.Add(
            capture.Snapshot.Coordinate.Address.StableKey,
            capture.Snapshot);
        foreach (var receipt in capture.Receipts)
        {
            model.Receipts.Add(
                receipt.Request.ScopedOperationKey,
                receipt);
        }

        foreach (var record in capture.History)
        {
            model.History.Add(record.InstanceId, record);
        }

        foreach (var schedule in capture.Schedules)
        {
            model.Schedules.Add(schedule.StableKey, schedule);
        }

        foreach (var operation in capture.ScheduleOperations)
        {
            model.ScheduleOperations.Add(
                operation.ScopedOperationKey,
                operation);
        }

        EnsureCapacity(model);
        ValidateLoadedModel(model);
        cancellationToken.ThrowIfCancellationRequested();
        Persist(model);
    }

    public ValueTask<WorldTransactionBeginResult> BeginAsync(
        WorldTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (model.Receipts.TryGetValue(
                request.ScopedOperationKey,
                out var existingReceipt))
        {
            return new ValueTask<WorldTransactionBeginResult>(
                SameRequest(existingReceipt.Request, request)
                    ? WorldTransactionBeginResult.Terminal(existingReceipt)
                    : Conflict());
        }

        if (model.Pending.TryGetValue(
                request.ScopedOperationKey,
                out var existingPending))
        {
            return new ValueTask<WorldTransactionBeginResult>(
                SameRequest(existingPending.Request, request)
                    ? WorldTransactionBeginResult.NonTerminal(
                        WorldTransactionBeginStatus.ReconciliationRequired,
                        WorldTransactionReasonCodes.ReconciliationRequired)
                    : Conflict());
        }

        if (model.Commands.TryGetValue(
                request.ScopedCommandKey,
                out var command))
        {
            return new ValueTask<WorldTransactionBeginResult>(
                string.Equals(
                    command.OperationId,
                    request.OperationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    command.RequestFingerprint,
                    request.RequestFingerprint,
                    StringComparison.Ordinal)
                    ? WorldTransactionBeginResult.NonTerminal(
                        WorldTransactionBeginStatus.ReconciliationRequired,
                        WorldTransactionReasonCodes.ReconciliationRequired)
                    : Conflict());
        }

        EnsureNewOperationCapacity(model);
        var addressKey = request.ExpectedCoordinate.Address.StableKey;
        if (!model.States.TryGetValue(addressKey, out var state))
        {
            var rejected = CreateReceipt(
                request,
                WorldCommandReceiptStatus.Rejected,
                WorldTransactionReasonCodes.StateNotFound,
                null,
                null,
                null);
            model.Receipts.Add(request.ScopedOperationKey, rejected);
            model.Commands.Add(
                request.ScopedCommandKey,
                new CommandIdentity(
                    request.OperationId,
                    request.RequestFingerprint));
            Persist(model);
            return new ValueTask<WorldTransactionBeginResult>(
                WorldTransactionBeginResult.Terminal(rejected));
        }

        var mismatch = CoordinateMismatch(
            request.ExpectedCoordinate,
            state.Coordinate);
        if (mismatch is not null)
        {
            return new ValueTask<WorldTransactionBeginResult>(
                PersistAdmissionRejection(model, request, state, mismatch));
        }

        if (!IncarnationsMatch(
                request.ExpectedIncarnations,
                state.EntityIncarnations))
        {
            return new ValueTask<WorldTransactionBeginResult>(
                PersistAdmissionRejection(
                    model,
                    request,
                    state,
                    WorldTransactionReasonCodes.StaleIncarnation));
        }

        if (request.EventOccurrence is not null
            && model.History.TryGetValue(
                request.EventOccurrence.InstanceId,
                out var recorded))
        {
            var reason = recorded.IsEquivalentTo(request.EventOccurrence)
                ? WorldTransactionReasonCodes.EventAlreadyCommitted
                : WorldTransactionReasonCodes.InvalidHistory;
            return new ValueTask<WorldTransactionBeginResult>(
                PersistAdmissionRejection(model, request, state, reason));
        }

        if (model.Pending.Values.Any(
                item => string.Equals(
                    item.Request.ExpectedCoordinate.Address.StableKey,
                    addressKey,
                    StringComparison.Ordinal)))
        {
            return new ValueTask<WorldTransactionBeginResult>(
                WorldTransactionBeginResult.NonTerminal(
                    WorldTransactionBeginStatus.Busy,
                    WorldTransactionReasonCodes.Busy));
        }

        if (request.EventOccurrence is not null
            && model.History.Count >= _options.MaxHistoryRecords)
        {
            throw Capacity(
                "The history capacity cannot admit this transaction.");
        }

        var token = Guid.NewGuid().ToString(
            "N",
            CultureInfo.InvariantCulture);
        var pending = new PendingOperation(request, token);
        model.Pending.Add(request.ScopedOperationKey, pending);
        model.Commands.Add(
            request.ScopedCommandKey,
            new CommandIdentity(
                request.OperationId,
                request.RequestFingerprint));

        // Ownership is persisted before the capability is returned.
        Persist(model);
        return new ValueTask<WorldTransactionBeginResult>(
            WorldTransactionBeginResult.Acquired(
                new FileTransaction(this, request, state, token)));
    }

    public ValueTask<WorldTransactionInspectionResult> InspectAsync(
        WorldTransactionScope scope,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        var operationKey = WorldValidation.ComposeStableKey(
            scope.StableKey,
            normalizedOperation);
        if (model.Receipts.TryGetValue(operationKey, out var receipt))
        {
            return new ValueTask<WorldTransactionInspectionResult>(
                WorldTransactionInspectionResult.Terminal(receipt));
        }

        if (model.Pending.TryGetValue(operationKey, out var pending))
        {
            return new ValueTask<WorldTransactionInspectionResult>(
                WorldTransactionInspectionResult.Pending(
                    pending.Request));
        }

        return new ValueTask<WorldTransactionInspectionResult>(
            WorldTransactionInspectionResult.NotFound());
    }

    ValueTask<WorldCommandReceipt?>
        IWorldAuthoritativeReceiptSource.ReadReceiptAsync(
            WorldTimelineAddress address,
            long timelineEpoch,
            string receiptId,
            int maximumTransactionRecords,
            CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        var normalizedReceiptId = WorldValidation.Required(
            receiptId,
            nameof(receiptId),
            128);
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }
        if (maximumTransactionRecords < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionRecords));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (model.Receipts.Count > maximumTransactionRecords)
        {
            return new ValueTask<WorldCommandReceipt?>(result: null);
        }

        WorldCommandReceipt? match = null;
        foreach (var receipt in model.Receipts.Values)
        {
            if (!string.Equals(
                    receipt.ReceiptId,
                    normalizedReceiptId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.ExpectedCoordinate.Address.StableKey,
                    address.StableKey,
                    StringComparison.Ordinal)
                || receipt.ExpectedCoordinate.TimelineEpoch
                != timelineEpoch)
            {
                continue;
            }

            if (match is not null)
            {
                return new ValueTask<WorldCommandReceipt?>(result: null);
            }

            match = receipt;
        }

        return new ValueTask<WorldCommandReceipt?>(match);
    }

    public ValueTask<WorldTransactionReconciliationResult> ReconcileAsync(
        WorldTransactionScope scope,
        string operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        var normalizedFingerprint = WorldValidation.Required(
            requestFingerprint,
            nameof(requestFingerprint),
            128);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        var operationKey = WorldValidation.ComposeStableKey(
            scope.StableKey,
            normalizedOperation);
        if (model.Receipts.TryGetValue(
                operationKey,
                out var receipt))
        {
            return new ValueTask<WorldTransactionReconciliationResult>(
                string.Equals(
                    receipt.RequestFingerprint,
                    normalizedFingerprint,
                    StringComparison.Ordinal)
                    ? WorldTransactionReconciliationResult.Terminal(receipt)
                    : ReconciliationConflict());
        }

        if (model.Pending.TryGetValue(
                operationKey,
                out var pending))
        {
            return new ValueTask<WorldTransactionReconciliationResult>(
                string.Equals(
                    pending.Request.RequestFingerprint,
                    normalizedFingerprint,
                    StringComparison.Ordinal)
                    ? WorldTransactionReconciliationResult.NonTerminal(
                        WorldTransactionReconciliationStatus.Pending,
                        WorldTransactionReasonCodes.ReconciliationRequired)
                    : ReconciliationConflict());
        }

        return new ValueTask<WorldTransactionReconciliationResult>(
            WorldTransactionReconciliationResult.NonTerminal(
                WorldTransactionReconciliationStatus.NotFound,
                WorldTransactionReasonCodes.StateNotFound));
    }

    public ValueTask<WorldTransactionReconciliationResult>
        CancelPendingAsync(
            WorldTransactionScope scope,
            string operationId,
            string requestFingerprint,
            string outcomeCode,
            CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        var normalizedFingerprint = WorldValidation.Required(
            requestFingerprint,
            nameof(requestFingerprint),
            128);
        var normalizedOutcome = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        var operationKey = WorldValidation.ComposeStableKey(
            scope.StableKey,
            normalizedOperation);
        if (model.Receipts.TryGetValue(
                operationKey,
                out var receipt))
        {
            return new ValueTask<WorldTransactionReconciliationResult>(
                string.Equals(
                    receipt.RequestFingerprint,
                    normalizedFingerprint,
                    StringComparison.Ordinal)
                    ? WorldTransactionReconciliationResult.Terminal(receipt)
                    : ReconciliationConflict());
        }

        if (!model.Pending.TryGetValue(
                operationKey,
                out var pending))
        {
            return new ValueTask<WorldTransactionReconciliationResult>(
                WorldTransactionReconciliationResult.NonTerminal(
                    WorldTransactionReconciliationStatus.NotFound,
                    WorldTransactionReasonCodes.StateNotFound));
        }

        if (!string.Equals(
                pending.Request.RequestFingerprint,
                normalizedFingerprint,
                StringComparison.Ordinal))
        {
            return new ValueTask<WorldTransactionReconciliationResult>(
                ReconciliationConflict());
        }

        model.States.TryGetValue(
            pending.Request.ExpectedCoordinate.Address.StableKey,
            out var state);
        var cancelled = CreateReceipt(
            pending.Request,
            WorldCommandReceiptStatus.Cancelled,
            normalizedOutcome,
            state?.Coordinate,
            state?.StateDigest,
            null);
        model.Pending.Remove(operationKey);
        model.Receipts.Add(operationKey, cancelled);
        Persist(model);
        return new ValueTask<WorldTransactionReconciliationResult>(
            WorldTransactionReconciliationResult.Terminal(cancelled));
    }

    public ValueTask<WorldEventDefinitionHistory> ReadDefinitionAsync(
        WorldEventDefinitionKey definition,
        CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        var matching = model.History.Values
            .Where(
                item => string.Equals(
                    item.Definition.StableKey,
                    definition.StableKey,
                    StringComparison.Ordinal))
            .ToArray();
        if (matching.Length == 0)
        {
            return new ValueTask<WorldEventDefinitionHistory>(
                WorldEventDefinitionHistory.Empty);
        }

        return new ValueTask<WorldEventDefinitionHistory>(
            new WorldEventDefinitionHistory(
                matching.LongLength,
                LatestTime(matching)));
    }

    public ValueTask<WorldEventHistoryRecord?> FindInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var normalized = WorldValidation.Required(
            instanceId,
            nameof(instanceId));
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        model.History.TryGetValue(normalized, out var record);
        return new ValueTask<WorldEventHistoryRecord?>(record);
    }

    public ValueTask<WorldEventHistoryAppendResult> TryAppendAsync(
        WorldEventHistoryRecord record,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (model.History.TryGetValue(record.InstanceId, out var existing))
        {
            if (!existing.IsEquivalentTo(record))
            {
                throw InvalidHistory(
                    "An instance identifier maps to conflicting history.");
            }

            return new ValueTask<WorldEventHistoryAppendResult>(
                WorldEventHistoryAppendResult.AlreadyExists);
        }

        if (model.Pending.Values.Any(
                item => string.Equals(
                    item.Request.EventOccurrence?.InstanceId,
                    record.InstanceId,
                    StringComparison.Ordinal)))
        {
            throw InvalidHistory(
                "History cannot bypass a pending authoritative transaction.");
        }

        if (model.History.Count >= _options.MaxHistoryRecords)
        {
            throw Capacity("The history capacity has been reached.");
        }

        ValidateHistoryAppend(model.History.Values, record);
        model.History.Add(record.InstanceId, record);
        Persist(model);
        return new ValueTask<WorldEventHistoryAppendResult>(
            WorldEventHistoryAppendResult.Appended);
    }

    private WorldTransactionCommitResult CommitEvent(
        WorldTransactionRequest request,
        string token,
        FileStateDraft draft,
        WorldEffectReceipt effect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (!Owns(model, request, token, out var pending, out var state))
        {
            return WorldTransactionCommitResult.LeaseLost();
        }

        var occurrence = pending!.Request.EventOccurrence
                         ?? throw new InvalidOperationException(
                             "An event commit requires an occurrence.");
        if (model.History.TryGetValue(
                occurrence.InstanceId,
                out var existing))
        {
            if (!existing.IsEquivalentTo(occurrence))
            {
                throw InvalidHistory(
                    "An instance identifier maps to conflicting history.");
            }

            return WorldTransactionCommitResult.LeaseLost();
        }

        if (model.History.Count >= _options.MaxHistoryRecords)
        {
            throw Capacity("The history capacity has been reached.");
        }

        ValidateHistoryAppend(model.History.Values, occurrence);
        var nextCoordinate = state!.Coordinate.Advance(draft.HasChanges);
        var nextState = draft.Build(nextCoordinate);
        var receipt = CreateReceipt(
            pending.Request,
            WorldCommandReceiptStatus.Applied,
            effect.OutcomeCode,
            nextCoordinate,
            nextState.StateDigest,
            effect);
        model.States[state.Coordinate.Address.StableKey] = nextState;
        model.History.Add(occurrence.InstanceId, occurrence);
        model.Pending.Remove(request.ScopedOperationKey);
        model.Receipts.Add(request.ScopedOperationKey, receipt);

        // State, occurrence history, and terminal receipt are one file image.
        Persist(model);
        return WorldTransactionCommitResult.Committed(receipt);
    }

    private WorldTransactionCommitResult CompleteWithoutMutation(
        WorldTransactionRequest request,
        string token,
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldEffectReceipt? effect,
        CancellationToken cancellationToken)
    {
        if (status == WorldCommandReceiptStatus.Applied)
        {
            throw new ArgumentException(
                "A non-mutating completion cannot be applied.",
                nameof(status));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireLock(cancellationToken);
        var model = Load();
        if (!Owns(model, request, token, out var pending, out var state))
        {
            return WorldTransactionCommitResult.LeaseLost();
        }

        var receipt = CreateReceipt(
            pending!.Request,
            status,
            outcomeCode,
            state!.Coordinate,
            state.StateDigest,
            effect);
        model.Pending.Remove(request.ScopedOperationKey);
        model.Receipts.Add(request.ScopedOperationKey, receipt);
        Persist(model);
        return WorldTransactionCommitResult.Committed(receipt);
    }

    private bool Owns(
        StoreModel model,
        WorldTransactionRequest request,
        string token,
        out PendingOperation? pending,
        out WorldAuthoritativeStateSnapshot? state)
    {
        state = null;
        if (!model.Pending.TryGetValue(
                request.ScopedOperationKey,
                out pending)
            || !string.Equals(
                pending.OwnerToken,
                token,
                StringComparison.Ordinal)
            || !SameRequest(pending.Request, request)
            || !model.States.TryGetValue(
                request.ExpectedCoordinate.Address.StableKey,
                out state))
        {
            return false;
        }

        return state.Coordinate.IsExactMatch(request.ExpectedCoordinate);
    }

    private WorldTransactionBeginResult PersistAdmissionRejection(
        StoreModel model,
        WorldTransactionRequest request,
        WorldAuthoritativeStateSnapshot state,
        string outcomeCode)
    {
        var exposesCurrentState =
            request.ExpectedCoordinate.IsSameTimelineAs(
                state.Coordinate);
        var receipt = CreateReceipt(
            request,
            WorldCommandReceiptStatus.Rejected,
            outcomeCode,
            exposesCurrentState ? state.Coordinate : null,
            exposesCurrentState ? state.StateDigest : null,
            null);
        model.Receipts.Add(request.ScopedOperationKey, receipt);
        model.Commands.Add(
            request.ScopedCommandKey,
            new CommandIdentity(
                request.OperationId,
                request.RequestFingerprint));
        Persist(model);
        return WorldTransactionBeginResult.Terminal(receipt);
    }

    private StoreModel Load()
    {
        byte[] bytes;
        try
        {
            using var source = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (source.Length <= 0 || source.Length > _options.MaxFileBytes)
            {
                throw source.Length > _options.MaxFileBytes
                    ? Error(
                        FileWorldAuthoritativeStoreReasonCodes
                            .ByteLimitExceeded,
                        "The authoritative store exceeds its byte limit.")
                    : Corrupt("The authoritative store is empty.");
            }

            bytes = new byte[checked((int)source.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = source.Read(
                    bytes,
                    offset,
                    bytes.Length - offset);
                if (read == 0)
                {
                    throw Corrupt(
                        "The authoritative store was truncated while reading.");
                }

                offset += read;
            }
        }
        catch (FileWorldAuthoritativeStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw Corrupt(
                "The authoritative store could not be read.",
                exception);
        }

        try
        {
            using var document = WorldDataJson.Parse(
                bytes,
                _jsonLimits,
                nameof(bytes));
            var root = document.RootElement;
            WorldDataJson.RequireOnlyProperties(root, RootFields);
            if (!string.Equals(
                    WorldDataJson.RequiredString(root, "contract", 96),
                    Contract,
                    StringComparison.Ordinal))
            {
                throw Corrupt(
                    "The authoritative store contract is unsupported.");
            }

            var persistedDigest = WorldDataJson.RequiredString(
                root,
                "contentDigest",
                64);
            if (!CanonicalJsonDigest.IsSha256(persistedDigest)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !string.Equals(
                    WorldLargeCanonicalJsonDigest.Compute(
                        payload,
                        _options.MaxFileBytes,
                        "payload"),
                    persistedDigest,
                    StringComparison.Ordinal))
            {
                throw Corrupt(
                    "The authoritative store content digest is invalid.");
            }

            var model = ParsePayload(payload);
            EnsureCapacity(model);
            ValidateLoadedModel(model);
            return model;
        }
        catch (FileWorldAuthoritativeStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is WorldDataContractException
            or WorldScheduleStoreException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or KeyNotFoundException)
        {
            throw Corrupt(
                "The authoritative store is malformed or inconsistent.",
                exception);
        }
    }

    private void Persist(StoreModel model)
    {
        EnsureCapacity(model);
        if (model.Generation == long.MaxValue)
        {
            throw Capacity("The store generation cannot advance further.");
        }

        model.Generation++;
        byte[] bytes;
        try
        {
            var payloadBytes = WritePayload(
                model,
                _options.MaxFileBytes);
            using var payloadDocument =
                JsonDocument.Parse(payloadBytes);
            var digest = WorldLargeCanonicalJsonDigest.Compute(
                payloadDocument.RootElement,
                _options.MaxFileBytes,
                "payload");
            using var output = new MemoryStream();
            using var boundedOutput =
                new WorldBoundedArchiveWriteStream(
                    output,
                    _options.MaxFileBytes,
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "The authoritative store exceeds its byte limit.");
            using (var writer = new Utf8JsonWriter(boundedOutput))
            {
                writer.WriteStartObject();
                writer.WriteString("contract", Contract);
                writer.WriteString("contentDigest", digest);
                writer.WritePropertyName("payload");
                payloadDocument.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            bytes = output.ToArray();
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw Error(
                FileWorldAuthoritativeStoreReasonCodes.ByteLimitExceeded,
                "The authoritative store exceeds its byte limit.",
                exception);
        }

        var nextPath = _path + ".next";
        RemoveStaleNextImage();
        try
        {
            using (var destination = new FileStream(
                       nextPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81_920,
                       FileOptions.WriteThrough))
            {
                destination.Write(bytes, 0, bytes.Length);
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(nextPath, _path, null);
            }
            else
            {
                File.Move(nextPath, _path);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            throw Error(
                FileWorldAuthoritativeStoreReasonCodes.AtomicReplaceFailed,
                "The authoritative snapshot could not be atomically published.",
                exception);
        }
        finally
        {
            try
            {
                if (File.Exists(nextPath))
                {
                    File.Delete(nextPath);
                }
            }
            catch
            {
                // Preserve the authoritative publication result. A startup
                // maintenance pass may remove an uncommitted .next file.
            }
        }
    }

    private void RemoveStaleNextImage()
    {
        var nextPath = _path + ".next";
        try
        {
            if (File.Exists(nextPath))
            {
                File.Delete(nextPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw Error(
                FileWorldAuthoritativeStoreReasonCodes.AtomicReplaceFailed,
                "A stale next authoritative image could not be removed.",
                exception);
        }
    }

    private LockLease AcquireLock(CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                stream.Lock(0, 1);
                return new LockLease(stream);
            }
            catch (IOException exception)
            {
                stream?.Dispose();
                if (started.Elapsed >= _options.LockTimeout)
                {
                    throw Error(
                        FileWorldAuthoritativeStoreReasonCodes.LockTimeout,
                        "Timed out acquiring authoritative store ownership.",
                        exception);
                }

                Thread.Sleep(10);
            }
        }
    }

    private StoreModel ParsePayload(JsonElement payload)
    {
        WorldDataJson.RequireOnlyProperties(payload, PayloadFields);
        var model = new StoreModel
        {
            Generation = RequiredInt64(payload, "generation", minimum: 0)
        };
        foreach (var item in RequiredArray(payload, "states"))
        {
            WorldDataJson.RequireOnlyProperties(item, StateFields);
            var coordinate = ParseCoordinate(
                RequiredObject(item, "coordinate"));
            var state = RequiredObject(item, "state").Clone();
            IReadOnlyDictionary<string, long> incarnations;
            IEnumerable<WorldIssuedEntityIncarnation>?
                issuedIncarnations = null;
            if (item.TryGetProperty(
                    "packedIncarnations",
                    out var packedValue))
            {
                if (item.TryGetProperty("incarnations", out _)
                    || item.TryGetProperty(
                        "issuedIncarnations",
                        out _))
                {
                    throw Corrupt(
                        "Packed and legacy incarnation encodings cannot be mixed.");
                }

                var packed = ParsePackedIncarnations(packedValue);
                incarnations = packed.Current;
                issuedIncarnations = packed.Issued;
            }
            else
            {
                incarnations = ParseIncarnations(
                    RequiredArray(item, "incarnations"));
                if (item.TryGetProperty(
                        "issuedIncarnations",
                        out var issuedValue))
                {
                    if (issuedValue.ValueKind != JsonValueKind.Array)
                    {
                        throw Corrupt(
                            "The issued-incarnation ledger is invalid.");
                    }

                    issuedIncarnations = ParseIssuedIncarnations(
                        issuedValue.EnumerateArray(),
                        _options.MaxIssuedEntityIncarnations);
                }
            }

            var snapshot = new WorldAuthoritativeStateSnapshot(
                coordinate,
                state,
                incarnations,
                issuedIncarnations);
            var stateDigest = RequiredString(item, "stateDigest", 64);
            if (!string.Equals(
                    snapshot.StateDigest,
                    stateDigest,
                    StringComparison.Ordinal)
                || !model.States.TryAdd(
                    coordinate.Address.StableKey,
                    snapshot))
            {
                throw Corrupt(
                    "A state digest or timeline identity is invalid.");
            }
        }

        foreach (var item in RequiredArray(payload, "pending"))
        {
            WorldDataJson.RequireOnlyProperties(item, PendingFields);
            var pending = new PendingOperation(
                ParseRequest(RequiredObject(item, "request")),
                RequiredString(item, "ownerToken", 128));
            if (!model.Pending.TryAdd(
                    pending.Request.ScopedOperationKey,
                    pending))
            {
                throw Corrupt(
                    "Pending operation identifiers must be unique.");
            }
        }

        foreach (var item in RequiredArray(payload, "receipts"))
        {
            var receipt = ParseReceipt(item);
            if (!model.Receipts.TryAdd(
                    receipt.Request.ScopedOperationKey,
                    receipt))
            {
                throw Corrupt(
                    "Receipt operation identifiers must be unique.");
            }
        }

        foreach (var item in RequiredArray(payload, "history"))
        {
            var record = ParseHistory(item);
            if (!model.History.TryAdd(record.InstanceId, record))
            {
                throw Corrupt(
                    "History instance identifiers must be unique.");
            }
        }

        if (payload.TryGetProperty(
                "schedules",
                out var schedulesValue))
        {
            if (schedulesValue.ValueKind != JsonValueKind.Array)
            {
                throw Corrupt(
                    "The schedule collection is invalid.");
            }

            foreach (var item in schedulesValue.EnumerateArray())
            {
                if (model.Schedules.Count
                    >= _options.Schedules.MaxSchedules)
                {
                    throw Capacity(
                        "The schedule capacity has been exceeded.");
                }

                var schedule =
                    WorldScheduleStoreCodec.ReadRecord(item);
                if (!model.Schedules.TryAdd(
                        schedule.StableKey,
                        schedule))
                {
                    throw Corrupt(
                        "Schedule identities must be unique.");
                }
            }
        }

        if (payload.TryGetProperty(
                "scheduleOperations",
                out var operationsValue))
        {
            if (operationsValue.ValueKind != JsonValueKind.Array)
            {
                throw Corrupt(
                    "The schedule operation collection is invalid.");
            }

            foreach (var item in operationsValue.EnumerateArray())
            {
                if (model.ScheduleOperations.Count
                    >= _options.Schedules.MaxOperations)
                {
                    throw Capacity(
                        "The schedule operation capacity has been exceeded.");
                }

                var receipt =
                    WorldScheduleStoreCodec.ReadReceipt(item);
                if (!model.ScheduleOperations.TryAdd(
                        receipt.ScopedOperationKey,
                        receipt))
                {
                    throw Corrupt(
                        "Schedule operation identities must be unique.");
                }
            }
        }

        return model;
    }

    private static byte[] WritePayload(
        StoreModel model,
        long maximumBytes)
    {
        using var output = new MemoryStream();
        using var boundedOutput = new WorldBoundedArchiveWriteStream(
            output,
            maximumBytes,
            WorldDataReasonCodes.ByteLimitExceeded,
            "The authoritative store exceeds its byte limit.");
        using (var writer = new Utf8JsonWriter(boundedOutput))
        {
            writer.WriteStartObject();
            writer.WriteNumber("generation", model.Generation);
            writer.WritePropertyName("states");
            writer.WriteStartArray();
            foreach (var state in model.States.Values
                         .OrderBy(
                             item => item.Coordinate.Address.StableKey,
                             StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("coordinate");
                WriteCoordinate(writer, state.Coordinate);
                writer.WriteString("stateDigest", state.StateDigest);
                writer.WritePropertyName("state");
                state.State.WriteTo(writer);
                var chunks = NativeWorldIncarnationLedgerCodec.Encode(
                    state.EntityIncarnations,
                    state.IssuedEntityIncarnations,
                    out var byteLength);
                writer.WritePropertyName("packedIncarnations");
                writer.WriteStartObject();
                writer.WriteString("encoding", "base85-v1");
                writer.WriteNumber("byteLength", byteLength);
                writer.WritePropertyName("chunks");
                writer.WriteStartArray();
                foreach (var chunk in chunks)
                {
                    writer.WriteStringValue(chunk);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("pending");
            writer.WriteStartArray();
            foreach (var pending in model.Pending.Values
                         .OrderBy(
                             item => item.Request.ExpectedCoordinate
                                 .Scope.StableKey,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.Request.OperationId,
                             StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("ownerToken", pending.OwnerToken);
                writer.WritePropertyName("request");
                WriteRequest(writer, pending.Request);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("receipts");
            writer.WriteStartArray();
            foreach (var receipt in model.Receipts.Values
                         .OrderBy(
                             item => item.Request.ExpectedCoordinate
                                 .Scope.StableKey,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.OperationId,
                             StringComparer.Ordinal))
            {
                WriteReceipt(writer, receipt);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("history");
            writer.WriteStartArray();
            foreach (var record in model.History.Values
                         .OrderBy(
                             item => item.InstanceId,
                             StringComparer.Ordinal))
            {
                WriteHistory(writer, record);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("schedules");
            writer.WriteStartArray();
            foreach (var schedule in model.Schedules.Values
                         .OrderBy(
                             item => item.StableKey,
                             StringComparer.Ordinal))
            {
                WorldScheduleStoreCodec.WriteRecord(
                    writer,
                    schedule);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("scheduleOperations");
            writer.WriteStartArray();
            foreach (var operation in model.ScheduleOperations.Values
                         .OrderBy(
                             item => item.Scope.StableKey,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.OperationId,
                             StringComparer.Ordinal))
            {
                WorldScheduleStoreCodec.WriteReceipt(
                    writer,
                    operation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static void WriteCoordinate(
        Utf8JsonWriter writer,
        WorldAuthoritativeCoordinate coordinate)
    {
        writer.WriteStartObject();
        writer.WriteString("worldId", coordinate.WorldId);
        writer.WriteString("timelineId", coordinate.TimelineId);
        writer.WriteNumber("timelineEpoch", coordinate.TimelineEpoch);
        writer.WriteNumber("saveRevision", coordinate.SaveRevision);
        writer.WriteNumber("stateVersion", coordinate.StateVersion);
        writer.WriteString("catalogDigest", coordinate.CatalogDigest);
        writer.WriteEndObject();
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
        foreach (var expectation in request.ExpectedIncarnations)
        {
            writer.WriteStartObject();
            writer.WriteString("entityId", expectation.EntityId);
            writer.WriteNumber("incarnation", expectation.Incarnation);
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
            WriteHistory(writer, request.EventOccurrence);
        }

        writer.WriteEndObject();
    }

    private static void WriteReceipt(
        Utf8JsonWriter writer,
        WorldCommandReceipt receipt)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("request");
        WriteRequest(writer, receipt.Request);
        writer.WriteNumber("status", (int)receipt.Status);
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
            writer.WritePropertyName("typedResult");
            if (receipt.Effect.TypedResult.HasValue)
            {
                receipt.Effect.TypedResult.Value.WriteTo(writer);
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

    private static void WriteHistory(
        Utf8JsonWriter writer,
        WorldEventHistoryRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("instanceId", record.InstanceId);
        writer.WritePropertyName("definition");
        writer.WriteStartObject();
        writer.WriteString("worldId", record.Definition.WorldId);
        writer.WriteString("timelineId", record.Definition.TimelineId);
        writer.WriteNumber(
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
        writer.WriteString("resolutionKey", record.ResolutionKey);
        writer.WriteString("planFingerprint", record.PlanFingerprint);
        writer.WritePropertyName("occurredAt");
        WriteTime(writer, record.OccurredAt);
        WriteOptionalString(
            writer,
            "parentInstanceId",
            record.ParentInstanceId);
        writer.WriteEndObject();
    }

    private static void WriteTime(
        Utf8JsonWriter writer,
        GameTimePoint? time)
    {
        if (time is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("clockId", time.ClockId);
        writer.WriteString("timelineId", time.TimelineId);
        writer.WriteNumber("epoch", time.Epoch);
        writer.WriteNumber("tick", time.Tick);
        writer.WriteEndObject();
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

    private static WorldAuthoritativeCoordinate ParseCoordinate(
        JsonElement value)
    {
        WorldDataJson.RequireOnlyProperties(value, CoordinateFields);
        return new WorldAuthoritativeCoordinate(
            RequiredString(value, "worldId"),
            RequiredString(value, "timelineId"),
            RequiredInt64(value, "timelineEpoch", minimum: 0),
            RequiredInt64(value, "saveRevision", minimum: 0),
            RequiredInt64(value, "stateVersion", minimum: 0),
            RequiredString(value, "catalogDigest", 64));
    }

    private static IReadOnlyDictionary<string, long> ParseIncarnations(
        JsonElement.ArrayEnumerator values)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            WorldDataJson.RequireOnlyProperties(item, IncarnationFields);
            if (!result.TryAdd(
                    RequiredString(item, "entityId"),
                    RequiredInt64(item, "incarnation", minimum: 0)))
            {
                throw Corrupt(
                    "Entity incarnation identifiers must be unique.");
            }
        }

        return new ReadOnlyDictionary<string, long>(result);
    }

    private NativeWorldPackedIncarnationLedger ParsePackedIncarnations(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Corrupt(
                "The packed incarnation ledger is invalid.");
        }

        WorldDataJson.RequireOnlyProperties(
            value,
            PackedIncarnationFields);
        if (!string.Equals(
                RequiredString(value, "encoding", 32),
                "base85-v1",
                StringComparison.Ordinal))
        {
            throw Corrupt(
                "The packed incarnation ledger encoding is unsupported.");
        }

        var byteLength = RequiredInt64(
            value,
            "byteLength",
            minimum: 1);
        if (byteLength > _options.MaxFileBytes
            || byteLength > int.MaxValue)
        {
            throw Capacity(
                "The packed incarnation ledger exceeds its byte limit.");
        }

        if (!value.TryGetProperty("chunks", out var chunksValue)
            || chunksValue.ValueKind != JsonValueKind.Array)
        {
            throw Corrupt(
                "The packed incarnation ledger chunks are invalid.");
        }

        if (chunksValue.GetArrayLength() is < 1 or > 8)
        {
            throw Corrupt(
                "The packed incarnation ledger chunks are invalid.");
        }

        var chunks = new List<string>(
            chunksValue.GetArrayLength());
        foreach (var item in chunksValue.EnumerateArray())
        {
            var chunk = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            if (string.IsNullOrEmpty(chunk))
            {
                throw Corrupt(
                    "The packed incarnation ledger chunks are invalid.");
            }

            chunks.Add(chunk);
        }

        try
        {
            return NativeWorldIncarnationLedgerCodec.Decode(
                chunks,
                checked((int)byteLength),
                _options.MaxIssuedEntityIncarnations,
                WorldValidation.MaximumParticipants);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            throw Corrupt(
                "The packed incarnation ledger is invalid.",
                exception);
        }
    }

    private static IReadOnlyList<WorldIssuedEntityIncarnation>
        ParseIssuedIncarnations(
            JsonElement.ArrayEnumerator values,
            int maximumCount)
    {
        var result = new List<WorldIssuedEntityIncarnation>();
        var identities = new Dictionary<string, HashSet<long>>(
            StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (result.Count >= maximumCount)
            {
                throw Capacity(
                    "The issued-incarnation ledger exceeds its configured capacity.");
            }

            WorldDataJson.RequireOnlyProperties(
                item,
                IncarnationFields);
            var entityId = RequiredString(item, "entityId");
            var incarnation = RequiredInt64(
                item,
                "incarnation",
                minimum: 0);
            if (!identities.TryGetValue(
                    entityId,
                    out var incarnations))
            {
                incarnations = [];
                identities.Add(entityId, incarnations);
            }

            if (!incarnations.Add(incarnation))
            {
                throw Corrupt(
                    "Issued entity incarnations must be unique.");
            }

            result.Add(
                new WorldIssuedEntityIncarnation(
                    entityId,
                    incarnation));
        }

        return new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
            result);
    }

    private static WorldTransactionRequest ParseRequest(JsonElement value)
    {
        WorldDataJson.RequireOnlyProperties(value, RequestFields);
        var expectations = ParseIncarnations(
                RequiredArray(value, "expectedIncarnations"))
            .Select(
                pair => new WorldEntityIncarnationExpectation(
                    pair.Key,
                    pair.Value))
            .ToArray();
        WorldEventHistoryRecord? occurrence = null;
        if (!value.TryGetProperty("eventOccurrence", out var occurrenceValue))
        {
            throw Corrupt("The event occurrence field is missing.");
        }

        if (occurrenceValue.ValueKind == JsonValueKind.Object)
        {
            occurrence = ParseHistory(occurrenceValue);
        }
        else if (occurrenceValue.ValueKind != JsonValueKind.Null)
        {
            throw Corrupt("The event occurrence field is invalid.");
        }

        var request = new WorldTransactionRequest(
            RequiredString(value, "operationId"),
            RequiredString(value, "commandId"),
            RequiredString(value, "commandPayloadDigest", 64),
            ParseCoordinate(RequiredObject(value, "expectedCoordinate")),
            expectations,
            occurrence);
        if (!string.Equals(
                request.RequestFingerprint,
                RequiredString(value, "requestFingerprint", 64),
                StringComparison.Ordinal))
        {
            throw Corrupt("A request fingerprint is invalid.");
        }

        return request;
    }

    private static WorldCommandReceipt ParseReceipt(JsonElement value)
    {
        WorldDataJson.RequireOnlyProperties(value, ReceiptFields);
        var request = ParseRequest(RequiredObject(value, "request"));
        var statusValue = RequiredInt64(value, "status", minimum: 0);
        if (statusValue > (int)WorldCommandReceiptStatus.Cancelled)
        {
            throw Corrupt("A receipt status is invalid.");
        }

        WorldAuthoritativeCoordinate? resultingCoordinate = null;
        if (!value.TryGetProperty(
                "resultingCoordinate",
                out var coordinateValue))
        {
            throw Corrupt("The resulting coordinate field is missing.");
        }

        if (coordinateValue.ValueKind == JsonValueKind.Object)
        {
            resultingCoordinate = ParseCoordinate(coordinateValue);
        }
        else if (coordinateValue.ValueKind != JsonValueKind.Null)
        {
            throw Corrupt("The resulting coordinate field is invalid.");
        }

        WorldEffectReceipt? effect = null;
        if (!value.TryGetProperty("effect", out var effectValue))
        {
            throw Corrupt("The effect field is missing.");
        }

        if (effectValue.ValueKind == JsonValueKind.Object)
        {
            WorldDataJson.RequireOnlyProperties(effectValue, EffectFields);
            JsonElement? typedResult = null;
            if (!effectValue.TryGetProperty(
                    "typedResult",
                    out var typedResultValue))
            {
                throw Corrupt("The typed result field is missing.");
            }

            if (typedResultValue.ValueKind != JsonValueKind.Null)
            {
                typedResult = typedResultValue.Clone();
            }

            effect = new WorldEffectReceipt(
                RequiredBoolean(effectValue, "applied"),
                RequiredString(effectValue, "outcomeCode", 96),
                typedResult);
        }
        else if (effectValue.ValueKind != JsonValueKind.Null)
        {
            throw Corrupt("The effect field is invalid.");
        }

        var receipt = new WorldCommandReceipt(
            request,
            (WorldCommandReceiptStatus)statusValue,
            RequiredString(value, "outcomeCode", 96),
            resultingCoordinate,
            OptionalString(value, "resultingStateDigest", 64),
            effect,
            OptionalString(value, "eventInstanceId"));
        if (!string.Equals(
                receipt.ReceiptId,
                RequiredString(value, "receiptId", 128),
                StringComparison.Ordinal))
        {
            throw Corrupt("A receipt identity is invalid.");
        }

        return receipt;
    }

    private static WorldEventHistoryRecord ParseHistory(JsonElement value)
    {
        WorldDataJson.RequireOnlyProperties(value, HistoryFields);
        var definitionValue = RequiredObject(value, "definition");
        WorldDataJson.RequireOnlyProperties(
            definitionValue,
            DefinitionFields);
        var definition = new WorldEventDefinitionKey(
            RequiredString(definitionValue, "worldId"),
            RequiredString(definitionValue, "timelineId"),
            RequiredInt64(
                definitionValue,
                "timelineEpoch",
                minimum: 0),
            RequiredString(definitionValue, "definitionId"),
            RequiredString(
                definitionValue,
                "definitionVersion",
                96));
        GameTimePoint? occurredAt = null;
        if (!value.TryGetProperty("occurredAt", out var timeValue))
        {
            throw Corrupt("The occurrence time field is missing.");
        }

        if (timeValue.ValueKind == JsonValueKind.Object)
        {
            WorldDataJson.RequireOnlyProperties(timeValue, TimeFields);
            occurredAt = new GameTimePoint(
                RequiredString(timeValue, "clockId"),
                RequiredString(timeValue, "timelineId"),
                RequiredInt64(timeValue, "epoch", minimum: 0),
                RequiredInt64(timeValue, "tick"));
        }
        else if (timeValue.ValueKind != JsonValueKind.Null)
        {
            throw Corrupt("The occurrence time field is invalid.");
        }

        return new WorldEventHistoryRecord(
            RequiredString(value, "instanceId"),
            definition,
            RequiredString(value, "triggerId"),
            RequiredString(value, "resolutionKey"),
            RequiredString(value, "planFingerprint", 128),
            occurredAt,
            OptionalString(value, "parentInstanceId"));
    }

    private void ValidateLoadedModel(StoreModel model)
    {
        if (model.States.Count == 0)
        {
            throw Corrupt(
                "The authoritative store must contain at least one state.");
        }

        foreach (var receipt in model.Receipts.Values)
        {
            if (model.Pending.ContainsKey(
                    receipt.Request.ScopedOperationKey))
            {
                throw Corrupt(
                    "An operation cannot be both pending and terminal.");
            }

            AddCommand(model, receipt.Request);
        }

        var pendingTimelines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in model.Pending.Values)
        {
            AddCommand(model, pending.Request);
            var address =
                pending.Request.ExpectedCoordinate.Address.StableKey;
            if (!pendingTimelines.Add(address)
                || !model.States.TryGetValue(address, out var state)
                || !state.Coordinate.IsExactMatch(
                    pending.Request.ExpectedCoordinate)
                || (pending.Request.EventOccurrence is not null
                    && model.History.ContainsKey(
                        pending.Request.EventOccurrence.InstanceId)))
            {
                throw Corrupt(
                    "Pending ownership is inconsistent with world state.");
            }
        }

        var definitionClocks = new Dictionary<
            string,
            GameTimePoint>(StringComparer.Ordinal);
        foreach (var record in model.History.Values)
        {
            if (record.OccurredAt is null)
            {
                continue;
            }

            if (definitionClocks.TryGetValue(
                    record.Definition.StableKey,
                    out var existing)
                && !existing.IsComparableTo(record.OccurredAt))
            {
                throw InvalidHistory(
                    "Definition history mixes incompatible game clocks.");
            }

            definitionClocks.TryAdd(
                record.Definition.StableKey,
                record.OccurredAt);
        }

        foreach (var schedule in model.Schedules.Values)
        {
            var address = new WorldTimelineAddress(
                schedule.Scope.WorldId,
                schedule.Scope.TimelineId);
            if (!model.States.TryGetValue(
                    address.StableKey,
                    out var state)
                || state.Coordinate.TimelineEpoch
                != schedule.Scope.TimelineEpoch)
            {
                throw Corrupt(
                    "A schedule is not bound to an authoritative timeline.");
            }
        }

        foreach (var operation in model.ScheduleOperations.Values)
        {
            var address = new WorldTimelineAddress(
                operation.Scope.WorldId,
                operation.Scope.TimelineId);
            if (!model.States.TryGetValue(
                    address.StableKey,
                    out var state)
                || state.Coordinate.TimelineEpoch
                != operation.Scope.TimelineEpoch)
            {
                if (!operation.Applied
                    && string.Equals(
                        operation.OutcomeCode,
                        WorldScheduleReasonCodes.TimelineNotFound,
                        StringComparison.Ordinal)
                    && !operation.ResultingGeneration.HasValue
                    && !operation.ResultingStatus.HasValue
                    && operation.OccurrenceId is null
                    && operation.Claim is null)
                {
                    continue;
                }

                throw Corrupt(
                    "A schedule operation is not bound to an authoritative timeline.");
            }
        }

        foreach (var schedule in model.Schedules.Values)
        {
            if (schedule.Claim is not null
                && (!model.ScheduleOperations.TryGetValue(
                        WorldValidation.ComposeStableKey(
                            schedule.Scope.StableKey,
                            schedule.Claim.OperationId),
                        out var operation)
                    || !operation.EstablishesClaim(schedule)))
            {
                throw Corrupt(
                    "An active schedule claim lacks its establishing operation receipt.");
            }
        }
    }

    private static void AddCommand(
        StoreModel model,
        WorldTransactionRequest request)
    {
        var identity = new CommandIdentity(
            request.OperationId,
            request.RequestFingerprint);
        if (model.Commands.TryGetValue(
                request.ScopedCommandKey,
                out var existing))
        {
            if (!string.Equals(
                    existing.OperationId,
                    identity.OperationId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.RequestFingerprint,
                    identity.RequestFingerprint,
                    StringComparison.Ordinal))
            {
                throw Corrupt(
                    "A command identifier maps to conflicting operations.");
            }

            throw Corrupt(
                "A command identifier appears more than once.");
        }

        model.Commands.Add(request.ScopedCommandKey, identity);
    }

    private void EnsureCapacity(StoreModel model)
    {
        if (model.States.Count > _options.MaxStates
            || model.States.Values.Any(
                state => state.IssuedEntityIncarnations.Count
                         > _options.MaxIssuedEntityIncarnations)
            || model.Pending.Count + model.Receipts.Count
            > _options.MaxOperations
            || model.History.Count > _options.MaxHistoryRecords
            || model.Schedules.Count
            > _options.Schedules.MaxSchedules
            || model.ScheduleOperations.Count
            > _options.Schedules.MaxOperations)
        {
            throw Capacity(
                "The authoritative store exceeds its configured capacity.");
        }

        WorldScheduleStoreLogic.EnsureCapacity(
            model.Schedules.Values,
            model.ScheduleOperations.Count,
            _options.Schedules);
    }

    private void EnsureNewOperationCapacity(StoreModel model)
    {
        if (model.Pending.Count + model.Receipts.Count
            >= _options.MaxOperations)
        {
            throw Capacity("The operation capacity has been reached.");
        }
    }

    private static void ValidateHistoryAppend(
        IEnumerable<WorldEventHistoryRecord> records,
        WorldEventHistoryRecord candidate)
    {
        GameTimePoint? existingTime = null;
        foreach (var record in records)
        {
            if (!string.Equals(
                    record.Definition.StableKey,
                    candidate.Definition.StableKey,
                    StringComparison.Ordinal)
                || record.OccurredAt is null)
            {
                continue;
            }

            existingTime ??= record.OccurredAt;
            if (!existingTime.IsComparableTo(record.OccurredAt))
            {
                throw InvalidHistory(
                    "Definition history mixes incompatible game clocks.");
            }
        }

        if (candidate.OccurredAt is not null
            && existingTime is not null
            && !existingTime.IsComparableTo(candidate.OccurredAt))
        {
            throw InvalidHistory(
                "Definition history mixes incompatible game clocks.");
        }
    }

    private static GameTimePoint? LatestTime(
        IEnumerable<WorldEventHistoryRecord> records)
    {
        GameTimePoint? latest = null;
        foreach (var record in records)
        {
            if (record.OccurredAt is null)
            {
                continue;
            }

            if (latest is null
                || latest.CompareTo(record.OccurredAt) < 0)
            {
                latest = record.OccurredAt;
            }
        }

        return latest;
    }

    private static WorldCommandReceipt CreateReceipt(
        WorldTransactionRequest request,
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldAuthoritativeCoordinate? coordinate,
        string? stateDigest,
        WorldEffectReceipt? effect)
    {
        return new WorldCommandReceipt(
            request,
            status,
            outcomeCode,
            coordinate,
            stateDigest,
            effect,
            status == WorldCommandReceiptStatus.Applied
                ? request.EventOccurrence?.InstanceId
                : null);
    }

    private static string? CoordinateMismatch(
        WorldAuthoritativeCoordinate expected,
        WorldAuthoritativeCoordinate actual)
    {
        if (!expected.IsSameTimelineAs(actual))
        {
            return WorldTransactionReasonCodes.StaleCoordinate;
        }

        if (!string.Equals(
                expected.CatalogDigest,
                actual.CatalogDigest,
                StringComparison.Ordinal))
        {
            return WorldTransactionReasonCodes.StaleCatalog;
        }

        return expected.SaveRevision != actual.SaveRevision
               || expected.StateVersion != actual.StateVersion
            ? WorldTransactionReasonCodes.StaleVersion
            : null;
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

    private static bool IncarnationsMatch(
        IReadOnlyList<WorldEntityIncarnationExpectation> expected,
        IReadOnlyDictionary<string, long> actual)
    {
        return expected.All(
            item => actual.TryGetValue(
                        item.EntityId,
                        out var incarnation)
                    && incarnation == item.Incarnation);
    }

    private static bool SameRequest(
        WorldTransactionRequest left,
        WorldTransactionRequest right)
    {
        return string.Equals(
            left.RequestFingerprint,
            right.RequestFingerprint,
            StringComparison.Ordinal);
    }

    private static WorldTransactionBeginResult Conflict()
    {
        return WorldTransactionBeginResult.NonTerminal(
            WorldTransactionBeginStatus.IdempotencyConflict,
            WorldTransactionReasonCodes.IdempotencyConflict);
    }

    private static WorldTransactionReconciliationResult
        ReconciliationConflict()
    {
        return WorldTransactionReconciliationResult.NonTerminal(
            WorldTransactionReconciliationStatus.IdempotencyConflict,
            WorldTransactionReasonCodes.IdempotencyConflict);
    }

    private static WorldEventConfigurationException InvalidHistory(
        string message)
    {
        return new WorldEventConfigurationException(
            WorldEvolutionReasonCodes.InvalidHistory,
            message);
    }

    private static FileWorldAuthoritativeStoreException Capacity(
        string message)
    {
        return Error(
            FileWorldAuthoritativeStoreReasonCodes.CapacityExceeded,
            message);
    }

    private static FileWorldAuthoritativeStoreException Corrupt(
        string message,
        Exception? exception = null)
    {
        return Error(
            FileWorldAuthoritativeStoreReasonCodes.Corrupt,
            message,
            exception);
    }

    private static FileWorldAuthoritativeStoreException Error(
        string reasonCode,
        string message,
        Exception? exception = null)
    {
        return new FileWorldAuthoritativeStoreException(
            reasonCode,
            message,
            exception);
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Corrupt("A required object field is missing or invalid.");
        }

        return value;
    }

    private static JsonElement.ArrayEnumerator RequiredArray(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw Corrupt("A required array field is missing or invalid.");
        }

        return value.EnumerateArray();
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes = 512)
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
            throw Corrupt("A required string field is invalid.", exception);
        }
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes = 512)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw Corrupt("An optional string field is missing.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Corrupt("An optional string field is invalid.");
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
            throw Corrupt("An optional string field is invalid.", exception);
        }
    }

    private static long RequiredInt64(
        JsonElement parent,
        string propertyName,
        long minimum = long.MinValue)
    {
        try
        {
            return WorldDataJson.RequiredInt64(
                parent,
                propertyName,
                minimum);
        }
        catch (WorldDataContractException exception)
        {
            throw Corrupt("A required integer field is invalid.", exception);
        }
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw Corrupt("A required boolean field is invalid.");
        }

        return value.GetBoolean();
    }

    private sealed class StoreModel
    {
        public long Generation { get; set; }

        public Dictionary<string, WorldAuthoritativeStateSnapshot> States
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, PendingOperation> Pending
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorldCommandReceipt> Receipts
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorldEventHistoryRecord> History
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorldScheduleRecord> Schedules
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorldScheduleOperationReceipt>
            ScheduleOperations
        { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, CommandIdentity> Commands
        { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingOperation(
        WorldTransactionRequest request,
        string ownerToken)
    {
        public WorldTransactionRequest Request { get; } = request;

        public string OwnerToken { get; } = WorldValidation.Required(
                ownerToken,
                nameof(ownerToken),
                128);
    }

    private sealed class CommandIdentity(
        string operationId,
        string requestFingerprint)
    {
        public string OperationId { get; } = operationId;

        public string RequestFingerprint { get; } = requestFingerprint;
    }

    private sealed class LockLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed class FileTransaction(
        FileWorldAuthoritativeTransactionStore owner,
        WorldTransactionRequest request,
        WorldAuthoritativeStateSnapshot source,
        string ownerToken) : IWorldAuthoritativeTransaction
    {
        private readonly FileWorldAuthoritativeTransactionStore _owner = owner;
        private readonly string _ownerToken = ownerToken;
        private readonly FileStateDraft _draft = new FileStateDraft(source);

        public WorldTransactionRequest Request { get; } = request;

        public WorldAuthoritativeStateSnapshot Source => _draft.Source;

        public IWorldStateDraft Draft => _draft;

        public ValueTask<WorldTransactionCommitResult> CommitEventAsync(
            WorldEffectReceipt effect,
            CancellationToken cancellationToken)
        {
            if (effect is null)
            {
                throw new ArgumentNullException(nameof(effect));
            }

            if (!effect.Applied)
            {
                throw new ArgumentException(
                    "An event commit requires an applied effect.",
                    nameof(effect));
            }

            return new ValueTask<WorldTransactionCommitResult>(
                _owner.CommitEvent(
                    Request,
                    _ownerToken,
                    _draft,
                    effect,
                    cancellationToken));
        }

        public ValueTask<WorldTransactionCommitResult>
            CompleteWithoutMutationAsync(
                WorldCommandReceiptStatus status,
                string outcomeCode,
                WorldEffectReceipt? effect,
                CancellationToken cancellationToken)
        {
            var normalizedOutcome = WorldValidation.Required(
                outcomeCode,
                nameof(outcomeCode),
                96);
            return new ValueTask<WorldTransactionCommitResult>(
                _owner.CompleteWithoutMutation(
                    Request,
                    _ownerToken,
                    status,
                    normalizedOutcome,
                    effect,
                    cancellationToken));
        }

        public ValueTask DisposeAsync()
        {
            // Unknown ownership remains durable until explicit reconciliation.
            return default;
        }
    }

    private sealed class FileStateDraft(WorldAuthoritativeStateSnapshot source)
                : IWorldStateDraft,
          IWorldIssuedIncarnationDraft
    {
        private JsonElement? _replacement;
        private Dictionary<string, long>? _incarnations;
        private Dictionary<string, HashSet<long>>? _issuedIncarnations;
        private int _issuedIncarnationCount;

        public WorldAuthoritativeStateSnapshot Source { get; } = source;

        public JsonElement State => _replacement ?? Source.State;

        public string StateDigest => _replacement.HasValue
            ? WorldStateJson.ComputeDigest(_replacement.Value)
            : Source.StateDigest;

        public IReadOnlyDictionary<string, long> EntityIncarnations =>
            _incarnations is null
                ? Source.EntityIncarnations
                : new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(
                        _incarnations,
                        StringComparer.Ordinal));

        public IReadOnlyList<WorldIssuedEntityIncarnation>
            IssuedEntityIncarnations =>
            new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
                EnumerateIssuedIncarnations().ToArray());

        public bool HasChanges =>
            !string.Equals(
                StateDigest,
                Source.StateDigest,
                StringComparison.Ordinal)
            || !SameIncarnations(
                Source.EntityIncarnations,
                _incarnations)
            || _issuedIncarnations is not null;

        public void ReplaceState(JsonElement state)
        {
            WorldAuthoritativeStateSnapshot.ValidateState(
                state,
                nameof(state));
            _replacement = state.Clone();
        }

        public bool TryGetIncarnation(
            string entityId,
            out long incarnation)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            return (_incarnations ?? Source.EntityIncarnations)
                .TryGetValue(normalized, out incarnation);
        }

        public void SetIncarnation(string entityId, long incarnation)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            if (incarnation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(incarnation));
            }

            var current = _incarnations ?? Source.EntityIncarnations;
            if (current.TryGetValue(normalized, out var existing)
                && existing == incarnation)
            {
                return;
            }

            if (TryGetLatestIssuedIncarnation(
                    normalized,
                    out var latest)
                && incarnation <= latest)
            {
                throw new InvalidOperationException(
                    "A new entity incarnation must be greater than every lifetime previously issued for that entity.");
            }

            if (!current.ContainsKey(normalized)
                && current.Count >= WorldValidation.MaximumParticipants)
            {
                throw new InvalidOperationException(
                    "The current entity-incarnation collection exceeds its item limit.");
            }

            AddIssuedIncarnation(normalized, incarnation);
            EnsureIncarnationCopy()[normalized] = incarnation;
        }

        public void RemoveIncarnation(string entityId)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            EnsureIncarnationCopy().Remove(normalized);
        }

        public WorldAuthoritativeStateSnapshot Build(
            WorldAuthoritativeCoordinate coordinate)
        {
            return new WorldAuthoritativeStateSnapshot(
                coordinate,
                State,
                _incarnations ?? Source.EntityIncarnations,
                EnumerateIssuedIncarnations());
        }

        private Dictionary<string, long> EnsureIncarnationCopy()
        {
            return _incarnations ??= new Dictionary<string, long>(
                Source.EntityIncarnations,
                StringComparer.Ordinal);
        }

        private bool TryGetLatestIssuedIncarnation(
            string entityId,
            out long incarnation)
        {
            if (_issuedIncarnations is not null)
            {
                if (_issuedIncarnations.TryGetValue(
                        entityId,
                        out var incarnations)
                    && incarnations.Count != 0)
                {
                    incarnation = incarnations.Max();
                    return true;
                }

                incarnation = default;
                return false;
            }

            return Source.TryGetLatestIssuedIncarnation(
                entityId,
                out incarnation);
        }

        private void AddIssuedIncarnation(
            string entityId,
            long incarnation)
        {
            var issued = EnsureIssuedIncarnationCopy();
            if (_issuedIncarnationCount
                >= WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount)
            {
                throw new InvalidOperationException(
                    "The issued-incarnation ledger exceeds its item limit.");
            }

            if (!issued.TryGetValue(entityId, out var incarnations))
            {
                if (issued.Count
                    >= WorldAuthoritativeStateSnapshot
                        .MaximumIssuedEntityCount)
                {
                    throw new InvalidOperationException(
                        "The issued-incarnation ledger exceeds its entity limit.");
                }

                incarnations = [];
                issued.Add(entityId, incarnations);
            }

            if (!incarnations.Add(incarnation))
            {
                throw new InvalidOperationException(
                    "An issued entity incarnation cannot be reused.");
            }

            _issuedIncarnationCount++;
        }

        private Dictionary<string, HashSet<long>>
            EnsureIssuedIncarnationCopy()
        {
            if (_issuedIncarnations is not null)
            {
                return _issuedIncarnations;
            }

            _issuedIncarnations =
                new Dictionary<string, HashSet<long>>(
                    StringComparer.Ordinal);
            foreach (var item in Source.IssuedEntityIncarnations)
            {
                if (!_issuedIncarnations.TryGetValue(
                        item.EntityId,
                        out var incarnations))
                {
                    incarnations = [];
                    _issuedIncarnations.Add(
                        item.EntityId,
                        incarnations);
                }

                incarnations.Add(item.Incarnation);
            }

            _issuedIncarnationCount =
                Source.IssuedEntityIncarnations.Count;
            return _issuedIncarnations;
        }

        private IEnumerable<WorldIssuedEntityIncarnation>
            EnumerateIssuedIncarnations()
        {
            if (_issuedIncarnations is null)
            {
                return Source.IssuedEntityIncarnations;
            }

            return _issuedIncarnations
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(
                    pair => pair.Value
                        .OrderBy(value => value)
                        .Select(
                            value =>
                                new WorldIssuedEntityIncarnation(
                                    pair.Key,
                                    value)));
        }

        private static bool SameIncarnations(
            IReadOnlyDictionary<string, long> source,
            IReadOnlyDictionary<string, long>? changed)
        {
            if (changed is null)
            {
                return true;
            }

            return source.Count == changed.Count
                   && source.All(
                       pair => changed.TryGetValue(
                                   pair.Key,
                                   out var value)
                               && value == pair.Value);
        }
    }
}
