namespace GameAgent.World;

/// <summary>
/// Stateful engine convenience layer shared by managed engine adapters. It
/// owns only loaded artifacts and bounded background work; the game still
/// owns definitions, handlers, catalogs, and authoritative state.
/// </summary>
public sealed class InteractiveWorldEngineSession : IAsyncDisposable
{
    private readonly object _artifactGate = new();
    private readonly InteractiveWorldFacade _facade;
    private readonly WorldBackgroundOperationQueue _operations;
    private WorldPackageDefinition? _package;
    private WorldSaveDocument? _save;

    public InteractiveWorldEngineSession(
        InteractiveWorldFacade facade,
        int backgroundCapacity = 256)
    {
        _facade = facade
                  ?? throw new ArgumentNullException(nameof(facade));
        _operations = new WorldBackgroundOperationQueue(
            backgroundCapacity);
    }

    public WorldPackageDefinition? CurrentPackage
    {
        get
        {
            lock (_artifactGate)
            {
                return _package;
            }
        }
    }

    public WorldSaveDocument? CurrentSave
    {
        get
        {
            lock (_artifactGate)
            {
                return _save;
            }
        }
    }

    public int OutstandingOperationCount => _operations.OutstandingCount;

    public WorldPackageDefinition ImportPackage(
        ReadOnlySpan<byte> archive,
        WorldPackageLimits? limits = null)
    {
        var package = _facade.ImportPackage(archive, limits);
        ReplacePackage(package);
        return package;
    }

    public WorldPackageDefinition ImportPackageFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        var package = _facade.ImportPackageFile(path, limits);
        ReplacePackage(package);
        return package;
    }

    public byte[] ExportPackage(WorldPackageLimits? limits = null)
    {
        return _facade.ExportPackage(
            RequirePackage(),
            limits);
    }

    public void ExportPackageFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        _facade.ExportPackageFile(path, RequirePackage(), limits);
    }

    public WorldSaveDocument ImportSave(
        ReadOnlySpan<byte> utf8,
        WorldPackageLimits? limits = null)
    {
        lock (_artifactGate)
        {
            var save = _facade.ImportSave(
                utf8,
                RequirePackageLocked(),
                limits);
            _save = save;
            return save;
        }
    }

    public WorldSaveDocument ImportSaveFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        lock (_artifactGate)
        {
            var save = _facade.ImportSaveFile(
                path,
                RequirePackageLocked(),
                limits);
            _save = save;
            return save;
        }
    }

    public void SetSave(WorldSaveDocument save)
    {
        if (save is null)
        {
            throw new ArgumentNullException(nameof(save));
        }

        lock (_artifactGate)
        {
            WorldSaveBinding.Validate(save, RequirePackageLocked());
            _save = save;
        }
    }

    public byte[] ExportSave(WorldPackageLimits? limits = null)
    {
        return _facade.ExportSave(RequireSave(), limits);
    }

    public void ExportSaveFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        _facade.ExportSaveFile(path, RequireSave(), limits);
    }

    public ValueTask<InteractiveWorldResult<WorldEventPlan>>
        PlanTriggerAsync(
            WorldEvolutionTrigger trigger,
            IReadOnlyList<WorldEventDefinition> definitions,
            WorldStateFence currentState,
            int cascadeDepth = 0,
            string? parentInstanceId = null,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.PlanTriggerAsync(
            trigger,
            definitions,
            currentState,
            cascadeDepth,
            parentInstanceId,
            hostContext,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<WorldEventPlan>>
        PlanTriggerAsync(
            WorldEvolutionTrigger trigger,
            WorldEventCatalogSnapshot catalog,
            WorldStateFence currentState,
            int cascadeDepth = 0,
            string? parentInstanceId = null,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.PlanTriggerAsync(
            trigger,
            catalog,
            currentState,
            cascadeDepth,
            parentInstanceId,
            hostContext,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<InteractionQueryResult>>
        QueryInteractionsAsync(
            InteractionCatalogSnapshot catalog,
            InteractionQueryRequest request,
            WorldStateFence currentState,
            IInteractionAdmissionEvaluator evaluator,
            CancellationToken cancellationToken = default)
    {
        return _facade.QueryInteractionsAsync(
            catalog,
            request,
            currentState,
            evaluator,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<WorldInteractionPlan>>
        PlanInteractionAsync(
            InteractionCatalogSnapshot catalog,
            InteractionExecutionRequest request,
            WorldStateFence currentState,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.PlanInteractionAsync(
            catalog,
            request,
            currentState,
            hostContext,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<WorldPlanExecutionResult>>
        ExecutePlanAsync(
            WorldEventPlan plan,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.ExecutePlanAsync(
            plan,
            hostContext,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteAuthoritativePlanAsync(
            WorldAuthoritativeEventPlan artifact,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.ExecuteAuthoritativePlanAsync(
            artifact,
            hostContext,
            cancellationToken);
    }

    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteAuthoritativeInteractionAsync(
            WorldInteractionPlan interaction,
            WorldAuthoritativeCoordinate expectedCoordinate,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        return _facade.ExecuteAuthoritativeInteractionAsync(
            interaction,
            expectedCoordinate,
            hostContext,
            cancellationToken);
    }

    public bool TryScheduleTrigger(
        string operationId,
        WorldEvolutionTrigger trigger,
        IReadOnlyList<WorldEventDefinition> definitions,
        WorldStateFence currentState,
        out string? rejectionReason,
        int cascadeDepth = 0,
        string? parentInstanceId = null,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.TriggerPlanning,
            async token => await PlanTriggerAsync(
                trigger,
                definitions,
                currentState,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleTrigger(
        string operationId,
        WorldEvolutionTrigger trigger,
        WorldEventCatalogSnapshot catalog,
        WorldStateFence currentState,
        out string? rejectionReason,
        int cascadeDepth = 0,
        string? parentInstanceId = null,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.TriggerPlanning,
            async token => await PlanTriggerAsync(
                trigger,
                catalog,
                currentState,
                cascadeDepth,
                parentInstanceId,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleInteractionQuery(
        string operationId,
        InteractionCatalogSnapshot catalog,
        InteractionQueryRequest request,
        WorldStateFence currentState,
        IInteractionAdmissionEvaluator evaluator,
        out string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.InteractionQuery,
            async token => await QueryInteractionsAsync(
                catalog,
                request,
                currentState,
                evaluator,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleInteraction(
        string operationId,
        InteractionCatalogSnapshot catalog,
        InteractionExecutionRequest request,
        WorldStateFence currentState,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.InteractionPlanning,
            async token => await PlanInteractionAsync(
                catalog,
                request,
                currentState,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleExecution(
        string operationId,
        WorldEventPlan plan,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.PlanExecution,
            async token => await ExecutePlanAsync(
                plan,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleAuthoritativeExecution(
        string operationId,
        WorldAuthoritativeEventPlan artifact,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.PlanExecution,
            async token => await ExecuteAuthoritativePlanAsync(
                artifact,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryScheduleAuthoritativeInteraction(
        string operationId,
        WorldInteractionPlan interaction,
        WorldAuthoritativeCoordinate expectedCoordinate,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return _operations.TrySchedule(
            operationId,
            WorldBackgroundOperationKind.PlanExecution,
            async token => await ExecuteAuthoritativeInteractionAsync(
                interaction,
                expectedCoordinate,
                hostContext,
                token).ConfigureAwait(false),
            out rejectionReason,
            cancellationToken);
    }

    public bool TryCancel(string operationId)
    {
        return _operations.TryCancel(operationId);
    }

    public int Pump(
        int maximumResults,
        Action<WorldBackgroundOperationResult> publish)
    {
        return _operations.Drain(maximumResults, publish);
    }

    /// <summary>
    /// Controlled engine shutdown. The caller must publish returned results
    /// on the engine main thread before releasing game-owned handlers and
    /// stores.
    /// </summary>
    public ValueTask<IReadOnlyList<WorldBackgroundOperationResult>>
        ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return _operations.ShutdownAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _operations.DisposeAsync();
    }

    private void ReplacePackage(WorldPackageDefinition package)
    {
        lock (_artifactGate)
        {
            _package = package;
            if (_save is not null)
            {
                try
                {
                    WorldSaveBinding.Validate(_save, package);
                }
                catch (WorldDataContractException)
                {
                    _save = null;
                }
            }
        }
    }

    private WorldPackageDefinition RequirePackage()
    {
        lock (_artifactGate)
        {
            return RequirePackageLocked();
        }
    }

    private WorldPackageDefinition RequirePackageLocked()
    {
        return _package
               ?? throw new InvalidOperationException(
                   "Import a world package before this operation.");
    }

    private WorldSaveDocument RequireSave()
    {
        lock (_artifactGate)
        {
            return _save
                   ?? throw new InvalidOperationException(
                       "Import or set a world save before this operation.");
        }
    }
}
