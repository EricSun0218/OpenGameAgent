using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace GameAgent.World;

/// <summary>
/// Stable reason codes returned by the engine-neutral interactive-world
/// facade. Engine adapters forward these values without translating them.
/// </summary>
public static class InteractiveWorldReasonCodes
{
    public const string WorldFenceMismatch = "world_fence_mismatch";

    public const string StaleState = "world_state_stale";

    public const string StaleCatalog = "world_catalog_stale";

    public const string ExecutionNotConfigured =
        "world_execution_not_configured";
}

/// <summary>
/// The authoritative state identity captured by an engine before it asks the
/// portable world layer to query, compile, or plan work.
/// </summary>
public sealed class WorldStateFence
{
    public WorldStateFence(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        string stateVersion,
        string? catalogDigest = null,
        string? eventCatalogDigest = null,
        string? interactionCatalogDigest = null)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        StateVersion = WorldValidation.Required(
            stateVersion,
            nameof(stateVersion));
        if (catalogDigest is not null
            && !GameAgent.Core.CanonicalJsonDigest.IsSha256(catalogDigest))
        {
            throw new ArgumentException(
                "Catalog digest must be a lowercase SHA-256 digest.",
                nameof(catalogDigest));
        }

        if (eventCatalogDigest is not null
            && !GameAgent.Core.CanonicalJsonDigest.IsSha256(
                eventCatalogDigest))
        {
            throw new ArgumentException(
                "Event catalog digest must be a lowercase SHA-256 digest.",
                nameof(eventCatalogDigest));
        }

        if (interactionCatalogDigest is not null
            && !GameAgent.Core.CanonicalJsonDigest.IsSha256(
                interactionCatalogDigest))
        {
            throw new ArgumentException(
                "Interaction catalog digest must be a lowercase SHA-256 "
                + "digest.",
                nameof(interactionCatalogDigest));
        }

        TimelineEpoch = timelineEpoch;
        SaveRevision = saveRevision;
        CatalogDigest = catalogDigest;
        EventCatalogDigest = eventCatalogDigest;
        InteractionCatalogDigest = interactionCatalogDigest;
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public string StateVersion { get; }

    public string? CatalogDigest { get; }

    public string? EventCatalogDigest { get; }

    public string? InteractionCatalogDigest { get; }
}

/// <summary>
/// A non-throwing admission result for expected stale-input and validation
/// failures. Configuration errors and handler failures still throw.
/// </summary>
public sealed class InteractiveWorldResult<T>
    where T : class
{
    private InteractiveWorldResult(
        T? value,
        string reasonCode,
        IReadOnlyList<InteractionParameterValidationError>? parameterErrors)
    {
        Value = value;
        ReasonCode = reasonCode;
        ParameterErrors = parameterErrors
                          ?? Array.Empty<
                              InteractionParameterValidationError>();
    }

    public bool Succeeded => Value is not null;

    public T? Value { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<InteractionParameterValidationError>
        ParameterErrors
    { get; }

    public static InteractiveWorldResult<T> Success(T value)
    {
        return new InteractiveWorldResult<T>(
            value ?? throw new ArgumentNullException(nameof(value)),
            string.Empty,
            null);
    }

    public static InteractiveWorldResult<T> Rejected(
        string reasonCode,
        IReadOnlyList<InteractionParameterValidationError>?
            parameterErrors = null)
    {
        return new InteractiveWorldResult<T>(
            null,
            WorldValidation.Required(
                reasonCode,
                nameof(reasonCode),
                96),
            parameterErrors is null
                ? null
                : new ReadOnlyCollection<
                    InteractionParameterValidationError>(
                    parameterErrors.ToArray()));
    }
}

public sealed class WorldInteractionPlan
{
    internal WorldInteractionPlan(
        CompiledInteractionExecution compilation,
        WorldEventPlan plan)
    {
        Compilation = compilation
                      ?? throw new ArgumentNullException(
                          nameof(compilation));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public CompiledInteractionExecution Compilation { get; }

    public WorldEventPlan Plan { get; }

    /// <summary>
    /// Binds this admitted interaction to the exact reference-store
    /// coordinate it was compiled from. A stale interaction cannot be rebound
    /// to a newer revision.
    /// </summary>
    public WorldAuthoritativeEventPlan Bind(
        WorldAuthoritativeCoordinate expectedCoordinate)
    {
        if (expectedCoordinate is null)
        {
            throw new ArgumentNullException(nameof(expectedCoordinate));
        }

        var trigger = Compilation.Trigger;
        if (!string.Equals(
                trigger.WorldId,
                expectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                trigger.TimelineId,
                expectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || trigger.TimelineEpoch != expectedCoordinate.TimelineEpoch
            || trigger.ExpectedSaveRevision
            != expectedCoordinate.SaveRevision
            || !string.Equals(
                trigger.ExpectedStateVersion,
                expectedCoordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(
                trigger.CatalogDigest,
                expectedCoordinate.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The interaction plan was not compiled from the supplied authoritative coordinate.",
                nameof(expectedCoordinate));
        }

        return new WorldAuthoritativeEventPlan(
            Plan,
            expectedCoordinate);
    }
}

public sealed class WorldPlanExecutionResult
{
    private readonly JsonElement? _evidence;

    public WorldPlanExecutionResult(
        string outcomeCode,
        JsonElement? evidence = null)
    {
        OutcomeCode = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        if (evidence.HasValue)
        {
            GameAgent.Core.JsonValueInspector.ValidateAndMeasure(
                evidence.Value,
                new GameAgent.Core.JsonValueLimits(
                    maxUtf8Bytes: 262_144,
                    maxDepth: 32,
                    maxNodes: 16_384,
                    maxStringUtf8Bytes: 65_536,
                    maxContainerItems: 4_096),
                nameof(evidence));
            _evidence = evidence.Value.Clone();
        }
    }

    public string OutcomeCode { get; }

    public JsonElement? Evidence => _evidence?.Clone();
}

/// <summary>
/// Game-owned transaction boundary for an admitted event plan. The framework
/// never supplies business mutation rules. An implementation must atomically
/// coordinate authoritative state, receipts, and occurrence history.
/// </summary>
public interface IWorldEventPlanExecutor
{
    ValueTask<WorldPlanExecutionResult> ExecuteAsync(
        WorldEventPlan plan,
        object? hostContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Engine-neutral artifact, interaction, and event facade shared by every
/// managed engine adapter.
/// </summary>
public sealed class InteractiveWorldFacade
{
    private readonly WorldEventPlanner _planner;
    private readonly InteractionQueryService _interactionQueries;
    private readonly IWorldEventPlanExecutor? _executor;
    private readonly IWorldAuthoritativeEventPlanExecutor?
        _authoritativeExecutor;

    public InteractiveWorldFacade(
        WorldEventPlanner planner,
        IWorldEventPlanExecutor? executor = null,
        InteractionQueryService? interactionQueries = null,
        IWorldAuthoritativeEventPlanExecutor?
            authoritativeExecutor = null)
    {
        _planner = planner
                   ?? throw new ArgumentNullException(nameof(planner));
        _executor = executor;
        _authoritativeExecutor =
            authoritativeExecutor
            ?? executor as IWorldAuthoritativeEventPlanExecutor;
        _interactionQueries =
            interactionQueries ?? new InteractionQueryService();
    }

    public WorldPackageDefinition ImportPackage(
        ReadOnlySpan<byte> archive,
        WorldPackageLimits? limits = null)
    {
        var effectiveLimits = limits ?? new WorldPackageLimits();
        if (archive.Length > effectiveLimits.MaxCompressedBytes)
        {
            throw new WorldDataContractException(
                WorldDataReasonCodes.CompressionLimitExceeded,
                "Native package exceeds its compressed byte limit.");
        }

        using var stream = new MemoryStream(archive.ToArray(), writable: false);
        return WorldPackageArchive.Read(stream, effectiveLimits);
    }

    public WorldPackageDefinition ImportPackageFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        using var stream = OpenRead(path);
        return WorldPackageArchive.Read(stream, limits);
    }

    public byte[] ExportPackage(
        WorldPackageDefinition package,
        WorldPackageLimits? limits = null)
    {
        using var stream = new MemoryStream();
        WorldPackageArchive.Write(stream, package, limits);
        return stream.ToArray();
    }

    public void ExportPackageFile(
        string path,
        WorldPackageDefinition package,
        WorldPackageLimits? limits = null)
    {
        var bytes = ExportPackage(package, limits);
        WriteAtomic(path, bytes);
    }

    public WorldSaveDocument ImportSave(
        ReadOnlySpan<byte> utf8,
        WorldPackageDefinition package,
        WorldPackageLimits? limits = null)
    {
        var save = WorldSaveCodec.Read(utf8, limits);
        WorldSaveBinding.Validate(save, package);
        return save;
    }

    public WorldSaveDocument ImportSaveFile(
        string path,
        WorldPackageDefinition package,
        WorldPackageLimits? limits = null)
    {
        using var stream = OpenRead(path);
        var save = WorldSaveCodec.Read(stream, limits);
        WorldSaveBinding.Validate(save, package);
        return save;
    }

    public byte[] ExportSave(
        WorldSaveDocument save,
        WorldPackageLimits? limits = null)
    {
        return WorldSaveCodec.Write(
            save ?? throw new ArgumentNullException(nameof(save)),
            limits);
    }

    public void ExportSaveFile(
        string path,
        WorldSaveDocument save,
        WorldPackageLimits? limits = null)
    {
        var bytes = ExportSave(
            save ?? throw new ArgumentNullException(nameof(save)),
            limits);
        WriteAtomic(path, bytes);
    }

    /// <summary>
    /// Low-level planning from loose host definitions. The returned plan is
    /// intentionally unbound and cannot enter the built-in authoritative
    /// executor. Use the event-catalog overload for executable planning.
    /// </summary>
    public async ValueTask<InteractiveWorldResult<WorldEventPlan>>
        PlanTriggerAsync(
            WorldEvolutionTrigger trigger,
            IReadOnlyList<WorldEventDefinition> definitions,
            WorldStateFence currentState,
            int cascadeDepth = 0,
            string? parentInstanceId = null,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (trigger is null)
        {
            throw new ArgumentNullException(nameof(trigger));
        }

        if (currentState is null)
        {
            throw new ArgumentNullException(nameof(currentState));
        }

        var fenceFailure = ValidateCoordinate(
            trigger.WorldId,
            trigger.TimelineId,
            trigger.TimelineEpoch,
            currentState);
        if (fenceFailure is not null)
        {
            return InteractiveWorldResult<WorldEventPlan>.Rejected(
                fenceFailure);
        }

        var plan = await _planner.PlanAsync(
            new WorldEventPlanningRequest(
                trigger,
                definitions,
                cascadeDepth,
                parentInstanceId,
                hostContext),
            cancellationToken).ConfigureAwait(false);
        return InteractiveWorldResult<WorldEventPlan>.Success(plan);
    }

    public async ValueTask<InteractiveWorldResult<WorldEventPlan>>
        PlanTriggerAsync(
            WorldEvolutionTrigger trigger,
            WorldEventCatalogSnapshot catalog,
            WorldStateFence currentState,
            int cascadeDepth = 0,
            string? parentInstanceId = null,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (trigger is null)
        {
            throw new ArgumentNullException(nameof(trigger));
        }

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (currentState is null)
        {
            throw new ArgumentNullException(nameof(currentState));
        }

        var fenceFailure = ValidateCoordinate(
            trigger.WorldId,
            trigger.TimelineId,
            trigger.TimelineEpoch,
            currentState);
        if (fenceFailure is not null)
        {
            return InteractiveWorldResult<WorldEventPlan>.Rejected(
                fenceFailure);
        }

        if (!string.Equals(
                catalog.Digest,
                currentState.CatalogDigest,
                StringComparison.Ordinal)
            || (currentState.EventCatalogDigest is not null
                && !string.Equals(
                    catalog.ComponentDigest,
                    currentState.EventCatalogDigest,
                    StringComparison.Ordinal)))
        {
            return InteractiveWorldResult<WorldEventPlan>.Rejected(
                InteractiveWorldReasonCodes.StaleCatalog);
        }

        var plan = await _planner.PlanAsync(
            new WorldEventPlanningRequest(
                trigger,
                catalog.Definitions,
                cascadeDepth,
                parentInstanceId,
                hostContext),
            cancellationToken).ConfigureAwait(false);
        plan = plan.WithAdmissionFence(currentState);
        return InteractiveWorldResult<WorldEventPlan>.Success(plan);
    }

    public async ValueTask<InteractiveWorldResult<InteractionQueryResult>>
        QueryInteractionsAsync(
            InteractionCatalogSnapshot catalog,
            InteractionQueryRequest request,
            WorldStateFence currentState,
            IInteractionAdmissionEvaluator evaluator,
            CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (currentState is null)
        {
            throw new ArgumentNullException(nameof(currentState));
        }

        var failure = ValidateInteractionFence(
            catalog,
            request.WorldId,
            request.TimelineId,
            request.TimelineEpoch,
            request.SaveRevision,
            request.StateVersion,
            currentState);
        if (failure is not null)
        {
            return InteractiveWorldResult<InteractionQueryResult>.Rejected(
                failure);
        }

        var result = await _interactionQueries.QueryAsync(
            catalog,
            request,
            evaluator,
            cancellationToken).ConfigureAwait(false);
        return InteractiveWorldResult<InteractionQueryResult>.Success(result);
    }

    public async ValueTask<InteractiveWorldResult<WorldInteractionPlan>>
        PlanInteractionAsync(
            InteractionCatalogSnapshot catalog,
            InteractionExecutionRequest request,
            WorldStateFence currentState,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (currentState is null)
        {
            throw new ArgumentNullException(nameof(currentState));
        }

        var failure = ValidateInteractionFence(
            catalog,
            request.WorldId,
            request.TimelineId,
            request.TimelineEpoch,
            request.ExpectedSaveRevision,
            request.ExpectedStateVersion,
            currentState);
        if (failure is not null)
        {
            return InteractiveWorldResult<WorldInteractionPlan>.Rejected(
                failure);
        }

        var compilation = InteractionExecutionCompiler.Compile(
            catalog,
            request);
        if (!compilation.Succeeded || compilation.Execution is null)
        {
            return InteractiveWorldResult<WorldInteractionPlan>.Rejected(
                compilation.ReasonCode,
                compilation.ParameterErrors);
        }

        var plan = await _planner.PlanAsync(
            new WorldEventPlanningRequest(
                compilation.Execution.Trigger,
                new[] { compilation.Execution.RootEventDefinition },
                hostContext: hostContext),
            cancellationToken).ConfigureAwait(false);
        plan = plan.WithAdmissionFence(currentState);
        return InteractiveWorldResult<WorldInteractionPlan>.Success(
            new WorldInteractionPlan(compilation.Execution, plan));
    }

    public ValueTask<InteractiveWorldResult<WorldPlanExecutionResult>>
        ExecutePlanAsync(
            WorldEventPlan plan,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (_executor is null)
        {
            return new ValueTask<
                InteractiveWorldResult<WorldPlanExecutionResult>>(
                InteractiveWorldResult<
                    WorldPlanExecutionResult>.Rejected(
                    InteractiveWorldReasonCodes.ExecutionNotConfigured));
        }

        return ExecuteConfiguredAsync(
            _executor,
            plan,
            hostContext,
            cancellationToken);
    }

    /// <summary>
    /// Executes a plan that is already bound to an exact authoritative
    /// coordinate. This is the preferred path for the built-in durable world
    /// transaction store.
    /// </summary>
    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteAuthoritativePlanAsync(
            WorldAuthoritativeEventPlan artifact,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (artifact is null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        if (_authoritativeExecutor is null)
        {
            return new ValueTask<InteractiveWorldResult<
                WorldAuthoritativePlanExecutionResult>>(
                InteractiveWorldResult<
                    WorldAuthoritativePlanExecutionResult>.Rejected(
                    InteractiveWorldReasonCodes
                        .ExecutionNotConfigured));
        }

        return ExecuteAuthoritativeConfiguredAsync(
            _authoritativeExecutor,
            artifact,
            hostContext,
            cancellationToken);
    }

    /// <summary>
    /// Atomically executes an admitted interaction after checking that its
    /// original save, state, catalog, and timeline still match.
    /// </summary>
    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteAuthoritativeInteractionAsync(
            WorldInteractionPlan interaction,
            WorldAuthoritativeCoordinate expectedCoordinate,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        return ExecuteAuthoritativePlanAsync(
            interaction.Bind(expectedCoordinate),
            hostContext,
            cancellationToken);
    }

    private static async ValueTask<
        InteractiveWorldResult<WorldPlanExecutionResult>>
        ExecuteConfiguredAsync(
            IWorldEventPlanExecutor executor,
            WorldEventPlan plan,
            object? hostContext,
            CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            plan,
            hostContext,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException(
                "The world plan executor returned null.");
        }

        return InteractiveWorldResult<WorldPlanExecutionResult>.Success(
            result);
    }

    private static async ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteAuthoritativeConfiguredAsync(
            IWorldAuthoritativeEventPlanExecutor executor,
            WorldAuthoritativeEventPlan artifact,
            object? hostContext,
            CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                new WorldEventPlanExecutionRequest(
                    artifact,
                    hostContext),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException(
                "The authoritative world plan executor returned null.");
        }

        return InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>.Success(result);
    }

    private static string? ValidateInteractionFence(
        InteractionCatalogSnapshot catalog,
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        string stateVersion,
        WorldStateFence currentState)
    {
        var coordinateFailure = ValidateCoordinate(
            worldId,
            timelineId,
            timelineEpoch,
            currentState);
        if (coordinateFailure is not null)
        {
            return coordinateFailure;
        }

        if (saveRevision != currentState.SaveRevision
            || !string.Equals(
                stateVersion,
                currentState.StateVersion,
                StringComparison.Ordinal))
        {
            return InteractiveWorldReasonCodes.StaleState;
        }

        if (currentState.CatalogDigest is null
            || !string.Equals(
                currentState.CatalogDigest,
                catalog.Digest,
                StringComparison.Ordinal)
            || (currentState.InteractionCatalogDigest is not null
                && !string.Equals(
                    currentState.InteractionCatalogDigest,
                    catalog.ComponentDigest,
                    StringComparison.Ordinal)))
        {
            return InteractiveWorldReasonCodes.StaleCatalog;
        }

        return null;
    }

    private static string? ValidateCoordinate(
        string worldId,
        string timelineId,
        long timelineEpoch,
        WorldStateFence currentState)
    {
        return string.Equals(
                   worldId,
                   currentState.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   timelineId,
                   currentState.TimelineId,
                   StringComparison.Ordinal)
               && timelineEpoch == currentState.TimelineEpoch
            ? null
            : InteractiveWorldReasonCodes.WorldFenceMismatch;
    }

    private static FileStream OpenRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(path));
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: false);
    }

    private static void WriteAtomic(
        string path,
        ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(path));
        }

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "The file path must have a parent directory.",
                nameof(path));
        }

        var temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(target)
                + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81_920,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(target))
            {
                File.Replace(temporary, target, null);
            }
            else
            {
                try
                {
                    File.Move(temporary, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    File.Replace(temporary, target, null);
                }
            }
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Preserve the original export failure.
            }

            throw;
        }
    }
}
