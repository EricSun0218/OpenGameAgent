using GameAgent.World;
using Godot;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

/// <summary>
/// Godot lifecycle and signal bridge for the shared interactive-world
/// session. Games register all business handlers through the portable facade.
/// </summary>
[global::Godot.GlobalClass]
public partial class GodotInteractiveWorldNode : global::Godot.Node
{
    [Signal]
    public delegate void WorldOperationCompletedEventHandler(
        GodotDictionary result);

    [Signal]
    public delegate void WorldOperationFailedEventHandler(
        GodotDictionary error);

    private InteractiveWorldEngineSession? _session;
    private NativeWorldEngineSession? _nativeSession;
    private int _exitStarted;

    [Export(PropertyHint.Range, "1,65536,1")]
    public int BackgroundCapacity { get; set; } = 256;

    [Export(PropertyHint.Range, "1,4096,1")]
    public int MaxResultsPerFrame { get; set; } = 64;

    /// <summary>
    /// Typed completion event. It is raised during <see cref="_Process"/> on
    /// Godot's main thread.
    /// </summary>
    public event Action<WorldBackgroundOperationResult>?
        TypedOperationCompleted;

    public bool IsConfigured =>
        _session is not null || _nativeSession is not null;

    public bool IsNativeConfigured => _nativeSession is not null;

    public InteractiveWorldEngineSession Typed =>
        _session
        ?? throw new InvalidOperationException(
            "Configure the interactive-world node before use.");

    public NativeWorldEngineSession Native =>
        _nativeSession
        ?? throw new InvalidOperationException(
            "Configure the native-world session before use.");

    public void Configure(InteractiveWorldFacade facade)
    {
        if (facade is null)
        {
            throw new ArgumentNullException(nameof(facade));
        }

        if (_session is not null || _nativeSession is not null)
        {
            throw new InvalidOperationException(
                "The interactive-world node is already configured.");
        }

        if (Volatile.Read(ref _exitStarted) != 0)
        {
            throw new ObjectDisposedException(
                nameof(GodotInteractiveWorldNode));
        }

        if (BackgroundCapacity is < 1 or > 65_536)
        {
            throw new InvalidOperationException(
                "BackgroundCapacity must be between 1 and 65536.");
        }

        _session = new InteractiveWorldEngineSession(
            facade,
            BackgroundCapacity);
    }

    /// <summary>
    /// Configures the high-level declarative-world path. Package and save
    /// loads then activate or restore the same runtime generation used by
    /// subsequent operations.
    /// </summary>
    public void ConfigureNative(
        NativeWorldEngineSessionOptions? options = null)
    {
        if (_session is not null || _nativeSession is not null)
        {
            throw new InvalidOperationException(
                "The interactive-world node is already configured.");
        }

        if (Volatile.Read(ref _exitStarted) != 0)
        {
            throw new ObjectDisposedException(
                nameof(GodotInteractiveWorldNode));
        }

        _nativeSession = new NativeWorldEngineSession(options);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_session is null)
        {
            return;
        }

        _session.Pump(
            Math.Max(1, MaxResultsPerFrame),
            PublishResult);
    }

    public override void _ExitTree()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        var session = Interlocked.Exchange(ref _session, null);
        var nativeSession = Interlocked.Exchange(
            ref _nativeSession,
            null);
        if (session is not null)
        {
            _ = session.DisposeAsync();
        }
        if (nativeSession is not null)
        {
            _ = nativeSession.DisposeAsync();
        }
    }

    public ValueTask<NativeWorldEnginePackageLoadResult>
        LoadNativePackageAsync(
            byte[] archive,
            string? timelineId = null,
            long timelineEpoch = 0,
            CancellationToken cancellationToken = default)
    {
        if (archive is null)
        {
            throw new ArgumentNullException(nameof(archive));
        }

        return Native.LoadPackageAsync(
            archive,
            timelineId,
            timelineEpoch,
            capabilities: null,
            cancellationToken);
    }

    public ValueTask<NativeWorldEnginePackageLoadResult>
        LoadNativePackageFileAsync(
            string path,
            string? timelineId = null,
            long timelineEpoch = 0,
            CancellationToken cancellationToken = default)
    {
        return Native.LoadPackageFileAsync(
            GlobalizeEnginePath(path),
            timelineId,
            timelineEpoch,
            capabilities: null,
            cancellationToken);
    }

    public ValueTask<NativeWorldEngineSaveLoadResult>
        LoadNativeSaveAsync(
            byte[] utf8,
            CancellationToken cancellationToken = default)
    {
        if (utf8 is null)
        {
            throw new ArgumentNullException(nameof(utf8));
        }

        return Native.LoadSaveAsync(utf8, cancellationToken);
    }

    public ValueTask<NativeWorldEngineSaveLoadResult>
        LoadNativeSaveFileAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        return Native.LoadSaveFileAsync(
            GlobalizeEnginePath(path),
            cancellationToken);
    }

    public ValueTask<byte[]> CaptureNativeSaveAsync(
        CancellationToken cancellationToken = default)
    {
        return Native.CaptureSaveBytesAsync(cancellationToken);
    }

    public ValueTask CaptureNativeSaveFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return Native.CaptureSaveFileAsync(
            GlobalizeEnginePath(path),
            cancellationToken);
    }

    public ValueTask<NativeWorldEngineShutdownReport>
        ShutdownNativeAsync(
            CancellationToken cancellationToken = default)
    {
        return Native.ShutdownAsync(cancellationToken);
    }

    public WorldPackageDefinition ImportPackage(
        byte[] archive,
        WorldPackageLimits? limits = null)
    {
        if (archive is null)
        {
            throw new ArgumentNullException(nameof(archive));
        }

        return Typed.ImportPackage(archive, limits);
    }

    public WorldPackageDefinition ImportPackageFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        return Typed.ImportPackageFile(
            GlobalizeEnginePath(path),
            limits);
    }

    public byte[] ExportPackage(WorldPackageLimits? limits = null)
    {
        return Typed.ExportPackage(limits);
    }

    public void ExportPackageFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        Typed.ExportPackageFile(
            GlobalizeEnginePath(path),
            limits);
    }

    public WorldSaveDocument ImportSave(
        byte[] utf8,
        WorldPackageLimits? limits = null)
    {
        if (utf8 is null)
        {
            throw new ArgumentNullException(nameof(utf8));
        }

        return Typed.ImportSave(utf8, limits);
    }

    public WorldSaveDocument ImportSaveFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        return Typed.ImportSaveFile(
            GlobalizeEnginePath(path),
            limits);
    }

    public void SetSave(WorldSaveDocument save)
    {
        Typed.SetSave(save);
    }

    public byte[] ExportSave(WorldPackageLimits? limits = null)
    {
        return Typed.ExportSave(limits);
    }

    public void ExportSaveFile(
        string path,
        WorldPackageLimits? limits = null)
    {
        Typed.ExportSaveFile(GlobalizeEnginePath(path), limits);
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
        return Typed.TryScheduleTrigger(
            operationId,
            trigger,
            definitions,
            currentState,
            out rejectionReason,
            cascadeDepth,
            parentInstanceId,
            hostContext,
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
        return Typed.TryScheduleTrigger(
            operationId,
            trigger,
            catalog,
            currentState,
            out rejectionReason,
            cascadeDepth,
            parentInstanceId,
            hostContext,
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
        return Typed.TryScheduleInteractionQuery(
            operationId,
            catalog,
            request,
            currentState,
            evaluator,
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
        return Typed.TryScheduleInteraction(
            operationId,
            catalog,
            request,
            currentState,
            out rejectionReason,
            hostContext,
            cancellationToken);
    }

    public bool TryScheduleExecution(
        string operationId,
        WorldEventPlan plan,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return Typed.TryScheduleExecution(
            operationId,
            plan,
            out rejectionReason,
            hostContext,
            cancellationToken);
    }

    public bool TryScheduleAuthoritativeExecution(
        string operationId,
        WorldAuthoritativeEventPlan artifact,
        out string? rejectionReason,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        return Typed.TryScheduleAuthoritativeExecution(
            operationId,
            artifact,
            out rejectionReason,
            hostContext,
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
        return Typed.TryScheduleAuthoritativeInteraction(
            operationId,
            interaction,
            expectedCoordinate,
            out rejectionReason,
            hostContext,
            cancellationToken);
    }

    public bool TryCancel(string operationId)
    {
        return Typed.TryCancel(operationId);
    }

    /// <summary>
    /// Controlled quit path. Call and await this from the main thread before
    /// releasing game-owned handlers or stores. Returned completions are
    /// published before this method completes.
    /// </summary>
    public async ValueTask<IReadOnlyList<WorldBackgroundOperationResult>>
        ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var results = await Typed.ShutdownAsync(cancellationToken)
            .ConfigureAwait(true);
        foreach (var result in results)
        {
            PublishResult(result);
        }

        return results;
    }

    // Variant-compatible artifact surface for GDScript.
    public GodotDictionary import_world_package(byte[] archive)
    {
        return PackageStatus(ImportPackage(archive));
    }

    public GodotDictionary import_world_package_file(string path)
    {
        return PackageStatus(ImportPackageFile(path));
    }

    public byte[] export_world_package()
    {
        return ExportPackage();
    }

    public void export_world_package_file(string path)
    {
        ExportPackageFile(path);
    }

    public GodotDictionary import_world_save(byte[] utf8)
    {
        return SaveStatus(ImportSave(utf8));
    }

    public GodotDictionary import_world_save_file(string path)
    {
        return SaveStatus(ImportSaveFile(path));
    }

    public byte[] export_world_save()
    {
        return ExportSave();
    }

    public void export_world_save_file(string path)
    {
        ExportSaveFile(path);
    }

    public bool cancel_world_operation(string operationId)
    {
        return TryCancel(operationId);
    }

    public GodotDictionary get_world_status()
    {
        if (_nativeSession is not null)
        {
            var native = _nativeSession.Status;
            return new GodotDictionary
            {
                ["configured"] = true,
                ["mode"] = "native",
                ["active_generation"] = native.Generation,
                ["active_package_id"] =
                    native.ActivePackageId ?? string.Empty,
                ["active_package_digest"] =
                    native.ActivePackageDigest ?? string.Empty,
                ["active_world_id"] =
                    native.ActiveWorldId ?? string.Empty,
                ["accepting_operations"] =
                    native.IsAcceptingOperations,
                ["outstanding_operations"] =
                    native.ActiveOperations
            };
        }

        var package = _session?.CurrentPackage;
        var save = _session?.CurrentSave;
        return new GodotDictionary
        {
            ["configured"] = IsConfigured,
            ["mode"] = _session is null ? "unconfigured" : "custom",
            ["artifact_package_id"] =
                package?.PackageId ?? string.Empty,
            ["artifact_package_digest"] =
                package?.PackageDigest ?? string.Empty,
            ["artifact_save_world_id"] =
                save?.WorldId ?? string.Empty,
            ["artifact_save_timeline_id"] =
                save?.TimelineId ?? string.Empty,
            ["artifact_save_revision"] =
                save?.SaveRevision ?? -1,
            ["artifact_state_version"] =
                save?.StateVersion ?? string.Empty,
            ["outstanding_operations"] =
                _session?.OutstandingOperationCount ?? 0
        };
    }

    private void PublishResult(WorldBackgroundOperationResult result)
    {
        var typed = TypedOperationCompleted;
        if (typed is not null)
        {
            foreach (Action<WorldBackgroundOperationResult> subscriber
                     in typed.GetInvocationList())
            {
                try
                {
                    subscriber(result);
                }
                catch (Exception exception)
                {
                    global::Godot.GD.PushError(exception.ToString());
                }
            }
        }

        var summary = ToDictionary(result);
        try
        {
            EmitSignal(
                result.Succeeded
                    ? SignalName.WorldOperationCompleted
                    : SignalName.WorldOperationFailed,
                summary);
        }
        catch (Exception exception)
        {
            global::Godot.GD.PushError(exception.ToString());
        }
    }

    private static GodotDictionary ToDictionary(
        WorldBackgroundOperationResult result)
    {
        var summary = new GodotDictionary
        {
            ["operation_id"] = result.OperationId,
            ["kind"] = result.Kind.ToString(),
            ["succeeded"] = result.Succeeded,
            ["canceled"] = result.IsCanceled,
            ["reason_code"] = string.Empty,
            ["message"] = result.Exception?.Message ?? string.Empty
        };
        switch (result.Value)
        {
            case InteractiveWorldResult<WorldEventPlan> plan:
                summary["admitted"] = plan.Succeeded;
                summary["reason_code"] = plan.ReasonCode;
                summary["event_count"] =
                    plan.Value?.Instances.Count ?? 0;
                break;
            case InteractiveWorldResult<InteractionQueryResult> query:
                summary["admitted"] = query.Succeeded;
                summary["reason_code"] = query.ReasonCode;
                summary["interaction_count"] =
                    query.Value?.Items.Count ?? 0;
                break;
            case InteractiveWorldResult<WorldInteractionPlan> interaction:
                summary["admitted"] = interaction.Succeeded;
                summary["reason_code"] = interaction.ReasonCode;
                summary["event_count"] =
                    interaction.Value?.Plan.Instances.Count ?? 0;
                break;
            case InteractiveWorldResult<WorldPlanExecutionResult> execution:
                summary["admitted"] = execution.Succeeded;
                summary["reason_code"] = execution.ReasonCode;
                summary["outcome_code"] =
                    execution.Value?.OutcomeCode ?? string.Empty;
                break;
            case InteractiveWorldResult<
                    WorldAuthoritativePlanExecutionResult> authoritative:
                summary["admitted"] = authoritative.Succeeded;
                summary["reason_code"] = authoritative.ReasonCode;
                summary["outcome_code"] =
                    authoritative.Value?.ReasonCode ?? string.Empty;
                summary["event_count"] =
                    authoritative.Value?.Executions.Count ?? 0;
                summary["save_revision"] =
                    authoritative.Value?.Coordinate.SaveRevision ?? -1;
                summary["state_version"] =
                    authoritative.Value?.Coordinate.StateVersion ?? -1;
                break;
        }

        return summary;
    }

    private static GodotDictionary PackageStatus(
        WorldPackageDefinition package)
    {
        return new GodotDictionary
        {
            ["contract"] = package.Contract,
            ["package_id"] = package.PackageId,
            ["content_version"] = package.ContentVersion,
            ["package_digest"] = package.PackageDigest,
            ["file_count"] = package.Files.Count
        };
    }

    private static GodotDictionary SaveStatus(WorldSaveDocument save)
    {
        return new GodotDictionary
        {
            ["contract"] = save.Contract,
            ["package_id"] = save.PackageId,
            ["package_digest"] = save.PackageDigest,
            ["world_id"] = save.WorldId,
            ["timeline_id"] = save.TimelineId,
            ["save_revision"] = save.SaveRevision,
            ["state_version"] = save.StateVersion,
            ["save_digest"] = save.SaveDigest
        };
    }

    private static string GlobalizeEnginePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(path));
        }

        return path.StartsWith("res://", StringComparison.Ordinal)
               || path.StartsWith("user://", StringComparison.Ordinal)
            ? global::Godot.ProjectSettings.GlobalizePath(path)
            : path;
    }
}
