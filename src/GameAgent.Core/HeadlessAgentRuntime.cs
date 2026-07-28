using System.Globalization;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class HeadlessAgentRuntimeLimits
{
    public const int DefaultMaxActiveRuns = 256;
    public const int DefaultMaxInFlightActions = 256;

    public HeadlessAgentRuntimeLimits(
        int maxActiveRuns = DefaultMaxActiveRuns,
        int maxObservations = 512,
        int maxTools = 512,
        int maxInputUtf8Bytes = 1_048_576,
        JsonValueLimits? inputJsonLimits = null,
        int maxInFlightActions = DefaultMaxInFlightActions)
    {
        if (maxActiveRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActiveRuns));
        }
        if (maxObservations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxObservations));
        }
        if (maxTools < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTools));
        }
        if (maxInputUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputUtf8Bytes));
        }
        if (maxInFlightActions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInFlightActions));
        }

        MaxActiveRuns = maxActiveRuns;
        MaxObservations = maxObservations;
        MaxTools = maxTools;
        MaxInputUtf8Bytes = maxInputUtf8Bytes;
        InputJsonLimits = inputJsonLimits ?? new JsonValueLimits();
        MaxInFlightActions = maxInFlightActions;
    }

    public int MaxActiveRuns { get; }

    public int MaxObservations { get; }

    public int MaxTools { get; }

    public int MaxInputUtf8Bytes { get; }

    public JsonValueLimits InputJsonLimits { get; }

    public int MaxInFlightActions { get; }
}

public sealed class HeadlessAgentRuntime
{
    private const int MaxProviderToolCalls = 128;
    private const int MaxProviderToolArgumentsUtf8Bytes = 1_048_576;

    private readonly IModelProvider _modelProvider;
    private readonly IGameHost _gameHost;
    private readonly ISessionStore _sessionStore;
    private readonly IRuntimeClock _clock;
    private readonly IRuntimeIdGenerator _ids;
    private readonly ToolInputSafetyGuard _toolSafety = new();
    private readonly object _activeRunSync = new();
    private readonly HashSet<string> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly HeadlessAgentRuntimeLimits _limits;
    private readonly SemaphoreSlim _actionSlots;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;

    public HeadlessAgentRuntime(
        IModelProvider modelProvider,
        IGameHost gameHost,
        ISessionStore sessionStore,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids)
        : this(
            modelProvider,
            gameHost,
            sessionStore,
            clock,
            ids,
            new HeadlessAgentRuntimeLimits(),
            BoundedCancellationDispatcher.Shared)
    {
    }

    public HeadlessAgentRuntime(
        IModelProvider modelProvider,
        IGameHost gameHost,
        ISessionStore sessionStore,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        HeadlessAgentRuntimeLimits limits)
        : this(
            modelProvider,
            gameHost,
            sessionStore,
            clock,
            ids,
            limits,
            BoundedCancellationDispatcher.Shared)
    {
    }

    internal HeadlessAgentRuntime(
        IModelProvider modelProvider,
        IGameHost gameHost,
        ISessionStore sessionStore,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        HeadlessAgentRuntimeLimits limits,
        BoundedCancellationDispatcher cancellationDispatcher)
    {
        _modelProvider = modelProvider;
        _gameHost = gameHost;
        _sessionStore = sessionStore;
        _clock = clock;
        _ids = ids;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _cancellationDispatcher = cancellationDispatcher
                                  ?? throw new ArgumentNullException(
                                      nameof(cancellationDispatcher));
        _actionSlots = new SemaphoreSlim(
            _limits.MaxInFlightActions,
            _limits.MaxInFlightActions);
    }

    public int ActiveRunCount
    {
        get
        {
            lock (_activeRunSync)
            {
                return _activeRuns.Count;
            }
        }
    }

    public int InFlightActionCount =>
        _limits.MaxInFlightActions - _actionSlots.CurrentCount;

    public async ValueTask<HeadlessRunOutcome> RunAsync(
        HeadlessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var sourceRun = request.Run
            ?? throw new ArgumentException(
                "A headless run request requires a run.",
                nameof(request));
        var sourceRunSnapshot =
            RuntimeProtocolInputGuard.ValidateAgentRunBeforeSerialization(
                sourceRun,
                _limits.InputJsonLimits,
                MaximumInputItemBytes,
                nameof(request.Run));
        RunAdmission.EnsureNewRun(sourceRunSnapshot, nameof(request));
        using var admission = Admit(sourceRunSnapshot.RunId);
        cancellationToken.ThrowIfCancellationRequested();
        var requestSnapshot = Snapshot(
            request,
            sourceRunSnapshot,
            cancellationToken);
        RunAdmission.EnsureNewRun(requestSnapshot.Run, nameof(request));

        foreach (var observation in requestSnapshot.Observations)
        {
            ProtocolValidator.EnsureValid(observation);
        }

        foreach (var tool in requestSnapshot.Tools)
        {
            ProtocolValidator.EnsureValid(tool);
        }

        var run = requestSnapshot.Run;
        var eventSequence = new RunEventSequence();
        var invocationStartedAt = _clock.UtcNow;
        var elapsedAtStart = Math.Max(0, run.Usage.DurationMs);
        var budget = new BudgetTracker(
            run.Budget,
            BudgetStartedAt(invocationStartedAt, elapsedAtStart));
        using var deadline = new HeadlessRunDeadline(
            run.Budget.MaxDurationMs,
            elapsedAtStart,
            cancellationToken,
            _cancellationDispatcher,
            admission);
        var runMonotonicDeadline = MonotonicDeadline.Start(
            TimeSpan.FromMilliseconds(
                Math.Max(
                    0,
                    run.Budget.MaxDurationMs - elapsedAtStart)));
        var toolsByName = requestSnapshot.Tools.ToDictionary(
            tool => tool.Name,
            StringComparer.Ordinal);
        var messages = new List<ModelMessage>
        {
            new()
            {
                Role = "user",
                Content = ProtocolJson.ToElement(new ObservationBatchPayload
                {
                    Observations = requestSnapshot.Observations.ToList()
                })
            }
        };
        if (deadline.IsExpired)
        {
            return await BudgetExhaustedOutcomeAsync(
                    run,
                    eventSequence,
                    "max_duration",
                    invocationStartedAt,
                    elapsedAtStart)
                .ConfigureAwait(false);
        }

        var existingEvents = await AwaitBoundedAsync(
                _sessionStore.ReadRunAsync(run.RunId, deadline.Token),
                deadline)
            .ConfigureAwait(false);
        if (existingEvents is null)
        {
            throw new InvalidDataException(
                "The session store returned no run history.");
        }

        if (existingEvents.Count != 0)
        {
            throw new DuplicateRunException(run.RunId);
        }

        try
        {
            SetRunState(run, RunStates.Running);
            await AppendAsync(
                run,
                eventSequence,
                null,
                RuntimeEventKinds.RunStarted,
                ProtocolJson.ToElement(new RunStartedEventPayload
                {
                    RunId = run.RunId
                }),
                deadline).ConfigureAwait(false);

            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var turnDecision = budget.CanStartTurn(
                    run.Usage,
                    _clock.UtcNow);
                if (!turnDecision.Allowed)
                {
                    return await BudgetExhaustedOutcomeAsync(
                            run,
                            eventSequence,
                            turnDecision.Reason!,
                            invocationStartedAt,
                            elapsedAtStart)
                        .ConfigureAwait(false);
                }

                var turnNumber = run.Usage.Turns;
                var turnId = _ids.NewId("turn");
                var attemptId = _ids.NewId("attempt");
                var streamAttemptId = _ids.NewId("stream");
                run.Usage.Turns = checked(run.Usage.Turns + 1);
                run.CurrentTurnId = turnId;
                SetRunState(run, RunStates.Running);

                await AppendAsync(
                    run,
                    eventSequence,
                    turnId,
                    RuntimeEventKinds.TurnStarted,
                    ProtocolJson.ToElement(new TurnStartedEventPayload
                    {
                        TurnNumber = turnNumber,
                        AttemptId = attemptId,
                        StreamAttemptId = streamAttemptId
                    }),
                    deadline).ConfigureAwait(false);

                var modelRequest = new ModelRequest
                {
                    RunId = run.RunId,
                    TurnId = turnId,
                    AttemptId = attemptId,
                    StreamAttemptId = streamAttemptId,
                    Messages = SnapshotMessages(messages),
                    Tools = SnapshotTools(requestSnapshot.Tools)
                };

                var providerResponse = await AwaitBoundedAsync(
                        _modelProvider.CompleteAsync(
                            modelRequest,
                            deadline.Token),
                        deadline)
                    .ConfigureAwait(false);
                if (providerResponse is null)
                {
                    throw new InvalidDataException(
                        "The model provider returned no response.");
                }

                var responseUsage = providerResponse.GetValidatedUsage();
                var usageOverflowReason = AccumulateUsage(
                    run.Usage,
                    responseUsage);
                UpdateDuration(
                    run,
                    invocationStartedAt,
                    elapsedAtStart);
                var response = Snapshot(providerResponse, responseUsage);
                if (usageOverflowReason is not null)
                {
                    return await BudgetExhaustedOutcomeAsync(
                            run,
                            eventSequence,
                            usageOverflowReason,
                            invocationStartedAt,
                            elapsedAtStart)
                        .ConfigureAwait(false);
                }

                var responseBudget = budget.CheckAfterCharge(
                    run.Usage,
                    _clock.UtcNow);
                if (!responseBudget.Allowed)
                {
                    return await BudgetExhaustedOutcomeAsync(
                            run,
                            eventSequence,
                            responseBudget.Reason!,
                            invocationStartedAt,
                            elapsedAtStart)
                        .ConfigureAwait(false);
                }

                if (response.IsFinal)
                {
                    messages.Add(new ModelMessage
                    {
                        Role = "assistant",
                        Content = response.FinalOutput.Clone()
                    });

                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.AssistantCompleted,
                        response.FinalOutput,
                        deadline).ConfigureAwait(false);
                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.TurnCompleted,
                        ProtocolJson.ToElement(new TurnCompletedEventPayload
                        {
                            Outcome = "final"
                        }),
                        deadline).ConfigureAwait(false);

                    run.CurrentTurnId = null;
                    SetRunState(run, RunStates.Completed);
                    UpdateDuration(
                        run,
                        invocationStartedAt,
                        elapsedAtStart);
                    await AppendAsync(
                        run,
                        eventSequence,
                        null,
                        RuntimeEventKinds.RunCompleted,
                        ProtocolJson.ToElement(new RunUsageEventPayload
                        {
                            Usage = run.Usage
                        }),
                        deadline).ConfigureAwait(false);

                    return new HeadlessRunOutcome
                    {
                        Run = run,
                        FinalOutput = response.FinalOutput.Clone()
                    };
                }

                if (response.ToolCalls.Count == 0)
                {
                    throw new InvalidOperationException(
                        "A model response must contain either final output or tool calls.");
                }

                foreach (var toolCall in response.ToolCalls)
                {
                    if (!toolsByName.TryGetValue(toolCall.Name, out var descriptor))
                    {
                        throw new InvalidOperationException(
                            $"The model requested unknown tool '{toolCall.Name}'.");
                    }

                    var actionDecision = budget.CanDispatchAction(
                        run.Usage,
                        _clock.UtcNow);
                    if (!actionDecision.Allowed)
                    {
                        return await BudgetExhaustedOutcomeAsync(
                                run,
                                eventSequence,
                                actionDecision.Reason!,
                                invocationStartedAt,
                                elapsedAtStart)
                            .ConfigureAwait(false);
                    }

                    var safety = _toolSafety.Validate(
                        new ToolCatalogEntry(descriptor, new RegistryLimits()),
                        toolCall.Arguments,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["agentId"] = run.AgentId,
                            ["worldId"] = run.WorldId,
                            ["runId"] = run.RunId,
                            ["turnId"] = turnId
                        });
                    if (!safety.IsValid)
                    {
                        throw new InvalidOperationException(
                            "The model supplied invalid tool arguments.");
                    }

                    var invocation = new ToolInvocation
                    {
                        ToolCallId = toolCall.ToolCallId,
                        RunId = run.RunId,
                        TurnId = turnId,
                        AttemptId = attemptId,
                        ToolName = descriptor.Name,
                        ToolVersion = descriptor.Version,
                        Arguments = toolCall.Arguments.Clone(),
                        Effect = descriptor.Effect,
                        ResolvedConflictKeys =
                            safety.ResolvedConflictKeys.ToList(),
                        Sequence = run.Usage.Actions,
                        CreatedAt = _clock.UtcNow
                    };

                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.ToolStarted,
                        ProtocolJson.ToElement(invocation),
                        deadline).ConfigureAwait(false);

                    var operationId = _ids.NewId("operation");
                    var requestedAt = _clock.UtcNow;
                    var toolMonotonicDeadline =
                        MonotonicDeadline.Start(
                            TimeSpan.FromMilliseconds(
                                descriptor.TimeoutMs));
                    var runRemaining = Math.Max(
                        0,
                        (long)runMonotonicDeadline
                            .Remaining.TotalMilliseconds);
                    var actionTimeoutMs = (int)Math.Min(
                        descriptor.TimeoutMs,
                        runRemaining);
                    var actionRequest = new ActionRequest
                    {
                        OperationId = operationId,
                        RunId = run.RunId,
                        TurnId = turnId,
                        ToolCallId = toolCall.ToolCallId,
                        AgentId = run.AgentId,
                        WorldId = run.WorldId,
                        ActionName = descriptor.Name,
                        ActionVersion = descriptor.Version,
                        Arguments = toolCall.Arguments.Clone(),
                        RequestedAt = requestedAt,
                        Deadline = requestedAt.AddMilliseconds(
                            actionTimeoutMs)
                    };
                    ProtocolValidator.EnsureValid(actionRequest);

                    run.PendingOperationIds.Add(operationId);
                    SetRunState(run, RunStates.WaitingForAction);

                    // Write-ahead is the safety boundary: the request is durable before
                    // game code receives a side-effecting operation.
                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.ActionRequested,
                        ProtocolJson.ToElement(actionRequest),
                        deadline).ConfigureAwait(false);

                    ActionWaitResult actionWait;
                    if (ActionRemaining(
                            actionRequest.Deadline!.Value,
                            toolMonotonicDeadline,
                            runMonotonicDeadline)
                        <= TimeSpan.Zero)
                    {
                        actionWait = new ActionWaitResult(
                            completed: true,
                            FailedActionReceipt(
                                actionRequest,
                                "action_deadline_expired"));
                    }
                    else
                    {
                        var actionLease = TryAdmitAction();
                        if (actionLease is null)
                        {
                            actionWait = new ActionWaitResult(
                                completed: true,
                                FailedActionReceipt(
                                    actionRequest,
                                    "action_capacity_exceeded"));
                        }
                        else
                        {
                            actionWait = await AwaitActionAsync(
                                    actionRequest,
                                    token => _gameHost.SubmitActionAsync(
                                        Snapshot(actionRequest),
                                    token),
                                    actionLease,
                                    toolMonotonicDeadline,
                                    runMonotonicDeadline,
                                    deadline.Token)
                                .ConfigureAwait(false);
                        }
                    }
                    if (!actionWait.Completed)
                    {
                        return await ReconciliationOutcomeAsync(
                                run,
                                eventSequence,
                                operationId,
                                invocationStartedAt,
                                elapsedAtStart)
                            .ConfigureAwait(false);
                    }

                    var receipt =
                        ActionReceiptIngressValidator.ValidateAndClone(
                            actionRequest,
                            actionWait.Receipt!);

                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(receipt),
                        deadline).ConfigureAwait(false);

                    if (receipt.Status == ReceiptStatuses.Unknown)
                    {
                        return await ReconciliationOutcomeAsync(
                                run,
                                eventSequence,
                                operationId,
                                invocationStartedAt,
                                elapsedAtStart)
                            .ConfigureAwait(false);
                    }

                    run.PendingOperationIds.Remove(operationId);
                    run.Usage.Actions = checked(run.Usage.Actions + 1);
                    SetRunState(run, RunStates.Running);
                    messages.Add(new ModelMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.ToolCallId,
                        Content = ProtocolJson.ToElement(receipt)
                    });

                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        receipt.Status == ReceiptStatuses.Failed
                            ? RuntimeEventKinds.ToolFailed
                            : RuntimeEventKinds.ToolCompleted,
                        ProtocolJson.ToElement(receipt),
                        deadline).ConfigureAwait(false);

                    UpdateDuration(
                        run,
                        invocationStartedAt,
                        elapsedAtStart);
                    var afterAction = budget.CheckAfterCharge(
                        run.Usage,
                        _clock.UtcNow);
                    if (!afterAction.Allowed)
                    {
                        return await BudgetExhaustedOutcomeAsync(
                                run,
                                eventSequence,
                                afterAction.Reason!,
                                invocationStartedAt,
                                elapsedAtStart)
                            .ConfigureAwait(false);
                    }
                }

                await AppendAsync(
                    run,
                    eventSequence,
                    turnId,
                    RuntimeEventKinds.TurnCompleted,
                    ProtocolJson.ToElement(new TurnCompletedEventPayload
                    {
                        Outcome = "tools_completed"
                    }),
                    deadline).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            if (run.PendingOperationIds.Count > 0)
            {
                return await ReconciliationOutcomeAsync(
                        run,
                        eventSequence,
                        run.PendingOperationIds[0],
                        invocationStartedAt,
                        elapsedAtStart)
                    .ConfigureAwait(false);
            }

            return await BudgetExhaustedOutcomeAsync(
                    run,
                    eventSequence,
                    "max_duration",
                    invocationStartedAt,
                    elapsedAtStart)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (run.PendingOperationIds.Count > 0)
            {
                return await ReconciliationOutcomeAsync(
                        run,
                        eventSequence,
                        run.PendingOperationIds[0],
                        invocationStartedAt,
                        elapsedAtStart)
                    .ConfigureAwait(false);
            }

            run.CurrentTurnId = null;
            run.TerminalReason = "cancelled";
            SetRunState(run, RunStates.Cancelled);
            UpdateDuration(
                run,
                invocationStartedAt,
                elapsedAtStart);
            await AppendWithoutCancellationAsync(
                run,
                eventSequence,
                RuntimeEventKinds.RunCancelled,
                ProtocolJson.ToElement(new BudgetEventPayload
                {
                    Reason = "cancelled"
                })).ConfigureAwait(false);
            return new HeadlessRunOutcome { Run = run };
        }
        catch (Exception exception)
        {
            if (run.PendingOperationIds.Count > 0)
            {
                return await ReconciliationOutcomeAsync(
                        run,
                        eventSequence,
                        run.PendingOperationIds[0],
                        invocationStartedAt,
                        elapsedAtStart)
                    .ConfigureAwait(false);
            }

            run.CurrentTurnId = null;
            run.TerminalReason = exception.GetType().Name;
            SetRunState(run, RunStates.Failed);
            UpdateDuration(
                run,
                invocationStartedAt,
                elapsedAtStart);
            await AppendWithoutCancellationAsync(
                run,
                eventSequence,
                RuntimeEventKinds.RunFailed,
                ProtocolJson.ToElement(new RuntimeErrorEventPayload
                {
                    Code = "runtime_error",
                    Category = "internal",
                    Message = "The runtime failed. Inspect the structured trace for details.",
                    Usage = run.Usage
                })).ConfigureAwait(false);
            return new HeadlessRunOutcome { Run = run };
        }
    }

    private async ValueTask<HeadlessRunOutcome> BudgetExhaustedOutcomeAsync(
        AgentRun run,
        RunEventSequence eventSequence,
        string reason,
        DateTimeOffset invocationStartedAt,
        long elapsedAtStart)
    {
        var turnId = run.CurrentTurnId;
        run.CurrentTurnId = null;
        run.TerminalReason = reason;
        UpdateDuration(
            run,
            invocationStartedAt,
            elapsedAtStart);
        if (string.Equals(reason, "max_duration", StringComparison.Ordinal))
        {
            run.Usage.DurationMs = Math.Max(
                run.Usage.DurationMs,
                run.Budget.MaxDurationMs);
        }

        SetRunState(run, RunStates.BudgetExhausted);
        await AppendWithoutCancellationAsync(
                run,
                eventSequence,
                RuntimeEventKinds.BudgetUpdated,
                ProtocolJson.ToElement(new BudgetEventPayload
                {
                    Reason = reason
                }),
                turnId)
            .ConfigureAwait(false);
        return new HeadlessRunOutcome { Run = run };
    }

    private async ValueTask<HeadlessRunOutcome> ReconciliationOutcomeAsync(
        AgentRun run,
        RunEventSequence eventSequence,
        string operationId,
        DateTimeOffset invocationStartedAt,
        long elapsedAtStart)
    {
        run.TerminalReason = null;
        SetRunState(run, RunStates.Reconciling);
        UpdateDuration(
            run,
            invocationStartedAt,
            elapsedAtStart);
        await AppendWithoutCancellationAsync(
                run,
                eventSequence,
                RuntimeEventKinds.ActionReconciling,
                ProtocolJson.ToElement(
                    new ActionReconcilingEventPayload
                    {
                        OperationId = operationId
                    }),
                run.CurrentTurnId)
            .ConfigureAwait(false);
        return new HeadlessRunOutcome { Run = run };
    }

    private async ValueTask AppendAsync(
        AgentRun run,
        RunEventSequence eventSequence,
        string? turnId,
        string kind,
        JsonElement payload,
        HeadlessRunDeadline deadline)
    {
        var runtimeEvent = new RuntimeEvent
        {
            EventId = _ids.NewId("event"),
            RunId = run.RunId,
            TurnId = turnId,
            Sequence = eventSequence.Next(),
            Kind = kind,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = run.RuntimeGeneration,
            Timestamp = _clock.UtcNow,
            Payload = payload.Clone()
        };

        var append = eventSequence.EnqueuePersistence(
            () => _sessionStore
                .AppendAsync(runtimeEvent, deadline.Token)
                .AsTask());
        await AwaitBoundedAsync(
                new ValueTask(append),
                deadline)
            .ConfigureAwait(false);
    }

    private async ValueTask AppendWithoutCancellationAsync(
        AgentRun run,
        RunEventSequence eventSequence,
        string kind,
        JsonElement payload,
        string? turnId = null)
    {
        try
        {
            var runtimeEvent = new RuntimeEvent
            {
                EventId = _ids.NewId("event"),
                RunId = run.RunId,
                TurnId = turnId,
                Sequence = eventSequence.Next(),
                Kind = kind,
                Durability = EventDurabilities.Durable,
                RuntimeGeneration = run.RuntimeGeneration,
                Timestamp = _clock.UtcNow,
                Payload = payload.Clone()
            };
            await eventSequence
                .EnqueuePersistence(
                    () => _sessionStore
                        .AppendAsync(runtimeEvent, CancellationToken.None)
                        .AsTask())
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original terminal state if persistence is also unavailable.
        }
    }

    private void SetRunState(AgentRun run, string state)
    {
        run.State = state;
        run.Revision++;
        run.UpdatedAt = _clock.UtcNow;
    }

    private void UpdateDuration(
        AgentRun run,
        DateTimeOffset invocationStartedAt,
        long elapsedAtStart)
    {
        var elapsedThisInvocation = Math.Max(
            0,
            (long)(_clock.UtcNow - invocationStartedAt).TotalMilliseconds);
        var total = elapsedAtStart > long.MaxValue - elapsedThisInvocation
            ? long.MaxValue
            : elapsedAtStart + elapsedThisInvocation;
        run.Usage.DurationMs = Math.Max(run.Usage.DurationMs, total);
        run.UpdatedAt = _clock.UtcNow;
    }

    private ActiveRunLease Admit(string runId)
    {
        lock (_activeRunSync)
        {
            if (_activeRuns.Contains(runId))
            {
                throw new DuplicateRunException(runId);
            }

            if (_activeRuns.Count >= _limits.MaxActiveRuns)
            {
                throw new RunWorkloadCapacityExceededException(
                    RunWorkloadCapacityReasonCodes.MaxActiveRuns,
                    _limits.MaxActiveRuns);
            }

            if (!_activeRuns.Add(runId))
            {
                throw new InvalidOperationException(
                    "Headless run admission is inconsistent.");
            }
        }

        return new ActiveRunLease(this, runId);
    }

    private void ReleaseAdmission(string runId)
    {
        lock (_activeRunSync)
        {
            if (!_activeRuns.Remove(runId))
            {
                throw new InvalidOperationException(
                    "Headless run admission accounting is inconsistent.");
            }
        }
    }

    private ActionDispatchLease? TryAdmitAction()
    {
        return _actionSlots.Wait(0)
            ? new ActionDispatchLease(_actionSlots)
            : null;
    }

    private HeadlessRunRequest Snapshot(
        HeadlessRunRequest request,
        AgentRun runSnapshot,
        CancellationToken cancellationToken)
    {
        var observations = request.Observations
            ?? throw new ArgumentException(
                "A headless run request requires an observation collection.",
                nameof(request));
        var tools = request.Tools
            ?? throw new ArgumentException(
                "A headless run request requires a tool collection.",
                nameof(request));

        long inputBytes = 0;
        var observationSnapshot = RuntimeInputGuard.CopyBounded(
            observations,
            _limits.MaxObservations,
            observation =>
            {
                if (observation is null)
                {
                    throw new ArgumentException(
                        "Observation collections cannot contain null entries.",
                        nameof(request));
                }

                var safeObservation = RuntimeProtocolInputGuard
                    .ValidateObservationBeforeSerialization(
                        observation,
                        _limits.InputJsonLimits,
                        MaximumInputItemBytes,
                        nameof(request.Observations));
                var encoded = ProtocolJson.ToElement(safeObservation);
                ChargeInput(
                    ref inputBytes,
                    JsonValueInspector.ValidateAndMeasure(
                        encoded,
                        _limits.InputJsonLimits,
                        nameof(request.Observations)));
                return ProtocolJson.DeserializeObservationEnvelope(
                    encoded.GetRawText());
            },
            nameof(request.Observations),
            "observation_count_exceeded",
            cancellationToken);
        var toolSnapshot = RuntimeInputGuard.CopyBounded(
            tools,
            _limits.MaxTools,
            tool =>
            {
                if (tool is null)
                {
                    throw new ArgumentException(
                        "Tool collections cannot contain null entries.",
                        nameof(request));
                }

                var safeTool =
                    RuntimeProtocolInputGuard
                        .ValidateToolBeforeSerialization(
                            tool,
                            _limits.InputJsonLimits,
                            MaximumInputItemBytes,
                            nameof(request.Tools));
                var encoded = ProtocolJson.ToElement(safeTool);
                ChargeInput(
                    ref inputBytes,
                    JsonValueInspector.ValidateAndMeasure(
                        encoded,
                        _limits.InputJsonLimits,
                        nameof(request.Tools)));
                return ProtocolJson.DeserializeToolDescriptor(
                    encoded.GetRawText());
            },
            nameof(request.Tools),
            "tool_count_exceeded",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var encodedRun = ProtocolJson.ToElement(runSnapshot);
        ChargeInput(
            ref inputBytes,
            JsonValueInspector.ValidateAndMeasure(
                encodedRun,
                _limits.InputJsonLimits,
                nameof(request.Run)));

        return new HeadlessRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(encodedRun.GetRawText()),
            Observations = observationSnapshot,
            Tools = toolSnapshot
        };

        void ChargeInput(ref long total, int bytes)
        {
            if (bytes > _limits.MaxInputUtf8Bytes - total)
            {
                throw new RuntimeContentLimitException(
                    nameof(request),
                    "headless_input_bytes_exceeded",
                    "The headless run input exceeds its byte limit.");
            }
            total += bytes;
        }
    }

    private int MaximumInputItemBytes => Math.Min(
        _limits.MaxInputUtf8Bytes,
        _limits.InputJsonLimits.MaxUtf8Bytes);

    private static ToolDescriptor Snapshot(ToolDescriptor tool)
    {
        if (tool is null)
        {
            throw new ArgumentException(
                "Tool collections cannot contain null entries.",
                nameof(tool));
        }

        return ProtocolJson.DeserializeToolDescriptor(
            ProtocolJson.Serialize(tool));
    }

    private static IReadOnlyList<ModelMessage> SnapshotMessages(
        IReadOnlyList<ModelMessage> messages)
    {
        var snapshot = new ModelMessage[messages.Count];
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index]
                ?? throw new ArgumentException(
                    "Provider message lists cannot contain null entries.",
                    nameof(messages));
            snapshot[index] = new ModelMessage
            {
                Role = message.Role,
                ToolCallId = message.ToolCallId,
                Content = message.Content.Clone()
            };
        }

        return snapshot;
    }

    private static IReadOnlyList<ToolDescriptor> SnapshotTools(
        IReadOnlyList<ToolDescriptor> tools)
    {
        var snapshot = new ToolDescriptor[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            snapshot[index] = Snapshot(tools[index]);
        }

        return snapshot;
    }

    private static ActionRequest Snapshot(ActionRequest request)
    {
        return ProtocolJson.DeserializeActionRequest(
            ProtocolJson.Serialize(request));
    }

    private static ProviderResponseSnapshot Snapshot(
        ModelResponse response,
        ProviderUsage usage)
    {
        var sourceCalls = response.ToolCalls
            ?? throw new InvalidDataException(
                "The model provider returned no tool-call collection.");
        if (response.IsFinal)
        {
            if (sourceCalls.Count != 0)
            {
                throw new InvalidDataException(
                    "A final model response cannot also contain tool calls.");
            }

            var finalOutput = response.FinalOutput.Clone();
            JsonValueInspector.ValidateAndMeasure(
                finalOutput,
                new JsonValueLimits(),
                nameof(response));
            return new ProviderResponseSnapshot(
                isFinal: true,
                finalOutput,
                Array.Empty<ModelToolCall>(),
                usage);
        }

        if (sourceCalls.Count == 0)
        {
            throw new InvalidDataException(
                "A non-final model response requires at least one tool call.");
        }

        if (sourceCalls.Count > MaxProviderToolCalls)
        {
            throw new InvalidDataException(
                "The model provider returned too many tool calls.");
        }

        if (response.FinalOutput.ValueKind != JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                "A tool-call model response cannot also contain final output.");
        }

        var calls = new ModelToolCall[sourceCalls.Count];
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var totalArgumentBytes = 0;
        for (var index = 0; index < sourceCalls.Count; index++)
        {
            var source = sourceCalls[index]
                ?? throw new InvalidDataException(
                    "Model tool-call collections cannot contain null entries.");
            var toolCallId = RuntimeGuard.RequiredId(
                source.ToolCallId,
                nameof(source.ToolCallId));
            if (!callIds.Add(toolCallId))
            {
                throw new InvalidDataException(
                    "Model tool-call identifiers must be unique per response.");
            }

            var name = RuntimeGuard.RequiredUtf8(
                source.Name,
                96,
                nameof(source.Name));
            var arguments = source.Arguments.Clone();
            var argumentBytes = JsonValueInspector.ValidateAndMeasure(
                arguments,
                new JsonValueLimits(),
                nameof(source.Arguments));
            totalArgumentBytes = checked(totalArgumentBytes + argumentBytes);
            if (totalArgumentBytes > MaxProviderToolArgumentsUtf8Bytes)
            {
                throw new InvalidDataException(
                    "The model provider returned too many aggregate tool-argument bytes.");
            }

            calls[index] = new ModelToolCall
            {
                ToolCallId = toolCallId,
                Name = name,
                Arguments = arguments
            };
        }

        return new ProviderResponseSnapshot(
            isFinal: false,
            default,
            calls,
            usage);
    }

    private static DateTimeOffset BudgetStartedAt(
        DateTimeOffset now,
        long elapsedDurationMs)
    {
        if (elapsedDurationMs <= 0)
        {
            return now;
        }

        try
        {
            return now.AddMilliseconds(-elapsedDurationMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string? AccumulateUsage(
        AgentUsage usage,
        ProviderUsage delta)
    {
        var tokenOverflow = false;
        usage.InputTokens = SaturatingAdd(
            usage.InputTokens,
            delta.InputTokens,
            out var inputOverflow);
        tokenOverflow |= inputOverflow;
        usage.OutputTokens = SaturatingAdd(
            usage.OutputTokens,
            delta.OutputTokens,
            out var outputOverflow);
        tokenOverflow |= outputOverflow;
        usage.CostUsd = SaturatingAddCost(
            usage.CostUsd,
            delta.CostUsd,
            out var costOverflow);

        if (tokenOverflow)
        {
            return "max_tokens";
        }

        return costOverflow ? "max_cost" : null;
    }

    private static int SaturatingAdd(
        int current,
        int delta,
        out bool overflow)
    {
        var sum = (long)current + delta;
        overflow = sum > int.MaxValue;
        return overflow ? int.MaxValue : (int)sum;
    }

    private static string SaturatingAddCost(
        string current,
        string delta,
        out bool overflow)
    {
        if (!decimal.TryParse(
                current,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var currentValue)
            || currentValue < 0
            || !decimal.TryParse(
                delta,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var deltaValue)
            || deltaValue < 0)
        {
            throw new InvalidDataException(
                "Provider usage cost is invalid.");
        }

        decimal sum;
        try
        {
            sum = currentValue + deltaValue;
            overflow = false;
        }
        catch (OverflowException)
        {
            sum = decimal.MaxValue;
            overflow = true;
        }

        return sum.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask<T> AwaitBoundedAsync<T>(
        ValueTask<T> operation,
        HeadlessRunDeadline deadline)
    {
        var cancellationToken = deadline.Token;
        var task = operation.AsTask();
        if (deadline.IsExpired)
        {
            if (!task.IsCompleted)
            {
                deadline.RetainAdmissionUntil(task);
            }
            _ = ObserveDetachedAsync(task);
            throw new OperationCanceledException(cancellationToken);
        }
        if (!task.IsCompleted)
        {
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                () => cancelled.TrySetResult(true));
            var completed = await Task.WhenAny(
                    task,
                    cancelled.Task,
                    deadline.ExpirationSignal)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                deadline.RetainAdmissionUntil(task);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                throw new OperationCanceledException(cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveDetachedAsync(task);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await task.ConfigureAwait(false);
        if (deadline.IsExpired)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async ValueTask AwaitBoundedAsync(
        ValueTask operation,
        HeadlessRunDeadline deadline)
    {
        var cancellationToken = deadline.Token;
        var task = operation.AsTask();
        if (deadline.IsExpired)
        {
            if (!task.IsCompleted)
            {
                deadline.RetainAdmissionUntil(task);
            }
            _ = ObserveDetachedAsync(task);
            throw new OperationCanceledException(cancellationToken);
        }
        if (!task.IsCompleted)
        {
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                () => cancelled.TrySetResult(true));
            var completed = await Task.WhenAny(
                    task,
                    cancelled.Task,
                    deadline.ExpirationSignal)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                deadline.RetainAdmissionUntil(task);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                throw new OperationCanceledException(cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveDetachedAsync(task);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await task.ConfigureAwait(false);
        if (deadline.IsExpired)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask<ActionWaitResult> AwaitActionAsync(
        ActionRequest request,
        Func<CancellationToken, ValueTask<ActionReceipt>> action,
        ActionDispatchLease dispatchLease,
        MonotonicDeadline toolMonotonicDeadline,
        MonotonicDeadline runMonotonicDeadline,
        CancellationToken runCancellationToken)
    {
        var actionDeadline = request.Deadline
            ?? throw new InvalidOperationException(
                "A dispatched action requires a deadline.");
        TimeSpan initialRemaining;
        try
        {
            initialRemaining = ActionRemaining(
                actionDeadline,
                toolMonotonicDeadline,
                runMonotonicDeadline);
        }
        catch
        {
            dispatchLease.Dispose();
            throw;
        }

        if (initialRemaining <= TimeSpan.Zero)
        {
            dispatchLease.Dispose();
            return new ActionWaitResult(
                completed: true,
                FailedActionReceipt(request, "action_deadline_expired"));
        }

        if (runCancellationToken.IsCancellationRequested)
        {
            dispatchLease.Dispose();
            runCancellationToken.ThrowIfCancellationRequested();
        }

        if (!_cancellationDispatcher.TryReserve(
                out var cancellationReservation))
        {
            dispatchLease.Dispose();
            return new ActionWaitResult(
                completed: true,
                FailedActionReceipt(
                    request,
                    "action_cancellation_capacity_exceeded"));
        }

        var executionCancellation = new CancellationTokenSource();
        CancellationTokenSource? timeoutCancellation = null;
        Task timeout;
        try
        {
            timeoutCancellation = new CancellationTokenSource();
            var remaining = ActionRemaining(
                actionDeadline,
                toolMonotonicDeadline,
                runMonotonicDeadline);
            timeout = remaining <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(remaining, timeoutCancellation.Token);
        }
        catch
        {
            timeoutCancellation?.Dispose();
            executionCancellation.Dispose();
            cancellationReservation!.Dispose();
            dispatchLease.Dispose();
            throw;
        }

        using (timeoutCancellation)
        {
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellationRegistration;
            try
            {
                cancellationRegistration = runCancellationToken.Register(
                    () => cancelled.TrySetResult(true));
            }
            catch
            {
                try
                {
                    timeoutCancellation.Cancel();
                    await ObserveCancellationAsync(timeout)
                        .ConfigureAwait(false);
                }
                finally
                {
                    cancellationReservation!.Dispose();
                    executionCancellation.Dispose();
                    dispatchLease.Dispose();
                }

                throw;
            }

            using (cancellationRegistration)
            {
                Task<ActionReceipt>? execution = null;
                var resourcesReleased = false;
                var ownershipTransferred = false;
                try
                {
                    runCancellationToken.ThrowIfCancellationRequested();
                    if (timeout.IsCompleted
                        || ActionRemaining(
                            actionDeadline,
                            toolMonotonicDeadline,
                            runMonotonicDeadline)
                        <= TimeSpan.Zero)
                    {
                        ReleaseCompletedExecution();
                        return new ActionWaitResult(
                            completed: true,
                            FailedActionReceipt(
                                request,
                                "action_deadline_expired"));
                    }

                    execution = action(executionCancellation.Token).AsTask();
                    var completed = await Task.WhenAny(
                            execution,
                            timeout,
                            cancelled.Task)
                        .ConfigureAwait(false);

                    var executionWonBeforeDeadline =
                        ReferenceEquals(completed, execution)
                        && ActionRemaining(
                            actionDeadline,
                            toolMonotonicDeadline,
                            runMonotonicDeadline)
                        > TimeSpan.Zero;
                    timeoutCancellation.Cancel();
                    await ObserveCancellationAsync(timeout)
                        .ConfigureAwait(false);

                    if (executionWonBeforeDeadline)
                    {
                        var receipt = await execution.ConfigureAwait(false);
                        ReleaseCompletedExecution();
                        runCancellationToken.ThrowIfCancellationRequested();
                        return new ActionWaitResult(
                            completed: true,
                            receipt);
                    }

                    TransferExecutionOwnership();
                    runCancellationToken.ThrowIfCancellationRequested();
                    return new ActionWaitResult(
                        completed: false,
                        receipt: null);
                }
                catch
                {
                    try
                    {
                        timeoutCancellation.Cancel();
                        await ObserveCancellationAsync(timeout)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!ownershipTransferred)
                        {
                            if (execution is not null
                                && !execution.IsCompleted)
                            {
                                TransferExecutionOwnership();
                            }
                            else
                            {
                                if (execution is not null)
                                {
                                    _ = ObserveDetachedAsync(execution);
                                }

                                ReleaseCompletedExecution();
                            }
                        }
                    }

                    throw;
                }

                void ReleaseCompletedExecution()
                {
                    if (resourcesReleased || ownershipTransferred)
                    {
                        return;
                    }

                    resourcesReleased = true;
                    try
                    {
                        executionCancellation.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            dispatchLease.Dispose();
                        }
                        finally
                        {
                            cancellationReservation!.Dispose();
                        }
                    }
                }

                void TransferExecutionOwnership()
                {
                    if (ownershipTransferred || resourcesReleased)
                    {
                        return;
                    }

                    if (execution is null || execution.IsCompleted)
                    {
                        if (execution is not null)
                        {
                            _ = ObserveDetachedAsync(execution);
                        }

                        ReleaseCompletedExecution();
                        return;
                    }

                    ownershipTransferred = true;
                    Task cancellation;
                    try
                    {
                        cancellation =
                            cancellationReservation!
                                .DispatchAsync(executionCancellation);
                    }
                    catch
                    {
                        cancellation = Task.CompletedTask;
                    }

                    _ = ObserveDetachedActionAsync(
                        execution,
                        executionCancellation,
                        cancellation,
                        dispatchLease,
                        cancellationReservation!);
                }
            }
        }
    }

    private TimeSpan ActionRemaining(
        DateTimeOffset deadline,
        MonotonicDeadline toolMonotonicDeadline,
        MonotonicDeadline runMonotonicDeadline)
    {
        var utcRemaining = deadline - _clock.UtcNow;
        var toolRemaining = toolMonotonicDeadline.Remaining;
        var runRemaining = runMonotonicDeadline.Remaining;
        if (utcRemaining <= TimeSpan.Zero
            || toolRemaining <= TimeSpan.Zero
            || runRemaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var monotonicRemaining = toolRemaining < runRemaining
            ? toolRemaining
            : runRemaining;
        return utcRemaining < monotonicRemaining
            ? utcRemaining
            : monotonicRemaining;
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveDetachedActionAsync(
        Task<ActionReceipt> action,
        CancellationTokenSource cancellationSource,
        Task cancellation,
        ActionDispatchLease dispatchLease,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await action.ConfigureAwait(false);
        }
        catch
        {
            // A reconciliation outcome already owns this operation. A late
            // receipt or failure must not mutate the completed runtime call.
        }
        finally
        {
            dispatchLease.Dispose();
            _ = DisposeCancellationWhenReadyAsync(
                cancellationSource,
                cancellation,
                cancellationReservation);
        }
    }

    private static async Task DisposeCancellationWhenReadyAsync(
        CancellationTokenSource cancellationSource,
        Task cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation is best-effort after the action task has ended.
        }
        finally
        {
            try
            {
                cancellationSource.Dispose();
            }
            catch
            {
                // Host cancellation cleanup cannot affect runtime admission.
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private ActionReceipt FailedActionReceipt(
        ActionRequest request,
        string errorCode)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = 0,
            Status = ReceiptStatuses.Failed,
            ErrorCode = errorCode,
            Retryable = false,
            ReceivedAt = _clock.UtcNow
        };
    }

    private static async Task ObserveDetachedAsync<T>(Task<T> task)
    {
        try
        {
            _ = await task.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller has already returned a cancellation or
            // deadline outcome. Late provider or host failures are observed.
        }
    }

    private static async Task ObserveDetachedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller has already returned a cancellation or
            // deadline outcome. Late provider or store failures are observed.
        }
    }

    private sealed class ProviderResponseSnapshot
    {
        public ProviderResponseSnapshot(
            bool isFinal,
            JsonElement finalOutput,
            IReadOnlyList<ModelToolCall> toolCalls,
            ProviderUsage usage)
        {
            IsFinal = isFinal;
            FinalOutput = finalOutput;
            ToolCalls = toolCalls;
            Usage = usage;
        }

        public bool IsFinal { get; }

        public JsonElement FinalOutput { get; }

        public IReadOnlyList<ModelToolCall> ToolCalls { get; }

        public ProviderUsage Usage { get; }
    }

    private sealed class ActionWaitResult
    {
        public ActionWaitResult(bool completed, ActionReceipt? receipt)
        {
            Completed = completed;
            Receipt = receipt;
        }

        public bool Completed { get; }

        public ActionReceipt? Receipt { get; }
    }

    private sealed class RunEventSequence
    {
        private readonly object _persistenceSync = new();
        private Task _persistenceTail = Task.CompletedTask;
        private long _value;

        public long Next()
        {
            return Interlocked.Increment(ref _value) - 1;
        }

        public Task EnqueuePersistence(Func<Task> append)
        {
            if (append is null)
            {
                throw new ArgumentNullException(nameof(append));
            }

            lock (_persistenceSync)
            {
                _persistenceTail = AppendAfterAsync(
                    _persistenceTail,
                    append);
                return _persistenceTail;
            }
        }

        private static async Task AppendAfterAsync(
            Task predecessor,
            Func<Task> append)
        {
            try
            {
                await predecessor.ConfigureAwait(false);
            }
            catch
            {
                // A terminal checkpoint may still be attempted after a failed
                // earlier write, but it must never overtake that write.
            }

            await append().ConfigureAwait(false);
        }
    }

    private sealed class ActiveRunLease : IDisposable
    {
        private readonly HeadlessAgentRuntime _owner;
        private readonly string _runId;
        private int _referenceCount = 1;

        public ActiveRunLease(HeadlessAgentRuntime owner, string runId)
        {
            _owner = owner;
            _runId = runId;
        }

        public void RetainUntil(Task operation)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            while (true)
            {
                var current = Volatile.Read(ref _referenceCount);
                if (current == 0)
                {
                    throw new ObjectDisposedException(nameof(ActiveRunLease));
                }
                if (current == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Headless run operation ownership overflowed.");
                }
                if (Interlocked.CompareExchange(
                        ref _referenceCount,
                        current + 1,
                        current) == current)
                {
                    break;
                }
            }

            _ = ReleaseAfterAsync(operation);
        }

        public void Dispose()
        {
            ReleaseReference();
        }

        private async Task ReleaseAfterAsync(Task operation)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                // The bounded caller already owns the visible outcome.
            }
            finally
            {
                ReleaseReference();
            }
        }

        private void ReleaseReference()
        {
            var remaining = Interlocked.Decrement(ref _referenceCount);
            if (remaining == 0)
            {
                _owner.ReleaseAdmission(_runId);
            }
            else if (remaining < 0)
            {
                throw new InvalidOperationException(
                    "Headless run operation ownership underflowed.");
            }
        }
    }

    private sealed class ActionDispatchLease : IDisposable
    {
        private SemaphoreSlim? _slots;

        public ActionDispatchLease(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose()
        {
            var slots = Interlocked.Exchange(ref _slots, null);
            slots?.Release();
        }
    }

    private sealed class HeadlessRunDeadline : IDisposable
    {
        private readonly ActiveRunLease _admission;
        private readonly CancellationToken _externalToken;
        private readonly CancellationTokenSource _expiration = new();
        private readonly CancellationTokenSource _stopTimer = new();
        private readonly CancellationTokenSource _linked;
        private readonly Task _timerTask;
        private readonly TaskCompletionSource<bool> _expirationSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private BoundedCancellationDispatcher.CancellationDispatchReservation?
            _expirationCancellationReservation;
        private Task? _expirationCancellation;
        private int _expired;
        private int _disposed;

        public HeadlessRunDeadline(
            long maxDurationMs,
            long elapsedDurationMs,
            CancellationToken externalToken,
            BoundedCancellationDispatcher cancellationDispatcher,
            ActiveRunLease admission)
        {
            _admission = admission ?? throw new ArgumentNullException(
                nameof(admission));
            _externalToken = externalToken;
            if (!cancellationDispatcher.TryReserve(
                    out _expirationCancellationReservation))
            {
                throw new InvalidOperationException(
                    "Run-deadline cancellation capacity is exhausted.");
            }

            try
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(
                    externalToken,
                    _expiration.Token);
            }
            catch
            {
                _expirationCancellationReservation!.Dispose();
                _stopTimer.Dispose();
                _expiration.Dispose();
                throw;
            }

            var elapsed = Math.Max(0, elapsedDurationMs);
            var remaining = maxDurationMs <= elapsed
                ? 0
                : maxDurationMs - elapsed;
            if (remaining == 0)
            {
                Expire();
                _timerTask = Task.CompletedTask;
            }
            else
            {
                _timerTask = ExpireAsync(remaining);
            }
        }

        public CancellationToken Token => _linked.Token;

        public Task ExpirationSignal => _expirationSignal.Task;

        public bool IsExpired =>
            Volatile.Read(ref _expired) != 0
            && !_externalToken.IsCancellationRequested;

        public void RetainAdmissionUntil(Task operation)
        {
            _admission.RetainUntil(operation);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopTimer.Cancel();
            try
            {
                _timerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            var cancellation = _expirationCancellation;
            if (cancellation is null || cancellation.IsCompleted)
            {
                Cleanup();
            }
            else
            {
                _ = CleanupAfterCancellationAsync(cancellation);
            }
        }

        private async Task ExpireAsync(long remaining)
        {
            try
            {
                while (remaining > 0)
                {
                    var delay = (int)Math.Min(remaining, int.MaxValue);
                    await Task.Delay(delay, _stopTimer.Token)
                        .ConfigureAwait(false);
                    remaining -= delay;
                }
            }
            catch (OperationCanceledException)
                when (_stopTimer.IsCancellationRequested)
            {
                return;
            }

            Expire();
        }

        private void Expire()
        {
            if (Interlocked.Exchange(ref _expired, 1) != 0)
            {
                return;
            }

            _expirationSignal.TrySetResult(true);
            try
            {
                _expirationCancellation =
                    _expirationCancellationReservation!
                        .DispatchAsync(_expiration);
            }
            catch
            {
                _expirationCancellation = Task.CompletedTask;
            }
        }

        private async Task CleanupAfterCancellationAsync(
            Task cancellation)
        {
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            finally
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            try
            {
                _linked.Dispose();
            }
            finally
            {
                try
                {
                    _stopTimer.Dispose();
                }
                finally
                {
                    try
                    {
                        _expiration.Dispose();
                    }
                    finally
                    {
                        _expirationCancellationReservation?.Dispose();
                        _expirationCancellationReservation = null;
                    }
                }
            }
        }
    }
}
