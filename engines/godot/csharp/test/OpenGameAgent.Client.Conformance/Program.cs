using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenGameAgent.Client;
using OpenGameAgent.Protocol;

TestBaseAddressAdmission();
TestFailureClassification();
await TestRunInputAdmissionErrorsAsync();
await TestInitializeAndBearerAsync();
await TestDeferredLifecycleAsync();
await TestAuthorityLifecycleAsync();
await TestWorkAuthorityLifecycleAsync();
await TestScopedRequestSnapshotAsync();
await TestAtomicActionExchangeAsync();
await TestRecoveryRejectsDispatchAsync();
await TestActorRecoveryReconcilesWithoutDispatchAsync();
await TestActorRecoveryGuardsAsync();
await TestAttachRunSessionAsync();
await TestAccessTokenValidationAsync();
await TestRequestLimitAsync();
await TestHttpResponseLimitAsync();
await TestSseReconnectCursorAsync();
await TestSseRetryDelayBoundsAsync();
await TestSseFailureClassificationAsync();
await TestExactRunSessionAsync();
await TestRunSessionAdmissionGateAsync();
await TestRunSessionActivationGateAsync();
await TestRunSessionRejectsInvalidApprovalBackoffAsync();
await TestRunSessionApprovalBackoffAsync();
await TestRunSessionRejectsMalformedAdmissionAsync();
await TestRunSessionRejectsMalformedPostAdmissionEventAsync();
await TestRunMetricsAsync();
await TestRunSessionActionAndEventCallbacksAsync();
await TestRunSessionRejectsInvalidActionDeliveryAsync();
await TestRunSessionRejectsOpaqueActionIdentifiersAsync();
await TestRunSessionRejectsInvalidApprovalRequestAsync();
await TestRunSessionAcceptsExplicitNullApprovalPresentationAsync();
await TestApprovalResponsesRejectExplicitNullOptionalsAsync();
await TestRunSessionApprovalDecisionAsync();
await TestRunSessionHandlerTimeoutsAsync();
await TestRunSessionAbortAndLocalCloseAsync();
await TestRunSessionEarlyEventEndAsync();
TestRuntimeProtocolRejectsUnmappedMembers();
TestGeneratedCanonicalEventShapes();
TestGeneratedApprovalPresentation();
await TestSseEventLimitAsync();
await TestSseMixedLineEndingsAndDelimiterLimitAsync();
await TestAttachmentReadAsync();
await TestAttachmentIntegrityAsync();
await TestAttachmentResponseLimitAsync();
await TestProductionRedirectBlockingAsync();
await TestProductionRunSessionCancellationAsync();
Console.WriteLine("OpenGameAgent Godot C# transport conformance passed (47 cases).");

static async Task TestWorkAuthorityLifecycleAsync()
{
    const string requestJson = """{"sessionId":"session","actorId":"actor","scope":"planning","generation":"1","operationId":"close-work","workRetirement":{"retirementId":"work","recordedAt":"2026-09-05T00:00:00.000Z","partition":{"ownerId":"owner","timelineId":"world","generation":"7","sessionId":"session","actorId":"actor"}}}""";
    const string resultJson = """{"phase":"finalized","cancelledScheduleCount":"9007199254740993","cancelledOccurrenceCount":"2","cancelledMessageCount":"3"}""";
    JsonObject closure = JsonNode.Parse(requestJson)!.AsObject();
    closure["closedAt"] = "2026-09-05T00:00:00.000Z";
    closure["workRetirementResult"] = JsonNode.Parse(resultJson);
    var tokenReady = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, closure.ToJsonString()),
        _ => JsonResponse(HttpStatusCode.OK, "{\"closure\":" + closure.ToJsonString() + "}"));
    using var client = new RuntimeClient(new RuntimeClientOptions
    {
        BaseAddress = new Uri("https://runtime.invalid"), MaxRequestBytes = 4096, MaxHttpResponseBytes = 4096,
        MaxAttachmentResponseBytes = 4096, MaxSseEventBytes = 4096,
        AccessTokenProvider = _ => new ValueTask<string?>(tokenReady.Task),
    }, handler);
    var request = JsonSerializer.Deserialize<RuntimeAuthorityCloseRequest>(requestJson)!;
    Task<RuntimeAuthorityClosure> pending = client.CloseAuthorityAsync(request);
    request.WorkRetirement!.Partition.OwnerId = "mutated";
    request.WorkRetirement.RetirementId = "mutated";
    tokenReady.SetResult("token");
    Assert((await pending).WorkRetirementResult?.CancelledScheduleCount == "9007199254740993", "Work count lost decimal precision");
    JsonObject sent = JsonNode.Parse(handler.RequestBodies[0])!.AsObject();
    Assert(sent["workRetirement"]!["partition"]!["ownerId"]!.GetValue<string>() == "owner", "Work lane changed during token await");
    Assert(sent["workRetirement"]!["retirementId"]!.GetValue<string>() == "work", "Work operation changed during token await");
    var read = new RuntimeAuthorityReadRequest { SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1" };
    Assert((await client.ReadAuthorityClosureAsync(read)).Closure!.WorkRetirementResult!.Phase == Phase.Finalized, "Work read lost finalization phase");

    var invalid = new List<JsonObject>();
    foreach (string field in new[] { "workRetirement", "workRetirementResult" })
    {
        JsonObject missing = closure.DeepClone().AsObject(); missing.Remove(field); invalid.Add(missing);
        JsonObject explicitNull = closure.DeepClone().AsObject(); explicitNull[field] = null; invalid.Add(explicitNull);
    }
    foreach (string field in new[] { "cancelledScheduleCount", "cancelledOccurrenceCount", "cancelledMessageCount" })
    {
        foreach (string value in new[] { "1", "\"01\"", "\"-1\"", "\"1.0\"", "null" })
        {
            JsonObject malformed = closure.DeepClone().AsObject();
            malformed["workRetirementResult"]![field] = JsonNode.Parse(value); invalid.Add(malformed);
        }
    }
    foreach ((string field, string value) in new[] { ("actorId", "other"), ("ownerId", "bad\nowner"), ("generation", "01") })
    {
        JsonObject malformed = closure.DeepClone().AsObject(); malformed["workRetirement"]!["partition"]![field] = value; invalid.Add(malformed);
    }
    JsonObject badPhase = closure.DeepClone().AsObject(); badPhase["workRetirementResult"]!["phase"] = "completed"; invalid.Add(badPhase);
    JsonObject badTime = closure.DeepClone().AsObject(); badTime["workRetirement"]!["recordedAt"] = "2026-02-30T00:00:00.000Z"; invalid.Add(badTime);
    foreach (JsonObject malformed in invalid)
    {
        using RuntimeClient rejecting = CreateClient(new SequenceHandler(
            _ => JsonResponse(HttpStatusCode.OK, malformed.ToJsonString()),
            _ => JsonResponse(HttpStatusCode.OK, "{\"closure\":" + malformed.ToJsonString() + "}")), 4096, 4096);
        foreach (Func<Task> operation in new Func<Task>[] { () => rejecting.CloseAuthorityAsync(JsonSerializer.Deserialize<RuntimeAuthorityCloseRequest>(requestJson)!), () => rejecting.ReadAuthorityClosureAsync(read) })
        {
            Exception failure = await AssertThrowsAsync<Exception>(operation);
            Assert(RuntimeFailureClassifier.Classify(failure) == RuntimeFailureDisposition.Halt,
                "invalid Work closure was not a protocol failure: " + malformed.ToJsonString() + " => " + failure);
        }
    }
    JsonObject mismatch = closure.DeepClone().AsObject(); mismatch["workRetirement"]!["retirementId"] = "different";
    using RuntimeClient mismatching = CreateClient(new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, mismatch.ToJsonString())), 4096, 4096);
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(() => mismatching.CloseAuthorityAsync(JsonSerializer.Deserialize<RuntimeAuthorityCloseRequest>(requestJson)!));
    JsonObject fenced = closure.DeepClone().AsObject(); fenced["workRetirementResult"] = JsonNode.Parse("""{"phase":"fenced","cancelledScheduleCount":"0","cancelledOccurrenceCount":"0","cancelledMessageCount":"0"}""");
    using RuntimeClient recovering = CreateClient(new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{\"closure\":" + fenced.ToJsonString() + "}")), 4096, 4096);
    Assert((await recovering.ReadAuthorityClosureAsync(read)).Closure!.WorkRetirementResult!.Phase == Phase.Fenced, "crash recovery lost fenced phase");
}

static async Task TestAuthorityLifecycleAsync()
{
    const string closure = """{"sessionId":"session","actorId":"actor","scope":"planning","generation":"1","operationId":"close-1","closedAt":"2026-09-05T00:00:00.000Z"}""";
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, closure),
        _ => JsonResponse(HttpStatusCode.OK, "{\"closure\":" + closure + "}"),
        _ => JsonResponse(HttpStatusCode.OK, closure.Replace("\"generation\":\"1\"", "\"generation\":\"2\"", StringComparison.Ordinal)));
    using RuntimeClient client = CreateClient(handler, 4096, 4096);
    var request = new RuntimeAuthorityCloseRequest { SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1", OperationId = "close-1" };
    RuntimeAuthorityClosure closed = await client.CloseAuthorityAsync(request);
    RuntimeAuthorityReadResponse read = await client.ReadAuthorityClosureAsync(new RuntimeAuthorityReadRequest { SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1" });
    Assert(read.Closure?.OperationId == closed.OperationId, "authority read lost the durable operation identity");
    Assert(handler.RequestPaths[0] == "/runtime/v1/authority/close" && handler.RequestPaths[1] == "/runtime/v1/authority/read", "authority routes changed");
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(() => client.CloseAuthorityAsync(request));
    request.Generation = "01";
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(() => client.CloseAuthorityAsync(request));
    Assert(handler.RequestCount == 3, "invalid authority reached HTTP");
}

static async Task TestScopedRequestSnapshotAsync()
{
    var tokenReady = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.Accepted,
        """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        _ => JsonResponse(HttpStatusCode.OK, """{"sessionId":"session","actorId":"actor","scope":"planning","generation":"1","operationId":"close-1","closedAt":"2026-09-05T00:00:00.000Z"}"""));
    using var client = new RuntimeClient(new RuntimeClientOptions
    {
        BaseAddress = new Uri("https://runtime.invalid"), MaxRequestBytes = 4096, MaxHttpResponseBytes = 4096,
        MaxAttachmentResponseBytes = 4096, MaxSseEventBytes = 4096,
        AccessTokenProvider = _ => new ValueTask<string?>(tokenReady.Task),
    }, handler);
    var authority = new RuntimeRunAdmissionAuthority { Scope = "planning", Generation = "1" };
    var request = new RuntimeRunRequest { SessionId = "session", ActorId = "actor", InputId = "input", Payload = new { }, AdmissionAuthority = authority };
    Task<RuntimeRunAccepted> submission = client.SubmitRunAsync(request);
    authority.Generation = "2";
    tokenReady.SetResult("token");
    await submission;
    using JsonDocument submitted = JsonDocument.Parse(handler.RequestBodies[0]);
    Assert(submitted.RootElement.GetProperty("admissionAuthority").GetProperty("generation").GetString() == "1", "submission authority mutated after invocation");
    var close = new RuntimeAuthorityCloseRequest { SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1", OperationId = "close-1" };
    Task<RuntimeAuthorityClosure> closing = client.CloseAuthorityAsync(close);
    close.Generation = "2";
    Assert((await closing).Generation == "1", "closure snapshot followed a mutable DTO");
}

static void TestBaseAddressAdmission()
{
    foreach (string admitted in
        new[] { "https://runtime.example/sidecar", "http://localhost:4411", "http://127.8.9.10:4411", "http://[::1]:4411" })
    {
        using RuntimeClient client = CreateClient(new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{}")), 1024, 1024, baseAddress: admitted);
    }

    foreach (string rejected in
        new[] { "http://runtime.example", "https://user@runtime.example", "https://runtime.example?token=x", "https://runtime.example#fragment", "https:\\\\runtime.example" })
    {
        AssertThrows<ArgumentException>(() =>
        {
            using RuntimeClient client = CreateClient(new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{}")), 1024, 1024, baseAddress: rejected);
        });
    }
}

static void TestFailureClassification()
{
    Assert(
        RuntimeFailureClassifier.Classify(new RuntimeHttpException(HttpStatusCode.ServiceUnavailable, null))
            == RuntimeFailureDisposition.Retry,
        "a 5xx was not classified as retryable");
    Assert(
        RuntimeFailureClassifier.Classify(new RuntimeHttpException(HttpStatusCode.Conflict, null))
            == RuntimeFailureDisposition.ReadAuthority,
        "a 409 did not retain its authority-read disposition");
    Assert(
        RuntimeFailureClassifier.Classify(new RuntimeRedirectException()) == RuntimeFailureDisposition.Halt,
        "a redirect was classified as retryable");
    Assert(
        RuntimeFailureClassifier.Classify(new JsonException()) == RuntimeFailureDisposition.Halt,
        "a protocol JSON error was classified as retryable");
    Assert(
        RuntimeFailureClassifier.Classify(new RuntimeRunSessionIdentityException()) == RuntimeFailureDisposition.Halt,
        "a protocol identity error was classified as retryable I/O");
}

static async Task TestRunInputAdmissionErrorsAsync()
{
    var oversizedPayload = new { text = "oversized" };
    int maxInputBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(oversizedPayload)) - 1;
    const int maxInputDepth = 1;
    foreach ((HttpStatusCode status, string code, string message, object payload) in new[]
    {
        (HttpStatusCode.RequestEntityTooLarge, "input_too_large",
            $"The run payload exceeds runtime.maxInputBytes ({maxInputBytes} UTF-8 bytes).", (object)oversizedPayload),
        (HttpStatusCode.BadRequest, "input_too_deep",
            $"The run payload exceeds runtime.maxInputDepth ({maxInputDepth}).", (object)new { nested = new { value = true } }),
    })
    {
        foreach (bool startSession in new[] { false, true })
        {
            var handler = new SequenceHandler(
                _ => JsonResponse(status, JsonSerializer.Serialize(new { error = new { code, message } })),
                _ => JsonResponse(HttpStatusCode.Accepted,
                    """{"runId":"accepted","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"accepted","turn":"0"},"eventCursor":"0"}"""));
            using RuntimeClient client = CreateClient(handler, 1024, 1024);
            var request = new RuntimeRunRequest
            {
                SessionId = "session", ActorId = "actor", InputId = "input", Payload = payload,
            };
            RuntimeHttpException failure = await AssertThrowsAsync<RuntimeHttpException>(async () =>
            {
                if (startSession)
                {
                    await using RuntimeRunSession session = await client.StartRunSessionAsync(request,
                        new RuntimeRunSessionOptions
                        {
                            EventRetryPolicy = new RuntimeSseRetryPolicy
                            {
                                DefaultDelay = TimeSpan.FromMilliseconds(1),
                                MaximumDelay = TimeSpan.FromMilliseconds(1),
                                MaximumConnectionAttempts = 3,
                            },
                            Actions = new RuntimeRunActionOptions
                            {
                                RetryPolicy = new RuntimeSseRetryPolicy
                                {
                                    DefaultDelay = TimeSpan.FromMilliseconds(1),
                                    MaximumDelay = TimeSpan.FromMilliseconds(1),
                                    MaximumConnectionAttempts = 3,
                                },
                                Timeout = TimeSpan.FromSeconds(1),
                                OnDelivery = (_, _) => Task.CompletedTask,
                            },
                            Approvals = new RuntimeRunApprovalOptions
                            {
                                WaitDuration = TimeSpan.FromMilliseconds(10), PageSize = 1,
                                Timeout = TimeSpan.FromSeconds(1), OnPending = (_, _) => Task.CompletedTask,
                            },
                        });
                }
                else
                {
                    await client.SubmitRunAsync(request);
                }
            });
            Assert(failure.StatusCode == status, "run admission HTTP status was not preserved");
            Assert(failure.RuntimeError?.Error.Code == code, "run admission error code was not preserved");
            Assert(failure.RuntimeError?.Error.Message == message, "run admission error message was not preserved");
            Assert(failure.Message == $"Runtime request failed: {code}: {message}", "run admission exception lost its actionable message");
            Assert(RuntimeFailureClassifier.Classify(failure) == RuntimeFailureDisposition.Halt,
                "run admission rejection was classified as retryable");
            Assert(handler.RequestCount == 1, "run admission rejection retried or opened a session observer");

            request.Payload = new { };
            RuntimeRunAccepted accepted = await client.SubmitRunAsync(request);
            Assert(accepted.RunId == "accepted", "a corrected request failed after admission rejection");
            Assert(handler.RequestCount == 2, "a corrected request issued unexpected HTTP requests");
            Assert(handler.RequestMethods.All(method => method == HttpMethod.Post)
                && handler.RequestPaths.All(path => path == "/runtime/v1/runs"),
                "run admission opened an event, Action, or Approval observer");
        }
    }
}

static async Task TestInitializeAndBearerAsync()
{
    var handler = new SequenceHandler(_ => JsonResponse(
        HttpStatusCode.OK,
        "{\"protocolVersion\":\"1\",\"capabilities\":[\"runs.submit\"]}"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, "token-value");

    RuntimeInitializeResponse initialized = await client.InitializeAsync();

    Assert(initialized.Capabilities.Length == 1, "initialize capabilities were not decoded");
    Assert(handler.Authorization?.Scheme == "Bearer", "Bearer scheme was not sent");
    Assert(handler.Authorization?.Parameter == "token-value", "Bearer token was not sent");
}

static async Task TestDeferredLifecycleAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(
            HttpStatusCode.OK,
            """{"deferred":{"operationId":"operation","inputId":"input","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"}}}"""),
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"eventCursor":"17"}"""),
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"eventCursor":"17"}"""),
        _ => JsonResponse(
            HttpStatusCode.OK,
            """{"operationId":"operation","providerCancellation":"failed"}"""),
        _ => JsonResponse(HttpStatusCode.OK, """{"deferred":null}"""));
    using RuntimeClient client = CreateClient(handler, 1024, 1024);

    RuntimeDeferredReadResponse read = await client.ReadDeferredAsync(new RuntimeDeferredReadRequest
    {
        SessionId = "session",
        ActorId = "actor",
    });
    RuntimeRunAccepted resumed = await client.ResumeDeferredAsync(new RuntimeDeferredCommandRequest
    {
        SessionId = "session",
        ActorId = "actor",
        OperationId = "operation",
    });
    RuntimeRunAccepted resumedAgain = await client.ResumeDeferredAsync(new RuntimeDeferredCommandRequest
    {
        SessionId = "session",
        ActorId = "actor",
        OperationId = "operation",
    });
    RuntimeDeferredAbandonResponse abandoned = await client.AbandonDeferredAsync(new RuntimeDeferredCommandRequest
    {
        SessionId = "session",
        ActorId = "actor",
        OperationId = "operation",
    });
    RuntimeDeferredReadResponse empty = await client.ReadDeferredAsync(new RuntimeDeferredReadRequest
    {
        SessionId = "session",
        ActorId = "actor",
    });

    RuntimeDeferredOperation deferred = read.Deferred
        ?? throw new InvalidOperationException("deferred read response was not decoded");
    Assert(deferred.OperationId == "operation", "deferred read response was not decoded");
    Assert(deferred.Coordinate.RunId == "run", "deferred coordinate was not decoded");
    Assert(resumed.RunId == "run", "deferred resume response was not decoded");
    Assert(resumedAgain.RunId == "run", "repeated explicit deferred resume response was not decoded");
    Assert(resumed.EventCursor == "17" && resumedAgain.EventCursor == "17", "deferred resume cursor was not stable");
    Assert(abandoned.ProviderCancellation == ProviderCancellation.Failed, "deferred abandon response was not decoded");
    Assert(empty.Deferred is null, "empty deferred projection was not decoded");
    Assert(
        handler.RequestPaths.SequenceEqual([
            "/runtime/v1/deferred/read",
            "/runtime/v1/deferred/resume",
            "/runtime/v1/deferred/resume",
            "/runtime/v1/deferred/abandon",
            "/runtime/v1/deferred/read",
        ]),
        "deferred lifecycle paths changed");
    AssertDeferredRequestBody(handler.RequestBodies[0], includeOperation: false);
    AssertDeferredRequestBody(handler.RequestBodies[1], includeOperation: true);
    AssertDeferredRequestBody(handler.RequestBodies[2], includeOperation: true);
    AssertDeferredRequestBody(handler.RequestBodies[3], includeOperation: true);
    AssertDeferredRequestBody(handler.RequestBodies[4], includeOperation: false);
    Assert(handler.RequestMethods.All(method => method == HttpMethod.Post), "deferred lifecycle did not use POST");

    var conflictHandler = new SequenceHandler(_ => JsonResponse(
        HttpStatusCode.Conflict,
        """{"error":{"code":"deferred_busy","message":"busy"}}"""));
    using RuntimeClient conflictClient = CreateClient(conflictHandler, 1024, 1024);
    try
    {
        await conflictClient.ResumeDeferredAsync(new RuntimeDeferredCommandRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = "operation",
        });
        throw new InvalidOperationException("Expected RuntimeHttpException.");
    }
    catch (RuntimeHttpException exception)
    {
        Assert(exception.StatusCode == HttpStatusCode.Conflict, "deferred conflict status was not preserved");
        Assert(exception.RuntimeError?.Error.Code == "deferred_busy", "deferred conflict body was not decoded");
    }
    Assert(conflictHandler.RequestCount == 1, "deferred conflict was retried");

    var cancellationHandler = new CancellationHandler();
    using RuntimeClient cancellationClient = CreateClient(cancellationHandler, 1024, 1024);
    using var cancellation = new CancellationTokenSource();
    Task<RuntimeRunAccepted> pendingResume = cancellationClient.ResumeDeferredAsync(
        new RuntimeDeferredCommandRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = "operation",
        },
        cancellation.Token);
    await cancellationHandler.Started;
    cancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(async () => await pendingResume);
    Assert(cancellationHandler.RequestCount == 1, "cancelled deferred resume was retried");
}

static async Task TestAtomicActionExchangeAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"),
        _ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"),
        _ => JsonResponse(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"action_conflict\",\"message\":\"conflict\"}}"),
        _ => JsonResponse(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"action_conflict\",\"message\":\"conflict\"}}"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024);
    const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    RuntimeActionNextResponse next = await client.NextActionAsync(new RuntimeActionNextRequest
    {
        SessionId = "session",
        ActorId = "actor",
    });
    Assert(next.Delivery is null, "empty atomic action delivery was not decoded");

    RuntimeActionRecoveryResponse recovery = await client.RecoverActionAsync(new RuntimeActionRecoveryRequest
    {
        SessionId = "session",
        ActorId = "actor",
    });
    Assert(recovery.Delivery is null, "empty recovery action delivery was not decoded");

    await AssertThrowsAsync<RuntimeHttpException>(() => client.RecordActionReceiptAsync(new RuntimeActionReceiptRequest
    {
        SessionId = "session",
        ActorId = "actor",
        OperationId = operationId,
        ClaimToken = "claim-token",
        Attempt = "7",
        Outcome = ReceiptOutcome.Succeeded,
        Result = new { applied = true },
    }));
    await AssertThrowsAsync<RuntimeHttpException>(() => client.ReconcileActionAsync(new RuntimeActionReconcileRequest
    {
        SessionId = "session",
        ActorId = "actor",
        OperationId = operationId,
        ClaimToken = "claim-token",
        Attempt = "7",
        Resolution = Resolution.Applied,
        Result = new { applied = true },
    }));

    Assert(
        handler.RequestPaths.SequenceEqual([
            "/runtime/v1/actions/next",
            "/runtime/v1/actions/recovery",
            "/runtime/v1/actions/receipt",
            "/runtime/v1/actions/reconcile",
        ]),
        "atomic action exchange paths changed");
    Assert(
        handler.RequestBodies[1] == "{\"actorId\":\"actor\",\"sessionId\":\"session\"}",
        "action recovery selector changed");
    foreach (string body in handler.RequestBodies.Skip(2))
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("operationId").GetString() == operationId, "action operation fence changed");
        Assert(root.GetProperty("claimToken").GetString() == "claim-token", "action claim fence changed");
        Assert(root.GetProperty("attempt").GetString() == "7", "action attempt fence changed");
    }
}

static async Task TestRecoveryRejectsDispatchAsync()
{
    const string response =
        "{\"delivery\":{\"kind\":\"dispatch\",\"action\":{\"actionName\":\"world.move\",\"actorId\":\"actor\",\"attempt\":\"1\",\"claim\":{\"attempt\":\"1\",\"claimToken\":\"claim-token\",\"claimantId\":\"host\",\"claimedAt\":\"2026-08-30T00:00:00.000Z\",\"expiresAt\":\"2026-08-30T00:01:00.000Z\"},\"conflictKeys\":[],\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"createdAt\":\"2026-08-30T00:00:00.000Z\",\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"1\",\"revision\":\"1\"},\"intentFingerprint\":\"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3\",\"operationId\":\"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62\",\"operationKey\":\"move\",\"payload\":{},\"payloadFingerprint\":\"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a\",\"replayPolicy\":\"never\",\"sessionId\":\"session\",\"status\":\"claimed\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}}}";
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, response));
    using RuntimeClient client = CreateClient(handler, 8192, 8192);

    Exception failure = await AssertThrowsAsync<Exception>(() => client.RecoverActionAsync(new RuntimeActionRecoveryRequest
    {
        SessionId = "session",
        ActorId = "actor",
    }));
    Assert(failure.Message.Contains("RuntimeActionRecoveryDeliveryKind", StringComparison.Ordinal),
        "the recovery dispatch did not fail at the reconcile-only protocol discriminator");
    Assert(handler.RequestCount == 1, "the rejected recovery dispatch was retried or skipped");
}

static async Task TestActorRecoveryReconcilesWithoutDispatchAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()),
        _ => JsonResponse(HttpStatusCode.OK, AppliedReconciliationResponse()),
        _ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"));
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    int reconciliations = 0;

    long recovered = await client.RecoverActorActionsAsync(
        "session",
        "actor",
        new RuntimeActorActionRecoveryOptions
        {
            MaximumActions = 1,
            Timeout = TimeSpan.FromSeconds(1),
            Reconcile = (delivery, _) =>
            {
                reconciliations++;
                Assert(delivery.Action.Status == RuntimeActionRecordStatus.Claimed,
                    "actor recovery changed the claimed Action before reconciliation");
                return Task.FromResult(new RuntimeActionRecoveryResolution
                {
                    Resolution = Resolution.Applied,
                    Result = new { applied = true },
                });
            },
        });

    Assert(recovered == 1 && reconciliations == 1, "actor recovery did not drain exactly one Action");
    Assert(
        handler.RequestPaths.SequenceEqual([
            "/runtime/v1/actions/recovery",
            "/runtime/v1/actions/reconcile",
            "/runtime/v1/actions/recovery",
        ]),
        "actor recovery left the reconcile-only endpoint sequence");
    Assert(
        handler.RequestBodies[1].Contains("\"resolution\":\"applied\"", StringComparison.Ordinal)
        && handler.RequestBodies[1].Contains("\"operationId\":\"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62\"", StringComparison.Ordinal)
        && handler.RequestBodies[1].Contains("\"claimToken\":\"claim-token\"", StringComparison.Ordinal),
        "actor recovery did not preserve the Action reconciliation fences");

    var result = new Dictionary<string, object?> { ["applied"] = true };
    var modelResult = new Dictionary<string, object?> { ["note"] = "initial" };
    var authority = new RuntimeActionReconcileRequestAuthorityAfter
    {
        TimelineId = "world",
        Generation = "2",
        Revision = "3",
    };
    var mutationHandler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()),
        _ =>
        {
            result["applied"] = false;
            modelResult["note"] = "mutated";
            authority.Revision = "99";
            return JsonResponse(HttpStatusCode.OK, AppliedSnapshotReconciliationResponse());
        },
        _ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"));
    using RuntimeClient mutationClient = CreateClient(mutationHandler, 8192, 8192);
    long mutationRecovered = await mutationClient.RecoverActorActionsAsync(
        "session",
        "actor",
        RecoveryOptions((_, _) => Task.FromResult(new RuntimeActionRecoveryResolution
        {
            Resolution = Resolution.Applied,
            Result = result,
            ModelResult = modelResult,
            AuthorityAfter = authority,
        })));
    Assert(mutationRecovered == 1, "post-callback DTO mutation invalidated the submitted reconciliation snapshot");
}

static async Task TestActorRecoveryGuardsAsync()
{
    int invalidCallbacks = 0;
    var invalidHandler = new SequenceHandler(_ => JsonResponse(
        HttpStatusCode.OK,
        ClaimedRecoveryResponse().Replace("\"actorId\":\"actor\"", "\"actorId\":\"other\"", StringComparison.Ordinal)));
    using (RuntimeClient client = CreateClient(invalidHandler, 8192, 8192))
    {
        await AssertThrowsAsync<RuntimeRunSessionIdentityException>(() => client.RecoverActorActionsAsync(
            "session",
            "actor",
            RecoveryOptions((_, _) =>
            {
                invalidCallbacks++;
                return Task.FromResult(new RuntimeActionRecoveryResolution { Resolution = Resolution.Applied });
            })));
    }
    Assert(invalidCallbacks == 0, "an invalid recovered Action reached the game callback");

    var limitHandler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()),
        _ => JsonResponse(HttpStatusCode.OK, AppliedReconciliationResponse()),
        _ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()));
    using (RuntimeClient client = CreateClient(limitHandler, 8192, 8192))
    {
        await AssertThrowsAsync<RuntimeActorActionRecoveryLimitException>(() => client.RecoverActorActionsAsync(
            "session",
            "actor",
            RecoveryOptions((_, _) => Task.FromResult(new RuntimeActionRecoveryResolution
            {
                Resolution = Resolution.Applied,
                Result = new { applied = true },
            }))));
    }

    var timeoutHandler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()));
    using (RuntimeClient client = CreateClient(timeoutHandler, 8192, 8192))
    {
        RuntimeRunSessionHandlerTimeoutException timeout =
            await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(() => client.RecoverActorActionsAsync(
                "session",
                "actor",
                new RuntimeActorActionRecoveryOptions
                {
                    MaximumActions = 1,
                    Timeout = TimeSpan.FromMilliseconds(1),
                    Reconcile = (_, _) =>
                        new TaskCompletionSource<RuntimeActionRecoveryResolution>(
                            TaskCreationOptions.RunContinuationsAsynchronously).Task,
                }));
        Assert(timeout.Handler == "action recovery", "actor recovery reported the wrong callback deadline");
    }

    var blockingHandler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()));
    using (RuntimeClient client = CreateClient(blockingHandler, 8192, 8192))
    {
        await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(() => client.RecoverActorActionsAsync(
            "session",
            "actor",
            new RuntimeActorActionRecoveryOptions
            {
                MaximumActions = 1,
                Timeout = TimeSpan.FromMilliseconds(1),
                Reconcile = (_, _) =>
                {
                    Thread.Sleep(100);
                    return Task.FromResult(new RuntimeActionRecoveryResolution { Resolution = Resolution.Applied });
                },
            }));
        await Task.Delay(150);
        Assert(blockingHandler.RequestCount == 1, "a synchronously blocked recovery callback submitted after timeout");
    }

    var throwingCancellationHandler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()));
    using (RuntimeClient client = CreateClient(throwingCancellationHandler, 8192, 8192))
    {
        RuntimeRunSessionHandlerTimeoutException timeout =
            await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(() => client.RecoverActorActionsAsync(
                "session",
                "actor",
                new RuntimeActorActionRecoveryOptions
                {
                    MaximumActions = 1,
                    Timeout = TimeSpan.FromMilliseconds(1),
                    Reconcile = (_, cancellationToken) =>
                    {
                        cancellationToken.Register(() => throw new InvalidOperationException("host cancellation failed"));
                        return new TaskCompletionSource<RuntimeActionRecoveryResolution>(
                            TaskCreationOptions.RunContinuationsAsynchronously).Task;
                    },
                }));
        Assert(timeout.Handler == "action recovery", "a throwing cancellation callback escaped the recovery boundary");
    }

    var blockingCancellationHandler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()));
    using (RuntimeClient client = CreateClient(blockingCancellationHandler, 8192, 8192))
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(() => client.RecoverActorActionsAsync(
            "session",
            "actor",
            new RuntimeActorActionRecoveryOptions
            {
                MaximumActions = 1,
                Timeout = TimeSpan.FromMilliseconds(10),
                Reconcile = (_, cancellationToken) =>
                {
                    cancellationToken.Register(() => Thread.Sleep(Timeout.Infinite));
                    return new TaskCompletionSource<RuntimeActionRecoveryResolution>(
                        TaskCreationOptions.RunContinuationsAsynchronously).Task;
                },
            }));
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "a blocking cancellation callback defeated the hard timeout");
    }

    var unresolvedHandler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, ClaimedRecoveryResponse()),
        _ => JsonResponse(HttpStatusCode.OK, UnknownReconciliationResponse()));
    using (RuntimeClient client = CreateClient(unresolvedHandler, 8192, 8192))
    {
        RuntimeActorActionRecoveryUnresolvedException unresolved =
            await AssertThrowsAsync<RuntimeActorActionRecoveryUnresolvedException>(() => client.RecoverActorActionsAsync(
                "session",
                "actor",
                RecoveryOptions((_, _) => Task.FromResult(new RuntimeActionRecoveryResolution
                {
                    Resolution = Resolution.Unknown,
                }))));
        Assert(
            unresolved.OperationId == "act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62",
            "actor recovery lost the unresolved operation identity");
    }
}

static async Task TestAttachRunSessionAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"),
        _ => SseResponse(
            """
            id: 42
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"42","revision":"42","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            id: 43
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"43","revision":"43","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}

            """ + "\n\n"));
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    await using RuntimeRunSession session = await client.AttachRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunAccepted
        {
            RunId = "run",
            InitialCoordinate = new InitialCoordinate
            {
                SessionId = "session",
                ActorId = "actor",
                RunId = "run",
                Turn = "0",
            },
            EventCursor = "41",
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            Recovery = RecoveryOptions((_, _) => throw new InvalidOperationException("empty recovery invoked callback")),
        });

    RuntimeSseFrameEvent terminal = await session.Completion;
    Assert(terminal.Data.Type == DataType.RunCompleted, "attached run session did not reach its exact terminal event");
    Assert(
        handler.RequestPaths.SequenceEqual(["/runtime/v1/actions/recovery", "/runtime/v1/events/stream"]),
        "attach submitted a replacement run or skipped actor recovery");
    Assert(
        handler.RequestQueries[1].Contains("afterRevision=41", StringComparison.Ordinal),
        "attach did not resume from the persisted admission cursor");
}

static RuntimeActorActionRecoveryOptions RecoveryOptions(
    Func<RuntimeActionRecoveryDelivery, CancellationToken, Task<RuntimeActionRecoveryResolution>> reconcile) =>
    new()
    {
        MaximumActions = 1,
        Timeout = TimeSpan.FromSeconds(1),
        Reconcile = reconcile,
    };

static string ClaimedRecoveryResponse() =>
    """{"delivery":{"kind":"reconcile","action":{"actionName":"world.move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","replayPolicy":"never","sessionId":"session","status":"claimed","updatedAt":"2026-08-30T00:00:00.000Z"}}}""";

static string AppliedReconciliationResponse() =>
    """{"action":{"actionName":"world.move","actorId":"actor","attempt":"1","conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","receipt":{"operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","claimToken":"claim-token","attempt":"1","outcome":"succeeded","receivedAt":"2026-08-30T00:00:01.000Z","result":{"applied":true}},"replayPolicy":"never","sessionId":"session","status":"succeeded","updatedAt":"2026-08-30T00:00:01.000Z"}}""";

static string AppliedSnapshotReconciliationResponse() =>
    """{"action":{"actionName":"world.move","actorId":"actor","attempt":"1","conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","receipt":{"operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","claimToken":"claim-token","attempt":"1","outcome":"succeeded","receivedAt":"2026-08-30T00:00:01.000Z","result":{"applied":true},"modelResult":{"note":"initial"},"authorityAfter":{"timelineId":"world","generation":"2","revision":"3"}},"replayPolicy":"never","sessionId":"session","status":"succeeded","updatedAt":"2026-08-30T00:00:01.000Z"}}""";

static string UnknownReconciliationResponse() =>
    """{"action":{"actionName":"world.move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","receipt":{"operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","claimToken":"claim-token","attempt":"1","outcome":"unknown","receivedAt":"2026-08-30T00:00:01.000Z"},"replayPolicy":"never","sessionId":"session","status":"uncertain","updatedAt":"2026-08-30T00:00:01.000Z"}}""";

static void AssertDeferredRequestBody(string body, bool includeOperation)
{
    using JsonDocument document = JsonDocument.Parse(body);
    JsonElement root = document.RootElement;
    Assert(root.GetProperty("sessionId").GetString() == "session", "deferred request session changed");
    Assert(root.GetProperty("actorId").GetString() == "actor", "deferred request actor changed");
    Assert(
        root.EnumerateObject().Count() == (includeOperation ? 3 : 2),
        "deferred request contained unexpected fields");
    Assert(
        !includeOperation || root.GetProperty("operationId").GetString() == "operation",
        "deferred request operation changed");
}

static async Task TestAccessTokenValidationAsync()
{
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, "abc\r\ndef");

    await AssertThrowsAsync<ArgumentException>(() => client.InitializeAsync());
    Assert(handler.RequestCount == 0, "invalid bearer data reached the HTTP transport");
}

static async Task TestRequestLimitAsync()
{
    var request = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
    const string exactBody = "{\"actorId\":\"actor\",\"sessionId\":\"session\"}";
    int exactBytes = Encoding.UTF8.GetByteCount(exactBody);
    var exactHandler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"));
    using (RuntimeClient exactClient = CreateClient(exactHandler, 1024, 1024, maxRequestBytes: exactBytes))
    {
        await exactClient.NextActionAsync(request);
        Assert(exactHandler.RequestBodies.Single() == exactBody, "an exact-size JSON request changed in transport");
    }

    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, "{\"delivery\":null}"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, maxRequestBytes: exactBytes - 1);

    await AssertThrowsAsync<RuntimeRequestLimitException>(() => client.NextActionAsync(request));
    Assert(handler.RequestCount == 0, "an oversized JSON request reached the HTTP transport");

    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromTicks(1),
        MaximumDelay = TimeSpan.FromTicks(1),
        MaximumConnectionAttempts = 1,
    };
    await AssertThrowsAsync<RuntimeRequestLimitException>(async () =>
    {
        await foreach (RuntimeActionSseFrame _ in client.StreamActionsAsync(request, retry))
        {
        }
    });
    Assert(handler.RequestCount == 0, "an oversized SSE POST body reached the HTTP transport");

    var streamHandler = new SequenceHandler(_ => SseResponse(string.Empty));
    using RuntimeClient streamClient = CreateClient(streamHandler, 1024, 1024, maxRequestBytes: exactBytes);
    var mutableRequest = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
    IAsyncEnumerable<RuntimeActionSseFrame> stream = streamClient.StreamActionsAsync(mutableRequest, retry);
    mutableRequest.ActorId = "changed";
    await AssertThrowsAsync<RuntimeSseDisconnectedException>(async () =>
    {
        await foreach (RuntimeActionSseFrame _ in stream)
        {
        }
    });
    Assert(streamHandler.RequestPaths.Single() == "/runtime/v1/actions/stream", "action SSE path changed");
    Assert(streamHandler.RequestBodies.Single() == exactBody, "action SSE selector mutated after stream creation");

    var runHandler = new SequenceHandler(_ => SseResponse(string.Empty));
    using RuntimeClient runClient = CreateClient(runHandler, 1024, 1024);
    var runRequest = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor", RunId = "run" };
    IAsyncEnumerable<RuntimeActionSseFrame> runStream = runClient.StreamActionsAsync(runRequest, retry);
    runRequest.RunId = "changed";
    await AssertThrowsAsync<RuntimeSseDisconnectedException>(async () =>
    {
        await foreach (RuntimeActionSseFrame _ in runStream)
        {
        }
    });
    Assert(
        runHandler.RequestBodies.Single() == "{\"actorId\":\"actor\",\"runId\":\"run\",\"sessionId\":\"session\"}",
        "action SSE selector dropped or mutated the exact run identity");
}

static async Task TestHttpResponseLimitAsync()
{
    const string body = "{\"protocolVersion\":\"1\",\"capabilities\":[]}";
    var handler = new SequenceHandler(_ => JsonResponse(HttpStatusCode.OK, body));
    using RuntimeClient client = CreateClient(handler, Encoding.UTF8.GetByteCount(body) - 1, 1024);

    await AssertThrowsAsync<RuntimeResponseLimitException>(() => client.InitializeAsync());
}

static async Task TestSseReconnectCursorAsync()
{
    var handler = new SequenceHandler(
        _ => SseResponse("id: 4\ndata: {\"type\":\"cursor\",\"revision\":\"4\"}\n\n"),
        _ => SseResponse("id: 5\ndata: {\"type\":\"cursor\",\"revision\":\"5\"}\n\n"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024);
    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromTicks(1),
        MaximumDelay = TimeSpan.FromTicks(1),
        MaximumConnectionAttempts = 2,
    };
    var revisions = new List<string?>();

    try
    {
        await foreach (RuntimeSseFrame frame in client.StreamEventsAsync("session", "actor", null, retry))
        {
            revisions.Add(frame.Revision);
        }
    }
    catch (RuntimeSseDisconnectedException exception)
    {
        Assert(exception.ConnectionAttempts == 2, "SSE attempt count was not preserved");
    }

    Assert(revisions.SequenceEqual(["4", "5"]), "SSE frames were not decoded across reconnect");
    Assert(handler.LastEventIds.SequenceEqual([null, "4"]), "Last-Event-ID was not advanced on reconnect");
}

static async Task TestSseRetryDelayBoundsAsync()
{
    var handler = new SequenceHandler(_ => SseResponse("data: {}\n\n"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024);
    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromMilliseconds(1),
        MaximumDelay = TimeSpan.FromMilliseconds(4_294_967_295d),
        MaximumConnectionAttempts = 1,
    };

    await AssertThrowsAsync<ArgumentOutOfRangeException>(async () =>
    {
        await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
        {
        }
    });
    Assert(handler.RequestCount == 0, "an unsupported Task.Delay value reached the HTTP transport");
}

static async Task TestSseFailureClassificationAsync()
{
    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromTicks(1),
        MaximumDelay = TimeSpan.FromTicks(1),
        MaximumConnectionAttempts = 2,
    };

    var unavailable = new SequenceHandler(
        _ => JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"),
        _ => JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
    using (RuntimeClient client = CreateClient(unavailable, 1024, 1024))
    {
        await AssertThrowsAsync<RuntimeSseDisconnectedException>(async () =>
        {
            await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
            {
            }
        });
    }
    Assert(unavailable.RequestCount == 2, "a retryable SSE 5xx did not use the explicit finite schedule");

    var redirected = new SequenceHandler(_ =>
    {
        var response = JsonResponse(HttpStatusCode.TemporaryRedirect, "");
        response.Headers.Location = new Uri("https://redirect.invalid");
        return response;
    });
    using (RuntimeClient client = CreateClient(redirected, 1024, 1024))
    {
        await AssertThrowsAsync<RuntimeRedirectException>(async () =>
        {
            await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
            {
            }
        });
    }
    Assert(redirected.RequestCount == 1, "a definitive SSE redirect was retried");

    var malformed = new SequenceHandler(
        _ => SseResponse("data: {\n\n"),
        _ => SseResponse("data: {\n\n"));
    using (RuntimeClient client = CreateClient(malformed, 1024, 1024))
    {
        await AssertThrowsAsync<JsonException>(async () =>
        {
            await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
            {
            }
        });
    }
    Assert(malformed.RequestCount == 1, "a definitive malformed SSE event was retried");

    var invalidToken = new SequenceHandler(_ => SseResponse(string.Empty));
    using (RuntimeClient client = CreateClient(invalidToken, 1024, 1024, "bad\r\ntoken"))
    {
        await AssertThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
            {
            }
        });
    }
    Assert(invalidToken.RequestCount == 0, "invalid SSE bearer data reached the HTTP transport");
}

static async Task TestExactRunSessionAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"41"}"""),
        _ => SseResponse(
            """
            id: 42
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"42","revision":"42","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"1fdd692c01738c0397f631157215b811c1f7299f552242eaaa7752e136996c59","admissionAuthority":{"scope":"planning","generation":"2"}}}}

            id: 43
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"43","revision":"43","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}

            """ + "\n\n"));
    using RuntimeClient client = CreateClient(handler, 4096, 4096);
    await using RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonDocument.Parse("""{"z":1e-7,"astral":"😀","a":1}""").RootElement.Clone(),
			AdmissionAuthority = new RuntimeRunAdmissionAuthority { Scope = "planning", Generation = "2" },
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = new RuntimeSseRetryPolicy
            {
                DefaultDelay = TimeSpan.FromTicks(1),
                MaximumDelay = TimeSpan.FromTicks(1),
                MaximumConnectionAttempts = 1,
            },
        });

    RuntimeSseFrameEvent terminal = await session.Completion;

    Assert(terminal.Data.Type == DataType.RunCompleted, "run session did not resolve from the exact terminal event");
    Assert(session.Accepted.EventCursor == "41", "run session did not retain the admission cursor");
    Assert(handler.RequestPaths.Count == 2, "run session used an unexpected number of requests");
    Assert(handler.RequestQueries[1].Contains("afterRevision=41", StringComparison.Ordinal), "run session ignored the admission cursor");
}

static async Task TestRunSessionAdmissionGateAsync()
{
    var paths = new List<string>();
    var handler = new RoutingHandler(request =>
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        lock (paths)
        {
            paths.Add(path);
        }
        return path switch
        {
            "/runtime/v1/runs" => JsonResponse(
                HttpStatusCode.Accepted,
                """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
            "/runtime/v1/events/stream" => SseResponse(
                """
                data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}

                """ + "\n\n"),
            _ => throw new InvalidOperationException("Action and Approval consumers started before run admission."),
        };
    });
    using RuntimeClient client = CreateClient(handler, 4096, 4096);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(1),
                OnDelivery = (_, _) => Task.CompletedTask,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromMilliseconds(10),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) => Task.CompletedTask,
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    lock (paths)
    {
        Assert(
            paths.SequenceEqual(["/runtime/v1/runs", "/runtime/v1/events/stream"]),
            "run session started child consumers before validating admission");
    }
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunSessionActivationGateAsync()
{
    var paths = new List<string>();
    var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new RoutingHandler(request =>
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        lock (paths)
        {
            paths.Add(path);
        }
        return path switch
        {
            "/runtime/v1/runs" => JsonResponse(
                HttpStatusCode.Accepted,
                """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
            "/runtime/v1/events/stream" => SseResponse(
                """
                data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

                data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"aborted"}}}

                """ + "\n\n"),
            _ => throw new InvalidOperationException("Action and Approval observers opened before run activation."),
        };
    });
    using RuntimeClient client = CreateClient(handler, 4096, 4096);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(1),
                Handler = (runtimeEvent, cancellationToken) =>
                {
                    if (runtimeEvent.Data.Type != DataType.RunQueued) return Task.CompletedTask;
                    queued.TrySetResult();
                    return releaseQueued.Task.WaitAsync(cancellationToken);
                },
            },
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(1),
                OnDelivery = (_, _) => Task.CompletedTask,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromMilliseconds(10),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) => Task.CompletedTask,
            },
        });

    await queued.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await Task.Delay(TimeSpan.FromMilliseconds(20));
    lock (paths)
    {
        Assert(
            paths.SequenceEqual(["/runtime/v1/runs", "/runtime/v1/events/stream"]),
            "queued run session opened Action or Approval observation before run.started");
    }
    releaseQueued.TrySetResult();
    RuntimeSseFrameEvent terminal = await session.Completion;
    Assert(
        terminal.Data.Type == DataType.RunCompleted && terminal.Data.Status == DataStatus.Aborted,
        "queued run session did not settle from its direct terminal event");
    await session.DisposeAsync();
}

static async Task TestRunSessionRejectsInvalidApprovalBackoffAsync()
{
    int requests = 0;
    var handler = new RoutingHandler(_ =>
    {
        requests++;
        return JsonResponse(HttpStatusCode.OK, "{}");
    });
    using RuntimeClient client = CreateClient(handler, 4096, 4096);

    await AssertThrowsAsync<ArgumentException>(() => client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.Zero,
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) => Task.CompletedTask,
            },
        }));
    Assert(requests == 0, "invalid Approval backoff reached the Runtime transport");
}

static async Task TestRunSessionApprovalBackoffAsync()
{
    const string approvalId = "apr_c6b5ed45ba19d42d5c258e3e35a107464a14f1270bf8c3cb8e011188f920fd4e";
    const string request = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_c6b5ed45ba19d42d5c258e3e35a107464a14f1270bf8c3cb8e011188f920fd4e","requestFingerprint":"6a4cc166442343b55c1a6e9366a249c045d13a98c1bf87a08cea4fc75306ebd2","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"presentation":{"kind":"game-choice","optionId":"choice"},"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    int approvalLists = 0;
    var firstList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thirdList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fourthList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new AsyncRoutingHandler(async (httpRequest, _) =>
    {
        string path = httpRequest.RequestUri?.AbsolutePath ?? string.Empty;
        if (path == "/runtime/v1/runs")
        {
            return JsonResponse(
                HttpStatusCode.Accepted,
                """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}""");
        }
        if (path == "/runtime/v1/events/stream")
        {
            return SseResponse(
                """
                data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

                data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

                """ + "\n\n");
        }
        if (path == "/runtime/v1/approvals/wait")
        {
            int current = Interlocked.Increment(ref approvalLists);
            if (current == 1) firstList.TrySetResult();
            if (current == 2) secondList.TrySetResult();
            if (current == 3) thirdList.TrySetResult();
            if (current == 4) fourthList.TrySetResult();
            if (current is 2 or 4)
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
            return JsonResponse(
                HttpStatusCode.OK,
                current <= 2
                    ? "{\"approvals\":[{\"request\":" + request
                        + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}],\"cursor\":\"1111111111111111111111111111111111111111111111111111111111111111\"}"
                    : """{"approvals":[],"cursor":"2222222222222222222222222222222222222222222222222222222222222222"}""");
        }
        if (path == "/runtime/v1/approvals/decision")
        {
            return JsonResponse(
                HttpStatusCode.OK,
                "{\"approval\":{\"request\":" + request
                    + ",\"status\":\"approved\",\"revision\":\"2\",\"updatedAt\":\"2026-08-30T00:00:01.000Z\","
                    + "\"decision\":{\"decision\":\"approved\",\"reviewerId\":\"player\",\"decidedAt\":\"2026-08-30T00:00:01.000Z\",\"reason\":\"allow\"}}}");
        }
        throw new InvalidOperationException("Unexpected Approval backoff route.");
    });
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Handler = HoldStartedEvent,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromMilliseconds(500),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) => Task.CompletedTask,
            },
        });

    await firstList.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await secondList.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await Task.Delay(TimeSpan.FromMilliseconds(50));
    Assert(Volatile.Read(ref approvalLists) == 2, "unchanged Approval discovery opened more than one long wait");
    RuntimeApproval decided = await session.DecideApprovalAsync(
        approvalId,
        RuntimeApprovalDecisionRequestDecision.Approve,
        "allow");
    Assert(decided.Status == RuntimeApprovalStatus.Approved, "run session did not accept the local Approval decision");
    await thirdList.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
    await fourthList.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
    session.Close();
    await session.DisposeAsync();
}

static async Task TestRunSessionRejectsMalformedAdmissionAsync()
{
    var paths = new List<string>();
    var handler = new RoutingHandler(request =>
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        lock (paths)
        {
            paths.Add(path);
        }
        return path switch
        {
            "/runtime/v1/runs" => JsonResponse(
                HttpStatusCode.Accepted,
                """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
            "/runtime/v1/events/stream" => SseResponse(
                """
                data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"public"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

                """ + "\n\n"),
            _ => throw new InvalidOperationException("Child consumer opened for malformed admission."),
        };
    });
    using RuntimeClient client = CreateClient(handler, 4096, 4096);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(1),
                OnDelivery = (_, _) => Task.CompletedTask,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromMilliseconds(10),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) => Task.CompletedTask,
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    lock (paths)
    {
        Assert(
            paths.SequenceEqual(["/runtime/v1/runs", "/runtime/v1/events/stream"]),
            "run session started child consumers for malformed admission");
    }
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunSessionRejectsMalformedPostAdmissionEventAsync()
{
    var delivered = new List<DataType>();
    var handler = new SequenceHandler(
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        _ => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"foreign-input"}}}

            """ + "\n\n"));
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(1),
                Handler = (runtimeEvent, _) =>
                {
                    delivered.Add(runtimeEvent.Data.Type);
                    return Task.CompletedTask;
                },
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    Assert(delivered.SequenceEqual([DataType.RunQueued]), "run session delivered a malformed post-admission event");
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunMetricsAsync()
{
    foreach (string variant in new[] { "completed", "aborted", "failed", "bad-duration", "bad-provider-first-response", "bad-provider-first-response-decimal", "bad-count", "private-content" })
    {
        bool invalid = variant.StartsWith("bad-", StringComparison.Ordinal) || variant == "private-content";
        string status = invalid ? "completed" : variant;
        var metrics = new Dictionary<string, object>
        {
            ["type"] = "run.metrics", ["unit"] = "microseconds", ["segmentStatus"] = status,
            ["durations"] = new { contextAndRecall = "100", provider = "200", durable = variant == "bad-duration" ? "351" : "30", native = "50", total = "350", firstResponse = "250", providerFirstResponse = variant == "bad-provider-first-response" ? "351" : variant == "bad-provider-first-response-decimal" ? "01" : "150" },
            ["counts"] = new { providerAttempts = "2", retries = variant == "bad-count" ? "2" : "0", fallbacks = "0", toolCalls = "1" },
        };
        if (variant == "private-content") metrics["content"] = new[] { new { type = "text", text = "private" } };
        string Envelope(int revision, object data) => "data: " + JsonSerializer.Serialize(new
        {
            type = "event",
            @event = new
            {
                protocolVersion = "1", eventId = "event-" + revision, sequence = revision.ToString(), revision = revision.ToString(),
                occurredAt = "2026-08-30T00:00:00.000Z", coordinate = new { sessionId = "session", actorId = "actor", runId = "run", turn = "0" },
                audience = new { visibility = "owner" }, data,
            },
        }) + "\n\n";
        var handler = new SequenceHandler(
            _ => JsonResponse(HttpStatusCode.Accepted, """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
            _ => SseResponse(
                Envelope(1, new { type = "run.queued", inputId = "input", inputFingerprint = "2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210" })
                + Envelope(2, new { type = "run.started", inputId = "input" })
                + Envelope(3, metrics) + Envelope(4, new { type = "run.completed", status })));
        using RuntimeClient client = CreateClient(handler, 8192, 8192);
        RuntimeRunSession session = await client.StartRunSessionAsync(
            new RuntimeRunRequest { SessionId = "session", ActorId = "actor", InputId = "input", Payload = JsonSerializer.SerializeToElement(new { }) },
            new RuntimeRunSessionOptions { EventRetryPolicy = OneAttemptRetry() });
        if (invalid)
        {
            await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
            await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
        }
        else
        {
            await session.Completion;
            await session.DisposeAsync();
        }
    }
}

static async Task TestRunSessionActionAndEventCallbacksAsync()
{
    var actionSeen = new TaskCompletionSource<RuntimeActionDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
    var eventTypes = new List<DataType>();
    var handler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"3","revision":"3","occurredAt":"2026-08-30T00:00:02.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}

            """ + "\n\n"),
        "/runtime/v1/actions/stream" => SseResponse(
            """
            data: {"type":"delivery","delivery":{"kind":"dispatch","action":{"actionName":"world.move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","replayPolicy":"never","sessionId":"session","status":"claimed","updatedAt":"2026-08-30T00:00:00.000Z"}}}

            """ + "\n\n"),
        _ => throw new InvalidOperationException("Unexpected Runtime session route."),
    });
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    await using RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(1),
                Handler = async (runtimeEvent, cancellationToken) =>
                {
                    eventTypes.Add(runtimeEvent.Data.Type);
                    if (runtimeEvent.Data.Type == DataType.RunStarted)
                    {
                        await actionSeen.Task.WaitAsync(cancellationToken);
                    }
                },
            },
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(2),
                OnDelivery = async (delivery, cancellationToken) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () => throw new InvalidOperationException("host cancellation callback failed"));
                    actionSeen.TrySetResult(delivery);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            },
        });

    RuntimeSseFrameEvent terminal = await session.Completion;

    RuntimeActionDelivery delivery = await actionSeen.Task;
    Assert(delivery.Action.Coordinate.RunId == "run", "run session delivered an Action for another run");
    Assert(
        eventTypes.SequenceEqual([DataType.RunQueued, DataType.RunStarted, DataType.RunCompleted]),
        "run session event callback order changed");
    Assert(terminal.Data.Type == DataType.RunCompleted, "run session did not converge after its child workers");
    Assert(
        handler.RequestBodies.Any(body => body.Contains("\"runId\":\"run\"", StringComparison.Ordinal)),
        "run session did not bind the Action stream to the exact run");
}

static async Task TestRunSessionRejectsInvalidActionDeliveryAsync()
{
    int deliveries = 0;
    var handler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/actions/stream" => SseResponse(
            """
            data: {"type":"delivery","delivery":{"kind":"dispatch","action":{"actionName":"world.move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","replayPolicy":"never","sessionId":"session","status":"uncertain","updatedAt":"2026-08-30T00:00:00.000Z"}}}

            """ + "\n\n"),
        _ => throw new InvalidOperationException("Unexpected invalid Action route."),
    });
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                Handler = HoldStartedEvent,
            },
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(1),
                OnDelivery = (_, _) =>
                {
                    deliveries++;
                    return Task.CompletedTask;
                },
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    Assert(deliveries == 0, "run session delivered a semantically invalid Action to the game");
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunSessionRejectsOpaqueActionIdentifiersAsync()
{
    int deliveries = 0;
    var handler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/actions/stream" => SseResponse(
            """
            data: {"type":"delivery","delivery":{"kind":"dispatch","action":{"actionName":"world\u0001move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"62bd976a104d990da3acacb0962757bfba0d444d75804db8cbb68be0736a5c4d","operationId":"act_591a3cf688331cce430bb822ebb5581e68b156c563fac4ad88e2fe9b2e00320b","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","replayPolicy":"never","sessionId":"session","status":"claimed","updatedAt":"2026-08-30T00:00:00.000Z"}}}

            """ + "\n\n"),
        _ => throw new InvalidOperationException("Unexpected opaque Action identifier route."),
    });
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                Handler = HoldStartedEvent,
            },
            Actions = new RuntimeRunActionOptions
            {
                RetryPolicy = OneAttemptRetry(),
                Timeout = TimeSpan.FromSeconds(1),
                OnDelivery = (_, _) =>
                {
                    deliveries++;
                    return Task.CompletedTask;
                },
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    Assert(deliveries == 0, "run session delivered an Action with a non-opaque identifier to the game");
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunSessionRejectsInvalidApprovalRequestAsync()
{
    int callbacks = 0;
    const string request = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_bf9c83bc93abaaceea0e6f455b627fa9b9e63cb7a639049c9cd98cc0955562a3","requestFingerprint":"22da8cfba56fd7efe780eca31cf5cddd13f6995f1988a260b8cbd3d2dad4c5b7","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    var handler = new RoutingHandler(httpRequest => httpRequest.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/approvals/wait" => JsonResponse(
            HttpStatusCode.OK,
            "{\"approvals\":[{\"request\":" + request
                + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}],\"cursor\":\"1111111111111111111111111111111111111111111111111111111111111111\"}"),
        _ => throw new InvalidOperationException("Unexpected invalid Approval route."),
    });
    using RuntimeClient client = CreateClient(handler, 16384, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                Handler = HoldStartedEvent,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromSeconds(2),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(1),
                OnPending = (_, _) =>
                {
                    callbacks++;
                    throw new InvalidOperationException("Invalid Approval reached the game callback.");
                },
            },
        });

    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.Completion);
    Assert(callbacks == 0, "run session delivered an Approval whose fingerprint did not bind its request");
    await AssertThrowsAsync<RuntimeRunSessionIdentityException>(async () => await session.DisposeAsync());
}

static async Task TestRunSessionAcceptsExplicitNullApprovalPresentationAsync()
{
    const string fingerprint = "22da8cfba56fd7efe780eca31cf5cddd13f6995f1988a260b8cbd3d2dad4c5b7";
    const string request = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_bf9c83bc93abaaceea0e6f455b627fa9b9e63cb7a639049c9cd98cc0955562a3","requestFingerprint":"22da8cfba56fd7efe780eca31cf5cddd13f6995f1988a260b8cbd3d2dad4c5b7","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"presentation":null,"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    var pendingSeen = new TaskCompletionSource<RuntimeApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new RoutingHandler(httpRequest => httpRequest.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/approvals/wait" => JsonResponse(
            HttpStatusCode.OK,
            "{\"approvals\":[{\"request\":" + request
                + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}],\"cursor\":\"1111111111111111111111111111111111111111111111111111111111111111\"}"),
        _ => throw new InvalidOperationException("Unexpected explicit-null Approval route."),
    });
    using RuntimeClient client = CreateClient(handler, 16384, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                Handler = HoldStartedEvent,
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromSeconds(20),
                PageSize = 1,
                Timeout = TimeSpan.FromSeconds(2),
                OnPending = (approval, _) =>
                {
                    pendingSeen.TrySetResult(approval);
                    return Task.CompletedTask;
                },
            },
        });

    RuntimeApproval pending = await pendingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(pending.Request.Presentation is null, "explicit-null Approval presentation changed value");
    Assert(pending.Request.RequestFingerprint == fingerprint, "explicit-null Approval presentation changed identity");
    session.Close();
    await session.DisposeAsync();
}

static async Task TestApprovalResponsesRejectExplicitNullOptionalsAsync()
{
    const string request = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_83959c9349e1a0b5f41ae3a4fe9c0751caf5ea714545c9150d6b8642856405c4","requestFingerprint":"58582f5763fa0222a1bdd946969619f1deeef64cd557d8bd76c752c7d36e0590","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    string[] invalidRecords =
    [
        "{\"request\":" + request
            + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"decision\":null}",
        "{\"request\":" + request
            + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"consumedAt\":null}",
        "{\"request\":" + request
            + ",\"status\":\"approved\",\"revision\":\"2\",\"updatedAt\":\"2026-08-30T00:00:01.000Z\","
            + "\"decision\":{\"decision\":\"approved\",\"reviewerId\":\"player\",\"decidedAt\":\"2026-08-30T00:00:01.000Z\",\"reason\":null}}",
    ];
    foreach (string invalidRecord in invalidRecords)
    {
        var handler = new SequenceHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"approvals\":[" + invalidRecord + "]}"));
        using RuntimeClient client = CreateClient(handler, 16384, 8192);
        await AssertThrowsAsync<JsonException>(() => client.ListApprovalsAsync(new RuntimeApprovalListRequest
        {
            SessionId = "session",
            ActorId = "actor",
            RunId = "run",
            Limit = 1,
        }));
    }
}

static async Task TestRunSessionApprovalDecisionAsync()
{
    const string approvalId = "apr_c6b5ed45ba19d42d5c258e3e35a107464a14f1270bf8c3cb8e011188f920fd4e";
    const string fingerprint = "6a4cc166442343b55c1a6e9366a249c045d13a98c1bf87a08cea4fc75306ebd2";
    const string request = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_c6b5ed45ba19d42d5c258e3e35a107464a14f1270bf8c3cb8e011188f920fd4e","requestFingerprint":"6a4cc166442343b55c1a6e9366a249c045d13a98c1bf87a08cea4fc75306ebd2","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"presentation":{"kind":"game-choice","optionId":"choice"},"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    var pendingSeen = new TaskCompletionSource<RuntimeApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
    var decisionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new RoutingHandler(httpRequest => httpRequest.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"3","revision":"3","occurredAt":"2026-08-30T00:00:02.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"completed"}}}

            """ + "\n\n"),
        "/runtime/v1/approvals/wait" => JsonResponse(
            HttpStatusCode.OK,
            "{\"approvals\":[{\"request\":" + request
                + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}],\"cursor\":\"1111111111111111111111111111111111111111111111111111111111111111\"}"),
        "/runtime/v1/approvals/decision" => JsonResponse(
            HttpStatusCode.OK,
            "{\"approval\":{\"request\":" + request
                + ",\"status\":\"approved\",\"revision\":\"2\",\"updatedAt\":\"2026-08-30T00:00:01.000Z\","
                + "\"decision\":{\"decision\":\"approved\",\"reviewerId\":\"player\",\"decidedAt\":\"2026-08-30T00:00:01.000Z\",\"reason\":\"allow\"}}}"),
        _ => throw new InvalidOperationException("Unexpected Runtime approval route."),
    });
    using RuntimeClient client = CreateClient(handler, 16384, 8192);
    await using RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Handler = async (runtimeEvent, cancellationToken) =>
                {
                    if (runtimeEvent.Data.Type == DataType.RunStarted)
                    {
                        await decisionDone.Task.WaitAsync(cancellationToken);
                    }
                },
            },
            Approvals = new RuntimeRunApprovalOptions
            {
                WaitDuration = TimeSpan.FromSeconds(20),
                PageSize = 8,
                Timeout = TimeSpan.FromSeconds(2),
                OnPending = (approval, _) =>
                {
                    pendingSeen.TrySetResult(approval);
                    return Task.CompletedTask;
                },
            },
        });

    RuntimeApproval pending = await pendingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    RuntimeApproval decided = await session.DecideApprovalAsync(
        approvalId,
        RuntimeApprovalDecisionRequestDecision.Approve,
        "allow");
    decisionDone.TrySetResult();
    RuntimeSseFrameEvent terminal = await session.Completion;

    Assert(pending.Request.RequestFingerprint == fingerprint, "run session changed the pending Approval identity");
    Assert(decided.Status == RuntimeApprovalStatus.Approved, "run session did not return the exact Approval decision");
    Assert(terminal.Data.Type == DataType.RunCompleted, "run session did not finish after Approval settlement");
    Assert(
        handler.RequestBodies.Any(body => body.Contains("\"runId\":\"run\"", StringComparison.Ordinal)),
        "run session did not bind Approval discovery to the exact run");
    Assert(
        handler.RequestBodies.Any(body =>
            body.Contains($"\"approvalId\":\"{approvalId}\"", StringComparison.Ordinal)
            && body.Contains("\"expectedRevision\":\"1\"", StringComparison.Ordinal)
            && body.Contains($"\"requestFingerprint\":\"{fingerprint}\"", StringComparison.Ordinal)),
        "run session did not decide the exact pending Approval");
}

static async Task TestRunSessionHandlerTimeoutsAsync()
{
    var actionHandler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/actions/stream" => SseResponse(
            """
            data: {"type":"delivery","delivery":{"kind":"dispatch","action":{"actionName":"world.move","actorId":"actor","attempt":"1","claim":{"attempt":"1","claimToken":"claim-token","claimantId":"host","claimedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"},"conflictKeys":[],"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"createdAt":"2026-08-30T00:00:00.000Z","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"intentFingerprint":"48bc2df8545845402d5773e73ad21045d10952a84a807cf50aecd101b2ea3ce3","operationId":"act_17c25f5cf4c7e0a4922b929f32a7af823994faec101b177cb2eeb9d28a008c62","operationKey":"move","payload":{},"payloadFingerprint":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","replayPolicy":"never","sessionId":"session","status":"claimed","updatedAt":"2026-08-30T00:00:00.000Z"}}}

            """ + "\n\n"),
        _ => throw new InvalidOperationException("Unexpected Runtime Action deadline route."),
    });
    using (RuntimeClient client = CreateClient(actionHandler, 8192, 8192))
    {
        RuntimeRunSession session = await client.StartRunSessionAsync(
            new RuntimeRunRequest
            {
                SessionId = "session",
                ActorId = "actor",
                InputId = "input",
                Payload = JsonSerializer.SerializeToElement(new { }),
            },
            new RuntimeRunSessionOptions
            {
                EventRetryPolicy = OneAttemptRetry(),
                EventHandler = new RuntimeRunEventHandlerOptions
                {
                    Timeout = TimeSpan.FromSeconds(2),
                    Handler = HoldStartedEvent,
                },
                Actions = new RuntimeRunActionOptions
                {
                    RetryPolicy = OneAttemptRetry(),
                    Timeout = TimeSpan.FromMilliseconds(1),
                    OnDelivery = (_, _) => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task,
                },
            });
        RuntimeRunSessionHandlerTimeoutException exception =
            await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(async () => await session.Completion);
        Assert(exception.Handler == "action", "run session reported the wrong Action handler deadline");
    }

    const string approvalRequest = """{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_83959c9349e1a0b5f41ae3a4fe9c0751caf5ea714545c9150d6b8642856405c4","requestFingerprint":"58582f5763fa0222a1bdd946969619f1deeef64cd557d8bd76c752c7d36e0590","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"1","revision":"1"},"requestedAt":"2026-08-30T00:00:00.000Z","expiresAt":"2026-08-30T00:01:00.000Z"}""";
    var approvalHandler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            """ + "\n\n"),
        "/runtime/v1/approvals/wait" => JsonResponse(
            HttpStatusCode.OK,
            "{\"approvals\":[{\"request\":" + approvalRequest
                + ",\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}],\"cursor\":\"1111111111111111111111111111111111111111111111111111111111111111\"}"),
        _ => throw new InvalidOperationException("Unexpected Runtime Approval deadline route."),
    });
    using (RuntimeClient client = CreateClient(approvalHandler, 16384, 8192))
    {
        RuntimeRunSession session = await client.StartRunSessionAsync(
            new RuntimeRunRequest
            {
                SessionId = "session",
                ActorId = "actor",
                InputId = "input",
                Payload = JsonSerializer.SerializeToElement(new { }),
            },
            new RuntimeRunSessionOptions
            {
                EventRetryPolicy = OneAttemptRetry(),
                EventHandler = new RuntimeRunEventHandlerOptions
                {
                    Timeout = TimeSpan.FromSeconds(2),
                    Handler = HoldStartedEvent,
                },
                Approvals = new RuntimeRunApprovalOptions
                {
                    WaitDuration = TimeSpan.FromSeconds(2),
                    PageSize = 1,
                    Timeout = TimeSpan.FromMilliseconds(1),
                    OnPending = (_, _) => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task,
                },
            });
        RuntimeRunSessionHandlerTimeoutException exception =
            await AssertThrowsAsync<RuntimeRunSessionHandlerTimeoutException>(async () => await session.Completion);
        Assert(exception.Handler == "approval", "run session reported the wrong Approval handler deadline");
    }
}

static async Task TestRunSessionAbortAndLocalCloseAsync()
{
    var startedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseEvents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
    {
        "/runtime/v1/runs" => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        "/runtime/v1/events/stream" => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"started","sequence":"2","revision":"2","occurredAt":"2026-08-30T00:00:01.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.started","inputId":"input"}}}

            data: {"type":"event","event":{"protocolVersion":"1","eventId":"terminal","sequence":"3","revision":"3","occurredAt":"2026-08-30T00:00:02.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"run.completed","status":"aborted"}}}

            """ + "\n\n"),
        "/runtime/v1/control" => JsonResponse(
            HttpStatusCode.OK,
            """{"accepted":true,"coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"}}"""),
        _ => throw new InvalidOperationException("Unexpected Runtime abort route."),
    });
    using (RuntimeClient client = CreateClient(handler, 8192, 8192))
    {
        await using RuntimeRunSession session = await client.StartRunSessionAsync(
            new RuntimeRunRequest
            {
                SessionId = "session",
                ActorId = "actor",
                InputId = "input",
                Payload = JsonSerializer.SerializeToElement(new { }),
            },
            new RuntimeRunSessionOptions
            {
                EventRetryPolicy = OneAttemptRetry(),
                EventHandler = new RuntimeRunEventHandlerOptions
                {
                    Timeout = TimeSpan.FromSeconds(2),
                    Handler = async (runtimeEvent, cancellationToken) =>
                    {
                        if (runtimeEvent.Data.Type == DataType.RunStarted)
                        {
                            startedSeen.TrySetResult();
                            await releaseEvents.Task.WaitAsync(cancellationToken);
                        }
                    },
                },
            });
        await startedSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        RuntimeControlResult result = await session.AbortAsync();
        releaseEvents.TrySetResult();
        RuntimeSseFrameEvent terminal = await session.Completion;

        Assert(result.Accepted, "run session did not return the exact accepted abort result");
        Assert(terminal.Data.Status == DataStatus.Aborted, "run session did not converge on the aborted terminal event");
        Assert(
            handler.RequestBodies.Any(body =>
                body.Contains("\"turn\":\"1\"", StringComparison.Ordinal)
                && body.Contains("\"type\":\"abort\"", StringComparison.Ordinal)),
            "run session abort did not use the latest exact coordinate");
    }

    var queuedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var closeHandler = new SequenceHandler(
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        _ => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            """ + "\n\n"));
    using RuntimeClient closeClient = CreateClient(closeHandler, 8192, 8192);
    await using RuntimeRunSession closeSession = await closeClient.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions
        {
            EventRetryPolicy = OneAttemptRetry(),
            EventHandler = new RuntimeRunEventHandlerOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                Handler = async (_, cancellationToken) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () => throw new InvalidOperationException("host close callback failed"));
                    queuedSeen.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            },
        });
    await queuedSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

    closeSession.Close();

    await AssertThrowsAsync<RuntimeRunSessionClosedException>(async () => await closeSession.Completion);
    Assert(closeHandler.RequestPaths.Count == 2, "local run session close sent a remote control request");
}

static async Task TestRunSessionEarlyEventEndAsync()
{
    var handler = new SequenceHandler(
        _ => JsonResponse(
            HttpStatusCode.Accepted,
            """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}"""),
        _ => SseResponse(
            """
            data: {"type":"event","event":{"protocolVersion":"1","eventId":"queued","sequence":"1","revision":"1","occurredAt":"2026-08-30T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"audience":{"visibility":"owner"},"data":{"type":"run.queued","inputId":"input","inputFingerprint":"2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210"}}}

            """ + "\n\n"));
    using RuntimeClient client = CreateClient(handler, 8192, 8192);
    RuntimeRunSession session = await client.StartRunSessionAsync(
        new RuntimeRunRequest
        {
            SessionId = "session",
            ActorId = "actor",
            InputId = "input",
            Payload = JsonSerializer.SerializeToElement(new { }),
        },
        new RuntimeRunSessionOptions { EventRetryPolicy = OneAttemptRetry() });

    try
    {
        await session.Completion;
        throw new InvalidOperationException("Expected RuntimeRunSessionStreamEndedException.");
    }
    catch (RuntimeRunSessionStreamEndedException exception)
    {
        Assert(exception.Stream == "events", "run session lost the early-ended stream identity");
    }
}

static void TestRuntimeProtocolRejectsUnmappedMembers()
{
    JsonSerializerOptions options = RuntimeProtocolJson.CreateSerializerOptions();
    AssertThrows<JsonException>(() => JsonSerializer.Deserialize<RuntimeSseFrame>(
        """{"type":"cursor","revision":"4","unexpected":true}""",
        options));
}

static void TestGeneratedCanonicalEventShapes()
{
    JsonSerializerOptions options = RuntimeProtocolJson.CreateSerializerOptions();
    RuntimeSseFrame updated = JsonSerializer.Deserialize<RuntimeSseFrame>(
        """{"type":"event","event":{"protocolVersion":"1","eventId":"event-update","sequence":"4","revision":"4","occurredAt":"2026-08-29T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"internal"},"data":{"type":"item.updated","itemId":"tool-item","audience":{"visibility":"internal"},"updateOrdinal":"2","content":[{"type":"text","text":"working"},{"type":"media-summary","mediaType":"image/png","bytes":8,"sha256":"4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6"}]}}}""",
        options) ?? throw new InvalidOperationException("item.updated frame was not decoded");
    FluffyData updatedData = updated.Event?.Data ?? throw new InvalidOperationException("item.updated event was absent");
    var updatedContent = updatedData.Content ?? throw new InvalidOperationException("Tool update content was absent");
    Assert(updatedData.Type == DataType.ItemUpdated, "item.updated type was not decoded");
    Assert(updatedData.UpdateOrdinal == "2", "Tool update ordinal was not decoded");
    Assert(updatedContent[1].Type == PurpleType.MediaSummary, "media summary type was not decoded");
    Assert(updatedContent[1].Bytes == 8, "media summary bytes were not decoded");

    RuntimeSseFrame message = JsonSerializer.Deserialize<RuntimeSseFrame>(
        """{"type":"event","event":{"protocolVersion":"1","eventId":"event-message","sequence":"5","revision":"5","occurredAt":"2026-08-29T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"item.completed","item":{"itemType":"message","itemId":"assistant-item","role":"assistant","content":[{"type":"text","text":"visible","sourceIndex":3}],"audience":{"visibility":"owner"}}}}}""",
        options) ?? throw new InvalidOperationException("assistant item frame was not decoded");
    Assert(message.Event?.Data.Item?.Content?[0].SourceIndex == 3, "assistant source index was not decoded");

    RuntimeSseFrame deferred = JsonSerializer.Deserialize<RuntimeSseFrame>(
        """{"type":"event","event":{"protocolVersion":"1","eventId":"event-turn","sequence":"6","revision":"6","occurredAt":"2026-08-29T00:00:00.000Z","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"audience":{"visibility":"owner"},"data":{"type":"turn.completed","finishReason":"deferred","toolResultItemIds":[]}}}""",
        options) ?? throw new InvalidOperationException("deferred turn frame was not decoded");
    Assert(deferred.Event?.Data.FinishReason == FinishReason.Deferred, "deferred finish reason was not decoded");
}

static void TestGeneratedApprovalPresentation()
{
    JsonSerializerOptions options = RuntimeProtocolJson.CreateSerializerOptions();
    RuntimeApprovalListResponse response = JsonSerializer.Deserialize<RuntimeApprovalListResponse>(
        """{"approvals":[{"request":{"sessionId":"session","actorId":"actor","coordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"1"},"approvalId":"apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","requestFingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","policyId":"high-risk","operationId":"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","intentFingerprint":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","actionName":"world.write","expectedAuthority":{"timelineId":"world","generation":"3","revision":"9"},"presentation":{"kind":"game-choice","optionId":"opaque-option-7","label":"赠送礼物"},"requestedAt":"2026-08-28T00:00:00.000Z","expiresAt":"2026-08-28T00:00:01.000Z"},"status":"pending","revision":"1","updatedAt":"2026-08-28T00:00:00.000Z"}]}""",
        options) ?? throw new InvalidOperationException("approval response was not decoded");
    object? raw = response.Approvals[0].Request.Presentation;
    Assert(raw is JsonElement, "approval presentation did not retain its JSON shape");
    JsonElement presentation = (JsonElement)raw!;
    Assert(presentation.GetProperty("optionId").GetString() == "opaque-option-7", "approval option was not decoded");
    Assert(presentation.GetProperty("label").GetString() == "赠送礼物", "approval label was not decoded");
}

static async Task TestSseEventLimitAsync()
{
    const string body = "data: {\"type\":\"cursor\",\"revision\":\"4\"}\n\n";
    var handler = new SequenceHandler(_ => SseResponse(body));
    int eventPayloadBytes = Encoding.UTF8.GetByteCount(body[..body.IndexOf("\n\n", StringComparison.Ordinal)]);
    using RuntimeClient client = CreateClient(handler, 1024, eventPayloadBytes - 1);
    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromTicks(1),
        MaximumDelay = TimeSpan.FromTicks(1),
        MaximumConnectionAttempts = 1,
    };

    await AssertThrowsAsync<RuntimeResponseLimitException>(async () =>
    {
        await foreach (RuntimeSseFrame _ in client.StreamEventsAsync("session", "actor", null, retry))
        {
        }
    });
}

static async Task TestSseMixedLineEndingsAndDelimiterLimitAsync()
{
    string[] boundaries = ["\n\n", "\n\r", "\n\r\n", "\r\r", "\r\r\n", "\r\n\n", "\r\n\r", "\r\n\r\n"];
    foreach (string boundary in boundaries)
    {
        string body = $"data: {{\"type\":\"cursor\",\"revision\":\"4\"}}{boundary}";
        var handler = new SequenceHandler(_ => SseResponse(body, oneByteReads: true));
        using RuntimeClient client = CreateClient(handler, 1024, Encoding.UTF8.GetByteCount(body));

        string? revision = await ReadFirstRevisionAsync(client);

        Assert(revision == "4", $"SSE mixed line-ending boundary {Escape(boundary)} was not decoded");
    }

    const string exactBody = "data: {\"type\":\"cursor\",\"revision\":\"5\"}\n\n";
    int exactBytes = Encoding.UTF8.GetByteCount(exactBody);
    var exactHandler = new SequenceHandler(_ => SseResponse(exactBody, oneByteReads: true));
    using (RuntimeClient exactClient = CreateClient(exactHandler, 1024, exactBytes))
    {
        Assert(await ReadFirstRevisionAsync(exactClient) == "5", "An exact-size SSE event was rejected");
    }

    var oversizedHandler = new SequenceHandler(_ => SseResponse(exactBody, oneByteReads: true));
    using RuntimeClient oversizedClient = CreateClient(oversizedHandler, 1024, exactBytes - 1);
    await AssertThrowsAsync<RuntimeResponseLimitException>(() => ReadFirstRevisionAsync(oversizedClient));
}

static async Task<string?> ReadFirstRevisionAsync(RuntimeClient client)
{
    var retry = new RuntimeSseRetryPolicy
    {
        DefaultDelay = TimeSpan.FromTicks(1),
        MaximumDelay = TimeSpan.FromTicks(1),
        MaximumConnectionAttempts = 1,
    };
    await foreach (RuntimeSseFrame frame in client.StreamEventsAsync("session", "actor", null, retry))
    {
        return frame.Revision;
    }
    throw new InvalidOperationException("Runtime SSE stream did not yield a frame.");
}

static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

static RuntimeSseRetryPolicy OneAttemptRetry() => new()
{
    DefaultDelay = TimeSpan.FromTicks(1),
    MaximumDelay = TimeSpan.FromTicks(1),
    MaximumConnectionAttempts = 1,
};

static Task HoldStartedEvent(RuntimeSseFrameEvent runtimeEvent, CancellationToken cancellationToken) =>
    runtimeEvent.Data.Type == DataType.RunStarted
        ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        : Task.CompletedTask;

static async Task TestAttachmentReadAsync()
{
    byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
    RuntimeAttachmentReadRequest request = AttachmentRequest(bytes);
    var handler = new SequenceHandler(_ => AttachmentResponse(bytes, request.Reference));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, maxAttachmentResponseBytes: bytes.Length);

    byte[] received = await client.ReadAttachmentAsync(request);

    Assert(received.SequenceEqual(bytes), "attachment bytes were not returned exactly");
}

static async Task TestAttachmentIntegrityAsync()
{
    byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
    RuntimeAttachmentReadRequest request = AttachmentRequest(bytes);
    var handler = new SequenceHandler(_ => AttachmentResponse(bytes, request.Reference, contentDigest: "sha-256=:bad:"));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, maxAttachmentResponseBytes: bytes.Length);

    await AssertThrowsAsync<RuntimeAttachmentIntegrityException>(() => client.ReadAttachmentAsync(request));
}

static async Task TestAttachmentResponseLimitAsync()
{
    byte[] authorized = [137, 80, 78, 71, 13, 10, 26];
    byte[] oversized = [.. authorized, 10];
    RuntimeAttachmentReadRequest request = AttachmentRequest(authorized);
    var handler = new SequenceHandler(_ => AttachmentResponse(oversized, request.Reference));
    using RuntimeClient client = CreateClient(handler, 1024, 1024, maxAttachmentResponseBytes: authorized.Length);

    await AssertThrowsAsync<RuntimeResponseLimitException>(() => client.ReadAttachmentAsync(request));
}

static async Task TestProductionRedirectBlockingAsync()
{
    var origin = new TcpListener(IPAddress.Loopback, 0);
    var target = new TcpListener(IPAddress.Loopback, 0);
    origin.Start();
    target.Start();
    try
    {
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        Task originResponse = Task.Run(async () =>
        {
            using TcpClient connection = await origin.AcceptTcpClientAsync().ConfigureAwait(false);
            await using NetworkStream stream = connection.GetStream();
            byte[] headerTerminator = [13, 10, 13, 10];
            int matched = 0;
            while (matched < headerTerminator.Length)
            {
                int received = stream.ReadByte();
                if (received < 0) throw new IOException("Runtime test request ended before its HTTP headers.");
                matched = received == headerTerminator[matched]
                    ? matched + 1
                    : received == headerTerminator[0] ? 1 : 0;
            }
            byte[] response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{targetPort}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response).ConfigureAwait(false);
        });

        using var client = new RuntimeClient(new RuntimeClientOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{originPort}"),
            MaxRequestBytes = 1024,
            MaxHttpResponseBytes = 1024,
            MaxAttachmentResponseBytes = 1024,
            MaxSseEventBytes = 1024,
            ConfigureHttpHandler = handler => handler.AllowAutoRedirect = true,
        });
        await AssertThrowsAsync<RuntimeRedirectException>(() => client.InitializeAsync());
        await originResponse.ConfigureAwait(false);
        Assert(!target.Pending(), "the production HTTP handler followed a redirect target");
    }
    finally
    {
        origin.Stop();
        target.Stop();
    }
}

static async Task TestProductionRunSessionCancellationAsync()
{
    double slowestMilliseconds = 0;
    foreach (string exit in new[] { "terminal", "close", "caller-cancel" })
    foreach (bool sendActionHeaders in new[] { false, true })
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverCancellation = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var actionOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new List<Task>();
        Task server = Task.Run(async () =>
        {
            try
            {
                for (int index = 0; index < 3; index++)
                    handlers.Add(HandleConnectionAsync(await listener.AcceptTcpClientAsync(serverCancellation.Token)));
            }
            finally
            {
                await Task.WhenAll(handlers);
            }
        });
        using var client = new RuntimeClient(new RuntimeClientOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"),
            MaxRequestBytes = 1024, MaxHttpResponseBytes = 1024, MaxAttachmentResponseBytes = 1024, MaxSseEventBytes = 8192,
        });
        RuntimeRunSession? session = null;
        try
        {
            session = await client.StartRunSessionAsync(new RuntimeRunRequest
            {
                SessionId = "session", ActorId = "actor", InputId = "input", Payload = JsonSerializer.SerializeToElement(new { }),
            }, new RuntimeRunSessionOptions
            {
                EventRetryPolicy = OneAttemptRetry(),
                Actions = new RuntimeRunActionOptions
                {
                    RetryPolicy = OneAttemptRetry(), Timeout = TimeSpan.FromSeconds(10),
                    OnDelivery = (_, _) => throw new InvalidOperationException("The idle fixture delivered an Action."),
                },
            }, callerCancellation.Token);
            await actionOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (exit == "terminal") terminalReleased.SetResult();
            else if (exit == "close") session.Close();
            else callerCancellation.Cancel();

            Task<RuntimeSseFrameEvent> completion = session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (exit == "terminal")
                Assert((await completion).Data.Status == DataStatus.Aborted, "the real HTTP session lost its aborted terminal event");
            else if (exit == "close")
                await AssertThrowsAsync<RuntimeRunSessionClosedException>(async () => await completion);
            else
                await AssertThrowsAsync<OperationCanceledException>(async () => await completion);
            slowestMilliseconds = Math.Max(slowestMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            Assert(!serverCancellation.IsCancellationRequested, "the server had to release the idle stream before session completion");
        }
        finally
        {
            try
            {
                if (session is not null)
                {
                    try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested) { }
                }
            }
            finally
            {
                serverCancellation.Cancel();
                listener.Stop();
                try { await server; }
                catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested) { }
            }
        }

        async Task HandleConnectionAsync(TcpClient connection)
        {
            using (connection)
            await using (NetworkStream stream = connection.GetStream())
            using (var reader = new StreamReader(stream, leaveOpen: true))
            {
                string requestLine = await reader.ReadLineAsync(serverCancellation.Token) ?? throw new IOException("Missing HTTP request.");
                int contentLength = 0;
                for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(serverCancellation.Token));)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(line[15..]);
                if (contentLength > 0) await reader.ReadBlockAsync(new char[contentLength], serverCancellation.Token);
                if (requestLine.StartsWith("POST /runtime/v1/runs ", StringComparison.Ordinal))
                {
                    const string accepted = """{"runId":"run","initialCoordinate":{"sessionId":"session","actorId":"actor","runId":"run","turn":"0"},"eventCursor":"0"}""";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 202 Accepted\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(accepted)}\r\nConnection: close\r\n\r\n{accepted}"), serverCancellation.Token);
                    return;
                }
                bool actions = requestLine.StartsWith("POST /runtime/v1/actions/stream ", StringComparison.Ordinal);
                if (!actions || sendActionHeaders)
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray(), serverCancellation.Token);
                if (actions)
                {
                    if (sendActionHeaders) await WriteChunkAsync(": connected\n\n");
                    actionOpened.SetResult();
                }
                else
                {
                    await WriteChunkAsync(Envelope(1, new { type = "run.queued", inputId = "input", inputFingerprint = "2dd4759d3236186a313dc9e2a6a30d92f9c5907df1e338007c39dcbaa054d210" })
                        + Envelope(2, new { type = "run.started", inputId = "input" }));
                    if (exit == "terminal")
                    {
                        await terminalReleased.Task.WaitAsync(serverCancellation.Token);
                        await WriteChunkAsync(Envelope(3, new { type = "run.completed", status = "aborted" }));
                    }
                }
                try { await stream.ReadAsync(new byte[1], serverCancellation.Token); }
                catch (IOException) { }

                async Task WriteChunkAsync(string text)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("X") + "\r\n"), serverCancellation.Token);
                    await stream.WriteAsync(bytes, serverCancellation.Token);
                    await stream.WriteAsync("\r\n"u8.ToArray(), serverCancellation.Token);
                }
            }
        }
        static string Envelope(int revision, object data) => "data: " + JsonSerializer.Serialize(new
        {
            type = "event", @event = new
            {
                protocolVersion = "1", eventId = "event-" + revision, sequence = revision.ToString(), revision = revision.ToString(),
                occurredAt = "2026-08-30T00:00:00.000Z", coordinate = new { sessionId = "session", actorId = "actor", runId = "run", turn = "0" },
                audience = new { visibility = "owner" }, data,
            },
        }) + "\n\n";
    }
    Console.WriteLine($"Production HTTP idle Action cancellation passed (6 scenarios; slowest {slowestMilliseconds:F1} ms).");
}

static RuntimeClient CreateClient(
    HttpMessageHandler handler,
    long maxHttpResponseBytes,
    long maxSseEventBytes,
    string? token = null,
    long maxAttachmentResponseBytes = 1024,
    string baseAddress = "https://runtime.invalid",
    long maxRequestBytes = 1024) =>
    new(
        new RuntimeClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            MaxRequestBytes = maxRequestBytes,
            MaxHttpResponseBytes = maxHttpResponseBytes,
            MaxAttachmentResponseBytes = maxAttachmentResponseBytes,
            MaxSseEventBytes = maxSseEventBytes,
            AccessTokenProvider = token is null ? null : _ => ValueTask.FromResult<string?>(token),
        },
        handler);

static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
    new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

static HttpResponseMessage SseResponse(string body, bool oneByteReads = false)
{
    HttpContent content = oneByteReads
        ? new StreamContent(new OneByteReadStream(Encoding.UTF8.GetBytes(body)))
        : new StringContent(body, Encoding.UTF8, "text/event-stream");
    content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
}

static RuntimeAttachmentReadRequest AttachmentRequest(byte[] bytes)
{
    string attachmentId = $"img_{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    return new RuntimeAttachmentReadRequest
    {
        SessionId = "session",
        ActorId = "actor",
        Reference = new RuntimeAttachmentReadRequestReference
        {
            Type = ReferenceType.ImageReference,
            AttachmentId = attachmentId,
            MediaType = MediaType.ImagePng,
            Bytes = bytes.LongLength,
            Width = 1,
            Height = 1,
        },
    };
}

static HttpResponseMessage AttachmentResponse(
    byte[] bytes,
    RuntimeAttachmentReadRequestReference reference,
    string? contentDigest = null)
{
    byte[] digest = SHA256.HashData(bytes);
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    response.Content.Headers.ContentLength = bytes.LongLength;
    response.Content.Headers.TryAddWithoutValidation(
        "Content-Digest",
        contentDigest ?? $"sha-256=:{Convert.ToBase64String(digest)}:");
    response.Headers.ETag = new EntityTagHeaderValue($"\"{reference.AttachmentId}\"");
    response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-store");
    response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
    return response;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int index;

    public AuthenticationHeaderValue? Authorization { get; private set; }

    public List<string?> LastEventIds { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public List<HttpMethod> RequestMethods { get; } = [];

    public List<string> RequestPaths { get; } = [];

    public List<string> RequestQueries { get; } = [];

    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        Authorization = request.Headers.Authorization;
        RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        RequestQueries.Add(request.RequestUri?.Query ?? string.Empty);
        RequestMethods.Add(request.Method);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        LastEventIds.Add(request.Headers.TryGetValues("Last-Event-ID", out IEnumerable<string>? values)
            ? values.Single()
            : null);
        if (index >= responses.Length) throw new InvalidOperationException("No fake response remains.");
        HttpResponseMessage response = responses[index++](request);
        response.RequestMessage ??= request;
        return response;
    }
}

sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
{
    private readonly object gate = new();

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (gate)
        {
            RequestBodies.Add(body);
        }
        HttpResponseMessage response = route(request);
        response.RequestMessage ??= request;
        return response;
    }
}

sealed class AsyncRoutingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
{
    private readonly object gate = new();

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (gate)
        {
            RequestBodies.Add(body);
        }
        HttpResponseMessage response = await route(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }
}

sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
{
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}

sealed class CancellationHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int RequestCount { get; private set; }

    public Task Started => started.Task;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _ = request;
        RequestCount++;
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The cancelled request unexpectedly continued.");
    }
}
