using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class DurableAgentRuntime :
    IDurableAgentRuntime,
    IGuardedDurableAgentRuntime,
    IAsyncDisposable
{
    private const int MaxDeferredContextCandidates = 128;
    private const int MaxContextDeferralTurns = 8;
    private const string DeferredCapacityReason = "deferred_capacity";
    private const string DeferredTurnLimitReason = "deferred_turn_limit";
    private static readonly JsonValueLimits DurableRunJsonLimits = new();

    private readonly ProviderAttemptRunner _provider;
    private readonly IGameHost _host;
    private readonly JournalCoordinator _journal;
    private readonly RunRecovery _recovery;
    private readonly ToolCatalogRegistry _tools;
    private readonly SkillCatalogRegistry _skills;
    private readonly SkillAdmissionEvaluator _skillAdmission;
    private readonly SkillContentRuntime _skillContent;
    private readonly ToolDisclosureEvaluator _toolDisclosure;
    private readonly ContextCompiler _contextCompiler;
    private readonly ToolBatchPlanner _toolPlanner;
    private readonly ToolBatchScheduler _toolScheduler;
    private readonly ToolInputSafetyGuard _toolSafety;
    private readonly IConversationContextEngine _conversationContext;
    private readonly AgentLifecyclePipeline _lifecycle;
    private readonly RuntimeMemoryAgentLoop? _memory;
    private readonly FinalOutputAdmissionEvaluator? _finalOutputAdmission;
    private readonly IRuntimeClock _clock;
    private readonly IRuntimeIdGenerator _ids;
    private readonly IRuntimeTokenEstimator _tokenEstimator;
    private readonly RunOwnershipRegistry _ownership;
    private readonly DurableAgentRuntimeOptions _options;
    private readonly RuntimeMetricsEmitter _metrics;
    private readonly ProviderWorkloadAdmission _providerAdmission;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly BoundedCancellationDispatcher
        _shutdownCancellationDispatcher;
    private readonly object _lifecycleSync = new();
    private readonly object _detachedProviderCleanupSync = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private TaskCompletionSource<bool>? _activeRunsDrained;
    private TaskCompletionSource<bool>? _detachedProviderCleanupsDrained;
    private Task? _stopTask;
    private Task? _shutdownResourceCleanupTask;
    private Task? _shutdownBoundedCleanupTask;
    private Task? _conversationContextCleanupTask;
    private long _ephemeralSequence;
    private long _detachedProviderCleanupCompletedCount;
    private long _detachedProviderCleanupFailureCount;
    private int _activeRuns;
    private int _detachedProviderCleanupCount;
    private int _lifecycleState;
    private int _detachedToolShutdownDrainResult;
    private int _detachedConversationShutdownDrainResult;
    private int _skillContentResolverShutdownDrainResult;
    private int _finalOutputAdmissionShutdownDrainResult;
    private int _detachedProviderShutdownDrainResult;
    private int _activeRunShutdownDrainResult;
    private int _shutdownResourceCleanupCompleted;

    public DurableAgentRuntime(
        ProviderAttemptRunner provider,
        IGameHost host,
        JournalCoordinator journal,
        RunRecovery recovery,
        ToolCatalogRegistry tools,
        SkillCatalogRegistry skills,
        ContextCompiler contextCompiler,
        ToolBatchPlanner toolPlanner,
        ToolBatchScheduler toolScheduler,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions? options = null,
        RuntimeControlPlane? controls = null,
        RunOwnershipRegistry? ownership = null,
        ToolInputSafetyGuard? toolSafety = null,
        ISkillAdmissionPolicy? skillAdmissionPolicy = null,
        IToolDisclosurePolicy? toolDisclosurePolicy = null,
        IConversationCompactor? conversationCompactor = null,
        RuntimeMemoryLifecycle? memoryLifecycle = null,
        IRuntimeMemoryPolicy? memoryPolicy = null,
        RuntimeMemoryIntegrationOptions? memoryOptions = null,
        ISkillContentResolver? skillContentResolver = null,
        IFinalOutputAdmissionPolicy? finalOutputAdmissionPolicy = null,
        IRuntimeTokenEstimator? tokenEstimator = null,
        IRuntimeMetricsSink? metricsSink = null,
        RuntimeMetricsOptions? metricsOptions = null,
        IConversationContextEngine? conversationContextEngine = null,
        AgentLifecyclePipeline? lifecyclePipeline = null)
        : this(
            provider,
            host,
            journal,
            recovery,
            tools,
            skills,
            contextCompiler,
            toolPlanner,
            toolScheduler,
            clock,
            ids,
            options,
            controls,
            ownership,
            toolSafety,
            skillAdmissionPolicy,
            toolDisclosurePolicy,
            BoundedCancellationDispatcher.Shared,
            BoundedCancellationDispatcher.LifecycleShared,
            conversationCompactor,
            memoryLifecycle,
            memoryPolicy,
            memoryOptions,
            skillContentResolver,
            finalOutputAdmissionPolicy,
            tokenEstimator,
            metricsSink,
            metricsOptions,
            conversationContextEngine,
            lifecyclePipeline)
    {
    }

    internal DurableAgentRuntime(
        ProviderAttemptRunner provider,
        IGameHost host,
        JournalCoordinator journal,
        RunRecovery recovery,
        ToolCatalogRegistry tools,
        SkillCatalogRegistry skills,
        ContextCompiler contextCompiler,
        ToolBatchPlanner toolPlanner,
        ToolBatchScheduler toolScheduler,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions? options,
        RuntimeControlPlane? controls,
        RunOwnershipRegistry? ownership,
        ToolInputSafetyGuard? toolSafety,
        ISkillAdmissionPolicy? skillAdmissionPolicy,
        IToolDisclosurePolicy? toolDisclosurePolicy,
        BoundedCancellationDispatcher cancellationDispatcher,
        BoundedCancellationDispatcher shutdownCancellationDispatcher,
        IConversationCompactor? conversationCompactor = null,
        RuntimeMemoryLifecycle? memoryLifecycle = null,
        IRuntimeMemoryPolicy? memoryPolicy = null,
        RuntimeMemoryIntegrationOptions? memoryOptions = null,
        ISkillContentResolver? skillContentResolver = null,
        IFinalOutputAdmissionPolicy? finalOutputAdmissionPolicy = null,
        IRuntimeTokenEstimator? tokenEstimator = null,
        IRuntimeMetricsSink? metricsSink = null,
        RuntimeMetricsOptions? metricsOptions = null,
        IConversationContextEngine? conversationContextEngine = null,
        AgentLifecyclePipeline? lifecyclePipeline = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _skillAdmission = new SkillAdmissionEvaluator(skillAdmissionPolicy);
        _toolDisclosure = new ToolDisclosureEvaluator(toolDisclosurePolicy);
        _contextCompiler =
            contextCompiler ?? throw new ArgumentNullException(nameof(contextCompiler));
        if (_contextCompiler.MaxCandidates
            > DurableRunInputJournalCodec.MaxContextCandidates)
        {
            throw new ArgumentException(
                "The durable runtime supports at most "
                + DurableRunInputJournalCodec.MaxContextCandidates
                + " context candidates.",
                nameof(contextCompiler));
        }
        _toolPlanner =
            toolPlanner ?? throw new ArgumentNullException(nameof(toolPlanner));
        _toolScheduler =
            toolScheduler ?? throw new ArgumentNullException(nameof(toolScheduler));
        _toolSafety = toolSafety ?? new ToolInputSafetyGuard();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _tokenEstimator =
            tokenEstimator ?? ScriptAwareTokenEstimator.Shared;
        _options = (options ?? new DurableAgentRuntimeOptions()).Snapshot();
        _skillContent = new SkillContentRuntime(
            skillContentResolver,
            _options.SkillRuntimeLimits,
            BoundedCancellationDispatcher.SkillContentResolverShared);
        if ((memoryLifecycle is null) != (memoryPolicy is null)
            || memoryLifecycle is null && memoryOptions is not null)
        {
            throw new ArgumentException(
                "Memory lifecycle and policy must be configured together.");
        }

        _memory = memoryLifecycle is null
            ? null
            : new RuntimeMemoryAgentLoop(
                memoryLifecycle,
                memoryPolicy!,
                memoryOptions);
        if (_options.FinalOutputAdmission.Enabled
            != (finalOutputAdmissionPolicy is not null))
        {
            throw new ArgumentException(
                _options.FinalOutputAdmission.Enabled
                    ? "Strict final-output admission requires an admission "
                      + "policy."
                    : "A final-output admission policy requires strict "
                      + "admission to be enabled.",
                nameof(finalOutputAdmissionPolicy));
        }

        _finalOutputAdmission = finalOutputAdmissionPolicy is null
            ? null
            : new FinalOutputAdmissionEvaluator(
                finalOutputAdmissionPolicy,
                _options.FinalOutputAdmission);
        _metrics = new RuntimeMetricsEmitter(metricsSink, metricsOptions);
        if (conversationContextEngine is not null
            && conversationCompactor is not null)
        {
            throw new ArgumentException(
                "A custom context engine cannot be combined with the built-in compactor.",
                nameof(conversationContextEngine));
        }

        _conversationContext = conversationContextEngine is null
            ? new ConversationContextManager(
                _options.ConversationContext,
                conversationCompactor
                ?? new ExtractiveConversationCompactor(),
                _clock,
                shutdownCancellationDispatcher
                ?? throw new ArgumentNullException(
                    nameof(shutdownCancellationDispatcher)),
                detachedCompactionCleanupCheckpoint: null,
                metrics: _metrics)
            : new BoundedConversationContextEngine(
                conversationContextEngine,
                _options.ConversationContext);
        _ = RuntimeGuard.RequiredUtf8(
            _conversationContext.EngineId,
            128,
            nameof(conversationContextEngine));
        _lifecycle = lifecyclePipeline
                     ?? new AgentLifecyclePipeline(registrations: null);
        _ = RuntimeGuard.RequiredUtf8(
            _conversationContext.Version,
            64,
            nameof(conversationContextEngine));
        Controls = controls ?? new RuntimeControlPlane();
        _ownership = ownership ?? new RunOwnershipRegistry();
        _cancellationDispatcher = cancellationDispatcher
                                  ?? throw new ArgumentNullException(
                                      nameof(cancellationDispatcher));
        _shutdownCancellationDispatcher = shutdownCancellationDispatcher
                                          ?? throw new ArgumentNullException(
                                              nameof(
                                                  shutdownCancellationDispatcher));
        _providerAdmission = new ProviderWorkloadAdmission(
            _options.MaxConcurrentProviderCalls,
            _options.MaxConcurrentBackgroundProviderCalls,
            _metrics);
    }

    public RuntimeControlPlane Controls { get; }

    internal ProviderWorkloadAdmission ProviderAdmission =>
        _providerAdmission;

    public RuntimeMetricsHealth MetricsHealth => _metrics.Health;

    public int DetachedToolExecutionCount =>
        _toolScheduler.DetachedExecutionCount;

    public bool? DetachedToolExecutionsDrainedOnStop
    {
        get
        {
            return Volatile.Read(
                ref _detachedToolShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    public bool? DetachedConversationCompactionsDrainedOnStop
    {
        get
        {
            return Volatile.Read(
                ref _detachedConversationShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    /// <summary>
    /// Reports whether tracked skill-content resolver calls drained within
    /// their bounded shutdown window. Null means shutdown has not reached
    /// that boundary.
    /// </summary>
    public bool? SkillContentResolversDrainedOnStop
    {
        get
        {
            return Volatile.Read(
                ref _skillContentResolverShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    /// <summary>
    /// Gets the number of resolver calls that outlived their bounded caller
    /// and remain isolated under the runtime's resolver concurrency cap.
    /// </summary>
    public int DetachedSkillContentResolverCallCount =>
        _skillContent.DetachedResolverCallCount;

    /// <summary>
    /// Reports whether final-output admission policy calls that outlived
    /// their caller drained within their bounded shutdown window. Null means
    /// strict admission is disabled or shutdown has not reached that
    /// boundary.
    /// </summary>
    public bool? FinalOutputAdmissionPolicyCallsDrainedOnStop
    {
        get
        {
            if (_finalOutputAdmission is null)
            {
                return null;
            }

            return Volatile.Read(
                ref _finalOutputAdmissionShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    /// <summary>
    /// Gets the number of final-output admission evaluations that outlived
    /// their caller and whose policy execution or cancellation cleanup has
    /// not yet settled.
    /// </summary>
    public int DetachedFinalOutputAdmissionPolicyCallCount =>
        _finalOutputAdmission?.DetachedEvaluationCount ?? 0;

    public bool? ActiveRunsDrainedOnStop
    {
        get
        {
            return Volatile.Read(ref _activeRunShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    /// <summary>
    /// Reports whether provider attempt cleanup tasks drained within the
    /// bounded shutdown window. Null means shutdown has not reached that
    /// boundary.
    /// </summary>
    public bool? DetachedProviderCleanupsDrainedOnStop
    {
        get
        {
            return Volatile.Read(
                ref _detachedProviderShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
        }
    }

    /// <summary>
    /// Gets the number of detached provider cleanup tasks that have not yet
    /// settled. The runtime retains only a count and a shared drain signal,
    /// rather than retaining every task.
    /// </summary>
    public int DetachedProviderCleanupCount =>
        Volatile.Read(ref _detachedProviderCleanupCount);

    /// <summary>
    /// Gets the number of tracked detached provider cleanup tasks that have
    /// settled, whether successfully or with an observed failure.
    /// </summary>
    public long DetachedProviderCleanupCompletedCount =>
        Volatile.Read(ref _detachedProviderCleanupCompletedCount);

    /// <summary>
    /// Gets the number of detached provider cleanup tasks that faulted after
    /// they were handed to the runtime.
    /// </summary>
    public long DetachedProviderCleanupFailureCount =>
        Volatile.Read(ref _detachedProviderCleanupFailureCount);

    /// <summary>
    /// Reports whether active runs, provider-owned cleanup, conversation
    /// cleanup, root cancellation callbacks, and the bounded skill-content
    /// resolver cancellation/isolation phase, and the bounded final-output
    /// admission cancellation/isolation phase have settled. This is distinct
    /// from the bounded result returned by <see cref="StopAsync"/> and does
    /// not claim that a non-cooperative host tool, skill-content resolver, or
    /// final-output admission policy callback exited. Inspect
    /// <see cref="DetachedSkillContentResolverCallCount"/> and
    /// <see cref="DetachedFinalOutputAdmissionPolicyCallCount"/> for those
    /// callbacks.
    /// </summary>
    public bool ShutdownResourceCleanupCompleted =>
        Volatile.Read(ref _shutdownResourceCleanupCompleted) != 0;

    internal bool ConversationContextCleanupCompleted
    {
        get
        {
            var cleanup = Volatile.Read(
                ref _conversationContextCleanupTask);
            return _conversationContext.CleanupCompleted
                   && (cleanup is null || cleanup.IsCompletedSuccessfully);
        }
    }

    public IReadOnlyList<DetachedToolExecutionSnapshot>
        GetDetachedToolExecutionSnapshot()
    {
        return _toolScheduler.GetDetachedExecutionSnapshot();
    }

    public IReadOnlyList<DetachedToolExecutionSnapshot>
        GetDetachedToolExecutionSnapshot(int maxItems)
    {
        return _toolScheduler.GetDetachedExecutionSnapshot(maxItems);
    }

    /// <summary>
    /// Captures the execution-policy identity that a newly admitted agent loop
    /// would use now. A caller may persist this through
    /// <see cref="DurableExecutionPolicyBinding"/> to fail closed if policy
    /// changes before execution starts.
    /// </summary>
    public DurableExecutionPolicyIdentity CaptureExecutionPolicyIdentity(
        CancellationToken cancellationToken = default)
    {
        return CaptureExecutionPolicy(
            DurableExecutionModes.Agent,
            routePreference: null,
            cancellationToken).Identity;
    }

    public DurableExecutionPolicyIdentity CaptureExecutionPolicyIdentity(
        string executionMode,
        CancellationToken cancellationToken = default)
    {
        return CaptureExecutionPolicy(
            executionMode,
            routePreference: null,
            cancellationToken).Identity;
    }

    public DurableExecutionPolicyIdentity CaptureExecutionPolicyIdentity(
        string executionMode,
        ProviderRoutePreference? routePreference,
        CancellationToken cancellationToken = default)
    {
        return CaptureExecutionPolicy(
            executionMode,
            routePreference,
            cancellationToken).Identity;
    }

    public async ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using var activeRun = EnterRun(cancellationToken);
        cancellationToken = activeRun.Token;
        var requestSnapshot = Snapshot(request, cancellationToken);
        var runId = requestSnapshot.Run.RunId;
        var startingGameContext = GameContextEnvelope.ValidateForRun(
            requestSnapshot.Run,
            nameof(request));
        if (_lifecycle.Count > 0)
        {
            await _lifecycle.InvokeAsync(
                    new RunStartingLifecycleEvent(
                        runId,
                        requestSnapshot.Run.AgentId,
                        requestSnapshot.Run.WorldId,
                        requestSnapshot.Run.SessionId,
                        isResume: false,
                        startingGameContext),
                    allowRejection: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        DurableRunOutcome? outcome = null;
        Exception? failure = null;
        try
        {
            outcome = await RunCoreAsync(requestSnapshot, cancellationToken)
                .ConfigureAwait(false);
            return outcome;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (_lifecycle.Count > 0)
                {
                    await _lifecycle.InvokeAsync(
                        new RunCompletedLifecycleEvent(
                            runId,
                            isResume: false,
                            outcome,
                            failure),
                        allowRejection: false,
                        CancellationToken.None,
                        enforceRequired: false)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                _ = exception;
            }
        }
    }

    private async ValueTask<DurableRunOutcome> RunCoreAsync(
        DurableRunRequest requestSnapshot,
        CancellationToken cancellationToken)
    {
        BindNewRunFinalOutputAdmission(
            requestSnapshot.Run,
            requestSnapshot.FinalOutputContract);
        if (string.Equals(
                requestSnapshot.ExecutionMode,
                DurableExecutionModes.Direct,
                StringComparison.Ordinal)
            && requestSnapshot.FinalOutputContract is not null)
        {
            throw new ArgumentException(
                "A direct run cannot use tool-mediated final-output admission.",
                nameof(requestSnapshot));
        }
        _ = GameContextEnvelope.ValidateForRun(
            requestSnapshot.Run,
            nameof(requestSnapshot));
        EnsureContextObservationsVisibleToRun(
            requestSnapshot.Context,
            requestSnapshot.Run);
        RunAdmission.EnsureNewRun(
            requestSnapshot.Run,
            "request");

        var laneId = LaneId(
            requestSnapshot.Run,
            requestSnapshot.LaneId);
        await using var ownership = await _ownership.AcquireAsync(
                requestSnapshot.Run.RunId,
                laneId,
                cancellationToken)
            .ConfigureAwait(false);
        using var control = Controls.Register(
            requestSnapshot.Run,
            _options.RequireAudienceIncarnationForRestrictedObservations);
        var transcript = DistinctTranscript(
                requestSnapshot.InitialTranscript)
            .ToList();
        var startedAt = _clock.UtcNow;
        using var deadline = new RunDeadline(
            requestSnapshot.Run.Budget.MaxDurationMs,
            requestSnapshot.Run.Usage.DurationMs,
            cancellationToken,
            _cancellationDispatcher);
        var executionPolicy = CaptureExecutionPolicy(
            requestSnapshot.ExecutionMode,
            requestSnapshot.RoutePreference,
            cancellationToken);
        EnsureExecutionPolicyBinding(
            requestSnapshot.Run,
            executionPolicy);
        var initialSkillActivationState = ReconcileActiveSkillState(
            requestSnapshot.ActiveSkills,
            Array.Empty<SkillActivationStateRecord>(),
            executionPolicy.Skills);
        SkillActivationStateCodec.Attach(
            requestSnapshot.Run,
            initialSkillActivationState);
        var runStarted = false;

        try
        {
            // Every validation and provider-policy capture that can fail
            // before persistence is complete above. From this point a store
            // exception can be an ambiguous acknowledgement of an atomically
            // committed run-start batch, so failure recovery must treat the
            // run as started.
            runStarted = true;
            await _journal.CommitRunStartAsync(
                    requestSnapshot.Run,
                    transcript,
                    requestSnapshot.Context,
                    requestSnapshot.ActiveSkills,
                    requestSnapshot.WorkloadClass,
                    requestSnapshot.ExecutionMode,
                    requestSnapshot.Inference,
                    requestSnapshot.RoutePreference,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ExecuteLoopAsync(
                    requestSnapshot.Run,
                    transcript,
                    requestSnapshot.Context,
                    requestSnapshot.ActiveSkills,
                    Array.Empty<ToolActivationRecord>(),
                    requestSnapshot.WorkloadClass,
                    requestSnapshot.ExecutionMode,
                    requestSnapshot.Inference,
                    control,
                    startedAt,
                    deadline,
                    executionPolicy,
                    initialSkillActivationState:
                        initialSkillActivationState)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return await CompleteDurationDeadlineAsync(
                    requestSnapshot.Run,
                    transcript,
                    startedAt)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            runStarted
            &&
            exception is not DuplicateRunException
            and not DurableExecutionPolicyMismatchException)
        {
            return await FailOrCancelAsync(
                    requestSnapshot.Run,
                    transcript,
                    exception,
                    startedAt,
                    cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// Deferred context is retained only while the current execution call remains
    /// active. A caller that resumes a run must resupply any still-relevant
    /// candidates through <paramref name="continuation"/>.
    /// </remarks>
    ValueTask<DurableRunOutcome> IDurableAgentRuntime.ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken)
    {
        return ResumeAsync(
            runId,
            continuation,
            reconciler,
            cancellationToken,
            guard: null);
    }

    public async ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default,
        DurableRunResumeGuard? guard = null)
    {
        using var activeRun = EnterRun(cancellationToken);
        cancellationToken = activeRun.Token;
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        var continuationSnapshot = Snapshot(
            continuation ?? new DurableRunContinuation(),
            cancellationToken);
        var guardSnapshot = guard is null ? null : Snapshot(guard);
        DurableRunOutcome? outcome = null;
        Exception? failure = null;
        try
        {
            outcome = await ResumeCoreAsync(
                    runId,
                    continuationSnapshot,
                    reconciler,
                    cancellationToken,
                    guardSnapshot)
                .ConfigureAwait(false);
            return outcome;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (_lifecycle.Count > 0)
                {
                    await _lifecycle.InvokeAsync(
                        new RunCompletedLifecycleEvent(
                            runId,
                            isResume: true,
                            outcome,
                            failure),
                        allowRejection: false,
                        CancellationToken.None,
                        enforceRequired: false)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                _ = exception;
            }
        }
    }

    private async ValueTask<DurableRunOutcome> ResumeCoreAsync(
        string runId,
        DurableRunContinuation continuationSnapshot,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard? guardSnapshot)
    {
        var recovered = await _recovery.LoadAsync(runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DurableRunNotFoundException(runId);
        var recoveredAdmission = EnsureRecoveredFinalOutputAdmission(
            recovered,
            continuationSnapshot.FinalOutputContract);
        if (_options.RequireSemanticResumeGuard
            && !RunStateMachine.IsTerminal(recovered.Run.State)
            && guardSnapshot?.SemanticExtensionName is null)
        {
            throw ResumeGuardFailure(
                DurableRunResumeGuardReasonCodes.SemanticGuardRequired);
        }

        if (guardSnapshot is not null)
        {
            EnsureResumeGuard(recovered.Run, runId, guardSnapshot);
        }

        var recoveredGameContext = GameContextEnvelope.ValidateForRun(
            recovered.Run,
            nameof(runId));
        if (_lifecycle.Count > 0)
        {
            await _lifecycle.InvokeAsync(
                    new RunStartingLifecycleEvent(
                        runId,
                        recovered.Run.AgentId,
                        recovered.Run.WorldId,
                        recovered.Run.SessionId,
                        isResume: true,
                        recoveredGameContext),
                    allowRejection: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        var laneId = LaneId(recovered.Run, continuationSnapshot.LaneId);
        await using var ownership = await _ownership.AcquireAsync(
                runId,
                laneId,
                cancellationToken)
            .ConfigureAwait(false);
        var transcript = recovered.Transcript.ToList();
        IReadOnlyList<ContextCandidate> context =
            continuationSnapshot.Context;
        IReadOnlyList<SkillReference> activeSkills =
            continuationSnapshot.ActiveSkills;
        var replacesActiveSkillState =
            activeSkills.Count > 0
            || continuationSnapshot.ReplaceActiveSkills;
        IReadOnlyList<SkillActivationStateRecord> activeSkillState =
            Array.Empty<SkillActivationStateRecord>();
        var workloadClass = continuationSnapshot.WorkloadClass
                            ?? recovered.RecoveryWorkloadClass;
        var executionMode = recovered.RecoveryExecutionMode;
        if (string.Equals(
                executionMode,
                DurableExecutionModes.Direct,
                StringComparison.Ordinal)
            && (activeSkills.Count != 0
                || continuationSnapshot.ReplaceActiveSkills))
        {
            throw new ArgumentException(
                "A direct durable run cannot activate or replace skills.",
                nameof(continuationSnapshot));
        }
        if (context.Count == 0)
        {
            context = recovered.RecoveryContext;
        }

        if (activeSkills.Count == 0
            && !continuationSnapshot.ReplaceActiveSkills)
        {
            activeSkills = recovered.RecoveryActiveSkills;
            activeSkillState = recovered.RecoverySkillActivationState;
        }

        EnsureContextObservationsVisibleToRun(context, recovered.Run);
        var startedAt = BackdateDuration(
            _clock.UtcNow,
            recovered.Run.Usage.DurationMs);
        using var deadline = new RunDeadline(
            recovered.Run.Budget.MaxDurationMs,
            recovered.Run.Usage.DurationMs,
            cancellationToken,
            _cancellationDispatcher);
        var replaySafeCheckpointTurnId = recovered.ReplaySafeTurnId;

        try
        {
            if (continuationSnapshot.RequestCancellation)
            {
                return await CancelRecoveredRunAsync(
                        recovered,
                        transcript,
                        reconciler,
                        startedAt,
                        deadline.Token)
                    .ConfigureAwait(false);
            }

            using var control = Controls.Register(
                recovered.Run,
                _options
                    .RequireAudienceIncarnationForRestrictedObservations);
            await ReplayPreparedMemoryCommitsAsync(
                    recovered,
                    cancellationToken)
                .ConfigureAwait(false);
            if (RunStateMachine.IsTerminal(recovered.Run.State))
            {
                EnsureTerminalOutputWasAdmitted(
                    recovered,
                    recoveredAdmission);
                await SettleRecoveredProviderDispatchesAsync(
                        recovered.Run,
                        recovered.UnsettledProviderDispatches,
                        startedAt,
                        terminalRun: true)
                    .ConfigureAwait(false);
                return Outcome(
                    recovered.Run,
                    transcript,
                    recovered.FinalOutput,
                    recovered.TerminalOutcome);
            }

            if (string.Equals(
                    recovered.Run.State,
                    RunStates.Preparing,
                    StringComparison.Ordinal))
            {
                return await FailOrCancelAsync(
                        recovered.Run,
                        transcript,
                        new InvalidDataException(
                            "The durable run did not commit its complete "
                            + "initialization batch."),
                        startedAt,
                        cancellationRequested: false)
                    .ConfigureAwait(false);
            }

            if (recovered.ReplaySafeTurnId is not null)
            {
                await AbandonReplaySafeTurnAsync(
                        recovered.Run,
                        recovered.ReplaySafeTurnId,
                        startedAt,
                        deadline.Token)
                    .ConfigureAwait(false);
                recovered.ReplaySafeTurnId = null;
            }

            var lostBilledProviderResult =
                await SettleRecoveredProviderDispatchesAsync(
                        recovered.Run,
                        recovered.UnsettledProviderDispatches,
                        startedAt,
                        terminalRun: false)
                    .ConfigureAwait(false);
            if (recovered.Run.Usage.HasUnaccountedUsage
                && recovered.PendingOperations.Count == 0)
            {
                return await FailUnaccountedUsageAsync(
                        recovered.Run,
                        transcript,
                        startedAt)
                    .ConfigureAwait(false);
            }

            if (lostBilledProviderResult)
            {
                return await FailProviderResultRecoveryAsync(
                        recovered.Run,
                        transcript,
                        startedAt)
                    .ConfigureAwait(false);
            }

            if (!recovered.Run.Usage.HasUnaccountedUsage
                && recovered.FinalOutput.HasValue
                && recovered.PendingOperations.Count == 0)
            {
                _ = await CommitRecoveredMemoryReceiptsAsync(
                        recovered,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _journal.CommitRecoveredCompletionAsync(
                        recovered.Run,
                        recovered.Run.CurrentTurnId ?? "recovered-turn",
                        attemptId: null,
                        startedAt,
                        deadline.Token)
                    .ConfigureAwait(false);
                return Outcome(
                    recovered.Run,
                    transcript,
                    recovered.FinalOutput);
            }

            if (recovered.PendingOperations.Count > 0)
            {
                if (reconciler is null)
                {
                    return recovered.Run.Usage.HasUnaccountedUsage
                        ? UnaccountedUsageOutcome(recovered.Run, transcript)
                        : Outcome(recovered.Run, transcript);
                }

                recovered = await _recovery.ReconcileAsync(
                        recovered,
                        reconciler,
                        _ids.NewId("reconcile-attempt"),
                        deadline.Token)
                    .ConfigureAwait(false);
                transcript = recovered.Transcript.ToList();
                if (recovered.PendingOperations.Count > 0)
                {
                    if (deadline.IsExpired)
                    {
                        return await CompleteDurationDeadlineAsync(
                                recovered.Run,
                                transcript,
                                startedAt)
                            .ConfigureAwait(false);
                    }

                    return Outcome(recovered.Run, transcript);
                }
            }

            if (recovered.Run.Usage.HasUnaccountedUsage)
            {
                return await FailUnaccountedUsageAsync(
                        recovered.Run,
                        transcript,
                        startedAt)
                    .ConfigureAwait(false);
            }

            await AdvanceRecoveredGameContextAsync(
                    recovered,
                    cancellationToken)
                .ConfigureAwait(false);
            var memoryDecisionSettled =
                await CommitRecoveredMemoryReceiptsAsync(
                    recovered,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(
                    recovered.Run.State,
                    RunStates.Running,
                    StringComparison.Ordinal)
                && recovered.Run.CurrentTurnId is string committedTurnId
                && memoryDecisionSettled)
            {
                await CompleteTurnWithoutStateChangeAsync(
                        recovered.Run,
                        committedTurnId,
                        _ids.NewId("memory-recovery"),
                        startedAt,
                        "memory_recovered_committed_turn",
                        deadline.Token)
                    .ConfigureAwait(false);
            }

            var terminal = CompletionState(recovered.Run.CompletionIntent);
            if (terminal is not null)
            {
                await TransitionAfterReconciliationAsync(
                        recovered.Run,
                        terminal,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(recovered.Run, transcript);
            }

            if (string.Equals(
                    recovered.Run.State,
                    RunStates.Reconciling,
                    StringComparison.Ordinal)
                || string.Equals(
                    recovered.Run.State,
                    RunStates.WaitingForAction,
                    StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAndMutationAsync(
                        recovered.Run,
                        RunStates.Running,
                        RuntimeEventKinds.TurnCompleted,
                        next =>
                        {
                            next.CurrentTurnId = null;
                            UpdateDuration(next, startedAt);
                        },
                        turnId: recovered.Run.CurrentTurnId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(
                         recovered.Run.State,
                         RunStates.Cancelling,
                         StringComparison.Ordinal)
                     || string.Equals(
                         recovered.Run.State,
                         RunStates.Interrupting,
                         StringComparison.Ordinal))
            {
                var state = string.Equals(
                    recovered.Run.State,
                    RunStates.Cancelling,
                    StringComparison.Ordinal)
                    ? RunStates.Cancelled
                    : RunStates.Interrupted;
                await TransitionAfterReconciliationAsync(
                        recovered.Run,
                        state,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(recovered.Run, transcript);
            }

            var executionPolicy = CaptureExecutionPolicy(
                executionMode,
                recovered.RecoveryRoutePreference,
                cancellationToken);
            EnsureExecutionPolicyBinding(
                recovered.Run,
                executionPolicy);
            if (replacesActiveSkillState)
            {
                activeSkillState = ReconcileActiveSkillState(
                    activeSkills,
                    Array.Empty<SkillActivationStateRecord>(),
                    executionPolicy.Skills);
            }

            return await ExecuteLoopAsync(
                    recovered.Run,
                    transcript,
                    context,
                    activeSkills,
                    recovered.RecoveryToolActivations,
                    workloadClass,
                    executionMode,
                    recovered.RecoveryInference,
                    control,
                    startedAt,
                    deadline,
                    executionPolicy,
                    CreateRuntimeLoopRecoveryState(
                        recovered,
                        replaySafeCheckpointTurnId),
                    activeSkillState,
                    replacesActiveSkillState)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return await CompleteDurationDeadlineAsync(
                    recovered.Run,
                    transcript,
                    startedAt)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not DuplicateRunException
            and not DurableExecutionPolicyMismatchException)
        {
            return await FailOrCancelAsync(
                    recovered.Run,
                    transcript,
                    exception,
                    startedAt,
                    cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        return WaitForShutdownDrainAsync();
    }

    public ValueTask StopAsync()
    {
        Task stopTask;
        Task? drainTask = null;
        TaskCompletionSource<bool>? completion = null;
        TaskCompletionSource<bool>? boundedCleanupCompletion = null;
        BoundedCancellationDispatcher.CancellationDispatchReservation?
            shutdownCancellationReservation = null;
        var initiate = false;
        var capacityRejected = false;
        lock (_lifecycleSync)
        {
            if (_stopTask is null)
            {
                if (!_shutdownCancellationDispatcher.TryReserve(
                        out shutdownCancellationReservation))
                {
                    capacityRejected = true;
                }
                else
                {
                    Volatile.Write(ref _lifecycleState, 1);
                    drainTask = _activeRuns == 0
                        ? Task.CompletedTask
                        : (_activeRunsDrained ??= NewCompletion()).Task;
                    completion = NewCompletion();
                    boundedCleanupCompletion = NewCompletion();
                    _stopTask = completion.Task;
                    initiate = true;
                }
            }

            stopTask = capacityRejected
                ? Task.FromException(
                    new InvalidOperationException(
                        "Runtime shutdown cancellation capacity is exhausted."))
                : _stopTask!;
        }

        if (initiate)
        {
            Task cancellationTask;
            try
            {
                cancellationTask = shutdownCancellationReservation!
                    .DispatchAsync(_shutdownCancellation);
            }
            catch (Exception exception)
            {
                lock (_lifecycleSync)
                {
                    if (ReferenceEquals(_stopTask, completion!.Task))
                    {
                        _stopTask = null;
                        Volatile.Write(ref _lifecycleState, 0);
                    }
                }

                completion!.TrySetException(exception);
                return new ValueTask(stopTask);
            }

            var shutdownCancellationCleanup =
                DisposeShutdownCancellationAsync(
                _shutdownCancellation,
                cancellationTask,
                shutdownCancellationReservation!);
            var resourceCleanup = CompleteShutdownResourceCleanupAsync(
                drainTask!,
                boundedCleanupCompletion!,
                shutdownCancellationCleanup);
            Volatile.Write(
                ref _shutdownBoundedCleanupTask,
                boundedCleanupCompletion!.Task);
            Volatile.Write(
                ref _shutdownResourceCleanupTask,
                resourceCleanup);
            ObserveTaskFailure(resourceCleanup);
            _ = CompleteStopAsync(
                drainTask!,
                completion!);
        }

        return new ValueTask(stopTask);
    }

    /// <summary>
    /// Initiates bounded shutdown and then waits for active runs, detached
    /// provider cleanup, conversation-context cleanup, and the bounded
    /// skill-content resolver and final-output admission
    /// cancellation/isolation phases to settle. It does not wait forever for
    /// a resolver or admission policy that ignores cancellation; such calls
    /// remain visible through
    /// <see cref="DetachedSkillContentResolverCallCount"/> and
    /// <see cref="DetachedFinalOutputAdmissionPolicyCallCount"/>. Calling
    /// this method does not cancel the shared cleanup when the caller's token
    /// is cancelled.
    /// </summary>
    public async ValueTask WaitForShutdownDrainAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitForSharedTaskAsync(
                StopAsync().AsTask(),
                cancellationToken)
            .ConfigureAwait(false);

        var cleanup = Volatile.Read(ref _shutdownResourceCleanupTask)
                      ?? throw new InvalidOperationException(
                          "Runtime shutdown cleanup was not initialized.");
        await WaitForSharedTaskAsync(cleanup, cancellationToken)
            .ConfigureAwait(false);

        if (!ShutdownResourceCleanupCompleted)
        {
            bool conversationContextDrained;
            try
            {
                conversationContextDrained = await _conversationContext
                    .StopAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                throw new InvalidOperationException(
                    "The conversation context engine failed during shutdown cleanup.",
                    exception);
            }

            if (!conversationContextDrained)
            {
                throw new InvalidOperationException(
                    "The conversation context engine did not complete shutdown cleanup.");
            }

            Volatile.Write(ref _shutdownResourceCleanupCompleted, 1);
        }
    }

    private ValueTask AbandonReplaySafeTurnAsync(
        AgentRun run,
        string turnId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                run.State,
                RunStates.Running,
                StringComparison.Ordinal)
            || !string.Equals(
                run.CurrentTurnId,
                turnId,
                StringComparison.Ordinal)
            || run.Usage.Turns < 1)
        {
            throw new InvalidDataException(
                "The recoverable provider-safe turn does not match "
                + "the durable run checkpoint.");
        }

        return _journal.CommitRunMutationAsync(
            run,
            RuntimeEventKinds.TurnCompleted,
            next =>
            {
                next.CurrentTurnId = null;
                next.Usage.Turns--;
                UpdateDuration(next, startedAt);
            },
            turnId,
            attemptId: null,
            cancellationToken,
            eventId: "provider-safe-turn-abandoned:" + turnId,
            reasonCode: RunRecovery.ReplaySafeTurnAbandonedReason);
    }

    private RuntimeExecutionPolicyLease CaptureExecutionPolicy(
        string executionMode,
        ProviderRoutePreference? routePreference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executionMode = DurableExecutionModes.Normalize(
            executionMode,
            nameof(executionMode));
        var direct = string.Equals(
            executionMode,
            DurableExecutionModes.Direct,
            StringComparison.Ordinal);
        var currentTools = _tools.Current;
        var currentSkills = _skills.Current;
        var tools = direct
            ? new ToolCatalogSnapshot(
                currentTools.Generation,
                Array.Empty<ToolCatalogEntry>())
            : currentTools;
        var skills = direct
            ? new SkillCatalogSnapshot(
                currentSkills.Generation,
                Array.Empty<SkillCatalogEntry>())
            : currentSkills;
        var providerRoutes = _provider.CaptureRoutePlan(
            routePreference,
            cancellationToken);
        var providerPolicy = new CanonicalDigestBuilder();
        providerPolicy.Add("type", "durable-provider-policy.v1");
        var modelPolicy = new CanonicalDigestBuilder();
        modelPolicy.Add("type", "durable-model-policy.v1");
        var identities = providerRoutes.RouteIdentities;
        providerPolicy.Add("routeCount", identities.Count);
        modelPolicy.Add("routeCount", identities.Count);
        for (var index = 0; index < identities.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = identities[index];
            providerPolicy.Add("routeIndex", index);
            providerPolicy.Add("providerId", route.ProviderId);
            providerPolicy.Add(
                "transportDialect",
                route.TransportDialect);
            providerPolicy.Add(
                "dialectSemanticDigest",
                route.DialectSemanticDigest);
            providerPolicy.Add(
                "capabilityDigest",
                route.CapabilityDigest);
            providerPolicy.Add(
                "routePolicyVersion",
                route.RoutePolicyVersion);
            providerPolicy.Add(
                "routePolicyDigest",
                route.RoutePolicyDigest);
            modelPolicy.Add("routeIndex", index);
            modelPolicy.Add("providerId", route.ProviderId);
            modelPolicy.Add(
                "modelId",
                string.Equals(
                    route.ModelId,
                    "unspecified",
                    StringComparison.Ordinal)
                    ? _options.ModelId
                    : route.ModelId);
        }

        return new RuntimeExecutionPolicyLease(
            tools,
            skills,
            providerRoutes,
            new DurableExecutionPolicyIdentity(
                tools.Digest,
                skills.Digest,
                providerPolicy.Finish(),
                modelPolicy.Finish()));
    }

    private static void EnsureExecutionPolicyBinding(
        AgentRun run,
        RuntimeExecutionPolicyLease executionPolicy)
    {
        var expected = DurableExecutionPolicyBinding.Read(run);
        if (expected is not null
            && !expected.Matches(executionPolicy.Identity))
        {
            throw new DurableExecutionPolicyMismatchException();
        }
    }

    private async ValueTask<DurableRunOutcome> ExecuteLoopAsync(
        AgentRun run,
        List<NormalizedMessage> transcript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        IReadOnlyList<ToolActivationRecord> initialToolActivations,
        string workloadClass,
        string executionMode,
        ModelInferenceOptions? inference,
        RuntimeControlPlane.Registration control,
        DateTimeOffset startedAt,
        RunDeadline deadline,
        RuntimeExecutionPolicyLease executionPolicy,
        RuntimeLoopRecoveryState? recoveryState = null,
        IReadOnlyList<SkillActivationStateRecord>?
            initialSkillActivationState = null,
        bool allowInitialSkillStateReplacement = false)
    {
        var cancellationToken = deadline.Token;
        var directExecution = string.Equals(
            executionMode,
            DurableExecutionModes.Direct,
            StringComparison.Ordinal);
        var toolSnapshot = executionPolicy.Tools;
        var skillSnapshot = executionPolicy.Skills;
        var routePlan = executionPolicy.ProviderRoutes;
        var effectiveActiveSkills = activeSkills
            .Select(value => new SkillReference(value.SkillId, value.Version))
            .ToList();
        var activeSkillState =
            (initialSkillActivationState
             ?? Array.Empty<SkillActivationStateRecord>())
            .Select(value => value.Clone())
            .ToList();
        var allowSkillStateReplacement =
            allowInitialSkillStateReplacement;
        var pendingContext = new List<ContextCandidate>(initialContext);
        var contextDeferralTurns = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var runAttemptId = _ids.NewId("run-attempt");
        var fence = new AttemptFence();
        var disclosedSkillDigest =
            recoveryState?.DisclosedSkillAdmissionDigest;
        var previousProviderCacheKey =
            recoveryState?.PreviousProviderCacheKey;
        var providerOpaqueContinuationState =
            recoveryState?.ProviderOpaqueContinuationState?.Snapshot();
        var finalOutputAdmission =
            FinalOutputAdmissionBinding.Read(run);
        var finalOutputEvidence = new FinalOutputEvidenceRegistry();
        foreach (var item in recoveryState?.FinalOutputCommittedEvidence
                 ?? Array.Empty<FinalOutputCommittedEvidence>())
        {
            finalOutputEvidence.Add(item);
        }
        var finalOutputAdmissionAttempts =
            recoveryState?.FinalOutputAdmissionAttempts ?? 0;
        if (finalOutputAdmission is not null)
        {
            EnsureFinalOutputAttemptsRemain(
                finalOutputAdmissionAttempts);
        }
        IReadOnlyList<ToolActivationRecord> toolActivations =
            initialToolActivations.Select(item => item.Clone()).ToArray();
        var toolLoopGuard = SemanticToolLoopGuard.Rebuild(
            _options.ToolLoopGuard,
            transcript);

        while (true)
        {
            if (directExecution && run.Usage.Turns >= 1)
            {
                return await FailOrCancelAsync(
                        run,
                        transcript,
                        new InvalidOperationException(
                            "A direct run cannot start a second model turn."),
                        startedAt,
                        deadline.ExternalCancellationRequested)
                    .ConfigureAwait(false);
            }

            if (toolLoopGuard.Decision?.HardStop == true)
            {
                return await CompleteToolLoopFailureAsync(
                        run,
                        transcript,
                        toolLoopGuard,
                        startedAt)
                    .ConfigureAwait(false);
            }

            if (run.Usage.HasUnaccountedUsage)
            {
                return await FailUnaccountedUsageAsync(
                        run,
                        transcript,
                        startedAt)
                    .ConfigureAwait(false);
            }

            if (deadline.IsExpired)
            {
                await CompleteBudgetAsync(
                        run,
                        "max_duration",
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var boundary = await DrainControlsAsync(
                    run,
                    control,
                    cancellationToken)
                .ConfigureAwait(false);
            pendingContext.AddRange(boundary.Context);
            if (boundary.Cancel)
            {
                await CompleteControlAsync(
                        run,
                        RunControlKinds.Cancel,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            if (boundary.Interrupt)
            {
                await CompleteControlAsync(
                        run,
                        RunControlKinds.Interrupt,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var budget = new BudgetTracker(run.Budget, startedAt);
            var turnDecision = budget.CanStartTurn(run.Usage, _clock.UtcNow);
            if (!turnDecision.Allowed)
            {
                await CompleteBudgetAsync(
                        run,
                        turnDecision.Reason!,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var turnId = _ids.NewId("turn");
            var attemptId = _ids.NewId("turn-attempt");
            RuntimeMemoryRecallSelection? memoryRecall = null;
            if (_memory is not null)
            {
                var memoryRecallStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                var memoryRecallOutcome = RuntimeMetricOutcomes.Failure;
                try
                {
                    using var step = control.BeginStep(cancellationToken);
                    if (step.PendingControlAtStart)
                    {
                        throw new OperationCanceledException(
                            "A queued run control superseded memory recall.",
                            step.CancellationToken);
                    }

                    memoryRecall = await _memory.RecallAsync(
                            run,
                            turnId,
                            transcript,
                            pendingContext,
                            Math.Max(
                                0,
                                _contextCompiler.MaxCandidates
                                - pendingContext.Count),
                            step.CancellationToken)
                        .ConfigureAwait(false);
                    memoryRecallOutcome = RuntimeMetricOutcomes.Success;
                }
                catch (OperationCanceledException)
                {
                    memoryRecallOutcome = RuntimeMetricOutcomes.Canceled;
                    var interrupted = await DrainControlsAsync(
                            run,
                            control,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    pendingContext.AddRange(interrupted.Context);
                    if (deadline.IsExpired)
                    {
                        await CompleteBudgetAsync(
                                run,
                                "max_duration",
                                startedAt,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        return Outcome(run, transcript);
                    }

                    if (interrupted.Steer
                        && !interrupted.Cancel
                        && !interrupted.Interrupt
                        && !cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (interrupted.Cancel
                        || interrupted.Interrupt
                        || cancellationToken.IsCancellationRequested)
                    {
                        await CompleteControlAsync(
                                run,
                                interrupted.Interrupt
                                    ? RunControlKinds.Interrupt
                                    : RunControlKinds.Cancel,
                                startedAt,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        return Outcome(run, transcript);
                    }

                    throw;
                }
                finally
                {
                    _metrics.Record(
                        RuntimeMetricNames.MemoryRecallMilliseconds,
                        RuntimeMetricKind.Histogram,
                        RuntimeMetricsEmitter.ElapsedMilliseconds(
                            memoryRecallStarted),
                        workloadClass,
                        memoryRecallOutcome);
                }
            }

            activeSkillState = ReconcileActiveSkillState(
                    effectiveActiveSkills,
                    activeSkillState,
                    skillSnapshot)
                .ToList();
            var activeSkillStateExtension =
                SkillActivationStateCodec.Encode(activeSkillState);
            var hasDurableSkillState =
                run.Extensions.TryGetValue(
                    SkillActivationStateCodec.ExtensionName,
                    out var priorSkillStateExtension);
            var durableSkillStateChanged =
                hasDurableSkillState
                    ? !string.Equals(
                        ProtocolJson.Serialize(priorSkillStateExtension),
                        ProtocolJson.Serialize(activeSkillStateExtension),
                        StringComparison.Ordinal)
                    : activeSkillState.Count > 0;
            if (durableSkillStateChanged
                && !allowSkillStateReplacement)
            {
                throw new InvalidDataException(
                    "Active skill state changed outside an authorized "
                    + "durable progression.");
            }
            var toolDisclosure = _toolDisclosure.Evaluate(
                run,
                turnId,
                toolSnapshot,
                toolActivations,
                _options.ToolDisclosureLimits);
            var skillAdmission = _skillAdmission.Evaluate(
                run,
                turnId,
                skillSnapshot,
                toolSnapshot,
                toolDisclosure,
                effectiveActiveSkills,
                _options.SkillDisclosureBudget);
            toolDisclosure.FinalizeSkillActivations();
            var disclosure = skillSnapshot.CreateDisclosure(
                effectiveActiveSkills,
                _options.SkillDisclosureBudget,
                skillAdmission.CatalogReferences);
            var skillRuntime = new SkillRuntimePlan(
                skillSnapshot,
                skillAdmission.CatalogReferences,
                activeSkillState,
                _options.SkillRuntimeLimits);
            var resolvedSkillContent = await _skillContent.ResolveAsync(
                    run,
                    turnId,
                    disclosure.Activated,
                    cancellationToken)
                .ConfigureAwait(false);
            var promptAssemblyStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            var compilationContext = memoryRecall is null
                || memoryRecall.Candidates.Count == 0
                ? pendingContext.ToArray()
                : pendingContext
                    .Concat(memoryRecall.Candidates)
                    .ToArray();
            var ephemeralMemoryIds = memoryRecall?.Candidates
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            var compiled = _contextCompiler.Compile(
                new ContextCompilationRequest(
                    run.RunId,
                    turnId,
                    compilationContext,
                    _clock.UtcNow,
                    disclosure,
                    _contextCompiler.MaxCandidates,
                    deadline.Token));
            RetainDeferredContext(
                pendingContext,
                contextDeferralTurns,
                compiled.BudgetReport,
                ephemeralMemoryIds);

            var preparedTranscript = transcript.ToList();
            preparedTranscript.RemoveAll(
                SemanticToolLoopGuard.IsWarningMessage);
            preparedTranscript.RemoveAll(IsSkillContentMessage);
            var currentUserMessageId = preparedTranscript
                .LastOrDefault(
                    message => string.Equals(
                                   message.Role,
                                   NormalizedRoles.User,
                                   StringComparison.Ordinal)
                               && !IsRuntimeContextMessage(message)
                               && !IsSkillContentMessage(message))
                ?.MessageId;
            var promptMessages = new List<NormalizedMessage>(4);
            var disclosureChanged = !string.Equals(
                    disclosedSkillDigest,
                    skillAdmission.DisclosureDigest,
                    StringComparison.Ordinal);
            if (disclosureChanged)
            {
                preparedTranscript.RemoveAll(IsSkillDisclosureMessage);
                var skillMessage = RuntimePromptBuilder.SkillMessage(
                    _ids.NewId("skill-message"),
                    disclosure,
                    _clock.UtcNow);
                preparedTranscript.Add(skillMessage);
                promptMessages.Add(skillMessage);
            }

            if (resolvedSkillContent.HasReferences)
            {
                var skillContentMessage =
                    RuntimePromptBuilder.SkillContentMessage(
                        _ids.NewId("skill-content-message"),
                        resolvedSkillContent,
                        _clock.UtcNow);
                preparedTranscript.Add(skillContentMessage);
                promptMessages.Add(skillContentMessage);
            }

            if (compiled.BudgetReport.InputCount > 0)
            {
                var contextMessage = RuntimePromptBuilder.ContextMessage(
                    _ids.NewId("context-message"),
                    compiled,
                    _clock.UtcNow);
                preparedTranscript.Add(contextMessage);
                promptMessages.Add(contextMessage);
            }

            var loopWarning = toolLoopGuard.CreateWarningMessage();
            if (loopWarning is not null)
            {
                preparedTranscript.Add(loopWarning);
                promptMessages.Add(loopWarning);
            }

            IReadOnlyList<ToolDescriptor> directTools =
                SkillRuntimePlan.MergeProviderTools(
                toolDisclosure.EffectiveProviderTools,
                skillRuntime.ControlTools);
            if (finalOutputAdmission is not null)
            {
                directTools = directTools
                    .Concat(
                        new[]
                        {
                            FinalOutputAdmissionCodec.CreateSubmitDescriptor(
                                _options.FinalOutputAdmission,
                                finalOutputAdmission.Contract)
                        })
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            var effectiveToolDigest =
                SkillRuntimePlan.ComputeProviderToolDigest(directTools);
            var providerPrompt = preparedTranscript
                .Where(IsSkillDisclosureMessage)
                .Concat(
                    preparedTranscript.Where(
                        message => !IsSkillDisclosureMessage(message)))
                .ToArray();
            var stablePrefix = providerPrompt
                .TakeWhile(IsSkillDisclosureMessage)
                .ToArray();
            var contextInput = providerPrompt
                .Select(
                    message => NormalizedMessageJournalCodec
                        .CloneValidated(message))
                .ToArray();
            var stablePrefixIds = stablePrefix
                .Select(message => message.MessageId)
                .Concat(
                    promptMessages.Select(
                        message => message.MessageId))
                .Concat(
                    currentUserMessageId is null
                        ? Array.Empty<string>()
                        : new[] { currentUserMessageId })
                .ToArray();
            var contextView = await _conversationContext.PrepareAsync(
                    run.RunId,
                    turnId,
                    contextInput,
                    stablePrefixIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (contextView is null)
            {
                throw new InvalidDataException(
                    "The conversation context engine returned no view.");
            }
            providerPrompt = contextView.Messages.ToArray();
            stablePrefix = providerPrompt
                .TakeWhile(IsSkillDisclosureMessage)
                .ToArray();
            var prompt = RuntimePromptBuilder.MeasurePrompt(
                providerPrompt,
                directTools,
                _options.MaxTranscriptMessages,
                _options.MaxPromptUtf8Bytes,
                _options.EstimatedPromptBytesPerToken,
                _tokenEstimator);
            _metrics.Record(
                RuntimeMetricNames.PromptAssemblyMilliseconds,
                RuntimeMetricKind.Histogram,
                RuntimeMetricsEmitter.ElapsedMilliseconds(
                    promptAssemblyStarted),
                workloadClass,
                RuntimeMetricOutcomes.Success);
            _metrics.Record(
                RuntimeMetricNames.PromptUtf8Bytes,
                RuntimeMetricKind.Histogram,
                prompt.Utf8Bytes,
                workloadClass,
                RuntimeMetricOutcomes.Success);
            var remainingTokens = run.Budget.MaxTokens
                                  - ((long)run.Usage.InputTokens
                                     + run.Usage.OutputTokens);
            var remainingOutputTokens =
                remainingTokens - prompt.EstimatedTokens;
            if (remainingOutputTokens < 1)
            {
                await CompleteBudgetAsync(
                        run,
                        "max_tokens",
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var maxOutputTokens = (int)Math.Min(
                int.MaxValue,
                remainingOutputTokens);
            var primaryRoute = routePlan.PrimaryRouteIdentity;
            var promptDigest = RuntimePromptBuilder.TranscriptDigest(
                providerPrompt,
                effectiveToolDigest,
                skillSnapshot);
            var memoryEvidence = _memory is null
                ? JsonArrayBuilder.Object(
                    ("memoryConfigured", JsonArrayBuilder.Boolean(false)))
                : memoryRecall?.Evidence
                  ?? RuntimeMemoryRecallSelection.Empty(
                      _memory.PolicyId,
                      _memory.PolicyVersion).Evidence;
            var providerCacheKey = new ProviderCacheKey(
                _options.PromptLayoutVersion,
                RuntimePromptBuilder.StablePrefixDigest(
                    stablePrefix,
                    _options.PromptLayoutVersion),
                effectiveToolDigest,
                skillAdmission.DisclosureDigest,
                primaryRoute.RouteDigest,
                CanonicalJsonDigest.ComputeSha256(memoryEvidence),
                ProviderCacheCompactionDigest(contextView.Report),
                ProviderCacheDynamicRequestDigest(
                    promptDigest,
                    providerOpaqueContinuationState));
            var providerCacheDecision = ProviderCacheTelemetry.Evaluate(
                previousProviderCacheKey,
                providerCacheKey);
            var snapshot = new TurnSnapshot
            {
                TurnId = turnId,
                RunId = run.RunId,
                RuntimeGeneration = run.RuntimeGeneration,
                ProviderId = primaryRoute.ProviderId,
                ModelId = string.Equals(
                    primaryRoute.ModelId,
                    "unspecified",
                    StringComparison.Ordinal)
                    ? _options.ModelId
                    : primaryRoute.ModelId,
                PromptLayoutVersion = _options.PromptLayoutVersion,
                StablePrefixHash = providerCacheKey.StablePrefixDigest,
                SkillGeneration = skillSnapshot.Generation,
                SkillDigests = disclosure.Activated
                    .Select(item => item.ContentDigest)
                    .ToList(),
                ToolCatalogGeneration = toolSnapshot.Generation,
                DirectToolDigest = effectiveToolDigest,
                DeferredCatalogDigest =
                    toolDisclosure.AuthorizedHiddenTools.Count == 0
                    ? null
                    : toolDisclosure.DeferredOnlyDigest,
                ContextPolicyVersion = _options.ContextPolicyVersion,
                BudgetPolicyVersion = _options.BudgetPolicyVersion,
                MaxSideEffectToolCallsPerTurn =
                    _options.MaxSideEffectToolCallsPerTurn,
                CreatedAt = _clock.UtcNow,
                Extensions = new Dictionary<string, JsonElement>(
                    StringComparer.Ordinal)
                {
                    ["skillAdmission"] =
                        skillAdmission.ToSnapshotExtension(),
                    [SkillActivationStateCodec.ExtensionName] =
                        activeSkillStateExtension.Clone(),
                    ["skillContentResolution"] =
                        resolvedSkillContent.Evidence,
                    ["toolDisclosure"] =
                        toolDisclosure.ToSnapshotExtension(),
                    ["contextBudget"] =
                        ProtocolJson.ToElement(compiled.BudgetReport),
                    ["conversationContext"] =
                        contextView.Report.ToSnapshotExtension(),
                    ["promptMeasurement"] =
                        RuntimePromptBuilder.PromptMeasurementEvidence(prompt),
                    ["promptDigest"] =
                        JsonArrayBuilder.String(promptDigest),
                    ["stablePrefixMessageCount"] =
                        JsonArrayBuilder.Number(stablePrefix.Length),
                    ["providerWorkloadClass"] =
                        JsonArrayBuilder.String(workloadClass),
                    [ProviderCacheTelemetry.KeyExtensionName] =
                        providerCacheKey.ToJson(),
                    [ProviderCacheTelemetry.DecisionExtensionName] =
                        providerCacheDecision.ToJson()
                }
            };
            if (_memory is not null)
            {
                snapshot.Extensions[
                        RuntimeMemoryAgentLoop.PolicySnapshotExtension] =
                    _memory.PolicyEvidence;
                snapshot.Extensions[
                        RuntimeMemoryAgentLoop.RecallSnapshotExtension] =
                    memoryRecall?.Evidence
                    ?? RuntimeMemoryRecallSelection.Empty(
                        _memory.PolicyId,
                        _memory.PolicyVersion).Evidence;
            }
            if (finalOutputAdmission is not null)
            {
                snapshot.Extensions[
                        FinalOutputAdmissionBinding
                            .TurnSnapshotExtensionName] =
                    finalOutputAdmission.ToJson();
            }

            if (hasDurableSkillState || activeSkillState.Count > 0)
            {
                run.Extensions[SkillActivationStateCodec.ExtensionName] =
                    activeSkillStateExtension.Clone();
            }
            TryAttachConversationContextCheckpoint(snapshot, contextView);
            ProtocolValidator.EnsureValid(snapshot);
            await _journal.CommitTurnPreparationAsync(
                    run,
                    turnId,
                    attemptId,
                    promptMessages,
                    snapshot,
                    startedAt,
                    cancellationToken,
                    toolDisclosure: toolDisclosure.StateChanged
                        ? toolDisclosure.ToJournalRecord(
                            toolDisclosure.StateReasonCodes)
                        : null,
                    checkpointReasonCode: durableSkillStateChanged
                        ? SkillRuntimeReasonCodes.ReplacedByContinuation
                        : null)
                .ConfigureAwait(false);
            allowSkillStateReplacement = false;
            previousProviderCacheKey = providerCacheKey;
            toolActivations = toolDisclosure.RequestedActivations;
            transcript.Clear();
            transcript.AddRange(preparedTranscript);
            if (disclosureChanged)
            {
                disclosedSkillDigest = skillAdmission.DisclosureDigest;
            }

            ProviderAttemptResult response;
            try
            {
                using var step = control.BeginStep(cancellationToken);
                if (step.PendingControlAtStart)
                {
                    throw new OperationCanceledException(
                        "A queued run control superseded provider dispatch.",
                        step.CancellationToken);
                }
                ProviderWorkloadAdmission.Lease? providerLease = null;
                try
                {
                    if (_lifecycle.Count > 0)
                    {
                        await _lifecycle.InvokeAsync(
                                new ModelDispatchingLifecycleEvent(
                                    run.RunId,
                                    turnId,
                                    promptDigest,
                                    providerPrompt.Length,
                                    routePlan.RouteIdentities
                                        .Select(route => route.ProviderId)
                                        .ToArray(),
                                    directTools
                                        .Select(tool => tool.Name)
                                        .ToArray(),
                                    inference),
                                allowRejection: true,
                                step.CancellationToken)
                            .ConfigureAwait(false);
                    }

                    step.CancellationToken.ThrowIfCancellationRequested();
                    providerLease = await _providerAdmission.AcquireAsync(
                            workloadClass,
                            step.CancellationToken)
                        .ConfigureAwait(false);
                    var streamStarted =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                    var streamOutcome = RuntimeMetricOutcomes.Failure;
                    var firstTokenRecorded = 0;
                    try
                    {
                        response = await _provider.RunAsync(
                                run.RunId,
                                runAttemptId,
                                turnId,
                                providerPrompt,
                                directTools,
                                fence,
                                async item =>
                                {
                                    if ((string.Equals(
                                                item.Kind,
                                                ModelStreamEventKinds.TextDelta,
                                                StringComparison.Ordinal)
                                         || string.Equals(
                                                item.Kind,
                                                ModelStreamEventKinds
                                                    .ReasoningDelta,
                                                StringComparison.Ordinal)
                                         || string.Equals(
                                                item.Kind,
                                                ModelStreamEventKinds
                                                    .ToolCallDelta,
                                                StringComparison.Ordinal))
                                        && Interlocked.CompareExchange(
                                            ref firstTokenRecorded,
                                            1,
                                            0) == 0)
                                    {
                                        _metrics.Record(
                                            RuntimeMetricNames
                                                .ProviderTimeToFirstTokenMilliseconds,
                                            RuntimeMetricKind.Histogram,
                                            RuntimeMetricsEmitter
                                                .ElapsedMilliseconds(
                                                    streamStarted),
                                            workloadClass,
                                            RuntimeMetricOutcomes.Success);
                                    }

                                    await PublishProviderEventAsync(run, item)
                                        .ConfigureAwait(false);
                                },
                                step.CancellationToken,
                                 onLifecycleNotice: notice => PublishProviderLifecycle(
                                     run,
                                     notice),
                                 estimatedPromptTokens: prompt.EstimatedTokens,
                                 maxOutputTokens: maxOutputTokens,
                                 onDetachedCleanup: TrackDetachedProviderCleanup,
                                 onUsage: notice =>
                                     ChargeAndEnforceProviderUsageAsync(
                                         run,
                                        notice,
                                        budget,
                                        startedAt,
                                        turnId),
                                onUsageUncertain: notice =>
                                    MarkProviderUsageUncertainAsync(
                                        run,
                                        notice,
                                        startedAt,
                                        turnId),
                                onDispatch: notice =>
                                    MarkProviderDispatchAsync(
                                        run,
                                        notice,
                                        startedAt,
                                        turnId),
                                onDispatchKnownZero: notice =>
                                    MarkProviderDispatchKnownZeroAsync(
                                        run,
                                        notice,
                                        startedAt,
                                        turnId),
                                onResultDiscarded: notice =>
                                    MarkProviderResultDiscardedAsync(
                                        run,
                                        notice,
                                        startedAt,
                                        turnId),
                                opaqueContinuationState:
                                    providerOpaqueContinuationState,
                                routePlan: routePlan,
                                inference: inference)
                            .ConfigureAwait(false);
                        if (_lifecycle.Count > 0)
                        {
                            await _lifecycle.InvokeAsync(
                                    new ModelCompletedLifecycleEvent(
                                        run.RunId,
                                        turnId,
                                        response),
                                    allowRejection: false,
                                    CancellationToken.None,
                                    enforceRequired: false)
                                .ConfigureAwait(false);
                        }
                        streamOutcome = RuntimeMetricOutcomes.Success;
                    }
                    catch (OperationCanceledException)
                    {
                        streamOutcome = RuntimeMetricOutcomes.Canceled;
                        throw;
                    }
                    finally
                    {
                        _metrics.Record(
                            RuntimeMetricNames
                                .ProviderStreamDurationMilliseconds,
                            RuntimeMetricKind.Histogram,
                            RuntimeMetricsEmitter.ElapsedMilliseconds(
                                streamStarted),
                            workloadClass,
                            streamOutcome);
                    }
                }
                finally
                {
                    providerLease?.Dispose();
                }
            }
            catch (ProviderBudgetExceededException exception)
            {
                fence.Invalidate();
                await CompleteBudgetAsync(
                        run,
                        exception.Reason,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }
            catch (ProviderException exception) when (
                string.Equals(
                    exception.Code,
                    "provider_token_budget_exhausted",
                    StringComparison.Ordinal))
            {
                fence.Invalidate();
                await CompleteBudgetAsync(
                        run,
                        "max_tokens",
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }
            catch (OperationCanceledException)
            {
                fence.Invalidate();
                if (deadline.IsExpired)
                {
                    await CompleteBudgetAsync(
                            run,
                            "max_duration",
                            startedAt,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return Outcome(run, transcript);
                }

                var interrupted = await DrainControlsAsync(
                        run,
                        control,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                pendingContext.AddRange(interrupted.Context);
                if (!interrupted.Cancel
                    && !interrupted.Interrupt
                    && !interrupted.Steer
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderException(
                        "provider_cancelled_unexpectedly",
                        "network",
                        "The provider operation was cancelled unexpectedly.",
                        false);
                }

                if (interrupted.Steer
                    && !interrupted.Cancel
                    && !interrupted.Interrupt
                    && !cancellationToken.IsCancellationRequested)
                {
                    if (run.Usage.HasUnaccountedUsage)
                    {
                        return await FailUnaccountedUsageAsync(
                                run,
                                transcript,
                                startedAt)
                            .ConfigureAwait(false);
                    }

                    await CompleteTurnWithoutStateChangeAsync(
                            run,
                            turnId,
                            attemptId,
                            startedAt,
                            "steered",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                await CompleteControlAsync(
                        run,
                        interrupted.Interrupt
                            ? RunControlKinds.Interrupt
                            : RunControlKinds.Cancel,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var afterProvider = await DrainControlsAsync(
                    run,
                    control,
                    CancellationToken.None)
                .ConfigureAwait(false);
            pendingContext.AddRange(afterProvider.Context);
            var discardReason = ProviderResponseDiscardReason(
                deadline,
                afterProvider);
            if (discardReason is not null)
            {
                fence.Invalidate();
                await MarkProviderResultDiscardedAsync(
                        run,
                        new ProviderResultDiscardedNotice
                        {
                            ProviderId = response.ProviderId,
                            ProviderAttemptId = response.ProviderAttemptId,
                            StreamAttemptId = response.StreamAttemptId,
                            ReasonCode = discardReason
                        },
                        startedAt,
                        turnId)
                    .ConfigureAwait(false);
                if (deadline.IsExpired)
                {
                    await CompleteBudgetAsync(
                            run,
                            "max_duration",
                            startedAt,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return Outcome(run, transcript);
                }

                var cancel = deadline.ExternalCancellationRequested
                             || afterProvider.Cancel;
                if (afterProvider.Steer
                    && !cancel
                    && !afterProvider.Interrupt)
                {
                    await CompleteTurnWithoutStateChangeAsync(
                            run,
                            turnId,
                            attemptId,
                            startedAt,
                            "steered",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                await CompleteControlAsync(
                        run,
                        cancel
                            ? RunControlKinds.Cancel
                            : RunControlKinds.Interrupt,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            var guardTranscriptStart = transcript.Count;
            var responseRoute = response.RouteIdentity
                                ?? throw new InvalidDataException(
                                    "A provider result is missing its route "
                                    + "identity.");
            var nextProviderOpaqueContinuationState =
                response.OpaqueContinuationState?.Snapshot();
            if (nextProviderOpaqueContinuationState is not null
                && !nextProviderOpaqueContinuationState.Matches(
                    responseRoute))
            {
                throw new InvalidDataException(
                    "A provider continuation does not match its result "
                    + "route.");
            }

            var durableProviderOpaqueContinuationState =
                _options
                    .AllowProviderDeclaredNonSecretContinuationPersistence
                && nextProviderOpaqueContinuationState
                    ?.IsDurableNonSecret == true
                    ? nextProviderOpaqueContinuationState
                    : null;
            var assistant = NormalizedTranscript.AssistantResponse(
                _ids.NewId("assistant-message"),
                response.Text,
                response.ReasoningContent,
                response.ToolCalls,
                _clock.UtcNow);
            AnnotateToolCallEvidence(
                assistant,
                toolDisclosure,
                directTools);

            if (response.ToolCalls.Count == 0)
            {
                if (afterProvider.FollowUp)
                {
                    await _journal.CommitProviderResultAsync(
                            run,
                            assistant,
                            turnId,
                            response.ProviderId,
                            response.ProviderAttemptId,
                            response.StreamAttemptId,
                            CancellationToken.None,
                            routeIdentity: responseRoute,
                            opaqueContinuationState:
                                durableProviderOpaqueContinuationState,
                            finalOutputPresentation:
                                finalOutputAdmission is null
                                    ? null
                                    : FinalOutputAdmissionCodec
                                        .CreatePresentation(
                                            FinalOutputAdmissionCodec
                                                .ProvisionalPresentationState,
                                            "provider_follow_up"))
                        .ConfigureAwait(false);
                    providerOpaqueContinuationState =
                        nextProviderOpaqueContinuationState;
                    transcript.Add(assistant);
                    await CommitMemoryForReceiptsAsync(
                            run,
                            turnId,
                            response.ProviderAttemptId,
                            ProviderMemorySources(
                                run,
                                turnId,
                                response.StreamAttemptId,
                                includeAssistantOutput: false),
                            Array.Empty<ActionReceipt>(),
                            transcript,
                            assistant,
                            null,
                            deadline.Token)
                        .ConfigureAwait(false);
                    await CompleteTurnWithoutStateChangeAsync(
                            run,
                            turnId,
                            attemptId,
                            startedAt,
                            "follow_up",
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (finalOutputAdmission is not null)
                {
                    finalOutputAdmissionAttempts = checked(
                        finalOutputAdmissionAttempts + 1);
                    await CommitMissingFinalOutputSubmissionAsync(
                            run,
                            transcript,
                            assistant,
                            turnId,
                            attemptId,
                            response,
                            responseRoute,
                            durableProviderOpaqueContinuationState,
                            startedAt,
                            deadline,
                            finalOutputAdmissionAttempts)
                        .ConfigureAwait(false);
                    providerOpaqueContinuationState =
                        nextProviderOpaqueContinuationState;
                    toolLoopGuard.ObserveMessages(
                        transcript.Skip(guardTranscriptStart).ToArray());
                    EnsureFinalOutputAttemptsRemain(
                        finalOutputAdmissionAttempts);
                    continue;
                }

                var finalOutput = RuntimePromptBuilder.FinalOutput(response.Text!);
                if (_memory is null)
                {
                    await _journal.CommitFinalCompletionAsync(
                            run,
                            assistant,
                            finalOutput,
                            turnId,
                            response.ProviderId,
                            response.ProviderAttemptId,
                            response.StreamAttemptId,
                            startedAt,
                            CancellationToken.None,
                            routeIdentity: responseRoute)
                        .ConfigureAwait(false);
                    transcript.Add(assistant);
                    return Outcome(run, transcript, finalOutput);
                }

                await _journal.CommitProviderResultAndOutputAsync(
                        run,
                        assistant,
                        finalOutput,
                        turnId,
                        response.ProviderId,
                        response.ProviderAttemptId,
                        response.StreamAttemptId,
                        CancellationToken.None,
                        routeIdentity: responseRoute)
                    .ConfigureAwait(false);
                transcript.Add(assistant);
                try
                {
                    await CommitMemoryForReceiptsAsync(
                            run,
                            turnId,
                            response.ProviderAttemptId,
                            ProviderMemorySources(
                                run,
                                turnId,
                                response.StreamAttemptId,
                                includeAssistantOutput: true),
                            Array.Empty<ActionReceipt>(),
                            transcript,
                            assistant,
                            finalOutput,
                            deadline.Token)
                        .ConfigureAwait(false);
                    await _journal.CommitRecoveredCompletionAsync(
                            run,
                            turnId,
                            response.ProviderAttemptId,
                            startedAt,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsExpired)
                {
                    var outcome = await CompleteDurationDeadlineAsync(
                            run,
                            transcript,
                            startedAt)
                        .ConfigureAwait(false);
                    outcome.FinalOutput = finalOutput.Clone();
                    return outcome;
                }
                catch (Exception exception) when (
                    exception is not DuplicateRunException)
                {
                    var outcome = await FailOrCancelAsync(
                            run,
                            transcript,
                            exception,
                            startedAt,
                            deadline.ExternalCancellationRequested)
                        .ConfigureAwait(false);
                    outcome.FinalOutput = finalOutput.Clone();
                    return outcome;
                }

                return Outcome(run, transcript, finalOutput);
            }

            if (finalOutputAdmission is not null
                && response.ToolCalls.Any(
                    call => string.Equals(
                        call.Name,
                        FinalOutputAdmissionControl.SubmitToolName,
                        StringComparison.Ordinal)))
            {
                finalOutputAdmissionAttempts = checked(
                    finalOutputAdmissionAttempts + 1);
                var admitted = await ProcessFinalOutputSubmissionAsync(
                        run,
                        transcript,
                        assistant,
                        response,
                        responseRoute,
                        durableProviderOpaqueContinuationState,
                        turnId,
                        attemptId,
                        startedAt,
                        deadline,
                        compiled.Selected
                            .Select(item => item.Candidate)
                            .ToArray(),
                        finalOutputAdmission,
                        finalOutputEvidence,
                        finalOutputAdmissionAttempts)
                    .ConfigureAwait(false);
                providerOpaqueContinuationState =
                    nextProviderOpaqueContinuationState;
                if (admitted is not null)
                {
                    return admitted;
                }

                toolLoopGuard.ObserveMessages(
                    transcript.Skip(guardTranscriptStart).ToArray());
                EnsureFinalOutputAttemptsRemain(
                    finalOutputAdmissionAttempts);
                continue;
            }

            var prepared = PrepareToolCalls(
                run,
                turnId,
                attemptId,
                response.ToolCalls,
                toolDisclosure,
                skillRuntime);
            prepared = ApplySideEffectToolCallLimit(
                prepared,
                snapshot.MaxSideEffectToolCallsPerTurn,
                run.Usage.Actions);
            await _journal.CommitProviderResultAsync(
                    run,
                    assistant,
                    turnId,
                    response.ProviderId,
                    response.ProviderAttemptId,
                    response.StreamAttemptId,
                    CancellationToken.None,
                    routeIdentity: responseRoute,
                    opaqueContinuationState:
                        durableProviderOpaqueContinuationState,
                    finalOutputPresentation:
                        finalOutputAdmission is null
                            ? null
                            : FinalOutputAdmissionCodec
                                .CreatePresentation(
                                    FinalOutputAdmissionCodec
                                        .ProvisionalPresentationState,
                                    "final_output_not_submitted"))
                .ConfigureAwait(false);
            providerOpaqueContinuationState =
                nextProviderOpaqueContinuationState;
            transcript.Add(assistant);
            var valid = prepared
                .Where(item => item.Execution is not null)
                .Select(item => item.Execution!)
                .ToArray();
            var toolMessages = prepared
                .Where(item => item.ImmediateMessage is not null)
                .ToDictionary(
                    item => item.ToolCall.ToolCallId,
                    item => item.ImmediateMessage!,
                    StringComparer.Ordinal);
            var actionDecision = budget.CheckShared(run.Usage, _clock.UtcNow);
            if (!actionDecision.Allowed
                || (long)run.Usage.Actions + valid.Length > run.Budget.MaxActions)
            {
                await CompleteBudgetAsync(
                        run,
                        actionDecision.Allowed
                            ? "max_actions"
                            : actionDecision.Reason!,
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            if (valid.Length == 0)
            {
                toolActivations = await AppendPreparedToolMessagesAsync(
                        run,
                        transcript,
                        prepared,
                        toolMessages,
                        toolDisclosure,
                        skillRuntime,
                        skillSnapshot,
                        toolSnapshot,
                        effectiveActiveSkills,
                        activeSkillState,
                        turnId,
                        attemptId,
                        cancellationToken)
                    .ConfigureAwait(false);
                toolLoopGuard.ObserveMessages(
                    transcript.Skip(guardTranscriptStart).ToArray());

                await CompleteTurnWithoutStateChangeAsync(
                        run,
                        turnId,
                        attemptId,
                        startedAt,
                        prepared.Any(item => item.Activation is not null
                                             || item.SkillActivation is not null
                                             || ToolDisclosureControlNames
                                                 .IsReserved(
                                                     item.ToolCall.Name)
                                             || SkillRuntimeControlNames
                                                 .IsReserved(
                                                     item.ToolCall.Name))
                            ? "tool_controls"
                            : "tool_errors",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var invocations = new Dictionary<string, ToolInvocation>(
                valid.Length,
                StringComparer.Ordinal);
            var committedReceipts = new List<ActionReceipt>(valid.Length);
            foreach (var call in valid)
            {
                var invocation = call.Invocation();
                ProtocolValidator.EnsureValid(invocation);
                invocations.Add(call.ToolCallId, invocation);
            }

            var plan = _toolPlanner.Plan(valid);
            var toolQueuedAt =
                System.Diagnostics.Stopwatch.GetTimestamp();
            using var schedulerReservation =
                _toolScheduler.ReserveExecution(plan);
            _metrics.Record(
                RuntimeMetricNames.ToolQueueDepth,
                RuntimeMetricKind.Gauge,
                _toolScheduler.QueuedCalls);
            var actionRequests = new Dictionary<string, ActionRequest>(
                StringComparer.Ordinal);
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in valid)
            {
                var request = ActionRequestFor(
                    run,
                    call,
                    RunDeadlineAt(startedAt, run.Budget.MaxDurationMs));
                ProtocolValidator.EnsureValid(request);
                if (!operationIds.Add(request.OperationId))
                {
                    throw new InvalidOperationException(
                        "The runtime generated a duplicate operation id.");
                }

                actionRequests.Add(call.ToolCallId, request);
            }

            if (_lifecycle.Count > 0)
            {
                await _lifecycle.InvokeAsync(
                        new ToolBatchDispatchingLifecycleEvent(
                            run.RunId,
                            turnId,
                            valid
                                .Select(
                                    call => new ToolLifecycleCall(
                                        call.ToolCallId,
                                        call.Tool.Name,
                                        call.Tool.Effect,
                                        call.Arguments))
                                .ToArray()),
                        allowRejection: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var call in valid)
            {
                await _journal.AppendBuiltInDurableAsync(
                        run,
                        RuntimeEventKinds.ToolStarted,
                        ProtocolJson.ToElement(
                            invocations[call.ToolCallId]),
                        turnId,
                        attemptId,
                        eventId: "tool-start:" + call.ToolCallId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await _journal.CommitTransitionAsync(
                    run,
                    RunStates.WaitingForAction,
                    RuntimeEventKinds.RunCheckpoint,
                    turnId: turnId,
                    attemptId: attemptId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await _journal.AppendActionRequestsAsync(
                    run,
                    valid
                        .Select(call => actionRequests[call.ToolCallId])
                        .ToArray(),
                    attemptId,
                    cancellationToken)
                .ConfigureAwait(false);

            await _journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.BudgetUpdated,
                    next =>
                    {
                        next.Usage.Actions = checked(
                            next.Usage.Actions + valid.Length);
                        UpdateDuration(next, startedAt);
                    },
                    turnId,
                    attemptId,
                    cancellationToken)
                .ConfigureAwait(false);

            var executor = new HostToolExecutor(
                _host,
                actionRequests,
                run,
                requireAudienceIncarnation:
                    _options
                        .RequireAudienceIncarnationForRestrictedObservations,
                progressPublisher: (action, progress) =>
                    PublishToolProgress(
                        run,
                        action,
                        progress,
                        attemptId));
            IReadOnlyList<ToolExecutionResult>? executionResults = null;
            var executionCancelled = false;
            var controlQueuedBeforeDispatch = false;
            var toolQueueWaitRecorded = false;
            try
            {
                using var step = control.BeginStep(cancellationToken);
                if (step.PendingControlAtStart)
                {
                    executionCancelled = true;
                    controlQueuedBeforeDispatch = true;
                }
                else
                {
                    _metrics.Record(
                        RuntimeMetricNames.ToolQueueWaitMilliseconds,
                        RuntimeMetricKind.Histogram,
                        RuntimeMetricsEmitter.ElapsedMilliseconds(
                            toolQueuedAt),
                        workloadClass,
                        RuntimeMetricOutcomes.Success);
                    toolQueueWaitRecorded = true;
                    var toolExecutionStarted =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                    var toolExecutionOutcome =
                        RuntimeMetricOutcomes.Failure;
                    try
                    {
                        executionResults = await _toolScheduler
                            .ExecuteReservedAsync(
                                plan,
                                executor,
                                _clock,
                                schedulerReservation,
                                step.CancellationToken,
                                step.TryAcquireDispatchPermit)
                            .ConfigureAwait(false);
                        step.CancellationToken.ThrowIfCancellationRequested();
                        toolExecutionOutcome =
                            executionResults.All(item => item.IsSuccess)
                                ? RuntimeMetricOutcomes.Success
                                : RuntimeMetricOutcomes.Failure;
                    }
                    catch (OperationCanceledException)
                    {
                        toolExecutionOutcome =
                            RuntimeMetricOutcomes.Canceled;
                        throw;
                    }
                    finally
                    {
                        _metrics.Record(
                            RuntimeMetricNames.ToolExecutionMilliseconds,
                            RuntimeMetricKind.Histogram,
                            RuntimeMetricsEmitter.ElapsedMilliseconds(
                                toolExecutionStarted),
                            workloadClass,
                            toolExecutionOutcome);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                executionCancelled = true;
            }
            finally
            {
                schedulerReservation.Dispose();
                _metrics.Record(
                    RuntimeMetricNames.ToolQueueDepth,
                    RuntimeMetricKind.Gauge,
                    _toolScheduler.QueuedCalls);
                if (!toolQueueWaitRecorded)
                {
                    _metrics.Record(
                        RuntimeMetricNames.ToolQueueWaitMilliseconds,
                        RuntimeMetricKind.Histogram,
                        RuntimeMetricsEmitter.ElapsedMilliseconds(
                            toolQueuedAt),
                        workloadClass,
                    RuntimeMetricOutcomes.Canceled);
                }
            }

            var runDeadlineReachedDuringToolExecution =
                executionResults?.Any(
                    result => !result.IsSuccess
                              && result.Request.ExecutionDeadline
                              == RunDeadlineAt(
                                  startedAt,
                                  run.Budget.MaxDurationMs)
                              && (string.Equals(
                                      result.ErrorCode,
                                      "tool_timeout",
                                      StringComparison.Ordinal)
                                  || string.Equals(
                                      result.ErrorCode,
                                      "tool_deadline_expired",
                                      StringComparison.Ordinal)))
                == true;

            var lifecycleResults = new List<ToolLifecycleResult>(
                valid.Length);
            foreach (var call in valid)
            {
                ActionReceipt receipt;
                var syntheticUncertainty = false;
                var executionResult = executionResults?
                    .FirstOrDefault(
                        item => string.Equals(
                            item.Request.ToolCallId,
                            call.ToolCallId,
                            StringComparison.Ordinal));
                if (executor.TryGetReceipt(
                        call.ToolCallId,
                        out var actual))
                {
                    receipt = actual!;
                }
                else
                {
                    var errorCode = executionResult?.ErrorCode
                        ?? (executionCancelled
                            ? "tool_execution_cancelled"
                            : "tool_result_missing");
                    var definitelyNoSideEffect =
                        controlQueuedBeforeDispatch
                        || executionResult is not null
                        && !executionResult.MayHaveExecuted
                        || executionResult is null
                        && executionCancelled
                        && string.Equals(
                            call.Tool.Effect,
                            ToolEffects.PureRead,
                            StringComparison.Ordinal);
                    receipt = definitelyNoSideEffect
                        ? FailedReceipt(
                            actionRequests[call.ToolCallId],
                            errorCode)
                        : UnknownReceipt(
                            actionRequests[call.ToolCallId],
                            errorCode);
                    syntheticUncertainty = !definitelyNoSideEffect;
                }

                var receiptSourceEventId =
                    syntheticUncertainty
                        ? await _journal.AppendActionUncertaintyAsync(
                                run,
                                turnId,
                                attemptId,
                                receipt,
                                CancellationToken.None)
                            .ConfigureAwait(false)
                        : await _journal.AppendActionReceiptAsync(
                                run,
                                turnId,
                                attemptId,
                                receipt,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                if (!string.Equals(
                        receipt.Status,
                        ReceiptStatuses.Unknown,
                        StringComparison.Ordinal))
                {
                    committedReceipts.Add(receipt);
                    finalOutputEvidence.Add(
                        new FinalOutputCommittedEvidence(
                            run.RunId,
                            turnId,
                            receiptSourceEventId,
                            receipt));
                    toolMessages[call.ToolCallId] =
                        NormalizedTranscript.ToolResult(
                            RunRecovery.ToolResultMessageId(receipt),
                            call.ToolCallId,
                            call.Tool.Name,
                            finalOutputAdmission is null
                                ? ProtocolJson.ToElement(receipt)
                                : FinalOutputAdmissionCodec
                                    .ForModelPresentation(
                                        receipt,
                                        receiptSourceEventId),
                                    receipt.ReceivedAt);
                }

                lifecycleResults.Add(
                    new ToolLifecycleResult(
                        receipt.OperationId,
                        call.ToolCallId,
                        receipt.Status,
                        receipt.ErrorCode));
            }


            if (_lifecycle.Count > 0)
            {
                await _lifecycle.InvokeAsync(
                        new ToolBatchCompletedLifecycleEvent(
                            run.RunId,
                            turnId,
                            lifecycleResults),
                        allowRejection: false,
                        CancellationToken.None,
                        enforceRequired: false)
                    .ConfigureAwait(false);
            }

            toolActivations = await AppendPreparedToolMessagesAsync(
                    run,
                    transcript,
                    prepared,
                    toolMessages,
                    toolDisclosure,
                    skillRuntime,
                    skillSnapshot,
                    toolSnapshot,
                    effectiveActiveSkills,
                    activeSkillState,
                    turnId,
                    attemptId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (run.PendingOperationIds.Count == 0)
            {
                var gameContextAdvancement =
                    GameContextAdvancementPlanner.Plan(
                        run,
                        actionRequests.Values
                            .OrderBy(
                                item => item.OperationId,
                                StringComparer.Ordinal)
                            .ToArray(),
                        committedReceipts);
                if (gameContextAdvancement is not null)
                {
                    await _journal.CommitGameContextAdvancementAsync(
                            run,
                            turnId,
                            attemptId,
                            gameContextAdvancement,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            if (run.PendingOperationIds.Count > 0)
            {
                toolLoopGuard.ResetForIndeterminateOutcome();
            }
            else
            {
                toolLoopGuard.ObserveMessages(
                    transcript.Skip(guardTranscriptStart).ToArray());
            }

            var afterTools = await DrainControlsAsync(
                    run,
                    control,
                    CancellationToken.None)
                .ConfigureAwait(false);
            pendingContext.AddRange(afterTools.Context);
            var durationDeadlineReached =
                deadline.IsExpired
                || runDeadlineReachedDuringToolExecution;
            if (run.PendingOperationIds.Count > 0)
            {
                var intent = durationDeadlineReached
                    ? null
                    : deadline.ExternalCancellationRequested
                      || afterTools.Cancel
                    ? CompletionIntents.Cancelled
                    : afterTools.Interrupt
                        ? CompletionIntents.Interrupted
                        : null;
                await _journal.CommitTransitionAsync(
                        run,
                        RunStates.Reconciling,
                        RuntimeEventKinds.ActionReconciling,
                        completionIntent: intent,
                        turnId: turnId,
                        attemptId: attemptId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                if (durationDeadlineReached)
                {
                    return await CompleteDurationDeadlineAsync(
                            run,
                            transcript,
                            startedAt)
                        .ConfigureAwait(false);
                }

                return Outcome(run, transcript);
            }

            await CommitMemoryForReceiptsAsync(
                    run,
                    turnId,
                    attemptId,
                    ReceiptMemorySources(run, committedReceipts),
                    committedReceipts,
                    transcript,
                    assistant,
                    null,
                    deadline.Token)
                .ConfigureAwait(false);

            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.TurnCompleted,
                    next =>
                    {
                        next.CurrentTurnId = null;
                        UpdateDuration(next, startedAt);
                    },
                    turnId: turnId,
                    attemptId: attemptId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            if (durationDeadlineReached)
            {
                return await CompleteDurationDeadlineAsync(
                        run,
                        transcript,
                        startedAt)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || afterTools.Cancel)
            {
                await CompleteControlAsync(
                        run,
                        RunControlKinds.Cancel,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

            if (afterTools.Interrupt)
            {
                await CompleteControlAsync(
                        run,
                        RunControlKinds.Interrupt,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }
        }
    }

    private async ValueTask CommitMissingFinalOutputSubmissionAsync(
        AgentRun run,
        ICollection<NormalizedMessage> transcript,
        NormalizedMessage assistant,
        string turnId,
        string attemptId,
        ProviderAttemptResult response,
        ProviderRouteIdentity responseRoute,
        ProviderOpaqueContinuationState?
            durableProviderOpaqueContinuationState,
        DateTimeOffset startedAt,
        RunDeadline deadline,
        int admissionAttempt)
    {
        var feedback = new NormalizedMessage
        {
            MessageId = _ids.NewId("final-output-feedback"),
            Role = NormalizedRoles.User,
            CreatedAt = _clock.UtcNow,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(
                    FinalOutputAdmissionCodec.CreateResult(
                        admitted: false,
                        "final_output_submission_required",
                        admissionAttempt,
                        _options.FinalOutputAdmission.MaxAttempts))
            }
        };
        await _journal.CommitProviderResultWithFeedbackAsync(
                run,
                assistant,
                new[] { feedback },
                turnId,
                response.ProviderId,
                response.ProviderAttemptId,
                response.StreamAttemptId,
                CancellationToken.None,
                routeIdentity: responseRoute,
                opaqueContinuationState:
                    durableProviderOpaqueContinuationState,
                finalOutputPresentation:
                    FinalOutputAdmissionCodec.CreatePresentation(
                        FinalOutputAdmissionCodec
                            .ProvisionalPresentationState,
                        "final_output_submission_required"))
            .ConfigureAwait(false);
        transcript.Add(assistant);
        transcript.Add(feedback);
        await CommitMemoryForReceiptsAsync(
                run,
                turnId,
                response.ProviderAttemptId,
                ProviderMemorySources(
                    run,
                    turnId,
                    response.StreamAttemptId,
                    includeAssistantOutput: false),
                Array.Empty<ActionReceipt>(),
                transcript.ToArray(),
                assistant,
                null,
                deadline.Token)
            .ConfigureAwait(false);
        await CompleteTurnWithoutStateChangeAsync(
                run,
                turnId,
                attemptId,
                startedAt,
                "final_output_submission_required",
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async ValueTask<DurableRunOutcome?>
        ProcessFinalOutputSubmissionAsync(
            AgentRun run,
            List<NormalizedMessage> transcript,
            NormalizedMessage assistant,
            ProviderAttemptResult response,
            ProviderRouteIdentity responseRoute,
            ProviderOpaqueContinuationState?
                durableProviderOpaqueContinuationState,
            string turnId,
            string attemptId,
            DateTimeOffset startedAt,
            RunDeadline deadline,
            IReadOnlyList<ContextCandidate> selectedContext,
            FinalOutputAdmissionBinding binding,
            FinalOutputEvidenceRegistry evidenceRegistry,
            int admissionAttempt)
    {
        var admissionCalls = response.ToolCalls
            .Where(
                call => string.Equals(
                    call.Name,
                    FinalOutputAdmissionControl.SubmitToolName,
                    StringComparison.Ordinal))
            .ToArray();
        ModelToolCall? admissionCall =
            admissionCalls.Length == 1
            && response.ToolCalls.Count == 1
                ? admissionCalls[0]
                : null;
        ParsedFinalOutputSubmission? submission = null;
        string reasonCode;
        if (admissionCall is null)
        {
            reasonCode =
                "final_output_submission_must_be_exclusive";
        }
        else if (!FinalOutputAdmissionCodec.TryParseSubmission(
                     admissionCall,
                     _options.FinalOutputAdmission,
                     binding.Contract,
                     evidenceRegistry,
                     run.RunId,
                     out submission,
                     out reasonCode))
        {
            // The parser returns a bounded, non-sensitive reason code.
        }
        else
        {
            var proposal = new FinalOutputProposal(
                admissionCall.ToolCallId,
                response.Text,
                submission!.Output,
                submission.Evidence);
            var policyRequest = new FinalOutputAdmissionRequest(
                run,
                turnId,
                selectedContext,
                proposal,
                evidenceRegistry.Snapshot());
            var decision = await _finalOutputAdmission!.EvaluateAsync(
                    policyRequest,
                    deadline.Token)
                .ConfigureAwait(false);
            if (decision.Accepted)
            {
                var finalOutput = proposal.Output;
                var admissionEvidence =
                    FinalOutputAdmissionCodec.CreateEvidence(
                        run,
                        turnId,
                        assistant,
                        proposal,
                        binding,
                        decision.ReasonCode);
                if (_memory is null)
                {
                    await _journal.CommitFinalCompletionAsync(
                            run,
                            assistant,
                            finalOutput,
                            turnId,
                            response.ProviderId,
                            response.ProviderAttemptId,
                            response.StreamAttemptId,
                            startedAt,
                            CancellationToken.None,
                            routeIdentity: responseRoute,
                            finalOutputAdmissionEvidence:
                                admissionEvidence)
                        .ConfigureAwait(false);
                    transcript.Add(assistant);
                    return Outcome(run, transcript, finalOutput);
                }

                await _journal.CommitProviderResultAndOutputAsync(
                        run,
                        assistant,
                        finalOutput,
                        turnId,
                        response.ProviderId,
                        response.ProviderAttemptId,
                        response.StreamAttemptId,
                        CancellationToken.None,
                        routeIdentity: responseRoute,
                        finalOutputAdmissionEvidence:
                            admissionEvidence)
                    .ConfigureAwait(false);
                transcript.Add(assistant);
                try
                {
                    await CommitMemoryForReceiptsAsync(
                            run,
                            turnId,
                            response.ProviderAttemptId,
                            ProviderMemorySources(
                                run,
                                turnId,
                                response.StreamAttemptId,
                                includeAssistantOutput: true),
                            Array.Empty<ActionReceipt>(),
                            transcript,
                            assistant,
                            finalOutput,
                            deadline.Token)
                        .ConfigureAwait(false);
                    await _journal.CommitRecoveredCompletionAsync(
                            run,
                            turnId,
                            response.ProviderAttemptId,
                            startedAt,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsExpired)
                {
                    var outcome = await CompleteDurationDeadlineAsync(
                            run,
                            transcript,
                            startedAt)
                        .ConfigureAwait(false);
                    outcome.FinalOutput = finalOutput.Clone();
                    return outcome;
                }
                catch (Exception exception) when (
                    exception is not DuplicateRunException)
                {
                    var outcome = await FailOrCancelAsync(
                            run,
                            transcript,
                            exception,
                            startedAt,
                            deadline.ExternalCancellationRequested)
                        .ConfigureAwait(false);
                    outcome.FinalOutput = finalOutput.Clone();
                    return outcome;
                }

                return Outcome(run, transcript, finalOutput);
            }

            reasonCode = decision.ReasonCode;
            return await CommitFinalOutputRejectionAsync(
                    run,
                    transcript,
                    assistant,
                    response,
                    responseRoute,
                    durableProviderOpaqueContinuationState,
                    turnId,
                    attemptId,
                    startedAt,
                    deadline,
                    admissionAttempt,
                    admissionCalls,
                    reasonCode,
                    decision.Feedback)
                .ConfigureAwait(false);
        }

        return await CommitFinalOutputRejectionAsync(
                run,
                transcript,
                assistant,
                response,
                responseRoute,
                durableProviderOpaqueContinuationState,
                turnId,
                attemptId,
                startedAt,
                deadline,
                admissionAttempt,
                admissionCalls,
                reasonCode)
            .ConfigureAwait(false);
    }

    private async ValueTask<DurableRunOutcome?>
        CommitFinalOutputRejectionAsync(
            AgentRun run,
            ICollection<NormalizedMessage> transcript,
            NormalizedMessage assistant,
            ProviderAttemptResult response,
            ProviderRouteIdentity responseRoute,
            ProviderOpaqueContinuationState?
                durableProviderOpaqueContinuationState,
            string turnId,
            string attemptId,
            DateTimeOffset startedAt,
            RunDeadline deadline,
            int admissionAttempt,
            IReadOnlyList<ModelToolCall> admissionCalls,
            string reasonCode,
            JsonElement? policyFeedback = null)
    {
        var calls = response.ToolCalls.Count == 1
            ? admissionCalls
            : response.ToolCalls;
        var feedbackMessages = new List<NormalizedMessage>(calls.Count);
        foreach (var call in calls)
        {
            var callReason = string.Equals(
                    call.Name,
                    FinalOutputAdmissionControl.SubmitToolName,
                    StringComparison.Ordinal)
                ? reasonCode
                : "final_output_submission_must_be_exclusive";
            var feedback = ToolControlMessage(
                call,
                FinalOutputAdmissionCodec.CreateResult(
                    admitted: false,
                    callReason,
                    admissionAttempt,
                    _options.FinalOutputAdmission.MaxAttempts,
                    string.Equals(
                        call.Name,
                        FinalOutputAdmissionControl.SubmitToolName,
                        StringComparison.Ordinal)
                        ? policyFeedback
                        : null));
            feedbackMessages.Add(feedback);
        }
        await _journal.CommitProviderResultWithFeedbackAsync(
                run,
                assistant,
                feedbackMessages,
                turnId,
                response.ProviderId,
                response.ProviderAttemptId,
                response.StreamAttemptId,
                CancellationToken.None,
                routeIdentity: responseRoute,
                opaqueContinuationState:
                    durableProviderOpaqueContinuationState,
                finalOutputPresentation:
                    FinalOutputAdmissionCodec.CreatePresentation(
                        FinalOutputAdmissionCodec
                            .ProvisionalPresentationState,
                        reasonCode))
            .ConfigureAwait(false);
        transcript.Add(assistant);
        foreach (var feedback in feedbackMessages)
        {
            transcript.Add(feedback);
        }

        await CommitMemoryForReceiptsAsync(
                run,
                turnId,
                response.ProviderAttemptId,
                ProviderMemorySources(
                    run,
                    turnId,
                    response.StreamAttemptId,
                    includeAssistantOutput: false),
                Array.Empty<ActionReceipt>(),
                transcript.ToArray(),
                assistant,
                null,
                deadline.Token)
            .ConfigureAwait(false);
        await CompleteTurnWithoutStateChangeAsync(
                run,
                turnId,
                attemptId,
                startedAt,
                "final_output_rejected",
                CancellationToken.None)
            .ConfigureAwait(false);
        return null;
    }

    private void EnsureFinalOutputAttemptsRemain(int attempts)
    {
        if (attempts >= _options.FinalOutputAdmission.MaxAttempts)
        {
            throw new ProviderException(
                "final_output_admission_attempts_exhausted",
                "policy",
                "The final output could not be admitted within the "
                + "configured attempt limit.",
                false);
        }
    }

    private static IReadOnlyList<string> ProviderMemorySources(
        AgentRun run,
        string turnId,
        string streamAttemptId,
        bool includeAssistantOutput)
    {
        var sources = new List<string>(includeAssistantOutput ? 2 : 1)
        {
            RuntimeEventIdDerivation.Derive(
                run.RunId,
                "provider-result-committed:" + streamAttemptId)
        };
        if (includeAssistantOutput)
        {
            sources.Add(
                RuntimeEventIdDerivation.Derive(
                    run.RunId,
                    "assistant-completed:" + turnId));
        }

        sources.Sort(StringComparer.Ordinal);
        return sources;
    }

    private static IReadOnlyList<string> ReceiptMemorySources(
        AgentRun run,
        IReadOnlyList<ActionReceipt> receipts)
    {
        return receipts
            .Select(
                receipt => RuntimeEventIdDerivation.Derive(
                    run.RunId,
                    "action-receipt:"
                    + receipt.OperationId
                    + ":"
                    + receipt.Revision.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private async ValueTask<PreparedRuntimeMemoryCommit?>
        CommitMemoryForReceiptsAsync(
        AgentRun run,
        string turnId,
        string? attemptId,
        IReadOnlyList<string> committedSourceEventIds,
        IReadOnlyList<ActionReceipt> receipts,
        IReadOnlyList<NormalizedMessage> committedTranscript,
        NormalizedMessage? assistantMessage,
        JsonElement? assistantOutput,
        CancellationToken cancellationToken)
    {
        if (_memory is null)
        {
            return null;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = RuntimeMetricOutcomes.Failure;
        try
        {
            var prepared = await CommitMemoryForReceiptsCoreAsync(
                    run,
                    turnId,
                    attemptId,
                    committedSourceEventIds,
                    receipts,
                    committedTranscript,
                    assistantMessage,
                    assistantOutput,
                    cancellationToken)
                .ConfigureAwait(false);
            outcome = RuntimeMetricOutcomes.Success;
            return prepared;
        }
        catch (OperationCanceledException)
        {
            outcome = RuntimeMetricOutcomes.Canceled;
            throw;
        }
        finally
        {
            _metrics.Record(
                RuntimeMetricNames.MemoryCommitMilliseconds,
                RuntimeMetricKind.Histogram,
                RuntimeMetricsEmitter.ElapsedMilliseconds(started),
                outcome: outcome);
        }
    }

    private async ValueTask<PreparedRuntimeMemoryCommit?>
        CommitMemoryForReceiptsCoreAsync(
        AgentRun run,
        string turnId,
        string? attemptId,
        IReadOnlyList<string> committedSourceEventIds,
        IReadOnlyList<ActionReceipt> receipts,
        IReadOnlyList<NormalizedMessage> committedTranscript,
        NormalizedMessage? assistantMessage,
        JsonElement? assistantOutput,
        CancellationToken cancellationToken)
    {
        var memory = _memory
                     ?? throw new InvalidOperationException(
                         "Runtime memory is not configured.");
        if (FinalOutputAdmissionBinding.Read(run) is not null)
        {
            var admittedAssistantMessageId = assistantOutput.HasValue
                ? assistantMessage?.MessageId
                : null;
            committedTranscript = committedTranscript
                .Where(
                    message => !string.Equals(
                                   message.Role,
                                   NormalizedRoles.Assistant,
                                   StringComparison.Ordinal)
                               || (admittedAssistantMessageId is not null
                                   && string.Equals(
                                       message.MessageId,
                                       admittedAssistantMessageId,
                                       StringComparison.Ordinal)))
                .ToArray();
            if (!assistantOutput.HasValue)
            {
                assistantMessage = null;
            }
        }

        var prepared = memory.PrepareCommit(
            run,
            turnId,
            committedSourceEventIds,
            receipts,
            committedTranscript,
            assistantMessage,
            assistantOutput);
        memory.ValidatePreparedForRun(
            prepared,
            run,
            committedSourceEventIds);
        if (prepared.Mutations.Count == 0)
        {
            await _journal.AppendBuiltInDurableAsync(
                    run,
                    RuntimeEventKinds.MemoryCommitSettled,
                    RuntimeMemoryCommitJournalCodec.EncodeSettled(prepared),
                    turnId,
                    attemptId,
                    eventId: "memory-settled:" + prepared.CommitId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return prepared;
        }

        await _journal.AppendBuiltInDurableAsync(
                run,
                RuntimeEventKinds.MemoryCommitPrepared,
                RuntimeMemoryCommitJournalCodec.EncodePrepared(prepared),
                turnId,
                attemptId,
                eventId: "memory-prepared:" + prepared.CommitId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await memory.ApplyPreparedAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        await _journal.AppendBuiltInDurableAsync(
                run,
                RuntimeEventKinds.MemoryCommitCompleted,
                RuntimeMemoryCommitJournalCodec.EncodeCompleted(prepared),
                turnId,
                attemptId,
                eventId: "memory-completed:" + prepared.CommitId,
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return prepared;
    }

    private async ValueTask ReplayPreparedMemoryCommitsAsync(
        RecoveredRun recovered,
        CancellationToken cancellationToken)
    {
        var count = recovered.PendingMemoryCommits.Count;
        if (count == 0)
        {
            return;
        }

        if (_memory is null)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.CommitFailed,
                "The recovered run has a pending memory commit, but this "
                + "runtime has no memory lifecycle.");
        }

        var completed = recovered.CompletedMemoryCommitIds.ToHashSet(
            StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = recovered.PendingMemoryCommits[index]
                           ?? throw new RuntimeMemoryIntegrationException(
                               RuntimeMemoryIntegrationReasonCodes
                                   .RecoveryRecordInvalid,
                               "A recovered memory commit is null.");
            var receiptBatch = recovered.MemoryReceiptBatches.SingleOrDefault(
                item => string.Equals(
                    item.TurnId,
                    prepared.TurnId,
                    StringComparison.Ordinal));
            IReadOnlyList<string> sourceEventIds;
            if (receiptBatch?.Receipts.Count > 0)
            {
                sourceEventIds = receiptBatch.SourceEventIds;
            }
            else if (!recovered.CommittedMemorySourceEventIdsByTurn.TryGetValue(
                         prepared.TurnId,
                         out sourceEventIds)
                     || sourceEventIds.Count == 0)
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A recovered memory commit has no committed sources.");
            }

            _memory.ValidatePreparedForRun(
                prepared,
                recovered.Run,
                sourceEventIds,
                enforceConfiguredLimits: false,
                enforcePolicyIdentity: false);
            await _memory.ApplyPreparedAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            await _journal.AppendBuiltInDurableAsync(
                    recovered.Run,
                    RuntimeEventKinds.MemoryCommitCompleted,
                    RuntimeMemoryCommitJournalCodec.EncodeCompleted(prepared),
                    prepared.TurnId,
                    attemptId: null,
                    eventId: "memory-completed:" + prepared.CommitId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            completed.Add(prepared.CommitId);
        }

        recovered.PendingMemoryCommits =
            Array.Empty<PreparedRuntimeMemoryCommit>();
        recovered.CompletedMemoryCommitIds = completed
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private async ValueTask AdvanceRecoveredGameContextAsync(
        RecoveredRun recovered,
        CancellationToken cancellationToken)
    {
        var turnId = recovered.Run.CurrentTurnId;
        if (turnId is null
            || recovered.GameContextAdvancedTurnIds.Contains(
                turnId,
                StringComparer.Ordinal))
        {
            return;
        }

        var receiptBatch = recovered.MemoryReceiptBatches.SingleOrDefault(
            item => string.Equals(
                item.TurnId,
                turnId,
                StringComparison.Ordinal));
        if (receiptBatch is null || receiptBatch.Receipts.Count == 0)
        {
            return;
        }

        var requests = recovered.RecoveryActionRequests
            .Where(
                item => string.Equals(
                    item.TurnId,
                    turnId,
                    StringComparison.Ordinal))
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .ToArray();
        var plan = GameContextAdvancementPlanner.Plan(
            recovered.Run,
            requests,
            receiptBatch.Receipts);
        if (plan is null)
        {
            return;
        }

        await _journal.CommitGameContextAdvancementAsync(
                recovered.Run,
                turnId,
                attemptId: null,
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        recovered.GameContextAdvancedTurnIds =
            recovered.GameContextAdvancedTurnIds
                .Append(turnId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
    }

    private async ValueTask<bool> CommitRecoveredMemoryReceiptsAsync(
        RecoveredRun recovered,
        CancellationToken cancellationToken)
    {
        var turnId = recovered.Run.CurrentTurnId;
        if (turnId is null)
        {
            return false;
        }

        var memoryWasConfigured =
            recovered.LastTurnSnapshot?.Extensions.ContainsKey(
                RuntimeMemoryAgentLoop.PolicySnapshotExtension) == true;
        if (!memoryWasConfigured)
        {
            return false;
        }

        var commitId = RuntimeMemoryAgentLoop.CommitId(
            recovered.Run.RunId,
            recovered.Run.RuntimeGeneration,
            turnId);
        RecoveredMemoryReceiptBatch? receiptBatch = null;
        var batchCount = recovered.MemoryReceiptBatches.Count;
        for (var index = 0; index < batchCount; index++)
        {
            var candidate = recovered.MemoryReceiptBatches[index]
                            ?? throw new InvalidDataException(
                                "A recovered memory receipt batch is null.");
            if (!string.Equals(
                    candidate.TurnId,
                    turnId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (receiptBatch is not null)
            {
                throw new InvalidDataException(
                    "A recoverable turn has duplicate receipt batches.");
            }

            receiptBatch = candidate;
        }

        recovered.CommittedAssistantMessagesByTurn.TryGetValue(
            turnId,
            out var assistantMessage);
        var assistantOutput =
            string.Equals(
                recovered.FinalOutputTurnId,
                turnId,
                StringComparison.Ordinal)
                ? recovered.FinalOutput
                : null;
        var committedProviderOutcome =
            recovered.CommittedProviderResultTurnIds.Contains(
                turnId,
                StringComparer.Ordinal)
            && assistantMessage is not null
            && (assistantOutput.HasValue
                || !assistantMessage.Parts.Any(
                    part => string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolCall,
                        StringComparison.Ordinal)));
        if ((receiptBatch is null || receiptBatch.Receipts.Count == 0)
            && !committedProviderOutcome)
        {
            return false;
        }

        IReadOnlyList<string> sourceEventIds;
        if (receiptBatch?.Receipts.Count > 0)
        {
            sourceEventIds = receiptBatch.SourceEventIds;
        }
        else if (!recovered.CommittedMemorySourceEventIdsByTurn.TryGetValue(
                     turnId,
                     out sourceEventIds)
                 || sourceEventIds.Count == 0)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A recovered memory decision has no committed source events.");
        }

        if (recovered.CompletedMemoryCommitIds.Contains(
                commitId,
                StringComparer.Ordinal))
        {
            if (!recovered.MemoryCommitRecords.TryGetValue(
                    commitId,
                    out _))
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A recovered memory settlement has no durable record.");
            }

            return true;
        }

        if (_memory is null)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryPolicyMismatch,
                "The recoverable turn requires a configured memory policy.");
        }

        _memory.EnsureRecoveryPolicy(
            recovered.LastTurnSnapshot,
            turnId);
        var prepared = await CommitMemoryForReceiptsAsync(
                recovered.Run,
                turnId,
                null,
                sourceEventIds,
                receiptBatch?.Receipts
                ?? Array.Empty<ActionReceipt>(),
                recovered.Transcript,
                assistantMessage,
                assistantOutput,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepared is not null)
        {
            recovered.CompletedMemoryCommitIds =
                recovered.CompletedMemoryCommitIds
                    .Append(commitId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
        }

        return prepared is not null;
    }

    private static void RetainDeferredContext(
        List<ContextCandidate> pendingContext,
        Dictionary<string, int> deferralTurns,
        ContextBudgetReport report,
        IReadOnlyCollection<string>? ephemeralIds = null)
    {
        if (report.DeferredIds.Count == 0)
        {
            pendingContext.Clear();
            deferralTurns.Clear();
            return;
        }

        var candidatesById = pendingContext.ToDictionary(
            candidate => candidate.Id,
            StringComparer.Ordinal);
        var retained = new List<ContextCandidate>(
            Math.Min(
                report.DeferredIds.Count,
                MaxDeferredContextCandidates));
        var retainedTurns = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var retainedIds = new List<string>(retained.Capacity);
        var reasons = new SortedSet<string>(
            report.ReasonCodes,
            StringComparer.Ordinal);

        foreach (var id in report.DeferredIds)
        {
            if (ephemeralIds?.Contains(id, StringComparer.Ordinal) == true)
            {
                continue;
            }

            if (!candidatesById.TryGetValue(id, out var candidate))
            {
                throw new InvalidDataException(
                    "The context compiler deferred an unknown candidate.");
            }

            var turns = checked(
                (deferralTurns.TryGetValue(id, out var previousTurns)
                    ? previousTurns
                    : 0) + 1);
            string? pruneReason = null;
            if (turns >= MaxContextDeferralTurns)
            {
                pruneReason = DeferredTurnLimitReason;
            }
            else if (retained.Count >= MaxDeferredContextCandidates)
            {
                pruneReason = DeferredCapacityReason;
            }

            if (pruneReason is not null)
            {
                report.Pruned.Add(
                    new PrunedContextItem
                    {
                        Id = candidate.Id,
                        Category = candidate.Category,
                        ReasonCode = pruneReason
                    });
                reasons.Add(pruneReason);
                continue;
            }

            retained.Add(candidate);
            retainedIds.Add(id);
            retainedTurns.Add(id, turns);
        }

        if (retainedIds.Count == 0)
        {
            reasons.Remove("deferred_budget");
        }

        report.DeferredIds = retainedIds;
        report.ReasonCodes = reasons.ToList();
        pendingContext.Clear();
        pendingContext.AddRange(retained);
        deferralTurns.Clear();
        foreach (var item in retainedTurns)
        {
            deferralTurns.Add(item.Key, item.Value);
        }
    }

    private static string? ProviderResponseDiscardReason(
        RunDeadline deadline,
        ControlBatch controls)
    {
        if (deadline.ExternalCancellationRequested)
        {
            return "provider_result_cancelled";
        }

        if (deadline.IsExpired)
        {
            return "provider_result_deadline";
        }

        if (controls.Cancel)
        {
            return "provider_result_cancelled";
        }

        if (controls.Interrupt)
        {
            return "provider_result_interrupted";
        }

        return controls.Steer
            ? "provider_result_steered"
            : null;
    }

    private IReadOnlyList<PreparedToolCall> PrepareToolCalls(
        AgentRun run,
        string turnId,
        string attemptId,
        IReadOnlyList<ModelToolCall> calls,
        ToolDisclosurePlan disclosure,
        SkillRuntimePlan skillRuntime)
    {
        var prepared = new List<PreparedToolCall>(calls.Count);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var dispatchIndex = 0;
        var controlCalls = 0;
        var skillControlCalls = 0;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (!callIds.Add(call.ToolCallId))
            {
                throw new ProviderException(
                    "provider_duplicate_tool_call",
                    "provider",
                    "The provider emitted a duplicate tool-call id.",
                    true);
            }

            if (disclosure.IsControlVisible(call.Name))
            {
                controlCalls++;
                if (controlCalls
                    > disclosure.Limits.MaxControlCallsPerTurn)
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            ToolErrorMessage(
                                call,
                                "tool_disclosure_control_limit_exceeded",
                                "The deferred-tool control-call limit was "
                                + "exceeded.")));
                    continue;
                }

                if (string.Equals(
                        call.Name,
                        ToolDisclosureControlNames.Search,
                        StringComparison.Ordinal))
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            PrepareToolSearchResult(call, disclosure)));
                    continue;
                }

                if (!TryReadActivationControl(
                        call.Arguments,
                        out var activation))
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            ToolErrorMessage(
                                call,
                                "tool_disclosure_arguments_invalid",
                                "The activation control arguments are "
                                + "invalid.")));
                    continue;
                }

                prepared.Add(
                    new PreparedToolCall(
                        call,
                        execution: null,
                        immediateMessage: null,
                        activation: activation));
                continue;
            }

            if (skillRuntime.IsControlVisible(call.Name))
            {
                skillControlCalls++;
                if (skillControlCalls
                    > skillRuntime.Limits.MaxControlCallsPerTurn)
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            ToolErrorMessage(
                                call,
                                "skill_control_call_limit_exceeded",
                                "The skill control-call limit was exceeded.")));
                    continue;
                }

                if (string.Equals(
                        call.Name,
                        SkillRuntimeControlNames.Search,
                        StringComparison.Ordinal))
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            PrepareSkillSearchResult(call, skillRuntime)));
                    continue;
                }

                if (!SkillRuntimePlan.TryReadActivation(
                        call.Arguments,
                        out var skillActivation))
                {
                    prepared.Add(
                        new PreparedToolCall(
                            call,
                            execution: null,
                            ToolErrorMessage(
                                call,
                                SkillRuntimeReasonCodes
                                    .ActivationArgumentsInvalid,
                                "The skill activation arguments are invalid.")));
                    continue;
                }

                prepared.Add(
                    new PreparedToolCall(
                        call,
                        execution: null,
                        immediateMessage: null,
                        skillActivation: skillActivation));
                continue;
            }

            if (!disclosure.TryGetEffectiveTool(call.Name, out var tool)
                || tool is null)
            {
                prepared.Add(
                    new PreparedToolCall(
                        call,
                        execution: null,
                        ToolErrorMessage(
                            call,
                            "unknown_tool",
                            "The requested tool is not available.")));
                continue;
            }

            var safety = _toolSafety.Validate(
                tool,
                call.Arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["agentId"] = run.AgentId,
                    ["worldId"] = run.WorldId,
                    ["runId"] = run.RunId,
                    ["turnId"] = turnId
                });
            if (!safety.IsValid)
            {
                prepared.Add(
                    new PreparedToolCall(
                        call,
                        execution: null,
                        ToolErrorMessage(
                            call,
                            safety.Errors.FirstOrDefault()?.Code
                            ?? "tool_arguments_invalid",
                            "The tool arguments failed validation.")));
                continue;
            }

            var invocation = new ToolInvocation
            {
                ToolCallId = call.ToolCallId,
                RunId = run.RunId,
                TurnId = turnId,
                AttemptId = attemptId,
                ToolName = tool.Name,
                ToolVersion = tool.Version,
                Arguments = call.Arguments.Clone(),
                Effect = tool.Effect,
                ResolvedConflictKeys = safety.ResolvedConflictKeys.ToList(),
                Sequence = checked(
                    (long)run.Usage.Actions + dispatchIndex),
                CreatedAt = _clock.UtcNow
            };
            dispatchIndex++;
            prepared.Add(
                new PreparedToolCall(
                    call,
                    new ToolExecutionRequest(run.AgentId, invocation, tool),
                    immediateMessage: null));
        }

        return new ReadOnlyCollection<PreparedToolCall>(prepared);
    }

    private IReadOnlyList<PreparedToolCall> ApplySideEffectToolCallLimit(
        IReadOnlyList<PreparedToolCall> prepared,
        int? maximumSideEffects,
        long firstSequence)
    {
        if (!maximumSideEffects.HasValue)
        {
            return prepared;
        }

        var sideEffectCount = prepared.Count(
            item => item.Execution is not null
                    && !string.Equals(
                        item.Execution.Tool.Effect,
                        ToolEffects.PureRead,
                        StringComparison.Ordinal));
        if (sideEffectCount <= maximumSideEffects.Value)
        {
            return prepared;
        }

        var admitted = new List<PreparedToolCall>(prepared.Count);
        var sequence = firstSequence;
        foreach (var item in prepared)
        {
            var execution = item.Execution;
            if (execution is null)
            {
                admitted.Add(item);
                continue;
            }

            if (!string.Equals(
                    execution.Tool.Effect,
                    ToolEffects.PureRead,
                    StringComparison.Ordinal))
            {
                admitted.Add(
                    new PreparedToolCall(
                        item.ToolCall,
                        execution: null,
                        ToolErrorMessage(
                            item.ToolCall,
                            "side_effect_tool_call_limit_exceeded",
                            "This turn requested too many side-effecting "
                            + "tools. No side-effecting tool from the "
                            + "response was executed.")));
                continue;
            }

            var invocation = execution.Invocation();
            invocation.Sequence = sequence;
            sequence = checked(sequence + 1);
            admitted.Add(
                new PreparedToolCall(
                    item.ToolCall,
                    new ToolExecutionRequest(
                        execution.AgentId,
                        invocation,
                        execution.Tool),
                    immediateMessage: null));
        }

        return new ReadOnlyCollection<PreparedToolCall>(admitted);
    }

    private NormalizedMessage PrepareSkillSearchResult(
        ModelToolCall call,
        SkillRuntimePlan runtimePlan)
    {
        if (!SkillRuntimePlan.TryReadSearch(
                call.Arguments,
                runtimePlan.Limits,
                out var query,
                out var limit,
                out var reasonCode))
        {
            return ToolErrorMessage(
                call,
                reasonCode,
                reasonCode == SkillRuntimeReasonCodes.SearchBudgetExceeded
                    ? "The skill search exceeded its bounded work budget."
                    : "The skill search arguments are invalid.");
        }

        if (!runtimePlan.TrySearch(
                query,
                limit,
                out var hits,
                out reasonCode))
        {
            return ToolErrorMessage(
                call,
                reasonCode,
                "The skill search exceeded its bounded work budget.");
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skill-search-result+json");
            writer.WritePropertyName("query");
            query.WriteTo(writer);
            writer.WriteNumber("count", hits.Count);
            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("skillId", hit.Skill.SkillId);
                writer.WriteString("version", hit.Skill.Version);
                writer.WriteString(
                    "skillDigest",
                    hit.Skill.ContentDigest);
                writer.WriteString(
                    "description",
                    hit.Skill.Description);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return ToolControlMessage(call, document.RootElement);
    }

    private NormalizedMessage PrepareToolSearchResult(
        ModelToolCall call,
        ToolDisclosurePlan disclosure)
    {
        if (!TryReadSearchControl(
                call.Arguments,
                disclosure.Limits,
                out var query,
                out var limit))
        {
            return ToolErrorMessage(
                call,
                "tool_disclosure_arguments_invalid",
                "The deferred-tool search arguments are invalid.");
        }

        var hits = disclosure.Search(query, limit);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.tool-search-result+json");
            writer.WriteString("query", query);
            writer.WriteNumber("count", hits.Count);
            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("name", hit.Tool.Name);
                writer.WriteString("version", hit.Tool.Version);
                writer.WriteString(
                    "descriptorDigest",
                    hit.Tool.Digest);
                writer.WriteString("source", hit.Tool.Toolset);
                writer.WriteString("description", hit.Tool.Description);
                writer.WriteString("effect", hit.Tool.Effect);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return ToolControlMessage(call, document.RootElement);
    }

    private static void AnnotateToolCallEvidence(
        NormalizedMessage assistant,
        ToolDisclosurePlan disclosure,
        IReadOnlyList<ToolDescriptor> providerTools)
    {
        foreach (var part in assistant.Parts)
        {
            if (!string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolCall,
                    StringComparison.Ordinal)
                || part.ToolName is null)
            {
                continue;
            }

            if (disclosure.TryGetEffectiveTool(
                    part.ToolName,
                    out var catalogEntry)
                && catalogEntry is not null)
            {
                part.ToolVersion = catalogEntry.Version;
                part.ToolEffect = catalogEntry.Effect;
                part.ToolDescriptorDigest = catalogEntry.Digest;
                continue;
            }

            var descriptor = providerTools.FirstOrDefault(
                item => string.Equals(
                    item.Name,
                    part.ToolName,
                    StringComparison.Ordinal));
            if (descriptor is null)
            {
                continue;
            }

            var digest = new CanonicalDigestBuilder();
            digest.Add(
                "toolDescriptor",
                ProtocolJson.ToElement(descriptor));
            part.ToolVersion = descriptor.Version;
            part.ToolEffect = descriptor.Effect;
            part.ToolDescriptorDigest = digest.Finish();
        }
    }

    private async ValueTask<IReadOnlyList<ToolActivationRecord>>
        AppendPreparedToolMessagesAsync(
            AgentRun run,
            ICollection<NormalizedMessage> transcript,
            IReadOnlyList<PreparedToolCall> prepared,
            IReadOnlyDictionary<string, NormalizedMessage> toolMessages,
            ToolDisclosurePlan disclosure,
            SkillRuntimePlan skillRuntime,
            SkillCatalogSnapshot skillSnapshot,
            ToolCatalogSnapshot toolSnapshot,
            ICollection<SkillReference> activeSkills,
            ICollection<SkillActivationStateRecord> activeSkillState,
            string turnId,
            string attemptId,
            CancellationToken cancellationToken)
    {
        foreach (var item in prepared)
        {
            if (item.Activation is not null)
            {
                var before = disclosure.StateDigest;
                var reason = disclosure.ActivateFromModel(
                    item.Activation.Name,
                    item.Activation.Version,
                    item.Activation.DescriptorDigest);
                var message = ToolActivationResultMessage(
                    item.ToolCall,
                    item.Activation,
                    reason);
                if (!string.Equals(
                        before,
                        disclosure.StateDigest,
                        StringComparison.Ordinal))
                {
                    await _journal.CommitToolDisclosureResultAsync(
                            run,
                            turnId,
                            attemptId,
                            item.ToolCall.ToolCallId,
                            disclosure.ToJournalRecord(new[] { reason }),
                            message,
                            cancellationToken)
                        .ConfigureAwait(false);
                    transcript.Add(message);
                }
                else
                {
                    await AppendTranscriptOnceAsync(
                            run,
                            transcript,
                            message,
                            turnId,
                            attemptId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (item.SkillActivation is not null)
            {
                var reason = ActivateSkillFromModel(
                    run,
                    turnId,
                    item.SkillActivation,
                    skillRuntime,
                    skillSnapshot,
                    toolSnapshot,
                    disclosure,
                    activeSkills,
                    activeSkillState,
                    out var activated);
                var message = SkillActivationResultMessage(
                    item.ToolCall,
                    item.SkillActivation,
                    reason);
                if (activated is not null)
                {
                    var nextSkillState = activeSkillState
                        .Concat(new[] { activated })
                        .ToArray();
                    await _journal.CommitSkillActivationResultAsync(
                            run,
                            turnId,
                            attemptId,
                            item.ToolCall.ToolCallId,
                            nextSkillState,
                            disclosure.ToJournalRecord(
                                new[]
                                {
                                    SkillRuntimeReasonCodes.ActivatedByModel
                                }),
                            message,
                            cancellationToken)
                        .ConfigureAwait(false);
                    activeSkills.Add(activated.ToReference());
                    activeSkillState.Add(activated);
                    transcript.Add(message);
                }
                else
                {
                    await AppendTranscriptOnceAsync(
                            run,
                            transcript,
                            message,
                            turnId,
                            attemptId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                continue;
            }

            if (toolMessages.TryGetValue(
                    item.ToolCall.ToolCallId,
                    out var immediate))
            {
                await AppendTranscriptOnceAsync(
                        run,
                        transcript,
                        immediate,
                        turnId,
                        attemptId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return disclosure.RequestedActivations;
    }

    private string ActivateSkillFromModel(
        AgentRun run,
        string turnId,
        PreparedSkillActivation activation,
        SkillRuntimePlan runtimePlan,
        SkillCatalogSnapshot skillSnapshot,
        ToolCatalogSnapshot toolSnapshot,
        ToolDisclosurePlan toolDisclosure,
        ICollection<SkillReference> activeSkills,
        ICollection<SkillActivationStateRecord> activeSkillState,
        out SkillActivationStateRecord? activated)
    {
        activated = null;
        var existing = activeSkillState.FirstOrDefault(
            value => string.Equals(
                         value.Reference,
                         activation.Reference,
                         StringComparison.Ordinal));
        if (existing is not null)
        {
            return string.Equals(
                    existing.ContentDigest,
                    activation.SkillDigest,
                    StringComparison.Ordinal)
                ? SkillRuntimeReasonCodes.AlreadyActivated
                : SkillRuntimeReasonCodes.ExactIdentityMismatch;
        }

        if (!runtimePlan.TryResolveExact(
                activation.SkillId,
                activation.Version,
                activation.SkillDigest,
                out var skill,
                out var reasonCode)
            || skill is null)
        {
            return reasonCode;
        }

        var proposed = activeSkills
            .Select(value => new SkillReference(value.SkillId, value.Version))
            .Concat(
                new[]
                {
                    new SkillReference(skill.SkillId, skill.Version)
                })
            .ToArray();
        try
        {
            _ = skillSnapshot.CreateDisclosure(
                proposed,
                _options.SkillDisclosureBudget);
            _ = _skillAdmission.Evaluate(
                run,
                turnId,
                skillSnapshot,
                toolSnapshot,
                toolDisclosure,
                proposed,
                _options.SkillDisclosureBudget);
            toolDisclosure.FinalizeSkillActivations();
        }
        catch (SkillAdmissionException exception)
        {
            return exception.ReasonCode;
        }
        catch (RuntimeContentLimitException exception)
        {
            return exception.LimitCode;
        }
        catch (ArgumentException)
        {
            return SkillRuntimeReasonCodes.NotAuthorized;
        }
        catch (KeyNotFoundException)
        {
            return SkillRuntimeReasonCodes.NotAuthorized;
        }

        activated = new SkillActivationStateRecord(
            skill.SkillId,
            skill.Version,
            skill.ContentDigest);
        return SkillRuntimeReasonCodes.ActivatedByModel;
    }

    private NormalizedMessage SkillActivationResultMessage(
        ModelToolCall call,
        PreparedSkillActivation activation,
        string reasonCode)
    {
        var activated = string.Equals(
                            reasonCode,
                            SkillRuntimeReasonCodes.ActivatedByModel,
                            StringComparison.Ordinal)
                        || string.Equals(
                            reasonCode,
                            SkillRuntimeReasonCodes.AlreadyActivated,
                            StringComparison.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skill-activation-result+json");
            writer.WriteBoolean("activated", activated);
            writer.WriteString("reasonCode", reasonCode);
            writer.WriteString("skillId", activation.SkillId);
            writer.WriteString("version", activation.Version);
            writer.WriteString("skillDigest", activation.SkillDigest);
            if (activated)
            {
                writer.WriteString(
                    "availableFrom",
                    string.Equals(
                        reasonCode,
                        SkillRuntimeReasonCodes.ActivatedByModel,
                        StringComparison.Ordinal)
                        ? "next_provider_turn"
                        : "current_provider_turn");
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return ToolControlMessage(call, document.RootElement);
    }

    private NormalizedMessage ToolActivationResultMessage(
        ModelToolCall call,
        PreparedToolActivation activation,
        string reasonCode)
    {
        var activated = string.Equals(
                            reasonCode,
                            ToolDisclosureReasonCodes.ActivatedByModel,
                            StringComparison.Ordinal)
                        || string.Equals(
                            reasonCode,
                            ToolDisclosureReasonCodes.AlreadyActivated,
                            StringComparison.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.tool-activation-result+json");
            writer.WriteBoolean("activated", activated);
            writer.WriteString("reasonCode", reasonCode);
            writer.WriteString("name", activation.Name);
            writer.WriteString("version", activation.Version);
            writer.WriteString(
                "descriptorDigest",
                activation.DescriptorDigest);
            if (activated)
            {
                writer.WriteString(
                    "availableFrom",
                    string.Equals(
                        reasonCode,
                        ToolDisclosureReasonCodes.ActivatedByModel,
                        StringComparison.Ordinal)
                        ? "next_provider_turn"
                        : "current_provider_turn");
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return ToolControlMessage(call, document.RootElement);
    }

    private NormalizedMessage ToolControlMessage(
        ModelToolCall call,
        JsonElement payload)
    {
        return new NormalizedMessage
        {
            MessageId = _ids.NewId("tool-control-message"),
            Role = NormalizedRoles.Tool,
            CreatedAt = _clock.UtcNow,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromToolResult(
                    call.ToolCallId,
                    call.Name,
                    payload.Clone())
            }
        };
    }

    private static bool TryReadSearchControl(
        JsonElement arguments,
        ToolDisclosureLimits limits,
        out string query,
        out int limit)
    {
        query = string.Empty;
        limit = limits.MaxSearchResults;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        var hasQuery = false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                return false;
            }

            if (string.Equals(
                    property.Name,
                    "query",
                    StringComparison.Ordinal))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                query = property.Value.GetString()!;
                hasQuery = true;
            }
            else if (string.Equals(
                         property.Name,
                         "limit",
                         StringComparison.Ordinal))
            {
                if (!property.Value.TryGetInt32(out limit))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return hasQuery
               && !string.IsNullOrWhiteSpace(query)
               && Encoding.UTF8.GetByteCount(query)
               <= limits.MaxSearchQueryUtf8Bytes
               && limit is >= 1
                   && limit <= limits.MaxSearchResults;
    }

    private static bool TryReadActivationControl(
        JsonElement arguments,
        out PreparedToolActivation? activation)
    {
        activation = null;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? name = null;
        string? version = null;
        string? descriptorDigest = null;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.Add(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (property.Name)
            {
                case "name":
                    name = property.Value.GetString();
                    break;
                case "version":
                    version = property.Value.GetString();
                    break;
                case "descriptorDigest":
                    descriptorDigest = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (!IsBoundedControlString(name, 96)
            || !IsBoundedControlString(version, 32)
            || !IsBoundedControlString(descriptorDigest, 256))
        {
            return false;
        }

        activation = new PreparedToolActivation(
            name!,
            version!,
            descriptorDigest!);
        return true;
    }

    private static bool IsBoundedControlString(
        string? value,
        int maximumUtf8Bytes)
    {
        return !string.IsNullOrWhiteSpace(value)
               && Encoding.UTF8.GetByteCount(value)
               <= maximumUtf8Bytes;
    }

    private ActionRequest ActionRequestFor(
        AgentRun run,
        ToolExecutionRequest call,
        DateTimeOffset runDeadline)
    {
        var requestedAt = _clock.UtcNow;
        var toolDeadline = requestedAt.AddMilliseconds(call.Tool.TimeoutMs);
        var effectiveDeadline = toolDeadline < runDeadline
            ? toolDeadline
            : runDeadline;
        var monotonicDeadline = MonotonicDeadline.Start(
            effectiveDeadline > requestedAt
                ? effectiveDeadline - requestedAt
                : TimeSpan.Zero);
        var request = new ActionRequest
        {
            OperationId = _ids.NewId("operation"),
            RunId = run.RunId,
            TurnId = call.TurnId,
            ToolCallId = call.ToolCallId,
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            ActionName = call.Tool.Name,
            ActionVersion = call.Tool.Version,
            Arguments = call.Arguments.Clone(),
            DecisionKey = run.DecisionKey,
            BatchId = run.BatchId,
            ExpectedEffects = call.ResolvedConflictKeys.ToList(),
            RequestedAt = requestedAt,
            Deadline = effectiveDeadline
        };
        var gameContext = GameContextEnvelope.ValidateForRun(
            run,
            nameof(run));
        if (gameContext is not null)
        {
            request.BasedOnStateVersion = gameContext.StateVersion
                                          ?? gameContext.Causality
                                              ?.BasedOnStateVersion;
            request.Extensions[GameContextEnvelope.ExtensionName] =
                GameContextEnvelope.ToJson(gameContext);
        }

        call.BindExecutionDeadline(
            effectiveDeadline,
            monotonicDeadline);
        if (call.Tool.ResultSchema.HasValue)
        {
            request.Extensions[RunRecovery.ResultSchemaExtension] =
                call.Tool.ResultSchema.Value.Clone();
        }

        return request;
    }

    private ActionReceipt UnknownReceipt(
        ActionRequest request,
        string errorCode)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 0,
            Status = ReceiptStatuses.Unknown,
            ErrorCode = errorCode,
            Retryable = false,
            ReceivedAt = _clock.UtcNow
        };
    }

    private ActionReceipt FailedReceipt(
        ActionRequest request,
        string errorCode)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 0,
            Status = ReceiptStatuses.Failed,
            ErrorCode = errorCode,
            Retryable = true,
            ReceivedAt = _clock.UtcNow
        };
    }

    private NormalizedMessage ToolErrorMessage(
        ModelToolCall call,
        string code,
        string message)
    {
        return new NormalizedMessage
        {
            MessageId = _ids.NewId("tool-error-message"),
            Role = NormalizedRoles.Tool,
            CreatedAt = _clock.UtcNow,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromToolResult(
                    call.ToolCallId,
                    call.Name,
                    RuntimePromptBuilder.ErrorPayload(
                        code,
                        "tool_validation",
                        message))
            }
        };
    }

    private async ValueTask<ControlBatch> DrainControlsAsync(
        AgentRun run,
        RuntimeControlPlane.Registration registration,
        CancellationToken cancellationToken)
    {
        var batch = new ControlBatch();
        foreach (var command in registration.Drain())
        {
            if (command.Observation is not null)
            {
                ObservationAdmission.EnsureVisibleToRun(
                    command.Observation,
                    run,
                    _options
                        .RequireAudienceIncarnationForRestrictedObservations);
            }

            await _journal.AppendBuiltInDurableAsync(
                    run,
                    RuntimeEventKinds.ControlReceived,
                    RuntimePromptBuilder.ControlPayload(command),
                    run.CurrentTurnId,
                    attemptId: null,
                    eventId: "control:" + command.CommandId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (command.Observation is not null)
            {
                batch.Context.Add(
                    ContextCandidate.FromObservation(
                        command.Observation,
                        run,
                        required: true,
                        canDefer: false));
            }

            batch.Cancel |= string.Equals(
                command.Kind,
                RunControlKinds.Cancel,
                StringComparison.Ordinal);
            batch.Interrupt |= string.Equals(
                command.Kind,
                RunControlKinds.Interrupt,
                StringComparison.Ordinal);
            batch.Steer |= string.Equals(
                command.Kind,
                RunControlKinds.Steer,
                StringComparison.Ordinal);
            batch.FollowUp |= string.Equals(
                command.Kind,
                RunControlKinds.FollowUp,
                StringComparison.Ordinal);
        }

        return batch;
    }

    private async ValueTask<DurableRunOutcome> CancelRecoveredRunAsync(
        RecoveredRun recovered,
        IReadOnlyList<NormalizedMessage> transcript,
        IGameOperationReconciler? reconciler,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var run = recovered.Run;
        await SettleRecoveredProviderDispatchesAsync(
                run,
                recovered.UnsettledProviderDispatches,
                startedAt,
                terminalRun: true)
            .ConfigureAwait(false);
        if (RunStateMachine.IsTerminal(run.State))
        {
            return Outcome(run, transcript, recovered.FinalOutput);
        }

        if (recovered.PendingOperations.Count == 0)
        {
            await AdvanceRecoveredGameContextAsync(
                    recovered,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(
                    run.State,
                    RunStates.Running,
                    StringComparison.Ordinal)
                || string.Equals(
                    run.State,
                    RunStates.WaitingForAction,
                    StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAndMutationAsync(
                        run,
                        RunStates.Cancelling,
                        RuntimeEventKinds.RunCheckpoint,
                        next => UpdateDuration(next, startedAt),
                        completionIntent: CompletionIntents.Cancelled,
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Cancelled,
                    RuntimeEventKinds.RunCancelled,
                    next =>
                    {
                        next.CurrentTurnId = null;
                        UpdateDuration(next, startedAt);
                    },
                    terminalReason: RunControlKinds.Cancel,
                    completionIntent: CompletionIntents.Cancelled,
                    turnId: run.CurrentTurnId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return Outcome(run, transcript);
        }

        if (string.Equals(
                run.State,
                RunStates.Running,
                StringComparison.Ordinal)
            || string.Equals(
                run.State,
                RunStates.WaitingForAction,
                StringComparison.Ordinal)
            || string.Equals(
                run.State,
                RunStates.Reconciling,
                StringComparison.Ordinal)
            && !string.Equals(
                run.CompletionIntent,
                CompletionIntents.Cancelled,
                StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Cancelling,
                    RuntimeEventKinds.RunCheckpoint,
                    next => UpdateDuration(next, startedAt),
                    completionIntent: CompletionIntents.Cancelled,
                    turnId: run.CurrentTurnId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (string.Equals(
                run.State,
                RunStates.Cancelling,
                StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Reconciling,
                    RuntimeEventKinds.ActionReconciling,
                    next => UpdateDuration(next, startedAt),
                    completionIntent: CompletionIntents.Cancelled,
                    turnId: run.CurrentTurnId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (reconciler is null)
        {
            return Outcome(run, transcript);
        }

        try
        {
            recovered = await _recovery.ReconcileAsync(
                    recovered,
                    reconciler,
                    _ids.NewId("reconcile-attempt"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Outcome(run, transcript);
        }

        transcript = recovered.Transcript.ToArray();
        if (recovered.PendingOperations.Count > 0)
        {
            return Outcome(run, transcript);
        }

        await AdvanceRecoveredGameContextAsync(
                recovered,
                CancellationToken.None)
            .ConfigureAwait(false);
        await TransitionAfterReconciliationAsync(
                run,
                RunStates.Cancelled,
                startedAt,
                CancellationToken.None)
            .ConfigureAwait(false);
        return Outcome(run, transcript);
    }

    private async ValueTask CompleteControlAsync(
        AgentRun run,
        string kind,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken,
        DurableTerminalOutcome? terminalError = null)
    {
        var cancelling = string.Equals(
            kind,
            RunControlKinds.Cancel,
            StringComparison.Ordinal);
        var intermediate = cancelling
            ? RunStates.Cancelling
            : RunStates.Interrupting;
        var terminal = cancelling
            ? RunStates.Cancelled
            : RunStates.Interrupted;
        var intent = cancelling
            ? CompletionIntents.Cancelled
            : CompletionIntents.Interrupted;

        if (run.PendingOperationIds.Count > 0)
        {
            if (string.Equals(
                    run.State,
                    RunStates.WaitingForAction,
                    StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAsync(
                        run,
                        intermediate,
                        RuntimeEventKinds.RunCheckpoint,
                        completionIntent: intent,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(run.State, intermediate, StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAsync(
                        run,
                        RunStates.Reconciling,
                        RuntimeEventKinds.ActionReconciling,
                        completionIntent: intent,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (string.Equals(run.State, RunStates.Preparing, StringComparison.Ordinal)
            || string.Equals(run.State, RunStates.Queued, StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Cancelled,
                    RuntimeEventKinds.RunCancelled,
                    next =>
                    {
                        next.CurrentTurnId = null;
                        UpdateDuration(next, startedAt);
                    },
                    terminalReason: kind,
                    completionIntent: intent,
                    cancellationToken: cancellationToken,
                    eventExtensions: terminalError is null
                        ? null
                        : TerminalOutcomeJournalCodec.Extensions(
                            terminalError))
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(run.State, RunStates.Running, StringComparison.Ordinal)
            || string.Equals(
                run.State,
                RunStates.WaitingForAction,
                StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAsync(
                    run,
                    intermediate,
                    RuntimeEventKinds.RunCheckpoint,
                    completionIntent: intent,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(run.State, intermediate, StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    terminal,
                    cancelling
                        ? RuntimeEventKinds.RunCancelled
                        : RuntimeEventKinds.RunInterrupted,
                    next =>
                    {
                        next.CurrentTurnId = null;
                        UpdateDuration(next, startedAt);
                    },
                    terminalReason: kind,
                    completionIntent: intent,
                    cancellationToken: cancellationToken,
                    eventExtensions: terminalError is null
                        ? null
                        : TerminalOutcomeJournalCodec.Extensions(
                            terminalError))
                .ConfigureAwait(false);
        }
    }

    private async ValueTask CompleteBudgetAsync(
        AgentRun run,
        string reason,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await _journal.CommitTransitionAndMutationAsync(
                run,
                RunStates.BudgetExhausted,
                RuntimeEventKinds.RunBudgetExhausted,
                next =>
                {
                    next.CurrentTurnId = null;
                    UpdateDuration(next, startedAt);
                },
                terminalReason: reason,
                turnId: run.CurrentTurnId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<DurableRunOutcome> CompleteToolLoopFailureAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        SemanticToolLoopGuard guard,
        DateTimeOffset startedAt)
    {
        await _journal.CommitTransitionAndMutationAsync(
                run,
                RunStates.Failed,
                RuntimeEventKinds.RunFailed,
                next =>
                {
                    next.CurrentTurnId = null;
                    next.Extensions["toolLoopGuard"] =
                        guard.SafeDiagnostic();
                    UpdateDuration(next, startedAt);
                },
                terminalReason: SemanticToolLoopGuard.HardStopReasonCode,
                completionIntent: CompletionIntents.Failed,
                turnId: run.CurrentTurnId,
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return new DurableRunOutcome
        {
            Run = run,
            Transcript = transcript.ToArray(),
            ErrorCode = SemanticToolLoopGuard.HardStopReasonCode,
            ErrorCategory = "tool_loop",
            SafeErrorMessage =
                "The agent repeated a tool outcome without observable progress."
        };
    }

    private async ValueTask ChargeAndEnforceProviderUsageAsync(
        AgentRun run,
        ProviderUsageNotice notice,
        BudgetTracker budget,
        DateTimeOffset startedAt,
        string turnId)
    {
        var chargedInputTokens =
            (long)run.Usage.InputTokens + notice.Usage.InputTokens;
        var chargedOutputTokens =
            (long)run.Usage.OutputTokens + notice.Usage.OutputTokens;
        var tokenUsageOverflowed =
            chargedInputTokens > int.MaxValue
            || chargedOutputTokens > int.MaxValue;
        await _journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.BudgetUpdated,
                next =>
                {
                    ProviderUsageAccounting.AccumulateDetails(
                        next.Usage,
                        notice.Usage);
                    next.Usage.InputTokens = (int)Math.Min(
                        int.MaxValue,
                        chargedInputTokens);
                    next.Usage.OutputTokens = (int)Math.Min(
                        int.MaxValue,
                        chargedOutputTokens);
                    if (string.Equals(
                            next.Usage.Availability,
                            UsageAvailabilityStates.CostAvailable,
                            StringComparison.Ordinal))
                    {
                        next.Usage.CostUsd = RuntimePromptBuilder.AddCost(
                            next.Usage.CostUsd,
                            notice.Usage.CostUsd);
                    }
                    else
                    {
                        next.Usage.CostUsd =
                            RuntimePromptBuilder.AddCost(
                                next.Usage.CostUsd,
                                notice.Usage.CostUsd);
                        next.Usage.HasUnaccountedUsage = true;
                        next.Usage.UnaccountedProviderAttempts =
                            next.Usage.UnaccountedProviderAttempts
                            == int.MaxValue
                                ? int.MaxValue
                                : next.Usage
                                      .UnaccountedProviderAttempts
                                  + 1;
                    }

                    UpdateDuration(next, startedAt);
                },
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-usage:" + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId,
                eventExtensions:
                    new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal)
                    {
                        [ProviderCacheTelemetry.UsageExtensionName] =
                            ProviderCacheUsageEvidence
                                .FromUsage(notice.Usage)
                                .ToJson()
                    })
            .ConfigureAwait(false);

        var decision = tokenUsageOverflowed
            ? new BudgetDecision
            {
                Allowed = false,
                Reason = "max_tokens"
            }
            : budget.CheckAfterCharge(run.Usage, _clock.UtcNow);
        if (!decision.Allowed)
        {
            await MarkProviderResultDiscardedAsync(
                    run,
                    new ProviderResultDiscardedNotice
                    {
                        ProviderId = notice.ProviderId,
                        ProviderAttemptId = notice.ProviderAttemptId,
                        StreamAttemptId = notice.StreamAttemptId,
                        ReasonCode = "provider_budget_exceeded"
                    },
                    startedAt,
                    turnId)
                .ConfigureAwait(false);
            throw new ProviderBudgetExceededException(decision.Reason!);
        }
    }

    private async ValueTask MarkProviderDispatchAsync(
        AgentRun run,
        ProviderDispatchNotice notice,
        DateTimeOffset startedAt,
        string turnId)
    {
        var wireEvidence = notice.WireRequestEvidence.ToJson();
        await _journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                next => UpdateDuration(next, startedAt),
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-dispatch:" + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId,
                modelId: notice.RouteIdentity.ModelId,
                transportDialect:
                    notice.RouteIdentity.TransportDialect,
                providerCapabilityDigest:
                    notice.RouteIdentity.CapabilityDigest,
                providerRouteDigest:
                    notice.RouteIdentity.RouteDigest,
                eventExtensions:
                    new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal)
                    {
                        ["providerRequestPreparation"] =
                            notice.RequestPreparation.ToSnapshotExtension(),
                        [ProviderWireRequestEvidence.JournalExtensionName] =
                            wireEvidence,
                        [ProviderWireRequestEvidence
                                .IntegrityDigestJournalExtensionName] =
                            JsonArrayBuilder.String(
                                CanonicalJsonDigest.ComputeSha256(
                                    wireEvidence)),
                        [ProviderWireRequestEvidence
                                .DialectSemanticDigestJournalExtensionName] =
                            JsonArrayBuilder.String(
                                notice.RouteIdentity.DialectSemanticDigest),
                        [ProviderDialectContract.JournalExtensionName] =
                            notice.RouteIdentity.DialectContract.ToJson(),
                        [ProviderRouteJournalExtensions.PolicyVersion] =
                            JsonArrayBuilder.String(
                                notice.RouteIdentity.RoutePolicyVersion),
                        [ProviderRouteJournalExtensions.PolicyDigest] =
                            JsonArrayBuilder.String(
                                notice.RouteIdentity.RoutePolicyDigest)
                    })
            .ConfigureAwait(false);
    }

    private async ValueTask MarkProviderDispatchKnownZeroAsync(
        AgentRun run,
        ProviderDispatchKnownZeroNotice notice,
        DateTimeOffset startedAt,
        string turnId)
    {
        await _journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderDispatchKnownZero,
                next => UpdateDuration(next, startedAt),
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-known-zero:" + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId,
                reasonCode: notice.ReasonCode)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> SettleRecoveredProviderDispatchesAsync(
        AgentRun run,
        IReadOnlyList<RecoveredProviderDispatch> dispatches,
        DateTimeOffset startedAt,
        bool terminalRun)
    {
        var lostBilledResult = false;
        foreach (var dispatch in dispatches)
        {
            if (dispatch.UsageSettled)
            {
                lostBilledResult = true;
                await MarkProviderResultDiscardedAsync(
                        run,
                        new ProviderResultDiscardedNotice
                        {
                            ProviderId = dispatch.ProviderId,
                            ProviderAttemptId =
                                dispatch.ProviderAttemptId,
                            StreamAttemptId = dispatch.StreamAttemptId,
                            ReasonCode = terminalRun
                                ? "terminal_provider_result_recovery"
                                : "provider_result_recovery_required"
                        },
                        startedAt,
                        dispatch.TurnId)
                    .ConfigureAwait(false);
                continue;
            }

            await MarkProviderUsageUncertainAsync(
                    run,
                    new ProviderUsageUncertainNotice
                    {
                        ProviderId = dispatch.ProviderId,
                        ProviderAttemptId =
                            dispatch.ProviderAttemptId,
                        StreamAttemptId = dispatch.StreamAttemptId,
                        ReasonCode = terminalRun
                            ? "terminal_provider_dispatch_unknown"
                            : "provider_dispatch_recovery_unknown"
                    },
                    startedAt,
                    dispatch.TurnId)
                .ConfigureAwait(false);
        }

        return lostBilledResult;
    }

    private async ValueTask MarkProviderResultDiscardedAsync(
        AgentRun run,
        ProviderResultDiscardedNotice notice,
        DateTimeOffset startedAt,
        string turnId)
    {
        await _journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderResultDiscarded,
                next => UpdateDuration(next, startedAt),
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-result-discarded:"
                         + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId,
                reasonCode: notice.ReasonCode)
            .ConfigureAwait(false);
    }

    private async ValueTask MarkProviderUsageUncertainAsync(
        AgentRun run,
        ProviderUsageUncertainNotice notice,
        DateTimeOffset startedAt,
        string turnId)
    {
        await _journal.CommitRunMutationAsync(
                run,
                RuntimeEventKinds.ProviderUsageUncertain,
                next =>
                {
                    next.Usage.HasUnaccountedUsage = true;
                    next.Usage.UnaccountedProviderAttempts =
                        next.Usage.UnaccountedProviderAttempts == int.MaxValue
                            ? int.MaxValue
                            : next.Usage.UnaccountedProviderAttempts + 1;
                    UpdateDuration(next, startedAt);
                },
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-usage-uncertain:"
                         + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId,
                reasonCode: notice.ReasonCode)
            .ConfigureAwait(false);
    }

    private ValueTask<DurableRunOutcome> FailUnaccountedUsageAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        DateTimeOffset startedAt)
    {
        return FailOrCancelAsync(
            run,
            transcript,
            new ProviderException(
                "provider_usage_reconciliation_required",
                "billing",
                "Provider usage must be reconciled before starting another run.",
                false),
            startedAt,
            cancellationRequested: false);
    }

    private ValueTask<DurableRunOutcome> FailProviderResultRecoveryAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        DateTimeOffset startedAt)
    {
        return FailOrCancelAsync(
            run,
            transcript,
            new ProviderException(
                "provider_result_recovery_required",
                "provider",
                "A billed provider result was not durably recorded.",
                false),
            startedAt,
            cancellationRequested: false);
    }

    private static DurableRunOutcome UnaccountedUsageOutcome(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript)
    {
        return new DurableRunOutcome
        {
            Run = run,
            Transcript = transcript.ToArray(),
            ErrorCode = "provider_usage_reconciliation_required",
            ErrorCategory = "billing",
            SafeErrorMessage =
                "Provider usage and pending game operations require reconciliation."
        };
    }

    private async ValueTask<DurableRunOutcome> CompleteDurationDeadlineAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        DateTimeOffset startedAt)
    {
        if (RunStateMachine.IsTerminal(run.State))
        {
            return Outcome(run, transcript);
        }

        if (run.PendingOperationIds.Count > 0)
        {
            if (string.Equals(
                    run.State,
                    RunStates.WaitingForAction,
                    StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAndMutationAsync(
                        run,
                        RunStates.Reconciling,
                        RuntimeEventKinds.ActionReconciling,
                        next =>
                        {
                            UpdateDuration(next, startedAt);
                        },
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(
                         run.State,
                         RunStates.Cancelling,
                         StringComparison.Ordinal)
                     || string.Equals(
                         run.State,
                         RunStates.Interrupting,
                         StringComparison.Ordinal))
            {
                await _journal.CommitTransitionAndMutationAsync(
                        run,
                        RunStates.Reconciling,
                        RuntimeEventKinds.ActionReconciling,
                        next => UpdateDuration(next, startedAt),
                        completionIntent: run.CompletionIntent,
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            if (string.Equals(
                    run.State,
                    RunStates.Reconciling,
                    StringComparison.Ordinal)
                && !string.Equals(
                    run.TerminalReason,
                    "max_duration",
                    StringComparison.Ordinal))
            {
                await _journal.CommitRunMutationAsync(
                        run,
                        RuntimeEventKinds.BudgetUpdated,
                        next =>
                        {
                            next.TerminalReason = "max_duration";
                            next.CompletionIntent = null;
                            UpdateDuration(next, startedAt);
                        },
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None,
                        reasonCode: "max_duration")
                    .ConfigureAwait(false);
            }

            return Outcome(run, transcript);
        }

        if (string.Equals(
                run.State,
                RunStates.WaitingForAction,
                StringComparison.Ordinal)
            || string.Equals(
                run.State,
                RunStates.Reconciling,
                StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAndMutationAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.TurnCompleted,
                    next =>
                    {
                        next.CurrentTurnId = null;
                        UpdateDuration(next, startedAt);
                    },
                    turnId: run.CurrentTurnId,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        else if (string.Equals(
                     run.State,
                     RunStates.Preparing,
                     StringComparison.Ordinal))
        {
            await _journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunCheckpoint,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (string.Equals(run.State, RunStates.Running, StringComparison.Ordinal))
        {
            await CompleteBudgetAsync(
                    run,
                    "max_duration",
                    startedAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return Outcome(run, transcript);
    }

    private ValueTask CompleteTurnWithoutStateChangeAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        DateTimeOffset startedAt,
        string outcome,
        CancellationToken cancellationToken)
    {
        return _journal.CommitRunMutationAsync(
            run,
            RuntimeEventKinds.TurnCompleted,
            next =>
            {
                next.CurrentTurnId = null;
                next.Extensions["turnOutcome"] =
                    JsonArrayBuilder.String(outcome);
                UpdateDuration(next, startedAt);
            },
            turnId,
            attemptId,
            cancellationToken);
    }

    private async ValueTask TransitionAfterReconciliationAsync(
        AgentRun run,
        string terminalState,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await _journal.CommitTransitionAndMutationAsync(
                run,
                terminalState,
                terminalState == RunStates.Cancelled
                    ? RuntimeEventKinds.RunCancelled
                    : terminalState == RunStates.Interrupted
                        ? RuntimeEventKinds.RunInterrupted
                        : RuntimeEventKinds.RunFailed,
                next =>
                {
                    next.CurrentTurnId = null;
                    UpdateDuration(next, startedAt);
                },
                terminalReason: run.CompletionIntent,
                completionIntent: run.CompletionIntent,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<DurableRunOutcome> FailOrCancelAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        Exception exception,
        DateTimeOffset startedAt,
        bool cancellationRequested)
    {
        var code = exception switch
        {
            ProviderException provider => provider.Code,
            SkillAdmissionException admission => admission.ReasonCode,
            SkillContentResolutionException skillContent =>
                skillContent.ReasonCode,
            RuntimeMemoryIntegrationException memory => memory.ReasonCode,
            GameContextAdvancementException gameContext =>
                gameContext.ReasonCode,
            DurableExecutionPolicyMismatchException =>
                DurableExecutionPolicyMismatchException.ReasonCode,
            _ when cancellationRequested => "run_cancelled",
            _ => "runtime_failure"
        };
        var category = exception switch
        {
            ProviderException provider => provider.Category,
            SkillAdmissionException => "skill_admission",
            SkillContentResolutionException => "skill_content",
            RuntimeMemoryIntegrationException => "memory",
            GameContextAdvancementException => "game_context",
            DurableExecutionPolicyMismatchException => "execution_policy",
            _ when cancellationRequested => "control",
            _ => "runtime"
        };
        var safeMessage = exception switch
        {
            ProviderException provider => provider.Message,
            SkillAdmissionException =>
                "An activated skill was not admitted for this turn.",
            SkillContentResolutionException =>
                "Required skill context could not be resolved safely.",
            RuntimeMemoryIntegrationException =>
                "The runtime-managed memory operation failed.",
            GameContextAdvancementException =>
                "The authoritative game-context transition was rejected.",
            DurableExecutionPolicyMismatchException =>
                "The executable runtime policy changed before admission.",
            _ when cancellationRequested => "The run was cancelled.",
            _ => "The runtime failed. Inspect the structured trace."
        };
        var terminalError = new DurableTerminalOutcome(
            code,
            category,
            safeMessage);
        var terminalErrorExtensions =
            TerminalOutcomeJournalCodec.Extensions(terminalError);

        try
        {
            if (run.PendingOperationIds.Count > 0)
            {
                if (string.Equals(
                        run.State,
                        RunStates.WaitingForAction,
                        StringComparison.Ordinal))
                {
                    await _journal.CommitTransitionAsync(
                            run,
                            RunStates.Reconciling,
                            RuntimeEventKinds.ActionReconciling,
                            completionIntent: cancellationRequested
                                ? CompletionIntents.Cancelled
                                : CompletionIntents.Failed,
                            cancellationToken: CancellationToken.None,
                            eventExtensions: terminalErrorExtensions)
                        .ConfigureAwait(false);
                }
                else if (cancellationRequested
                         && string.Equals(
                             run.State,
                             RunStates.Reconciling,
                             StringComparison.Ordinal)
                         && run.CompletionIntent is null)
                {
                    await _journal.CommitTransitionAndMutationAsync(
                            run,
                            RunStates.Cancelling,
                            RuntimeEventKinds.RunCheckpoint,
                            next => UpdateDuration(next, startedAt),
                            completionIntent: CompletionIntents.Cancelled,
                            turnId: run.CurrentTurnId,
                            cancellationToken: CancellationToken.None,
                            eventExtensions: terminalErrorExtensions)
                        .ConfigureAwait(false);
                    await _journal.CommitTransitionAndMutationAsync(
                            run,
                            RunStates.Reconciling,
                            RuntimeEventKinds.ActionReconciling,
                            next => UpdateDuration(next, startedAt),
                            completionIntent: CompletionIntents.Cancelled,
                            turnId: run.CurrentTurnId,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else if (!RunStateMachine.IsTerminal(run.State))
            {
                if (cancellationRequested)
                {
                    await CompleteControlAsync(
                            run,
                            RunControlKinds.Cancel,
                            startedAt,
                            CancellationToken.None,
                            terminalError)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (string.Equals(
                            run.State,
                            RunStates.WaitingForAction,
                            StringComparison.Ordinal))
                    {
                        await _journal.CommitTransitionAsync(
                                run,
                                RunStates.Running,
                                RuntimeEventKinds.RunCheckpoint,
                                cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    await _journal.CommitTransitionAndMutationAsync(
                            run,
                            RunStates.Failed,
                            RuntimeEventKinds.RunFailed,
                            next =>
                            {
                                next.CurrentTurnId = null;
                                UpdateDuration(next, startedAt);
                            },
                            terminalReason: code,
                            completionIntent: CompletionIntents.Failed,
                            cancellationToken: CancellationToken.None,
                            eventExtensions: terminalErrorExtensions)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // The caller still receives the original safe failure. A journal
            // failure cannot be disguised as a successfully persisted terminal.
        }

        return new DurableRunOutcome
        {
            Run = run,
            Transcript = transcript.ToArray(),
            ErrorCode = code,
            ErrorCategory = category,
            SafeErrorMessage = safeMessage
        };
    }

    private ValueTask PublishProviderEventAsync(
        AgentRun run,
        ModelStreamEvent item)
    {
        if (string.Equals(
                item.Kind,
                ModelStreamEventKinds.ReasoningDelta,
                StringComparison.Ordinal))
        {
            return default;
        }

        JsonElement payload;
        if (string.Equals(
                item.Kind,
                ModelStreamEventKinds.TextDelta,
                StringComparison.Ordinal))
        {
            payload = _finalOutputAdmission is null
                ? JsonArrayBuilder.Object(
                    ("kind", JsonArrayBuilder.String(item.Kind)),
                    ("text", JsonArrayBuilder.String(
                        item.TextDelta ?? string.Empty)))
                : JsonArrayBuilder.Object(
                    ("kind", JsonArrayBuilder.String(item.Kind)),
                    ("text", JsonArrayBuilder.String(
                        item.TextDelta ?? string.Empty)),
                    ("presentationState", JsonArrayBuilder.String(
                        "provisional")));
        }
        else if (string.Equals(
                     item.Kind,
                     ModelStreamEventKinds.ToolCallDelta,
                     StringComparison.Ordinal))
        {
            var properties = new List<(string Name, JsonElement Value)>
            {
                ("kind", JsonArrayBuilder.String(item.Kind)),
                ("toolCallId", item.ToolCallId is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(item.ToolCallId)),
                ("toolNameDelta", item.ToolNameDelta is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(item.ToolNameDelta)),
                ("argumentsJsonDelta", item.ArgumentsJsonDelta is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(item.ArgumentsJsonDelta))
            };
            if (_finalOutputAdmission is not null)
            {
                properties.Add((
                    "presentationState",
                    JsonArrayBuilder.String("provisional")));
            }

            payload = JsonArrayBuilder.Object(properties.ToArray());
        }
        else if (string.Equals(
                     item.Kind,
                     ModelStreamEventKinds.Usage,
                     StringComparison.Ordinal)
                 && item.Usage is not null)
        {
            var properties = new List<(string Name, JsonElement Value)>
            {
                ("kind", JsonArrayBuilder.String(item.Kind)),
                ("inputTokens", JsonArrayBuilder.Number(
                    item.Usage.InputTokens)),
                ("outputTokens", JsonArrayBuilder.Number(
                    item.Usage.OutputTokens)),
                ("cacheReadTokens", item.Usage.CacheReadTokens.HasValue
                    ? JsonArrayBuilder.Number(
                        item.Usage.CacheReadTokens.Value)
                    : JsonArrayBuilder.Null()),
                ("cacheWriteTokens", item.Usage.CacheWriteTokens.HasValue
                    ? JsonArrayBuilder.Number(
                        item.Usage.CacheWriteTokens.Value)
                    : JsonArrayBuilder.Null()),
                ("reasoningTokens", item.Usage.ReasoningTokens.HasValue
                    ? JsonArrayBuilder.Number(
                        item.Usage.ReasoningTokens.Value)
                    : JsonArrayBuilder.Null()),
                ("providerTotalTokens",
                    item.Usage.ProviderTotalTokens.HasValue
                        ? JsonArrayBuilder.Number(
                            item.Usage.ProviderTotalTokens.Value)
                        : JsonArrayBuilder.Null()),
                ("costUsd", JsonArrayBuilder.String(item.Usage.CostUsd)),
                ("availability", JsonArrayBuilder.String(
                    item.Usage.Availability))
            };
            if (_finalOutputAdmission is not null)
            {
                properties.Add((
                    "presentationState",
                    JsonArrayBuilder.String("provisional")));
            }

            payload = JsonArrayBuilder.Object(properties.ToArray());
        }
        else if (string.Equals(
                     item.Kind,
                     ModelStreamEventKinds.Completed,
                     StringComparison.Ordinal))
        {
            var properties = new List<(string Name, JsonElement Value)>
            {
                ("kind", JsonArrayBuilder.String(item.Kind)),
                ("finishReason", item.FinishReason is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(item.FinishReason))
            };
            if (_finalOutputAdmission is not null)
            {
                properties.Add((
                    "presentationState",
                    JsonArrayBuilder.String("provisional")));
            }

            payload = JsonArrayBuilder.Object(properties.ToArray());
        }
        else
        {
            payload = _finalOutputAdmission is null
                ? JsonArrayBuilder.Object(
                    ("kind", JsonArrayBuilder.String(item.Kind)))
                : JsonArrayBuilder.Object(
                    ("kind", JsonArrayBuilder.String(item.Kind)),
                    ("presentationState", JsonArrayBuilder.String(
                        "provisional")));
        }

        _journal.PublishEphemeral(
            run,
            RuntimeEventKinds.AssistantDelta,
            payload,
            run.CurrentTurnId,
            attemptId: null,
            item.StreamAttemptId,
            Interlocked.Increment(ref _ephemeralSequence) - 1);
        return default;
    }

    private void PublishToolProgress(
        AgentRun run,
        ActionRequest action,
        GameActionProgress progress,
        string? attemptId)
    {
        var payload = JsonArrayBuilder.Object(
            ("operationId", JsonArrayBuilder.String(action.OperationId)),
            ("toolCallId", JsonArrayBuilder.String(action.ToolCallId)),
            ("actionName", JsonArrayBuilder.String(action.ActionName)),
            ("stage", JsonArrayBuilder.String(progress.Stage)),
            ("message", progress.Message is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(progress.Message)),
            ("current", progress.Current.HasValue
                ? JsonArrayBuilder.Number(progress.Current.Value)
                : JsonArrayBuilder.Null()),
            ("total", progress.Total.HasValue
                ? JsonArrayBuilder.Number(progress.Total.Value)
                : JsonArrayBuilder.Null()),
            ("data", progress.Data?.Clone()
                ?? JsonArrayBuilder.Null()));
        _journal.PublishEphemeral(
            run,
            RuntimeEventKinds.ToolProgress,
            payload,
            action.TurnId,
            attemptId,
            streamAttemptId: null,
            Interlocked.Increment(ref _ephemeralSequence) - 1);
    }

    private void PublishProviderLifecycle(
        AgentRun run,
        ProviderAttemptNotice notice)
    {
        var payload = JsonArrayBuilder.Object(
            ("providerId", JsonArrayBuilder.String(notice.ProviderId)),
            ("nextProviderId", notice.NextProviderId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(notice.NextProviderId)),
            ("providerAttemptId", notice.ProviderAttemptId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(notice.ProviderAttemptId)),
            ("streamAttemptId", notice.StreamAttemptId is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.String(notice.StreamAttemptId)),
            ("attemptNumber", JsonArrayBuilder.Number(
                notice.AttemptNumber)),
            ("errorCode", JsonArrayBuilder.String(notice.ErrorCode)),
            ("errorCategory", JsonArrayBuilder.String(
                notice.ErrorCategory)),
            ("delayMs", JsonArrayBuilder.Number(
                notice.DelayMilliseconds)));
        _journal.PublishEphemeral(
            run,
            string.Equals(
                notice.Kind,
                ProviderAttemptNoticeKinds.Retry,
                StringComparison.Ordinal)
                ? RuntimeEventKinds.ProviderRetry
                : RuntimeEventKinds.ProviderFallback,
            payload,
            run.CurrentTurnId,
            attemptId: notice.ProviderAttemptId,
            streamAttemptId: notice.StreamAttemptId,
            Interlocked.Increment(ref _ephemeralSequence) - 1);
    }

    private async ValueTask AppendTranscriptOnceAsync(
        AgentRun run,
        ICollection<NormalizedMessage> transcript,
        NormalizedMessage message,
        string turnId,
        string? attemptId,
        CancellationToken cancellationToken)
    {
        if (transcript.Any(
                item => string.Equals(
                    item.MessageId,
                    message.MessageId,
                    StringComparison.Ordinal)))
        {
            return;
        }

        await _journal.AppendTranscriptAsync(
                run,
                message,
                turnId,
                attemptId,
                cancellationToken)
            .ConfigureAwait(false);
        transcript.Add(message);
    }

    private static IReadOnlyList<NormalizedMessage> DistinctTranscript(
        IReadOnlyList<NormalizedMessage> transcript)
    {
        var result = new List<NormalizedMessage>(transcript.Count);
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in transcript)
        {
            if (messageIds.Add(message.MessageId))
            {
                result.Add(message);
            }
        }

        return result;
    }

    private static IReadOnlyList<SkillActivationStateRecord>
        ReconcileActiveSkillState(
            IReadOnlyList<SkillReference> activeSkills,
            IReadOnlyList<SkillActivationStateRecord> priorState,
            SkillCatalogSnapshot snapshot)
    {
        var priorByReference = new Dictionary<
            string,
            SkillActivationStateRecord>(StringComparer.Ordinal);
        foreach (var record in priorState)
        {
            if (!priorByReference.TryAdd(record.Reference, record))
            {
                throw new InvalidDataException(
                    "The active-skill state contains duplicate references.");
            }
        }

        var result = new List<SkillActivationStateRecord>(
            activeSkills.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in activeSkills)
        {
            if (!seen.Add(reference.Value))
            {
                throw new ArgumentException(
                    "The active-skill list contains duplicate references.",
                    nameof(activeSkills));
            }

            if (!snapshot.TryGet(
                    reference.SkillId,
                    reference.Version,
                    out var skill)
                || skill is null)
            {
                throw new KeyNotFoundException(
                    $"Skill '{reference.Value}' is not in this snapshot.");
            }

            if (priorByReference.TryGetValue(
                    reference.Value,
                    out var prior)
                && !prior.Matches(skill))
            {
                throw new SkillAdmissionException(
                    SkillAdmissionReasonCodes.CatalogEntryChanged);
            }

            result.Add(
                prior?.Clone()
                ?? new SkillActivationStateRecord(
                    skill.SkillId,
                    skill.Version,
                    skill.ContentDigest));
        }

        return result;
    }

    private static bool IsSkillDisclosureMessage(NormalizedMessage message)
    {
        if (!string.Equals(
                message.Role,
                NormalizedRoles.System,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in message.Parts)
        {
            if (!part.Json.HasValue
                || part.Json.Value.ValueKind != JsonValueKind.Object
                || !part.Json.Value.TryGetProperty(
                    "contentType",
                    out var contentType)
                || contentType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(
                    contentType.GetString(),
                    "application/vnd.game-agent.skills+json",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRuntimeContextMessage(NormalizedMessage message)
    {
        if (!string.Equals(
                message.Role,
                NormalizedRoles.User,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in message.Parts)
        {
            if (part.Json.HasValue
                && part.Json.Value.ValueKind == JsonValueKind.Object
                && part.Json.Value.TryGetProperty(
                    "contentType",
                    out var contentType)
                && contentType.ValueKind == JsonValueKind.String
                && string.Equals(
                    contentType.GetString(),
                    "application/vnd.game-agent.context+json",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSkillContentMessage(NormalizedMessage message)
    {
        if (!string.Equals(
                message.Role,
                NormalizedRoles.User,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in message.Parts)
        {
            if (part.Json.HasValue
                && part.Json.Value.ValueKind == JsonValueKind.Object
                && part.Json.Value.TryGetProperty(
                    "contentType",
                    out var contentType)
                && contentType.ValueKind == JsonValueKind.String
                && string.Equals(
                    contentType.GetString(),
                    "application/vnd.game-agent.skill-content+json",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private DurableRunRequest Snapshot(
        DurableRunRequest request,
        CancellationToken cancellationToken)
    {
        var run = request.Run
            ?? throw new ArgumentException(
                "A durable run request requires a run.",
                nameof(request));
        var context = request.Context
            ?? throw new ArgumentException(
                "A durable run request requires a context collection.",
                nameof(request));
        var activeSkills = request.ActiveSkills
            ?? throw new ArgumentException(
                "A durable run request requires an active-skill collection.",
                nameof(request));
        var transcript = request.InitialTranscript
            ?? throw new ArgumentException(
                "A durable run request requires a transcript collection.",
                nameof(request));

        var contextSnapshot = RuntimeInputGuard.CopyBounded(
            context,
            _contextCompiler.MaxCandidates,
            candidate =>
            {
                if (candidate is null)
                {
                    throw new ArgumentException(
                        "Context collections cannot contain null entries.",
                        nameof(request));
                }
                _contextCompiler.ValidateCandidate(candidate);
                return Snapshot(candidate);
            },
            nameof(request.Context),
            "context_candidate_count_exceeded",
            cancellationToken);
        var activeSkillSnapshot = RuntimeInputGuard.CopyBounded(
            activeSkills,
            DurableRunInputJournalCodec.MaxActiveSkills,
            Snapshot,
            nameof(request.ActiveSkills),
            "activated_skill_count_exceeded",
            cancellationToken);
        DurableRunInputJournalCodec.ValidateEncodedSize(
            contextSnapshot,
            activeSkillSnapshot,
            request.WorkloadClass,
            request.ExecutionMode,
            request.Inference,
            request.RoutePreference);
        var transcriptInput = RuntimeInputGuard.CopyBounded(
            transcript,
            _options.MaxTranscriptMessages,
            message => message,
            nameof(request.InitialTranscript),
            "prompt_message_count_exceeded",
            cancellationToken);
        _ = RuntimePromptBuilder.MeasurePrompt(
            transcriptInput,
            Array.Empty<ToolDescriptor>(),
            _options.MaxTranscriptMessages,
            _options.MaxPromptUtf8Bytes,
            _options.EstimatedPromptBytesPerToken,
            _tokenEstimator);
        var transcriptSnapshot = RuntimeInputGuard.CopyBounded(
            transcriptInput,
            _options.MaxTranscriptMessages,
            Snapshot,
            nameof(request.InitialTranscript),
            "prompt_message_count_exceeded",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var safeRun =
            RuntimeProtocolInputGuard.ValidateAgentRunBeforeSerialization(
                run,
                DurableRunJsonLimits,
                DurableRunJsonLimits.MaxUtf8Bytes,
                nameof(request.Run));
        var encodedRun = ProtocolJson.ToElement(safeRun);
        JsonValueInspector.ValidateAndMeasure(
            encodedRun,
            DurableRunJsonLimits,
            nameof(request.Run));
        var runSnapshot = ProtocolJson.DeserializeAgentRun(
            encodedRun.GetRawText());

        return new DurableRunRequest
        {
            Run = runSnapshot,
            Context = contextSnapshot,
            ActiveSkills = activeSkillSnapshot,
            InitialTranscript = transcriptSnapshot,
            LaneId = request.LaneId,
            WorkloadClass = ProviderWorkloadClasses.Normalize(
                request.WorkloadClass,
                nameof(request.WorkloadClass)),
            ExecutionMode = DurableExecutionModes.Normalize(
                request.ExecutionMode,
                nameof(request.ExecutionMode)),
            Inference = request.Inference?.CloneValidated(),
            RoutePreference = request.RoutePreference?.CloneValidated(),
            FinalOutputContract =
                request.FinalOutputContract?.Snapshot()
        };
    }

    private DurableRunContinuation Snapshot(
        DurableRunContinuation continuation,
        CancellationToken cancellationToken)
    {
        var context = continuation.Context
            ?? throw new ArgumentException(
                "A durable continuation requires a context collection.",
                nameof(continuation));
        var activeSkills = continuation.ActiveSkills
            ?? throw new ArgumentException(
                "A durable continuation requires an active-skill collection.",
                nameof(continuation));
        var activeSkillSnapshot = RuntimeInputGuard.CopyBounded(
            activeSkills,
            DurableRunInputJournalCodec.MaxActiveSkills,
            Snapshot,
            nameof(continuation.ActiveSkills),
            "activated_skill_count_exceeded",
            cancellationToken);
        DurableRunInputJournalCodec.ValidateUniqueActiveSkills(
            activeSkillSnapshot);
        return new DurableRunContinuation
        {
            Context = RuntimeInputGuard.CopyBounded(
                context,
                _contextCompiler.MaxCandidates,
                candidate =>
                {
                    if (candidate is null)
                    {
                        throw new ArgumentException(
                            "Context collections cannot contain null entries.",
                            nameof(continuation));
                    }
                    _contextCompiler.ValidateCandidate(candidate);
                    return Snapshot(candidate);
                },
                nameof(continuation.Context),
                "context_candidate_count_exceeded",
                cancellationToken),
            ActiveSkills = activeSkillSnapshot,
            ReplaceActiveSkills = continuation.ReplaceActiveSkills,
            LaneId = continuation.LaneId,
            WorkloadClass = continuation.WorkloadClass is null
                ? null
                : ProviderWorkloadClasses.Normalize(
                    continuation.WorkloadClass,
                    nameof(continuation.WorkloadClass)),
            RequestCancellation = continuation.RequestCancellation,
            FinalOutputContract =
                continuation.FinalOutputContract?.Snapshot()
        };
    }

    private static DurableRunResumeGuard Snapshot(
        DurableRunResumeGuard guard)
    {
        var expectedBatchId = guard.ExpectedBatchId is null
            ? null
            : RuntimeGuard.RequiredId(
                guard.ExpectedBatchId,
                nameof(guard.ExpectedBatchId));
        var expectedAgentId = guard.ExpectedAgentId is null
            ? null
            : RuntimeGuard.RequiredId(
                guard.ExpectedAgentId,
                nameof(guard.ExpectedAgentId));
        var expectedDecisionKey = guard.ExpectedDecisionKey is null
            ? null
            : MultiActorDecisionCoordinator.RequiredDecisionKey(
                guard.ExpectedDecisionKey,
                nameof(guard.ExpectedDecisionKey));
        var extensionName = guard.RequiredInt32ExtensionName is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                guard.RequiredInt32ExtensionName,
                128,
                nameof(guard.RequiredInt32ExtensionName));
        var hasInt32Constraints =
            guard.ExpectedInt32ExtensionValue.HasValue
            || guard.MinimumInt32ExtensionValue != int.MinValue
            || guard.MaximumInt32ExtensionValue != int.MaxValue;
        if (extensionName is null && hasInt32Constraints)
        {
            throw new ArgumentException(
                "An Int32 extension name is required when Int32 constraints are set.",
                nameof(guard.RequiredInt32ExtensionName));
        }

        if (guard.MinimumInt32ExtensionValue
            > guard.MaximumInt32ExtensionValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(guard.MinimumInt32ExtensionValue),
                "The minimum extension value cannot exceed the maximum.");
        }

        if (guard.ExpectedInt32ExtensionValue is int expected
            && (expected < guard.MinimumInt32ExtensionValue
                || expected > guard.MaximumInt32ExtensionValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(guard.ExpectedInt32ExtensionValue),
                "The expected extension value must be inside the allowed range.");
        }

        var semanticExtensionName = guard.SemanticExtensionName is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                guard.SemanticExtensionName,
                128,
                nameof(guard.SemanticExtensionName));
        var semanticDigest = guard.ExpectedSemanticExtensionSha256;
        if ((semanticExtensionName is null) != (semanticDigest is null))
        {
            throw new ArgumentException(
                "A semantic extension name and expected SHA-256 digest must be supplied together.",
                semanticExtensionName is null
                    ? nameof(guard.SemanticExtensionName)
                    : nameof(guard.ExpectedSemanticExtensionSha256));
        }

        if (semanticDigest is not null
            && !CanonicalJsonDigest.IsSha256(semanticDigest))
        {
            throw new ArgumentException(
                "The expected semantic extension digest must contain exactly 64 lowercase hexadecimal characters.",
                nameof(guard.ExpectedSemanticExtensionSha256));
        }

        if (expectedBatchId is null
            && expectedAgentId is null
            && expectedDecisionKey is null
            && extensionName is null
            && semanticExtensionName is null)
        {
            throw new ArgumentException(
                "At least one durable resume expectation is required.",
                nameof(guard));
        }

        return new DurableRunResumeGuard
        {
            ExpectedBatchId = expectedBatchId,
            ExpectedAgentId = expectedAgentId,
            ExpectedDecisionKey = expectedDecisionKey,
            RequiredInt32ExtensionName = extensionName,
            MinimumInt32ExtensionValue =
                guard.MinimumInt32ExtensionValue,
            MaximumInt32ExtensionValue =
                guard.MaximumInt32ExtensionValue,
            ExpectedInt32ExtensionValue =
                guard.ExpectedInt32ExtensionValue,
            SemanticExtensionName = semanticExtensionName,
            ExpectedSemanticExtensionSha256 = semanticDigest
        };
    }

    private static void EnsureResumeGuard(
        AgentRun run,
        string requestedRunId,
        DurableRunResumeGuard guard)
    {
        if (!string.Equals(
                run.RunId,
                requestedRunId,
                StringComparison.Ordinal))
        {
            throw ResumeGuardFailure(
                DurableRunResumeGuardReasonCodes.RunIdMismatch);
        }

        if (guard.ExpectedBatchId is not null
            && !string.Equals(
                run.BatchId,
                guard.ExpectedBatchId,
                StringComparison.Ordinal))
        {
            throw ResumeGuardFailure(
                DurableRunResumeGuardReasonCodes.BatchIdMismatch);
        }

        if (guard.ExpectedAgentId is not null
            && !string.Equals(
                run.AgentId,
                guard.ExpectedAgentId,
                StringComparison.Ordinal))
        {
            throw ResumeGuardFailure(
                DurableRunResumeGuardReasonCodes.AgentIdMismatch);
        }

        if (guard.ExpectedDecisionKey is not null
            && !string.Equals(
                run.DecisionKey,
                guard.ExpectedDecisionKey,
                StringComparison.Ordinal))
        {
            throw ResumeGuardFailure(
                DurableRunResumeGuardReasonCodes.DecisionKeyMismatch);
        }

        if (guard.RequiredInt32ExtensionName is not null)
        {
            if (run.Extensions is null
                || !run.Extensions.TryGetValue(
                    guard.RequiredInt32ExtensionName,
                    out var extension))
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes.ExtensionMissing);
            }

            if (extension.ValueKind != JsonValueKind.Number
                || !extension.TryGetInt32(out var extensionValue))
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes.ExtensionNotInt32);
            }

            if (extensionValue < guard.MinimumInt32ExtensionValue
                || extensionValue > guard.MaximumInt32ExtensionValue)
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes.ExtensionOutOfRange);
            }

            if (guard.ExpectedInt32ExtensionValue is int expected
                && extensionValue != expected)
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes.ExtensionValueMismatch);
            }
        }

        if (guard.SemanticExtensionName is not null)
        {
            if (run.Extensions is null
                || !run.Extensions.TryGetValue(
                    guard.SemanticExtensionName,
                    out var semanticExtension))
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes
                        .SemanticExtensionMissing);
            }

            var actualDigest =
                CanonicalJsonDigest.ComputeSha256(semanticExtension);
            if (!string.Equals(
                    actualDigest,
                    guard.ExpectedSemanticExtensionSha256,
                    StringComparison.Ordinal))
            {
                throw ResumeGuardFailure(
                    DurableRunResumeGuardReasonCodes
                        .SemanticExtensionDigestMismatch);
            }
        }
    }

    private static DurableRunResumeGuardException ResumeGuardFailure(
        string reasonCode)
    {
        return new DurableRunResumeGuardException(reasonCode);
    }

    private void EnsureContextObservationsVisibleToRun(
        IReadOnlyList<ContextCandidate> context,
        AgentRun run)
    {
        foreach (var candidate in context)
        {
            if (candidate.ObservationAdmissionMetadata is not null)
            {
                ObservationAdmission.EnsureVisibleToRun(
                    candidate.ObservationAdmissionMetadata,
                    run,
                    _options
                        .RequireAudienceIncarnationForRestrictedObservations);
            }
        }
    }

    private static ContextCandidate Snapshot(ContextCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentException(
                "Context collections cannot contain null entries.",
                nameof(candidate));
        }

        return candidate.Clone();
    }

    private static SkillReference Snapshot(SkillReference skill)
    {
        if (skill is null)
        {
            throw new ArgumentException(
                "Active-skill collections cannot contain null entries.",
                nameof(skill));
        }

        return new SkillReference(skill.SkillId, skill.Version);
    }

    private static NormalizedMessage Snapshot(NormalizedMessage message)
    {
        if (message is null)
        {
            throw new ArgumentException(
                "Transcript collections cannot contain null entries.",
                nameof(message));
        }

        return NormalizedMessageJournalCodec.Decode(
            NormalizedMessageJournalCodec.Encode(message));
    }

    private void BindNewRunFinalOutputAdmission(
        AgentRun run,
        FinalOutputContract? contract)
    {
        if (_finalOutputAdmission is null)
        {
            if (contract is not null)
            {
                throw new ArgumentException(
                    "A final-output contract requires strict final-output "
                    + "admission.",
                    nameof(contract));
            }

            if (run.Extensions.ContainsKey(
                    FinalOutputAdmissionBinding.ExtensionName))
            {
                throw new ArgumentException(
                    "A new run cannot supply a runtime-owned final-output "
                    + "admission extension.",
                    nameof(run));
            }

            return;
        }

        if (run.Extensions.ContainsKey(
                FinalOutputAdmissionBinding.ExtensionName))
        {
            throw new ArgumentException(
                "A new run cannot supply a runtime-owned final-output "
                + "admission extension.",
                nameof(run));
        }

        var binding = new FinalOutputAdmissionBinding(
            _finalOutputAdmission.PolicyId,
            _finalOutputAdmission.PolicyVersion,
            _finalOutputAdmission.OptionsDigest,
            contract);
        run.Extensions[FinalOutputAdmissionBinding.ExtensionName] =
            binding.ToJson();
        var encoded = ProtocolJson.ToElement(run);
        JsonValueInspector.ValidateAndMeasure(
            encoded,
            DurableRunJsonLimits,
            nameof(run));
    }

    private FinalOutputAdmissionBinding?
        EnsureRecoveredFinalOutputAdmission(
            RecoveredRun recovered,
            FinalOutputContract? requestedContract)
    {
        var durable = FinalOutputAdmissionBinding.Read(recovered.Run);
        if (durable is null)
        {
            if (_finalOutputAdmission is not null)
            {
                throw new InvalidDataException(
                    "A non-admitted run cannot be resumed by a strict "
                    + "final-output runtime.");
            }

            if (requestedContract is not null)
            {
                throw new InvalidDataException(
                    "Resume cannot add a final-output contract to an "
                    + "existing run.");
            }

            return null;
        }

        if (_finalOutputAdmission is null)
        {
            throw new InvalidDataException(
                "A strict final-output run cannot be resumed by a runtime "
                + "without its admission policy.");
        }

        var current = new FinalOutputAdmissionBinding(
            _finalOutputAdmission.PolicyId,
            _finalOutputAdmission.PolicyVersion,
            _finalOutputAdmission.OptionsDigest,
            durable.Contract);
        if (!durable.Matches(current))
        {
            throw new InvalidDataException(
                "The final-output admission policy or limits do not match "
                + "the durable run.");
        }

        if (requestedContract is not null
            && (durable.Contract is null
                || !durable.Contract.Matches(requestedContract)))
        {
            throw new InvalidDataException(
                "The requested final-output contract does not match the "
                + "durable run.");
        }

        return durable;
    }

    private static void EnsureTerminalOutputWasAdmitted(
        RecoveredRun recovered,
        FinalOutputAdmissionBinding? admission)
    {
        if (admission is null || !recovered.FinalOutput.HasValue)
        {
            return;
        }

        if (!recovered.FinalOutputAdmissionEvidence.HasValue)
        {
            throw new InvalidDataException(
                "A strict terminal run has no durable final-output "
                + "admission evidence.");
        }
    }

    private static DurableRunOutcome Outcome(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        JsonElement? finalOutput = null,
        DurableTerminalOutcome? terminalOutcome = null)
    {
        var outcome = new DurableRunOutcome
        {
            Run = run,
            FinalOutput = finalOutput?.Clone(),
            Transcript = transcript.ToArray()
        };
        if (terminalOutcome is not null)
        {
            outcome.ErrorCode = terminalOutcome.Code;
            outcome.ErrorCategory = terminalOutcome.Category;
            outcome.SafeErrorMessage = terminalOutcome.SafeMessage;
        }

        return outcome;
    }

    private static string LaneId(AgentRun run, string? requested)
    {
        return string.IsNullOrWhiteSpace(requested)
            ? run.WorldId + "\0" + run.AgentId
            : RuntimeGuard.RequiredUtf8(requested, 256, nameof(requested));
    }

    private static string DirectToolDigest(ToolCatalogSnapshot snapshot)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "direct_tools");
        foreach (var tool in snapshot.DirectTools)
        {
            digest.Add("name", tool.Name);
            digest.Add("digest", tool.Digest);
        }

        return digest.Finish();
    }

    private static DateTimeOffset RunDeadlineAt(
        DateTimeOffset startedAt,
        long maxDurationMs)
    {
        var bounded = Math.Max(
            0,
            Math.Min(
                maxDurationMs,
                (long)TimeSpan.FromDays(3650).TotalMilliseconds));
        return startedAt.AddMilliseconds(bounded);
    }

    private static DateTimeOffset BackdateDuration(
        DateTimeOffset now,
        long durationMs)
    {
        if (durationMs <= 0)
        {
            return now;
        }

        var availableMilliseconds =
            (now.UtcDateTime.Ticks - DateTime.MinValue.Ticks)
            / TimeSpan.TicksPerMillisecond;
        if (durationMs >= availableMilliseconds)
        {
            return DateTimeOffset.MinValue;
        }

        return now.AddTicks(
            -durationMs * TimeSpan.TicksPerMillisecond);
    }

    private static string? CompletionState(string? intent)
    {
        return intent switch
        {
            CompletionIntents.Cancelled => RunStates.Cancelled,
            CompletionIntents.Interrupted => RunStates.Interrupted,
            CompletionIntents.Failed => RunStates.Failed,
            _ => null
        };
    }

    private void UpdateDuration(
        AgentRun run,
        DateTimeOffset startedAt)
    {
        run.Usage.DurationMs = Math.Max(
            run.Usage.DurationMs,
            (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
    }

    private ActiveRunLease EnterRun(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (_lifecycleState != 0)
            {
                throw new ObjectDisposedException(nameof(DurableAgentRuntime));
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdownCancellation.Token);
            _activeRuns = checked(_activeRuns + 1);
            return new ActiveRunLease(this, linked);
        }
    }

    private void ExitRun()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_lifecycleSync)
        {
            _activeRuns--;
            if (_activeRuns == 0 && _lifecycleState != 0)
            {
                drained = _activeRunsDrained;
            }
        }

        drained?.TrySetResult(true);
    }

    private RuntimeLoopRecoveryState CreateRuntimeLoopRecoveryState(
        RecoveredRun recovered,
        string? replaySafeCheckpointTurnId)
    {
        string? disclosedSkillAdmissionDigest = null;
        ProviderCacheKey? previousProviderCacheKey = null;
        var snapshot = recovered.LastTurnSnapshot;
        if (snapshot is not null)
        {
            var durableFinalOutputAdmission =
                FinalOutputAdmissionBinding.Read(recovered.Run);
            var hasSnapshotFinalOutputAdmission =
                snapshot.Extensions.TryGetValue(
                    FinalOutputAdmissionBinding.TurnSnapshotExtensionName,
                    out var snapshotFinalOutputAdmission);
            if (durableFinalOutputAdmission is null)
            {
                if (hasSnapshotFinalOutputAdmission)
                {
                    throw new InvalidDataException(
                        "A recovered turn has an unexpected final-output "
                        + "admission binding.");
                }
            }
            else if (!hasSnapshotFinalOutputAdmission
                     || !durableFinalOutputAdmission.Matches(
                         FinalOutputAdmissionBinding.FromJson(
                             snapshotFinalOutputAdmission)))
            {
                throw new InvalidDataException(
                    "A recovered turn does not match the durable "
                    + "final-output admission binding.");
            }

            if (snapshot.Extensions.TryGetValue(
                    "skillAdmission",
                    out var admission))
            {
                if (admission.ValueKind != JsonValueKind.Object
                    || !admission.TryGetProperty(
                        "admissionDigest",
                        out var admissionDigest)
                    || admissionDigest.ValueKind != JsonValueKind.String
                    || !CanonicalJsonDigest.IsSha256(
                        admissionDigest.GetString()))
                {
                    throw new InvalidDataException(
                        "A recovered skill admission digest is invalid.");
                }

                disclosedSkillAdmissionDigest =
                    admissionDigest.GetString();
            }

            if (snapshot.Extensions.TryGetValue(
                    ProviderCacheTelemetry.KeyExtensionName,
                    out var cacheKey))
            {
                previousProviderCacheKey =
                    ProviderCacheKey.FromJson(cacheKey);
            }

            if (replaySafeCheckpointTurnId is not null
                && string.Equals(
                    replaySafeCheckpointTurnId,
                    snapshot.TurnId,
                    StringComparison.Ordinal)
                && snapshot.Extensions.TryGetValue(
                    ConversationContextView.CheckpointExtensionName,
                    out var checkpoint))
            {
                try
                {
                    _conversationContext.RegisterCheckpoint(checkpoint);
                }
                catch (RuntimeContentLimitException)
                {
                    // Capacity pressure degrades to deterministic
                    // recompaction. It must not make a valid run
                    // unrecoverable.
                }
            }
        }

        return new RuntimeLoopRecoveryState(
            disclosedSkillAdmissionDigest,
            previousProviderCacheKey,
            recovered.ProviderOpaqueContinuationState,
            recovered.FinalOutputCommittedEvidence,
            recovered.FinalOutputAdmissionAttempts);
    }

    private static void TryAttachConversationContextCheckpoint(
        TurnSnapshot snapshot,
        ConversationContextView contextView)
    {
        try
        {
            snapshot.Extensions[
                    ConversationContextView.CheckpointExtensionName] =
                contextView.CreateCheckpoint(snapshot.RunId);
        }
        catch (RuntimeContentLimitException exception)
        {
            snapshot.Extensions["conversationContextCheckpointStatus"] =
                JsonArrayBuilder.Object(
                    ("available", JsonArrayBuilder.Boolean(false)),
                    ("reasonCode",
                        JsonArrayBuilder.String(exception.LimitCode)));
        }
    }

    private static string ProviderCacheCompactionDigest(
        ConversationContextReport report)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "provider-cache-compaction.v1");
        digest.Add("compacted", report.Compacted ? "true" : "false");
        digest.Add(
            "viewDigest",
            report.Compacted ? report.ViewDigest : "not-compacted");
        return digest.Finish();
    }

    private static string ProviderCacheDynamicRequestDigest(
        string promptDigest,
        ProviderOpaqueContinuationState? continuationState)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "provider-cache-dynamic-request.v1");
        digest.Add("promptDigest", promptDigest);
        if (continuationState is null)
        {
            digest.Add("providerContinuation", "absent");
        }
        else
        {
            digest.Add("providerContinuation", "present");
            digest.Add("providerId", continuationState.ProviderId);
            digest.Add(
                "providerRouteDigest",
                continuationState.ProviderRouteDigest);
            digest.Add("stateVersion", continuationState.StateVersion);
            digest.Add("payloadDigest", continuationState.PayloadDigest);
        }

        return digest.Finish();
    }

    private sealed class RuntimeExecutionPolicyLease
    {
        public RuntimeExecutionPolicyLease(
            ToolCatalogSnapshot tools,
            SkillCatalogSnapshot skills,
            ProviderRoutePlan providerRoutes,
            DurableExecutionPolicyIdentity identity)
        {
            Tools = tools ?? throw new ArgumentNullException(nameof(tools));
            Skills =
                skills ?? throw new ArgumentNullException(nameof(skills));
            ProviderRoutes = providerRoutes
                             ?? throw new ArgumentNullException(
                                 nameof(providerRoutes));
            Identity = identity
                       ?? throw new ArgumentNullException(nameof(identity));
        }

        public ToolCatalogSnapshot Tools { get; }

        public SkillCatalogSnapshot Skills { get; }

        public ProviderRoutePlan ProviderRoutes { get; }

        public DurableExecutionPolicyIdentity Identity { get; }
    }

    private sealed class RuntimeLoopRecoveryState
    {
        public RuntimeLoopRecoveryState(
            string? disclosedSkillAdmissionDigest,
            ProviderCacheKey? previousProviderCacheKey,
            ProviderOpaqueContinuationState? providerOpaqueContinuationState,
            IReadOnlyList<FinalOutputCommittedEvidence>
                finalOutputCommittedEvidence,
            int finalOutputAdmissionAttempts)
        {
            DisclosedSkillAdmissionDigest =
                disclosedSkillAdmissionDigest;
            PreviousProviderCacheKey = previousProviderCacheKey;
            ProviderOpaqueContinuationState =
                providerOpaqueContinuationState?.Snapshot();
            FinalOutputCommittedEvidence =
                finalOutputCommittedEvidence.ToArray();
            FinalOutputAdmissionAttempts =
                finalOutputAdmissionAttempts;
        }

        public string? DisclosedSkillAdmissionDigest { get; }

        public ProviderCacheKey? PreviousProviderCacheKey { get; }

        public ProviderOpaqueContinuationState?
            ProviderOpaqueContinuationState
        { get; }

        public IReadOnlyList<FinalOutputCommittedEvidence>
            FinalOutputCommittedEvidence
        { get; }

        public int FinalOutputAdmissionAttempts { get; }
    }

    private async Task CompleteStopAsync(
        Task drainTask,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            var activeRunsDrained = ReferenceEquals(
                await Task.WhenAny(
                        drainTask,
                        Task.Delay(_options.ShutdownDrainTimeout))
                    .ConfigureAwait(false),
                drainTask);
            Volatile.Write(
                ref _activeRunShutdownDrainResult,
                activeRunsDrained ? 1 : 2);
            if (activeRunsDrained)
            {
                await drainTask.ConfigureAwait(false);
                var boundedCleanup = Volatile.Read(
                    ref _shutdownBoundedCleanupTask)
                    ?? throw new InvalidOperationException(
                        "Runtime shutdown cleanup was not initialized.");
                await boundedCleanup.ConfigureAwait(false);
            }
            else
            {
                Interlocked.CompareExchange(
                    ref _detachedToolShutdownDrainResult,
                    2,
                    0);
                Interlocked.CompareExchange(
                    ref _detachedConversationShutdownDrainResult,
                    2,
                    0);
                if (_finalOutputAdmission is not null)
                {
                    Interlocked.CompareExchange(
                        ref _finalOutputAdmissionShutdownDrainResult,
                        2,
                        0);
                }
                Interlocked.CompareExchange(
                    ref _detachedProviderShutdownDrainResult,
                    2,
                    0);
            }

            Volatile.Write(ref _lifecycleState, 2);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task CompleteShutdownResourceCleanupAsync(
        Task activeRunsDrained,
        TaskCompletionSource<bool> boundedCleanupCompletion,
        Task shutdownCancellationCleanup)
    {
        try
        {
            // Stop accepting resolver work and request cancellation before
            // waiting for active runs. A resolver that ignores cancellation
            // remains isolated behind the bounded resolver-call cap.
            var skillContent = _skillContent
                .StopAsync()
                .AsTask();
            await activeRunsDrained.ConfigureAwait(false);
            var finalOutputAdmission = _finalOutputAdmission is null
                ? Task.FromResult(true)
                : _finalOutputAdmission.StopAsync().AsTask();

            // Provider admission is used only by active run leases. It must
            // remain alive until the final lease exits, but can close before
            // detached provider-owned stream cleanup settles.
            _providerAdmission.Dispose();

            var detachedTools = _toolScheduler
                .DrainDetachedExecutionsAsync(
                    _toolScheduler.DetachedShutdownDrainTimeout,
                    CancellationToken.None)
                .AsTask();
            var conversationContext = _conversationContext
                .StopAsync()
                .AsTask();
            var lifecycle = _lifecycle.StopAsync().AsTask();
            var metrics = _metrics.StopAsync().AsTask();
            var detachedProviders = DrainDetachedProviderCleanupsAsync(
                _options.ShutdownDrainTimeout);

            await Task.WhenAll(
                    detachedTools,
                    conversationContext,
                    lifecycle,
                    skillContent,
                    finalOutputAdmission,
                    metrics)
                .ConfigureAwait(false);

            var detachedToolsDrained = detachedTools.Result;
            var conversationContextDrained = conversationContext.Result;
            var skillContentResolversDrained = skillContent.Result;
            var finalOutputAdmissionPoliciesDrained =
                finalOutputAdmission.Result;
            Interlocked.CompareExchange(
                ref _detachedToolShutdownDrainResult,
                detachedToolsDrained ? 1 : 2,
                0);
            Interlocked.CompareExchange(
                ref _detachedConversationShutdownDrainResult,
                conversationContextDrained ? 1 : 2,
                0);
            Interlocked.CompareExchange(
                ref _skillContentResolverShutdownDrainResult,
                skillContentResolversDrained ? 1 : 2,
                0);
            if (_finalOutputAdmission is not null)
            {
                Interlocked.CompareExchange(
                    ref _finalOutputAdmissionShutdownDrainResult,
                    finalOutputAdmissionPoliciesDrained ? 1 : 2,
                    0);
            }
            boundedCleanupCompletion.TrySetResult(true);

            Task conversationCleanup = Task.CompletedTask;
            if (!conversationContextDrained)
            {
                conversationCleanup =
                    RetryConversationContextCleanupAsync();
                Volatile.Write(
                    ref _conversationContextCleanupTask,
                    conversationCleanup);
            }

            var detachedProvidersDrained =
                await detachedProviders.ConfigureAwait(false);
            Interlocked.CompareExchange(
                ref _detachedProviderShutdownDrainResult,
                detachedProvidersDrained ? 1 : 2,
                0);
            Task providerCleanup = Task.CompletedTask;
            if (!detachedProvidersDrained)
            {
                providerCleanup =
                    WaitForDetachedProviderCleanupsAsync();
            }

            await Task.WhenAll(conversationCleanup, providerCleanup)
                .ConfigureAwait(false);
            await shutdownCancellationCleanup.ConfigureAwait(false);
            if (_conversationContext.CleanupCompleted)
            {
                Volatile.Write(ref _shutdownResourceCleanupCompleted, 1);
            }
        }
        catch (Exception exception)
        {
            boundedCleanupCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task RetryConversationContextCleanupAsync()
    {
        var retryDelayMs = 10;
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                if (await _conversationContext.StopAsync()
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                _ = exception;
            }

            if (attempt + 1 < maximumAttempts)
            {
                await Task.Delay(retryDelayMs).ConfigureAwait(false);
                retryDelayMs = Math.Min(250, retryDelayMs * 2);
            }
        }
    }

    private void TrackDetachedProviderCleanup(Task cleanup)
    {
        if (cleanup is null)
        {
            return;
        }

        lock (_detachedProviderCleanupSync)
        {
            if (_detachedProviderCleanupCount == 0)
            {
                _detachedProviderCleanupsDrained = NewCompletion();
            }

            _detachedProviderCleanupCount =
                checked(_detachedProviderCleanupCount + 1);
        }

        _ = ObserveDetachedProviderCleanupAsync(cleanup);
    }

    private async Task ObserveDetachedProviderCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Increment(
                ref _detachedProviderCleanupFailureCount);
            ObserveTaskFailure(cleanup);
        }
        finally
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_detachedProviderCleanupSync)
            {
                _detachedProviderCleanupCount--;
                if (_detachedProviderCleanupCount == 0)
                {
                    drained = _detachedProviderCleanupsDrained;
                    _detachedProviderCleanupsDrained = null;
                }
            }

            Interlocked.Increment(
                ref _detachedProviderCleanupCompletedCount);
            drained?.TrySetResult(true);
        }
    }

    private async Task<bool> DrainDetachedProviderCleanupsAsync(
        TimeSpan timeout)
    {
        Task drain;
        lock (_detachedProviderCleanupSync)
        {
            drain = _detachedProviderCleanupCount == 0
                ? Task.CompletedTask
                : _detachedProviderCleanupsDrained!.Task;
        }

        if (drain.IsCompleted)
        {
            await drain.ConfigureAwait(false);
            return true;
        }

        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = Task.Delay(timeout, deadlineCancellation.Token);
        var completed = await Task.WhenAny(drain, deadline)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, drain))
        {
            deadlineCancellation.Cancel();
            try
            {
                await deadline.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The drain won; cancellation only releases the timer.
            }

            await drain.ConfigureAwait(false);
            return true;
        }

        await deadline.ConfigureAwait(false);
        return false;
    }

    private Task WaitForDetachedProviderCleanupsAsync()
    {
        lock (_detachedProviderCleanupSync)
        {
            return _detachedProviderCleanupCount == 0
                ? Task.CompletedTask
                : _detachedProviderCleanupsDrained!.Task;
        }
    }

    private static async Task DisposeShutdownCancellationAsync(
        CancellationTokenSource cancellation,
        Task cancellationTask,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private static TaskCompletionSource<bool> NewCompletion()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void ObserveTaskFailure(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
            | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task WaitForSharedTaskAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => cancelled.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(task, cancelled.Task)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private sealed class ActiveRunLease : IDisposable
    {
        private DurableAgentRuntime? _owner;
        private readonly CancellationTokenSource _linked;

        public ActiveRunLease(
            DurableAgentRuntime owner,
            CancellationTokenSource linked)
        {
            _owner = owner;
            _linked = linked;
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            try
            {
                _linked.Dispose();
            }
            finally
            {
                owner.ExitRun();
            }
        }
    }

    private sealed class PreparedToolCall
    {
        public PreparedToolCall(
            ModelToolCall toolCall,
            ToolExecutionRequest? execution,
            NormalizedMessage? immediateMessage,
            PreparedToolActivation? activation = null,
            PreparedSkillActivation? skillActivation = null)
        {
            ToolCall = toolCall;
            Execution = execution;
            ImmediateMessage = immediateMessage;
            Activation = activation;
            SkillActivation = skillActivation;
        }

        public ModelToolCall ToolCall { get; }

        public ToolExecutionRequest? Execution { get; }

        public NormalizedMessage? ImmediateMessage { get; }

        public PreparedToolActivation? Activation { get; }

        public PreparedSkillActivation? SkillActivation { get; }
    }

    private sealed class PreparedToolActivation
    {
        public PreparedToolActivation(
            string name,
            string version,
            string descriptorDigest)
        {
            Name = name;
            Version = version;
            DescriptorDigest = descriptorDigest;
        }

        public string Name { get; }

        public string Version { get; }

        public string DescriptorDigest { get; }
    }

    private sealed class ProviderBudgetExceededException : Exception
    {
        public ProviderBudgetExceededException(string reason)
            : base("The provider usage exhausted the run budget.")
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    private sealed class ControlBatch
    {
        public List<ContextCandidate> Context { get; } = new();

        public bool Cancel { get; set; }

        public bool Interrupt { get; set; }

        public bool Steer { get; set; }

        public bool FollowUp { get; set; }
    }

    private sealed class RunDeadline : IDisposable
    {
        private readonly CancellationToken _externalToken;
        private readonly CancellationTokenSource _timer = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationTokenSource _linked;
        private readonly Task _timerTask;
        private readonly object _cancellationSync = new();
        private BoundedCancellationDispatcher.CancellationDispatchReservation?
            _cancellationReservation;
        private Task? _cancellationTask;
        private int _expired;
        private int _disposed;

        public RunDeadline(
            long maxDurationMs,
            long elapsedDurationMs,
            CancellationToken externalToken,
            BoundedCancellationDispatcher cancellationDispatcher)
        {
            _externalToken = externalToken;
            if (!cancellationDispatcher.TryReserve(
                    out _cancellationReservation))
            {
                throw new InvalidOperationException(
                    "Run-deadline cancellation capacity is exhausted.");
            }

            try
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(
                    externalToken,
                    _timer.Token);
            }
            catch
            {
                _cancellationReservation!.Dispose();
                _lifetime.Dispose();
                _timer.Dispose();
                throw;
            }

            var elapsed = Math.Max(0, elapsedDurationMs);
            var remaining = maxDurationMs <= elapsed
                ? 0
                : maxDurationMs - elapsed;
            if (remaining == 0)
            {
                Interlocked.Exchange(ref _expired, 1);
                _ = StartCancellation(_timer);
                _timerTask = Task.CompletedTask;
            }
            else
            {
                _timerTask = RunTimerAsync(
                    remaining,
                    _lifetime.Token);
            }
        }

        public CancellationToken Token => _linked.Token;

        public bool IsExpired =>
            Volatile.Read(ref _expired) != 0
            && !_externalToken.IsCancellationRequested;

        public bool ExternalCancellationRequested =>
            _externalToken.IsCancellationRequested;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var cancellationTask = StartCancellation(_lifetime);
            _ = DisposeAfterTimerAsync(
                _timerTask,
                cancellationTask,
                _linked,
                _timer,
                _lifetime,
                _cancellationReservation!);
            _cancellationReservation = null;
        }

        private static async Task DisposeAfterTimerAsync(
            Task timerTask,
            Task cancellationTask,
            CancellationTokenSource linked,
            CancellationTokenSource timer,
            CancellationTokenSource lifetime,
            BoundedCancellationDispatcher.CancellationDispatchReservation
                cancellationReservation)
        {
            try
            {
                try
                {
                    await timerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Disposal owns the timer lifetime cancellation.
                }
                catch (AggregateException)
                {
                    // A cancellation callback cannot replace the run outcome.
                }

                try
                {
                    await cancellationTask.ConfigureAwait(false);
                }
                catch
                {
                    // The run already owns its terminal deadline decision.
                    // Observe cancellation-dispatch rejection before cleanup.
                }
            }
            finally
            {
                linked.Dispose();
                timer.Dispose();
                lifetime.Dispose();
                cancellationReservation.Dispose();
            }
        }

        private async Task RunTimerAsync(
            long remainingMilliseconds,
            CancellationToken lifetime)
        {
            try
            {
                var remaining = remainingMilliseconds;
                while (remaining > 0)
                {
                    var delay = (int)Math.Min(remaining, int.MaxValue);
                    await Task.Delay(delay, lifetime).ConfigureAwait(false);
                    remaining -= delay;
                }

                Interlocked.Exchange(ref _expired, 1);
                _ = StartCancellation(_timer);
            }
            catch (OperationCanceledException) when (
                lifetime.IsCancellationRequested)
            {
                // The owning run completed before its deadline.
            }
        }

        private Task StartCancellation(
            CancellationTokenSource source)
        {
            lock (_cancellationSync)
            {
                if (_cancellationTask is not null)
                {
                    return _cancellationTask;
                }

                try
                {
                    _cancellationTask =
                        _cancellationReservation!.DispatchAsync(source);
                }
                catch
                {
                    _cancellationTask = Task.CompletedTask;
                }

                return _cancellationTask;
            }
        }
    }
}

internal static class ToolExecutionRequestExtensions
{
    public static ToolInvocation Invocation(
        this ToolExecutionRequest request)
    {
        return new ToolInvocation
        {
            ToolCallId = request.ToolCallId,
            RunId = request.RunId,
            TurnId = request.TurnId,
            AttemptId = request.AttemptId,
            ToolName = request.Tool.Name,
            ToolVersion = request.Tool.Version,
            Arguments = request.Arguments.Clone(),
            Effect = request.Tool.Effect,
            ResolvedConflictKeys = request.ResolvedConflictKeys.ToList(),
            Sequence = request.Sequence,
            CreatedAt = request.CreatedAt
        };
    }
}
