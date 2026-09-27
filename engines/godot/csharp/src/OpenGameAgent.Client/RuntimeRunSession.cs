using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Protocol;

namespace OpenGameAgent.Client;

public sealed class RuntimeRunSessionOptions
{
    public required RuntimeSseRetryPolicy EventRetryPolicy { get; init; }

    public RuntimeRunEventHandlerOptions? EventHandler { get; init; }

    public RuntimeRunActionOptions? Actions { get; init; }

    public RuntimeRunApprovalOptions? Approvals { get; init; }

    public RuntimeActorActionRecoveryOptions? Recovery { get; init; }
}

public sealed class RuntimeRunEventHandlerOptions
{
    public required TimeSpan Timeout { get; init; }

    public required Func<RuntimeSseFrameEvent, CancellationToken, Task> Handler { get; init; }
}

public sealed class RuntimeRunActionOptions
{
    public required RuntimeSseRetryPolicy RetryPolicy { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required Func<RuntimeActionDelivery, CancellationToken, Task> OnDelivery { get; init; }
}

public sealed class RuntimeRunApprovalOptions
{
    public required TimeSpan WaitDuration { get; init; }

    public required long PageSize { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required Func<RuntimeApproval, CancellationToken, Task> OnPending { get; init; }
}

public sealed class RuntimeRunSessionAccepted
{
    internal RuntimeRunSessionAccepted(RuntimeRunAccepted accepted)
    {
        RunId = accepted.RunId;
        SessionId = accepted.InitialCoordinate.SessionId;
        ActorId = accepted.InitialCoordinate.ActorId;
        Turn = accepted.InitialCoordinate.Turn;
        EventCursor = accepted.EventCursor;
    }

    public string RunId { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public string Turn { get; }

    public string EventCursor { get; }
}

public sealed class RuntimeRunSession : IAsyncDisposable
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    private readonly RuntimeClient client;
    private readonly RuntimeRunSessionOptions options;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly CancellationToken callerCancellation;
    private readonly CancellationTokenSource lifetime;
    private readonly string expectedInputId;
    private readonly string expectedInputFingerprint;
    private readonly string? expectedAuthorityScope;
    private readonly string? expectedAuthorityGeneration;
    private readonly string admissionRevision;
    private readonly object stateGate = new();
    private readonly object approvalGate = new();
    private readonly TaskCompletionSource activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, PendingApproval> pendingApprovals = new(StringComparer.Ordinal);
    private TaskCompletionSource approvalMutationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string latestTurn;
    private long approvalMutation;
    private Exception? closeReason;

    private RuntimeRunSession(
        RuntimeClient client,
        RuntimeRunRequest request,
        RuntimeRunAccepted accepted,
        RuntimeRunSessionOptions options,
        JsonSerializerOptions jsonOptions,
        CancellationToken callerCancellation)
    {
        ValidateAccepted(accepted, request);
        this.client = client;
        this.options = options;
        this.jsonOptions = jsonOptions;
        this.callerCancellation = callerCancellation;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        Accepted = new RuntimeRunSessionAccepted(accepted);
        latestTurn = Accepted.Turn;
        expectedInputId = request.InputId;
        expectedAuthorityScope = request.AdmissionAuthority?.Scope;
        expectedAuthorityGeneration = request.AdmissionAuthority?.Generation;
        expectedInputFingerprint = CanonicalJson.Fingerprint(new Dictionary<string, object?>
        {
            ["payload"] = request.Payload, ["admissionAuthority"] = request.AdmissionAuthority,
        }, jsonOptions);
        admissionRevision = (ParseDecimal(accepted.EventCursor) + BigInteger.One).ToString(CultureInfo.InvariantCulture);
        Completion = ObserveAsync();
    }

    public RuntimeRunSessionAccepted Accepted { get; }

    public Task<RuntimeSseFrameEvent> Completion { get; }

    internal static async Task<RuntimeRunSession> StartAsync(
        RuntimeClient client,
        RuntimeRunRequest request,
        RuntimeRunSessionOptions options,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RuntimeRunSessionOptions capturedOptions = CaptureOptions(options);
        RuntimeRunRequest captured = CaptureRequest(request, jsonOptions);
        if (capturedOptions.Recovery is not null)
        {
            await client.RecoverActorActionsAsync(
                captured.SessionId,
                captured.ActorId,
                capturedOptions.Recovery,
                cancellationToken).ConfigureAwait(false);
        }
        RuntimeRunAccepted accepted = await client.SubmitRunAsync(captured, cancellationToken).ConfigureAwait(false);
        return new RuntimeRunSession(client, captured, accepted, capturedOptions, jsonOptions, cancellationToken);
    }

    internal static async Task<RuntimeRunSession> AttachAsync(
        RuntimeClient client,
        RuntimeRunRequest request,
        RuntimeRunAccepted accepted,
        RuntimeRunSessionOptions options,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(accepted);
        RuntimeRunSessionOptions capturedOptions = CaptureOptions(options);
        RuntimeRunRequest captured = CaptureRequest(request, jsonOptions);
        ValidateAccepted(accepted, captured);
        if (capturedOptions.Recovery is not null)
        {
            await client.RecoverActorActionsAsync(
                captured.SessionId,
                captured.ActorId,
                capturedOptions.Recovery,
                cancellationToken).ConfigureAwait(false);
        }
        return new RuntimeRunSession(client, captured, accepted, capturedOptions, jsonOptions, cancellationToken);
    }

    private static RuntimeRunSessionOptions CaptureOptions(RuntimeRunSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RuntimeClient.ValidateRetryPolicy(options.EventRetryPolicy);
        if (options.EventHandler is not null)
        {
            ValidateDelay(options.EventHandler.Timeout, nameof(options.EventHandler.Timeout));
            ArgumentNullException.ThrowIfNull(options.EventHandler.Handler);
        }
        if (options.Actions is not null)
        {
            RuntimeClient.ValidateRetryPolicy(options.Actions.RetryPolicy);
            ValidateDelay(options.Actions.Timeout, nameof(options.Actions.Timeout));
            ArgumentNullException.ThrowIfNull(options.Actions.OnDelivery);
        }
        if (options.Approvals is not null)
        {
            ValidateDelay(options.Approvals.WaitDuration, nameof(options.Approvals.WaitDuration));
            if (options.Approvals.PageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.Approvals.PageSize));
            }
            ValidateDelay(options.Approvals.Timeout, nameof(options.Approvals.Timeout));
            ArgumentNullException.ThrowIfNull(options.Approvals.OnPending);
        }
        RuntimeActorActionRecoveryOptions? capturedRecovery = options.Recovery is null
            ? null
            : RuntimeClient.CaptureRecoveryOptions(options.Recovery);
        RuntimeRunApprovalOptions? capturedApprovals = options.Approvals is null
            ? null
            : new RuntimeRunApprovalOptions
            {
                WaitDuration = options.Approvals.WaitDuration,
                PageSize = options.Approvals.PageSize,
                Timeout = options.Approvals.Timeout,
                OnPending = options.Approvals.OnPending,
            };
        var capturedOptions = new RuntimeRunSessionOptions
        {
            EventRetryPolicy = options.EventRetryPolicy,
            EventHandler = options.EventHandler,
            Actions = options.Actions,
            Approvals = capturedApprovals,
            Recovery = capturedRecovery,
        };
        return capturedOptions;
    }

    private static RuntimeRunRequest CaptureRequest(RuntimeRunRequest request, JsonSerializerOptions jsonOptions)
    {
        return RuntimeClient.CaptureRunRequest(request, jsonOptions);
    }

    public void Close(Exception? reason = null)
    {
        lock (stateGate)
        {
            if (closeReason is not null || lifetime.IsCancellationRequested) return;
            closeReason = reason ?? new RuntimeRunSessionClosedException();
        }
        CancelLifetime();
    }

    public async Task<RuntimeControlResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string expectedTurn;
        lock (stateGate)
        {
            expectedTurn = latestTurn;
        }
        RuntimeControlResult result = await client.ControlRunAsync(
            new RuntimeControlRequest
            {
                Coordinate = new RuntimeControlRequestCoordinate
                {
                    SessionId = Accepted.SessionId,
                    ActorId = Accepted.ActorId,
                    RunId = Accepted.RunId,
                    Turn = expectedTurn,
                },
                Command = new Command { Type = Control.Abort },
            },
            cancellationToken).ConfigureAwait(false);
        ValidateControlResult(result, expectedTurn);
        return Clone(result);
    }

    public async Task<RuntimeApproval> DecideApprovalAsync(
        string approvalId,
        RuntimeApprovalDecisionRequestDecision decision,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        PendingApproval pending;
        lock (approvalGate)
        {
            if (!pendingApprovals.TryGetValue(approvalId, out pending!))
            {
                throw new RuntimeRunSessionApprovalNotFoundException();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeApprovalDecisionResponse response = await client.DecideApprovalAsync(
            new RuntimeApprovalDecisionRequest
            {
                SessionId = Accepted.SessionId,
                ActorId = Accepted.ActorId,
                ApprovalId = pending.Approval.Request.ApprovalId,
                ExpectedRevision = pending.Approval.Revision,
                RequestFingerprint = pending.Approval.Request.RequestFingerprint,
                Decision = decision,
                Reason = reason,
            },
            cancellationToken).ConfigureAwait(false);
        RuntimeApproval decided = response.Approval
            ?? throw new RuntimeRunSessionIdentityException();
        ValidateApproval(decided);
        bool expired = decided.Status == RuntimeApprovalStatus.Expired;
        RuntimeApprovalStatus expectedStatus = decision == RuntimeApprovalDecisionRequestDecision.Approve
            ? RuntimeApprovalStatus.Approved
            : RuntimeApprovalStatus.Rejected;
        DecisionDecision expectedDecision = decision == RuntimeApprovalDecisionRequestDecision.Approve
            ? DecisionDecision.Approved
            : DecisionDecision.Rejected;
        if (!string.Equals(CanonicalJson.Serialize(decided.Request, jsonOptions), pending.CanonicalRequest, StringComparison.Ordinal)
            || ParseDecimal(decided.Revision) != ParseDecimal(pending.Approval.Revision) + BigInteger.One
            || expired && decided.Decision is not null
            || !expired && (decided.Status != expectedStatus
                || decided.Decision is null
                || decided.Decision.Decision != expectedDecision
                || !string.Equals(decided.Decision.Reason, reason, StringComparison.Ordinal)))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        TaskCompletionSource? mutationSignal = null;
        lock (approvalGate)
        {
            if (pendingApprovals.TryGetValue(approvalId, out PendingApproval? current)
                && string.Equals(current.CanonicalRequest, pending.CanonicalRequest, StringComparison.Ordinal)
                && string.Equals(current.Approval.Revision, pending.Approval.Revision, StringComparison.Ordinal))
            {
                pendingApprovals.Remove(approvalId);
                approvalMutation++;
                mutationSignal = approvalMutationSignal;
                approvalMutationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        mutationSignal?.TrySetResult();
        return Clone(decided);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Close();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (RuntimeRunSessionClosedException)
            {
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task<RuntimeSseFrameEvent> ObserveAsync()
    {
        Task<RuntimeSseFrameEvent> events = ObserveEventsAsync();
        var tasks = new List<Task> { events };
        if (options.Actions is not null) tasks.Add(ObserveActionsAsync(options.Actions));
        if (options.Approvals is not null) tasks.Add(ObserveApprovalsAsync(options.Approvals));
        try
        {
            Task completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            if (ReferenceEquals(completed, events)) return await events.ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            throw new RuntimeRunSessionStreamEndedException("child");
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) CancelLifetime();
            foreach (Task task in tasks)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<RuntimeSseFrameEvent> ObserveEventsAsync()
    {
        try
        {
            bool admissionObserved = false;
            await foreach (RuntimeSseFrame frame in client.StreamEventsAsync(
                Accepted.SessionId,
                Accepted.ActorId,
                Accepted.EventCursor,
                options.EventRetryPolicy,
                lifetime.Token).ConfigureAwait(false))
            {
                ValidateEventFrame(frame);
                if (frame.Type == RuntimeSseFrameType.Gap)
                {
                    throw new RuntimeRunSessionGapException(frame.Gap);
                }
                if (frame.Type == RuntimeSseFrameType.Cursor) continue;
                RuntimeSseFrameEvent current = frame.Event ?? throw new RuntimeRunSessionIdentityException();
                ValidateCanonicalEvent(current);
                if (!admissionObserved)
                {
                    ValidateAdmission(current);
                    admissionObserved = true;
                }
                ValidateStream(current.Coordinate);
                if (!string.Equals(current.Coordinate.RunId, Accepted.RunId, StringComparison.Ordinal)) continue;
                lock (stateGate)
                {
                    latestTurn = current.Coordinate.Turn;
                }
                if (current.Data.Type == DataType.RunStarted)
                {
                    if (!string.Equals(current.Data.InputId, expectedInputId, StringComparison.Ordinal))
                    {
                        throw new RuntimeRunSessionIdentityException();
                    }
                    activation.TrySetResult();
                }
                RuntimeRunEventHandlerOptions? eventHandler = options.EventHandler;
                if (eventHandler is not null)
                {
                    try
                    {
                        await eventHandler.Handler(Clone(current), lifetime.Token)
                            .WaitAsync(eventHandler.Timeout, lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        throw new RuntimeRunSessionHandlerTimeoutException("event");
                    }
                }
                if (current.Data.Type == DataType.RunCompleted)
                {
                    if (current.Data.Status is null) throw new RuntimeRunSessionIdentityException();
                    return current;
                }
            }
            throw new RuntimeRunSessionStreamEndedException("events");
        }
        catch (RuntimeSseDisconnectedException exception)
        {
            throw new RuntimeRunSessionStreamEndedException("events", exception);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Exception? reason;
            lock (stateGate)
            {
                reason = closeReason;
            }
            if (reason is not null) throw reason;
            callerCancellation.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async Task ObserveActionsAsync(RuntimeRunActionOptions actionOptions)
    {
        try
        {
            await activation.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            await foreach (RuntimeActionSseFrame frame in client.StreamActionsAsync(
                new RuntimeActionNextRequest
                {
                    SessionId = Accepted.SessionId,
                    ActorId = Accepted.ActorId,
                    RunId = Accepted.RunId,
                },
                actionOptions.RetryPolicy,
                lifetime.Token).ConfigureAwait(false))
            {
                RuntimeActionDelivery delivery = frame.Delivery ?? throw new RuntimeRunSessionIdentityException();
                ValidateActionDelivery(delivery);
                try
                {
                    await actionOptions.OnDelivery(Clone(delivery), lifetime.Token)
                        .WaitAsync(actionOptions.Timeout, lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new RuntimeRunSessionHandlerTimeoutException("action");
                }
            }
            throw new RuntimeRunSessionStreamEndedException("actions");
        }
        catch (RuntimeSseDisconnectedException exception)
        {
            throw new RuntimeRunSessionStreamEndedException("actions", exception);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            ThrowCancellationReason();
            throw;
        }
    }

    private async Task ObserveApprovalsAsync(RuntimeRunApprovalOptions approvalOptions)
    {
        try
        {
            await activation.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            string? cursor = null;
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                long observedMutation;
                Task mutationTask;
                lock (approvalGate)
                {
                    observedMutation = approvalMutation;
                    mutationTask = approvalMutationSignal.Task;
                }
                var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                Task<RuntimeApprovalWaitResponse> waitTask = client.WaitApprovalsAsync(
                    new RuntimeApprovalWaitRequest
                    {
                        SessionId = Accepted.SessionId,
                        ActorId = Accepted.ActorId,
                        RunId = Accepted.RunId,
                        Limit = approvalOptions.PageSize,
                        WaitMilliseconds = checked((long)approvalOptions.WaitDuration.TotalMilliseconds),
                        AfterCursor = cursor,
                    },
                    waitCancellation.Token);
                Task completed;
                try
                {
                    completed = await Task.WhenAny(waitTask, mutationTask)
                        .WaitAsync(lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    CancelAndObserve(waitCancellation, waitTask);
                    throw;
                }
                if (completed == mutationTask)
                {
                    CancelAndObserve(waitCancellation, waitTask);
                    continue;
                }
                RuntimeApprovalWaitResponse response;
                try
                {
                    response = await waitTask.ConfigureAwait(false);
                }
                finally
                {
                    waitCancellation.Dispose();
                }
                if (!IsSha256(response.Cursor)) throw new RuntimeRunSessionIdentityException();
                cursor = response.Cursor;
                RuntimeApproval[] approvals = response.Approvals ?? throw new RuntimeRunSessionIdentityException();
                if (approvals.LongLength > approvalOptions.PageSize) throw new RuntimeRunSessionIdentityException();
                Dictionary<string, PendingApproval> previous;
                lock (approvalGate)
                {
                    if (observedMutation != approvalMutation) continue;
                    previous = new Dictionary<string, PendingApproval>(pendingApprovals, StringComparer.Ordinal);
                }
                var current = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
                foreach (RuntimeApproval approval in approvals)
                {
                    if (approval is null)
                    {
                        throw new RuntimeRunSessionIdentityException();
                    }
                    ValidateApproval(approval);
                    if (approval.Status != RuntimeApprovalStatus.Pending)
                    {
                        throw new RuntimeRunSessionIdentityException();
                    }
                    var captured = new PendingApproval(Clone(approval), CanonicalJson.Serialize(approval.Request, jsonOptions));
                    if (!current.TryAdd(captured.Approval.Request.ApprovalId, captured))
                    {
                        throw new RuntimeRunSessionIdentityException();
                    }
                }
                lock (approvalGate)
                {
                    if (observedMutation != approvalMutation) continue;
                    pendingApprovals.Clear();
                    foreach ((string approvalId, PendingApproval approval) in current)
                    {
                        pendingApprovals.Add(approvalId, approval);
                    }
                }
                bool approvalMutated = false;
                foreach ((string approvalId, PendingApproval approval) in current)
                {
                    if (previous.TryGetValue(approvalId, out PendingApproval? prior)
                        && string.Equals(prior.Approval.Revision, approval.Approval.Revision, StringComparison.Ordinal)
                        && string.Equals(
                            prior.Approval.Request.RequestFingerprint,
                            approval.Approval.Request.RequestFingerprint,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        await approvalOptions.OnPending(Clone(approval.Approval), lifetime.Token)
                            .WaitAsync(approvalOptions.Timeout, lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        throw new RuntimeRunSessionHandlerTimeoutException("approval");
                    }
                    lock (approvalGate)
                    {
                        if (observedMutation != approvalMutation)
                        {
                            approvalMutated = true;
                        }
                    }
                    if (approvalMutated) break;
                }
                if (approvalMutated)
                {
                    continue;
                }
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            ThrowCancellationReason();
            throw;
        }
    }

    private void ValidateAdmission(RuntimeSseFrameEvent runtimeEvent)
    {
        ValidateExactCoordinate(runtimeEvent.Coordinate, Accepted.Turn);
        if (!string.Equals(runtimeEvent.Revision, admissionRevision, StringComparison.Ordinal)
            || runtimeEvent.Data.Type != DataType.RunQueued
            || !string.Equals(runtimeEvent.Data.InputId, expectedInputId, StringComparison.Ordinal)
            || !string.Equals(runtimeEvent.Data.InputFingerprint, expectedInputFingerprint, StringComparison.Ordinal)
            || runtimeEvent.Data.AdmissionAuthority?.Scope != expectedAuthorityScope
            || runtimeEvent.Data.AdmissionAuthority?.Generation != expectedAuthorityGeneration)
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateCanonicalEvent(RuntimeSseFrameEvent runtimeEvent)
    {
        FluffyCoordinate coordinate = runtimeEvent.Coordinate ?? throw new RuntimeRunSessionIdentityException();
        StickyAudience audience = runtimeEvent.Audience ?? throw new RuntimeRunSessionIdentityException();
        FluffyData data = runtimeEvent.Data ?? throw new RuntimeRunSessionIdentityException();
        if (runtimeEvent.ProtocolVersion != OpenGameAgent.Protocol.Version.The1
            || !IsOpaqueIdentifier(runtimeEvent.EventId)
            || !IsOpaqueIdentifier(coordinate.SessionId)
            || !IsOpaqueIdentifier(coordinate.ActorId)
            || !IsOpaqueIdentifier(coordinate.RunId))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ParseDecimal(runtimeEvent.Sequence);
        ParseDecimal(runtimeEvent.Revision);
        ParseDecimal(coordinate.Turn);
        ValidateCanonicalTimestamp(runtimeEvent.OccurredAt);
        ValidateAudience(audience.Visibility, audience.RecipientIds);
        ValidateCanonicalEventData(data);

        Visibility expectedVisibility = Visibility.Owner;
        string[]? expectedRecipients = null;
        if (data.Type is DataType.ItemStarted or DataType.ItemCompleted)
        {
            IndecentAudience itemAudience = data.Item!.Audience;
            expectedVisibility = itemAudience.Visibility;
            expectedRecipients = itemAudience.RecipientIds;
        }
        else if (data.Type is DataType.ItemDelta or DataType.ItemUpdated)
        {
            IndigoAudience dataAudience = data.Audience!;
            expectedVisibility = dataAudience.Visibility;
            expectedRecipients = dataAudience.RecipientIds;
        }
        if (audience.Visibility != expectedVisibility
            || !SameRecipients(audience.RecipientIds, expectedRecipients))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateCanonicalEventData(FluffyData data)
    {
        switch (data.Type)
        {
            case DataType.RunQueued:
                ValidateEventDataShape(data, EventDataFields.InputId | EventDataFields.InputFingerprint
                    | (data.AdmissionAuthority is null ? EventDataFields.None : EventDataFields.AdmissionAuthority));
                if (!IsOpaqueIdentifier(data.InputId) || !IsSha256(data.InputFingerprint)) RejectIdentity();
                if (data.AdmissionAuthority is not null)
                {
                    if (!IsOpaqueIdentifier(data.AdmissionAuthority.Scope)) RejectIdentity();
                    ParseDecimal(data.AdmissionAuthority.Generation);
                }
                return;
            case DataType.RunStarted:
                ValidateEventDataShape(data, EventDataFields.InputId);
                if (!IsOpaqueIdentifier(data.InputId)) RejectIdentity();
                return;
            case DataType.RunResumed:
            case DataType.RunSuspended:
                ValidateEventDataShape(data, EventDataFields.OperationId);
                if (!IsOpaqueIdentifier(data.OperationId)) RejectIdentity();
                return;
            case DataType.TurnStarted:
                ValidateEventDataShape(data, EventDataFields.None);
                return;
            case DataType.ItemStarted:
            case DataType.ItemCompleted:
                ValidateEventDataShape(data, EventDataFields.Item);
                ValidateCanonicalItem(data.Item!);
                return;
            case DataType.ItemDelta:
                ValidateEventDataShape(
                    data,
                    EventDataFields.ItemId | EventDataFields.Audience | EventDataFields.Delta);
                if (!IsOpaqueIdentifier(data.ItemId)
                    || data.Delta!.Text is null
                    || data.Delta.ContentIndex is < 0 or > MaximumSafeInteger)
                {
                    RejectIdentity();
                }
                ValidateAudience(data.Audience!.Visibility, data.Audience.RecipientIds);
                return;
            case DataType.ItemUpdated:
                ValidateEventDataShape(
                    data,
                    EventDataFields.ItemId
                        | EventDataFields.Audience
                        | EventDataFields.UpdateOrdinal
                        | EventDataFields.Content);
                if (!IsOpaqueIdentifier(data.ItemId)) RejectIdentity();
                ParseDecimal(data.UpdateOrdinal!);
                ValidateAudience(data.Audience!.Visibility, data.Audience.RecipientIds);
                ValidateToolResultContent(data.Content!);
                return;
            case DataType.TurnCompleted:
                ValidateEventDataShape(data, EventDataFields.FinishReason | EventDataFields.ToolResultItemIds);
                if (data.ToolResultItemIds!.Any(itemId => !IsOpaqueIdentifier(itemId))) RejectIdentity();
                return;
            case DataType.ControlAccepted:
                ValidateEventDataShape(data, EventDataFields.Control);
                return;
            case DataType.ToolRepeatDetected:
                ValidateEventDataShape(
                    data,
                    EventDataFields.ToolCallId
                        | EventDataFields.ToolName
                        | EventDataFields.ConsecutiveCount
                        | EventDataFields.Action);
                if (!IsOpaqueIdentifier(data.ToolCallId)
                    || !IsOpaqueIdentifier(data.ToolName)
                    || ParseDecimal(data.ConsecutiveCount!).IsZero)
                {
                    RejectIdentity();
                }
                return;
            case DataType.RunCompleted:
                ValidateEventDataShape(data, EventDataFields.Status);
                return;
            case DataType.RunMetrics:
                ValidateEventDataShape(
                    data,
                    EventDataFields.Counts
                        | EventDataFields.Durations
                        | EventDataFields.SegmentStatus
                        | EventDataFields.Unit);
                if (data.Unit != Unit.Microseconds || data.SegmentStatus is null) RejectIdentity();
                ValidateRunMetrics(data.Counts!, data.Durations!);
                return;
            default:
                RejectIdentity();
                return;
        }
    }

    private static void ValidateCanonicalItem(FluffyItem item)
    {
        IndecentAudience audience = item.Audience ?? throw new RuntimeRunSessionIdentityException();
        if (!IsOpaqueIdentifier(item.ItemId)) RejectIdentity();
        ValidateAudience(audience.Visibility, audience.RecipientIds);
        switch (item.ItemType)
        {
            case ItemType.Message:
                if (item.Role is null
                    || item.Content is null
                    || item.IsError is not null
                    || item.ParentMessageItemId is not null
                    || item.SourceIndex is not null
                    || item.ToolCallId is not null
                    || item.ToolName is not null)
                {
                    RejectIdentity();
                }
                ValidateMessageContent(item.Content, item.Role.Value, item.Context, audience.Visibility);
                return;
            case ItemType.ToolCall:
                if (!IsOpaqueIdentifier(item.ToolCallId)
                    || !IsOpaqueIdentifier(item.ToolName)
                    || item.Content is not null
                    || item.Context is not null
                    || item.IsError is not null
                    || item.Role is not null
                    || item.ParentMessageItemId is not null && !IsOpaqueIdentifier(item.ParentMessageItemId)
                    || item.SourceIndex is < 0 or > MaximumSafeInteger)
                {
                    RejectIdentity();
                }
                return;
            case ItemType.ToolResult:
                if (!IsOpaqueIdentifier(item.ToolCallId)
                    || !IsOpaqueIdentifier(item.ToolName)
                    || item.IsError is null
                    || item.Content is null
                    || item.Context is not null
                    || item.ParentMessageItemId is not null
                    || item.Role is not null
                    || item.SourceIndex is not null)
                {
                    RejectIdentity();
                }
                ValidateToolResultContent(item.Content);
                return;
            default:
                RejectIdentity();
                return;
        }
    }

    private static void ValidateMessageContent(
        StickyContent[] content,
        Role role,
        FluffyContext? context,
        Visibility visibility)
    {
        if (context is not null)
        {
            if (role != Role.User || visibility != Visibility.Internal || content.Length == 0)
            {
                RejectIdentity();
            }
            ValidatePlainText(content[0]);
            foreach (StickyContent part in content.Skip(1)) ValidateImageReference(part, allowName: false);
            ValidateContext(context);
            return;
        }
        foreach (StickyContent part in content)
        {
            if (role == Role.Assistant)
            {
                ValidateAssistantText(part);
            }
            else if (part.Type == FluffyType.Text)
            {
                ValidatePlainText(part);
            }
            else
            {
                ValidateImageReference(part, allowName: true);
            }
        }
    }

    private static void ValidatePlainText(StickyContent part)
    {
        if (part is null
            || part.Type != FluffyType.Text
            || part.Text is null
            || part.AttachmentId is not null
            || part.Bytes is not null
            || part.Height is not null
            || part.MediaType is not null
            || part.Name is not null
            || part.Sha256 is not null
            || part.SourceIndex is not null
            || part.Width is not null)
        {
            RejectIdentity();
        }
    }

    private static void ValidateAssistantText(StickyContent part)
    {
        if (part is null
            || part.Type is not (FluffyType.Text or FluffyType.ReasoningSummary)
            || part.Text is null
            || part.SourceIndex is null or < 0 or > MaximumSafeInteger
            || part.AttachmentId is not null
            || part.Bytes is not null
            || part.Height is not null
            || part.MediaType is not null
            || part.Name is not null
            || part.Sha256 is not null
            || part.Width is not null)
        {
            RejectIdentity();
        }
    }

    private static void ValidateImageReference(StickyContent part, bool allowName)
    {
        if (part is null
            || part.Type != FluffyType.ImageReference
            || !IsImageAttachmentId(part.AttachmentId)
            || !IsImageMediaType(part.MediaType)
            || part.Bytes is null or <= 0 or > MaximumSafeInteger
            || part.Width is null or <= 0 or > MaximumSafeInteger
            || part.Height is null or <= 0 or > MaximumSafeInteger
            || !allowName && part.Name is not null
            || part.Name is not null && !IsOpaqueIdentifier(part.Name)
            || part.Sha256 is not null
            || part.SourceIndex is not null
            || part.Text is not null)
        {
            RejectIdentity();
        }
    }

    private static void ValidateToolResultContent(TentacledContent[] content)
    {
        foreach (TentacledContent part in content)
        {
            if (part is null)
            {
                RejectIdentity();
            }
            if (part.Type == PurpleType.Text)
            {
                if (part.Text is null
                    || part.Bytes is not null
                    || part.MediaType is not null
                    || part.Sha256 is not null)
                {
                    RejectIdentity();
                }
            }
            else if (part.Type == PurpleType.MediaSummary)
            {
                if (string.IsNullOrEmpty(part.MediaType)
                    || part.Bytes is null or <= 0 or > MaximumSafeInteger
                    || !IsSha256(part.Sha256)
                    || part.Text is not null)
                {
                    RejectIdentity();
                }
            }
            else
            {
                RejectIdentity();
            }
        }
    }

    private static void ValidateToolResultContent(StickyContent[] content)
    {
        foreach (StickyContent part in content)
        {
            if (part is null)
            {
                RejectIdentity();
            }
            if (part.Type == FluffyType.Text)
            {
                if (part.Text is null
                    || part.AttachmentId is not null
                    || part.Bytes is not null
                    || part.Height is not null
                    || part.MediaType is not null
                    || part.Name is not null
                    || part.Sha256 is not null
                    || part.SourceIndex is not null
                    || part.Width is not null)
                {
                    RejectIdentity();
                }
            }
            else if (part.Type == FluffyType.MediaSummary)
            {
                if (string.IsNullOrEmpty(part.MediaType)
                    || part.Bytes is null or <= 0 or > MaximumSafeInteger
                    || !IsSha256(part.Sha256)
                    || part.AttachmentId is not null
                    || part.Height is not null
                    || part.Name is not null
                    || part.SourceIndex is not null
                    || part.Text is not null
                    || part.Width is not null)
                {
                    RejectIdentity();
                }
            }
            else
            {
                RejectIdentity();
            }
        }
    }

    private static void ValidateContext(FluffyContext context)
    {
        if (context.ContextVersion != OpenGameAgent.Protocol.Version.The1
            || !IsSha256(context.Fingerprint)
            || context.References is not { Length: > 0 })
        {
            RejectIdentity();
        }
        foreach (FluffyReference reference in context.References)
        {
            if (reference is null
                || !IsOpaqueIdentifier(reference.SourceId)
                || !IsOpaqueIdentifier(reference.ReferenceId)
                || !IsOpaqueIdentifier(reference.Revision)
                || !IsSha256(reference.ContentFingerprint))
            {
                RejectIdentity();
            }
        }
    }

    private static void ValidateEventDataShape(FluffyData data, EventDataFields expected)
    {
        EventDataFields actual = EventDataFields.None;
        if (data.AdmissionAuthority is not null) actual |= EventDataFields.AdmissionAuthority;
        if (data.Action is not null) actual |= EventDataFields.Action;
        if (data.Audience is not null) actual |= EventDataFields.Audience;
        if (data.ConsecutiveCount is not null) actual |= EventDataFields.ConsecutiveCount;
        if (data.Content is not null) actual |= EventDataFields.Content;
        if (data.Control is not null) actual |= EventDataFields.Control;
		if (data.Counts is not null) actual |= EventDataFields.Counts;
        if (data.Delta is not null) actual |= EventDataFields.Delta;
		if (data.Durations is not null) actual |= EventDataFields.Durations;
        if (data.FinishReason is not null) actual |= EventDataFields.FinishReason;
        if (data.InputFingerprint is not null) actual |= EventDataFields.InputFingerprint;
        if (data.InputId is not null) actual |= EventDataFields.InputId;
        if (data.Item is not null) actual |= EventDataFields.Item;
        if (data.ItemId is not null) actual |= EventDataFields.ItemId;
        if (data.OperationId is not null) actual |= EventDataFields.OperationId;
		if (data.SegmentStatus is not null) actual |= EventDataFields.SegmentStatus;
        if (data.Status is not null) actual |= EventDataFields.Status;
        if (data.ToolCallId is not null) actual |= EventDataFields.ToolCallId;
        if (data.ToolName is not null) actual |= EventDataFields.ToolName;
        if (data.ToolResultItemIds is not null) actual |= EventDataFields.ToolResultItemIds;
		if (data.Unit is not null) actual |= EventDataFields.Unit;
        if (data.UpdateOrdinal is not null) actual |= EventDataFields.UpdateOrdinal;
        if (actual != expected) RejectIdentity();
    }

    private static void ValidateRunMetrics(FluffyCounts counts, FluffyDurations durations)
    {
        BigInteger providerAttempts = ParseDecimal(counts.ProviderAttempts);
        BigInteger retries = ParseDecimal(counts.Retries);
        BigInteger fallbacks = ParseDecimal(counts.Fallbacks);
        ParseDecimal(counts.ToolCalls);
        if (retries > (providerAttempts.IsZero ? BigInteger.Zero : providerAttempts - BigInteger.One)
            || fallbacks > retries)
        {
            RejectIdentity();
        }

        BigInteger contextAndRecall = ParseDecimal(durations.ContextAndRecall);
        BigInteger provider = ParseDecimal(durations.Provider);
        BigInteger durable = ParseDecimal(durations.Durable);
        BigInteger native = ParseDecimal(durations.Native);
        BigInteger total = ParseDecimal(durations.Total);
        if (contextAndRecall + provider + native != total || durable > total) RejectIdentity();
        if (durations.FirstResponse is not null && ParseDecimal(durations.FirstResponse) > total) RejectIdentity();
        if (durations.ProviderFirstResponse is not null && ParseDecimal(durations.ProviderFirstResponse) > total) RejectIdentity();
        if (durations.FirstTool is not null && ParseDecimal(durations.FirstTool) > total) RejectIdentity();
        foreach (string? phase in new[] { durations.Admission, durations.Queue, durations.MemoryPrepare,
            durations.MemorySearch, durations.Tool, durations.Approval, durations.Settlement })
        {
            if (phase is not null) ParseDecimal(phase);
        }
    }

    private static void ValidateAudience(Visibility visibility, string[]? recipientIds)
    {
        if (visibility != Visibility.Recipient)
        {
            if (recipientIds is not null) RejectIdentity();
            return;
        }
        if (recipientIds is not { Length: > 0 }
            || recipientIds.Any(recipientId => !IsOpaqueIdentifier(recipientId))
            || recipientIds.Distinct(StringComparer.Ordinal).Count() != recipientIds.Length)
        {
            RejectIdentity();
        }
    }

    private static bool SameRecipients(string[]? left, string[]? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool IsImageAttachmentId(string? value) =>
        value is { Length: 68 }
        && value.StartsWith("img_", StringComparison.Ordinal)
        && IsSha256(value[4..]);

    private static bool IsImageMediaType(string? value) =>
        value is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    [DoesNotReturn]
    private static void RejectIdentity() => throw new RuntimeRunSessionIdentityException();

    private void ValidateExactCoordinate(FluffyCoordinate coordinate, string expectedTurn)
    {
        ValidateStream(coordinate);
        if (!string.Equals(coordinate.RunId, Accepted.RunId, StringComparison.Ordinal)
            || !string.Equals(coordinate.Turn, expectedTurn, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private void ValidateActionDelivery(RuntimeActionDelivery delivery)
    {
        RuntimeActionRecord action = delivery.Action ?? throw new RuntimeRunSessionIdentityException();
        ValidateActionRecord(action, Accepted.SessionId, Accepted.ActorId, Accepted.RunId, jsonOptions);
        if ((delivery.Kind == RuntimeActionDeliveryKind.Dispatch && action.Status != RuntimeActionRecordStatus.Claimed)
            || (delivery.Kind == RuntimeActionDeliveryKind.Reconcile
                && action.Status != RuntimeActionRecordStatus.Claimed
                && action.Status != RuntimeActionRecordStatus.Uncertain))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        if (action.GoalProjectionSettlement is not null) throw new RuntimeRunSessionIdentityException();
    }

    internal static void ValidateRecoveryDelivery(
        RuntimeActionRecoveryDelivery delivery,
        string sessionId,
        string actorId,
        JsonSerializerOptions jsonOptions)
    {
        if (delivery is null || delivery.Kind != RuntimeActionRecoveryDeliveryKind.Reconcile)
        {
            throw new RuntimeRunSessionIdentityException();
        }
        RuntimeActionRecord action = delivery.Action ?? throw new RuntimeRunSessionIdentityException();
        ValidateActionRecord(action, sessionId, actorId, expectedRunId: null, jsonOptions);
        if (action.Status is not (RuntimeActionRecordStatus.Claimed or RuntimeActionRecordStatus.Uncertain)
            || action.GoalProjectionSettlement is not null)
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    internal static void ValidateRecoveryReconciliation(RuntimeActionReconcileRequest reconciliation)
    {
        if (reconciliation.Resolution is not (Resolution.Applied or Resolution.NotApplied or Resolution.Unknown))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        if (reconciliation.AuthorityAfter is not null)
        {
            ValidateAuthority(
                reconciliation.AuthorityAfter.TimelineId,
                reconciliation.AuthorityAfter.Generation,
                reconciliation.AuthorityAfter.Revision);
        }
    }

    internal static void ValidateReconciledAction(
        RuntimeActionRecord original,
        RuntimeActionRecord reconciled,
        RuntimeActionReconcileRequest reconciliation,
        JsonSerializerOptions jsonOptions)
    {
        if (original.Claim is null || reconciled is null)
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ValidateActionRecord(
            reconciled,
            original.SessionId,
            original.ActorId,
            original.Coordinate.RunId,
            jsonOptions);
        RuntimeActionRecordStatus expectedStatus = reconciliation.Resolution switch
        {
            Resolution.Applied => RuntimeActionRecordStatus.Succeeded,
            Resolution.Unknown => RuntimeActionRecordStatus.Uncertain,
            Resolution.NotApplied when original.ReplayPolicy != ReplayPolicy.Never =>
                RuntimeActionRecordStatus.Pending,
            Resolution.NotApplied => RuntimeActionRecordStatus.Failed,
            _ => throw new RuntimeRunSessionIdentityException(),
        };
        ReceiptOutcome expectedOutcome = reconciliation.Resolution switch
        {
            Resolution.Applied => ReceiptOutcome.Succeeded,
            Resolution.NotApplied => ReceiptOutcome.Rejected,
            Resolution.Unknown => ReceiptOutcome.Unknown,
            _ => throw new RuntimeRunSessionIdentityException(),
        };
        Receipt receipt = reconciled.Receipt ?? throw new RuntimeRunSessionIdentityException();
        if (reconciled.Status != expectedStatus
            || !string.Equals(reconciled.OperationId, original.OperationId, StringComparison.Ordinal)
            || !string.Equals(reconciled.IntentFingerprint, original.IntentFingerprint, StringComparison.Ordinal)
            || !string.Equals(reconciled.PayloadFingerprint, original.PayloadFingerprint, StringComparison.Ordinal)
            || !string.Equals(reconciled.Attempt, original.Attempt, StringComparison.Ordinal)
            || reconciled.GoalProjectionSettlement is not null
            || !string.Equals(receipt.ClaimToken, original.Claim.ClaimToken, StringComparison.Ordinal)
            || !string.Equals(receipt.Attempt, original.Claim.Attempt, StringComparison.Ordinal)
            || receipt.Outcome != expectedOutcome
            || !SameJson(receipt.Result, reconciliation.Result, jsonOptions)
            || !SameJson(receipt.ModelResult, reconciliation.ModelResult, jsonOptions)
            || !SameAuthority(receipt.AuthorityAfter, reconciliation.AuthorityAfter))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    internal static void ValidateActorIdentity(string sessionId, string actorId)
    {
        if (!IsOpaqueIdentifier(sessionId) || !IsOpaqueIdentifier(actorId))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateActionRecord(
        RuntimeActionRecord action,
        string expectedSessionId,
        string expectedActorId,
        string? expectedRunId,
        JsonSerializerOptions jsonOptions)
    {
        RuntimeActionRecordCoordinate coordinate = action.Coordinate ?? throw new RuntimeRunSessionIdentityException();
        if (!IsOpaqueIdentifier(action.ActionName)
            || !IsOpaqueIdentifier(action.OperationKey)
            || !IsOpaqueIdentifier(coordinate.RunId)
            || !string.Equals(action.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(action.ActorId, expectedActorId, StringComparison.Ordinal)
            || !string.Equals(coordinate.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(coordinate.ActorId, expectedActorId, StringComparison.Ordinal)
            || expectedRunId is not null && !string.Equals(coordinate.RunId, expectedRunId, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ParseDecimal(coordinate.Turn);
        RuntimeActionRecordExpectedAuthority authority = action.ExpectedAuthority
            ?? throw new RuntimeRunSessionIdentityException();
        ValidateAuthority(authority.TimelineId, authority.Generation, authority.Revision);
        ParseDecimal(action.Attempt);
        ValidateCanonicalTimestamp(action.CreatedAt);
        ValidateCanonicalTimestamp(action.UpdatedAt);
        bool requiresClaim = action.Status is RuntimeActionRecordStatus.Claimed or RuntimeActionRecordStatus.Uncertain;
        if (requiresClaim != (action.Claim is not null)) throw new RuntimeRunSessionIdentityException();
        if (action.Claim is not null)
        {
            if (action.Attempt == "0"
                || !string.Equals(action.Claim.Attempt, action.Attempt, StringComparison.Ordinal)
                || !IsOpaqueIdentifier(action.Claim.ClaimToken)
                || !IsOpaqueIdentifier(action.Claim.ClaimantId))
            {
                throw new RuntimeRunSessionIdentityException();
            }
            ValidateCanonicalTimestamp(action.Claim.ClaimedAt);
            ValidateCanonicalTimestamp(action.Claim.ExpiresAt);
            if (string.CompareOrdinal(action.Claim.ExpiresAt, action.Claim.ClaimedAt) <= 0)
            {
                throw new RuntimeRunSessionIdentityException();
            }
        }
        ValidateConflictKeys(action.ConflictKeys);
        ValidateGoalProjection(action);
        ValidateActionReceipt(action, jsonOptions);

        string operationId = "act_" + CanonicalJson.Fingerprint(
            new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2",
                ["coordinate"] = action.Coordinate,
                ["actionName"] = action.ActionName,
                ["operationKey"] = action.OperationKey,
            },
            jsonOptions);
        var intent = new Dictionary<string, object?>
        {
            ["protocolVersion"] = "2",
            ["coordinate"] = action.Coordinate,
            ["actionName"] = action.ActionName,
            ["operationKey"] = action.OperationKey,
            ["payload"] = action.Payload,
            ["expectedAuthority"] = action.ExpectedAuthority,
            ["conflictKeys"] = action.ConflictKeys,
            ["replayPolicy"] = action.ReplayPolicy,
        };
        if (action.GoalProjection is not null) intent["goalProjection"] = action.GoalProjection;
        if (!string.Equals(action.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(action.PayloadFingerprint, CanonicalJson.Fingerprint(action.Payload, jsonOptions), StringComparison.Ordinal)
            || !string.Equals(action.IntentFingerprint, CanonicalJson.Fingerprint(intent, jsonOptions), StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateConflictKeys(string[]? conflictKeys)
    {
        if (conflictKeys is null) throw new RuntimeRunSessionIdentityException();
        string? previous = null;
        foreach (string key in conflictKeys)
        {
            if (!IsOpaqueIdentifier(key) || previous is not null && string.CompareOrdinal(previous, key) >= 0)
            {
                throw new RuntimeRunSessionIdentityException();
            }
            previous = key;
        }
    }

    private static void ValidateGoalProjection(RuntimeActionRecord action)
    {
        GoalProjection? projection = action.GoalProjection;
        if (projection is null) return;
        var partition = projection.Partition ?? throw new RuntimeRunSessionIdentityException();
        if (!IsOpaqueIdentifier(projection.GoalId)
            || !IsOpaqueIdentifier(projection.StepId)
            || !IsOpaqueIdentifier(projection.InputId)
            || !IsOpaqueIdentifier(partition.OwnerId)
            || !IsOpaqueIdentifier(partition.TimelineId)
            || partition.SessionId is not null && !IsOpaqueIdentifier(partition.SessionId)
            || partition.ActorId is not null && !IsOpaqueIdentifier(partition.ActorId))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ParseDecimal(projection.ExpectedRevision);
        ParseDecimal(partition.Generation);
        if (!string.Equals(partition.TimelineId, action.ExpectedAuthority.TimelineId, StringComparison.Ordinal)
            || !string.Equals(partition.Generation, action.ExpectedAuthority.Generation, StringComparison.Ordinal)
            || partition.SessionId is not null && !string.Equals(partition.SessionId, action.SessionId, StringComparison.Ordinal)
            || partition.ActorId is not null && !string.Equals(partition.ActorId, action.ActorId, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateActionReceipt(RuntimeActionRecord action, JsonSerializerOptions jsonOptions)
    {
        Receipt? receipt = action.Receipt;
        if (receipt is null) return;
        if (!string.Equals(receipt.OperationId, action.OperationId, StringComparison.Ordinal)
            || !IsOpaqueIdentifier(receipt.ClaimToken)
            || ParseDecimal(receipt.Attempt).IsZero)
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ValidateCanonicalTimestamp(receipt.ReceivedAt);
        if (receipt.AuthorityAfter is not null)
        {
            ValidateAuthority(
                receipt.AuthorityAfter.TimelineId,
                receipt.AuthorityAfter.Generation,
                receipt.AuthorityAfter.Revision);
        }
        if (receipt.Result is not null) CanonicalJson.Serialize(receipt.Result, jsonOptions);
        if (receipt.ModelResult is not null) CanonicalJson.Serialize(receipt.ModelResult, jsonOptions);
    }

    private static bool SameJson(object? left, object? right, JsonSerializerOptions jsonOptions) =>
        left is null
            ? right is null
            : right is not null
                && string.Equals(
                    CanonicalJson.Serialize(left, jsonOptions),
                    CanonicalJson.Serialize(right, jsonOptions),
                    StringComparison.Ordinal);

    private static bool SameAuthority(
        ReceiptAuthorityAfter? left,
        RuntimeActionReconcileRequestAuthorityAfter? right) =>
        left is null
            ? right is null
            : right is not null
                && string.Equals(left.TimelineId, right.TimelineId, StringComparison.Ordinal)
                && string.Equals(left.Generation, right.Generation, StringComparison.Ordinal)
                && string.Equals(left.Revision, right.Revision, StringComparison.Ordinal);

    private static void ValidateAuthority(string timelineId, string generation, string revision)
    {
        if (!IsOpaqueIdentifier(timelineId)) throw new RuntimeRunSessionIdentityException();
        ParseDecimal(generation);
        ParseDecimal(revision);
    }

    internal static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(character => character is not (< '\u0020' or >= '\u007f' and <= '\u009f'));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void CancelAndObserve(CancellationTokenSource cancellation, Task abandoned)
    {
        _ = abandoned.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Task cancellationTask;
        try
        {
            cancellationTask = cancellation.CancelAsync();
        }
        catch
        {
            cancellation.Dispose();
            return;
        }
        _ = cancellationTask.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted) _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ValidateEventFrame(RuntimeSseFrame frame)
    {
        switch (frame.Type)
        {
            case RuntimeSseFrameType.Event when frame.Event is not null && frame.Gap is null && frame.Revision is null:
                return;
            case RuntimeSseFrameType.Cursor when frame.Event is null && frame.Gap is null && frame.Revision is not null:
                ParseDecimal(frame.Revision);
                return;
            case RuntimeSseFrameType.Gap when frame.Event is null && frame.Gap is not null && frame.Revision is null:
                if (frame.Gap.Kind != GapKind.Gap || !frame.Gap.RequiresTranscriptReconciliation)
                {
                    throw new RuntimeRunSessionIdentityException();
                }
                ParseDecimal(frame.Gap.RequestedAfterRevision);
                if (frame.Gap.EarliestAvailableRevision is not null)
                {
                    ParseDecimal(frame.Gap.EarliestAvailableRevision);
                }
                ParseDecimal(frame.Gap.NextAfterRevision);
                return;
            default:
                throw new RuntimeRunSessionIdentityException();
        }
    }

    internal static void ValidateCanonicalTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp)
            || !string.Equals(
                timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private void ValidateApproval(RuntimeApproval approval)
    {
        ApprovalRequestIdentity request = ValidateApprovalRequest(approval.Request);
        ValidateApprovalRecord(
            approval.Status,
            approval.Revision,
            approval.UpdatedAt,
            approval.Decision is null
                ? null
                : new ApprovalDecisionIdentity(
                    approval.Decision.Decision,
                    approval.Decision.ReviewerId,
                    approval.Decision.DecidedAt),
            approval.ConsumedAt,
            request);
    }

    private ApprovalRequestIdentity ValidateApprovalRequest(Request request)
    {
        RequestCoordinate coordinate = request?.Coordinate ?? throw new RuntimeRunSessionIdentityException();
        RequestExpectedAuthority authority = request.ExpectedAuthority ?? throw new RuntimeRunSessionIdentityException();
        return ValidateApprovalRequest(new ApprovalRequestIdentity(
            request.SessionId,
            request.ActorId,
            coordinate.SessionId,
            coordinate.ActorId,
            coordinate.RunId,
            coordinate.Turn,
            request.ApprovalId,
            request.RequestFingerprint,
            request.PolicyId,
            request.OperationId,
            request.IntentFingerprint,
            request.ActionName,
            authority.TimelineId,
            authority.Generation,
            authority.Revision,
            request.TaskId,
            RuntimeClient.HasPresentation(request),
            request.Presentation,
            request.RequestedAt,
            request.ExpiresAt));
    }

    private ApprovalRequestIdentity ValidateApprovalRequest(ApprovalRequestIdentity request)
    {
        if (!IsOpaqueIdentifier(request.SessionId)
            || !IsOpaqueIdentifier(request.ActorId)
            || !IsOpaqueIdentifier(request.CoordinateSessionId)
            || !IsOpaqueIdentifier(request.CoordinateActorId)
            || !IsOpaqueIdentifier(request.CoordinateRunId)
            || !IsApprovalId(request.ApprovalId)
            || !IsSha256(request.RequestFingerprint)
            || !IsOpaqueIdentifier(request.PolicyId)
            || !IsActionOperationId(request.OperationId)
            || !IsSha256(request.IntentFingerprint)
            || !IsOpaqueIdentifier(request.ActionName)
            || request.TaskId is not null && !IsOpaqueIdentifier(request.TaskId)
            || !string.Equals(request.SessionId, Accepted.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.ActorId, Accepted.ActorId, StringComparison.Ordinal)
            || !SameCoordinate(
                request.CoordinateSessionId,
                request.CoordinateActorId,
                request.CoordinateRunId))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ParseDecimal(request.CoordinateTurn);
        ValidateAuthority(request.TimelineId, request.AuthorityGeneration, request.AuthorityRevision);
        ValidateCanonicalTimestamp(request.RequestedAt);
        ValidateCanonicalTimestamp(request.ExpiresAt);
        if (string.CompareOrdinal(request.ExpiresAt, request.RequestedAt) <= 0)
        {
            throw new RuntimeRunSessionIdentityException();
        }
        if (request.Presentation is not null) CanonicalJson.Serialize(request.Presentation, jsonOptions);

        string expectedFingerprint = ApprovalRequestFingerprint(request);
        string expectedApprovalId = "apr_" + CanonicalJson.Fingerprint(
            new Dictionary<string, object?>
            {
                ["protocolVersion"] = "1",
                ["operationId"] = request.OperationId,
                ["requestFingerprint"] = request.RequestFingerprint,
            },
            jsonOptions);
        if (!string.Equals(expectedFingerprint, request.RequestFingerprint, StringComparison.Ordinal)
            || !string.Equals(expectedApprovalId, request.ApprovalId, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        return request;
    }

    private string ApprovalRequestFingerprint(ApprovalRequestIdentity request) =>
        CanonicalJson.Fingerprint(
            new Dictionary<string, object?>
            {
                ["protocolVersion"] = "3",
                ["coordinate"] = new Dictionary<string, object?>
                {
                    ["sessionId"] = request.CoordinateSessionId,
                    ["actorId"] = request.CoordinateActorId,
                    ["runId"] = request.CoordinateRunId,
                    ["turn"] = request.CoordinateTurn,
                },
                ["policyId"] = request.PolicyId,
                ["operationId"] = request.OperationId,
                ["intentFingerprint"] = request.IntentFingerprint,
                ["actionName"] = request.ActionName,
                ["expectedAuthority"] = new Dictionary<string, object?>
                {
                    ["timelineId"] = request.TimelineId,
                    ["generation"] = request.AuthorityGeneration,
                    ["revision"] = request.AuthorityRevision,
                },
                ["taskId"] = request.TaskId,
                ["presentationPresent"] = request.PresentationPresent,
                ["presentation"] = request.Presentation,
            },
            jsonOptions);

    private static void ValidateApprovalRecord(
        RuntimeApprovalStatus status,
        string revision,
        string updatedAt,
        ApprovalDecisionIdentity? decision,
        string? consumedAt,
        ApprovalRequestIdentity request)
    {
        if (ParseDecimal(revision).IsZero) RejectIdentity();
        ValidateCanonicalTimestamp(updatedAt);
        if (decision is not null)
        {
            if (!IsOpaqueIdentifier(decision.ReviewerId)) RejectIdentity();
            ValidateCanonicalTimestamp(decision.DecidedAt);
            if (string.CompareOrdinal(decision.DecidedAt, request.RequestedAt) < 0
                || string.CompareOrdinal(decision.DecidedAt, request.ExpiresAt) >= 0)
            {
                RejectIdentity();
            }
        }
        if (status == RuntimeApprovalStatus.Pending && decision is not null
            || status is RuntimeApprovalStatus.Approved or RuntimeApprovalStatus.Rejected or RuntimeApprovalStatus.Consumed && decision is null
            || status == RuntimeApprovalStatus.Approved && decision?.Decision != DecisionDecision.Approved
            || status == RuntimeApprovalStatus.Rejected && decision?.Decision != DecisionDecision.Rejected
            || status is RuntimeApprovalStatus.Approved or RuntimeApprovalStatus.Rejected
                && !string.Equals(updatedAt, decision?.DecidedAt, StringComparison.Ordinal)
            || status == RuntimeApprovalStatus.Consumed && decision?.Decision != DecisionDecision.Approved
            || status == RuntimeApprovalStatus.Expired && !string.Equals(updatedAt, request.ExpiresAt, StringComparison.Ordinal)
            || (status == RuntimeApprovalStatus.Consumed) != (consumedAt is not null))
        {
            RejectIdentity();
        }
        if (consumedAt is not null)
        {
            ValidateCanonicalTimestamp(consumedAt);
            if (decision is null
                || string.CompareOrdinal(consumedAt, decision.DecidedAt) < 0
                || string.CompareOrdinal(consumedAt, request.ExpiresAt) >= 0
                || !string.Equals(updatedAt, consumedAt, StringComparison.Ordinal))
            {
                RejectIdentity();
            }
        }
    }

    private static bool IsApprovalId(string? value) =>
        value is { Length: 68 }
        && value.StartsWith("apr_", StringComparison.Ordinal)
        && IsSha256(value[4..]);

    private static bool IsActionOperationId(string? value) =>
        value is { Length: 68 }
        && value.StartsWith("act_", StringComparison.Ordinal)
        && IsSha256(value[4..]);

    private bool SameCoordinate(string sessionId, string actorId, string runId) =>
        string.Equals(sessionId, Accepted.SessionId, StringComparison.Ordinal)
        && string.Equals(actorId, Accepted.ActorId, StringComparison.Ordinal)
        && string.Equals(runId, Accepted.RunId, StringComparison.Ordinal);

    private void ValidateControlResult(RuntimeControlResult result, string expectedTurn)
    {
        if (result.Accepted)
        {
            RuntimeControlResultCoordinate coordinate = result.Coordinate
                ?? throw new RuntimeRunSessionIdentityException();
            if (result.Reason is not null
                || result.ActiveCoordinate is not null
                || !SameCoordinate(coordinate.SessionId, coordinate.ActorId, coordinate.RunId)
                || !string.Equals(coordinate.Turn, expectedTurn, StringComparison.Ordinal))
            {
                throw new RuntimeRunSessionIdentityException();
            }
            return;
        }
        if (result.Coordinate is not null || result.Reason is null)
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ActiveCoordinate? active = result.ActiveCoordinate;
        switch (result.Reason.Value)
        {
            case RuntimeControlResultReason.NoActiveRun when active is null:
                return;
            case RuntimeControlResultReason.RunNotActive when active is not null:
                if (SameCoordinate(active.SessionId, active.ActorId, active.RunId)
                    && string.Equals(active.Turn, expectedTurn, StringComparison.Ordinal))
                {
                    return;
                }
                break;
            case RuntimeControlResultReason.CoordinateMismatch when active is not null:
                if (SameCoordinate(active.SessionId, active.ActorId, active.RunId)
                    && !string.Equals(active.Turn, expectedTurn, StringComparison.Ordinal))
                {
                    return;
                }
                break;
        }
        throw new RuntimeRunSessionIdentityException();
    }

    private T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, jsonOptions), jsonOptions)
        ?? throw new JsonException("Runtime session value serialized as JSON null.");

    private void ThrowCancellationReason()
    {
        Exception? reason;
        lock (stateGate)
        {
            reason = closeReason;
        }
        if (reason is not null) throw reason;
        callerCancellation.ThrowIfCancellationRequested();
    }

    private void CancelLifetime()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    private void ValidateStream(FluffyCoordinate? coordinate)
    {
        if (coordinate is null
            || !string.Equals(coordinate.SessionId, Accepted.SessionId, StringComparison.Ordinal)
            || !string.Equals(coordinate.ActorId, Accepted.ActorId, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
    }

    private static void ValidateAccepted(RuntimeRunAccepted? accepted, RuntimeRunRequest request)
    {
        if (accepted?.InitialCoordinate is null
            || !IsOpaqueIdentifier(request.SessionId)
            || !IsOpaqueIdentifier(request.ActorId)
            || !IsOpaqueIdentifier(request.InputId)
            || !IsOpaqueIdentifier(accepted.RunId)
            || !IsOpaqueIdentifier(accepted.InitialCoordinate.SessionId)
            || !IsOpaqueIdentifier(accepted.InitialCoordinate.ActorId)
            || !IsOpaqueIdentifier(accepted.InitialCoordinate.RunId)
            || !string.Equals(accepted.RunId, accepted.InitialCoordinate.RunId, StringComparison.Ordinal)
            || !string.Equals(request.SessionId, accepted.InitialCoordinate.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.ActorId, accepted.InitialCoordinate.ActorId, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        ParseDecimal(accepted.EventCursor);
        ParseDecimal(accepted.InitialCoordinate.Turn);
    }

    internal static BigInteger ParseDecimal(string value)
    {
        if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger parsed)
            || parsed.Sign < 0
            || !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new RuntimeRunSessionIdentityException();
        }
        return parsed;
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > RuntimeClient.MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    [Flags]
    private enum EventDataFields
    {
        None = 0,
        AdmissionAuthority = 1 << 21,
        Action = 1 << 0,
        Audience = 1 << 1,
        ConsecutiveCount = 1 << 2,
        Content = 1 << 3,
        Control = 1 << 4,
		Counts = 1 << 17,
        Delta = 1 << 5,
		Durations = 1 << 18,
        FinishReason = 1 << 6,
        InputFingerprint = 1 << 7,
        InputId = 1 << 8,
        Item = 1 << 9,
        ItemId = 1 << 10,
        OperationId = 1 << 11,
		SegmentStatus = 1 << 19,
        Status = 1 << 12,
        ToolCallId = 1 << 13,
        ToolName = 1 << 14,
        ToolResultItemIds = 1 << 15,
		Unit = 1 << 20,
        UpdateOrdinal = 1 << 16,
    }

    private sealed record PendingApproval(RuntimeApproval Approval, string CanonicalRequest);

    private sealed record ApprovalDecisionIdentity(
        DecisionDecision Decision,
        string ReviewerId,
        string DecidedAt);

    private sealed record ApprovalRequestIdentity(
        string SessionId,
        string ActorId,
        string CoordinateSessionId,
        string CoordinateActorId,
        string CoordinateRunId,
        string CoordinateTurn,
        string ApprovalId,
        string RequestFingerprint,
        string PolicyId,
        string OperationId,
        string IntentFingerprint,
        string ActionName,
        string TimelineId,
        string AuthorityGeneration,
        string AuthorityRevision,
        string? TaskId,
        bool PresentationPresent,
        object? Presentation,
        string RequestedAt,
        string ExpiresAt);
}

internal static class CanonicalJson
{
    public static string Fingerprint(object payload, JsonSerializerOptions options)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(payload, options)));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string Serialize(object payload, JsonSerializerOptions options)
    {
        JsonElement element = payload is JsonElement json
            ? json.Clone()
            : JsonSerializer.SerializeToElement(payload, options);
        var builder = new StringBuilder();
        AppendValue(builder, element);
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                builder.Append("null");
                return;
            case JsonValueKind.True:
                builder.Append("true");
                return;
            case JsonValueKind.False:
                builder.Append("false");
                return;
            case JsonValueKind.String:
                AppendString(builder, value.GetString()!);
                return;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(value));
                return;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    AppendValue(builder, item);
                }
                builder.Append(']');
                return;
            case JsonValueKind.Object:
                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject()) properties[property.Name] = property.Value;
                builder.Append('{');
                bool firstProperty = true;
                foreach (KeyValuePair<string, JsonElement> property in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(',');
                    firstProperty = false;
                    AppendString(builder, property.Key);
                    builder.Append(':');
                    AppendValue(builder, property.Value);
                }
                builder.Append('}');
                return;
            default:
                throw new JsonException("Runtime payload is not a JSON value.");
        }
    }

    private static string FormatNumber(JsonElement value)
    {
        if (!value.TryGetDouble(out double number)
            || !double.IsFinite(number)
            || number == 0d && BitConverter.DoubleToInt64Bits(number) < 0)
        {
            throw new JsonException("Runtime payload contains an unsupported number.");
        }
        if (number == 0d) return "0";
        string roundTrip = number.ToString("R", CultureInfo.InvariantCulture);
        bool negative = roundTrip[0] == '-';
        ReadOnlySpan<char> unsigned = negative ? roundTrip.AsSpan(1) : roundTrip.AsSpan();
        int exponentMarker = unsigned.IndexOfAny('E', 'e');
        ReadOnlySpan<char> significand = exponentMarker < 0 ? unsigned : unsigned[..exponentMarker];
        int exponent = exponentMarker < 0
            ? 0
            : int.Parse(unsigned[(exponentMarker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int decimalPoint = significand.IndexOf('.');
        int decimalPosition = decimalPoint < 0 ? significand.Length : decimalPoint;
        string digits = significand.ToString().Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        int removedLeading = significand.Length - (decimalPoint < 0 ? 0 : 1) - digits.Length;
        int n = decimalPosition - removedLeading + exponent;
        digits = digits.TrimEnd('0');
        string formatted;
        if (digits.Length <= n && n <= 21)
        {
            formatted = digits + new string('0', n - digits.Length);
        }
        else if (0 < n && n <= 21)
        {
            formatted = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            formatted = "0." + new string('0', -n) + digits;
        }
        else
        {
            string coefficient = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            int scientificExponent = n - 1;
            formatted = coefficient + "e" + (scientificExponent >= 0 ? "+" : string.Empty)
                + scientificExponent.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + formatted : formatted;
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (char.IsHighSurrogate(character)
                        && index + 1 < value.Length
                        && char.IsLowSurrogate(value[index + 1]))
                    {
                        builder.Append(character).Append(value[++index]);
                    }
                    else if (character < ' ' || char.IsSurrogate(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
