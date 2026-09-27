using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenGameAgent.Protocol;

namespace OpenGameAgent.Client;

public sealed class RuntimeClient : IDisposable
{
    internal static readonly TimeSpan MaximumTaskDelay = TimeSpan.FromMilliseconds(4_294_967_294d);
    private static readonly ConditionalWeakTable<object, PresentationPresence> PresentationPresences = new();

    private readonly Uri baseAddress;
    private readonly HttpClient httpClient;
    private readonly long maxRequestBytes;
    private readonly long maxHttpResponseBytes;
    private readonly long maxAttachmentResponseBytes;
    private readonly long maxSseEventBytes;
    private readonly Func<CancellationToken, ValueTask<string?>>? accessTokenProvider;
    private readonly JsonSerializerOptions jsonOptions;

    public RuntimeClient(RuntimeClientOptions options, JsonSerializerOptions? jsonOptions = null)
        : this(options, CreateHttpClient(options), jsonOptions)
    {
    }

    internal RuntimeClient(
        RuntimeClientOptions options,
        HttpMessageHandler handler,
        JsonSerializerOptions? jsonOptions = null)
        : this(options, CreateHttpClient(options, handler), jsonOptions)
    {
    }

    private RuntimeClient(RuntimeClientOptions options, HttpClient client, JsonSerializerOptions? jsonOptions)
    {
        baseAddress = new Uri($"{options.BaseAddress.AbsoluteUri.TrimEnd('/')}/", UriKind.Absolute);
        httpClient = client;
        maxRequestBytes = options.MaxRequestBytes;
        maxHttpResponseBytes = options.MaxHttpResponseBytes;
        maxAttachmentResponseBytes = options.MaxAttachmentResponseBytes;
        maxSseEventBytes = options.MaxSseEventBytes;
        accessTokenProvider = options.AccessTokenProvider;
        var configuredJsonOptions = new JsonSerializerOptions(jsonOptions ?? RuntimeProtocolJson.CreateSerializerOptions())
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        configuredJsonOptions.TypeInfoResolver = new RuntimeTypeInfoResolver(
            configuredJsonOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver());
        this.jsonOptions = configuredJsonOptions;
    }

    public void Dispose() => httpClient.Dispose();

    public Task<RuntimeInitializeResponse> InitializeAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeInitializeResponse>(HttpMethod.Post, "/runtime/v1/initialize", null, cancellationToken);

    public Task<RuntimeRunAccepted> SubmitRunAsync(
        RuntimeRunRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeRunAccepted>(HttpMethod.Post, "/runtime/v1/runs", CaptureRunRequest(request, jsonOptions), cancellationToken);

    public async Task<RuntimeAuthorityClosure> CloseAuthorityAsync(
        RuntimeAuthorityCloseRequest request, CancellationToken cancellationToken = default)
    {
        RuntimeAuthorityCloseRequest captured = Clone(request);
        ValidateAuthorityClose(captured);
        RuntimeAuthorityClosure response = await SendJsonAsync<RuntimeAuthorityClosure>(
            HttpMethod.Post, "/runtime/v1/authority/close", captured, cancellationToken).ConfigureAwait(false);
        RuntimeAuthorityCloseRequest returned = ValidateAuthorityClosure(response);
        if (!string.Equals(CanonicalJson.Serialize(captured, jsonOptions), CanonicalJson.Serialize(returned, jsonOptions), StringComparison.Ordinal))
            throw new RuntimeRunSessionIdentityException();
        return response;
    }

    public async Task<RuntimeAuthorityReadResponse> ReadAuthorityClosureAsync(
        RuntimeAuthorityReadRequest request, CancellationToken cancellationToken = default)
    {
        RuntimeAuthorityReadRequest captured = Clone(request);
        ValidateAuthorityKey(captured.SessionId, captured.ActorId, captured.Scope, captured.Generation);
        RuntimeAuthorityReadResponse response = await SendJsonAsync<RuntimeAuthorityReadResponse>(
            HttpMethod.Post, "/runtime/v1/authority/read", captured, cancellationToken).ConfigureAwait(false);
        if (response.Closure is not null)
        {
            RuntimeAuthorityCloseRequest returned = ValidateAuthorityClosure(response.Closure);
            if (returned.SessionId != captured.SessionId || returned.ActorId != captured.ActorId
                || returned.Scope != captured.Scope || returned.Generation != captured.Generation)
                throw new RuntimeRunSessionIdentityException();
        }
        return response;
    }

    internal static RuntimeRunRequest CaptureRunRequest(RuntimeRunRequest request, JsonSerializerOptions options)
    {
        RuntimeRunAdmissionAuthority? authority = request.AdmissionAuthority;
        var captured = new RuntimeRunRequest
        {
            SessionId = request.SessionId, ActorId = request.ActorId, InputId = request.InputId,
            Payload = JsonSerializer.SerializeToElement(request.Payload, options),
            AdmissionAuthority = authority is null ? null : new RuntimeRunAdmissionAuthority { Scope = authority.Scope, Generation = authority.Generation },
        };
        if (!RuntimeRunSession.IsOpaqueIdentifier(captured.SessionId) || !RuntimeRunSession.IsOpaqueIdentifier(captured.ActorId)
            || !RuntimeRunSession.IsOpaqueIdentifier(captured.InputId)) throw new RuntimeRunSessionIdentityException();
        if (captured.AdmissionAuthority is not null)
            ValidateAuthorityKey(captured.SessionId, captured.ActorId, captured.AdmissionAuthority.Scope, captured.AdmissionAuthority.Generation);
        return captured;
    }

    private static void ValidateAuthorityKey(string sessionId, string actorId, string scope, string generation)
    {
        if (!RuntimeRunSession.IsOpaqueIdentifier(sessionId) || !RuntimeRunSession.IsOpaqueIdentifier(actorId)
            || !RuntimeRunSession.IsOpaqueIdentifier(scope)) throw new RuntimeRunSessionIdentityException();
        RuntimeRunSession.ParseDecimal(generation);
    }

    private static void ValidateAuthorityClose(RuntimeAuthorityCloseRequest request)
    {
        ValidateAuthorityKey(request.SessionId, request.ActorId, request.Scope, request.Generation);
        if (!RuntimeRunSession.IsOpaqueIdentifier(request.OperationId)) throw new RuntimeRunSessionIdentityException();
        RuntimeAuthorityGoalRetirement? goal = request.GoalRetirement;
        if (goal is not null)
        {
            var partition = goal.Partition ?? throw new RuntimeRunSessionIdentityException();
            var time = goal.GameTime ?? throw new RuntimeRunSessionIdentityException();
            if (!RuntimeRunSession.IsOpaqueIdentifier(goal.RetirementId) || !RuntimeRunSession.IsOpaqueIdentifier(partition.OwnerId)
                || !RuntimeRunSession.IsOpaqueIdentifier(partition.TimelineId)
                || partition.SessionId is not null && !RuntimeRunSession.IsOpaqueIdentifier(partition.SessionId)
                || partition.ActorId is not null && !RuntimeRunSession.IsOpaqueIdentifier(partition.ActorId)
                || partition.TimelineId != time.TimelineId || partition.Generation != time.Generation)
                throw new RuntimeRunSessionIdentityException();
            RuntimeRunSession.ParseDecimal(goal.StateRevision);
            RuntimeRunSession.ParseDecimal(partition.Generation);
            RuntimeRunSession.ParseDecimal(time.Tick);
            RuntimeRunSession.ValidateCanonicalTimestamp(goal.RecordedAt);
        }
        RuntimeAuthorityWorkRetirement? work = request.WorkRetirement;
        if (work is not null)
        {
            var lane = work.Partition ?? throw new RuntimeRunSessionIdentityException();
            if (!RuntimeRunSession.IsOpaqueIdentifier(work.RetirementId)
                || !RuntimeRunSession.IsOpaqueIdentifier(lane.OwnerId) || !RuntimeRunSession.IsOpaqueIdentifier(lane.TimelineId)
                || lane.SessionId != request.SessionId || lane.ActorId != request.ActorId)
                throw new RuntimeRunSessionIdentityException();
            RuntimeRunSession.ParseDecimal(lane.Generation);
            RuntimeRunSession.ValidateCanonicalTimestamp(work.RecordedAt);
        }
    }

    private static RuntimeAuthorityCloseRequest ValidateAuthorityClosure(RuntimeAuthorityClosure response)
    {
        RuntimeRunSession.ValidateCanonicalTimestamp(response.ClosedAt);
        var request = new RuntimeAuthorityCloseRequest
        {
            SessionId = response.SessionId, ActorId = response.ActorId, Scope = response.Scope,
            Generation = response.Generation, OperationId = response.OperationId, GoalRetirement = response.GoalRetirement,
            WorkRetirement = response.WorkRetirement,
        };
        ValidateAuthorityClose(request);
        if ((response.WorkRetirement is null) != (response.WorkRetirementResult is null))
            throw new RuntimeRunSessionIdentityException();
        if (response.WorkRetirementResult is { } result)
        {
            if (result.Phase is not (Phase.Fenced or Phase.Finalized)) throw new RuntimeRunSessionIdentityException();
            RuntimeRunSession.ParseDecimal(result.CancelledScheduleCount);
            RuntimeRunSession.ParseDecimal(result.CancelledOccurrenceCount);
            RuntimeRunSession.ParseDecimal(result.CancelledMessageCount);
        }
        return request;
    }

    public Task<RuntimeRunSession> StartRunSessionAsync(
        RuntimeRunRequest request,
        RuntimeRunSessionOptions options,
        CancellationToken cancellationToken = default) =>
        RuntimeRunSession.StartAsync(this, request, options, jsonOptions, cancellationToken);

    public Task<RuntimeRunSession> AttachRunSessionAsync(
        RuntimeRunRequest request,
        RuntimeRunAccepted accepted,
        RuntimeRunSessionOptions options,
        CancellationToken cancellationToken = default) =>
        RuntimeRunSession.AttachAsync(this, request, accepted, options, jsonOptions, cancellationToken);

    public Task<RuntimeDeferredReadResponse> ReadDeferredAsync(
        RuntimeDeferredReadRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeDeferredReadResponse>(
            HttpMethod.Post,
            "/runtime/v1/deferred/read",
            request,
            cancellationToken);

    public Task<RuntimeRunAccepted> ResumeDeferredAsync(
        RuntimeDeferredCommandRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeRunAccepted>(
            HttpMethod.Post,
            "/runtime/v1/deferred/resume",
            request,
            cancellationToken);

    public Task<RuntimeDeferredAbandonResponse> AbandonDeferredAsync(
        RuntimeDeferredCommandRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeDeferredAbandonResponse>(
            HttpMethod.Post,
            "/runtime/v1/deferred/abandon",
            request,
            cancellationToken);

    public Task<RuntimeEventReplay> ReadEventsAsync(
        RuntimeEventPageRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeEventReplay>(HttpMethod.Post, "/runtime/v1/events", request, cancellationToken);

    public Task<RuntimeControlResult> ControlRunAsync(
        RuntimeControlRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeControlResult>(HttpMethod.Post, "/runtime/v1/control", request, cancellationToken);

    public Task<RuntimeActionNextResponse> NextActionAsync(
        RuntimeActionNextRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeActionNextResponse>(
            HttpMethod.Post,
            "/runtime/v1/actions/next",
            request,
            cancellationToken);

    public Task<RuntimeActionRecoveryResponse> RecoverActionAsync(
        RuntimeActionRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeActionRecoveryResponse>(
            HttpMethod.Post,
            "/runtime/v1/actions/recovery",
            request,
            cancellationToken);

    public async Task<long> RecoverActorActionsAsync(
        string sessionId,
        string actorId,
        RuntimeActorActionRecoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        RuntimeActorActionRecoveryOptions captured = CaptureRecoveryOptions(options);
        RuntimeRunSession.ValidateActorIdentity(sessionId, actorId);
        for (long recovered = 0; ; recovered++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeActionRecoveryResponse response = await RecoverActionAsync(
                new RuntimeActionRecoveryRequest { SessionId = sessionId, ActorId = actorId },
                cancellationToken).ConfigureAwait(false);
            RuntimeActionRecoveryDelivery? delivery = response.Delivery;
            if (delivery is null) return recovered;
            if (recovered >= captured.MaximumActions) throw new RuntimeActorActionRecoveryLimitException();
            RuntimeRunSession.ValidateRecoveryDelivery(delivery, sessionId, actorId, jsonOptions);

            RuntimeActionRecoveryResolution resolution;
            var deadline = new CancellationTokenSource();
            bool cleanupDeferred = false;
            try
            {
                RuntimeActionRecoveryDelivery deliverySnapshot = Clone(delivery);
                Task<RuntimeActionRecoveryResolution> reconciliationCallback = Task.Run(
                    () => captured.Reconcile(deliverySnapshot, deadline.Token),
                    CancellationToken.None);
                try
                {
                    resolution = await reconciliationCallback
                        .WaitAsync(captured.Timeout, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new RuntimeRunSessionIdentityException();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (TimeoutException)
                {
                    cleanupDeferred = true;
                    CancelRecoveryCallback(deadline, reconciliationCallback);
                    throw new RuntimeRunSessionHandlerTimeoutException("action recovery");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cleanupDeferred = true;
                    CancelRecoveryCallback(deadline, reconciliationCallback);
                    throw;
                }
            }
            finally
            {
                if (!cleanupDeferred) deadline.Dispose();
            }
            Claim claim = delivery.Action.Claim ?? throw new RuntimeRunSessionIdentityException();
            RuntimeActionReconcileRequestAuthorityAfter? authority = resolution.AuthorityAfter;
            var reconciliation = new RuntimeActionReconcileRequest
            {
                SessionId = sessionId,
                ActorId = actorId,
                OperationId = delivery.Action.OperationId,
                ClaimToken = claim.ClaimToken,
                Attempt = claim.Attempt,
                Resolution = resolution.Resolution,
                Result = SnapshotJson(resolution.Result),
                ModelResult = SnapshotJson(resolution.ModelResult),
                AuthorityAfter = authority is null
                    ? null
                    : new RuntimeActionReconcileRequestAuthorityAfter
                    {
                        TimelineId = authority.TimelineId,
                        Generation = authority.Generation,
                        Revision = authority.Revision,
                    },
            };
            RuntimeRunSession.ValidateRecoveryReconciliation(reconciliation);
            RuntimeActionRecordResponse reconciled = await ReconcileActionAsync(
                reconciliation,
                cancellationToken).ConfigureAwait(false);
            RuntimeRunSession.ValidateReconciledAction(
                delivery.Action,
                reconciled.Action,
                reconciliation,
                jsonOptions);
            if (reconciliation.Resolution == Resolution.Unknown)
            {
                throw new RuntimeActorActionRecoveryUnresolvedException(delivery.Action.OperationId);
            }
        }
    }

    public Task<RuntimeActionGetResponse> GetActionAsync(
        RuntimeActionGetRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeActionGetResponse>(
            HttpMethod.Post,
            "/runtime/v1/actions/get",
            request,
            cancellationToken);

    public Task<RuntimeActionRecordResponse> RecordActionReceiptAsync(
        RuntimeActionReceiptRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeActionRecordResponse>(
            HttpMethod.Post,
            "/runtime/v1/actions/receipt",
            request,
            cancellationToken);

    public Task<RuntimeActionRecordResponse> ReconcileActionAsync(
        RuntimeActionReconcileRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeActionRecordResponse>(
            HttpMethod.Post,
            "/runtime/v1/actions/reconcile",
            request,
            cancellationToken);

    public Task<RuntimeApprovalListResponse> ListApprovalsAsync(
        RuntimeApprovalListRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeApprovalListResponse>(
            HttpMethod.Post,
            "/runtime/v1/approvals",
            request,
            cancellationToken);

    public Task<RuntimeApprovalWaitResponse> WaitApprovalsAsync(
        RuntimeApprovalWaitRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeApprovalWaitResponse>(
            HttpMethod.Post,
            "/runtime/v1/approvals/wait",
            request,
            cancellationToken);

    public Task<RuntimeApprovalDecisionResponse> DecideApprovalAsync(
        RuntimeApprovalDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RuntimeApprovalDecisionResponse>(
            HttpMethod.Post,
            "/runtime/v1/approvals/decision",
            request,
            cancellationToken);

    public async Task<byte[]> ReadAttachmentAsync(
        RuntimeAttachmentReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateAttachmentRequest(request, maxAttachmentResponseBytes);
        using HttpRequestMessage outgoing = await CreateRequestAsync(
            HttpMethod.Post,
            "/runtime/v1/attachments/read",
            request,
            lastEventId: null,
            cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await httpClient.SendAsync(
            outgoing,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ThrowIfRedirected(outgoing, response);
        await ThrowForStatusAsync(response, cancellationToken).ConfigureAwait(false);

        RuntimeAttachmentReadRequestReference reference = request.Reference;
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maxAttachmentResponseBytes)
        {
            throw new RuntimeResponseLimitException("attachment response");
        }
        if (contentLength is null
            || contentLength <= 0
            || contentLength != reference.Bytes
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            || response.Headers.ETag is null
            || response.Headers.ETag.IsWeak
            || !string.Equals(response.Headers.ETag.Tag, $"\"{reference.AttachmentId}\"", StringComparison.Ordinal)
            || !string.Equals(response.Headers.CacheControl?.ToString(), "no-store, private", StringComparison.OrdinalIgnoreCase)
            || !HasSingleHeaderValue(response, "X-Content-Type-Options", "nosniff"))
        {
            throw new RuntimeAttachmentIntegrityException("invalid_response");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new BoundedReadStream(source, reference.Bytes, resetAtSseBoundary: false);
        using var destination = new MemoryStream((int)reference.Bytes);
        await bounded.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        byte[] data = destination.ToArray();
        if (data.LongLength != reference.Bytes)
        {
            throw new RuntimeAttachmentIntegrityException("invalid_response");
        }

        byte[] digest = SHA256.HashData(data);
        string expectedAttachmentId = $"img_{Convert.ToHexString(digest).ToLowerInvariant()}";
        string expectedContentDigest = $"sha-256=:{Convert.ToBase64String(digest)}:";
        if (!string.Equals(reference.AttachmentId, expectedAttachmentId, StringComparison.Ordinal)
            || !HasSingleHeaderValue(response, "Content-Digest", expectedContentDigest))
        {
            throw new RuntimeAttachmentIntegrityException("content_mismatch");
        }
        return data;
    }

    public IAsyncEnumerable<RuntimeActionSseFrame> StreamActionsAsync(
        RuntimeActionNextRequest request,
        RuntimeSseRetryPolicy retryPolicy,
        CancellationToken cancellationToken = default)
    {
        var captured = new RuntimeActionNextRequest
        {
            SessionId = request.SessionId,
            ActorId = request.ActorId,
            RunId = request.RunId,
        };
        return StreamAsync<RuntimeActionSseFrame>(
            lastEventId => CreateRequestAsync(
                HttpMethod.Post,
                "/runtime/v1/actions/stream",
                captured,
                lastEventId,
                cancellationToken),
            retryPolicy,
            cancellationToken,
            (_, data) => DeserializeExactSseFrame<RuntimeActionSseFrame>(data));
    }

    public IAsyncEnumerable<RuntimeSseFrame> StreamEventsAsync(
        string sessionId,
        string actorId,
        string? afterRevision,
        RuntimeSseRetryPolicy retryPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(actorId);
        string path =
            $"/runtime/v1/events/stream?sessionId={Uri.EscapeDataString(sessionId)}"
            + $"&actorId={Uri.EscapeDataString(actorId)}"
            + (afterRevision is null ? string.Empty : $"&afterRevision={Uri.EscapeDataString(afterRevision)}");
        return StreamAsync<RuntimeSseFrame>(
            lastEventId => CreateRequestAsync(HttpMethod.Get, path, null, lastEventId, cancellationToken),
            retryPolicy,
            cancellationToken,
            (_, data) => DeserializeExactSseFrame<RuntimeSseFrame>(data));
    }

    private T? DeserializeExactSseFrame<T>(ReadOnlySpan<byte> data)
        where T : class
    {
        using JsonDocument source = JsonDocument.Parse(data.ToArray());
        T? value = JsonSerializer.Deserialize<T>(data, jsonOptions);
        if (value is not null) ValidateExactRoundTrip(source.RootElement, value, "SSE frame");
        return value;
    }

    private async Task<TResponse> SendJsonAsync<TResponse>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            method,
            path,
            body,
            lastEventId: null,
            cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ThrowIfRedirected(request, response);
        await ThrowForStatusAsync(response, cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<TFrame> StreamAsync<TFrame>(
        Func<string?, ValueTask<HttpRequestMessage>> requestFactory,
        RuntimeSseRetryPolicy retryPolicy,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        SseItemParser<TFrame?> parser)
        where TFrame : class
    {
        ValidateRetryPolicy(retryPolicy);
        string? lastEventId = null;
        TimeSpan nextDelay = retryPolicy.DefaultDelay;
        Exception? lastFailure = null;

        for (int attemptIndex = 0; attemptIndex < retryPolicy.MaximumConnectionAttempts; attemptIndex++)
        {
            SseConnection<TFrame>? connection = null;
            try
            {
                connection = await OpenSseConnectionAsync(
                    requestFactory,
                    lastEventId,
                    parser,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                RuntimeFailureClassifier.Classify(exception) == RuntimeFailureDisposition.Retry)
            {
                lastFailure = exception;
            }

            if (connection is not null)
            {
                await using (connection.ConfigureAwait(false))
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await connection.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception) when (
                            RuntimeFailureClassifier.Classify(exception) == RuntimeFailureDisposition.Retry)
                        {
                            lastFailure = exception;
                            break;
                        }

                        if (!hasNext)
                        {
                            lastFailure = null;
                            break;
                        }

                        SseItem<TFrame?> item = connection.Current;
                        if (item.Data is null)
                        {
                            throw new JsonException("Runtime SSE event contained JSON null.");
                        }

                        lastEventId = item.EventId ?? lastEventId;
                        yield return item.Data;
                    }

                    if (connection.ReconnectionInterval != Timeout.InfiniteTimeSpan)
                    {
                        nextDelay = Min(connection.ReconnectionInterval, retryPolicy.MaximumDelay);
                    }
                }
            }

            if (attemptIndex < retryPolicy.MaximumConnectionAttempts - 1)
            {
                await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new RuntimeSseDisconnectedException(retryPolicy.MaximumConnectionAttempts, lastFailure);
    }

    private async Task<SseConnection<TFrame>> OpenSseConnectionAsync<TFrame>(
        Func<string?, ValueTask<HttpRequestMessage>> requestFactory,
        string? lastEventId,
        SseItemParser<TFrame?> parser,
        CancellationToken cancellationToken)
        where TFrame : class
    {
        using HttpRequestMessage request = await requestFactory(lastEventId).ConfigureAwait(false);
        HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfRedirected(request, response);
            await ThrowForStatusAsync(response, cancellationToken).ConfigureAwait(false);
            Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bounded = new BoundedReadStream(source, maxSseEventBytes, resetAtSseBoundary: true);
            SseParser<TFrame?> sseParser = SseParser.Create(bounded, parser);
            IAsyncEnumerator<SseItem<TFrame?>> enumerator = sseParser
                .EnumerateAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            return new SseConnection<TFrame>(response, bounded, sseParser, enumerator);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path,
        object? body,
        string? lastEventId,
        CancellationToken cancellationToken)
    {
        byte[]? json = null;
        if (body is not null)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), jsonOptions);
            if (json.LongLength > maxRequestBytes) throw new RuntimeRequestLimitException();
        }

        var request = new HttpRequestMessage(method, new Uri(baseAddress, path.TrimStart('/')));
        if (json is not null)
        {
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        if (accessTokenProvider is not null)
        {
            string? token = await accessTokenProvider(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                if (!IsValidAccessToken(token))
                {
                    request.Dispose();
                    throw new ArgumentException("The Runtime access token is invalid.", nameof(accessTokenProvider));
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return request;
    }

    private static HttpClient CreateHttpClient(RuntimeClientOptions options)
    {
        ValidateOptions(options);
        var handler = new SocketsHttpHandler();
        try
        {
            options.ConfigureHttpHandler?.Invoke(handler);
            handler.AllowAutoRedirect = false;
            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static HttpClient CreateHttpClient(RuntimeClientOptions options, HttpMessageHandler handler)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(handler);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void ValidateOptions(RuntimeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!IsAllowedBaseAddress(options.BaseAddress))
        {
            throw new ArgumentException(
                "The Runtime base address must use HTTPS or loopback HTTP without credentials, query, fragment, or backslashes.",
                nameof(options));
        }
        if (options.MaxHttpResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxHttpResponseBytes must be positive.");
        }
        if (options.MaxRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRequestBytes must be positive.");
        }
        if (options.MaxSseEventBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSseEventBytes must be positive.");
        }
        if (options.MaxAttachmentResponseBytes <= 0 || options.MaxAttachmentResponseBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaxAttachmentResponseBytes must be positive and no greater than {Array.MaxLength}.");
        }
    }

    private static bool IsAllowedBaseAddress(Uri value)
    {
        if (!value.IsAbsoluteUri
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || value.OriginalString.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(value.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(value.Host, out IPAddress? address)) return false;
        if (IPAddress.IPv6Loopback.Equals(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes[0] == 127;
    }

    private static bool IsValidAccessToken(string value)
    {
        bool sawTokenCharacter = false;
        bool sawPadding = false;
        foreach (char character in value)
        {
            if (character == '=')
            {
                if (!sawTokenCharacter) return false;
                sawPadding = true;
                continue;
            }
            bool alphaNumeric = (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9');
            bool tokenCharacter = alphaNumeric || character is '-' or '.' or '_' or '~' or '+' or '/';
            if (sawPadding || !tokenCharacter) return false;
            sawTokenCharacter = true;
        }
        return sawTokenCharacter;
    }

    private static void ThrowIfRedirected(HttpRequestMessage request, HttpResponseMessage response)
    {
        Uri? expected = request.RequestUri;
        Uri? effective = response.RequestMessage?.RequestUri;
        int status = (int)response.StatusCode;
        if (status is >= 300 and <= 399
            || expected is null
            || effective is null
            || !string.Equals(expected.AbsoluteUri, effective.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new RuntimeRedirectException();
        }
    }

    private async Task<T> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxHttpResponseBytes)
        {
            throw new RuntimeResponseLimitException("HTTP response");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new BoundedReadStream(source, maxHttpResponseBytes, resetAtSseBoundary: false);
        if (typeof(T) == typeof(RuntimeApprovalListResponse)
            || typeof(T) == typeof(RuntimeApprovalDecisionResponse)
            || typeof(T) == typeof(RuntimeAuthorityClosure)
            || typeof(T) == typeof(RuntimeAuthorityReadResponse))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                bounded,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            T? exactValue = document.RootElement.Deserialize<T>(jsonOptions);
            if (exactValue is null)
            {
                throw new JsonException("Runtime response contained JSON null.");
            }
            ValidateExactRoundTrip(document.RootElement, exactValue, "response");
            return exactValue;
        }
        T? value = await JsonSerializer.DeserializeAsync<T>(bounded, jsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new JsonException("Runtime response contained JSON null.");
    }

    internal static bool HasPresentation(object request) => PresentationPresences.TryGetValue(request, out _);

    private static void MarkPresentation(object request) =>
        PresentationPresences.GetValue(request, static _ => new PresentationPresence());

    private void ValidateExactRoundTrip<T>(JsonElement source, T value, string description)
    {
        if (!string.Equals(
                CanonicalJson.Serialize(source, jsonOptions),
                CanonicalJson.Serialize(value!, jsonOptions),
                StringComparison.Ordinal))
        {
            throw new JsonException($"Runtime {description} did not round-trip exactly through the protocol DTO.");
        }
    }

    private async Task ThrowForStatusAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        RuntimeError? error = null;
        try
        {
            error = await DeserializeResponseAsync<RuntimeError>(response, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // The HTTP status remains authoritative when an upstream error body is malformed.
        }

        throw new RuntimeHttpException(response.StatusCode, error);
    }

    internal static void ValidateRetryPolicy(RuntimeSseRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.DefaultDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "DefaultDelay must be positive.");
        }

        if (policy.MaximumDelay < policy.DefaultDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "MaximumDelay must not be shorter than DefaultDelay.");
        }

        if (policy.DefaultDelay > MaximumTaskDelay || policy.MaximumDelay > MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "SSE retry delays must not exceed the Task.Delay limit of 4294967294 milliseconds.");
        }

        if (policy.MaximumConnectionAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "MaximumConnectionAttempts must be positive.");
        }
    }

    internal static RuntimeActorActionRecoveryOptions CaptureRecoveryOptions(RuntimeActorActionRecoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumActions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumActions));
        }
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Timeout));
        }
        ArgumentNullException.ThrowIfNull(options.Reconcile);
        return new RuntimeActorActionRecoveryOptions
        {
            MaximumActions = options.MaximumActions,
            Timeout = options.Timeout,
            Reconcile = options.Reconcile,
        };
    }

    private T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, jsonOptions), jsonOptions)
        ?? throw new JsonException("Runtime value serialized as JSON null.");

    private object? SnapshotJson(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToElement(value, value.GetType(), jsonOptions);

    private static void CancelRecoveryCallback(
        CancellationTokenSource cancellation,
        Task<RuntimeActionRecoveryResolution> reconciliation)
    {
        ObserveFault(reconciliation);
        Task cancellationCallbacks;
        try
        {
            cancellationCallbacks = cancellation.CancelAsync();
        }
        catch
        {
            cancellation.Dispose();
            return;
        }
        _ = cancellationCallbacks.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ValidateAttachmentRequest(RuntimeAttachmentReadRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Reference);
        RuntimeAttachmentReadRequestReference reference = request.Reference;
        if (string.IsNullOrEmpty(request.SessionId)
            || string.IsNullOrEmpty(request.ActorId)
            || reference.Type != ReferenceType.ImageReference
            || reference.MediaType != MediaType.ImagePng
            || reference.Bytes <= 0
            || reference.Bytes > maxBytes
            || reference.Width <= 0
            || reference.Height <= 0
            || !IsAttachmentId(reference.AttachmentId))
        {
            throw new ArgumentException("The attachment request is invalid.", nameof(request));
        }
    }

    private static bool IsAttachmentId(string? value)
    {
        if (value is null || value.Length != 68 || !value.StartsWith("img_", StringComparison.Ordinal)) return false;
        for (int index = 4; index < value.Length; index++)
        {
            char character = value[index];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
        }
        return true;
    }

    private static bool HasSingleHeaderValue(HttpResponseMessage response, string name, string expected)
    {
        IEnumerable<string> values;
        bool responseHas = response.Headers.TryGetValues(name, out IEnumerable<string>? responseValues);
        bool contentHas = response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues);
        if (responseHas == contentHas) return false;
        values = responseHas ? responseValues! : contentValues!;
        using IEnumerator<string> enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext() || !string.Equals(enumerator.Current, expected, StringComparison.Ordinal))
        {
            return false;
        }
        return !enumerator.MoveNext();
    }


    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private sealed class RuntimeTypeInfoResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? typeInfo = inner.GetTypeInfo(type, options);
            if (typeInfo is null || type != typeof(Request))
            {
                return typeInfo;
            }

            JsonPropertyInfo? presentation = typeInfo.Properties.FirstOrDefault(
                static property => string.Equals(property.Name, "presentation", StringComparison.Ordinal));
            if (presentation?.Set is not Action<object, object?> setter) return typeInfo;
            presentation.Set = (request, value) =>
            {
                setter(request, value);
                MarkPresentation(request);
            };
            presentation.ShouldSerialize = static (request, value) => value is not null || HasPresentation(request);
            return typeInfo;
        }
    }

    private sealed class PresentationPresence;

    private sealed class SseConnection<TFrame> : IAsyncDisposable
        where TFrame : class
    {
        private readonly HttpResponseMessage response;
        private readonly BoundedReadStream stream;
        private readonly SseParser<TFrame?> parser;
        private readonly IAsyncEnumerator<SseItem<TFrame?>> enumerator;

        public SseConnection(
            HttpResponseMessage response,
            BoundedReadStream stream,
            SseParser<TFrame?> parser,
            IAsyncEnumerator<SseItem<TFrame?>> enumerator)
        {
            this.response = response;
            this.stream = stream;
            this.parser = parser;
            this.enumerator = enumerator;
        }

        public SseItem<TFrame?> Current => enumerator.Current;

        public TimeSpan ReconnectionInterval => parser.ReconnectionInterval;

        public ValueTask<bool> MoveNextAsync() => enumerator.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }
    }
}
