using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class DurableAgentRuntime :
    IDurableAgentRuntime,
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
    private readonly ToolDisclosureEvaluator _toolDisclosure;
    private readonly ContextCompiler _contextCompiler;
    private readonly ToolBatchPlanner _toolPlanner;
    private readonly ToolBatchScheduler _toolScheduler;
    private readonly ToolInputSafetyGuard _toolSafety;
    private readonly IRuntimeClock _clock;
    private readonly IRuntimeIdGenerator _ids;
    private readonly RunOwnershipRegistry _ownership;
    private readonly DurableAgentRuntimeOptions _options;
    private readonly SemaphoreSlim _providerSlots;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly BoundedCancellationDispatcher
        _shutdownCancellationDispatcher;
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private TaskCompletionSource<bool>? _activeRunsDrained;
    private Task? _stopTask;
    private long _ephemeralSequence;
    private int _activeRuns;
    private int _lifecycleState;
    private int _detachedShutdownDrainResult;

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
        IToolDisclosurePolicy? toolDisclosurePolicy = null)
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
            BoundedCancellationDispatcher.LifecycleShared)
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
        BoundedCancellationDispatcher shutdownCancellationDispatcher)
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
        _options = (options ?? new DurableAgentRuntimeOptions()).Snapshot();
        Controls = controls ?? new RuntimeControlPlane();
        _ownership = ownership ?? new RunOwnershipRegistry();
        _cancellationDispatcher = cancellationDispatcher
                                  ?? throw new ArgumentNullException(
                                      nameof(cancellationDispatcher));
        _shutdownCancellationDispatcher = shutdownCancellationDispatcher
                                          ?? throw new ArgumentNullException(
                                              nameof(
                                                  shutdownCancellationDispatcher));
        _providerSlots = new SemaphoreSlim(
            _options.MaxConcurrentProviderCalls,
            _options.MaxConcurrentProviderCalls);
    }

    public RuntimeControlPlane Controls { get; }

    public int DetachedToolExecutionCount =>
        _toolScheduler.DetachedExecutionCount;

    public bool? DetachedToolExecutionsDrainedOnStop
    {
        get
        {
            return Volatile.Read(ref _detachedShutdownDrainResult) switch
            {
                1 => true,
                2 => false,
                _ => null
            };
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
        RunAdmission.EnsureNewRun(requestSnapshot.Run, nameof(request));

        var laneId = LaneId(
            requestSnapshot.Run,
            requestSnapshot.LaneId);
        await using var ownership = await _ownership.AcquireAsync(
                requestSnapshot.Run.RunId,
                laneId,
                cancellationToken)
            .ConfigureAwait(false);
        using var control = Controls.Register(
            requestSnapshot.Run.RunId,
            requestSnapshot.Run.WorldId);
        var transcript = DistinctTranscript(
                requestSnapshot.InitialTranscript)
            .ToList();
        var startedAt = _clock.UtcNow;
        using var deadline = new RunDeadline(
            requestSnapshot.Run.Budget.MaxDurationMs,
            requestSnapshot.Run.Usage.DurationMs,
            cancellationToken,
            _cancellationDispatcher);

        try
        {
            await _journal.CommitRunStartAsync(
                    requestSnapshot.Run,
                    transcript,
                    requestSnapshot.Context,
                    requestSnapshot.ActiveSkills,
                    cancellationToken)
                .ConfigureAwait(false);

            return await ExecuteLoopAsync(
                    requestSnapshot.Run,
                    transcript,
                    requestSnapshot.Context,
                    requestSnapshot.ActiveSkills,
                    Array.Empty<ToolActivationRecord>(),
                    control,
                    startedAt,
                    deadline)
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
            exception is not DuplicateRunException)
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
    public async ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        using var activeRun = EnterRun(cancellationToken);
        cancellationToken = activeRun.Token;
        var continuationSnapshot = Snapshot(
            continuation ?? new DurableRunContinuation(),
            cancellationToken);
        var recovered = await _recovery.LoadAsync(runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Run '{runId}' does not exist in the durable journal.");
        var laneId = LaneId(recovered.Run, continuationSnapshot.LaneId);
        await using var ownership = await _ownership.AcquireAsync(
                runId,
                laneId,
                cancellationToken)
            .ConfigureAwait(false);
        using var control = Controls.Register(runId, recovered.Run.WorldId);
        var transcript = recovered.Transcript.ToList();
        IReadOnlyList<ContextCandidate> context =
            continuationSnapshot.Context;
        IReadOnlyList<SkillReference> activeSkills =
            continuationSnapshot.ActiveSkills;
        if (context.Count == 0)
        {
            context = recovered.RecoveryContext;
        }

        if (activeSkills.Count == 0
            && !continuationSnapshot.ReplaceActiveSkills)
        {
            activeSkills = recovered.RecoveryActiveSkills;
        }

        var startedAt = _clock.UtcNow.AddMilliseconds(
            -Math.Max(0, recovered.Run.Usage.DurationMs));
        using var deadline = new RunDeadline(
            recovered.Run.Budget.MaxDurationMs,
            recovered.Run.Usage.DurationMs,
            cancellationToken,
            _cancellationDispatcher);

        try
        {
            if (RunStateMachine.IsTerminal(recovered.Run.State))
            {
                await SettleRecoveredProviderDispatchesAsync(
                        recovered.Run,
                        recovered.UnsettledProviderDispatches,
                        startedAt,
                        terminalRun: true)
                    .ConfigureAwait(false);
                return Outcome(
                    recovered.Run,
                    transcript,
                    recovered.FinalOutput);
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

            return await ExecuteLoopAsync(
                    recovered.Run,
                    transcript,
                    context,
                    activeSkills,
                    recovered.RecoveryToolActivations,
                    control,
                    startedAt,
                    deadline)
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
            exception is not DuplicateRunException)
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
        return StopAsync();
    }

    public ValueTask StopAsync()
    {
        Task stopTask;
        Task? drainTask = null;
        TaskCompletionSource<bool>? completion = null;
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

            _ = DisposeShutdownCancellationAsync(
                _shutdownCancellation,
                cancellationTask,
                shutdownCancellationReservation!);
            _ = CompleteStopAsync(
                drainTask!,
                completion!);
        }

        return new ValueTask(stopTask);
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

    private async ValueTask<DurableRunOutcome> ExecuteLoopAsync(
        AgentRun run,
        List<NormalizedMessage> transcript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        IReadOnlyList<ToolActivationRecord> initialToolActivations,
        RuntimeControlPlane.Registration control,
        DateTimeOffset startedAt,
        RunDeadline deadline)
    {
        var cancellationToken = deadline.Token;
        var pendingContext = new List<ContextCandidate>(initialContext);
        var contextDeferralTurns = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var runAttemptId = _ids.NewId("run-attempt");
        var fence = new AttemptFence();
        string? disclosedSkillDigest = null;
        IReadOnlyList<ToolActivationRecord> toolActivations =
            initialToolActivations.Select(item => item.Clone()).ToArray();
        var toolLoopGuard = SemanticToolLoopGuard.Rebuild(
            _options.ToolLoopGuard,
            transcript);

        while (true)
        {
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
            var toolSnapshot = _tools.Current;
            var skillSnapshot = _skills.Current;
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
                activeSkills,
                _options.SkillDisclosureBudget);
            toolDisclosure.FinalizeSkillActivations();
            var disclosure = skillSnapshot.CreateDisclosure(
                activeSkills,
                _options.SkillDisclosureBudget,
                skillAdmission.CatalogReferences);
            var compiled = _contextCompiler.Compile(
                new ContextCompilationRequest(
                    run.RunId,
                    turnId,
                    pendingContext,
                    _clock.UtcNow,
                    disclosure,
                    _contextCompiler.MaxCandidates,
                    deadline.Token));
            RetainDeferredContext(
                pendingContext,
                contextDeferralTurns,
                compiled.BudgetReport);

            var preparedTranscript = transcript.ToList();
            preparedTranscript.RemoveAll(
                SemanticToolLoopGuard.IsWarningMessage);
            var promptMessages = new List<NormalizedMessage>(3);
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

            var directTools = toolDisclosure.EffectiveProviderTools;
            var providerPrompt = preparedTranscript
                .Where(IsSkillDisclosureMessage)
                .Concat(
                    preparedTranscript.Where(
                        message => !IsSkillDisclosureMessage(message)))
                .ToArray();
            var stablePrefix = providerPrompt
                .TakeWhile(IsSkillDisclosureMessage)
                .ToArray();
            var prompt = RuntimePromptBuilder.MeasurePrompt(
                providerPrompt,
                directTools,
                _options.MaxTranscriptMessages,
                _options.MaxPromptUtf8Bytes,
                _options.EstimatedPromptBytesPerToken);
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
            var snapshot = new TurnSnapshot
            {
                TurnId = turnId,
                RunId = run.RunId,
                RuntimeGeneration = run.RuntimeGeneration,
                ProviderId = _provider.PrimaryProviderId,
                ModelId = string.Equals(
                    _provider.PrimaryRouteMetadata.ModelId,
                    "unspecified",
                    StringComparison.Ordinal)
                    ? _options.ModelId
                    : _provider.PrimaryRouteMetadata.ModelId,
                PromptLayoutVersion = _options.PromptLayoutVersion,
                StablePrefixHash = RuntimePromptBuilder.StablePrefixDigest(
                    stablePrefix,
                    _options.PromptLayoutVersion),
                SkillGeneration = skillSnapshot.Generation,
                SkillDigests = disclosure.Activated
                    .Select(item => item.ContentDigest)
                    .ToList(),
                ToolCatalogGeneration = toolSnapshot.Generation,
                DirectToolDigest = toolDisclosure.EffectiveDirectDigest,
                DeferredCatalogDigest =
                    toolDisclosure.AuthorizedHiddenTools.Count == 0
                    ? null
                    : toolDisclosure.DeferredOnlyDigest,
                ContextPolicyVersion = _options.ContextPolicyVersion,
                BudgetPolicyVersion = _options.BudgetPolicyVersion,
                CreatedAt = _clock.UtcNow,
                Extensions = new Dictionary<string, JsonElement>(
                    StringComparer.Ordinal)
                {
                    ["skillAdmission"] =
                        skillAdmission.ToSnapshotExtension(),
                    ["toolDisclosure"] =
                        toolDisclosure.ToSnapshotExtension(),
                    ["contextBudget"] =
                        ProtocolJson.ToElement(compiled.BudgetReport),
                    ["promptMeasurement"] =
                        RuntimePromptBuilder.PromptMeasurementEvidence(prompt),
                    ["promptDigest"] =
                        JsonArrayBuilder.String(
                            RuntimePromptBuilder.TranscriptDigest(
                                providerPrompt,
                                toolDisclosure.EffectiveDirectDigest,
                                skillSnapshot)),
                    ["stablePrefixMessageCount"] =
                        JsonArrayBuilder.Number(stablePrefix.Length)
                }
            };
            await _journal.CommitTurnPreparationAsync(
                    run,
                    turnId,
                    attemptId,
                    promptMessages,
                    snapshot,
                    startedAt,
                    cancellationToken,
                    toolDisclosure.StateChanged
                        ? toolDisclosure.ToJournalRecord(
                            toolDisclosure.StateReasonCodes)
                        : null)
                .ConfigureAwait(false);
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
                var acquired = false;
                Task? detachedProviderCleanup = null;
                try
                {
                    await _providerSlots.WaitAsync(step.CancellationToken)
                        .ConfigureAwait(false);
                    acquired = true;
                    response = await _provider.RunAsync(
                            run.RunId,
                            runAttemptId,
                            turnId,
                            providerPrompt,
                            directTools,
                            fence,
                            item => PublishProviderEventAsync(run, item),
                            step.CancellationToken,
                            onLifecycleNotice: notice => PublishProviderLifecycle(
                                run,
                                notice),
                            estimatedPromptTokens: prompt.EstimatedTokens,
                            maxOutputTokens: maxOutputTokens,
                            onDetachedCleanup: cleanup =>
                                detachedProviderCleanup = cleanup,
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
                                    turnId))
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (acquired)
                    {
                        if (detachedProviderCleanup is null)
                        {
                            _providerSlots.Release();
                        }
                        else
                        {
                            _ = ReleaseProviderSlotAfterCleanupAsync(
                                detachedProviderCleanup,
                                _providerSlots);
                        }
                    }
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
            var assistant = NormalizedTranscript.AssistantResponse(
                _ids.NewId("assistant-message"),
                response.Text,
                response.ReasoningContent,
                response.ToolCalls,
                _clock.UtcNow);
            AnnotateToolCallEvidence(assistant, toolDisclosure);

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
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    transcript.Add(assistant);
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

                var finalOutput = RuntimePromptBuilder.FinalOutput(response.Text!);
                await _journal.CommitFinalCompletionAsync(
                        run,
                        assistant,
                        finalOutput,
                        turnId,
                        response.ProviderId,
                        response.ProviderAttemptId,
                        response.StreamAttemptId,
                        startedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                transcript.Add(assistant);
                return Outcome(run, transcript, finalOutput);
            }

            await _journal.CommitProviderResultAsync(
                    run,
                    assistant,
                    turnId,
                    response.ProviderId,
                    response.ProviderAttemptId,
                    response.StreamAttemptId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            transcript.Add(assistant);
            var prepared = PrepareToolCalls(
                run,
                turnId,
                attemptId,
                response.ToolCalls,
                toolDisclosure);
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
                                             || ToolDisclosureControlNames
                                                 .IsReserved(
                                                     item.ToolCall.Name))
                            ? "tool_controls"
                            : "tool_errors",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            foreach (var call in valid)
            {
                await _journal.AppendDurableAsync(
                        run,
                        RuntimeEventKinds.ToolStarted,
                        ProtocolJson.ToElement(call.Invocation()),
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

            var actionRequests = new Dictionary<string, ActionRequest>(
                StringComparer.Ordinal);
            foreach (var call in valid)
            {
                var request = ActionRequestFor(
                    run,
                    call,
                    RunDeadlineAt(startedAt, run.Budget.MaxDurationMs));
                actionRequests.Add(call.ToolCallId, request);
            }

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

            var executor = new HostToolExecutor(_host, actionRequests);
            var plan = _toolPlanner.Plan(valid);
            IReadOnlyList<ToolExecutionResult>? executionResults = null;
            var executionCancelled = false;
            try
            {
                using var step = control.BeginStep(cancellationToken);
                executionResults = await _toolScheduler.ExecuteAsync(
                        plan,
                        executor,
                        _clock,
                        step.CancellationToken)
                    .ConfigureAwait(false);
                step.CancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                executionCancelled = true;
            }

            foreach (var call in valid)
            {
                ActionReceipt receipt;
                var executionResult = executionResults?
                    .FirstOrDefault(
                        item => string.Equals(
                            item.Request.ToolCallId,
                            call.ToolCallId,
                            StringComparison.Ordinal));
                if (executionResult?.IsSuccess == true
                    && executor.TryGetReceipt(
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
                    receipt = executionResult is not null
                              && !executionResult.MayHaveExecuted
                        ? FailedReceipt(
                            actionRequests[call.ToolCallId],
                            errorCode)
                        : UnknownReceipt(
                            actionRequests[call.ToolCallId],
                            errorCode);
                }

                await _journal.AppendActionReceiptAsync(
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
                    toolMessages[call.ToolCallId] =
                        NormalizedTranscript.ToolResult(
                            RunRecovery.ToolResultMessageId(receipt),
                            call.ToolCallId,
                            call.Tool.Name,
                            receipt,
                            receipt.ReceivedAt);
                }
            }

            toolActivations = await AppendPreparedToolMessagesAsync(
                    run,
                    transcript,
                    prepared,
                    toolMessages,
                    toolDisclosure,
                    turnId,
                    attemptId,
                    CancellationToken.None)
                .ConfigureAwait(false);
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
            if (run.PendingOperationIds.Count > 0)
            {
                var intent = deadline.IsExpired
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
                        terminalReason: deadline.IsExpired
                            ? "max_duration"
                            : null,
                        completionIntent: intent,
                        turnId: turnId,
                        attemptId: attemptId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return Outcome(run, transcript);
            }

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

    private static void RetainDeferredContext(
        List<ContextCandidate> pendingContext,
        Dictionary<string, int> deferralTurns,
        ContextBudgetReport report)
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
            var candidate = candidatesById[id];
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
        ToolDisclosurePlan disclosure)
    {
        var prepared = new List<PreparedToolCall>(calls.Count);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var dispatchIndex = 0;
        var controlCalls = 0;
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
        ToolDisclosurePlan disclosure)
    {
        var providerTools = disclosure.EffectiveProviderTools;
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
            ExpectedEffects = call.ResolvedConflictKeys.ToList(),
            RequestedAt = requestedAt,
            Deadline = effectiveDeadline
        };
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
            if (command.Observation is not null
                && !string.Equals(
                    command.Observation.WorldId,
                    run.WorldId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A control observation belongs to a different world.");
            }

            await _journal.AppendDurableAsync(
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

    private async ValueTask CompleteControlAsync(
        AgentRun run,
        string kind,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
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
                    cancellationToken: cancellationToken)
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
                    cancellationToken: cancellationToken)
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
                    next.Usage.InputTokens = (int)Math.Min(
                        int.MaxValue,
                        chargedInputTokens);
                    next.Usage.OutputTokens = (int)Math.Min(
                        int.MaxValue,
                        chargedOutputTokens);
                    next.Usage.CostUsd = RuntimePromptBuilder.AddCost(
                        next.Usage.CostUsd,
                        notice.Usage.CostUsd);
                    UpdateDuration(next, startedAt);
                },
                turnId,
                notice.ProviderAttemptId,
                CancellationToken.None,
                eventId: "provider-usage:" + notice.StreamAttemptId,
                streamAttemptId: notice.StreamAttemptId,
                providerId: notice.ProviderId)
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
                    notice.RouteIdentity.RouteDigest)
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
                    StringComparison.Ordinal)
                || string.Equals(
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
                        next =>
                        {
                            next.TerminalReason = "max_duration";
                            next.CompletionIntent = null;
                            UpdateDuration(next, startedAt);
                        },
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _journal.CommitRunMutationAsync(
                        run,
                        RuntimeEventKinds.RunCheckpoint,
                        next =>
                        {
                            next.TerminalReason = "max_duration";
                            next.CompletionIntent = null;
                            UpdateDuration(next, startedAt);
                        },
                        turnId: run.CurrentTurnId,
                        cancellationToken: CancellationToken.None)
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
            _ when cancellationRequested => "run_cancelled",
            _ => "runtime_failure"
        };
        var category = exception switch
        {
            ProviderException provider => provider.Category,
            SkillAdmissionException => "skill_admission",
            _ when cancellationRequested => "control",
            _ => "runtime"
        };
        var safeMessage = exception switch
        {
            ProviderException provider => provider.Message,
            SkillAdmissionException =>
                "An activated skill was not admitted for this turn.",
            _ when cancellationRequested => "The run was cancelled.",
            _ => "The runtime failed. Inspect the structured trace."
        };

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
                            CancellationToken.None)
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
                            cancellationToken: CancellationToken.None)
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
            payload = JsonArrayBuilder.Object(
                ("kind", JsonArrayBuilder.String(item.Kind)),
                ("text", JsonArrayBuilder.String(item.TextDelta ?? string.Empty)));
        }
        else
        {
            payload = JsonArrayBuilder.Object(
                ("kind", JsonArrayBuilder.String(item.Kind)));
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

    private void PublishProviderLifecycle(
        AgentRun run,
        ProviderAttemptNotice notice)
    {
        var payload = JsonArrayBuilder.Object(
            ("providerId", JsonArrayBuilder.String(notice.ProviderId)),
            ("nextProviderId", notice.NextProviderId is null
                ? ProtocolJson.ParseElement("null")
                : JsonArrayBuilder.String(notice.NextProviderId)),
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
            attemptId: null,
            streamAttemptId: null,
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
            activeSkillSnapshot);
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
            _options.EstimatedPromptBytesPerToken);
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
            LaneId = request.LaneId
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
            ActiveSkills = RuntimeInputGuard.CopyBounded(
                activeSkills,
                DurableRunInputJournalCodec.MaxActiveSkills,
                Snapshot,
                nameof(continuation.ActiveSkills),
                "activated_skill_count_exceeded",
                cancellationToken),
            ReplaceActiveSkills = continuation.ReplaceActiveSkills,
            LaneId = continuation.LaneId
        };
    }

    private static ContextCandidate Snapshot(ContextCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentException(
                "Context collections cannot contain null entries.",
                nameof(candidate));
        }

        if (candidate.Content.HasValue)
        {
            return new ContextCandidate(
                candidate.Id,
                candidate.Category,
                candidate.Content.Value,
                candidate.Priority,
                candidate.Required,
                candidate.CanDefer,
                candidate.EstimatedTokens,
                candidate.ExpiresAt,
                candidate.Provenance);
        }

        var resource = candidate.Resource
            ?? throw new ArgumentException(
                "A context candidate requires content or a resource.",
                nameof(candidate));
        return new ContextCandidate(
            candidate.Id,
            candidate.Category,
            new ContextResourceReference(
                resource.Uri,
                resource.MediaType,
                resource.Digest,
                resource.SizeBytes),
            candidate.Priority,
            candidate.Required,
            candidate.CanDefer,
            candidate.EstimatedTokens,
            candidate.ExpiresAt,
            candidate.Provenance);
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

    private static DurableRunOutcome Outcome(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> transcript,
        JsonElement? finalOutput = null)
    {
        return new DurableRunOutcome
        {
            Run = run,
            FinalOutput = finalOutput?.Clone(),
            Transcript = transcript.ToArray()
        };
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

    private async Task CompleteStopAsync(
        Task drainTask,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await drainTask.ConfigureAwait(false);
            var detachedDrained =
                await _toolScheduler.DrainDetachedExecutionsAsync(
                        _toolScheduler.DetachedShutdownDrainTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            Volatile.Write(
                ref _detachedShutdownDrainResult,
                detachedDrained ? 1 : 2);
            _providerSlots.Dispose();
            Volatile.Write(ref _lifecycleState, 2);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
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

    private static async Task ReleaseProviderSlotAfterCleanupAsync(
        Task cleanup,
        SemaphoreSlim providerSlots)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                providerSlots.Release();
            }
            catch (ObjectDisposedException)
            {
                // Runtime shutdown closes admission before forcing transport
                // disposal. A late quarantined attempt cannot reopen it.
            }
        }
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
            PreparedToolActivation? activation = null)
        {
            ToolCall = toolCall;
            Execution = execution;
            ImmediateMessage = immediateMessage;
            Activation = activation;
        }

        public ModelToolCall ToolCall { get; }

        public ToolExecutionRequest? Execution { get; }

        public NormalizedMessage? ImmediateMessage { get; }

        public PreparedToolActivation? Activation { get; }
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

                await cancellationTask.ConfigureAwait(false);
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
