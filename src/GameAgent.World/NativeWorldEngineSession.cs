using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.World;

/// <summary>
/// Lifecycle state for one engine-owned declarative world session.
/// </summary>
public enum NativeWorldEngineSessionState
{
    Empty = 0,
    Active = 1,
    Stopping = 2,
    Stopped = 3,
    Disposed = 4
}

/// <summary>
/// Bounded configuration for a high-level engine session. The session
/// activates declarative packages and keeps package, runtime, and save
/// restoration on one atomic generation boundary.
/// </summary>
public sealed class NativeWorldEngineSessionOptions
{
    public NativeWorldEngineSessionOptions(
        int maxConcurrentOperations = 256,
        NativeWorldPackageCompilerOptions? compiler = null,
        WorldPackageLimits? packages = null,
        NativeWorldRuntimeOptions? runtime = null,
        NativeWorldSaveBridgeOptions? saves = null)
    {
        if (maxConcurrentOperations is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentOperations));
        }

        MaxConcurrentOperations = maxConcurrentOperations;
        Compiler = compiler ?? new NativeWorldPackageCompilerOptions();
        Packages = packages ?? new WorldPackageLimits();
        Runtime = runtime ?? new NativeWorldRuntimeOptions();
        Saves = saves ?? new NativeWorldSaveBridgeOptions();
    }

    public int MaxConcurrentOperations { get; }

    public NativeWorldPackageCompilerOptions Compiler { get; }

    public WorldPackageLimits Packages { get; }

    public NativeWorldRuntimeOptions Runtime { get; }

    public NativeWorldSaveBridgeOptions Saves { get; }
}

/// <summary>
/// Immutable status that never presents a parsed artifact as an activated
/// runtime. Read <see cref="NativeWorldEngineSession.ReadSnapshotAsync"/> for
/// the current authoritative save/state coordinate.
/// </summary>
public sealed class NativeWorldEngineSessionStatus
{
    internal NativeWorldEngineSessionStatus(
        NativeWorldEngineSessionState state,
        long generation,
        string? packageId,
        string? packageDigest,
        string? worldId,
        bool acceptingOperations,
        int activeOperations,
        IReadOnlyList<string> activeAuthoritativeOperationIds)
    {
        State = state;
        Generation = generation;
        ActivePackageId = packageId;
        ActivePackageDigest = packageDigest;
        ActiveWorldId = worldId;
        IsAcceptingOperations = acceptingOperations;
        ActiveOperations = activeOperations;
        ActiveAuthoritativeOperationIds =
            new ReadOnlyCollection<string>(
                activeAuthoritativeOperationIds.ToArray());
    }

    public NativeWorldEngineSessionState State { get; }

    public bool IsActive =>
        State == NativeWorldEngineSessionState.Active;

    public long Generation { get; }

    public string? ActivePackageId { get; }

    public string? ActivePackageDigest { get; }

    public string? ActiveWorldId { get; }

    public bool IsAcceptingOperations { get; }

    public int ActiveOperations { get; }

    public IReadOnlyList<string> ActiveAuthoritativeOperationIds { get; }
}

/// <summary>
/// Result of validating and atomically activating a native package.
/// Compilation failure leaves the previous generation untouched.
/// </summary>
public sealed class NativeWorldEnginePackageLoadResult
{
    internal NativeWorldEnginePackageLoadResult(
        bool activated,
        long generation,
        WorldPackageDefinition definition,
        ActivatedWorldPackage? package,
        IReadOnlyList<WorldSemanticDiagnostic> diagnostics,
        WorldAuthoritativeCoordinate? coordinate)
    {
        Activated = activated;
        Generation = generation;
        Definition = definition;
        Package = package;
        Diagnostics = new ReadOnlyCollection<WorldSemanticDiagnostic>(
            diagnostics.ToArray());
        Coordinate = coordinate;
    }

    public bool Activated { get; }

    public long Generation { get; }

    public WorldPackageDefinition Definition { get; }

    public ActivatedWorldPackage? Package { get; }

    public IReadOnlyList<WorldSemanticDiagnostic> Diagnostics { get; }

    public WorldAuthoritativeCoordinate? Coordinate { get; }
}

/// <summary>
/// Result of fully admitting a save and atomically replacing the live
/// runtime. The prior generation remains active if validation fails.
/// </summary>
public sealed class NativeWorldEngineSaveLoadResult
{
    internal NativeWorldEngineSaveLoadResult(
        long generation,
        WorldSaveDocument save,
        WorldAuthoritativeCoordinate coordinate)
    {
        Generation = generation;
        Save = save;
        Coordinate = coordinate;
    }

    public long Generation { get; }

    public WorldSaveDocument Save { get; }

    public WorldAuthoritativeCoordinate Coordinate { get; }
}

/// <summary>
/// Interaction plan fenced to the exact engine-session generation that
/// admitted it.
/// </summary>
public sealed class NativeWorldEnginePlannedInteraction
{
    internal NativeWorldEnginePlannedInteraction(
        long generation,
        NativeWorldPlannedInteraction interaction)
    {
        Generation = generation;
        Interaction = interaction;
    }

    public long Generation { get; }

    public WorldInteractionPlan Plan => Interaction.Plan;

    public WorldAuthoritativeCoordinate ExpectedCoordinate =>
        Interaction.ExpectedCoordinate;

    internal NativeWorldPlannedInteraction Interaction { get; }
}

public sealed class NativeWorldEngineShutdownReport
{
    internal NativeWorldEngineShutdownReport(
        long generation,
        int settledOperationCount)
    {
        Generation = generation;
        SettledOperationCount = settledOperationCount;
    }

    public long Generation { get; }

    public int SettledOperationCount { get; }
}

/// <summary>
/// Controlled shutdown timed out or was cancelled. Authoritative operation
/// IDs remain available so the game can keep the owning stores alive and
/// reconcile them before teardown.
/// </summary>
public sealed class NativeWorldEngineShutdownIncompleteException
    : OperationCanceledException
{
    internal NativeWorldEngineShutdownIncompleteException(
        CancellationToken cancellationToken,
        IReadOnlyList<string> outstandingOperationIds,
        IReadOnlyList<string> authoritativeOperationIds)
        : base(
            "Native-world shutdown did not settle every admitted operation.",
            cancellationToken)
    {
        OutstandingOperationIds = new ReadOnlyCollection<string>(
            outstandingOperationIds.ToArray());
        AuthoritativeOperationIds = new ReadOnlyCollection<string>(
            authoritativeOperationIds.ToArray());
    }

    public IReadOnlyList<string> OutstandingOperationIds { get; }

    public IReadOnlyList<string> AuthoritativeOperationIds { get; }
}

/// <summary>
/// Immutable receipt captured from one active native-world session
/// generation. The authoritative store remains private to the session.
/// </summary>
public sealed class NativeWorldEngineReceiptRead
{
    internal NativeWorldEngineReceiptRead(
        long generation,
        WorldCommandReceipt receipt)
    {
        Generation = generation;
        Receipt = receipt
                  ?? throw new ArgumentNullException(nameof(receipt));
    }

    public long Generation { get; }

    public WorldCommandReceipt Receipt { get; }
}

/// <summary>
/// Exclusive, exact-coordinate native-world fence used by settlement
/// authority composition. The session admits no operation or generation
/// replacement until this lease is disposed.
/// </summary>
public sealed class NativeWorldEngineSettlementLease : IAsyncDisposable
{
    private NativeWorldEngineSession? _owner;
    private readonly NativeWorldRuntime _runtime;
    private readonly long _leaseId;

    internal NativeWorldEngineSettlementLease(
        NativeWorldEngineSession owner,
        NativeWorldRuntime runtime,
        long leaseId,
        long generation,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        _owner = owner;
        _runtime = runtime;
        _leaseId = leaseId;
        Generation = generation;
        Snapshot = snapshot
                   ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public long Generation { get; }

    public WorldAuthoritativeStateSnapshot Snapshot { get; }

    public ValueTask<NativeWorldEngineReceiptRead?> ReadReceiptAsync(
        string receiptId,
        CancellationToken cancellationToken = default)
    {
        var owner = Volatile.Read(ref _owner)
                    ?? throw new ObjectDisposedException(
                        nameof(NativeWorldEngineSettlementLease));
        return owner.ReadSettlementReceiptAsync(
            _runtime,
            _leaseId,
            Generation,
            receiptId,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReleaseSettlementLease(_leaseId);
        return default;
    }
}

internal delegate NativeWorldEngineSettlementLease
    NativeWorldEngineSettlementLeaseFactory(
        NativeWorldEngineSession owner,
        NativeWorldRuntime runtime,
        long leaseId,
        long generation,
        WorldAuthoritativeStateSnapshot snapshot);

/// <summary>
/// High-level engine composition root for one activated declarative package.
/// Package and save replacement validate a complete candidate first, pause
/// new admissions, drain the old generation, and then publish one atomic
/// generation swap.
/// </summary>
public sealed class NativeWorldEngineSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly SemaphoreSlim _settlementAdmission = new(1, 1);
    private readonly AsyncLocal<int> _operationContextDepth = new();
    private readonly AsyncLocal<int> _settlementPolicyContextDepth = new();
    private readonly NativeWorldEngineSessionOptions _options;
    private readonly INativeWorldSaveBridge _saveBridge;
    private readonly NativeWorldEngineSettlementLeaseFactory
        _settlementLeaseFactory;
    private readonly WorldCancellationDispatcher.CancellationOwner
        _lifetimeCancellation;
    private readonly Dictionary<string, ActiveOperation> _active =
        new(StringComparer.Ordinal);
    private NativeWorldEngineSessionState _state =
        NativeWorldEngineSessionState.Empty;
    private TaskCompletionSource<bool>? _activeDrained;
    private WorldPackageDefinition? _definition;
    private ActivatedWorldPackage? _package;
    private NativeWorldRuntime? _runtime;
    private bool _admissionsPaused;
    private long _generation;
    private long _localOperationSequence;
    private long _settlementLeaseSequence;
    private long _activeSettlementLeaseId;
    private int _shutdownInitialOperationCount;
    private int _lifetimeClosed;

    public NativeWorldEngineSession(
        NativeWorldEngineSessionOptions? options = null,
        INativeWorldSaveBridge? saveBridge = null)
        : this(
            options,
            saveBridge,
            CreateSettlementLease)
    {
    }

    internal NativeWorldEngineSession(
        NativeWorldEngineSessionOptions? options,
        INativeWorldSaveBridge? saveBridge,
        NativeWorldEngineSettlementLeaseFactory settlementLeaseFactory)
    {
        _options = options ?? new NativeWorldEngineSessionOptions();
        _saveBridge = saveBridge ?? new NativeWorldSaveBridge();
        _settlementLeaseFactory = settlementLeaseFactory
                                  ?? throw new ArgumentNullException(
                                      nameof(settlementLeaseFactory));
        if (!WorldCancellationDispatcher.TryCreateOwner(
                out var cancellation)
            || cancellation is null)
        {
            throw new InvalidOperationException(
                "Native-world cancellation capacity is exhausted.");
        }

        _lifetimeCancellation = cancellation;
    }

    public NativeWorldEngineSessionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return StatusLocked();
            }
        }
    }

    public async ValueTask<NativeWorldEnginePackageLoadResult>
        LoadPackageAsync(
            ReadOnlyMemory<byte> archive,
            string? timelineId = null,
            long timelineEpoch = 0,
            IWorldExtensionCapabilityResolver? capabilities = null,
            CancellationToken cancellationToken = default)
    {
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }
        if (archive.Length > _options.Packages.MaxCompressedBytes)
        {
            throw new WorldDataContractException(
                WorldDataReasonCodes.CompressionLimitExceeded,
                "Native package exceeds its compressed byte limit.");
        }

        EnsureOperationCallbackCanTransition();
        await _transition.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureCanTransition();
            var candidate = await RunBackgroundAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(
                            archive.ToArray(),
                            writable: false);
                        var definition = WorldPackageArchive.Read(
                            stream,
                            _options.Packages);
                        var compilation =
                            new NativeWorldPackageCompiler(
                                    _options.Compiler,
                                    _options.Packages)
                                .Compile(definition, capabilities);
                        return new ValueTask<PackageCandidate>(
                            new PackageCandidate(
                                definition,
                                compilation,
                                compilation.Package is null
                                    ? null
                                    : NativeWorldRuntime.CreateInMemory(
                                        compilation.Package,
                                        timelineId,
                                        timelineEpoch,
                                        _options.Runtime)));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!candidate.Compilation.Succeeded
                || candidate.Runtime is null
                || candidate.Compilation.Package is null)
            {
                return new NativeWorldEnginePackageLoadResult(
                    activated: false,
                    Status.Generation,
                    candidate.Definition,
                    package: null,
                    candidate.Compilation.Diagnostics,
                    coordinate: null);
            }

            var snapshot = await candidate.Runtime.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A newly activated native world has no initial state.");
            var generation = await SwapAsync(
                    candidate.Definition,
                    candidate.Compilation.Package,
                    candidate.Runtime,
                    cancellationToken)
                .ConfigureAwait(false);
            return new NativeWorldEnginePackageLoadResult(
                activated: true,
                generation,
                candidate.Definition,
                candidate.Compilation.Package,
                candidate.Compilation.Diagnostics,
                snapshot.Coordinate);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async ValueTask<NativeWorldEnginePackageLoadResult>
        LoadPackageFileAsync(
            string path,
            string? timelineId = null,
            long timelineEpoch = 0,
            IWorldExtensionCapabilityResolver? capabilities = null,
            CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequiredPath(path);
        EnsureOperationCallbackCanTransition();
        await _transition.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureCanTransition();
            var candidate = await RunBackgroundAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new FileStream(
                            normalizedPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 65_536,
                            FileOptions.SequentialScan);
                        var definition = WorldPackageArchive.Read(
                            stream,
                            _options.Packages);
                        var compilation =
                            new NativeWorldPackageCompiler(
                                    _options.Compiler,
                                    _options.Packages)
                                .Compile(definition, capabilities);
                        return new ValueTask<PackageCandidate>(
                            new PackageCandidate(
                                definition,
                                compilation,
                                compilation.Package is null
                                    ? null
                                    : NativeWorldRuntime.CreateInMemory(
                                        compilation.Package,
                                        timelineId,
                                        timelineEpoch,
                                        _options.Runtime)));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!candidate.Compilation.Succeeded
                || candidate.Runtime is null
                || candidate.Compilation.Package is null)
            {
                return new NativeWorldEnginePackageLoadResult(
                    activated: false,
                    Status.Generation,
                    candidate.Definition,
                    package: null,
                    candidate.Compilation.Diagnostics,
                    coordinate: null);
            }

            var snapshot = await candidate.Runtime.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A newly activated native world has no initial state.");
            var generation = await SwapAsync(
                    candidate.Definition,
                    candidate.Compilation.Package,
                    candidate.Runtime,
                    cancellationToken)
                .ConfigureAwait(false);
            return new NativeWorldEnginePackageLoadResult(
                activated: true,
                generation,
                candidate.Definition,
                candidate.Compilation.Package,
                candidate.Compilation.Diagnostics,
                snapshot.Coordinate);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async ValueTask<NativeWorldEngineSaveLoadResult> LoadSaveAsync(
        ReadOnlyMemory<byte> utf8,
        CancellationToken cancellationToken = default)
    {
        EnsureOperationCallbackCanTransition();
        await _transition.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureCanTransition();
            var package = RequirePackage();
            var candidate = await RunBackgroundAsync(
                    async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(
                            utf8.ToArray(),
                            writable: false);
                        var save = WorldSaveCodec.Read(
                            stream,
                            _options.Packages);
                        var runtime = await _saveBridge
                            .RestoreInMemoryAsync(
                                package,
                                save,
                                _options.Runtime,
                                _options.Saves,
                                token)
                            .ConfigureAwait(false);
                        return new SaveCandidate(save, runtime);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await candidate.Runtime.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A restored native world has no authoritative state.");
            var generation = await SwapAsync(
                    RequireDefinition(),
                    package,
                    candidate.Runtime,
                    cancellationToken)
                .ConfigureAwait(false);
            return new NativeWorldEngineSaveLoadResult(
                generation,
                candidate.Save,
                snapshot.Coordinate);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async ValueTask<NativeWorldEngineSaveLoadResult>
        LoadSaveFileAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequiredPath(path);
        EnsureOperationCallbackCanTransition();
        await _transition.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureCanTransition();
            var package = RequirePackage();
            var candidate = await RunBackgroundAsync(
                    async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new FileStream(
                            normalizedPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 65_536,
                            FileOptions.SequentialScan);
                        var save = WorldSaveCodec.Read(
                            stream,
                            _options.Packages);
                        var runtime = await _saveBridge
                            .RestoreInMemoryAsync(
                                package,
                                save,
                                _options.Runtime,
                                _options.Saves,
                                token)
                            .ConfigureAwait(false);
                        return new SaveCandidate(save, runtime);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await candidate.Runtime.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A restored native world has no authoritative state.");
            var generation = await SwapAsync(
                    RequireDefinition(),
                    package,
                    candidate.Runtime,
                    cancellationToken)
                .ConfigureAwait(false);
            return new NativeWorldEngineSaveLoadResult(
                generation,
                candidate.Save,
                snapshot.Coordinate);
        }
        finally
        {
            _transition.Release();
        }
    }

    public ValueTask<WorldSaveDocument> CaptureSaveAsync(
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            LocalOperationId("capture-save"),
            authoritative: false,
            (runtime, token) => _saveBridge.CaptureAsync(
                runtime,
                _options.Saves,
                token),
            cancellationToken);
    }

    public async ValueTask<byte[]> CaptureSaveBytesAsync(
        CancellationToken cancellationToken = default)
    {
        var save = await CaptureSaveAsync(cancellationToken)
            .ConfigureAwait(false);
        return await RunBackgroundAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ValueTask<byte[]>(
                        WorldSaveCodec.Write(
                            save,
                            _options.Packages));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CaptureSaveFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = RequiredPath(path);
        var bytes = await CaptureSaveBytesAsync(cancellationToken)
            .ConfigureAwait(false);
        await RunBackgroundAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    WriteAtomic(normalizedPath, bytes);
                    return new ValueTask<bool>(true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ExportActivePackageAsync(
        CancellationToken cancellationToken = default)
    {
        using var lease = EnterOperation(
            LocalOperationId("export-package"),
            authoritative: false,
            cancellationToken);
        WorldPackageDefinition definition;
        lock (_gate)
        {
            if (_generation != lease.Generation || _definition is null)
            {
                throw new InvalidOperationException(
                    "The active package changed before export.");
            }

            definition = _definition;
        }

        return await RunBackgroundAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream();
                        WorldPackageArchive.Write(
                            stream,
                            definition,
                            _options.Packages);
                        return new ValueTask<byte[]>(stream.ToArray());
                    },
                    lease.CancellationToken)
                .ConfigureAwait(false);
    }

    public ValueTask<WorldAuthoritativeStateSnapshot?> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            LocalOperationId("read-snapshot"),
            authoritative: false,
            (runtime, token) => runtime.ReadSnapshotAsync(token),
            cancellationToken);
    }

    /// <summary>
    /// Reads one immutable receipt from the active generation without
    /// exposing the authoritative transaction store.
    /// </summary>
    public async ValueTask<NativeWorldEngineReceiptRead?> ReadReceiptAsync(
        string receiptId,
        CancellationToken cancellationToken = default)
    {
        var normalizedReceiptId = WorldValidation.Required(
            receiptId,
            nameof(receiptId),
            128);
        using var lease = EnterOperation(
            LocalOperationId("read-receipt"),
            authoritative: false,
            cancellationToken);
        var receipt = await lease.Runtime.ReadReceiptAsync(
                normalizedReceiptId,
                _options.Saves.MaxTransactionRecords,
                lease.CancellationToken)
            .ConfigureAwait(false);
        return receipt is null
            ? null
            : new NativeWorldEngineReceiptRead(
                lease.Generation,
                receipt);
    }

    /// <summary>
    /// Pauses admission, drains admitted work, and captures an exclusive
    /// lease only when the current authoritative snapshot exactly matches
    /// the requested settlement binding. Transition locks are not held for
    /// the returned lease lifetime.
    /// </summary>
    public async ValueTask<NativeWorldEngineSettlementLease?>
        AcquireSettlementLeaseAsync(
            WorldPresentationBinding binding,
            CancellationToken cancellationToken = default)
    {
        if (binding is null)
        {
            throw new ArgumentNullException(nameof(binding));
        }
        if (_operationContextDepth.Value != 0)
        {
            throw new InvalidOperationException(
                "A native-world operation callback cannot acquire a "
                + "settlement lease from its own session.");
        }
        if (_settlementPolicyContextDepth.Value != 0)
        {
            throw new InvalidOperationException(
                "A native-world settlement policy callback cannot "
                + "acquire another settlement lease from its own "
                + "session.");
        }

        await _settlementAdmission.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var releaseAdmission = true;
        var registered = false;
        var leaseId = 0L;
        NativeWorldRuntime? runtime = null;
        var generation = 0L;
        Task? drain = null;
        try
        {
            await _transition.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    EnsureCanTransitionLocked();
                    if (_state != NativeWorldEngineSessionState.Active
                        || _runtime is null)
                    {
                        throw new InvalidOperationException(
                            "The native-world session has no active "
                            + "authoritative generation.");
                    }

                    leaseId = checked(_settlementLeaseSequence + 1);
                    _settlementLeaseSequence = leaseId;
                    _activeSettlementLeaseId = leaseId;
                    _admissionsPaused = true;
                    registered = true;
                    runtime = _runtime;
                    generation = _generation;
                    drain = ActiveDrainTaskLocked();
                }
            }
            finally
            {
                _transition.Release();
            }

            await WaitWithCancellationAsync(
                    drain!,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await runtime!.ReadSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var exact = false;
            lock (_gate)
            {
                exact = snapshot is not null
                        && _state
                        == NativeWorldEngineSessionState.Active
                        && _activeSettlementLeaseId == leaseId
                        && ReferenceEquals(_runtime, runtime)
                        && _generation == generation
                        && _active.Count == 0
                        && BindingMatches(binding, snapshot);
            }

            if (!exact)
            {
                ReleaseSettlementLease(leaseId);
                registered = false;
                releaseAdmission = false;
                return null;
            }

            var lease = _settlementLeaseFactory(
                this,
                runtime,
                leaseId,
                generation,
                snapshot!);
            if (lease is null)
            {
                throw new InvalidOperationException(
                    "The native-world settlement lease factory returned "
                    + "no lease.");
            }

            registered = false;
            releaseAdmission = false;
            return lease;
        }
        catch
        {
            if (registered)
            {
                ReleaseSettlementLease(leaseId);
                releaseAdmission = false;
            }

            throw;
        }
        finally
        {
            if (releaseAdmission)
            {
                _settlementAdmission.Release();
            }
        }
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

        return RunAsync(
            LocalOperationId("query-interactions"),
            authoritative: false,
            (runtime, token) =>
                runtime.QueryInteractionsAsync(request, token),
            cancellationToken);
    }

    public async ValueTask<InteractiveWorldResult<
            NativeWorldEnginePlannedInteraction>>
        PlanInteractionAsync(
            InteractionExecutionRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using var lease = EnterOperation(
            SessionOperationId("plan", request.IdempotencyKey),
            authoritative: false,
            cancellationToken);
        var result = await lease.Runtime.PlanInteractionAsync(
                request,
                lease.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return InteractiveWorldResult<
                NativeWorldEnginePlannedInteraction>.Rejected(
                result.ReasonCode,
                result.ParameterErrors);
        }

        return InteractiveWorldResult<
            NativeWorldEnginePlannedInteraction>.Success(
            new NativeWorldEnginePlannedInteraction(
                lease.Generation,
                result.Value));
    }

    public ValueTask<InteractiveWorldResult<
            WorldAuthoritativePlanExecutionResult>>
        ExecuteInteractionAsync(
            NativeWorldEnginePlannedInteraction interaction,
            object? hostContext = null,
            CancellationToken cancellationToken = default)
    {
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        return RunAsync(
            SessionOperationId(
                "execute",
                interaction.Interaction.Plan.Compilation.Trigger
                    .IdempotencyKey),
            authoritative: true,
            (runtime, token) =>
            {
                EnsureGeneration(
                    interaction.Generation,
                    nameof(interaction));
                return runtime.ExecuteInteractionAsync(
                    interaction.Interaction,
                    hostContext,
                    token);
            },
            cancellationToken);
    }

    public ValueTask<WorldAdvanceClockResult> AdvanceClockAsync(
        WorldAdvanceClockCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        return RunAsync(
            SessionOperationId("clock", command.OperationId),
            authoritative: true,
            (runtime, token) => runtime.AdvanceClockAsync(command, token),
            cancellationToken);
    }

    public ValueTask<WorldScheduleMutationResult> ExecuteScheduleAsync(
        WorldScheduleCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        return RunAsync(
            SessionOperationId("schedule", command.OperationId),
            authoritative: true,
            (runtime, token) => runtime.ExecuteScheduleAsync(command, token),
            cancellationToken);
    }

    /// <summary>
    /// Runs advanced composition against the active runtime while holding a
    /// generation lease. Package/save replacement and controlled shutdown
    /// cannot pass this operation until it settles.
    /// </summary>
    public async ValueTask<T> RunAsync<T>(
        string operationId,
        bool authoritative,
        Func<NativeWorldRuntime, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        using var lease = EnterOperation(
            operationId,
            authoritative,
            cancellationToken);
        var priorDepth = _operationContextDepth.Value;
        _operationContextDepth.Value = checked(priorDepth + 1);
        try
        {
            return await operation(
                    lease.Runtime,
                    lease.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationContextDepth.Value = priorDepth;
        }
    }

    /// <summary>
    /// Stops admission, requests cancellation off-thread, and waits until
    /// every admitted operation has settled. Cancellation of this wait throws
    /// an exception carrying operation IDs that still require ownership and
    /// possible reconciliation.
    /// </summary>
    public async ValueTask<NativeWorldEngineShutdownReport> ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOperationCallbackCanTransition();
        await _transition.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            Task drain;
            long generation;
            lock (_gate)
            {
                if (_state == NativeWorldEngineSessionState.Disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(NativeWorldEngineSession));
                }

                if (_activeSettlementLeaseId != 0)
                {
                    throw new InvalidOperationException(
                        "A native-world settlement lease is active.");
                }

                if (_state == NativeWorldEngineSessionState.Stopped)
                {
                    return new NativeWorldEngineShutdownReport(
                        _generation,
                        _shutdownInitialOperationCount);
                }

                if (_state is NativeWorldEngineSessionState.Empty
                    or NativeWorldEngineSessionState.Active)
                {
                    _state = NativeWorldEngineSessionState.Stopping;
                    _admissionsPaused = true;
                    _shutdownInitialOperationCount = _active.Count;
                    _ = _lifetimeCancellation.Request();
                }

                generation = _generation;
                drain = ActiveDrainTaskLocked();
            }

            try
            {
                await WaitWithCancellationAsync(
                        drain,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                IReadOnlyList<string> outstanding;
                IReadOnlyList<string> authoritative;
                lock (_gate)
                {
                    outstanding = _active.Keys
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    authoritative = _active
                        .Where(pair => pair.Value.Authoritative)
                        .Select(pair => pair.Key)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                }

                throw new NativeWorldEngineShutdownIncompleteException(
                    cancellationToken,
                    outstanding,
                    authoritative);
            }

            lock (_gate)
            {
                if (_state != NativeWorldEngineSessionState.Disposed)
                {
                    _state = NativeWorldEngineSessionState.Stopped;
                }

                CloseLifetime();
                return new NativeWorldEngineShutdownReport(
                    generation,
                    _shutdownInitialOperationCount);
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Emergency detach used by engine destruction callbacks. Controlled
    /// application quit should call and await <see cref="ShutdownAsync"/>
    /// first.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        var releaseSettlementAdmission = false;
        lock (_gate)
        {
            if (_state == NativeWorldEngineSessionState.Disposed)
            {
                return default;
            }

            _state = NativeWorldEngineSessionState.Disposed;
            _admissionsPaused = true;
            _ = _lifetimeCancellation.Request();
            CloseLifetime();
            _definition = null;
            _package = null;
            _runtime = null;
            if (_activeSettlementLeaseId != 0)
            {
                _activeSettlementLeaseId = 0;
                releaseSettlementAdmission = true;
            }
        }

        if (releaseSettlementAdmission)
        {
            _settlementAdmission.Release();
        }

        return default;
    }

    internal async ValueTask<NativeWorldEngineReceiptRead?>
        ReadSettlementReceiptAsync(
            NativeWorldRuntime runtime,
            long leaseId,
            long generation,
            string receiptId,
            CancellationToken cancellationToken)
    {
        var normalizedReceiptId = WorldValidation.Required(
            receiptId,
            nameof(receiptId),
            128);
        lock (_gate)
        {
            EnsureSettlementLeaseLocked(runtime, leaseId, generation);
        }

        var receipt = await runtime.ReadReceiptAsync(
                normalizedReceiptId,
                _options.Saves.MaxTransactionRecords,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            EnsureSettlementLeaseLocked(runtime, leaseId, generation);
        }

        return receipt is null
            ? null
            : new NativeWorldEngineReceiptRead(generation, receipt);
    }

    internal void ReleaseSettlementLease(long leaseId)
    {
        var release = false;
        lock (_gate)
        {
            if (_activeSettlementLeaseId == leaseId)
            {
                _activeSettlementLeaseId = 0;
                if (_state == NativeWorldEngineSessionState.Active)
                {
                    _admissionsPaused = false;
                }

                release = true;
            }
        }

        if (release)
        {
            _settlementAdmission.Release();
        }
    }

    internal async ValueTask<T> InvokeSettlementPolicyCallbackAsync<T>(
        Func<ValueTask<T>> callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        var priorDepth = _settlementPolicyContextDepth.Value;
        _settlementPolicyContextDepth.Value =
            checked(priorDepth + 1);
        try
        {
            return await callback().ConfigureAwait(false);
        }
        finally
        {
            _settlementPolicyContextDepth.Value = priorDepth;
        }
    }

    internal async ValueTask InvokeSettlementPolicyCallbackAsync(
        Func<ValueTask> callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        _ = await InvokeSettlementPolicyCallbackAsync(
                async () =>
                {
                    await callback().ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);
    }

    private OperationLease EnterOperation(
        string operationId,
        bool authoritative,
        CancellationToken cancellationToken)
    {
        var normalizedId = WorldValidation.Required(
            operationId,
            nameof(operationId),
            512);
        lock (_gate)
        {
            if (_state == NativeWorldEngineSessionState.Disposed)
            {
                throw new ObjectDisposedException(
                    nameof(NativeWorldEngineSession));
            }

            if (_state != NativeWorldEngineSessionState.Active
                || _admissionsPaused
                || _runtime is null)
            {
                throw new InvalidOperationException(
                    "The native-world session is not accepting operations.");
            }

            if (_active.Count >= _options.MaxConcurrentOperations)
            {
                throw new InvalidOperationException(
                    "Native-world session operation capacity is exhausted.");
            }

            if (_active.ContainsKey(normalizedId))
            {
                throw new InvalidOperationException(
                    "The native-world operation id is already active.");
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var registration = new ActiveOperation(
                authoritative,
                _generation);
            _active.Add(normalizedId, registration);
            return new OperationLease(
                this,
                normalizedId,
                _runtime,
                _generation,
                linked);
        }
    }

    private void ExitOperation(
        string operationId,
        CancellationTokenSource linked)
    {
        try
        {
            linked.Dispose();
        }
        finally
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_gate)
            {
                if (_active.Remove(operationId)
                    && _active.Count == 0)
                {
                    drained = _activeDrained;
                    _activeDrained = null;
                }
            }

            drained?.TrySetResult(true);
        }
    }

    private async ValueTask<long> SwapAsync(
        WorldPackageDefinition definition,
        ActivatedWorldPackage package,
        NativeWorldRuntime runtime,
        CancellationToken cancellationToken)
    {
        Task drain;
        lock (_gate)
        {
            EnsureCanTransitionLocked();
            _admissionsPaused = true;
            drain = ActiveDrainTaskLocked();
        }

        try
        {
            await WaitWithCancellationAsync(drain, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_state is NativeWorldEngineSessionState.Empty
                    or NativeWorldEngineSessionState.Active)
                {
                    _admissionsPaused = false;
                }
            }

            throw;
        }

        lock (_gate)
        {
            EnsureCanTransitionLocked();
            if (_active.Count != 0)
            {
                throw new InvalidOperationException(
                    "The prior native-world generation did not drain.");
            }

            _definition = definition;
            _package = package;
            _runtime = runtime;
            _generation = checked(_generation + 1);
            _state = NativeWorldEngineSessionState.Active;
            _admissionsPaused = false;
            return _generation;
        }
    }

    private Task ActiveDrainTaskLocked()
    {
        if (_active.Count == 0)
        {
            return Task.CompletedTask;
        }

        _activeDrained ??= new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _activeDrained.Task;
    }

    private ActivatedWorldPackage RequirePackage()
    {
        lock (_gate)
        {
            EnsureCanTransitionLocked();
            return _package
                   ?? throw new InvalidOperationException(
                       "Load and activate a native package first.");
        }
    }

    private WorldPackageDefinition RequireDefinition()
    {
        lock (_gate)
        {
            EnsureCanTransitionLocked();
            return _definition
                   ?? throw new InvalidOperationException(
                       "Load and activate a native package first.");
        }
    }

    private void EnsureGeneration(long generation, string parameterName)
    {
        lock (_gate)
        {
            if (_generation != generation)
            {
                throw new ArgumentException(
                    "The interaction belongs to a stale native-world "
                    + "session generation.",
                    parameterName);
            }
        }
    }

    private void EnsureCanTransition()
    {
        lock (_gate)
        {
            EnsureCanTransitionLocked();
        }
    }

    private void EnsureOperationCallbackCanTransition()
    {
        if (_operationContextDepth.Value != 0)
        {
            throw new InvalidOperationException(
                "A native-world operation callback cannot start a "
                + "draining session transition.");
        }
    }

    private void EnsureCanTransitionLocked()
    {
        if (_state == NativeWorldEngineSessionState.Disposed)
        {
            throw new ObjectDisposedException(
                nameof(NativeWorldEngineSession));
        }

        if (_state is NativeWorldEngineSessionState.Stopping
            or NativeWorldEngineSessionState.Stopped)
        {
            throw new InvalidOperationException(
                "The native-world session is stopping or stopped.");
        }

        if (_activeSettlementLeaseId != 0)
        {
            throw new InvalidOperationException(
                "A native-world settlement lease is active.");
        }
    }

    private void EnsureSettlementLeaseLocked(
        NativeWorldRuntime runtime,
        long leaseId,
        long generation)
    {
        if (_state == NativeWorldEngineSessionState.Disposed)
        {
            throw new ObjectDisposedException(
                nameof(NativeWorldEngineSession));
        }

        if (_state != NativeWorldEngineSessionState.Active
            || _activeSettlementLeaseId != leaseId
            || !ReferenceEquals(_runtime, runtime)
            || _generation != generation)
        {
            throw new InvalidOperationException(
                "The native-world settlement lease is no longer active.");
        }
    }

    private static bool BindingMatches(
        WorldPresentationBinding binding,
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        return string.Equals(
                   binding.WorldId,
                   coordinate.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   binding.TimelineId,
                   coordinate.TimelineId,
                   StringComparison.Ordinal)
               && binding.TimelineEpoch == coordinate.TimelineEpoch
               && binding.SaveRevision == coordinate.SaveRevision
               && binding.StateVersion == coordinate.StateVersion
               && string.Equals(
                   binding.CatalogDigest,
                   coordinate.CatalogDigest,
                   StringComparison.Ordinal)
               && (binding.CommittedStateDigest is null
                   || string.Equals(
                       binding.CommittedStateDigest,
                       snapshot.StateDigest,
                       StringComparison.Ordinal));
    }

    private NativeWorldEngineSessionStatus StatusLocked()
    {
        return new NativeWorldEngineSessionStatus(
            _state,
            _generation,
            _package?.SourcePackage.PackageId,
            _package?.SourcePackage.PackageDigest,
            _package?.World.WorldId,
            _state == NativeWorldEngineSessionState.Active
            && !_admissionsPaused,
            _active.Count,
            _active
                .Where(pair => pair.Value.Authoritative)
                .Select(pair => pair.Key)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private string LocalOperationId(string prefix)
    {
        return prefix
               + ":"
               + Interlocked.Increment(ref _localOperationSequence)
                   .ToString(
                   System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SessionOperationId(
        string prefix,
        string sourceId)
    {
        return NativeWorldIdentity.Derive(prefix, sourceId);
    }

    private static NativeWorldEngineSettlementLease
        CreateSettlementLease(
            NativeWorldEngineSession owner,
            NativeWorldRuntime runtime,
            long leaseId,
            long generation,
            WorldAuthoritativeStateSnapshot snapshot)
    {
        return new NativeWorldEngineSettlementLease(
            owner,
            runtime,
            leaseId,
            generation,
            snapshot);
    }

    private void CloseLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeClosed, 1) == 0)
        {
            _lifetimeCancellation.Close();
        }
    }

    private static async ValueTask<T> RunBackgroundAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!WorldBackgroundWorkDispatcher.TryDispatch(
                async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await operation(cancellationToken)
                                .ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                out _))
        {
            throw new InvalidOperationException(
                WorldBackgroundOperationReasonCodes.QueueAtCapacity);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task WaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var canceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state =>
                ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            canceled);
        if (!ReferenceEquals(
                await Task.WhenAny(task, canceled.Task)
                    .ConfigureAwait(false),
                task))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private static string RequiredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new ArgumentException(
                            "The target path has no directory.",
                            nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = path
                        + "."
                        + Guid.NewGuid().ToString("N")
                        + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 65_536,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // The admitted target was never replaced by cleanup failure.
            }
        }
    }

    private sealed class ActiveOperation
    {
        public ActiveOperation(bool authoritative, long generation)
        {
            Authoritative = authoritative;
            Generation = generation;
        }

        public bool Authoritative { get; }

        public long Generation { get; }
    }

    private sealed class OperationLease : IDisposable
    {
        private NativeWorldEngineSession? _owner;
        private readonly string _operationId;
        private CancellationTokenSource? _linked;

        public OperationLease(
            NativeWorldEngineSession owner,
            string operationId,
            NativeWorldRuntime runtime,
            long generation,
            CancellationTokenSource linked)
        {
            _owner = owner;
            _operationId = operationId;
            Runtime = runtime;
            Generation = generation;
            _linked = linked;
        }

        public NativeWorldRuntime Runtime { get; }

        public long Generation { get; }

        public CancellationToken CancellationToken =>
            _linked?.Token
            ?? throw new ObjectDisposedException(nameof(OperationLease));

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var linked = Interlocked.Exchange(ref _linked, null);
            if (owner is not null && linked is not null)
            {
                owner.ExitOperation(_operationId, linked);
            }
        }
    }

    private sealed class PackageCandidate
    {
        public PackageCandidate(
            WorldPackageDefinition definition,
            NativeWorldPackageCompilation compilation,
            NativeWorldRuntime? runtime)
        {
            Definition = definition;
            Compilation = compilation;
            Runtime = runtime;
        }

        public WorldPackageDefinition Definition { get; }

        public NativeWorldPackageCompilation Compilation { get; }

        public NativeWorldRuntime? Runtime { get; }
    }

    private sealed class SaveCandidate
    {
        public SaveCandidate(
            WorldSaveDocument save,
            NativeWorldRuntime runtime)
        {
            Save = save;
            Runtime = runtime;
        }

        public WorldSaveDocument Save { get; }

        public NativeWorldRuntime Runtime { get; }
    }
}
