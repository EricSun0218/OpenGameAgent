using System.Globalization;

namespace GameAgent.World;

/// <summary>
/// Bounded configuration for the engine-neutral native-world composition
/// root.
/// </summary>
public sealed class NativeWorldRuntimeOptions
{
    public NativeWorldRuntimeOptions(
        WorldEventPlannerOptions? planner = null,
        WorldAdvanceClockRunnerOptions? clock = null)
    {
        Planner = planner ?? new WorldEventPlannerOptions();
        Clock = clock ?? new WorldAdvanceClockRunnerOptions();
    }

    public WorldEventPlannerOptions Planner { get; }

    public WorldAdvanceClockRunnerOptions Clock { get; }
}

/// <summary>
/// An admitted interaction together with the exact authoritative coordinate
/// from which it was planned. The coordinate cannot be replaced at execution
/// time.
/// </summary>
public sealed class NativeWorldPlannedInteraction
{
    internal NativeWorldPlannedInteraction(
        WorldInteractionPlan plan,
        WorldAuthoritativeCoordinate expectedCoordinate)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
    }

    public WorldInteractionPlan Plan { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }
}

/// <summary>
/// Engine-neutral composition root for one activated package and one
/// authoritative timeline. It wires the package handlers, durable history,
/// typed effects, interaction facade, and discrete-clock runner to the same
/// transaction store. Engine callers should use this type's direct methods;
/// file-backed work is dispatched through the bounded world background lane.
/// </summary>
public sealed class NativeWorldRuntime
{
    private readonly bool _requiresBackgroundIo;

    private NativeWorldRuntime(
        ActivatedWorldPackage package,
        WorldTimelineAddress address,
        long timelineEpoch,
        IWorldAuthoritativeTransactionStore transactionStore,
        IWorldEventHistory history,
        IWorldScheduleStore scheduleStore,
        NativeWorldRuntimeOptions options)
    {
        Package = package;
        Address = address;
        TimelineEpoch = timelineEpoch;
        TransactionStore = transactionStore;
        ScheduleStore = scheduleStore;
        _requiresBackgroundIo =
            transactionStore is FileWorldAuthoritativeTransactionStore;
        Planner = new WorldEventPlanner(
            package.EventHandlers,
            history,
            options.Planner);
        AuthoritativeExecutor =
            new WorldAuthoritativeEventPlanExecutor(
                transactionStore,
                package.TransactionalEffects);
        InteractiveWorld = new InteractiveWorldFacade(
            Planner,
            executor: null,
            interactionQueries: null,
            authoritativeExecutor: AuthoritativeExecutor);
        ClockRunner = new WorldAdvanceClockRunner(
            package,
            transactionStore,
            options.Clock);
    }

    public ActivatedWorldPackage Package { get; }

    public WorldTimelineAddress Address { get; }

    public long TimelineEpoch { get; }

    /// <summary>
    /// Advanced persistence escape hatch. File-backed engine integrations
    /// should prefer the runtime methods so locking I/O stays off the engine
    /// thread.
    /// </summary>
    public IWorldAuthoritativeTransactionStore TransactionStore { get; }

    /// <summary>
    /// Durable game-time intent boundary sharing the authoritative store's
    /// timeline isolation and persistence transaction.
    /// </summary>
    public IWorldScheduleStore ScheduleStore { get; }

    public WorldEventPlanner Planner { get; }

    public WorldAuthoritativeEventPlanExecutor AuthoritativeExecutor
    {
        get;
    }

    public InteractiveWorldFacade InteractiveWorld { get; }

    public WorldAdvanceClockRunner ClockRunner { get; }

    /// <summary>
    /// Creates a process-local runtime from the package's declared initial
    /// state.
    /// </summary>
    public static NativeWorldRuntime CreateInMemory(
        ActivatedWorldPackage package,
        string? timelineId = null,
        long timelineEpoch = 0,
        NativeWorldRuntimeOptions? options = null)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        return CreateInMemoryFromSnapshot(
            package,
            package.CreateInitialSnapshot(timelineId, timelineEpoch),
            options);
    }

    /// <summary>
    /// Creates a process-local runtime from an existing authoritative
    /// snapshot that is already bound to the activated package.
    /// </summary>
    public static NativeWorldRuntime CreateInMemoryFromSnapshot(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot initialState,
        NativeWorldRuntimeOptions? options = null)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (initialState is null)
        {
            throw new ArgumentNullException(nameof(initialState));
        }

        ValidatePackageBinding(
            package,
            initialState,
            initialState.Coordinate.Address,
            initialState.Coordinate.TimelineEpoch,
            nameof(initialState));
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            initialState);
        return Compose(
            package,
            initialState.Coordinate.Address,
            initialState.Coordinate.TimelineEpoch,
            store,
            options);
    }

    /// <summary>
    /// Opens or creates a bounded local-file runtime. Existing state must be
    /// bound to the same world, timeline epoch, and activated catalog. File
    /// locking, parsing, and initialization run on the bounded world
    /// background lane.
    /// </summary>
    public static ValueTask<NativeWorldRuntime> CreateFileAsync(
        ActivatedWorldPackage package,
        string path,
        string? timelineId = null,
        long timelineEpoch = 0,
        FileWorldAuthoritativeTransactionStoreOptions? storeOptions = null,
        NativeWorldRuntimeOptions? runtimeOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var initial = package.CreateInitialSnapshot(
            timelineId,
            timelineEpoch);
        return DispatchAsync(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var store =
                    new FileWorldAuthoritativeTransactionStore(
                        path,
                        new[] { initial },
                        storeOptions);
                var address = initial.Coordinate.Address;
                var current = await store.ReadAsync(
                        address,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (current is null)
                {
                    throw new InvalidOperationException(
                        "The authoritative store does not contain the "
                        + "requested world timeline.");
                }

                ValidatePackageBinding(
                    package,
                    current,
                    address,
                    timelineEpoch,
                    nameof(path));
                if (!store.SchedulesUseOnlyDeclaredClocks(
                        address,
                        timelineEpoch,
                        package.Clocks.Select(
                            clock => clock.ClockId),
                        cancellationToken))
                {
                    throw new ArgumentException(
                        "The authoritative store contains a schedule "
                        + "whose clock is not declared by the activated package.",
                        nameof(path));
                }

                return Compose(
                    package,
                    address,
                    timelineEpoch,
                    store,
                    runtimeOptions);
            });
    }

    public ValueTask<WorldAuthoritativeStateSnapshot?>
        ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
    {
        return RunStoreOperationAsync(
            () => ReadSnapshotCoreAsync(cancellationToken));
    }

    internal ValueTask<WorldCommandReceipt?> ReadReceiptAsync(
        string receiptId,
        int maximumTransactionRecords,
        CancellationToken cancellationToken)
    {
        var normalizedReceiptId = WorldValidation.Required(
            receiptId,
            nameof(receiptId),
            128);
        if (maximumTransactionRecords is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionRecords));
        }
        if (TransactionStore
            is not IWorldAuthoritativeReceiptSource source)
        {
            return new ValueTask<WorldCommandReceipt?>(
                result: null);
        }

        return RunStoreOperationAsync(
            () => source.ReadReceiptAsync(
                Address,
                TimelineEpoch,
                normalizedReceiptId,
                maximumTransactionRecords,
                cancellationToken));
    }

    internal ValueTask<WorldAuthoritativeStoreCapture>
        CaptureSettledStoreAsync(
            int maximumTransactionRecords,
            int maximumHistoryRecords,
            int maximumScheduleRecords,
            int maximumScheduleOperations,
            CancellationToken cancellationToken)
    {
        if (TransactionStore
            is not IWorldAuthoritativeStoreCaptureSource source)
        {
            throw new NativeWorldSaveBridgeException(
                NativeWorldSaveBridgeReasonCodes.UnsupportedStore,
                "The authoritative store cannot provide an atomic settled capture.");
        }

        return RunStoreOperationAsync(
            () => source.CaptureSettledAsync(
                Address,
                TimelineEpoch,
                maximumTransactionRecords,
                maximumHistoryRecords,
                maximumScheduleRecords,
                maximumScheduleOperations,
                cancellationToken));
    }

    internal static NativeWorldRuntime RestoreInMemory(
        ActivatedWorldPackage package,
        WorldAuthoritativeStoreCapture capture,
        NativeWorldRuntimeOptions? options,
        WorldScheduleStoreOptions? scheduleOptions = null)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        var snapshot = capture.Snapshot;
        ValidatePackageBinding(
            package,
            snapshot,
            snapshot.Coordinate.Address,
            snapshot.Coordinate.TimelineEpoch,
            nameof(capture));
        var store =
            new InMemoryWorldAuthoritativeTransactionStore(
                capture,
                scheduleOptions);
        return Compose(
            package,
            snapshot.Coordinate.Address,
            snapshot.Coordinate.TimelineEpoch,
            store,
            options);
    }

    public ValueTask<InteractiveWorldResult<InteractionQueryResult>>
        QueryInteractionsAsync(
            InteractionQueryRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return RunStoreOperationAsync(
            () => QueryInteractionsCoreAsync(request, cancellationToken));
    }

    private async ValueTask<
            InteractiveWorldResult<InteractionQueryResult>>
        QueryInteractionsCoreAsync(
            InteractionQueryRequest request,
            CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadSnapshotCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return InteractiveWorldResult<InteractionQueryResult>.Rejected(
                WorldTransactionReasonCodes.StateNotFound);
        }

        return await InteractiveWorld.QueryInteractionsAsync(
                Package.InteractionCatalog,
                request,
                CreateFence(snapshot),
                Package.CreateInteractionAdmissionEvaluator(snapshot),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<
            InteractiveWorldResult<NativeWorldPlannedInteraction>>
        PlanInteractionAsync(
            InteractionExecutionRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return RunStoreOperationAsync(
            () => PlanInteractionCoreAsync(request, cancellationToken));
    }

    private async ValueTask<
            InteractiveWorldResult<NativeWorldPlannedInteraction>>
        PlanInteractionCoreAsync(
            InteractionExecutionRequest request,
            CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return InteractiveWorldResult<
                NativeWorldPlannedInteraction>.Rejected(
                WorldTransactionReasonCodes.StateNotFound);
        }

        var result = await InteractiveWorld.PlanInteractionAsync(
                Package.InteractionCatalog,
                request,
                CreateFence(snapshot),
                new NativeWorldPlanningContext(snapshot),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return InteractiveWorldResult<
                NativeWorldPlannedInteraction>.Rejected(
                result.ReasonCode,
                result.ParameterErrors);
        }

        return InteractiveWorldResult<
            NativeWorldPlannedInteraction>.Success(
                new NativeWorldPlannedInteraction(
                    result.Value,
                    snapshot.Coordinate));
    }

    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteInteractionAsync(
            NativeWorldPlannedInteraction interaction,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        EnsureBoundCoordinate(
            interaction.ExpectedCoordinate,
            nameof(interaction));
        return RunStoreOperationAsync(
            () =>
                InteractiveWorld.ExecuteAuthoritativeInteractionAsync(
                    interaction.Plan,
                    interaction.ExpectedCoordinate,
                    hostContext,
                    cancellationToken));
    }

    public ValueTask<WorldAdvanceClockResult> AdvanceClockAsync(
        WorldAdvanceClockCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        EnsureBoundCoordinate(command.ExpectedCoordinate, nameof(command));
        return RunStoreOperationAsync(
            () => ClockRunner.ExecuteAsync(command, cancellationToken));
    }

    public ValueTask<WorldScheduleMutationResult> ExecuteScheduleAsync(
        WorldScheduleCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        EnsureBoundScope(command.Scope, nameof(command));
        return RunStoreOperationAsync(
            () => ExecuteScheduleCoreAsync(
                command,
                cancellationToken));
    }

    public ValueTask<WorldScheduleRecord?> FindScheduleAsync(
        string scheduleId,
        CancellationToken cancellationToken = default)
    {
        return RunStoreOperationAsync(
            () => ScheduleStore.FindAsync(
                new WorldTransactionScope(
                    Address.WorldId,
                    Address.TimelineId,
                    TimelineEpoch),
                scheduleId,
                cancellationToken));
    }

    public ValueTask<WorldScheduleDuePage> QueryDueSchedulesAsync(
        WorldScheduleDueQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        EnsureBoundScope(query.Scope, nameof(query));
        return RunStoreOperationAsync(
            () => QueryDueSchedulesCoreAsync(
                query,
                cancellationToken));
    }

    private async ValueTask<WorldScheduleMutationResult>
        ExecuteScheduleCoreAsync(
            WorldScheduleCommand command,
            CancellationToken cancellationToken)
    {
        var dueAt = command.Kind switch
        {
            WorldScheduleOperationKind.Create =>
                command.CreateIntent!.DueAt,
            WorldScheduleOperationKind.Reschedule =>
                command.DueAt!,
            _ => null
        };
        if (dueAt is not null
            && !Package.Clocks.Any(
                clock => string.Equals(
                    clock.ClockId,
                    dueAt.ClockId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A schedule must use a declared package clock.",
                nameof(command));
        }

        if (command.Kind == WorldScheduleOperationKind.Claim)
        {
            var current = await ReadSnapshotCoreAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null
                || !TryReadClock(
                    current,
                    command.ObservedAt!.ClockId,
                    out var currentTick)
                || command.ObservedAt.Tick > currentTick)
            {
                throw new ArgumentException(
                    "A claim cannot observe a future or undeclared game-time point.",
                    nameof(command));
            }
        }

        return await ScheduleStore.ExecuteAsync(
                command,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorldScheduleDuePage>
        QueryDueSchedulesCoreAsync(
            WorldScheduleDueQuery query,
            CancellationToken cancellationToken)
    {
        var current = await ReadSnapshotCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || !TryReadClock(
                current,
                query.ClockId,
                out var currentTick)
            || query.ThroughTick > currentTick)
        {
            throw new ArgumentException(
                "A due query cannot observe a future or undeclared game-time point.",
                nameof(query));
        }

        return await ScheduleStore.QueryDueAsync(
                query,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static NativeWorldRuntime Compose<TStore>(
        ActivatedWorldPackage package,
        WorldTimelineAddress address,
        long timelineEpoch,
        TStore store,
        NativeWorldRuntimeOptions? options)
        where TStore : IWorldAuthoritativeTransactionStore,
        IWorldEventHistory,
        IWorldScheduleStore
    {
        return new NativeWorldRuntime(
            package,
            address,
            timelineEpoch,
            store,
            store,
            store,
            options ?? new NativeWorldRuntimeOptions());
    }

    private static WorldStateFence CreateFence(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        return new WorldStateFence(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            coordinate.CatalogDigest);
    }

    private async ValueTask<WorldAuthoritativeStateSnapshot?>
        ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = await TransactionStore.ReadAsync(
                Address,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is not null
            && !IsPackageBound(
                Package,
                snapshot,
                Address,
                TimelineEpoch))
        {
            throw new InvalidOperationException(
                "The authoritative store no longer matches this runtime's "
                + "world timeline and catalog.");
        }

        return snapshot;
    }

    private ValueTask<T> RunStoreOperationAsync<T>(
        Func<ValueTask<T>> operation)
    {
        return _requiresBackgroundIo
            ? DispatchAsync(operation)
            : operation();
    }

    private static ValueTask<T> DispatchAsync<T>(
        Func<ValueTask<T>> operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = WorldBackgroundWorkDispatcher.Dispatch(
                async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await operation().ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return new ValueTask<T>(completion.Task);
    }

    private static void ValidatePackageBinding(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot,
        WorldTimelineAddress address,
        long timelineEpoch,
        string parameterName)
    {
        if (!IsPackageBound(
                package,
                snapshot,
                address,
                timelineEpoch))
        {
            throw new ArgumentException(
                "The authoritative snapshot is not bound to the requested "
                + "activated world timeline and catalog.",
                parameterName);
        }
    }

    private static bool IsPackageBound(
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot,
        WorldTimelineAddress address,
        long timelineEpoch)
    {
        var coordinate = snapshot.Coordinate;
        return string.Equals(
                   coordinate.WorldId,
                   package.World.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   coordinate.WorldId,
                   address.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   coordinate.TimelineId,
                   address.TimelineId,
                   StringComparison.Ordinal)
               && coordinate.TimelineEpoch == timelineEpoch
               && string.Equals(
                   coordinate.CatalogDigest,
                   package.CatalogDigest,
                   StringComparison.Ordinal);
    }

    private void EnsureBoundCoordinate(
        WorldAuthoritativeCoordinate coordinate,
        string parameterName)
    {
        if (!string.Equals(
                coordinate.WorldId,
                Address.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                coordinate.TimelineId,
                Address.TimelineId,
                StringComparison.Ordinal)
            || coordinate.TimelineEpoch != TimelineEpoch
            || !string.Equals(
                coordinate.CatalogDigest,
                Package.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The command is not bound to this runtime timeline.",
                parameterName);
        }
    }

    private void EnsureBoundScope(
        WorldTransactionScope scope,
        string parameterName)
    {
        if (!string.Equals(
                scope.WorldId,
                Address.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                scope.TimelineId,
                Address.TimelineId,
                StringComparison.Ordinal)
            || scope.TimelineEpoch != TimelineEpoch)
        {
            throw new ArgumentException(
                "The schedule is not bound to this runtime timeline.",
                parameterName);
        }
    }

    private bool TryReadClock(
        WorldAuthoritativeStateSnapshot snapshot,
        string clockId,
        out long tick)
    {
        var clock = Package.Clocks.FirstOrDefault(
            item => string.Equals(
                item.ClockId,
                clockId,
                StringComparison.Ordinal));
        if (clock is null
            || !NativeWorldConditionEvaluator.TryResolve(
                snapshot.State,
                clock.StatePath,
                out var value)
            || value.ValueKind
            != System.Text.Json.JsonValueKind.String
            || !NativeWorldConditionEvaluator.TryParseCanonicalInt64(
                value.GetString(),
                out tick))
        {
            tick = 0;
            return false;
        }

        return true;
    }
}
