using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.Testing;
using UnityEngine;

namespace GameAgent.Unity.Tests;

public sealed class UnityHostConformanceTests
{
    [Fact]
    public void RuntimeAssemblyIsMarkedForUnityManagedStripping()
    {
        var runtimeAssembly = typeof(UnityAgentRuntimeFacade).Assembly;

        Assert.Contains(
            runtimeAssembly.GetCustomAttributesData(),
            attribute => string.Equals(
                attribute.AttributeType.FullName,
                "UnityEngine.Scripting.AlwaysLinkAssemblyAttribute",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DispatcherExecutesBackgroundRequestsOnItsCreatingThread()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 8);
        var mainThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;

        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ =>
                {
                    callbackThread = Environment.CurrentManagedThreadId;
                    return new ValueTask<int>(42);
                },
                CancellationToken.None));

        PumpUntilCompleted(dispatcher, pending);

        Assert.Equal(42, pending.GetAwaiter().GetResult());
        Assert.Equal(mainThread, callbackThread);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void DispatcherRejectsOverflowAndCancelsQueuedWorkOnShutdown()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var first = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(1),
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        var overflow = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(2),
                CancellationToken.None));
        Assert.Throws<UnityDispatcherQueueFullException>(
            () => overflow.GetAwaiter().GetResult());

        dispatcher.Shutdown();
        Assert.ThrowsAny<OperationCanceledException>(
            () => first.GetAwaiter().GetResult());
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void DispatcherHonorsCallerCancellationBeforePump()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ =>
                {
                    invoked = true;
                    return new ValueTask<int>(1);
                },
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => pending.GetAwaiter().GetResult());

        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        Assert.False(invoked);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task DispatcherShutdownBetweenDequeueAndClaimCancelsWork()
    {
        UnityMainThreadDispatcher? dispatcher = null;
        dispatcher = new UnityMainThreadDispatcher(
            capacity: 1,
            beforeWorkClaim: () => dispatcher!.Shutdown());
        using (dispatcher)
        {
            var invoked = false;
            var pending = Task.Run(
                async () => await dispatcher.InvokeAsync(
                    _ =>
                    {
                        invoked = true;
                        return new ValueTask<int>(1);
                    },
                    CancellationToken.None));
            Assert.True(
                SpinWait.SpinUntil(
                    () => dispatcher.PendingCount == 1,
                    TimeSpan.FromSeconds(2)));

            Assert.Equal(
                1,
                dispatcher.Pump(maxItems: 1, maxMilliseconds: 10));
            await Assert.ThrowsAsync<
                UnityDispatchCancelledBeforeExecutionException>(
                () => pending);

            Assert.False(invoked);
            Assert.True(dispatcher.IsShutdown);
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.Equal(0, dispatcher.RunningCount);
            Assert.True(
                dispatcher.WaitForRunningWorkAsync(CancellationToken.None)
                    .IsCompleted);
        }
    }

    [Fact]
    public async Task DispatcherShutdownBeforeDirectClaimRejectsWork()
    {
        UnityMainThreadDispatcher? dispatcher = null;
        dispatcher = new UnityMainThreadDispatcher(
            capacity: 1,
            beforeWorkClaim: () => dispatcher!.Shutdown());
        using (dispatcher)
        {
            var invoked = false;

            await Assert.ThrowsAsync<
                UnityDispatchCancelledBeforeExecutionException>(
                () => dispatcher.InvokeAsync(
                        _ =>
                        {
                            invoked = true;
                            return new ValueTask<int>(1);
                        },
                        CancellationToken.None)
                    .AsTask());

            Assert.False(invoked);
            Assert.True(dispatcher.IsShutdown);
            Assert.Equal(0, dispatcher.RunningCount);
        }
    }

    [Fact]
    public async Task DispatcherWaitsForRunningActionAfterCancellation()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(
            async () => await dispatcher.InvokeAsync(
                async token =>
                {
                    started.TrySetResult(token);
                    return await release.Task;
                },
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var actionToken = await started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.True(actionToken.IsCancellationRequested);
        Assert.False(pending.IsCompleted);

        release.TrySetResult(9);
        Assert.Equal(9, await pending);
    }

    [Fact]
    public async Task DispatcherReportsThrowingCancellationCallbacksAndShutsDown()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.UnhandledException +=
            exception => observed.TrySetResult(exception);

        var pending = dispatcher.InvokeAsync(
            async cancellationToken =>
                {
                    var delay = Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    using var registration = cancellationToken.Register(
                        () => throw new InvalidOperationException(
                            "Cancellation observer failed."));
                    registered.TrySetResult();
                    await delay;
                    return 1;
                },
                CancellationToken.None)
            .AsTask();

        await registered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending);
        var cancellationFailure = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.IsType<AggregateException>(cancellationFailure);
        Assert.True(dispatcher.IsShutdown);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void KnownPreExecutionFailuresReturnDefinitiveReceipts()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var invoked = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                invoked = true;
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            });
        using var cancellation = new CancellationTokenSource();
        var cancelled = Task.Run(
            async () => await host.SubmitActionAsync(
                UnityActionRequest(),
                cancellation.Token));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var cancelledReceipt = cancelled.GetAwaiter().GetResult();
        Assert.Equal(ReceiptStatuses.Failed, cancelledReceipt.Status);
        Assert.Equal(
            "unity_dispatch_cancelled",
            cancelledReceipt.ErrorCode);
        Assert.False(invoked);
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);

        var occupied = Task.Run(
            async () => await dispatcher.InvokeAsync(
                _ => new ValueTask<int>(1),
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
        var overflow = Task.Run(
                async () => await host.SubmitActionAsync(
                UnityActionRequest(),
                CancellationToken.None))
            .GetAwaiter()
            .GetResult();
        Assert.Equal(ReceiptStatuses.Failed, overflow.Status);
        Assert.Equal("unity_dispatch_queue_full", overflow.ErrorCode);
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        Assert.Equal(1, occupied.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task StartedHandlerCancellationFaultRemainsUnknownToCore()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var mutated = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                mutated = true;
                throw new OperationCanceledException(
                    "handler stopped after mutation");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host
                .SubmitActionAsync(
                    UnityActionRequest(),
                    CancellationToken.None)
                .AsTask());
        Assert.True(mutated);
    }

    [Fact]
    public async Task ExpiredActionDoesNotEnterUnityHandler()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var clock = new FakeRuntimeClock();
        var invoked = false;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                invoked = true;
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            },
            clock);
        var request = UnityActionRequest();
        request.Deadline = clock.UtcNow.AddMilliseconds(-1);

        var receipt = await host.SubmitActionAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ReceiptStatuses.Failed, receipt.Status);
        Assert.Equal("unity_dispatch_deadline", receipt.ErrorCode);
        Assert.False(invoked);
    }

    [Fact]
    public void ActionExpiringWhileQueuedDoesNotEnterUnityHandler()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 1);
        var clock = new FakeRuntimeClock();
        var invocationCount = 0;
        var host = new UnityMainThreadGameHost(
            dispatcher,
            (_, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return new ValueTask<ActionReceipt>(
                    new ActionReceipt());
            },
            clock);
        var request = UnityActionRequest();
        request.Deadline = clock.UtcNow.AddSeconds(1);

        var pending = Task.Run(
            async () => await host.SubmitActionAsync(
                request,
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));

        clock.Advance(TimeSpan.FromSeconds(1));
        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var receipt = pending.GetAwaiter().GetResult();

        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(0, receipt.Revision);
        Assert.Equal(ReceiptStatuses.Failed, receipt.Status);
        Assert.Equal("unity_dispatch_deadline", receipt.ErrorCode);
        Assert.True(receipt.Retryable);
        Assert.Equal(clock.UtcNow, receipt.ReceivedAt);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public void DtoBridgePreservesStructuredJsonWithoutUnitySerialization()
    {
        var input = new UnityObservationData
        {
            observationId = "observation-1",
            worldId = "world-1",
            sessionId = "session-1",
            source = "game.metrics",
            kind = "snapshot",
            contentType = "application/json",
            schemaRef = "game://schemas/metrics/1",
            contentSchemaVersion = "1",
            payloadJson = """{"hunger":70,"nearby":["berries"]}""",
            extensionsJson = """{"trace":{"id":1}}""",
            observedAtUnixMilliseconds = 1_785_196_800_000,
            trust = "authoritative",
            visibilityScope = "agent",
            audienceIds = new[] { "agent-1" },
            audienceIncarnations = new[]
            {
                new UnityAudienceIncarnationData
                {
                    audienceId = "agent-1",
                    entityId = "npc-17",
                    incarnation = 4
                }
            }
        };

        var protocol = UnityProtocolBridge.ToProtocol(input);
        var json = UnityProtocolBridge.ToJson(protocol);
        var roundTrip = UnityProtocolBridge.ObservationFromJson(json);

        Assert.Equal(70, roundTrip.Payload!.Value
            .GetProperty("hunger")
            .GetInt32());
        Assert.Equal("berries", roundTrip.Payload.Value
            .GetProperty("nearby")[0]
            .GetString());
        Assert.Equal("1", roundTrip.ContentSchemaVersion);
        Assert.Equal(
            1,
            roundTrip.Extensions["trace"].GetProperty("id").GetInt32());
        Assert.Equal("agent-1", Assert.Single(
            roundTrip.Visibility.AudienceIds));
        Assert.True(
            ObservationAudienceIncarnations.TryRead(
                roundTrip,
                out var audienceIncarnations));
        var incarnation = Assert.Single(audienceIncarnations);
        Assert.Equal("agent-1", incarnation.AudienceId);
        Assert.Equal("npc-17", incarnation.Entity.EntityId);
        Assert.Equal(4, incarnation.Entity.Incarnation);
    }

    [Fact]
    public void ReceiptDtoRoundTripsResultingGameContextExtension()
    {
        var coordinate = new GameContextCoordinate(
            "world-1",
            "timeline-1",
            2,
            new GameEntityIdentity("npc-17", 4),
            stateVersion: "state-2",
            gameTime: new GameTimePoint(
                "world-clock",
                "timeline-1",
                3,
                200),
            sessionId: "session-1");
        var extension = GameContextEnvelope.ToJson(coordinate)
            .GetRawText();
        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "operation-coordinate",
                revision = 1,
                status = ReceiptStatuses.Succeeded,
                extensionsJson =
                    $$"""{"{{GameContextReceiptEnvelope.ResultingExtensionName}}":{{extension}}}""",
                hasReceivedAtUnixMilliseconds = true,
                receivedAtUnixMilliseconds = 0
            });

        var roundTrip = UnityProtocolBridge.ActionReceiptFromJson(
            UnityProtocolBridge.ToJson(receipt));

        Assert.True(
            GameContextReceiptEnvelope.TryReadResulting(
                roundTrip,
                out var restored));
        Assert.Equal("world-1", restored!.WorldId);
        Assert.Equal("session-1", restored.SessionId);
        Assert.Equal("npc-17", restored.Observer!.EntityId);
        Assert.Equal(4, restored.Observer.Incarnation);
        Assert.Equal("state-2", restored.StateVersion);
    }

    [Fact]
    public void DtoBridgeAccepts64ExtensionsAndRejects65BeforeCopying()
    {
        static string Extensions(int count) =>
            "{"
            + string.Join(
                ",",
                Enumerable.Range(0, count)
                    .Select(index => "\"extension_"
                        + index
                        + "\":true"))
            + "}";

        var input = FieldObservation("extension-boundary");
        input.extensionsJson = Extensions(
            ProtocolLimits.MaxProtocolExtensions);
        var accepted = UnityProtocolBridge.ToProtocol(input);
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            accepted.Extensions.Count);

        input.extensionsJson = Extensions(
            ProtocolLimits.MaxProtocolExtensions + 1);
        Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ToProtocol(input));
    }

    [Fact]
    public void DtoBridgeRejectsDeepLargeAndWideJsonBeforeMapping()
    {
        var deep = FieldObservation("deep-payload");
        deep.payloadJson =
            new string(
                '[',
                ProtocolLimits.MaxProtocolJsonDepth + 1)
            + "0"
            + new string(
                ']',
                ProtocolLimits.MaxProtocolJsonDepth + 1);
        Assert.ThrowsAny<JsonException>(
            () => UnityProtocolBridge.ToProtocol(deep));

        var large = FieldObservation("large-payload");
        large.payloadJson =
            "{\"value\":\""
            + new string(
                'x',
                ProtocolLimits.MaxProtocolJsonStringUtf8Bytes + 1)
            + "\"}";
        Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ToProtocol(large));

        var wide = FieldObservation("wide-payload");
        wide.payloadJson =
            "["
            + string.Join(
                ",",
                Enumerable.Repeat(
                    "0",
                    ProtocolLimits.MaxProtocolJsonContainerItems + 1))
            + "]";
        Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ToProtocol(wide));
    }

    [Fact]
    public void DtoBridgeBoundsAggregateJsonAcrossNestedObservations()
    {
        var paddedObject =
            "{}"
            + new string(
                ' ',
                ProtocolLimits.MaxProtocolJsonUtf8Bytes - 2);
        var shared = FieldObservation("aggregate-payload");
        shared.extensionsJson = paddedObject;
        shared.payloadJson = paddedObject;
        var observations = Enumerable.Repeat(
                shared,
                ProtocolLimits.MaxAuthoritativeObservationsPerReceipt)
            .ToArray();

        Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ToProtocol(
                new UnityActionReceiptData
                {
                    operationId = "aggregate-operation",
                    status = ReceiptStatuses.Succeeded,
                    authoritativeObservations = observations,
                    hasReceivedAtUnixMilliseconds = true,
                    receivedAtUnixMilliseconds = 0
                }));
    }

    [Fact]
    public void DtoBridgeChecksArrayCardinalityBeforeCopying()
    {
        var input = FieldObservation("subject-cardinality");
        input.subjectIds = new string[
            ProtocolLimits.MaxObservationSubjectIds + 1];

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => UnityProtocolBridge.ToProtocol(input));

        Assert.Equal("subjectIds", error.ParamName);

        input = FieldObservation("audience-cardinality");
        input.audienceIds = new string[
            ProtocolLimits.MaxObservationAudienceIds + 1];
        error = Assert.Throws<ArgumentOutOfRangeException>(
            () => UnityProtocolBridge.ToProtocol(input));

        Assert.Equal("audienceIds", error.ParamName);
    }

    [Fact]
    public void JsonObjectIngressRejectsExcessiveDepthBeforeDeserialization()
    {
        var deeplyNested =
            new string('[', 65)
            + "0"
            + new string(']', 65);

        Assert.ThrowsAny<JsonException>(
            () => UnityProtocolBridge.ObservationFromJson(deeplyNested));

        var escapedExpansion =
            "{\"value\":\""
            + string.Concat(
                Enumerable.Repeat(
                    "\\u0061",
                    ProtocolLimits.MaxProtocolJsonStringUtf8Bytes + 1))
            + "\"}";
        var expansionError = Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ObservationFromJson(
                escapedExpansion));
        Assert.Contains(
            "bounded decode allocation limit",
            expansionError.Message);
    }

    [Fact]
    public void JsonObjectIngressAppliesProtocolContainerLimitsBeforeDeserialization()
    {
        static string Extensions(int count) =>
            "{"
            + string.Join(
                ",",
                Enumerable.Range(0, count)
                    .Select(index => "\"extension_"
                        + index
                        + "\":true"))
            + "}";

        static string Observation(
            string extensions,
            string payload = "{}") =>
            "{"
            + "\"protocolVersion\":\"0.2\","
            + "\"schemaVersion\":\"0.2\","
            + "\"extensions\":" + extensions + ","
            + "\"observationId\":\"wire-observation\","
            + "\"worldId\":\"world-1\","
            + "\"source\":\"game.state\","
            + "\"kind\":\"snapshot\","
            + "\"contentType\":\"application/json\","
            + "\"payload\":" + payload + ","
            + "\"observedAt\":\"2026-07-30T00:00:00Z\","
            + "\"trust\":\"trusted\","
            + "\"visibility\":{\"scope\":\"world\",\"audienceIds\":[]}"
            + "}";

        var atLimit = UnityProtocolBridge.ObservationFromJson(
            Observation(
                Extensions(ProtocolLimits.MaxProtocolExtensions)));
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            atLimit.Extensions.Count);

        var overExtensions = Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ObservationFromJson(
                Observation(
                    Extensions(
                        ProtocolLimits.MaxProtocolExtensions + 1))));
        Assert.Contains("over 64 items", overExtensions.Message);

        var opaquePayload = UnityProtocolBridge.ObservationFromJson(
            Observation(
                "{}",
                "{\"extensions\":"
                + Extensions(ProtocolLimits.MaxProtocolExtensions + 1)
                + "}"));
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions + 1,
            opaquePayload.Payload!.Value
                .GetProperty("extensions")
                .EnumerateObject()
                .Count());

        var receiptOverLimit =
            "{"
            + "\"protocolVersion\":\"0.2\","
            + "\"schemaVersion\":\"0.2\","
            + "\"operationId\":\"wire-operation\","
            + "\"revision\":0,"
            + "\"status\":\"succeeded\","
            + "\"authoritativeObservations\":["
            + string.Join(
                ",",
                Enumerable.Repeat(
                    "{}",
                    ProtocolLimits
                        .MaxAuthoritativeObservationsPerReceipt + 1))
            + "],"
            + "\"retryable\":false,"
            + "\"receivedAt\":\"2026-07-30T00:00:00Z\""
            + "}";
        var overObservations = Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ActionReceiptFromJson(
                receiptOverLimit));
        Assert.Contains("over 64 items", overObservations.Message);

        var nestedObservationOverLimit =
            "{"
            + "\"protocolVersion\":\"0.2\","
            + "\"schemaVersion\":\"0.2\","
            + "\"operationId\":\"nested-wire-operation\","
            + "\"revision\":0,"
            + "\"status\":\"succeeded\","
            + "\"authoritativeObservations\":["
            + Observation(
                Extensions(
                    ProtocolLimits.MaxProtocolExtensions + 1))
            + "],"
            + "\"retryable\":false,"
            + "\"receivedAt\":\"2026-07-30T00:00:00Z\""
            + "}";
        var overNestedExtensions = Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ActionReceiptFromJson(
                nestedObservationOverLimit));
        Assert.Contains("over 64 items", overNestedExtensions.Message);

        var requestOverLimit =
            "{"
            + "\"protocolVersion\":\"0.2\","
            + "\"schemaVersion\":\"0.2\","
            + "\"expectedEffects\":["
            + string.Join(
                ",",
                Enumerable.Repeat(
                    "\"effect\"",
                    ProtocolLimits.MaxActionExpectedEffects + 1))
            + "]"
            + "}";
        var overExpectedEffects = Assert.Throws<JsonException>(
            () => UnityProtocolBridge.ActionRequestFromJson(
                requestOverLimit));
        Assert.Contains("over 32 items", overExpectedEffects.Message);
    }

    [Fact]
    public void RuntimeEventBridgeRejectsSemanticWireViolations()
    {
        var runtimeEvent = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 0,
            Kind = RuntimeEventKinds.ProviderDispatchStarted,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            ProviderId = "gateway/provider",
            ModelId = "openai/gpt-4.1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = ProtocolJson.ParseElement("{}")
        };

        var roundTrip = UnityProtocolBridge.RuntimeEventFromJson(
            UnityProtocolBridge.ToJson(runtimeEvent));
        Assert.Equal("openai/gpt-4.1", roundTrip.ModelId);

        runtimeEvent.Sequence = -1;
        runtimeEvent.Kind = string.Empty;
        runtimeEvent.RuntimeGeneration = 0;
        runtimeEvent.ProviderId = new string(
            'p',
            ProtocolLimits.MaxProviderIdUnicodeScalars + 1);

        Assert.Throws<JsonException>(
            () => UnityProtocolBridge.RuntimeEventFromJson(
                UnityProtocolBridge.ToJson(runtimeEvent)));
    }

    [Fact]
    public void FieldDtosPreserveResourcesAuthorityAndExtensions()
    {
        var resource = new UnityObservationData
        {
            observationId = "resource-observation",
            worldId = "world-1",
            source = "game.resource",
            kind = ObservationKinds.ResourceReference,
            resourceUri = "game://world/map",
            resourceMediaType = "application/json",
            resourceDigest = "sha256:abc",
            resourceSizeBytes = 42,
            observedAtUnixMilliseconds = 1_785_196_800_000
        };
        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "operation-1",
                status = ReceiptStatuses.Succeeded,
                resultJson = """{"ok":true}""",
                authoritativeObservations = new[] { resource },
                extensionsJson = """{"host":"unity"}""",
                receivedAtUnixMilliseconds = 1_785_196_800_000
            });
        var request = new ActionRequest
        {
            OperationId = "operation-1",
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "inspect",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            DecisionKey = "npc inspect decision",
            BatchId = "world-tick-1",
            RequestedAt = DateTimeOffset.UnixEpoch,
            Extensions = new Dictionary<string, JsonElement>
            {
                ["trace"] = ProtocolJson.ParseElement("""{"id":2}""")
            }
        };
        var unityRequest = UnityProtocolBridge.ToUnity(request);

        Assert.Equal(
            42,
            Assert.Single(receipt.AuthoritativeObservations)
                .ResourceRef!
                .SizeBytes);
        Assert.Equal(
            "unity",
            receipt.Extensions["host"].GetString());
        Assert.Contains("\"trace\"", unityRequest.extensionsJson);
        Assert.Equal(
            ProtocolConstants.ProtocolVersion,
            unityRequest.protocolVersion);
        Assert.True(unityRequest.hasRequestedAtUnixMilliseconds);
        Assert.Equal(0, unityRequest.requestedAtUnixMilliseconds);
        Assert.False(unityRequest.hasDeadlineUnixMilliseconds);
        Assert.Equal("npc inspect decision", unityRequest.decisionKey);
        Assert.Equal("world-tick-1", unityRequest.batchId);
    }

    [Fact]
    public void FieldDtosPreserveEpochAndZeroTtl()
    {
        var observation = UnityProtocolBridge.ToProtocol(
            new UnityObservationData
            {
                observationId = "epoch-observation",
                worldId = "world-1",
                source = "game.clock",
                kind = "snapshot",
                payloadJson = "{}",
                hasObservedAtUnixMilliseconds = true,
                observedAtUnixMilliseconds = 0,
                hasTtlMilliseconds = true,
                ttlMilliseconds = 0
            });
        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "epoch-operation",
                status = ReceiptStatuses.Succeeded,
                hasCommittedAtUnixMilliseconds = true,
                committedAtUnixMilliseconds = 0,
                hasReceivedAtUnixMilliseconds = true,
                receivedAtUnixMilliseconds = 0
            });
        var request = UnityProtocolBridge.ToUnity(
            new ActionRequest
            {
                OperationId = "epoch-operation",
                RunId = "run-1",
                TurnId = "turn-1",
                ToolCallId = "call-1",
                AgentId = "agent-1",
                WorldId = "world-1",
                ActionName = "inspect",
                ActionVersion = "1",
                Arguments = ProtocolJson.ParseElement("{}"),
                RequestedAt = DateTimeOffset.UnixEpoch,
                Deadline = DateTimeOffset.UnixEpoch
            });

        Assert.Equal(DateTimeOffset.UnixEpoch, observation.ObservedAt);
        Assert.Equal(0, observation.TtlMs);
        Assert.Equal(DateTimeOffset.UnixEpoch, receipt.CommittedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, receipt.ReceivedAt);
        Assert.True(request.hasRequestedAtUnixMilliseconds);
        Assert.True(request.hasDeadlineUnixMilliseconds);
        Assert.Equal(0, request.requestedAtUnixMilliseconds);
        Assert.Equal(0, request.deadlineUnixMilliseconds);
    }

    [Fact]
    public void FieldReceiptAcceptsAuthoritativeObservationLimit()
    {
        var observations = Enumerable
            .Range(0, ProtocolLimits.MaxAuthoritativeObservationsPerReceipt)
            .Select(index => FieldObservation("observation-" + index))
            .ToArray();

        var receipt = UnityProtocolBridge.ToProtocol(
            new UnityActionReceiptData
            {
                operationId = "operation-at-limit",
                status = ReceiptStatuses.Succeeded,
                authoritativeObservations = observations,
                hasReceivedAtUnixMilliseconds = true,
                receivedAtUnixMilliseconds = 0
            });

        Assert.Equal(
            ProtocolLimits.MaxAuthoritativeObservationsPerReceipt,
            receipt.AuthoritativeObservations.Count);
    }

    [Fact]
    public void FieldReceiptRejectsAuthoritativeObservationsOverLimitBeforeMapping()
    {
        var observations = new UnityObservationData[
            ProtocolLimits.MaxAuthoritativeObservationsPerReceipt + 1];

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => UnityProtocolBridge.ToProtocol(
                new UnityActionReceiptData
                {
                    operationId = "operation-over-limit",
                    status = ReceiptStatuses.Succeeded,
                    authoritativeObservations = observations,
                    hasReceivedAtUnixMilliseconds = true,
                    receivedAtUnixMilliseconds = 0
                }));

        Assert.Equal("authoritativeObservations", error.ParamName);
    }

    [Fact]
    public void FieldDtosRejectMissingRequiredTimestamps()
    {
        var observationError = Assert.Throws<ArgumentException>(
            () => UnityProtocolBridge.ToProtocol(
                new UnityObservationData
                {
                    observationId = "missing-observed-at",
                    worldId = "world-1",
                    source = "game.state"
                }));
        var receiptError = Assert.Throws<ArgumentException>(
            () => UnityProtocolBridge.ToProtocol(
                new UnityActionReceiptData
                {
                    operationId = "missing-received-at",
                    status = ReceiptStatuses.Succeeded
                }));

        Assert.Equal(
            "observedAtUnixMilliseconds",
            observationError.ParamName);
        Assert.Equal(
            "receivedAtUnixMilliseconds",
            receiptError.ParamName);
    }

    [Theory]
    [InlineData("sequence", -1, true)]
    [InlineData("sequence", -2, false)]
    [InlineData("resourceSizeBytes", -1, true)]
    [InlineData("resourceSizeBytes", -2, false)]
    public void FieldObservationRejectsNegativeOptionalNumbers(
        string field,
        long value,
        bool hasValue)
    {
        var observation = FieldObservation("negative-" + field);
        if (string.Equals(field, "sequence", StringComparison.Ordinal))
        {
            observation.sequence = value;
            observation.hasSequence = hasValue;
        }
        else
        {
            observation.resourceUri = "game://world/map";
            observation.kind = ObservationKinds.ResourceReference;
            observation.resourceSizeBytes = value;
            observation.hasResourceSizeBytes = hasValue;
        }

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => UnityProtocolBridge.ToProtocol(observation));

        Assert.Equal(field, error.ParamName);
    }

    [Fact]
    public void FieldObservationKeepsSentinelDefaultsAsAbsent()
    {
        var payload = UnityProtocolBridge.ToProtocol(
            FieldObservation("payload-defaults"));
        var resourceData = FieldObservation("resource-defaults");
        resourceData.kind = ObservationKinds.ResourceReference;
        resourceData.resourceUri = "game://world/map";
        var resource = UnityProtocolBridge.ToProtocol(resourceData);

        Assert.Null(payload.Sequence);
        Assert.NotNull(resource.ResourceRef);
        Assert.Null(resource.ResourceRef!.SizeBytes);
    }

    [Fact]
    public void FieldDtoMappingIsDeterministicWithExplicitTimestamps()
    {
        var input = new UnityActionReceiptData
        {
            operationId = "deterministic-operation",
            status = ReceiptStatuses.Succeeded,
            authoritativeObservations =
                new[] { FieldObservation("deterministic-observation") },
            hasCommittedAtUnixMilliseconds = true,
            committedAtUnixMilliseconds = 0,
            hasReceivedAtUnixMilliseconds = true,
            receivedAtUnixMilliseconds = 0
        };

        var first = UnityProtocolBridge.ToJson(
            UnityProtocolBridge.ToProtocol(input));
        var second = UnityProtocolBridge.ToJson(
            UnityProtocolBridge.ToProtocol(input));

        Assert.Equal(first, second);
        Assert.Contains(
            "\"receivedAt\":\"1970-01-01T00:00:00+00:00\"",
            first,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredToolLoopMarshalsGameActionToMainThread()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 16);
        var store = new InMemorySessionStore();
        var clock = new FakeRuntimeClock();
        var provider = new ScriptedModelProvider(
            ModelResponse.CallTools(new ModelToolCall
            {
                ToolCallId = "call-1",
                Name = "gather_food",
                Arguments = ProtocolJson.ParseElement(
                    """{"resource":"berries"}""")
            }),
            ModelResponse.Final(
                ProtocolJson.ParseElement(
                    """{"decision":"eat","resource":"berries"}""")));
        var mainThread = Environment.CurrentManagedThreadId;
        var handlerThread = 0;
        var facade = new UnityAgentRuntimeFacade(
            provider,
            store,
            dispatcher,
            (request, _) =>
            {
                handlerThread = Environment.CurrentManagedThreadId;
                return new ValueTask<ActionReceipt>(new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"gathered":1}"""),
                    ReceivedAt = clock.UtcNow,
                    CommittedAt = clock.UtcNow
                });
            },
            clock,
            new SequentialIdGenerator(),
            ownsSessionStore: false);

        try
        {
            var completed = Task.Run(
                async () => await facade.RunAsync(
                    CreateRunRequest(clock),
                    CancellationToken.None));
            PumpUntilCompleted(dispatcher, completed);
            var outcome = completed.GetAwaiter().GetResult();

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(mainThread, handlerThread);
            Assert.Equal(
                "eat",
                outcome.FinalOutput!.Value
                    .GetProperty("decision")
                    .GetString());
            Assert.Contains(
                store.Events,
                item => item.Kind == RuntimeEventKinds.ActionRequested);
            Assert.Contains(
                store.Events,
                item => item.Kind == RuntimeEventKinds.ActionReceived);
        }
        finally
        {
            facade.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task DurableToolLoopRunsEndToEndThroughUnityHost()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new FileSessionStore(
            Path.Combine(directory, "runtime.journal"));
        var clock = new FakeRuntimeClock();
        var ids = new SequentialIdGenerator();
        var tools = new ToolCatalogRegistry();
        tools.Replace(new[]
        {
            new ToolDescriptor
            {
                Name = "gather_food",
                Version = "1",
                Description = "Gather a visible food resource.",
                ParametersSchema = ProtocolJson.ParseElement(
                    """
                    {
                      "type":"object",
                      "required":["resource"],
                      "properties":{"resource":{"type":"string"}}
                    }
                    """),
                Effect = ToolEffects.WorldCommand,
                ThreadAffinity = ThreadAffinities.EngineMainThread,
                ConflictScopes = new List<string> { "inventory:player" },
                TimeoutMs = 1000,
                RetryPolicy = "idempotent",
                IdempotencyPolicy = "required"
            }
        });
        var host = new GameObject("GameAgentRuntimeDurableLoopTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var mainThread = Environment.CurrentManagedThreadId;
        var actionThread = 0;
        var journal = new JournalCoordinator(store, store, clock, ids);
        var runtime = new DurableAgentRuntime(
            new ProviderAttemptRunner(
                new IStreamingModelProvider[]
                {
                    new DurableLoopProvider()
                },
                new ProviderRetryPolicy
                {
                    MaxAttemptsPerProvider = 1,
                    IdleTimeout = TimeSpan.FromSeconds(2),
                    TotalTimeout = TimeSpan.FromSeconds(5)
                },
                new SystemRuntimeDelay(),
                ids),
            new UnityMainThreadGameHost(
                host.Dispatcher,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionThread = Environment.CurrentManagedThreadId;
                    return new ValueTask<ActionReceipt>(new ActionReceipt
                    {
                        OperationId = request.OperationId,
                        Revision = 0,
                        Status = ReceiptStatuses.Succeeded,
                        Result = ProtocolJson.ParseElement(
                            """{"gathered":1}"""),
                        ReceivedAt = clock.UtcNow,
                        CommittedAt = clock.UtcNow
                    });
                },
                clock),
            journal,
            new RunRecovery(store, store, journal),
            tools,
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "unity-test",
                MaxConcurrentProviderCalls = 1
            });
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);

        try
        {
            var pending = Task.Run(
                async () => await host.RunAsync(
                    CreateDurableRunRequest(clock),
                    CancellationToken.None));
            PumpUntilCompleted(host.Dispatcher, pending);
            var outcome = await pending;

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(mainThread, actionThread);
            Assert.Equal(
                "eat",
                outcome.FinalOutput!.Value
                    .GetProperty("decision")
                    .GetString());
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DurablePlayerGateScenarioProducesAVerifiableMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-player-gate-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var markerPath = Path.Combine(
            directory,
            "durable-loop.pass.json");
        var root = new GameObject("GameAgentUnityPlayerGateTest");
        var host = root.AddComponent<UnityAgentRuntimeHost>();

        try
        {
            var pending = UnityDurableGateScenario.RunAsync(
                host,
                Path.Combine(directory, "runtime.journal"),
                CancellationToken.None);
            PumpUntilCompleted(host.Dispatcher, pending);
            var result = pending.GetAwaiter().GetResult();
            UnityDurableGateScenario.WritePassMarker(
                markerPath,
                "CompileHost",
                result);

            Assert.True(result.Passed);
            Assert.True(File.Exists(markerPath));
            var marker = ProtocolJson.ParseElement(
                File.ReadAllText(markerPath));
            Assert.Equal(
                UnityDurableGateScenario.MarkerSchema,
                marker.GetProperty("schema").GetString());
            Assert.Equal(
                "passed",
                marker.GetProperty("status").GetString());
            Assert.Equal(
                "CompileHost",
                marker.GetProperty("backend").GetString());
            Assert.True(
                marker.GetProperty("mainThreadReceipt").GetBoolean());
            Assert.True(
                marker.GetProperty("actionRequested").GetBoolean());
            Assert.True(
                marker.GetProperty("actionReceived").GetBoolean());
        }
        finally
        {
            host.ShutdownAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FacadeShutdownCancelsTrackedRunBeforeReturning()
    {
        using var dispatcher = new UnityMainThreadDispatcher(capacity: 8);
        var clock = new FakeRuntimeClock();
        var facade = new UnityAgentRuntimeFacade(
            new CancellationOnlyProvider(),
            new InMemorySessionStore(),
            dispatcher,
            (_, _) => throw new InvalidOperationException(
                "No action should be dispatched."),
            clock,
            new SequentialIdGenerator(),
            ownsSessionStore: false);

        var run = facade.RunAsync(
            CreateRunRequest(clock),
            CancellationToken.None);
        Assert.Equal(1, facade.ActiveRunCount);

        await facade.ShutdownAsync(CancellationToken.None);
        var outcome = await run;

        Assert.True(facade.IsShutdownRequested);
        Assert.Equal(0, facade.ActiveRunCount);
        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = facade.RunAsync(
                CreateRunRequest(clock),
                CancellationToken.None);
        });
    }

    [Fact]
    public async Task SynchronousBackendFailureCannotStrandShutdownSnapshot()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var backend = new SynchronousBlockingThrowBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        Exception? startFailure = null;
        var starting = Task.Run(
            () =>
            {
                try
                {
                    _ = facade.RunAsync(
                        new HeadlessRunRequest(),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    startFailure = exception;
                }
            });
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = facade.ShutdownAsync(CancellationToken.None).AsTask();
        release.Set();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<InvalidOperationException>(startFailure);
        Assert.Equal(0, facade.ActiveRunCount);
    }

    [Fact]
    public async Task FacadeFlushesAfterThrowingCancellationCallback()
    {
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var backend = new ThrowingCancellationBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: false);

        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run);

        Assert.True(store.FlushStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(0, facade.ActiveRunCount);
        Assert.True(facade.IsShutdownRequested);
    }

    [Fact]
    public async Task FacadeShutdownDoesNotRunCancellationCallbacksInline()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var backend = new BlockingCancellationCallbackBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);

        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        var shutdown = facade
            .ShutdownAsync(CancellationToken.None)
            .AsTask();
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(250),
            "ShutdownAsync synchronously ran a blocking cancellation callback.");
        Assert.False(shutdown.IsCompleted);

        release.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task FacadeCancelActiveRunsDoesNotRunCancellationCallbacksInline()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var backend = new BlockingCancellationCallbackBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var caller = Task.Run(facade.CancelActiveRuns);
        try
        {
            await backend.CancellationCallbackStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await caller.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            release.Set();
        }

        await caller.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await facade.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FacadeCallerCancellationDoesNotRunBackendCallbacksInline()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        using var callerCancellation = new CancellationTokenSource();
        var backend = new BlockingCancellationCallbackBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            callerCancellation.Token);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        callerCancellation.Cancel();
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(250),
            "Caller cancellation synchronously ran a backend callback.");
        await backend.CancellationCallbackStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await facade.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FacadeShutdownIsBoundedWhenCancellationCallbackBlocks()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var backend =
            new IndefinitelyBlockingCancellationCallbackBackend(release);
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = facade
            .ShutdownAsync(CancellationToken.None)
            .AsTask();
        try
        {
            await backend.CancellationCallbackStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var failure = await Assert.ThrowsAsync<AggregateException>(
                () => shutdown.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains(
                failure.Flatten().InnerExceptions,
                exception => exception is TimeoutException);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => UnityLifecycleCancellationDispatcher.ActiveCount == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task
        FacadeRunCancellationSaturationDoesNotBlockShutdownCancellation()
    {
        using var runRelease =
            new ManualResetEventSlim(initialState: false);
        using var shutdownRelease =
            new ManualResetEventSlim(initialState: false);
        var facades = new List<UnityAgentRuntimeFacade>();
        var runs = new List<Task<HeadlessRunOutcome>>();
        UnityAgentRuntimeFacade? shutdownFacade = null;

        try
        {
            for (var index = 0;
                 index < UnityRunCancellationDispatcher.Capacity;
                 index++)
            {
                var backend =
                    new IndefinitelyBlockingCancellationCallbackBackend(
                        runRelease);
                var facade = new UnityAgentRuntimeFacade(
                    backend,
                    new InMemorySessionStore(),
                    ownsSessionStore: false);
                var run = facade.RunAsync(
                    new HeadlessRunRequest(),
                    CancellationToken.None);
                await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                facades.Add(facade);
                runs.Add(run);
                facade.CancelActiveRuns();
                await backend.CancellationCallbackStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
            }

            Assert.Equal(
                UnityRunCancellationDispatcher.Capacity,
                UnityRunCancellationDispatcher.ActiveCount);
            Assert.Equal(
                0,
                UnityLifecycleCancellationDispatcher.ActiveCount);

            var shutdownBackend =
                new IndefinitelyBlockingCancellationCallbackBackend(
                    shutdownRelease);
            shutdownFacade = new UnityAgentRuntimeFacade(
                shutdownBackend,
                new InMemorySessionStore(),
                ownsSessionStore: false);
            runs.Add(
                shutdownFacade.RunAsync(
                    new HeadlessRunRequest(),
                    CancellationToken.None));
            await shutdownBackend.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            shutdownFacade.CancelActiveRuns();
            Assert.Equal(1, UnityRunCancellationDispatcher.PendingCount);
            shutdownFacade.RequestShutdown();
            await shutdownBackend.CancellationCallbackStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(
                UnityRunCancellationDispatcher.Capacity,
                UnityRunCancellationDispatcher.ActiveCount);
            Assert.Equal(
                1,
                UnityShutdownRunCancellationDispatcher.ActiveCount);
            Assert.True(
                SpinWait.SpinUntil(
                    () => UnityLifecycleCancellationDispatcher.ActiveCount
                        == 0,
                    TimeSpan.FromSeconds(2)));
        }
        finally
        {
            runRelease.Set();
            shutdownRelease.Set();

            foreach (var facade in facades)
            {
                try
                {
                    await facade.ShutdownAsync(CancellationToken.None);
                }
                catch
                {
                }
            }

            if (shutdownFacade is not null)
            {
                try
                {
                    await shutdownFacade.ShutdownAsync(
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            try
            {
                await Task.WhenAll(runs);
            }
            catch
            {
            }
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => UnityRunCancellationDispatcher.ActiveCount == 0
                    && UnityShutdownRunCancellationDispatcher.ActiveCount == 0
                    && UnityLifecycleCancellationDispatcher.ActiveCount == 0,
                TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task
        FacadeLifecycleCancellationWaitsForCapacityWithoutNewWorker()
    {
        var releases = new List<ManualResetEventSlim>();
        var facades = new List<UnityAgentRuntimeFacade>();
        var runs = new List<Task<HeadlessRunOutcome>>();
        using var overflowRelease =
            new ManualResetEventSlim(initialState: false);
        UnityAgentRuntimeFacade? overflowFacade = null;
        Task<HeadlessRunOutcome>? overflowRun = null;

        try
        {
            for (var index = 0;
                 index < UnityShutdownRunCancellationDispatcher.Capacity;
                 index++)
            {
                var release =
                    new ManualResetEventSlim(initialState: false);
                releases.Add(release);
                var backend =
                    new IndefinitelyBlockingCancellationCallbackBackend(
                        release);
                var facade = new UnityAgentRuntimeFacade(
                    backend,
                    new InMemorySessionStore(),
                    ownsSessionStore: false);
                var run = facade.RunAsync(
                    new HeadlessRunRequest(),
                    CancellationToken.None);
                await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                facades.Add(facade);
                runs.Add(run);
                facade.RequestShutdown();
                await backend.CancellationCallbackStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
            }

            Assert.Equal(
                UnityShutdownRunCancellationDispatcher.Capacity,
                UnityShutdownRunCancellationDispatcher.ActiveCount);

            var overflowBackend =
                new IndefinitelyBlockingCancellationCallbackBackend(
                    overflowRelease);
            overflowFacade = new UnityAgentRuntimeFacade(
                overflowBackend,
                new InMemorySessionStore(),
                ownsSessionStore: false);
            overflowRun = overflowFacade.RunAsync(
                new HeadlessRunRequest(),
                CancellationToken.None);
            await overflowBackend.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            overflowFacade.RequestShutdown();
            Assert.False(
                overflowFacade.RequiresShutdownCancellationAdmission);
            Assert.Equal(
                UnityShutdownRunCancellationDispatcher.Capacity,
                UnityShutdownRunCancellationDispatcher.ActiveCount);
            Assert.Equal(
                1,
                UnityShutdownRunCancellationDispatcher.PendingCount);
            Assert.False(
                overflowBackend.CancellationCallbackStarted.Task.IsCompleted);

            releases[0].Set();
            await overflowBackend.CancellationCallbackStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            overflowRelease.Set();
            await overflowFacade
                .ShutdownAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            overflowRelease.Set();
            foreach (var release in releases)
            {
                release.Set();
            }

            foreach (var facade in facades)
            {
                try
                {
                    await facade.ShutdownAsync(CancellationToken.None);
                }
                catch
                {
                }
            }

            if (overflowFacade is not null)
            {
                try
                {
                    await overflowFacade.ShutdownAsync(
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            try
            {
                await Task.WhenAll(
                    overflowRun is null
                        ? runs
                        : runs.Append(overflowRun));
            }
            catch
            {
            }

            foreach (var release in releases)
            {
                release.Dispose();
            }
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => UnityLifecycleCancellationDispatcher.ActiveCount == 0,
                TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task
        FacadeLifecycleLeasesRemainHeldUntilIgnoringRunsActuallyDrain()
    {
        var totalCapacity =
            UnityLifecycleCancellationDispatcher.Capacity
            + UnityLifecycleCancellationDispatcher.PendingCapacity;
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var facades = new List<UnityAgentRuntimeFacade>();
        var runs = new List<Task<HeadlessRunOutcome>>();
        var shutdowns = new List<Task>();
        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => UnityLifecycleCancellationDispatcher.LeaseCount == 0,
                    TimeSpan.FromSeconds(2)));
            for (var index = 0; index < totalCapacity; index++)
            {
                var backend =
                    new IgnoringCancellationBackend(release.Task);
                var facade = new UnityAgentRuntimeFacade(
                    backend,
                    new InMemorySessionStore(),
                    ownsSessionStore: false);
                facades.Add(facade);
                runs.Add(
                    facade.RunAsync(
                        new HeadlessRunRequest(),
                        CancellationToken.None));
                await backend.Started.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
            }

            var failure = Assert.Throws<InvalidOperationException>(
                () => new UnityAgentRuntimeFacade(
                    new ThrowingCancellationBackend(),
                    new InMemorySessionStore(),
                    ownsSessionStore: false));
            Assert.Contains(
                "lifecycle cancellation capacity is exhausted",
                failure.Message,
                StringComparison.Ordinal);

            shutdowns.AddRange(
                facades.Select(
                    facade => facade
                        .ShutdownAsync(CancellationToken.None)
                        .AsTask()));
            try
            {
                await Task.WhenAll(shutdowns).WaitAsync(
                    TimeSpan.FromSeconds(4));
            }
            catch
            {
            }

            Assert.All(shutdowns, shutdown => Assert.True(shutdown.IsCompleted));
            Assert.Equal(
                totalCapacity,
                UnityLifecycleCancellationDispatcher.LeaseCount);
        }
        finally
        {
            release.TrySetResult(true);
            try
            {
                await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            foreach (var facade in facades)
            {
                try
                {
                    await facade.ShutdownAsync(CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => UnityLifecycleCancellationDispatcher.LeaseCount == 0,
                TimeSpan.FromSeconds(3)));
        var recovered = new UnityAgentRuntimeFacade(
            new ThrowingCancellationBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);
        await recovered.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task
        FacadeDefersOwnedResourceCleanupUntilIgnoringRunActuallyDrains()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new OwnedIgnoringCancellationBackend(release.Task);
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: true,
            ownsBackend: true);
        var run = facade.RunAsync(
            new HeadlessRunRequest(),
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => facade
                .ShutdownAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(
            failure.Flatten().InnerExceptions,
            exception => exception is TimeoutException);
        Assert.False(backend.IsDisposed);
        Assert.False(store.FlushStarted.Task.IsCompleted);
        Assert.False(store.IsDisposed);

        release.TrySetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await facade
            .ShutdownAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(backend.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task FacadePreservesOwnedStoreWhenBackendShutdownFails()
    {
        var backend = new FailingLifecycleBackend();
        var store = new FailingLifecycleStore();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: true,
            ownsBackend: true);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());

        Assert.Single(failure.InnerExceptions);
        Assert.False(store.FlushAttempted);
        Assert.True(backend.DisposeAttempted);
        Assert.False(store.DisposeAttempted);
    }

    [Fact]
    public async Task FacadeRetriesBackendBeforeDisposingOwnedStore()
    {
        var backend = new FailsOnceLifecycleBackend();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: true,
            ownsBackend: true);

        await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, backend.DisposeAttempts);
        Assert.False(store.FlushStarted.Task.IsCompleted);
        Assert.False(store.IsDisposed);

        await facade.ShutdownAsync(CancellationToken.None);

        Assert.Equal(2, backend.DisposeAttempts);
        Assert.True(store.FlushStarted.Task.IsCompletedSuccessfully);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task FacadeRetriesOnlyIncompletePersistenceStages()
    {
        var backend = new RecordingLifecycleBackend();
        var store = new TransientLifecycleStore();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            store,
            ownsSessionStore: true,
            ownsBackend: true);

        await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, backend.DisposeAttempts);
        Assert.Equal(1, store.FlushAttempts);
        Assert.Equal(0, store.DisposeAttempts);

        await Assert.ThrowsAsync<AggregateException>(
            () => facade.ShutdownAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, backend.DisposeAttempts);
        Assert.Equal(2, store.FlushAttempts);
        Assert.Equal(1, store.DisposeAttempts);

        await facade.ShutdownAsync(CancellationToken.None);

        Assert.Equal(1, backend.DisposeAttempts);
        Assert.Equal(2, store.FlushAttempts);
        Assert.Equal(2, store.DisposeAttempts);
    }

    [Fact]
    public async Task FacadeRoutesDurableRunAndResumeThroughInjectedBackend()
    {
        var backend = new RecordingDurableBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var request = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "durable-run",
                State = RunStates.Queued
            }
        };

        var started = await facade.RunAsync(
            request,
            CancellationToken.None);
        var resumed = await facade.ResumeAsync(
            "resumed-run",
            cancellationToken: CancellationToken.None);

        Assert.Same(backend.Controls, facade.DurableControls);
        Assert.NotSame(request, backend.LastRequest);
        Assert.Equal("durable-run", backend.LastRequest!.Run.RunId);
        Assert.Equal("durable-run", started.Run.RunId);
        Assert.Equal("resumed-run", backend.LastResumeRunId);
        Assert.Equal("resumed-run", resumed.Run.RunId);
        Assert.Equal(0, facade.ActiveRunCount);

        await facade.DisposeAsync();
    }

    [Fact]
    public async Task FacadeAndHostExposeTrackedRoutingAndStatelessCompletion()
    {
        var backend = new RecordingRoutedBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var routedRequest = new RoutedExecutionRequest
        {
            Route = new ExecutionRouteRequest
            {
                OperationKind = "npc-bark"
            },
            Run = new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "unity-routed-run",
                    State = RunStates.Queued
                }
            }
        };
        var completionRequest = new SimpleCompletionRequest
        {
            OperationId = "unity-completion",
            Messages = new[]
            {
                new NormalizedMessage
                {
                    MessageId = "unity-completion-message",
                    Role = NormalizedRoles.User,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("Classify event.")
                    }
                }
            }
        };

        var routed = await facade.RunRoutedAsync(
            routedRequest,
            CancellationToken.None);
        var completed = await facade.CompleteAsync(
            completionRequest,
            CancellationToken.None);
        var childRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "unity-child-run",
                State = RunStates.Queued
            }
        };
        var child = await facade.RunChildAsync(
            "unity-parent-run",
            childRequest,
            CancellationToken.None);
        Assert.NotSame(childRequest, backend.LastChildRequest);
        Assert.Equal("unity-child-run", backend.LastChildRequest!.Run.RunId);
        Assert.Equal("unity-parent-run", backend.LastParentRunId);
        var persistedParent = new AgentRun
        {
            RunId = "unity-persisted-parent",
            State = RunStates.Completed,
            Extensions = new Dictionary<string, JsonElement>
            {
                [ChildAgentLineage.ExtensionName] =
                    ProtocolJson.ParseElement(
                        """
                        {"rootRunId":"unity-root","parentRunId":"unity-root","childRunId":"unity-persisted-parent","depth":1}
                        """)
            }
        };
        var grandchild = await facade.RunChildAsync(
            persistedParent,
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "unity-grandchild",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);

        Assert.NotSame(routedRequest, backend.LastRoutedRequest);
        Assert.Equal(
            "npc-bark",
            backend.LastRoutedRequest!.Route.OperationKind);
        Assert.NotSame(completionRequest, backend.LastCompletionRequest);
        Assert.Equal(
            "unity-completion",
            backend.LastCompletionRequest!.OperationId);
        Assert.Equal(1, child.Lineage.Depth);
        Assert.NotSame(persistedParent, backend.LastParentRun);
        Assert.Equal(
            "unity-persisted-parent",
            backend.LastParentRun!.RunId);
        Assert.Equal("unity-root", grandchild.Lineage.RootRunId);
        Assert.Equal(2, grandchild.Lineage.Depth);
        Assert.Equal(ExecutionPath.Direct, routed.Decision.Path);
        Assert.Equal("unity-completed", completed.Text);
        Assert.Equal(0, facade.ActiveRunCount);
        await facade.DisposeAsync();

        var host = new GameObject("GameAgentRuntimeRoutedTest")
            .AddComponent<UnityAgentRuntimeHost>();
        RoutedExecutionOutcome? observedRoute = null;
        SimpleCompletionOutcome? observedCompletion = null;
        host.RoutedRunCompleted += outcome => observedRoute = outcome;
        host.CompletionCompleted += outcome => observedCompletion = outcome;
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);

        var hostRoute = await host.RunRoutedAsync(
            routedRequest,
            CancellationToken.None);
        var hostCompletion = await host.CompleteAsync(
            completionRequest,
            CancellationToken.None);
        var hostChild = await host.RunChildAsync(
            "unity-host-parent",
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "unity-host-child",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        PumpHost(host);

        Assert.Same(hostRoute, observedRoute);
        Assert.Same(hostCompletion, observedCompletion);
        Assert.Equal("unity-host-parent", hostChild.Lineage.ParentRunId);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FacadeExposesFailClosedGuardedResume()
    {
        var backend = new RecordingGuardedDurableBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var semantic = ProtocolJson.ParseElement(
            """{"revision":12,"timeline":"prime"}""");
        var guard = new DurableRunResumeGuard
        {
            SemanticExtensionName = "game.coordinate",
            ExpectedSemanticExtensionSha256 =
                CanonicalJsonDigest.ComputeSha256(semantic)
        };

        var outcome = await facade.ResumeAsync(
            "guarded-run",
            guard,
            cancellationToken: CancellationToken.None);

        Assert.Equal("guarded-run", outcome.Run.RunId);
        Assert.NotSame(guard, backend.LastGuard);
        Assert.Equal(
            guard.ExpectedSemanticExtensionSha256,
            backend.LastGuard!.ExpectedSemanticExtensionSha256);
        Assert.Equal(0, facade.ActiveRunCount);
        await facade.DisposeAsync();

        var hostBackend = new RecordingGuardedDurableBackend();
        var host = new GameObject("GuardedResumeHost")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            hostBackend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var hostOutcome = await host.ResumeAsync(
            "host-guarded-run",
            guard,
            cancellationToken: CancellationToken.None);
        Assert.Equal("host-guarded-run", hostOutcome.Run.RunId);
        Assert.NotSame(guard, hostBackend.LastGuard);
        Assert.Equal(
            guard.ExpectedSemanticExtensionSha256,
            hostBackend.LastGuard!.ExpectedSemanticExtensionSha256);
        await host.ShutdownAsync(CancellationToken.None);

        var unsupportedFacade = new UnityAgentRuntimeFacade(
            new RecordingDurableBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var unsupported =
            await Assert.ThrowsAsync<DurableRunResumeGuardException>(
                () => unsupportedFacade.ResumeAsync(
                    "unsupported-run",
                    guard));
        Assert.Equal(
            DurableRunResumeGuardReasonCodes.NotSupported,
            unsupported.ReasonCode);
        await unsupportedFacade.DisposeAsync();
    }

    [Fact]
    public async Task FacadeSnapshotsMutableRequestsBeforeReturning()
    {
        var durableBackend = new RecordingDurableBackend();
        var durableFacade = new UnityAgentRuntimeFacade(
            durableBackend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var durableRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "snapshot-durable",
                State = RunStates.Queued
            }
        };

        var durableTask = durableFacade.RunAsync(
            durableRequest,
            CancellationToken.None);
        durableRequest.Run.RunId = "caller-mutated-durable";
        await durableTask;

        Assert.Equal(
            "snapshot-durable",
            durableBackend.LastRequest!.Run.RunId);
        await durableFacade.DisposeAsync();

        var routedBackend = new RecordingRoutedBackend();
        var routedFacade = new UnityAgentRuntimeFacade(
            routedBackend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var routedRequest = new RoutedExecutionRequest
        {
            Route = new ExecutionRouteRequest
            {
                OperationKind = "snapshot-route"
            },
            Run = new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "snapshot-routed-run",
                    State = RunStates.Queued
                }
            }
        };
        var completionRequest = new SimpleCompletionRequest
        {
            OperationId = "snapshot-completion",
            Messages = new[]
            {
                new NormalizedMessage
                {
                    MessageId = "snapshot-message",
                    Role = NormalizedRoles.User,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("Classify.")
                    }
                }
            }
        };

        var routedTask = routedFacade.RunRoutedAsync(
            routedRequest,
            CancellationToken.None);
        routedRequest.Route.OperationKind = "caller-mutated-route";
        routedRequest.Run.Run.RunId = "caller-mutated-run";
        var completionTask = routedFacade.CompleteAsync(
            completionRequest,
            CancellationToken.None);
        completionRequest.OperationId = "caller-mutated-completion";
        await Task.WhenAll(routedTask, completionTask);

        Assert.Equal(
            "snapshot-route",
            routedBackend.LastRoutedRequest!.Route.OperationKind);
        Assert.Equal(
            "snapshot-routed-run",
            routedBackend.LastRoutedRequest.Run!.Run.RunId);
        Assert.Equal(
            "snapshot-completion",
            routedBackend.LastCompletionRequest!.OperationId);
        Assert.Equal(0, routedFacade.ActiveRunCount);
        await routedFacade.DisposeAsync();
    }

    [Fact]
    public async Task FacadeSnapshotFailureReleasesRunAdmission()
    {
        var backend = new RecordingRoutedBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var runLeaseBaseline = UnityRunCancellationDispatcher.LeaseCount;
        var shutdownLeaseBaseline =
            UnityShutdownRunCancellationDispatcher.LeaseCount;
        var request = new SimpleCompletionRequest
        {
            OperationId = "snapshot-failure",
            Messages = new ThrowingReadOnlyList<NormalizedMessage>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.CompleteAsync(request, CancellationToken.None));

        Assert.True(
            SpinWait.SpinUntil(
                () => facade.ActiveRunCount == 0
                    && UnityRunCancellationDispatcher.LeaseCount
                        == runLeaseBaseline
                    && UnityShutdownRunCancellationDispatcher.LeaseCount
                        == shutdownLeaseBaseline,
                TimeSpan.FromSeconds(2)));
        Assert.Null(backend.LastCompletionRequest);
        await facade.DisposeAsync();
    }

    [Fact]
    public async Task FacadeCreatesTrackedMultiActorCoordinatorWithLifecycle()
    {
        var lifecycle = new RecordingMultiActorLifecycle();
        var runtime = new RecordingGuardedMultiActorRuntime(
            lifecycle,
            pauseInitialRuns: false);
        var facade = new UnityAgentRuntimeFacade(
            runtime,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            ownsRuntime: false,
            maxActiveRuns: 4);
        var coordinator = facade.CreateMultiActorCoordinator(
            new MultiActorCoordinatorOptions(
                maxBatchSize: 4,
                maxConcurrentRuns: 2),
            lifecycle);

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "unity-batch",
                MultiActorCoordinate(),
                new[]
                {
                    CreateMultiActorRequest(0),
                    CreateMultiActorRequest(1)
                }));

        Assert.Equal("unity-batch", outcome.Manifest.BatchId);
        Assert.Equal(
            "session-1",
            outcome.Manifest.Coordinate.SessionId);
        Assert.Equal(2, outcome.Manifest.Participants.Count);
        Assert.Equal(
            new[] { "npc-0", "npc-1" },
            outcome.Results.Select(result => result.AgentId));
        Assert.All(outcome.Results, result => Assert.True(result.Succeeded));
        Assert.True(runtime.AllRunsObservedManifest);
        Assert.Equal(
            new[] { "npc-0", "npc-1" },
            lifecycle.FinishedAgentIds.OrderBy(
                value => value,
                StringComparer.Ordinal));
        Assert.Null(lifecycle.AbortedBatchId);
        Assert.Equal(0, facade.ActiveRunCount);

        await facade.DisposeAsync();
    }

    [Fact]
    public async Task HostCoordinatorFactoryPreservesGuardedParticipantResume()
    {
        var lifecycle = new RecordingMultiActorLifecycle();
        var runtime = new RecordingGuardedMultiActorRuntime(
            lifecycle,
            pauseInitialRuns: true);
        var host = new GameObject("GameAgentRuntimeMultiActorTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            ownsRuntime: false);
        var coordinator = host.CreateMultiActorCoordinator(
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxConcurrentRuns: 1,
                maxConcurrentParticipantResumes: 1),
            lifecycle);

        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "unity-resume-batch",
                MultiActorCoordinate(),
                new[] { CreateMultiActorRequest(7) }));
        var paused = Assert.Single(batch.Results);
        Assert.NotNull(paused.Outcome);
        Assert.False(paused.Outcome!.IsTerminal);
        Assert.Empty(lifecycle.FinishedAgentIds);

        var participant = Assert.Single(batch.Manifest.Participants);
        var semanticExpectation = DurableRunSemanticExpectation.FromJson(
            "game.currentCoordinate",
            ProtocolJson.ParseElement("""{"revision":13}"""));
        var resumed = await coordinator.ResumeParticipantAsync(
            batch.BatchId,
            participant,
            semanticExpectation);

        Assert.True(resumed.Outcome!.IsTerminal);
        Assert.NotNull(runtime.LastResumeGuard);
        Assert.Equal(
            batch.BatchId,
            runtime.LastResumeGuard!.ExpectedBatchId);
        Assert.Equal(
            participant.AgentId,
            runtime.LastResumeGuard.ExpectedAgentId);
        Assert.Equal(
            participant.DecisionKey,
            runtime.LastResumeGuard.ExpectedDecisionKey);
        Assert.Equal(
            participant.InputIndex,
            runtime.LastResumeGuard.ExpectedInt32ExtensionValue);
        Assert.Equal(
            semanticExpectation.ExtensionName,
            runtime.LastResumeGuard.SemanticExtensionName);
        Assert.Equal(
            semanticExpectation.ExpectedSha256,
            runtime.LastResumeGuard.ExpectedSemanticExtensionSha256);
        Assert.Equal(
            new[] { participant.AgentId },
            lifecycle.FinishedAgentIds);
        Assert.Null(lifecycle.AbortedBatchId);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostCoordinatorFactoryPreservesGuardedAbandonment()
    {
        var lifecycle = new RecordingMultiActorLifecycle();
        var runtime = new RecordingGuardedMultiActorRuntime(
            lifecycle,
            pauseInitialRuns: true);
        var host = new GameObject("GameAgentRuntimeAbandonmentTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            ownsRuntime: false);
        var coordinator = host.CreateMultiActorCoordinator(
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxConcurrentRuns: 1,
                maxConcurrentParticipantResumes: 1),
            lifecycle);

        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "unity-abandon-batch",
                MultiActorCoordinate(),
                new[] { CreateMultiActorRequest(8) }));
        var participant = Assert.Single(batch.Manifest.Participants);

        var abandoned =
            await coordinator.ReconcileAbandonedParticipantAsync(
                batch.BatchId,
                participant,
                "actor_removed");

        Assert.Equal(RunStates.Cancelled, abandoned.Outcome!.Run.State);
        var error = Assert.IsType<MultiActorParticipantAbandonedException>(
            abandoned.Error);
        Assert.Equal("actor_removed", error.ReasonCode);
        Assert.True(runtime.LastContinuation!.RequestCancellation);
        Assert.NotNull(runtime.LastResumeGuard);
        Assert.Equal(
            batch.BatchId,
            runtime.LastResumeGuard!.ExpectedBatchId);
        Assert.Equal(
            participant.AgentId,
            runtime.LastResumeGuard.ExpectedAgentId);
        Assert.Equal(
            participant.DecisionKey,
            runtime.LastResumeGuard.ExpectedDecisionKey);
        Assert.Equal(
            new[] { participant.AgentId },
            lifecycle.FinishedAgentIds);
        Assert.Null(lifecycle.AbortedBatchId);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MultiActorFactoryRejectsBackendWithoutGuardedResume()
    {
        var facade = new UnityAgentRuntimeFacade(
            new RecordingDurableBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);

        var error = Assert.Throws<DurableRunResumeGuardException>(
            () => facade.CreateMultiActorCoordinator());

        Assert.Equal(
            DurableRunResumeGuardReasonCodes.NotSupported,
            error.ReasonCode);
        await facade.DisposeAsync();
    }

    [Fact]
    public async Task MultiActorFactoryRejectsUnguardedRuntimeConveniencePath()
    {
        var runtime = new OwnedDurableRuntime();
        var facade = new UnityAgentRuntimeFacade(
            runtime,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            ownsRuntime: false);

        var error = Assert.Throws<DurableRunResumeGuardException>(
            () => facade.CreateMultiActorCoordinator());

        Assert.Equal(
            DurableRunResumeGuardReasonCodes.NotSupported,
            error.ReasonCode);
        Assert.False(runtime.IsDisposed);
        await facade.DisposeAsync();
    }

    [Fact]
    public async Task HostExposesDurableBackendControlsAndCompletion()
    {
        var backend = new RecordingDurableBackend();
        var host = new GameObject("GameAgentRuntimeDurableTest")
            .AddComponent<UnityAgentRuntimeHost>();
        DurableRunOutcome? observed = null;
        host.DurableRunCompleted += outcome => observed = outcome;
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var request = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "host-durable-run",
                State = RunStates.Queued
            }
        };

        var outcome = await host.RunAsync(
            request,
            CancellationToken.None);
        PumpHost(host);

        Assert.Same(backend.Controls, host.DurableControls);
        Assert.Same(outcome, observed);

        await host.ShutdownAsync(CancellationToken.None);
        Assert.True(host.Dispatcher.IsShutdown);
    }

    [Fact]
    public async Task TerminalCompletionIsIndependentFromAFullActionQueue()
    {
        var host = new GameObject("UnityTerminalIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            new RecordingDurableBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);
        DurableRunOutcome? observed = null;
        host.DurableRunCompleted += outcome => observed = outcome;
        for (var index = 0; index < host.Dispatcher.Capacity; index++)
        {
            Assert.True(host.Dispatcher.TryPost(() => { }));
        }

        var outcome = await host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "terminal-isolation-run",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);

        Assert.Equal(1, host.PendingTerminalObserverCount);
        PumpHost(host);
        Assert.Same(outcome, observed);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentFaultEventsRetainRunIdentity()
    {
        var backend = new ControlledFailingDurableBackend();
        var host = new GameObject("UnityFaultIdentityTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var faults = new List<UnityRunFault>();
        host.RunFaultedDetailed += faults.Add;

        var first = host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "fault-run-a",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        var second = host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "fault-run-b",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        backend.Fail("fault-run-b");
        Assert.Throws<InvalidOperationException>(
            () => second.GetAwaiter().GetResult());
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 1,
                TimeSpan.FromSeconds(2)));
        backend.Fail("fault-run-a");
        Assert.Throws<InvalidOperationException>(
            () => first.GetAwaiter().GetResult());
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 2,
                TimeSpan.FromSeconds(2)));

        PumpHost(host);

        Assert.Equal(
            new[] { "fault-run-b", "fault-run-a" },
            faults.Select(item => item.RunId));
        Assert.All(
            faults,
            item =>
            {
                Assert.Equal("durable_run", item.OperationKind);
                Assert.True(item.ReconciliationRequired);
                Assert.IsType<InvalidOperationException>(item.Exception);
            });
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownPreservesPublishedTerminalObserverForLaterPump()
    {
        var host = new GameObject("UnityTerminalShutdownDrainTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            new RecordingDurableBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);
        DurableRunOutcome? observed = null;
        host.DurableRunCompleted += outcome => observed = outcome;
        var outcome = await host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "shutdown-terminal-run",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 1,
                TimeSpan.FromSeconds(2)));

        await host.ShutdownAsync(CancellationToken.None);

        Assert.Null(observed);
        Assert.Equal(1, host.PendingTerminalObserverCount);
        Assert.Equal(1, host.TerminalObserverReservationCount);
        Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = host.RunAsync(
                    new DurableRunRequest
                    {
                        Run = new AgentRun
                        {
                            RunId = "shutdown-rejected-run",
                            State = RunStates.Queued
                        }
                    },
                    CancellationToken.None);
            });

        PumpHost(host);

        Assert.Same(outcome, observed);
        Assert.Equal(0, host.PendingTerminalObserverCount);
        Assert.Equal(0, host.TerminalObserverReservationCount);
    }

    [Fact]
    public async Task
        TerminalQueueStopsNewReservationsButDrainsIssuedPublishers()
    {
        using var queue = new UnityTerminalObserverQueue(capacity: 1);
        Assert.True(queue.TryReserve(out var issued));

        var publisherDrain = queue.StopAccepting();

        Assert.False(publisherDrain.IsCompleted);
        Assert.False(queue.TryReserve(out _));
        var observed = 0;
        Assert.True(issued.Publish(() => observed++));
        await publisherDrain.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.ReservedCount);
        Assert.Equal(
            1,
            queue.Pump(
                maxItems: 1,
                maxMilliseconds: 10,
                report: null));
        Assert.Equal(1, observed);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ReservedCount);
    }

    [Fact]
    public async Task HostShutdownWaitsForIssuedTerminalPublisher()
    {
        var host = new GameObject("UnityTerminalPublisherDrainTest")
            .AddComponent<UnityAgentRuntimeHost>();
        _ = host.Dispatcher;
        var queue = Assert.IsType<UnityTerminalObserverQueue>(
            typeof(UnityAgentRuntimeHost)
                .GetField(
                    "_terminalObservers",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host));
        Assert.True(queue.TryReserve(out var issued));
        var observed = 0;

        var shutdown = host.ShutdownAsync(CancellationToken.None);

        Assert.False(shutdown.IsCompleted);
        Assert.False(queue.TryReserve(out _));
        Assert.True(issued.Publish(() => observed++));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(host.Dispatcher.IsShutdown);
        Assert.Equal(1, host.PendingTerminalObserverCount);
        Assert.Equal(1, host.TerminalObserverReservationCount);
        PumpHost(host);
        Assert.Equal(1, observed);
        Assert.Equal(0, host.PendingTerminalObserverCount);
        Assert.Equal(0, host.TerminalObserverReservationCount);
    }

    [Fact]
    public async Task AbandonedTerminalReservationCompletesPublisherDrain()
    {
        using var queue = new UnityTerminalObserverQueue(capacity: 1);
        Assert.True(queue.TryReserve(out var issued));
        var publisherDrain = queue.StopAccepting();

        issued.Dispose();
        await publisherDrain.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ReservedCount);
    }

    [Fact]
    public async Task SuccessObserversAreIsolatedPerSubscriberAndApiShape()
    {
        var headlessHost = new GameObject("UnityHeadlessObserverIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var headlessClock = new FakeRuntimeClock();
        headlessHost.Configure(
            new CompletingProvider(),
            new InMemorySessionStore(),
            (_, _) => throw new InvalidOperationException(
                "No action should be dispatched."),
            headlessClock,
            new SequentialIdGenerator(),
            ownsSessionStore: false);
        var headlessObserved = 0;
        headlessHost.RunCompleted += _ =>
            throw new InvalidOperationException("malicious headless observer");
        headlessHost.RunCompleted += _ => headlessObserved++;

        _ = await headlessHost.RunAsync(
            CreateRunRequest(headlessClock),
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => headlessHost.PendingTerminalObserverCount == 1,
                TimeSpan.FromSeconds(2)));
        PumpHost(headlessHost);
        Assert.Equal(1, headlessObserved);
        await headlessHost.ShutdownAsync(CancellationToken.None);

        var backend = new RecordingRoutedBackend();
        var host = new GameObject("UnitySuccessObserverIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var durableObserved = 0;
        var routedObserved = 0;
        var completionObserved = 0;
        host.DurableRunCompleted += _ =>
            throw new InvalidOperationException("malicious durable observer");
        host.DurableRunCompleted += _ => durableObserved++;
        host.RoutedRunCompleted += _ =>
            throw new InvalidOperationException("malicious routed observer");
        host.RoutedRunCompleted += _ => routedObserved++;
        host.CompletionCompleted += _ =>
            throw new InvalidOperationException("malicious completion observer");
        host.CompletionCompleted += _ => completionObserved++;

        _ = await host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "isolated-success-run",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        _ = await host.RunChildAsync(
            "isolated-success-parent",
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "isolated-success-child",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        _ = await host.RunRoutedAsync(
            new RoutedExecutionRequest(),
            CancellationToken.None);
        _ = await host.CompleteAsync(
            new SimpleCompletionRequest
            {
                OperationId = "isolated-success-completion"
            },
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 4,
                TimeSpan.FromSeconds(2)));

        PumpHost(host);

        Assert.Equal(2, durableObserved);
        Assert.Equal(1, routedObserved);
        Assert.Equal(1, completionObserved);
        Assert.Equal(0, host.TerminalObserverReservationCount);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FaultObserversAreIsolatedPerSubscriberAndApiShape()
    {
        var backend = new ControlledFailingDurableBackend();
        var host = new GameObject("UnityFaultObserverIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var detailedObserved = 0;
        var legacyObserved = 0;
        host.RunFaultedDetailed += _ =>
            throw new InvalidOperationException("malicious detailed observer");
        host.RunFaultedDetailed += _ => detailedObserved++;
        host.RunFaulted += _ =>
            throw new InvalidOperationException("malicious legacy observer");
        host.RunFaulted += _ => legacyObserved++;
        var run = host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "isolated-fault-run",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);

        backend.Fail("isolated-fault-run");
        Assert.Throws<InvalidOperationException>(
            () => run.GetAwaiter().GetResult());
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 1,
                TimeSpan.FromSeconds(2)));
        PumpHost(host);

        Assert.Equal(1, detailedObserved);
        Assert.Equal(1, legacyObserved);
        Assert.Equal(0, host.TerminalObserverReservationCount);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TerminalObserverPumpChecksBudgetBetweenCallbacks()
    {
        var host = new GameObject("UnityTerminalBudgetTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            new RecordingDurableBackend(),
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var observed = 0;
        host.DurableRunCompleted += _ =>
        {
            Thread.Sleep(20);
            observed++;
        };
        var runs = Enumerable.Range(0, 3)
            .Select(index => host.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "terminal-budget-" + index,
                        State = RunStates.Queued
                    }
                },
                CancellationToken.None))
            .ToArray();
        await Task.WhenAll(runs);
        Assert.True(
            SpinWait.SpinUntil(
                () => host.PendingTerminalObserverCount == 3,
                TimeSpan.FromSeconds(2)));

        PumpHost(host);

        Assert.Equal(1, observed);
        Assert.Equal(2, host.PendingTerminalObserverCount);
        PumpHost(host);
        Assert.Equal(2, observed);
        Assert.Equal(1, host.PendingTerminalObserverCount);
        PumpHost(host);
        Assert.Equal(3, observed);
        Assert.Equal(0, host.PendingTerminalObserverCount);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublishesRuntimeEventSnapshotsOnMainThread()
    {
        var host = new GameObject("GameAgentRuntimeEventTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var mainThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;
        RuntimeEvent? observed = null;
        host.RuntimeEventPublished += runtimeEvent =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            observed = runtimeEvent;
        };
        var source = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 1,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement("""{"state":"preparing"}""")
        };

        host.EventPublisher.Publish(source);
        source.Kind = "mutated";
        typeof(UnityAgentRuntimeHost)
            .GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host, null);

        Assert.NotNull(observed);
        Assert.Equal(RuntimeEventKinds.RunStarted, observed!.Kind);
        Assert.Equal(mainThread, callbackThread);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostIsolatesRuntimeEventSubscribers()
    {
        var host = new GameObject("GameAgentRuntimeEventObserverIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var observed = 0;
        host.RuntimeEventPublished += _ =>
            throw new InvalidOperationException("malicious runtime observer");
        host.RuntimeEventPublished += _ => observed++;

        host.EventPublisher.Publish(
            new RuntimeEvent
            {
                EventId = "isolated-event",
                RunId = "isolated-run",
                Sequence = 1,
                Kind = RuntimeEventKinds.RunStarted,
                Durability = EventDurabilities.Durable,
                RuntimeGeneration = 1,
                Timestamp = DateTimeOffset.UnixEpoch,
                Payload = ProtocolJson.ParseElement("{}")
            });
        PumpHost(host);

        Assert.Equal(1, observed);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostIsolatesApplicationPauseSubscribers()
    {
        var host = new GameObject("GameAgentPauseObserverIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var observed = 0;
        host.ApplicationPauseChanged += _ =>
            throw new InvalidOperationException("malicious pause observer");
        host.ApplicationPauseChanged += paused =>
        {
            if (paused)
            {
                observed++;
            }
        };

        typeof(UnityAgentRuntimeHost)
            .GetMethod(
                "OnApplicationPause",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host, new object[] { true });

        Assert.Equal(1, observed);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RuntimeEventBurstCannotStarveActionDispatcher()
    {
        var host = new GameObject("GameAgentRuntimeEventIsolationTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var runtimeEvent = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 1,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Ephemeral,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement("""{"state":"preparing"}""")
        };

        for (var index = 0; index < 2_048; index++)
        {
            runtimeEvent.EventId = "event-" + index;
            host.EventPublisher.Publish(runtimeEvent);
        }

        Assert.Equal(0, host.Dispatcher.PendingCount);
        Assert.True(host.DroppedRuntimeEventCount > 0);

        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostDoesNotCreateEventPublisherAfterShutdown()
    {
        var host = new GameObject("GameAgentRuntimeNoResurrectionTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var dispatcher = host.Dispatcher;

        await host.ShutdownAsync(CancellationToken.None);

        Assert.Same(dispatcher, host.Dispatcher);
        Assert.True(dispatcher.IsShutdown);
        Assert.Throws<ObjectDisposedException>(
            () => _ = host.EventPublisher);
        Assert.Null(
            typeof(UnityAgentRuntimeHost)
                .GetField(
                    "_runtimeEventDispatcher",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host));
        Assert.Null(
            typeof(UnityAgentRuntimeHost)
                .GetField(
                    "_eventPublisher",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host));
    }

    [Fact]
    public async Task FacadeRejectsRunsBeyondConfiguredCapacity()
    {
        var backend = new BlockingDurableBackend();
        var facade = new UnityAgentRuntimeFacade(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false,
            maxActiveRuns: 1);
        var firstRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "first",
                State = RunStates.Queued
            }
        };
        var secondRequest = new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "second",
                State = RunStates.Queued
            }
        };

        var first = facade.RunAsync(firstRequest, CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rejected = Assert.IsType<UnityRunCapacityExceededException>(
            Record.Exception(
                () =>
                {
                    _ = facade.RunAsync(
                        secondRequest,
                        CancellationToken.None);
                }));
        Assert.Equal(1, rejected.Capacity);

        backend.Release();
        await first;
        await facade.DisposeAsync();
    }

    [Fact]
    public async Task HostOwnsCompleteBuilderComposition()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-unity-built-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            var host = new GameObject("GameAgentRuntimeBuiltTest")
                .AddComponent<UnityAgentRuntimeHost>();
            var built = new GameAgentRuntimeBuilder(
                    new UnityMainThreadGameHost(
                        host.Dispatcher,
                        (_, _) => throw new InvalidOperationException(
                            "No action should be dispatched.")))
                .UseFileJournal(path)
                .AddProvider(new DurableLoopProvider())
                .Build();

            host.Configure(built);
            Assert.Same(
                built.Runtime.Controls,
                host.DurableControls);

            await host.ShutdownAsync(CancellationToken.None);

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HostDisposesOwnedDurableRuntimeAndStore()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimeOwnershipTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);

        await host.ShutdownAsync(CancellationToken.None);

        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public void HostApplicationQuitCompletesOwnedCleanupWithinCallback()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimePlayerQuitTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);

        typeof(UnityAgentRuntimeHost)
            .GetMethod(
                "OnApplicationQuit",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host, null);

        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task HostApplicationQuitTimeoutSetsIncompleteState()
    {
        using var release =
            new ManualResetEventSlim(initialState: false);
        var host = new GameObject("GameAgentRuntimePlayerQuitTimeoutTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var workStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StartBlockingDispatcherWork(
            host,
            release,
            workStarted,
            cancellationStarted);
        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var elapsed = Stopwatch.StartNew();
            typeof(UnityAgentRuntimeHost)
                .GetMethod(
                    "OnApplicationQuit",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(host, null);
            elapsed.Stop();

            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(4));
            Assert.True(host.IsShutdownIncomplete);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(2)));
        await host.ShutdownAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(host.IsShutdownIncomplete);
    }

    [Fact]
    public async Task
        HostReportsIncompleteWhenCancellationCallbackFails()
    {
        var backend = new ThrowingCancellationDurableBackend();
        var host = new GameObject(
                "GameAgentRuntimeCancellationFailureTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            backend,
            new InMemorySessionStore(),
            ownsSessionStore: false);
        var running = host.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "throwing-cancellation-host-run",
                    State = RunStates.Queued
                }
            },
            CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<AggregateException>(
            () => host.ShutdownAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running);

        Assert.True(host.IsShutdownIncomplete);
        Assert.True(host.Dispatcher.IsShutdown);
    }

    [Fact]
    public async Task HostShutdownWaitsForStartedDispatcherWorkBeforeDisposal()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimeDrainTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(
            async () => await host.Dispatcher.InvokeAsync(
                async cancellationToken =>
                {
                    started.TrySetResult(cancellationToken);
                    return await release.Task;
                },
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => host.Dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
        host.Dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        var handlerToken = await started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var shutdown = host.ShutdownAsync(
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => host.Dispatcher.IsShutdown,
                TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => handlerToken.IsCancellationRequested,
                TimeSpan.FromSeconds(2)));
        Assert.False(shutdown.IsCompleted);
        Assert.False(runtime.IsDisposed);
        Assert.False(store.IsDisposed);
        Assert.Equal(1, host.Dispatcher.RunningCount);

        release.TrySetResult(7);
        Assert.Equal(7, await running);
        await shutdown;

        Assert.Equal(0, host.Dispatcher.RunningCount);
        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task
        HostDefersOwnedResourceCleanupAfterDispatcherDrainTimeout()
    {
        var runtime = new OwnedDurableRuntime();
        var store = new BlockingDurableStore();
        store.ReleaseFlush();
        var host = new GameObject("GameAgentRuntimeTimedDrainTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            runtime,
            store,
            ownsSessionStore: true,
            ownsRuntime: true);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(
            async () => await host.Dispatcher.InvokeAsync(
                async _ =>
                {
                    started.TrySetResult(true);
                    return await release.Task;
                },
                CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(
                () => host.Dispatcher.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
        host.Dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => host
                .ShutdownAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(
            failure.Flatten().InnerExceptions,
            exception => exception is TimeoutException);
        Assert.False(runtime.IsDisposed);
        Assert.False(store.FlushStarted.Task.IsCompleted);
        Assert.False(store.IsDisposed);

        release.TrySetResult(9);
        Assert.Equal(9, await running.WaitAsync(TimeSpan.FromSeconds(2)));
        await host
            .ShutdownAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(runtime.IsDisposed);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task
        HostShutdownIsBoundedAndRetriableWhenDispatcherCancellationBlocks()
    {
        using var release =
            new ManualResetEventSlim(initialState: false);
        var host = new GameObject("GameAgentRuntimeBoundedShutdownTest")
            .AddComponent<UnityAgentRuntimeHost>();
        var workStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StartBlockingDispatcherWork(
            host,
            release,
            workStarted,
            cancellationStarted);
        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        var shutdown = host.ShutdownAsync(CancellationToken.None);
        elapsed.Stop();
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(250),
            "Host shutdown synchronously ran dispatcher cancellation callbacks.");

        try
        {
            await cancellationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var failure = await Assert.ThrowsAsync<AggregateException>(
                () => shutdown.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Contains(
                failure.Flatten().InnerExceptions,
                exception => exception is TimeoutException);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(2)));
        await host.ShutdownAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(
            SpinWait.SpinUntil(
                () => UnityLifecycleCancellationDispatcher.ActiveCount == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task
        HostOnDestroyQueuesLifecycleCancellationAcrossProcessSaturation()
    {
        var releases = new List<ManualResetEventSlim>();
        var hosts = new List<UnityAgentRuntimeHost>();
        var running = new List<Task<int>>();
        var shutdowns = new List<Task>();
        using var overflowRelease =
            new ManualResetEventSlim(initialState: false);
        UnityAgentRuntimeHost? overflowHost = null;
        Task<int>? overflowRunning = null;
        var overflowWorkStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overflowCancellationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var instanceField = typeof(UnityAgentRuntimeHost).GetField(
            "_instance",
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)!;
        var previousInstance = instanceField.GetValue(null);

        try
        {
            overflowHost = new GameObject(
                    "GameAgentRuntimeLifecycleOverflow")
                .AddComponent<UnityAgentRuntimeHost>();
            overflowRunning = StartBlockingDispatcherWork(
                overflowHost,
                overflowRelease,
                overflowWorkStarted,
                overflowCancellationStarted);
            await overflowWorkStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            for (var index = 0;
                 index < UnityLifecycleCancellationDispatcher.Capacity;
                 index++)
            {
                var release =
                    new ManualResetEventSlim(initialState: false);
                releases.Add(release);
                var host = new GameObject(
                        "GameAgentRuntimeLifecycleCapacity" + index)
                    .AddComponent<UnityAgentRuntimeHost>();
                var workStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var cancellationStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                running.Add(
                    StartBlockingDispatcherWork(
                        host,
                        release,
                        workStarted,
                        cancellationStarted));
                await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                hosts.Add(host);
                shutdowns.Add(
                    host.ShutdownAsync(CancellationToken.None));
                await cancellationStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(2));
            }

            Assert.Equal(
                UnityLifecycleCancellationDispatcher.Capacity,
                UnityLifecycleCancellationDispatcher.ActiveCount);

            instanceField.SetValue(null, overflowHost);
            typeof(UnityAgentRuntimeHost)
                .GetMethod(
                    "OnDestroy",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overflowHost, null);
            Assert.Equal(
                UnityLifecycleCancellationDispatcher.Capacity,
                UnityLifecycleCancellationDispatcher.ActiveCount);
            Assert.Equal(
                1,
                UnityLifecycleCancellationDispatcher.PendingCount);
            Assert.False(
                overflowCancellationStarted.Task.IsCompleted);

            releases[0].Set();
            await overflowCancellationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            overflowRelease.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => overflowRunning.WaitAsync(TimeSpan.FromSeconds(2)));
            await overflowHost
                .ShutdownAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(overflowHost.Dispatcher.IsShutdown);
        }
        finally
        {
            overflowRelease.Set();
            foreach (var release in releases)
            {
                release.Set();
            }

            foreach (var task in running)
            {
                try
                {
                    await task;
                }
                catch
                {
                }
            }

            foreach (var task in shutdowns)
            {
                try
                {
                    await task;
                }
                catch
                {
                }
            }

            foreach (var host in hosts)
            {
                try
                {
                    await host.ShutdownAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                }
            }

            if (overflowRunning is not null)
            {
                try
                {
                    await overflowRunning;
                }
                catch
                {
                }
            }

            if (overflowHost is not null)
            {
                try
                {
                    await overflowHost
                        .ShutdownAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                }
            }

            foreach (var release in releases)
            {
                release.Dispose();
            }

            instanceField.SetValue(null, previousInstance);
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => UnityLifecycleCancellationDispatcher.ActiveCount == 0
                    && UnityLifecycleCancellationDispatcher.PendingCount == 0,
                TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task HostCallerCancellationDoesNotPoisonSharedShutdown()
    {
        var store = new BlockingDurableStore();
        var host = new GameObject("GameAgentRuntimeTest")
            .AddComponent<UnityAgentRuntimeHost>();
        host.Configure(
            new CancellationOnlyProvider(),
            store,
            (_, _) => throw new InvalidOperationException(
                "No action should be dispatched."),
            new FakeRuntimeClock(),
            new SequentialIdGenerator(),
            ownsSessionStore: true);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.ShutdownAsync(canceled.Token));
        await store.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(store.FlushToken.IsCancellationRequested);
        Assert.False(store.IsDisposed);

        store.ReleaseFlush();
        await host.ShutdownAsync(CancellationToken.None);

        Assert.True(store.IsDisposed);
        Assert.True(host.Dispatcher.IsShutdown);
    }

    private static Task<int> StartBlockingDispatcherWork(
        UnityAgentRuntimeHost host,
        ManualResetEventSlim release,
        TaskCompletionSource<bool> workStarted,
        TaskCompletionSource<bool> cancellationStarted)
    {
        var dispatcher = host.Dispatcher;
        using var queued = new ManualResetEventSlim(initialState: false);
        var running = Task.Run(
            async () =>
            {
                var pending = dispatcher.InvokeAsync(
                    async cancellationToken =>
                    {
                        var delay = Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken);
                        using var registration = cancellationToken.Register(
                            () =>
                            {
                                cancellationStarted.TrySetResult(true);
                                release.Wait();
                            });
                        workStarted.TrySetResult(true);
                        await delay;
                        return 1;
                    },
                    CancellationToken.None);
                queued.Set();
                return await pending;
            });
        if (!queued.Wait(TimeSpan.FromSeconds(10))
            || dispatcher.PendingCount != 1)
        {
            dispatcher.Shutdown();
            release.Set();
            try
            {
                _ = running.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            throw new TimeoutException(
                "Timed out while waiting for dispatcher work to be queued.");
        }

        dispatcher.Pump(maxItems: 1, maxMilliseconds: 10);
        return running;
    }

    private static void PumpHost(UnityAgentRuntimeHost host)
    {
        typeof(UnityAgentRuntimeHost)
            .GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host, null);
    }

    private static HeadlessRunRequest CreateRunRequest(
        FakeRuntimeClock clock)
    {
        return new HeadlessRunRequest
        {
            Run = new AgentRun
            {
                RunId = "run-1",
                AgentId = "agent-1",
                WorldId = "world-1",
                SessionId = "session-1",
                State = RunStates.Queued,
                RuntimeGeneration = 1,
                Budget = new AgentBudget
                {
                    MaxTurns = 4,
                    MaxActions = 2,
                    MaxDurationMs = 5000,
                    MaxTokens = 2000,
                    MaxCostUsd = "0.10"
                },
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            },
            Observations = new[]
            {
                new ObservationEnvelope
                {
                    ObservationId = "observation-1",
                    WorldId = "world-1",
                    SessionId = "session-1",
                    Source = "game.world",
                    Kind = "snapshot",
                    ContentType = "application/json",
                    ContentSchemaVersion = "1",
                    Payload = ProtocolJson.ParseElement(
                        """{"hunger":70}"""),
                    ObservedAt = clock.UtcNow,
                    Trust = "authoritative",
                    Visibility = new VisibilityRule
                    {
                        Scope = "agent",
                        AudienceIds = new List<string> { "agent-1" }
                    }
                }
            },
            Tools = new[]
            {
                new ToolDescriptor
                {
                    Name = "gather_food",
                    Version = "1",
                    Description = "Gather a visible food resource.",
                    ParametersSchema = ProtocolJson.ParseElement(
                        """
                        {
                          "type":"object",
                          "required":["resource"],
                          "properties":{"resource":{"type":"string"}}
                        }
                        """),
                    Effect = ToolEffects.WorldCommand,
                    ThreadAffinity = ThreadAffinities.EngineMainThread,
                    TimeoutMs = 1000,
                    RetryPolicy = "idempotent",
                    IdempotencyPolicy = "required"
                }
            }
        };
    }

    private static UnityObservationData FieldObservation(string observationId)
    {
        return new UnityObservationData
        {
            observationId = observationId,
            worldId = "world-1",
            source = "game.state",
            kind = "snapshot",
            payloadJson = "{}",
            hasObservedAtUnixMilliseconds = true,
            observedAtUnixMilliseconds = 0
        };
    }

    private static ActionRequest UnityActionRequest()
    {
        return new ActionRequest
        {
            OperationId = "operation-1",
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "test_action",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private static DurableRunRequest CreateDurableRunRequest(
        FakeRuntimeClock clock)
    {
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "durable-loop-run",
                AgentId = "agent-1",
                WorldId = "world-1",
                SessionId = "session-1",
                State = RunStates.Queued,
                RuntimeGeneration = 1,
                Budget = new AgentBudget
                {
                    MaxTurns = 4,
                    MaxActions = 2,
                    MaxDurationMs = 5000,
                    MaxTokens = 2000,
                    MaxCostUsd = "0.10"
                },
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            },
            Context = new[]
            {
                new ContextCandidate(
                    "world-state",
                    "world_state",
                    ProtocolJson.ParseElement(
                        """{"hunger":70,"visible":["berries"]}"""),
                    required: true,
                    canDefer: false)
            }
        };
    }

    private static GameContextCoordinate MultiActorCoordinate()
    {
        return new GameContextCoordinate(
            "world-1",
            "prime",
            saveRevision: 3,
            stateVersion: "world-v3",
            gameTime: new GameTimePoint(
                "simulation",
                "prime",
                epoch: 1,
                tick: 42),
            sessionId: "session-1");
    }

    private static DurableRunRequest CreateMultiActorRequest(int index)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun
        {
            RunId = "unity-multi-run-" + index,
            AgentId = "npc-" + index,
            WorldId = "world-1",
            SessionId = "session-1",
            DecisionKey = "unity-decision-" + index,
            State = RunStates.Queued,
            RuntimeGeneration = 1,
            Budget = new AgentBudget
            {
                MaxTurns = 4,
                MaxActions = 2,
                MaxDurationMs = 5000,
                MaxTokens = 2000,
                MaxCostUsd = "0.10"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
        GameContextEnvelope.Attach(
            run,
            new GameContextCoordinate(
                "world-1",
                "prime",
                saveRevision: 3,
                observer: new GameEntityIdentity("npc-" + index, 1),
                stateVersion: "world-v3",
                gameTime: new GameTimePoint(
                    "simulation",
                    "prime",
                    epoch: 1,
                    tick: 42)));
        return new DurableRunRequest
        {
            Run = run,
            Context = new[]
            {
                new ContextCandidate(
                    "private-context-" + index,
                    "npc_state",
                    ProtocolJson.ParseElement(
                        """{"goal":"observe","danger":2}"""),
                    required: true,
                    canDefer: false)
            },
            WorkloadClass = ProviderWorkloadClasses.Background
        };
    }

    private static void PumpUntilCompleted(
        UnityMainThreadDispatcher dispatcher,
        Task pending)
    {
        var timeout = Stopwatch.StartNew();
        while (!pending.IsCompleted)
        {
            dispatcher.Pump(maxItems: 64, maxMilliseconds: 10);
            Thread.Yield();
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException(
                    "The Unity host conformance task did not complete.");
            }
        }
    }

    private sealed class CancellationOnlyProvider : IModelProvider
    {
        public async ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class CompletingProvider : IModelProvider
    {
        public ValueTask<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ModelResponse>(
                ModelResponse.Final(
                    ProtocolJson.ParseElement(
                        """{"result":"completed"}""")));
        }
    }

    private sealed class DurableLoopProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "unity-test";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 16_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "call-1",
                    ToolNameDelta = "gather_food",
                    ArgumentsJsonDelta = """{"resource":"berries"}"""
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 0,
                        OutputTokens = 0,
                        CostUsd = "0"
                    }
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "tool_calls"
                };
                yield break;
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = """{"decision":"eat","resource":"berries"}"""
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            };
            await Task.Yield();
        }
    }

    private sealed class ThrowingCancellationBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started => _started;

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException(
                    "Cancellation observer failed."));
            _started.TrySetResult(true);
            await cancellation;
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class SynchronousBlockingThrowBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly ManualResetEventSlim _release;

        public SynchronousBlockingThrowBackend(ManualResetEventSlim release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Started.TrySetResult(true);
            _release.Wait();
            throw new InvalidOperationException(
                "Synchronous backend start failed.");
        }
    }

    private sealed class ThrowingCancellationDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeControlPlane Controls { get; } = new();

        public TaskCompletionSource<bool> Started => _started;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException(
                    "Cancellation observer failed."));
            _started.TrySetResult(true);
            await cancellation;
            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation,
            IGameOperationReconciler reconciler,
            CancellationToken cancellationToken)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }
    }

    private sealed class IgnoringCancellationBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly Task _release;

        public IgnoringCancellationBackend(Task release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Started.TrySetResult(true);
            await _release;
            return new HeadlessRunOutcome();
        }
    }

    private sealed class OwnedIgnoringCancellationBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>,
          IAsyncDisposable
    {
        private readonly Task _release;
        private int _disposed;

        public OwnedIgnoringCancellationBackend(Task release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Started.TrySetResult(true);
            await _release;
            return new HeadlessRunOutcome();
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return default;
        }
    }

    private sealed class BlockingCancellationCallbackBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly ManualResetEventSlim _release;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationCallbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCancellationCallbackBackend(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started => _started;

        public TaskCompletionSource<bool> CancellationCallbackStarted =>
            _cancellationCallbackStarted;

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var cancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    _cancellationCallbackStarted.TrySetResult(true);
                    _release.Wait(TimeSpan.FromSeconds(5));
                });
            _started.TrySetResult(true);
            await cancellation;
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count =>
            throw new InvalidOperationException("snapshot enumeration failed");

        public T this[int index] =>
            throw new InvalidOperationException("snapshot enumeration failed");

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("snapshot enumeration failed");

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IndefinitelyBlockingCancellationCallbackBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>
    {
        private readonly ManualResetEventSlim _release;

        public IndefinitelyBlockingCancellationCallbackBackend(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationCallbackStarted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var delay = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CancellationCallbackStarted.TrySetResult(true);
                    _release.Wait();
                });
            Started.TrySetResult(true);
            await delay;
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailingLifecycleBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>,
          IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("backend dispose failed"));
        }
    }

    private sealed class FailsOnceLifecycleBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>,
          IAsyncDisposable
    {
        public int DisposeAttempts { get; private set; }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return DisposeAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("transient dispose failure"))
                : default;
        }
    }

    private sealed class RecordingLifecycleBackend
        : IUnityAgentRuntimeBackend<HeadlessRunRequest, HeadlessRunOutcome>,
          IAsyncDisposable
    {
        public int DisposeAttempts { get; private set; }

        public ValueTask<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return default;
        }
    }

    private sealed class RecordingDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        public RuntimeControlPlane Controls { get; } = new();

        public DurableRunRequest? LastRequest { get; private set; }

        public string? LastResumeRunId { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastResumeRunId = runId;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = new AgentRun
                    {
                        RunId = runId,
                        State = RunStates.Completed
                    }
                });
        }
    }

    private sealed class ControlledFailingDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        private readonly ConcurrentDictionary<
            string,
            TaskCompletionSource<DurableRunOutcome>> _runs =
            new(StringComparer.Ordinal);

        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = _runs.GetOrAdd(
                request.Run.RunId,
                static _ => new TaskCompletionSource<DurableRunOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            return new ValueTask<DurableRunOutcome>(completion.Task);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Fail(string runId)
        {
            Assert.True(
                _runs.TryGetValue(runId, out var completion),
                "The requested controlled run was not started.");
            completion.TrySetException(
                new InvalidOperationException("controlled failure"));
        }
    }

    private sealed class RecordingRoutedBackend
        : IUnityDurableAgentRuntimeBackend,
          IUnityRoutedExecutionBackend,
          IUnityChildAgentBackend,
          IUnityPersistentChildAgentBackend
    {
        public RuntimeControlPlane Controls { get; } = new();

        public RoutedExecutionRequest? LastRoutedRequest { get; private set; }

        public SimpleCompletionRequest? LastCompletionRequest
        {
            get;
            private set;
        }

        public DurableRunRequest? LastChildRequest { get; private set; }

        public string? LastParentRunId { get; private set; }

        public AgentRun? LastParentRun { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken) =>
            new(new DurableRunOutcome { Run = request.Run });

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
            RoutedExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRoutedRequest = request;
            return new ValueTask<RoutedExecutionOutcome>(
                new RoutedExecutionOutcome
                {
                    Decision = new ExecutionRouteDecision(
                        ExecutionPath.Direct,
                        ExecutionRouteReasonCodes.DirectSufficient,
                        "unity-test-router",
                        "1")
                });
        }

        public ValueTask<SimpleCompletionOutcome> CompleteAsync(
            SimpleCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCompletionRequest = request;
            return new ValueTask<SimpleCompletionOutcome>(
                new SimpleCompletionOutcome
                {
                    OperationId = request.OperationId ?? "generated",
                    ProviderId = "unity-test-provider",
                    Text = "unity-completed",
                    Usage = new ProviderUsage
                    {
                        InputTokens = 1,
                        OutputTokens = 1,
                        CostUsd = "0"
                    },
                    FinishReason = "stop"
                });
        }

        public ValueTask<ChildAgentRunResult> RunChildAsync(
            string parentRunId,
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastParentRunId = parentRunId;
            LastChildRequest = request;
            request.Run.State = RunStates.Completed;
            var lineage = new ChildAgentLineage(
                parentRunId,
                parentRunId,
                request.Run.RunId,
                depth: 1);
            return new ValueTask<ChildAgentRunResult>(
                new ChildAgentRunResult(
                    lineage,
                    new DurableRunOutcome { Run = request.Run }));
        }

        public ValueTask<ChildAgentRunResult> RunChildAsync(
            AgentRun parentRun,
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastParentRun = parentRun;
            LastParentRunId = parentRun.RunId;
            LastChildRequest = request;
            request.Run.State = RunStates.Completed;
            var parentLineage = ChildAgentLineage.Read(parentRun);
            var lineage = new ChildAgentLineage(
                parentLineage?.RootRunId ?? parentRun.RunId,
                parentRun.RunId,
                request.Run.RunId,
                checked((parentLineage?.Depth ?? 0) + 1));
            return new ValueTask<ChildAgentRunResult>(
                new ChildAgentRunResult(
                    lineage,
                    new DurableRunOutcome { Run = request.Run }));
        }

        public int CancelChildren(string parentRunId)
        {
            LastParentRunId = parentRunId;
            return 0;
        }
    }

    private sealed class RecordingGuardedDurableBackend
        : IUnityGuardedDurableAgentRuntimeBackend
    {
        public RuntimeControlPlane Controls { get; } = new();

        public bool SupportsGuardedResume => true;

        public DurableRunResumeGuard? LastGuard { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
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
                new DurableRunResumeGuard
                {
                    ExpectedAgentId = "unguarded"
                });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard guard)
        {
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            LastGuard = guard;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = new AgentRun
                    {
                        RunId = runId,
                        State = RunStates.Completed
                    }
                });
        }
    }

    private sealed class RecordingGuardedMultiActorRuntime
        : IGuardedDurableAgentRuntime
    {
        private readonly object _sync = new();
        private readonly RecordingMultiActorLifecycle _lifecycle;
        private readonly bool _pauseInitialRuns;
        private readonly Dictionary<string, AgentRun> _runs =
            new(StringComparer.Ordinal);
        private int _manifestMisses;

        public RecordingGuardedMultiActorRuntime(
            RecordingMultiActorLifecycle lifecycle,
            bool pauseInitialRuns)
        {
            _lifecycle = lifecycle;
            _pauseInitialRuns = pauseInitialRuns;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public bool AllRunsObservedManifest =>
            Volatile.Read(ref _manifestMisses) == 0;

        public DurableRunResumeGuard? LastResumeGuard { get; private set; }

        public DurableRunContinuation? LastContinuation { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lifecycle.Manifest is null)
            {
                Interlocked.Increment(ref _manifestMisses);
            }

            request.Run.State = _pauseInitialRuns
                ? RunStates.Reconciling
                : RunStates.Completed;
            lock (_sync)
            {
                _runs[request.Run.RunId] = request.Run;
            }

            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Multi-actor recovery must use guarded resume.");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard is null)
            {
                throw new InvalidOperationException(
                    "A multi-actor resume guard is required.");
            }

            AgentRun run;
            lock (_sync)
            {
                run = _runs[runId];
                run.State = continuation?.RequestCancellation == true
                    ? RunStates.Cancelled
                    : RunStates.Completed;
            }

            LastContinuation = continuation;
            LastResumeGuard = guard;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = run });
        }
    }

    private sealed class RecordingMultiActorLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly object _sync = new();
        private readonly List<string> _finishedAgentIds = new();
        private MultiActorBatchManifest? _manifest;
        private string? _abortedBatchId;

        public MultiActorBatchManifest? Manifest
        {
            get
            {
                lock (_sync)
                {
                    return _manifest;
                }
            }
        }

        public IReadOnlyList<string> FinishedAgentIds
        {
            get
            {
                lock (_sync)
                {
                    return _finishedAgentIds.ToArray();
                }
            }
        }

        public string? AbortedBatchId
        {
            get
            {
                lock (_sync)
                {
                    return _abortedBatchId;
                }
            }
        }

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _manifest = manifest;
            }

            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!string.Equals(
                        _manifest?.BatchId,
                        batchId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "ActorFinished preceded the batch manifest.");
                }

                _finishedAgentIds.Add(result.AgentId);
            }

            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = reasonCode;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _abortedBatchId = batchId;
            }

            return default;
        }
    }

    private sealed class BlockingDurableBackend
        : IUnityDurableAgentRuntimeBackend
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeControlPlane Controls { get; } = new();

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new DurableRunOutcome { Run = request.Run };
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class OwnedDurableRuntime
        : IDurableAgentRuntime, IDisposable
    {
        private int _disposed;

        public RuntimeControlPlane Controls { get; } = new();

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class BlockingDurableStore : IDurableSessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly TaskCompletionSource<bool> _flushStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFlush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isDisposed;

        public TaskCompletionSource<bool> FlushStarted => _flushStarted;

        public CancellationToken FlushToken { get; private set; }

        public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            return new JournalAppendResult(
                sequence: 0,
                revision: expectedRunRevision.GetValueOrDefault() + 1,
                wasDuplicate: false);
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = new List<JournalAppendResult>(
                runtimeEvents.Count);
            var revision = expectedRunRevision.GetValueOrDefault();
            foreach (var runtimeEvent in runtimeEvents)
            {
                await _inner.AppendAsync(
                    runtimeEvent,
                    cancellationToken);
                revision++;
                results.Add(
                    new JournalAppendResult(
                        results.Count,
                        revision,
                        false));
            }

            return results;
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public async ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            FlushToken = cancellationToken;
            _flushStarted.TrySetResult(true);
            await _releaseFlush.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _isDisposed, 1);
            return default;
        }

        public void ReleaseFlush()
        {
            _releaseFlush.TrySetResult(true);
        }
    }

    private sealed class TransientLifecycleStore : IDurableSessionStore
    {
        private readonly InMemorySessionStore _inner = new();

        public int FlushAttempts { get; private set; }

        public int DisposeAttempts { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            return new JournalAppendResult(
                sequence: 0,
                revision: expectedRunRevision.GetValueOrDefault() + 1,
                wasDuplicate: false);
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = new List<JournalAppendResult>(
                runtimeEvents.Count);
            var revision = expectedRunRevision.GetValueOrDefault();
            foreach (var runtimeEvent in runtimeEvents)
            {
                await _inner.AppendAsync(
                    runtimeEvent,
                    cancellationToken);
                results.Add(
                    new JournalAppendResult(
                        results.Count,
                        ++revision,
                        false));
            }

            return results;
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushAttempts++;
            return FlushAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("transient flush failure"))
                : default;
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return DisposeAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("transient dispose failure"))
                : default;
        }
    }

    private sealed class FailingLifecycleStore : IDurableSessionStore
    {
        public bool FlushAttempted { get; private set; }

        public bool DisposeAttempted { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            _ = runtimeEvent;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            _ = runtimeEvent;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    0,
                    expectedRunRevision.GetValueOrDefault() + 1,
                    false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = expectedRunRevision.GetValueOrDefault();
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            index,
                            ++revision,
                            false))
                    .ToArray());
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(
                Array.Empty<RuntimeEvent>());
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(runId, 0, 0));
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("store flush failed"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return ValueTask.FromException(
                new InvalidOperationException("store dispose failed"));
        }
    }
}
