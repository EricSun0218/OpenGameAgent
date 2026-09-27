using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Godot;
using OpenGameAgent.Client;
using OpenGameAgent.Protocol;

namespace OpenGameAgent.Godot;

public partial class OpenGameAgentRuntimeNode : Node
{
    private static readonly JsonSerializerOptions ProtocolJson = RuntimeProtocolJson.CreateSerializerOptions();

    [Signal]
    public delegate void RuntimeFailureEventHandler(string message);

    [Export]
    public string BaseUrl { get; set; } = string.Empty;

    [Export]
    public string AccessToken { get; set; } = string.Empty;

    [Export]
    public string SessionId { get; set; } = string.Empty;

    [Export]
    public string ActorId { get; set; } = string.Empty;

    [Export]
    public NodePath AuthoritativeAdapterPath { get; set; } = new();

    [Export]
    public long MaxRequestBytes { get; set; }

    [Export]
    public long MaxHttpResponseBytes { get; set; }

    [Export]
    public long MaxAttachmentResponseBytes { get; set; }

    [Export]
    public long MaxSseEventBytes { get; set; }

    [Export]
    public int MaxQueuedActions { get; set; }

    [Export]
    public long ExecutionDurationMilliseconds { get; set; }

    [Export]
    public global::Godot.Collections.Array<int> DeliveryRetryDelayMilliseconds { get; set; } = new();

    [Export]
    public int RetryDefaultDelayMilliseconds { get; set; }

    [Export]
    public int RetryMaximumDelayMilliseconds { get; set; }

    [Export]
    public int MaximumConnectionAttempts { get; set; }

    private readonly ConcurrentQueue<PumpEvent> pumpEvents = new();
    private readonly CancellationTokenSource lifetime = new();
    private GodotActionDelivery? activeDelivery;
    private long activeEpoch;
    private TimeSpan activeExecutionDuration;
    private long activeStartedAt;
    private IGodotAuthoritativeActionAdapter? authoritativeAdapter;
    private string actorId = string.Empty;
    private int[] deliveryRetryDelays = [];
    private Channel<ActionChannelItem>? deliveries;
    private CancellationTokenSource? executionCancellation;
    private TimeSpan executionDuration;
    private long nextEpoch;
    private int mainThreadId;
    private ActionFence? lastAuthoritativeConflictFence;
    private RuntimeClient? runtimeClient;
    private volatile bool recoveryComplete;
    private string sessionId = string.Empty;
    private int state;

    public override void _Ready()
    {
        ValidateConfiguration();
        mainThreadId = System.Environment.CurrentManagedThreadId;
        sessionId = SessionId;
        actorId = ActorId;
        executionDuration = TimeSpan.FromMilliseconds(ExecutionDurationMilliseconds);
        deliveryRetryDelays = [.. DeliveryRetryDelayMilliseconds];
        authoritativeAdapter = GetNode<Node>(AuthoritativeAdapterPath) as IGodotAuthoritativeActionAdapter
            ?? throw new InvalidOperationException("The configured Godot node is not an authoritative action adapter.");
        deliveries = Channel.CreateBounded<ActionChannelItem>(new BoundedChannelOptions(MaxQueuedActions)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        runtimeClient = new RuntimeClient(new RuntimeClientOptions
        {
            BaseAddress = new Uri(BaseUrl, UriKind.Absolute),
            MaxRequestBytes = MaxRequestBytes,
            MaxHttpResponseBytes = MaxHttpResponseBytes,
            MaxAttachmentResponseBytes = MaxAttachmentResponseBytes,
            MaxSseEventBytes = MaxSseEventBytes,
            AccessTokenProvider = string.IsNullOrEmpty(AccessToken)
                ? null
                : _ => ValueTask.FromResult<string?>(AccessToken),
        });
        SetState(ActionChannelState.Idle);
        _ = RecoverOrStreamActionsAsync(lifetime.Token);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (State is ActionChannelState.Stopped or ActionChannelState.Halted) return;

        while (pumpEvents.TryDequeue(out PumpEvent? pumpEvent)) ProcessPumpEvent(pumpEvent);
        if (State == ActionChannelState.Executing && ExecutionExpired()) BeginExecutionInspection();
        if (State != ActionChannelState.Idle
            || deliveries is null
            || !deliveries.Reader.TryRead(out ActionChannelItem? item))
        {
            return;
        }

        try
        {
            if (item.Failure is not null)
            {
                Halt(item.Failure);
                return;
            }
            GodotActionDelivery delivery = Normalize(item.Frame!.Delivery);
            if (delivery.SessionId != sessionId || delivery.ActorId != actorId)
            {
                Halt("The Runtime action delivery does not belong to this action pump.");
                return;
            }
            activeDelivery = delivery;
            lastAuthoritativeConflictFence = null;
            activeEpoch = ++nextEpoch;
            if (delivery.Kind == RuntimeActionDeliveryKind.Reconcile) BeginInspection();
            else BeginExecution();
        }
        catch (Exception exception)
        {
            Halt(exception.Message);
        }
    }

    public override void _ExitTree()
    {
        SetState(ActionChannelState.Stopped);
        CancelExecution();
        lifetime.Cancel();
        runtimeClient?.Dispose();
        lifetime.Dispose();
    }

    public Task<byte[]> ReadAttachmentAsync(
        RuntimeAttachmentReadRequest request,
        CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<byte[]>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.ReadAttachmentAsync(request, cancellationToken);
    }

    public bool IsActionPumpHalted => State == ActionChannelState.Halted;

    public Task<RuntimeAuthorityClosure> CloseAuthorityAsync(
        string scope,
        string generation,
        string operationId,
        RuntimeAuthorityGoalRetirement? goalRetirement = null,
        RuntimeAuthorityWorkRetirement? workRetirement = null,
        CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<RuntimeAuthorityClosure>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.CloseAuthorityAsync(new RuntimeAuthorityCloseRequest
            {
                SessionId = sessionId,
                ActorId = actorId,
                Scope = scope,
                Generation = generation,
                OperationId = operationId,
                GoalRetirement = goalRetirement,
                WorkRetirement = workRetirement,
            }, cancellationToken);
    }

    public Task<RuntimeAuthorityReadResponse> ReadAuthorityClosureAsync(
        string scope,
        string generation,
        CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<RuntimeAuthorityReadResponse>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.ReadAuthorityClosureAsync(new RuntimeAuthorityReadRequest
            {
                SessionId = sessionId,
                ActorId = actorId,
                Scope = scope,
                Generation = generation,
            }, cancellationToken);
    }

    public Task<RuntimeDeferredReadResponse> ReadDeferredAsync(CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<RuntimeDeferredReadResponse>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.ReadDeferredAsync(
                new RuntimeDeferredReadRequest
                {
                    SessionId = sessionId,
                    ActorId = actorId,
                },
                cancellationToken);
    }

    public Task<RuntimeRunAccepted> ResumeDeferredAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<RuntimeRunAccepted>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.ResumeDeferredAsync(
                new RuntimeDeferredCommandRequest
                {
                    SessionId = sessionId,
                    ActorId = actorId,
                    OperationId = operationId,
                },
                cancellationToken);
    }

    public Task<RuntimeDeferredAbandonResponse> AbandonDeferredAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return runtimeClient is null
            ? Task.FromException<RuntimeDeferredAbandonResponse>(new InvalidOperationException("The Runtime client is not ready."))
            : runtimeClient.AbandonDeferredAsync(
                new RuntimeDeferredCommandRequest
                {
                    SessionId = sessionId,
                    ActorId = actorId,
                    OperationId = operationId,
                },
                cancellationToken);
    }

    private void BeginExecution()
    {
        GodotActionDelivery delivery = activeDelivery
            ?? throw new InvalidOperationException("The action channel has no active delivery.");
        activeStartedAt = Stopwatch.GetTimestamp();
        DateTimeOffset claimExpiry = DateTimeOffset.Parse(
            delivery.ClaimExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        TimeSpan claimRemaining = claimExpiry - DateTimeOffset.UtcNow;
        activeExecutionDuration = claimRemaining < executionDuration ? claimRemaining : executionDuration;
        if (activeExecutionDuration <= TimeSpan.Zero)
        {
            BeginInspection();
            return;
        }

        SetState(ActionChannelState.Executing);
        executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var context = new GodotActionExecutionContext(
            activeEpoch,
            executionCancellation.Token,
            transaction => TryCommit(activeEpoch, transaction),
            completion => QueueExecutionCompletion(activeEpoch, completion));
        try
        {
            authoritativeAdapter!.Execute(delivery, context);
        }
        catch (Exception exception)
        {
            BeginExecutionInspection(exception.Message);
        }
    }

    private bool TryCommit(long epoch, Func<GodotActionCompletion?> transaction)
    {
        if (System.Environment.CurrentManagedThreadId != mainThreadId
            || State != ActionChannelState.Executing
            || activeEpoch != epoch
            || ExecutionExpired()
            || !authoritativeAdapter!.RecheckExpectedAuthority(activeDelivery!)
            || activeEpoch != epoch
            || ExecutionExpired()
            || !TryTransitionState(ActionChannelState.Executing, ActionChannelState.Committing))
        {
            return false;
        }

        GodotActionCompletion? completion;
        try
        {
            completion = transaction();
            if (completion is not null) completion = Snapshot(completion);
        }
        catch
        {
            _ = TryTransitionState(ActionChannelState.Committing, ActionChannelState.Executing);
            throw;
        }
        if (completion is null)
        {
            if (TryTransitionState(ActionChannelState.Committing, ActionChannelState.InspectionQueued))
            {
                pumpEvents.Enqueue(PumpEvent.ExecutionUncertain(
                    epoch,
                    "The game transaction returned no action completion after its world write."));
            }
            return true;
        }
        if (TryTransitionState(ActionChannelState.Committing, ActionChannelState.CompletionQueued))
        {
            pumpEvents.Enqueue(PumpEvent.ExecutionCompleted(epoch, completion));
        }
        return true;
    }

    private bool QueueExecutionCompletion(long epoch, GodotActionCompletion completion)
    {
        if (activeEpoch != epoch) return false;
        GodotActionCompletion snapshot = Snapshot(completion);
        long completedAt = Stopwatch.GetTimestamp();
        if (activeEpoch != epoch || ExecutionExpiredAt(completedAt)) return false;
        if (!TryTransitionState(ActionChannelState.Executing, ActionChannelState.CompletionQueued)) return false;
        pumpEvents.Enqueue(PumpEvent.ExecutionCompleted(epoch, snapshot));
        return true;
    }

    private void BeginExecutionInspection(string? failure = null)
    {
        if (!TryTransitionState(ActionChannelState.Executing, ActionChannelState.Inspecting)) return;
        if (failure is not null) EmitFailure(failure);
        BeginInspection();
    }

    private void BeginInspection()
    {
        CancelExecution();
        SetState(ActionChannelState.Inspecting);
        try
        {
            GodotActionInspection inspection = Snapshot(
                authoritativeAdapter!.Inspect(activeDelivery!)
                    ?? throw new InvalidOperationException("The authoritative action adapter returned no inspection result."));
            var request = new RuntimeActionReconcileRequest
            {
                SessionId = activeDelivery!.SessionId,
                ActorId = activeDelivery.ActorId,
                OperationId = activeDelivery.OperationId,
                ClaimToken = activeDelivery.ClaimToken,
                Attempt = activeDelivery.Attempt,
                Resolution = inspection.Resolution,
                Result = inspection.Result,
                ModelResult = inspection.ModelResult,
                AuthorityAfter = inspection.AuthorityAfter is null
                    ? null
                    : new RuntimeActionReconcileRequestAuthorityAfter
                    {
                        TimelineId = inspection.AuthorityAfter.TimelineId,
                        Generation = inspection.AuthorityAfter.Generation,
                        Revision = inspection.AuthorityAfter.Revision,
                    },
            };
            SetState(ActionChannelState.Reconciling);
            _ = DeliverAsync(
                activeEpoch,
                cancellationToken => runtimeClient!.ReconcileActionAsync(request, cancellationToken),
                lifetime.Token,
                inspection.Resolution == Resolution.Unknown
                    ? "The accepted action reconciliation remains unresolved."
                    : null);
        }
        catch (Exception exception)
        {
            Halt(exception.Message);
        }
    }

    private void BeginReceipt(GodotActionCompletion completion)
    {
        CancelExecution();
        var request = new RuntimeActionReceiptRequest
        {
            SessionId = activeDelivery!.SessionId,
            ActorId = activeDelivery.ActorId,
            OperationId = activeDelivery.OperationId,
            ClaimToken = activeDelivery.ClaimToken,
            Attempt = activeDelivery.Attempt,
            Outcome = completion.Outcome,
            Result = completion.Result,
            ModelResult = completion.ModelResult,
            AuthorityAfter = completion.AuthorityAfter is null
                ? null
                : new RuntimeActionReceiptRequestAuthorityAfter
                {
                    TimelineId = completion.AuthorityAfter.TimelineId,
                    Generation = completion.AuthorityAfter.Generation,
                    Revision = completion.AuthorityAfter.Revision,
                },
        };
        SetState(ActionChannelState.DeliveringReceipt);
        _ = DeliverAsync(
            activeEpoch,
            cancellationToken => runtimeClient!.RecordActionReceiptAsync(request, cancellationToken),
            lifetime.Token,
            completion.Outcome == ReceiptOutcome.Unknown
                ? "The accepted action receipt remains unresolved."
                : null);
    }

    private async Task DeliverAsync(
        long epoch,
        Func<CancellationToken, Task<RuntimeActionRecordResponse>> deliver,
        CancellationToken cancellationToken,
        string? acceptedFailure = null)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                await deliver(cancellationToken);
                pumpEvents.Enqueue(acceptedFailure is null
                    ? PumpEvent.DeliverySucceeded(epoch)
                    : PumpEvent.DeliveryFailed(epoch, acceptedFailure));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RuntimeHttpException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                await ReadAfterConflictAsync(epoch, cancellationToken);
                return;
            }
            catch (Exception exception) when (IsRetryableDeliveryFailure(exception) && retry < deliveryRetryDelays.Length)
            {
                await Task.Delay(deliveryRetryDelays[retry], cancellationToken);
            }
            catch (Exception exception)
            {
                pumpEvents.Enqueue(PumpEvent.DeliveryFailed(epoch, exception.Message));
                return;
            }
        }
    }

    private async Task ReadAfterConflictAsync(long epoch, CancellationToken cancellationToken)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                RuntimeActionGetResponse response = await runtimeClient!.GetActionAsync(
                    new RuntimeActionGetRequest
                    {
                        SessionId = activeDelivery!.SessionId,
                        ActorId = activeDelivery.ActorId,
                        OperationId = activeDelivery.OperationId,
                    },
                    cancellationToken);
                RuntimeActionRecord? action = response.Action;
                if (action is null)
                {
                    pumpEvents.Enqueue(PumpEvent.DeliveryFailed(epoch, "The authoritative action read returned no action."));
                }
                else if (action.SessionId != activeDelivery!.SessionId
                    || action.ActorId != activeDelivery.ActorId
                    || action.OperationId != activeDelivery.OperationId)
                {
                    pumpEvents.Enqueue(PumpEvent.DeliveryFailed(
                        epoch,
                        "The authoritative action read returned a different action."));
                }
                else if (action.Status is RuntimeActionRecordStatus.Succeeded
                    or RuntimeActionRecordStatus.Failed
                    or RuntimeActionRecordStatus.Pending)
                {
                    pumpEvents.Enqueue(PumpEvent.DeliverySucceeded(epoch));
                }
                else
                {
                    pumpEvents.Enqueue(PumpEvent.DeliveryConflict(epoch, Normalize(action)));
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsRetryableDeliveryFailure(exception) && retry < deliveryRetryDelays.Length)
            {
                await Task.Delay(deliveryRetryDelays[retry], cancellationToken);
            }
            catch (Exception exception)
            {
                pumpEvents.Enqueue(PumpEvent.DeliveryFailed(epoch, exception.Message));
                return;
            }
        }
    }

    private void ProcessPumpEvent(PumpEvent pumpEvent)
    {
        if (pumpEvent.Epoch != activeEpoch) return;
        switch (pumpEvent.Kind)
        {
            case PumpEventKind.ExecutionUncertain when State == ActionChannelState.InspectionQueued:
                EmitFailure(pumpEvent.Error!);
                BeginInspection();
                break;
            case PumpEventKind.ExecutionCompleted when State == ActionChannelState.CompletionQueued:
                BeginReceipt(pumpEvent.Completion!);
                break;
            case PumpEventKind.DeliverySucceeded
                when State is ActionChannelState.DeliveringReceipt or ActionChannelState.Reconciling:
                ClearActive();
                SetState(ActionChannelState.Idle);
                if (!recoveryComplete) _ = RecoverOrStreamActionsAsync(lifetime.Token);
                break;
            case PumpEventKind.DeliveryConflict
                when State is ActionChannelState.DeliveringReceipt or ActionChannelState.Reconciling:
                GodotActionDelivery conflictDelivery = pumpEvent.Delivery!;
                var conflictFence = new ActionFence(conflictDelivery.ClaimToken, conflictDelivery.Attempt);
                if (lastAuthoritativeConflictFence == conflictFence)
                {
                    Halt("The authoritative action read returned an unchanged unresolved claim fence.");
                    break;
                }
                lastAuthoritativeConflictFence = conflictFence;
                activeDelivery = conflictDelivery;
                BeginInspection();
                break;
            case PumpEventKind.DeliveryFailed
                when State is ActionChannelState.DeliveringReceipt or ActionChannelState.Reconciling:
                Halt(pumpEvent.Error!);
                break;
        }
    }

    private bool ExecutionExpired() => ExecutionExpiredAt(Stopwatch.GetTimestamp());

    private bool ExecutionExpiredAt(long timestamp) =>
        Stopwatch.GetElapsedTime(activeStartedAt, timestamp) >= activeExecutionDuration;

    private void CancelExecution()
    {
        if (executionCancellation is null) return;
        executionCancellation.Cancel();
        executionCancellation.Dispose();
        executionCancellation = null;
    }

    private void ClearActive()
    {
        CancelExecution();
        activeDelivery = null;
        lastAuthoritativeConflictFence = null;
    }

    private void Halt(string message)
    {
        if (State is ActionChannelState.Stopped or ActionChannelState.Halted) return;
        CancelExecution();
        lifetime.Cancel();
        SetState(ActionChannelState.Halted);
        EmitFailure(message);
    }

    private async Task RecoverOrStreamActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (int retry = 0; ; retry++)
            {
                try
                {
                    RuntimeActionRecoveryResponse response = await runtimeClient!.RecoverActionAsync(
                        new RuntimeActionRecoveryRequest { SessionId = sessionId, ActorId = actorId },
                        cancellationToken);
                    if (response.Delivery is not null)
                    {
                        await deliveries!.Writer.WriteAsync(
                            ActionChannelItem.Delivery(new RuntimeActionSseFrame
                            {
                                Type = RuntimeActionSseFrameType.Delivery,
                                Delivery = new RuntimeActionDelivery
                                {
                                    Kind = RuntimeActionDeliveryKind.Reconcile,
                                    Action = response.Delivery.Action,
                                },
                            }),
                            cancellationToken);
                        return;
                    }
                    recoveryComplete = true;
                    await StreamActionsAsync(cancellationToken);
                    return;
                }
                catch (Exception exception)
                    when (IsRetryableDeliveryFailure(exception) && retry < deliveryRetryDelays.Length)
                {
                    await Task.Delay(deliveryRetryDelays[retry], cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await deliveries!.Writer.WriteAsync(ActionChannelItem.TerminalFailure(exception.Message), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task StreamActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new RuntimeActionNextRequest
            {
                SessionId = sessionId,
                ActorId = actorId,
            };
            var retry = new RuntimeSseRetryPolicy
            {
                DefaultDelay = TimeSpan.FromMilliseconds(RetryDefaultDelayMilliseconds),
                MaximumDelay = TimeSpan.FromMilliseconds(RetryMaximumDelayMilliseconds),
                MaximumConnectionAttempts = MaximumConnectionAttempts,
            };
            await foreach (RuntimeActionSseFrame frame in runtimeClient!
                .StreamActionsAsync(request, retry, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                await deliveries!.Writer.WriteAsync(ActionChannelItem.Delivery(frame), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await deliveries!.Writer.WriteAsync(ActionChannelItem.TerminalFailure(exception.Message), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private void EmitFailure(string message)
    {
        EmitSignal(SignalName.RuntimeFailure, message);
    }

    private void ValidateConfiguration()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("BaseUrl must be absolute.");
        if (string.IsNullOrEmpty(SessionId) || string.IsNullOrEmpty(ActorId))
        {
            throw new InvalidOperationException("SessionId and ActorId are required.");
        }

        if (MaxRequestBytes <= 0
            || MaxHttpResponseBytes <= 0
            || MaxAttachmentResponseBytes <= 0
            || MaxSseEventBytes <= 0
            || MaxQueuedActions <= 0)
        {
            throw new InvalidOperationException("Runtime byte and queue limits must be host-configured positive values.");
        }

        if (ExecutionDurationMilliseconds <= 0
            || ExecutionDurationMilliseconds > (long)TimeSpan.MaxValue.TotalMilliseconds
            || DeliveryRetryDelayMilliseconds.Any(delay => delay < 0))
        {
            throw new InvalidOperationException("Action execution and finite delivery retry policy must be host-configured and representable.");
        }

        if (RetryDefaultDelayMilliseconds <= 0
            || RetryMaximumDelayMilliseconds < RetryDefaultDelayMilliseconds
            || MaximumConnectionAttempts <= 0)
        {
            throw new InvalidOperationException("Runtime retry policy must be host-configured and internally consistent.");
        }
    }

    private static bool IsRetryableDeliveryFailure(Exception exception)
        => RuntimeFailureClassifier.Classify(exception) == RuntimeFailureDisposition.Retry;

    private static GodotActionCompletion Snapshot(GodotActionCompletion value) => new()
    {
        Outcome = value.Outcome,
        Result = SnapshotJson(value.Result),
        ModelResult = SnapshotJson(value.ModelResult),
        AuthorityAfter = Snapshot(value.AuthorityAfter),
    };

    private static GodotActionInspection Snapshot(GodotActionInspection value) => new()
    {
        Resolution = value.Resolution,
        Result = SnapshotJson(value.Result),
        ModelResult = SnapshotJson(value.ModelResult),
        AuthorityAfter = Snapshot(value.AuthorityAfter),
    };

    private static GodotAuthorityVersion? Snapshot(GodotAuthorityVersion? value) => value is null
        ? null
        : new GodotAuthorityVersion
        {
            TimelineId = value.TimelineId,
            Generation = value.Generation,
            Revision = value.Revision,
        };

    private static object? SnapshotJson(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToElement(value, value.GetType(), ProtocolJson);

    private ActionChannelState State => (ActionChannelState)Volatile.Read(ref state);

    private void SetState(ActionChannelState value) => Volatile.Write(ref state, (int)value);

    private bool TryTransitionState(ActionChannelState expected, ActionChannelState next) =>
        Interlocked.CompareExchange(ref state, (int)next, (int)expected) == (int)expected;

    private static GodotActionDelivery Normalize(RuntimeActionDelivery delivery) =>
        Normalize(delivery.Action, delivery.Kind);

    private static GodotActionDelivery Normalize(
        RuntimeActionRecord action,
        RuntimeActionDeliveryKind kind)
    {
        Claim claim = action.Claim
            ?? throw new InvalidOperationException("The Runtime action delivery did not contain a claim fence.");
        if ((kind == RuntimeActionDeliveryKind.Dispatch && action.Status != RuntimeActionRecordStatus.Claimed)
            || (kind == RuntimeActionDeliveryKind.Reconcile
                && action.Status is not (RuntimeActionRecordStatus.Claimed or RuntimeActionRecordStatus.Uncertain))
            || action.GoalProjectionSettlement is not null
            || !string.Equals(claim.Attempt, action.Attempt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Runtime action delivery violates the authoritative state boundary.");
        }
        return new GodotActionDelivery
        {
            Kind = kind,
            SessionId = action.SessionId,
            ActorId = action.ActorId,
            OperationId = action.OperationId,
            ActionName = action.ActionName,
            OperationKey = action.OperationKey,
            Payload = action.Payload,
            IntentFingerprint = action.IntentFingerprint,
            PayloadFingerprint = action.PayloadFingerprint,
            Coordinate = new GodotActionCoordinate
            {
                SessionId = action.Coordinate.SessionId,
                ActorId = action.Coordinate.ActorId,
                RunId = action.Coordinate.RunId,
                Turn = action.Coordinate.Turn,
            },
            ExpectedAuthority = new GodotAuthorityVersion
            {
                TimelineId = action.ExpectedAuthority.TimelineId,
                Generation = action.ExpectedAuthority.Generation,
                Revision = action.ExpectedAuthority.Revision,
            },
            ConflictKeys = [.. action.ConflictKeys],
            ReplayPolicy = action.ReplayPolicy,
            Status = action.Status,
            ClaimToken = claim.ClaimToken,
            Attempt = claim.Attempt,
            ClaimExpiresAt = claim.ExpiresAt,
        };
    }

    private static GodotActionDelivery Normalize(RuntimeActionRecord action) =>
        Normalize(action, RuntimeActionDeliveryKind.Reconcile);

    private enum ActionChannelState
    {
        Idle,
        Inspecting,
        Executing,
        Committing,
        InspectionQueued,
        CompletionQueued,
        DeliveringReceipt,
        Reconciling,
        Halted,
        Stopped,
    }

    private readonly record struct ActionFence(string ClaimToken, string Attempt);

    private enum PumpEventKind
    {
        ExecutionUncertain,
        ExecutionCompleted,
        DeliverySucceeded,
        DeliveryConflict,
        DeliveryFailed,
    }

    private sealed class PumpEvent
    {
        private PumpEvent(
            PumpEventKind kind,
            long epoch,
            GodotActionCompletion? completion = null,
            GodotActionDelivery? delivery = null,
            string? error = null)
        {
            Kind = kind;
            Epoch = epoch;
            Completion = completion;
            Delivery = delivery;
            Error = error;
        }

        public PumpEventKind Kind { get; }

        public long Epoch { get; }

        public GodotActionCompletion? Completion { get; }

        public GodotActionDelivery? Delivery { get; }

        public string? Error { get; }

        public static PumpEvent ExecutionCompleted(long epoch, GodotActionCompletion completion) =>
            new(PumpEventKind.ExecutionCompleted, epoch, completion: completion);

        public static PumpEvent ExecutionUncertain(long epoch, string error) =>
            new(PumpEventKind.ExecutionUncertain, epoch, error: error);

        public static PumpEvent DeliverySucceeded(long epoch) =>
            new(PumpEventKind.DeliverySucceeded, epoch);

        public static PumpEvent DeliveryConflict(long epoch, GodotActionDelivery delivery) =>
            new(PumpEventKind.DeliveryConflict, epoch, delivery: delivery);

        public static PumpEvent DeliveryFailed(long epoch, string error) =>
            new(PumpEventKind.DeliveryFailed, epoch, error: error);
    }

    private sealed class ActionChannelItem
    {
        private ActionChannelItem(RuntimeActionSseFrame? frame, string? failure)
        {
            Frame = frame;
            Failure = failure;
        }

        public RuntimeActionSseFrame? Frame { get; }

        public string? Failure { get; }

        public static ActionChannelItem Delivery(RuntimeActionSseFrame frame) => new(frame, null);

        public static ActionChannelItem TerminalFailure(string failure) => new(null, failure);
    }
}
