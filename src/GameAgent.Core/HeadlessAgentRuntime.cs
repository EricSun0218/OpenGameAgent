using System.Globalization;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class HeadlessAgentRuntimeLimits
{
    public const int DefaultMaxActiveRuns = 256;

    public HeadlessAgentRuntimeLimits(
        int maxActiveRuns = DefaultMaxActiveRuns)
    {
        if (maxActiveRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActiveRuns));
        }

        MaxActiveRuns = maxActiveRuns;
    }

    public int MaxActiveRuns { get; }
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
            new HeadlessAgentRuntimeLimits())
    {
    }

    public HeadlessAgentRuntime(
        IModelProvider modelProvider,
        IGameHost gameHost,
        ISessionStore sessionStore,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        HeadlessAgentRuntimeLimits limits)
    {
        _modelProvider = modelProvider;
        _gameHost = gameHost;
        _sessionStore = sessionStore;
        _clock = clock;
        _ids = ids;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
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

    public async ValueTask<HeadlessRunOutcome> RunAsync(
        HeadlessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var requestSnapshot = Snapshot(request);
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
        using var admission = Admit(run.RunId);
        var eventSequence = new RunEventSequence();
        var invocationStartedAt = _clock.UtcNow;
        var elapsedAtStart = Math.Max(0, run.Usage.DurationMs);
        var budget = new BudgetTracker(
            run.Budget,
            BudgetStartedAt(invocationStartedAt, elapsedAtStart));
        using var deadline = new HeadlessRunDeadline(
            run.Budget.MaxDurationMs,
            elapsedAtStart,
            cancellationToken);
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
        var existingEvents = await AwaitBoundedAsync(
                _sessionStore.ReadRunAsync(run.RunId, deadline.Token),
                deadline.Token)
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
                deadline.Token).ConfigureAwait(false);

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
                    deadline.Token).ConfigureAwait(false);

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
                        deadline.Token)
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
                        deadline.Token).ConfigureAwait(false);
                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.TurnCompleted,
                        ProtocolJson.ToElement(new TurnCompletedEventPayload
                        {
                            Outcome = "final"
                        }),
                        deadline.Token).ConfigureAwait(false);

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
                        deadline.Token).ConfigureAwait(false);

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
                        deadline.Token).ConfigureAwait(false);

                    var operationId = _ids.NewId("operation");
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
                        RequestedAt = _clock.UtcNow,
                        Deadline = _clock.UtcNow.AddMilliseconds(descriptor.TimeoutMs)
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
                        deadline.Token).ConfigureAwait(false);

                    var hostReceipt = await AwaitBoundedAsync(
                            _gameHost.SubmitActionAsync(
                                Snapshot(actionRequest),
                                deadline.Token),
                            deadline.Token)
                        .ConfigureAwait(false);
                    var receipt =
                        ActionReceiptIngressValidator.ValidateAndClone(
                            actionRequest,
                            hostReceipt);

                    await AppendAsync(
                        run,
                        eventSequence,
                        turnId,
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(receipt),
                        deadline.Token).ConfigureAwait(false);

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
                        deadline.Token).ConfigureAwait(false);

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
                    deadline.Token).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
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

        await _sessionStore
            .AppendAsync(runtimeEvent, cancellationToken)
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
            await AppendAsync(
                    run,
                    eventSequence,
                    turnId,
                    kind,
                    payload,
                    CancellationToken.None)
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

    private IDisposable Admit(string runId)
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

    private static HeadlessRunRequest Snapshot(HeadlessRunRequest request)
    {
        var run = request.Run
            ?? throw new ArgumentException(
                "A headless run request requires a run.",
                nameof(request));
        var observations = request.Observations
            ?? throw new ArgumentException(
                "A headless run request requires an observation collection.",
                nameof(request));
        var tools = request.Tools
            ?? throw new ArgumentException(
                "A headless run request requires a tool collection.",
                nameof(request));

        return new HeadlessRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(ProtocolJson.Serialize(run)),
            Observations = observations.Select(Snapshot).ToArray(),
            Tools = tools.Select(Snapshot).ToArray()
        };
    }

    private static ObservationEnvelope Snapshot(
        ObservationEnvelope observation)
    {
        if (observation is null)
        {
            throw new ArgumentException(
                "Observation collections cannot contain null entries.",
                nameof(observation));
        }

        return ProtocolJson.DeserializeObservationEnvelope(
            ProtocolJson.Serialize(observation));
    }

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
        CancellationToken cancellationToken)
    {
        var task = operation.AsTask();
        if (!task.IsCompleted)
        {
            var cancelled = Task.Delay(
                Timeout.Infinite,
                cancellationToken);
            var completed = await Task.WhenAny(task, cancelled)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                _ = ObserveDetachedAsync(task);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveDetachedAsync(task);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
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

    private sealed class RunEventSequence
    {
        private long _value;

        public long Next()
        {
            return Interlocked.Increment(ref _value) - 1;
        }
    }

    private sealed class ActiveRunLease : IDisposable
    {
        private readonly HeadlessAgentRuntime _owner;
        private string? _runId;

        public ActiveRunLease(HeadlessAgentRuntime owner, string runId)
        {
            _owner = owner;
            _runId = runId;
        }

        public void Dispose()
        {
            var runId = Interlocked.Exchange(ref _runId, null);
            if (runId is not null)
            {
                _owner.ReleaseAdmission(runId);
            }
        }
    }

    private sealed class HeadlessRunDeadline : IDisposable
    {
        private readonly CancellationToken _externalToken;
        private readonly CancellationTokenSource _expiration = new();
        private readonly CancellationTokenSource _stopTimer = new();
        private readonly CancellationTokenSource _linked;
        private readonly Task _timerTask;
        private int _expired;

        public HeadlessRunDeadline(
            long maxDurationMs,
            long elapsedDurationMs,
            CancellationToken externalToken)
        {
            _externalToken = externalToken;
            var elapsed = Math.Max(0, elapsedDurationMs);
            var remaining = maxDurationMs <= elapsed
                ? 0
                : maxDurationMs - elapsed;
            if (remaining == 0)
            {
                _expired = 1;
                _expiration.Cancel();
                _timerTask = Task.CompletedTask;
            }
            else
            {
                _timerTask = ExpireAsync(remaining);
            }

            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                externalToken,
                _expiration.Token);
        }

        public CancellationToken Token => _linked.Token;

        public bool IsExpired =>
            Volatile.Read(ref _expired) != 0
            && !_externalToken.IsCancellationRequested;

        public void Dispose()
        {
            _stopTimer.Cancel();
            try
            {
                _timerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _linked.Dispose();
            _stopTimer.Dispose();
            _expiration.Dispose();
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

            Interlocked.Exchange(ref _expired, 1);
            try
            {
                _expiration.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation is already visible even if a consumer's
                // callback rejects notification.
            }
        }
    }
}
