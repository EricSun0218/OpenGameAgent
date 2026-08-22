using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Kernel;

public static class AgentLoop
{
    private const int ModelStreamCleanupTimeoutMilliseconds = 5_000;

    public static Task<AgentRunResult> RunAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopOptions options,
        Func<AgentEvent, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(prompts, context, options, emit, isContinuation: false, skipInitialSteeringPoll: false, cancellationToken);
    }

    public static Task<AgentRunResult> ContinueAsync(
        AgentContext context,
        AgentLoopOptions options,
        Func<AgentEvent, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (context.Messages.Count == 0)
        {
            throw new InvalidOperationException("Cannot continue because the transcript is empty.");
        }

        if (context.Messages[context.Messages.Count - 1].Role == AgentRole.Assistant)
        {
            throw new InvalidOperationException("Cannot continue from an assistant message without queued input.");
        }

        return RunCoreAsync(Array.Empty<AgentMessage>(), context, options, emit, isContinuation: true, skipInitialSteeringPoll: false, cancellationToken);
    }

    internal static Task<AgentRunResult> RunQueuedAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopOptions options,
        Func<AgentEvent, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        return RunCoreAsync(prompts, context, options, emit, isContinuation: false, skipInitialSteeringPoll: true, cancellationToken);
    }

    private static async Task<AgentRunResult> RunCoreAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext initialContext,
        AgentLoopOptions options,
        Func<AgentEvent, CancellationToken, ValueTask> emit,
        bool isContinuation,
        bool skipInitialSteeringPoll,
        CancellationToken cancellationToken)
    {
        if (prompts is null)
        {
            throw new ArgumentNullException(nameof(prompts));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (emit is null)
        {
            throw new ArgumentNullException(nameof(emit));
        }

        var limits = options.Limits?.Copy()
            ?? throw new ArgumentException("Agent limits are required.", nameof(options));
        if (!Enum.IsDefined(typeof(ToolExecutionMode), options.ToolExecution))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The tool execution mode is invalid.");
        }

        AgentValidator.ValidateOptions(
            options.Model,
            options.SessionId,
            options.Parameters,
            limits,
            options.Clock,
            options.RunIdFactory);
        AgentValidator.ValidateContext(initialContext, limits);
        AgentValidator.ValidateMessages(prompts, limits);
        if (initialContext.Messages.Count + prompts.Count > limits.MaxMessages)
        {
            throw new AgentLimitException(nameof(limits.MaxMessages), "The prompt would exceed the maximum transcript size.");
        }

        var runId = options.RunIdFactory();
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > limits.MaxSessionIdCharacters)
        {
            throw new InvalidOperationException("RunIdFactory returned an invalid run ID.");
        }

        var current = new MutableLoopContext(initialContext, prompts);
        var newMessages = new List<AgentMessage>(prompts);
        var turns = 0;
        var toolCallCount = 0;
        var totalTokens = 0L;
        var provider = options.Provider;
        var model = options.Model;
        var parameters = options.Parameters.Copy();
        var toolRepeatTracker = new ExactToolRepeatTracker();
        var ended = false;
        var emitGate = new SemaphoreSlim(1, 1);

        async ValueTask EmitCoreAsync(AgentEvent value, CancellationToken callbackToken)
        {
            await emitGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await emit(value, callbackToken).ConfigureAwait(false);
            }
            finally
            {
                emitGate.Release();
            }
        }

        ValueTask EmitAsync(AgentEvent value) => EmitCoreAsync(value, cancellationToken);

        ValueTask EmitTerminalAsync(AgentEvent value) => EmitCoreAsync(value, CancellationToken.None);

        async Task<AgentRunResult> FinishAsync(AgentRunStatus status, string? error = null)
        {
            options.NotifyRunFinishing?.Invoke();
            if (ended)
            {
                return new AgentRunResult(runId, status, newMessages, turns, toolCallCount, error);
            }

            ended = true;
            if (status is not AgentRunStatus.Completed and not AgentRunStatus.Stopped)
            {
                await EmitTerminalAsync(new AgentEvent(
                    AgentEventKind.RunFaulted,
                    runId,
                    turns,
                    error: error ?? status.ToString(),
                    status: status)).ConfigureAwait(false);
            }

            await EmitTerminalAsync(new AgentEvent(
                AgentEventKind.RunEnded,
                runId,
                turns,
                error: error,
                status: status,
                messages: newMessages)).ConfigureAwait(false);
            return new AgentRunResult(runId, status, newMessages, turns, toolCallCount, error);
        }

        await EmitAsync(new AgentEvent(AgentEventKind.RunStarted, runId)).ConfigureAwait(false);

        try
        {
            IReadOnlyList<AgentMessage> pendingMessages = Array.Empty<AgentMessage>();
            var firstTurn = true;

            while (true)
            {
                var continueModelLoop = true;
                while (continueModelLoop || pendingMessages.Count > 0)
                {
                    if (turns >= limits.MaxTurns)
                    {
                        return await FinishAsync(
                            AgentRunStatus.LimitExceeded,
                            $"The run reached the maximum of {limits.MaxTurns} model turns.").ConfigureAwait(false);
                    }

                    turns++;
                    await EmitAsync(new AgentEvent(AgentEventKind.TurnStarted, runId, turns)).ConfigureAwait(false);

                    if (firstTurn)
                    {
                        firstTurn = false;
                        if (!isContinuation)
                        {
                            foreach (var prompt in prompts)
                            {
                                await EmitMessageAsync(prompt, runId, turns, EmitAsync).ConfigureAwait(false);
                            }
                        }

                        if (!skipInitialSteeringPoll)
                        {
                            pendingMessages = await DrainAsync(
                                options.GetSteeringMessagesAsync,
                                cancellationToken).ConfigureAwait(false);
                            ValidateQueuedMessages(pendingMessages, limits);
                        }
                    }

                    if (pendingMessages.Count > 0)
                    {
                        toolRepeatTracker.Reset();
                        EnsureMessageCapacity(current.Messages.Count, pendingMessages.Count, limits);
                        foreach (var pending in pendingMessages)
                        {
                            AgentValidator.ValidateMessage(pending, limits);
                            current.Messages.Add(pending);
                            newMessages.Add(pending);
                            await EmitMessageAsync(pending, runId, turns, EmitAsync).ConfigureAwait(false);
                        }

                        pendingMessages = Array.Empty<AgentMessage>();
                    }

                    var responseMessageReserve = current.Tools.Count == 0
                        ? 1
                        : checked(2 + limits.MaxToolCallsPerTurn);
                    EnsureMessageCapacity(current.Messages.Count, responseMessageReserve, limits);

                    var streamed = await StreamAssistantAsync(
                        runId,
                        turns,
                        current,
                        provider,
                        model,
                        parameters,
                        options,
                        limits,
                        EmitAsync,
                        cancellationToken).ConfigureAwait(false);
                    var response = streamed.Response;
                    var assistantMessage = ToAssistantMessage(response, options.Clock(), streamed.Model);
                    current.Messages.Add(assistantMessage);
                    newMessages.Add(assistantMessage);
                    totalTokens = checked(totalTokens + response.Usage.TotalTokens);
                    var calls = response.Content.OfType<ToolCallContent>().ToArray();
                    toolCallCount = checked(toolCallCount + calls.Length);

                    if (response.StopReason is ModelStopReason.Error or ModelStopReason.Aborted)
                    {
                        var failedCalls = await FailUnexecutedToolCallsAsync(
                            calls,
                            runId,
                            turns,
                            options.Clock,
                            EmitAsync,
                            limits,
                            response.StopReason == ModelStopReason.Aborted
                                ? "the model request was aborted"
                                : "the model request failed").ConfigureAwait(false);
                        foreach (var toolMessage in failedCalls.Messages)
                        {
                            current.Messages.Add(toolMessage);
                            newMessages.Add(toolMessage);
                        }

                        await EmitAsync(new AgentEvent(
                            AgentEventKind.TurnEnded,
                            runId,
                            turns,
                            assistantMessage,
                            error: response.ErrorMessage,
                            messages: failedCalls.Messages)).ConfigureAwait(false);
                        var status = response.StopReason == ModelStopReason.Aborted
                            ? AgentRunStatus.Aborted
                            : AgentRunStatus.ProviderError;
                        return await FinishAsync(status, response.ErrorMessage).ConfigureAwait(false);
                    }

                    ToolBatchOutcome batch;
                    if (totalTokens > limits.MaxTotalTokens)
                    {
                        batch = await FailUnexecutedToolCallsAsync(
                            calls,
                            runId,
                            turns,
                            options.Clock,
                            EmitAsync,
                            limits,
                            "the run exceeded its total token budget").ConfigureAwait(false);
                    }
                    else if (calls.Length == 0)
                    {
                        batch = ToolBatchOutcome.Empty;
                    }
                    else if (response.StopReason == ModelStopReason.Length)
                    {
                        batch = await FailTruncatedToolCallsAsync(calls, runId, turns, options.Clock, EmitAsync, limits).ConfigureAwait(false);
                    }
                    else
                    {
                        batch = await ExecuteToolCallsAsync(
                            assistantMessage,
                            calls,
                            runId,
                            turns,
                            current,
                            options,
                            limits,
                            toolRepeatTracker,
                            EmitAsync,
                            cancellationToken).ConfigureAwait(false);
                    }

                    EnsureMessageCapacity(current.Messages.Count, batch.Messages.Count, limits);
                    foreach (var toolMessage in batch.Messages)
                    {
                        current.Messages.Add(toolMessage);
                        newMessages.Add(toolMessage);
                        if (toolMessage.Usage is not null)
                        {
                            totalTokens = checked(totalTokens + toolMessage.Usage.TotalTokens);
                        }
                    }

                    await EmitAsync(new AgentEvent(
                        AgentEventKind.TurnEnded,
                        runId,
                        turns,
                        assistantMessage,
                        messages: batch.Messages)).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (totalTokens > limits.MaxTotalTokens)
                    {
                        return await FinishAsync(
                            AgentRunStatus.LimitExceeded,
                            $"The run exceeded the maximum of {limits.MaxTotalTokens} total tokens.").ConfigureAwait(false);
                    }

                    var afterTurn = new AfterTurnContext(
                        runId,
                        turns,
                        assistantMessage,
                        batch.Messages,
                        current.Snapshot(),
                        newMessages.ToArray());

                    if (options.Hooks.PrepareNextTurnAsync is not null)
                    {
                        var update = await options.Hooks.PrepareNextTurnAsync(afterTurn, cancellationToken).ConfigureAwait(false);
                        if (update?.Provider is not null && update.Model is null)
                        {
                            throw new InvalidOperationException("A next-turn provider replacement must include its model name.");
                        }

                        if (update?.Context is not null)
                        {
                            AgentValidator.ValidateContext(update.Context, limits);
                            current = new MutableLoopContext(update.Context, Array.Empty<AgentMessage>());
                        }

                        if (update?.Provider is not null)
                        {
                            provider = update.Provider;
                        }

                        if (update?.Model is not null)
                        {
                            AgentValidator.ValidateOptions(
                                update.Model,
                                options.SessionId,
                                update.Parameters ?? parameters,
                                limits,
                                options.Clock,
                                options.RunIdFactory);
                            model = update.Model;
                        }

                        if (update?.Parameters is not null)
                        {
                            AgentValidator.ValidateOptions(
                                model,
                                options.SessionId,
                                update.Parameters,
                                limits,
                                options.Clock,
                                options.RunIdFactory);
                            parameters = update.Parameters.Copy();
                        }
                    }

                    afterTurn = new AfterTurnContext(
                        runId,
                        turns,
                        assistantMessage,
                        batch.Messages,
                        current.Snapshot(),
                        newMessages.ToArray());

                    if (options.Hooks.ShouldStopAfterTurnAsync is not null
                        && await options.Hooks.ShouldStopAfterTurnAsync(afterTurn, cancellationToken).ConfigureAwait(false))
                    {
                        return await FinishAsync(AgentRunStatus.Stopped).ConfigureAwait(false);
                    }

                    pendingMessages = await DrainAsync(options.GetSteeringMessagesAsync, cancellationToken).ConfigureAwait(false);
                    ValidateQueuedMessages(pendingMessages, limits);
                    continueModelLoop = calls.Length > 0 && !batch.Terminate;
                }

                var followUps = options.FinalizePendingMessages is null
                    ? await DrainAsync(options.GetFollowUpMessagesAsync, cancellationToken).ConfigureAwait(false)
                    : options.FinalizePendingMessages();
                ValidateQueuedMessages(followUps, limits);
                if (followUps.Count == 0)
                {
                    return await FinishAsync(AgentRunStatus.Completed).ConfigureAwait(false);
                }

                pendingMessages = followUps;
                continueModelLoop = false;
            }
        }
        catch (AgentLimitException exception)
        {
            return await FinishAsync(
                AgentRunStatus.LimitExceeded,
                BoundError(exception.Message, limits)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(AgentRunStatus.Aborted, "The agent run was aborted.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await FinishAsync(
                AgentRunStatus.KernelError,
                BoundError(exception.Message, limits)).ConfigureAwait(false);
        }
    }

    private static async Task<AssistantStreamResult> StreamAssistantAsync(
        string runId,
        int turn,
        MutableLoopContext context,
        IModelProvider provider,
        string model,
        ModelParameters parameters,
        AgentLoopOptions options,
        AgentLimits limits,
        Func<AgentEvent, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentMessage> outboundMessages = context.Messages.ToArray();
        if (options.Hooks.TransformContextAsync is not null)
        {
            outboundMessages = await options.Hooks.TransformContextAsync(outboundMessages, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("TransformContextAsync returned null.");
            AgentValidator.ValidateMessages(outboundMessages, limits);
            if (outboundMessages.Count > limits.MaxMessages)
            {
                throw new AgentLimitException(nameof(limits.MaxMessages), "The transformed provider context contains too many messages.");
            }
        }

        var request = new ModelRequest(
            model,
            context.SystemPrompt,
            outboundMessages,
            context.Tools.Select(tool => tool.Definition).ToArray(),
            parameters,
            options.SessionId,
            runId,
            turn);
        if (options.Hooks.BeforeModelRequestAsync is not null)
        {
            request = await options.Hooks.BeforeModelRequestAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("BeforeModelRequestAsync returned null.");
        }

        AgentValidator.ValidateRequest(request, limits, options.Clock, options.RunIdFactory);
        if (!string.Equals(request.RunId, runId, StringComparison.Ordinal) || request.Turn != turn)
        {
            throw new InvalidOperationException("BeforeModelRequestAsync cannot change the active run ID or turn number.");
        }

        await emit(new AgentEvent(
            AgentEventKind.ModelRequestStarted,
            runId,
            turn,
            modelRequest: request)).ConfigureAwait(false);

        var started = false;
        ModelResponse? lastPartial = null;
        AssistantStreamResult? completedStream = null;
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadlineCancellation = new CancellationTokenSource();
        using var callerWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(limits.ModelTimeoutMilliseconds, deadlineCancellation.Token);
        var callerCancellation = Task.Delay(Timeout.Infinite, callerWaitCancellation.Token);
        IAsyncEnumerator<ModelStreamEvent>? enumerator = null;
        var disposeEnumerator = true;
        try
        {
            enumerator = (provider.StreamAsync(request, requestCancellation.Token)
                    ?? throw new InvalidOperationException("The model provider returned a null stream."))
                .GetAsyncEnumerator(requestCancellation.Token)
                ?? throw new InvalidOperationException("The model provider returned a null stream enumerator.");
            while (true)
            {
                var pendingMove = enumerator.MoveNextAsync().AsTask();
                var winner = await Task.WhenAny(pendingMove, deadline, callerCancellation).ConfigureAwait(false);
                if (!ReferenceEquals(winner, pendingMove) && !pendingMove.IsCompleted)
                {
                    disposeEnumerator = false;
                    TryCancel(requestCancellation);
                    _ = ObservePendingStreamAsync(enumerator, pendingMove);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    throw new TimeoutException(
                        $"The model request exceeded {limits.ModelTimeoutMilliseconds} ms.");
                }

                if (!await pendingMove.ConfigureAwait(false))
                {
                    break;
                }

                var modelEvent = enumerator.Current;
                if (modelEvent is null)
                {
                    throw new InvalidOperationException("The model provider emitted a null stream event.");
                }

                if (modelEvent.Partial is not null)
                {
                    if (modelEvent.Partial.StopReason != ModelStopReason.Pending)
                    {
                        throw new InvalidOperationException("A partial model stream response must have a pending stop reason.");
                    }

                    AgentValidator.ValidateResponse(modelEvent.Partial, limits);
                    lastPartial = modelEvent.Partial;
                    var partialMessage = ToAssistantMessage(lastPartial, options.Clock(), request.Model);
                    if (modelEvent.Kind == ModelStreamEventKind.Started)
                    {
                        if (started)
                        {
                            throw new InvalidOperationException("The model provider emitted more than one stream start event.");
                        }

                        started = true;
                        await emit(new AgentEvent(AgentEventKind.MessageStarted, runId, turn, partialMessage, modelEvent)).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!started)
                        {
                            throw new InvalidOperationException("The model provider emitted a stream update before its start event.");
                        }

                        await emit(new AgentEvent(AgentEventKind.MessageUpdated, runId, turn, partialMessage, modelEvent)).ConfigureAwait(false);
                    }
                }

                if (!modelEvent.IsTerminal)
                {
                    continue;
                }

                var response = modelEvent.Response
                    ?? throw new InvalidOperationException("A terminal model stream event requires a response.");
                if (response.StopReason == ModelStopReason.Pending)
                {
                    throw new InvalidOperationException("A terminal model stream response cannot have a pending stop reason.");
                }

                AgentValidator.ValidateResponse(response, limits);
                EnsureMessageCapacity(
                    context.Messages.Count,
                    1 + response.Content.Count(part => part is ToolCallContent),
                    limits);
                var finalMessage = ToAssistantMessage(response, options.Clock(), request.Model);
                if (!started)
                {
                    started = true;
                    await emit(new AgentEvent(AgentEventKind.MessageStarted, runId, turn, finalMessage, modelEvent)).ConfigureAwait(false);
                }

                await emit(new AgentEvent(AgentEventKind.MessageEnded, runId, turn, finalMessage, modelEvent)).ConfigureAwait(false);
                completedStream = new AssistantStreamResult(response, request.Model);
                return completedStream;
            }

            var syntheticResponse = await EmitSyntheticModelFailureAsync(
                "The model stream ended without a terminal response.",
                ModelStopReason.Error,
                lastPartial,
                started,
                runId,
                turn,
                request.Model,
                options.Clock,
                limits,
                emit).ConfigureAwait(false);
            return new AssistantStreamResult(syntheticResponse, request.Model);
        }
        catch (Exception) when (completedStream is not null)
        {
            return completedStream;
        }
        catch (AgentLimitException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var syntheticResponse = await EmitSyntheticModelFailureAsync(
                "The model request was aborted.",
                ModelStopReason.Aborted,
                lastPartial,
                started,
                runId,
                turn,
                request.Model,
                options.Clock,
                limits,
                emit).ConfigureAwait(false);
            return new AssistantStreamResult(syntheticResponse, request.Model);
        }
        catch (Exception exception)
        {
            var syntheticResponse = await EmitSyntheticModelFailureAsync(
                exception.Message,
                ModelStopReason.Error,
                lastPartial,
                started,
                runId,
                turn,
                request.Model,
                options.Clock,
                limits,
                emit,
                (exception as ModelProviderException)?.Diagnostics).ConfigureAwait(false);
            return new AssistantStreamResult(syntheticResponse, request.Model);
        }
        finally
        {
            TryCancel(deadlineCancellation);
            TryCancel(callerWaitCancellation);
            if (disposeEnumerator && enumerator is not null)
            {
                TryCancel(requestCancellation);
                var cleanup = enumerator.DisposeAsync().AsTask();
                if (cleanup.IsCompleted)
                {
                    try
                    {
                        await cleanup.ConfigureAwait(false);
                    }
                    catch when (completedStream is not null || cancellationToken.IsCancellationRequested)
                    {
                        // Stream cleanup cannot replace a completed or cancelled request outcome.
                    }
                }
                else
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await AwaitCancelledStreamCleanupAsync(cleanup).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = IgnoreFailureAsync(cleanup);
                    }
                }
            }
        }
    }

    private static async Task AwaitCancelledStreamCleanupAsync(Task cleanup)
    {
        using var timeout = new CancellationTokenSource();
        var deadline = Task.Delay(ModelStreamCleanupTimeoutMilliseconds, timeout.Token);
        var completed = await Task.WhenAny(cleanup, deadline).ConfigureAwait(false);
        if (ReferenceEquals(completed, cleanup))
        {
            timeout.Cancel();
            try
            {
                await cleanup.ConfigureAwait(false);
            }
            catch
            {
                // Provider cleanup cannot replace an already cancelled request outcome.
            }

            return;
        }

        _ = IgnoreFailureAsync(cleanup);
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // A provider cancellation callback cannot replace the request outcome.
        }
    }

    private static async Task ObservePendingStreamAsync(
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        Task<bool> pendingMove)
    {
        try
        {
            _ = await pendingMove.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class AssistantStreamResult
    {
        public AssistantStreamResult(ModelResponse response, string model)
        {
            Response = response;
            Model = model;
        }

        public ModelResponse Response { get; }

        public string Model { get; }
    }

    private static async Task<ModelResponse> EmitSyntheticModelFailureAsync(
        string error,
        ModelStopReason reason,
        ModelResponse? partial,
        bool started,
        string runId,
        int turn,
        string model,
        Func<DateTimeOffset> clock,
        AgentLimits limits,
        Func<AgentEvent, ValueTask> emit,
        IReadOnlyList<ModelDiagnostic>? failureDiagnostics = null)
    {
        var safeError = string.IsNullOrWhiteSpace(error)
            ? reason == ModelStopReason.Aborted ? "The model request was aborted." : "The model request failed."
            : error;
        if (safeError.Length > limits.MaxTextCharactersPerPart)
        {
            safeError = safeError.Substring(0, limits.MaxTextCharactersPerPart);
        }

        var safeContent = partial?.Content.Where(part => part is not ToolCallContent).ToArray()
            ?? Array.Empty<AgentContent>();
        var diagnostics = partial?.Diagnostics.ToList() ?? new List<ModelDiagnostic>();
        if (failureDiagnostics is not null)
        {
            diagnostics.AddRange(failureDiagnostics);
        }

        var response = new ModelResponse(
            safeContent,
            reason,
            partial?.Usage,
            safeError,
            partial?.Provider,
            partial?.Api,
            partial?.ResponseModel,
            partial?.ResponseId,
            partial?.RawStopReason,
            partial?.EndTurn,
            diagnostics);
        AgentValidator.ValidateResponse(response, limits);
        var terminal = ModelStreamEvent.Terminal(response);
        var message = ToAssistantMessage(response, clock(), model);
        if (!started)
        {
            await emit(new AgentEvent(AgentEventKind.MessageStarted, runId, turn, message, terminal)).ConfigureAwait(false);
        }

        await emit(new AgentEvent(AgentEventKind.MessageEnded, runId, turn, message, terminal)).ConfigureAwait(false);
        return response;
    }

    private static async Task<ToolBatchOutcome> FailTruncatedToolCallsAsync(
        IReadOnlyList<ToolCallContent> calls,
        string runId,
        int turn,
        Func<DateTimeOffset> clock,
        Func<AgentEvent, ValueTask> emit,
        AgentLimits limits)
        => await FailUnexecutedToolCallsAsync(
            calls,
            runId,
            turn,
            clock,
            emit,
            limits,
            "the model response reached its output limit and the arguments may be incomplete").ConfigureAwait(false);

    private static async Task<ToolBatchOutcome> FailUnexecutedToolCallsAsync(
        IReadOnlyList<ToolCallContent> calls,
        string runId,
        int turn,
        Func<DateTimeOffset> clock,
        Func<AgentEvent, ValueTask> emit,
        AgentLimits limits,
        string reason)
    {
        var messages = new List<AgentMessage>(calls.Count);
        foreach (var call in calls)
        {
            await emit(new AgentEvent(AgentEventKind.ToolStarted, runId, turn, toolCall: call)).ConfigureAwait(false);
            var result = CreateToolError($"Tool '{call.Name}' was not executed because {reason}.", limits);
            await emit(new AgentEvent(AgentEventKind.ToolEnded, runId, turn, toolCall: call, toolResult: result)).ConfigureAwait(false);
            var message = AgentMessage.ToolResult(call, result, clock());
            messages.Add(message);
            await EmitMessageAsync(message, runId, turn, emit).ConfigureAwait(false);
        }

        return new ToolBatchOutcome(messages, terminate: false);
    }

    private static async Task<ToolBatchOutcome> ExecuteToolCallsAsync(
        AgentMessage assistantMessage,
        IReadOnlyList<ToolCallContent> calls,
        string runId,
        int turn,
        MutableLoopContext current,
        AgentLoopOptions options,
        AgentLimits limits,
        ExactToolRepeatTracker repeatTracker,
        Func<AgentEvent, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedToolCall>(calls.Count);
        var outcomes = new ToolCallOutcome?[calls.Count];
        var preparationCanceled = false;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            await emit(new AgentEvent(AgentEventKind.ToolStarted, runId, turn, toolCall: call)).ConfigureAwait(false);
            ToolPreparation preparation;
            if (preparationCanceled || cancellationToken.IsCancellationRequested)
            {
                preparationCanceled = true;
                preparation = ToolPreparation.Failed(CreateToolError(
                    "Tool execution was aborted before dispatch.",
                    limits,
                    failureCategory: ToolFailureCategory.Cancelled));
            }
            else
            {
                try
                {
                    preparation = await PrepareToolCallAsync(
                        assistantMessage,
                        call,
                        runId,
                        turn,
                        current,
                        options,
                        limits,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    preparationCanceled = true;
                    preparation = ToolPreparation.Failed(CreateToolError(
                        "Tool execution was aborted before dispatch.",
                        limits,
                        failureCategory: ToolFailureCategory.Cancelled));
                }
            }

            if (preparation.Error is not null)
            {
                outcomes[index] = new ToolCallOutcome(index, call, preparation.Error);
                await emit(new AgentEvent(
                    AgentEventKind.ToolEnded,
                    runId,
                    turn,
                    toolCall: call,
                    toolResult: preparation.Error)).ConfigureAwait(false);
            }
            else
            {
                prepared.Add(new PreparedToolCall(index, preparation.Call!, preparation.Tool!, preparation.Arguments, preparation.ConflictKey));
            }
        }

        preparationCanceled |= cancellationToken.IsCancellationRequested;
        if (preparationCanceled)
        {
            foreach (var item in prepared)
            {
                var result = CreateToolError(
                    "Tool execution was aborted before dispatch.",
                    limits,
                    failureCategory: ToolFailureCategory.Cancelled);
                outcomes[item.Index] = new ToolCallOutcome(item.Index, item.Call, result);
                await emit(new AgentEvent(
                    AgentEventKind.ToolEnded,
                    runId,
                    turn,
                    toolCall: item.Call,
                    toolResult: result)).ConfigureAwait(false);
            }

            prepared.Clear();
        }

        AgentMessage? repeatAdvisory = null;
        var repeatTerminated = false;
        var repeatBlockedIndexes = new HashSet<int>();
        var preparedByIndex = prepared.ToDictionary(value => value.Index);
        for (var index = 0; index < calls.Count; index++)
        {
            if (!preparedByIndex.TryGetValue(index, out var item))
            {
                repeatTracker.Reset();
                continue;
            }

            var repeat = repeatTracker.Observe(item, limits);
            if (repeat is null)
            {
                continue;
            }

            await emit(new AgentEvent(
                AgentEventKind.ToolRepeatDetected,
                runId,
                turn,
                toolCall: item.Call,
                toolRepeat: new ToolRepeatDetection(repeat.Count, repeat.Action))).ConfigureAwait(false);
            if (repeat.Action == ToolRepeatPolicyAction.Advisory)
            {
                repeatAdvisory ??= CreateToolRepeatAdvisory(item.Call.Name, repeat.Count, options.Clock());
                continue;
            }

            repeatTerminated = true;
            repeatBlockedIndexes.Add(item.Index);
            var result = CreateToolError(
                $"Tool '{item.Call.Name}' was not dispatched because the same prepared call repeated {repeat.Count} consecutive times.",
                limits,
                terminate: true,
                failureCategory: ToolFailureCategory.Conflict);
            outcomes[item.Index] = new ToolCallOutcome(item.Index, item.Call, result);
            await emit(new AgentEvent(
                AgentEventKind.ToolEnded,
                runId,
                turn,
                toolCall: item.Call,
                toolResult: result)).ConfigureAwait(false);
        }

        if (repeatBlockedIndexes.Count > 0)
        {
            prepared.RemoveAll(item => repeatBlockedIndexes.Contains(item.Index));
        }

        using var globalGate = new SemaphoreSlim(limits.MaxConcurrentTools, limits.MaxConcurrentTools);
        var conflictGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        var uncertainConflicts = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var hasUnscopedUncertainWrite = 0;

        bool IsBlockedByUncertainWrite(PreparedToolCall item)
        {
            if (item.Tool.Risk == ToolRisk.ReadOnly)
            {
                return false;
            }

            if (Volatile.Read(ref hasUnscopedUncertainWrite) != 0)
            {
                return true;
            }

            return string.IsNullOrEmpty(item.ConflictKey)
                ? !uncertainConflicts.IsEmpty
                : uncertainConflicts.ContainsKey(item.ConflictKey!);
        }

        void RecordUncertainWrite(PreparedToolCall item, ToolCallOutcome outcome)
        {
            if (!outcome.UncertainSideEffect || item.Tool.Risk == ToolRisk.ReadOnly)
            {
                return;
            }

            if (string.IsNullOrEmpty(item.ConflictKey))
            {
                Interlocked.Exchange(ref hasUnscopedUncertainWrite, 1);
            }
            else
            {
                uncertainConflicts.TryAdd(item.ConflictKey!, 0);
            }
        }

        ToolCallOutcome BlockUncertain(PreparedToolCall item)
        {
            var sameKey = !string.IsNullOrEmpty(item.ConflictKey)
                && uncertainConflicts.ContainsKey(item.ConflictKey!);
            return new ToolCallOutcome(
                item.Index,
                item.Call,
                CreateToolError(
                    sameKey
                        ? "The tool was not executed because an earlier write with the same conflict key has an uncertain outcome."
                        : "The tool was not executed because an earlier potentially conflicting write has an uncertain outcome.",
                    limits,
                    failureCategory: ToolFailureCategory.Conflict));
        }

        async Task<ToolCallOutcome> ExecuteBoundedAsync(PreparedToolCall item)
        {
            var globalGateAcquired = false;
            SemaphoreSlim? conflictGate = null;
            var conflictGateAcquired = false;
            try
            {
                await globalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                globalGateAcquired = true;
                if (!string.IsNullOrEmpty(item.ConflictKey))
                {
                    conflictGate = conflictGates.GetOrAdd(item.ConflictKey!, _ => new SemaphoreSlim(1, 1));
                    await conflictGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    conflictGateAcquired = true;
                }

                if (IsBlockedByUncertainWrite(item))
                {
                    return BlockUncertain(item);
                }

                var outcome = await ExecutePreparedToolCallAsync(
                    item,
                    assistantMessage,
                    runId,
                    turn,
                    current.Snapshot(),
                    options,
                    limits,
                    emit,
                    cancellationToken).ConfigureAwait(false);
                RecordUncertainWrite(item, outcome);
                return outcome;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ToolCallOutcome(
                    item.Index,
                    item.Call,
                    CreateToolError(
                        "Tool execution was aborted.",
                        limits,
                        failureCategory: ToolFailureCategory.Cancelled));
            }
            finally
            {
                if (conflictGateAcquired)
                {
                    conflictGate!.Release();
                }

                if (globalGateAcquired)
                {
                    globalGate.Release();
                }
            }
        }

        var epochs = CreateToolExecutionEpochs(prepared, options.ToolExecution);
        for (var epochIndex = 0; epochIndex < epochs.Count; epochIndex++)
        {
            var epoch = epochs[epochIndex];
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var remaining in epochs.Skip(epochIndex).SelectMany(value => value.Calls))
                {
                    var cancelled = new ToolCallOutcome(
                        remaining.Index,
                        remaining.Call,
                        CreateToolError(
                            "Tool execution was aborted before dispatch.",
                            limits,
                            failureCategory: ToolFailureCategory.Cancelled));
                    outcomes[cancelled.Index] = cancelled;
                    await emit(new AgentEvent(
                        AgentEventKind.ToolEnded,
                        runId,
                        turn,
                        toolCall: cancelled.Call,
                        toolResult: cancelled.Result)).ConfigureAwait(false);
                }

                break;
            }

            if (epoch.Parallel)
            {
                var pending = epoch.Calls.Select(ExecuteBoundedAsync).ToList();
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(completed);
                    var outcome = await completed.ConfigureAwait(false);
                    outcomes[outcome.Index] = outcome;
                    await emit(new AgentEvent(
                        AgentEventKind.ToolEnded,
                        runId,
                        turn,
                        toolCall: outcome.Call,
                        toolResult: outcome.Result)).ConfigureAwait(false);
                }

                continue;
            }

            var item = epoch.Calls[0];
            ToolCallOutcome sequentialOutcome;
            if (IsBlockedByUncertainWrite(item))
            {
                sequentialOutcome = BlockUncertain(item);
            }
            else
            {
                sequentialOutcome = await ExecutePreparedToolCallAsync(
                    item,
                    assistantMessage,
                    runId,
                    turn,
                    current.Snapshot(),
                    options,
                    limits,
                    emit,
                    cancellationToken).ConfigureAwait(false);
                RecordUncertainWrite(item, sequentialOutcome);
            }

            outcomes[sequentialOutcome.Index] = sequentialOutcome;
            await emit(new AgentEvent(
                AgentEventKind.ToolEnded,
                runId,
                turn,
                toolCall: sequentialOutcome.Call,
                toolResult: sequentialOutcome.Result)).ConfigureAwait(false);
        }

        foreach (var gate in conflictGates.Values)
        {
            gate.Dispose();
        }

        var ordered = outcomes.Select((outcome, index) =>
            outcome ?? new ToolCallOutcome(index, calls[index], CreateToolError("Tool execution did not produce a result.", limits))).ToArray();
        var messages = new List<AgentMessage>(ordered.Length + (repeatAdvisory is null ? 0 : 1));
        foreach (var outcome in ordered)
        {
            var message = AgentMessage.ToolResult(outcome.Call, outcome.Result, options.Clock());
            messages.Add(message);
            await EmitMessageAsync(message, runId, turn, emit).ConfigureAwait(false);
        }

        if (repeatAdvisory is not null)
        {
            messages.Add(repeatAdvisory);
            await EmitMessageAsync(repeatAdvisory, runId, turn, emit).ConfigureAwait(false);
        }

        return new ToolBatchOutcome(
            messages,
            repeatTerminated || ordered.Length > 0 && ordered.All(outcome => outcome.Result.Terminate));
    }

    private static async Task<ToolPreparation> PrepareToolCallAsync(
        AgentMessage assistantMessage,
        ToolCallContent originalCall,
        string runId,
        int turn,
        MutableLoopContext current,
        AgentLoopOptions options,
        AgentLimits limits,
        CancellationToken cancellationToken)
    {
        var tool = current.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, originalCall.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            return ToolPreparation.Failed(CreateToolError(
                $"Tool '{originalCall.Name}' is not available.",
                limits,
                failureCategory: ToolFailureCategory.InvalidArguments));
        }

        try
        {
            var call = originalCall;
            cancellationToken.ThrowIfCancellationRequested();

            using var initialDocument = JsonDocument.Parse(call.ArgumentsJson);
            var arguments = initialDocument.RootElement.Clone();
            var preparedArgumentsJson = tool.PrepareArguments(arguments);
            if (preparedArgumentsJson is not null)
            {
                call = new ToolCallContent(call.Id, call.Name, preparedArgumentsJson);
                AgentValidator.ValidateToolCall(call, limits);
                using var preparedDocument = JsonDocument.Parse(call.ArgumentsJson);
                arguments = preparedDocument.RootElement.Clone();
            }

            var validationError = tool.Validate(arguments);
            if (validationError is not null)
            {
                return ToolPreparation.Failed(CreateToolError(
                    "Invalid tool arguments: " + validationError,
                    limits,
                    failureCategory: ToolFailureCategory.InvalidArguments));
            }

            if (options.Hooks.BeforeToolCallAsync is not null)
            {
                var decision = await options.Hooks.BeforeToolCallAsync(
                    new BeforeToolCallContext(
                        runId,
                        turn,
                        assistantMessage,
                        call,
                        arguments,
                        current.Snapshot()),
                    cancellationToken).ConfigureAwait(false);
                if (decision?.Blocked == true)
                {
                    return ToolPreparation.Failed(CreateToolError(
                        decision.Reason ?? "Tool execution was blocked.",
                        limits,
                        decision.Terminate,
                        failureCategory: ToolFailureCategory.Authorization));
                }

                if (decision?.ReplacementArgumentsJson is not null)
                {
                    call = new ToolCallContent(call.Id, call.Name, decision.ReplacementArgumentsJson);
                    AgentValidator.ValidateToolCall(call, limits);
                    using var replacementDocument = JsonDocument.Parse(call.ArgumentsJson);
                    arguments = replacementDocument.RootElement.Clone();
                    validationError = tool.Validate(arguments);
                    if (validationError is not null)
                    {
                        return ToolPreparation.Failed(CreateToolError(
                            "Invalid tool arguments: " + validationError,
                            limits,
                            failureCategory: ToolFailureCategory.InvalidArguments));
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            string? conflictKey = null;
            if (tool.ConflictKey is not null)
            {
                conflictKey = tool.ConflictKey(arguments);
                if (conflictKey is { Length: > 1024 })
                {
                    return ToolPreparation.Failed(CreateToolError(
                        "The tool conflict key is too large.",
                        limits,
                        failureCategory: ToolFailureCategory.InvalidArguments));
                }
            }

            if (options.Hooks.AuthorizeToolCallAsync is not null)
            {
                var authorization = await options.Hooks.AuthorizeToolCallAsync(
                    new AuthorizeToolCallContext(
                        runId,
                        turn,
                        assistantMessage,
                        call,
                        arguments,
                        conflictKey,
                        current.Snapshot()),
                    cancellationToken).ConfigureAwait(false);
                if (authorization?.ReplacementArgumentsJson is not null)
                {
                    throw new InvalidOperationException("Final tool authorizers cannot rewrite arguments.");
                }

                if (authorization?.Blocked == true)
                {
                    return ToolPreparation.Failed(CreateToolError(
                        authorization.Reason ?? "Tool execution was not authorized.",
                        limits,
                        authorization.Terminate,
                        failureCategory: ToolFailureCategory.Authorization));
                }
            }

            return ToolPreparation.Ready(call, tool, arguments, conflictKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolPreparation.Failed(CreateToolError(exception.Message, limits));
        }
    }

    private static async Task<ToolCallOutcome> ExecutePreparedToolCallAsync(
        PreparedToolCall prepared,
        AgentMessage assistantMessage,
        string runId,
        int turn,
        AgentContext context,
        AgentLoopOptions options,
        AgentLimits limits,
        Func<AgentEvent, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        var progressCount = 0;
        var progressGate = new object();
        var acceptedProgress = new List<Task>();
        var acceptingProgress = true;
        var dispatched = false;
        var uncertainSideEffect = false;
        var executionContext = new ToolExecutionContext(
            runId,
            turn,
            prepared.Index,
            prepared.Call,
            prepared.ConflictKey,
            async (progress, progressCancellation) =>
            {
                progressCancellation.ThrowIfCancellationRequested();
                AgentValidator.ValidateProgress(progress, limits);
                TaskCompletionSource<object?> completion;
                lock (progressGate)
                {
                    if (!acceptingProgress)
                    {
                        return;
                    }

                    progressCount++;
                    if (progressCount > limits.MaxProgressEventsPerTool)
                    {
                        return;
                    }

                    completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    acceptedProgress.Add(completion.Task);
                }

                _ = CompleteProgressAsync(completion, emit, new AgentEvent(
                        AgentEventKind.ToolProgressed,
                        runId,
                        turn,
                        toolCall: prepared.Call,
                        progress: progress));
                await completion.Task.ConfigureAwait(false);
            });

        ToolResult result;
        Task<ToolResult>? execution = null;
        try
        {
            if (options.Hooks.BeforeToolExecutionAsync is not null)
            {
                var decision = await options.Hooks.BeforeToolExecutionAsync(
                    new BeforeToolExecutionContext(
                        runId,
                        turn,
                        prepared.Index,
                        prepared.Call,
                        prepared.Arguments,
                        prepared.ConflictKey,
                        prepared.Tool.Risk,
                        prepared.Tool.ReplayPolicy,
                        context),
                    cancellationToken).ConfigureAwait(false);
                if (decision?.Kind == ToolExecutionDecisionKind.ReplayResult)
                {
                    result = decision.Result
                        ?? throw new InvalidOperationException("A replay decision requires a tool result.");
                    uncertainSideEffect |= result.OutcomeUncertain;
                    result = await ApplyAfterToolCallAsync(
                        result,
                        prepared,
                        assistantMessage,
                        runId,
                        turn,
                        context,
                        options,
                        limits,
                        uncertainSideEffect,
                        cancellationToken).ConfigureAwait(false);
                    AgentValidator.ValidateToolResult(result, limits);
                    return new ToolCallOutcome(prepared.Index, prepared.Call, result, result.OutcomeUncertain);
                }

                if (decision?.Kind == ToolExecutionDecisionKind.Recover)
                {
                    result = await prepared.Tool.RecoverAsync(
                            prepared.Arguments,
                            executionContext,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? CreateToolError(
                            "The dispatched tool outcome could not be recovered.",
                            limits,
                            uncertain: prepared.Tool.Risk != ToolRisk.ReadOnly,
                            failureCategory: ToolFailureCategory.Conflict);
                    uncertainSideEffect |= result.OutcomeUncertain;
                    result = await ApplyAfterToolCallAsync(
                        result,
                        prepared,
                        assistantMessage,
                        runId,
                        turn,
                        context,
                        options,
                        limits,
                        uncertainSideEffect,
                        cancellationToken).ConfigureAwait(false);
                    AgentValidator.ValidateToolResult(result, limits);
                    return new ToolCallOutcome(prepared.Index, prepared.Call, result, result.OutcomeUncertain);
                }
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timeoutDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dispatched = true;
            execution = prepared.Tool.ExecuteAsync(
                prepared.Arguments,
                executionContext,
                timeoutCancellation.Token).AsTask();
            var timeout = Task.Delay(limits.ToolTimeoutMilliseconds, timeoutDelayCancellation.Token);
            var completed = await Task.WhenAny(execution, timeout).ConfigureAwait(false);
            if (completed == execution)
            {
                TryCancel(timeoutDelayCancellation);
                result = await execution.ConfigureAwait(false) ?? CreateToolError("The tool returned no result.", limits);
                uncertainSideEffect |= result.OutcomeUncertain;
            }
            else
            {
                _ = ObserveToolCompletionAsync(execution);
                cancellationToken.ThrowIfCancellationRequested();
                TryCancel(timeoutCancellation);
                uncertainSideEffect = prepared.Tool.Risk != ToolRisk.ReadOnly;
                result = CreateToolError(
                    prepared.Tool.Risk == ToolRisk.ReadOnly
                        ? $"Tool execution exceeded {limits.ToolTimeoutMilliseconds} ms."
                        : $"Tool execution exceeded {limits.ToolTimeoutMilliseconds} ms. Its side-effect outcome is uncertain and must be reconciled.",
                    limits,
                    uncertain: uncertainSideEffect,
                    failureCategory: ToolFailureCategory.Timeout);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                _ = ObserveToolCompletionAsync(execution);
            }

            uncertainSideEffect = dispatched && prepared.Tool.Risk != ToolRisk.ReadOnly;
            result = CreateToolError(
                prepared.Tool.Risk == ToolRisk.ReadOnly
                    ? "Tool execution was aborted."
                    : "Tool execution was aborted after dispatch. Its side-effect outcome is uncertain and must be reconciled.",
                limits,
                uncertain: uncertainSideEffect,
                failureCategory: ToolFailureCategory.Cancelled);
        }
        catch (Exception exception)
        {
            uncertainSideEffect = dispatched && prepared.Tool.Risk != ToolRisk.ReadOnly;
            result = CreateToolError(exception.Message, limits, uncertain: uncertainSideEffect);
        }
        finally
        {
            lock (progressGate)
            {
                acceptingProgress = false;
            }
        }

        Task[] progressToSettle;
        lock (progressGate)
        {
            progressToSettle = acceptedProgress.ToArray();
        }

        await Task.WhenAll(progressToSettle).ConfigureAwait(false);

        result = await ApplyAfterToolCallAsync(
            result,
            prepared,
            assistantMessage,
            runId,
            turn,
            context,
            options,
            limits,
            uncertainSideEffect,
            cancellationToken).ConfigureAwait(false);
        uncertainSideEffect |= result.OutcomeUncertain;

        try
        {
            AgentValidator.ValidateToolResult(result, limits);
        }
        catch (Exception exception)
        {
            uncertainSideEffect |= prepared.Tool.Risk != ToolRisk.ReadOnly;
            result = CreateToolError(
                "Tool result rejected: " + exception.Message,
                limits,
                uncertain: uncertainSideEffect);
        }

        return new ToolCallOutcome(prepared.Index, prepared.Call, result, uncertainSideEffect);
    }

    private static async Task<ToolResult> ApplyAfterToolCallAsync(
        ToolResult result,
        PreparedToolCall prepared,
        AgentMessage assistantMessage,
        string runId,
        int turn,
        AgentContext context,
        AgentLoopOptions options,
        AgentLimits limits,
        bool uncertainSideEffect,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.Hooks.AfterToolCallAsync is not null)
            {
                return await options.Hooks.AfterToolCallAsync(
                        new AfterToolCallContext(
                            runId,
                            turn,
                            assistantMessage,
                            prepared.Call,
                            prepared.Arguments,
                            result,
                            context),
                        cancellationToken).ConfigureAwait(false)
                    ?? result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return result;
        }
        catch (Exception exception)
        {
            uncertainSideEffect |= prepared.Tool.Risk != ToolRisk.ReadOnly;
            return CreateToolError(exception.Message, limits, uncertain: uncertainSideEffect);
        }

        return result;
    }

    private static async Task CompleteProgressAsync(
        TaskCompletionSource<object?> completion,
        Func<AgentEvent, ValueTask> emit,
        AgentEvent agentEvent)
    {
        try
        {
            await emit(agentEvent).ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task ObserveToolCompletionAsync(Task<ToolResult> execution)
    {
        try
        {
            _ = await execution.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static ToolResult CreateToolError(
        string? message,
        AgentLimits limits,
        bool terminate = false,
        bool uncertain = false,
        ToolFailureCategory failureCategory = ToolFailureCategory.Internal)
    {
        var text = message ?? string.Empty;
        if (text.Length > limits.MaxTextCharactersPerPart)
        {
            text = text.Substring(0, limits.MaxTextCharactersPerPart);
        }

        return new ToolResult(
            new AgentContent[] { new TextContent(text) },
            isError: true,
            detailsJson: uncertain ? "{\"outcome\":\"uncertain\"}" : null,
            terminate: terminate,
            outcomeUncertain: uncertain,
            failureCategory: failureCategory);
    }

    private static string BoundError(string? message, AgentLimits limits)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "The agent run failed." : message;
        return value.Length <= limits.MaxTextCharactersPerPart
            ? value
            : value.Substring(0, limits.MaxTextCharactersPerPart);
    }

    private static IReadOnlyList<ToolExecutionEpoch> CreateToolExecutionEpochs(
        IReadOnlyList<PreparedToolCall> calls,
        ToolExecutionMode mode)
    {
        var result = new List<ToolExecutionEpoch>();
        var parallel = new List<PreparedToolCall>();

        void FlushParallel()
        {
            if (parallel.Count == 0)
            {
                return;
            }

            result.Add(new ToolExecutionEpoch(parallel.ToArray(), parallel: parallel.Count > 1));
            parallel.Clear();
        }

        foreach (var call in calls.OrderBy(value => value.Index))
        {
            var canRunInParallel = mode != ToolExecutionMode.Sequential
                && call.Tool.ExecutionMode != ToolExecutionMode.Sequential
                && (mode == ToolExecutionMode.Parallel
                    || call.Tool.ExecutionMode == ToolExecutionMode.Parallel
                    || call.Tool.Risk == ToolRisk.ReadOnly);
            if (!canRunInParallel)
            {
                FlushParallel();
                result.Add(new ToolExecutionEpoch(new[] { call }, parallel: false));
                continue;
            }

            parallel.Add(call);
        }

        FlushParallel();
        return result;
    }

    private static AgentMessage CreateToolRepeatAdvisory(
        string toolName,
        int count,
        DateTimeOffset timestamp) => new(
            AgentRole.Custom,
            new AgentContent[]
            {
                new TextContent(
                    $"The same prepared call to '{toolName}' has been requested {count} consecutive times. "
                    + "Re-evaluate progress and change the approach or arguments before repeating it again."),
            },
            timestamp,
            customRole: "agent_policy",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oga.policy"] = "exact-tool-repeat",
                ["oga.tool"] = toolName,
                ["oga.repeatCount"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    private static AgentMessage ToAssistantMessage(ModelResponse response, DateTimeOffset timestamp, string? model) =>
        new(
            AgentRole.Assistant,
            response.Content,
            timestamp,
            model: model,
            stopReason: response.StopReason,
            usage: response.Usage,
            errorMessage: response.ErrorMessage,
            provider: response.Provider,
            api: response.Api,
            responseModel: response.ResponseModel,
            responseId: response.ResponseId,
            rawStopReason: response.RawStopReason,
            endTurn: response.EndTurn,
            diagnostics: response.Diagnostics,
            deferred: response.Deferred);

    private static async Task EmitMessageAsync(
        AgentMessage message,
        string runId,
        int turn,
        Func<AgentEvent, ValueTask> emit)
    {
        await emit(new AgentEvent(AgentEventKind.MessageStarted, runId, turn, message)).ConfigureAwait(false);
        await emit(new AgentEvent(AgentEventKind.MessageEnded, runId, turn, message)).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<AgentMessage>> DrainAsync(
        Func<CancellationToken, ValueTask<IReadOnlyList<AgentMessage>>>? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return Array.Empty<AgentMessage>();
        }

        return await source(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A pending message source returned null.");
    }

    private static void ValidateQueuedMessages(IReadOnlyList<AgentMessage> messages, AgentLimits limits)
    {
        if (messages.Count > limits.MaxQueuedMessages)
        {
            throw new AgentLimitException(nameof(limits.MaxQueuedMessages), "A queue drain returned too many messages.");
        }

        AgentValidator.ValidateMessages(messages, limits);
    }

    private static void EnsureMessageCapacity(int current, int additional, AgentLimits limits)
    {
        if (additional > limits.MaxMessages - current)
        {
            throw new AgentLimitException(nameof(limits.MaxMessages), "The run would exceed the maximum transcript size.");
        }
    }

    private sealed class MutableLoopContext
    {
        public MutableLoopContext(AgentContext context, IReadOnlyList<AgentMessage> prompts)
        {
            SystemPrompt = context.SystemPrompt;
            Messages = new List<AgentMessage>(context.Messages.Count + prompts.Count);
            Messages.AddRange(context.Messages);
            Messages.AddRange(prompts);
            Tools = context.Tools.ToList();
        }

        public string SystemPrompt { get; }

        public List<AgentMessage> Messages { get; }

        public List<AgentTool> Tools { get; }

        public AgentContext Snapshot() => new(SystemPrompt, Messages, Tools);
    }

    private sealed class ToolPreparation
    {
        private ToolPreparation(
            ToolCallContent? call,
            AgentTool? tool,
            JsonElement arguments,
            string? conflictKey,
            ToolResult? error)
        {
            Call = call;
            Tool = tool;
            Arguments = arguments;
            ConflictKey = conflictKey;
            Error = error;
        }

        public ToolCallContent? Call { get; }

        public AgentTool? Tool { get; }

        public JsonElement Arguments { get; }

        public string? ConflictKey { get; }

        public ToolResult? Error { get; }

        public static ToolPreparation Ready(ToolCallContent call, AgentTool tool, JsonElement arguments, string? conflictKey) =>
            new(call, tool, arguments, conflictKey, null);

        public static ToolPreparation Failed(ToolResult error) =>
            new(null, null, default, null, error);
    }

    private sealed class PreparedToolCall
    {
        public PreparedToolCall(int index, ToolCallContent call, AgentTool tool, JsonElement arguments, string? conflictKey)
        {
            Index = index;
            Call = call;
            Tool = tool;
            Arguments = arguments;
            ConflictKey = conflictKey;
        }

        public int Index { get; }

        public ToolCallContent Call { get; }

        public AgentTool Tool { get; }

        public JsonElement Arguments { get; }

        public string? ConflictKey { get; }
    }

    private sealed class ToolExecutionEpoch
    {
        public ToolExecutionEpoch(IReadOnlyList<PreparedToolCall> calls, bool parallel)
        {
            Calls = calls;
            Parallel = parallel;
        }

        public IReadOnlyList<PreparedToolCall> Calls { get; }

        public bool Parallel { get; }
    }

    private sealed class ExactToolRepeatTracker
    {
        private string? _fingerprint;
        private int _count;

        public ToolRepeatObservation? Observe(PreparedToolCall call, AgentLimits limits)
        {
            if (!call.Tool.TrackExactRepeats
                || limits.ExactToolRepeatAdvisoryThreshold == 0
                    && limits.ExactToolRepeatTerminationThreshold == 0)
            {
                return null;
            }

            var fingerprint = CreateFingerprint(call);
            if (string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _count = checked(_count + 1);
            }
            else
            {
                _fingerprint = fingerprint;
                _count = 1;
            }

            if (limits.ExactToolRepeatTerminationThreshold > 0
                && _count >= limits.ExactToolRepeatTerminationThreshold)
            {
                return new ToolRepeatObservation(_count, ToolRepeatPolicyAction.Terminated);
            }

            return limits.ExactToolRepeatAdvisoryThreshold > 0
                   && _count == limits.ExactToolRepeatAdvisoryThreshold
                ? new ToolRepeatObservation(_count, ToolRepeatPolicyAction.Advisory)
                : null;
        }

        public void Reset()
        {
            _fingerprint = null;
            _count = 0;
        }

        private static string CreateFingerprint(PreparedToolCall call)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonical(writer, call.Arguments);
            }

            using var algorithm = SHA256.Create();
            var hash = algorithm.ComputeHash(buffer.ToArray());
            return call.Call.Name + ":" + Convert.ToBase64String(hash);
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                    {
                        WriteCanonical(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(value.GetString());
                    break;

                case JsonValueKind.Number:
                    writer.WriteStringValue("number:" + CanonicalizeNumber(value.GetRawText()));
                    break;

                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;

                default:
                    throw new InvalidOperationException("Prepared tool arguments contain an unsupported JSON value.");
            }
        }

        private static string CanonicalizeNumber(string raw)
        {
            var value = raw;
            var negative = value.Length > 0 && value[0] == '-';
            if (negative)
            {
                value = value.Substring(1);
            }

            var exponentIndex = value.IndexOfAny(new[] { 'e', 'E' });
            var exponent = BigInteger.Zero;
            if (exponentIndex >= 0)
            {
                exponent = BigInteger.Parse(
                    value.Substring(exponentIndex + 1),
                    System.Globalization.CultureInfo.InvariantCulture);
                value = value.Substring(0, exponentIndex);
            }

            var decimalIndex = value.IndexOf('.');
            var decimalPlaces = decimalIndex < 0 ? 0 : value.Length - decimalIndex - 1;
            var digits = decimalIndex < 0 ? value : value.Remove(decimalIndex, 1);
            digits = digits.TrimStart('0');
            if (digits.Length == 0)
            {
                return "0e0";
            }

            var trailingZeros = 0;
            while (digits.Length > 1 && digits[digits.Length - 1] == '0')
            {
                trailingZeros++;
                digits = digits.Substring(0, digits.Length - 1);
            }

            exponent = exponent - decimalPlaces + trailingZeros;
            return (negative ? "-" : string.Empty)
                + digits
                + "e"
                + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class ToolRepeatObservation
    {
        public ToolRepeatObservation(int count, ToolRepeatPolicyAction action)
        {
            Count = count;
            Action = action;
        }

        public int Count { get; }

        public ToolRepeatPolicyAction Action { get; }
    }

    private sealed class ToolCallOutcome
    {
        public ToolCallOutcome(
            int index,
            ToolCallContent call,
            ToolResult result,
            bool uncertainSideEffect = false)
        {
            Index = index;
            Call = call;
            Result = result;
            UncertainSideEffect = uncertainSideEffect;
        }

        public int Index { get; }

        public ToolCallContent Call { get; }

        public ToolResult Result { get; }

        public bool UncertainSideEffect { get; }
    }

    private sealed class ToolBatchOutcome
    {
        public static ToolBatchOutcome Empty { get; } = new(Array.Empty<AgentMessage>(), false);

        public ToolBatchOutcome(IReadOnlyList<AgentMessage> messages, bool terminate)
        {
            Messages = messages;
            Terminate = terminate;
        }

        public IReadOnlyList<AgentMessage> Messages { get; }

        public bool Terminate { get; }
    }
}
