using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ProviderTransportContractTests
{
    [Fact]
    public void TypedDialectSemanticsAreBoundIntoRouteIdentity()
    {
        var firstDialect = Dialect("typed-state.v1");
        var secondDialect = new ProviderDialectContract(
            firstDialect.Identifier,
            firstDialect.RequestFamily,
            firstDialect.RequestSchemaVersion,
            firstDialect.StreamFraming,
            firstDialect.StreamFramingVersion,
            firstDialect.ToolCallSemanticsVersion,
            "typed.usage.v2",
            firstDialect.ReasoningSemanticsVersion,
            firstDialect.RequestContentType,
            firstDialect.OpaqueContinuationStateVersion);
        var first = Identity("provider", firstDialect);
        var second = Identity("provider", secondDialect);

        Assert.NotEqual(
            first.DialectSemanticDigest,
            second.DialectSemanticDigest);
        Assert.NotEqual(first.RoutePolicyDigest, second.RoutePolicyDigest);
        Assert.NotEqual(first.RouteDigest, second.RouteDigest);
    }

    [Fact]
    public void TypedDialectEvidenceRoundTripsExactly()
    {
        var dialect = Dialect("typed-state.v1");

        var restored = ProviderDialectContract.Restore(
            dialect.ToJson());

        Assert.Equal(dialect.ContractVersion, restored.ContractVersion);
        Assert.Equal(dialect.Identifier, restored.Identifier);
        Assert.Equal(dialect.RequestFamily, restored.RequestFamily);
        Assert.Equal(
            dialect.RequestSchemaVersion,
            restored.RequestSchemaVersion);
        Assert.Equal(dialect.StreamFraming, restored.StreamFraming);
        Assert.Equal(
            dialect.StreamFramingVersion,
            restored.StreamFramingVersion);
        Assert.Equal(
            dialect.ToolCallSemanticsVersion,
            restored.ToolCallSemanticsVersion);
        Assert.Equal(
            dialect.UsageSemanticsVersion,
            restored.UsageSemanticsVersion);
        Assert.Equal(
            dialect.ReasoningSemanticsVersion,
            restored.ReasoningSemanticsVersion);
        Assert.Equal(
            dialect.RequestContentType,
            restored.RequestContentType);
        Assert.Equal(
            dialect.OpaqueContinuationStateVersion,
            restored.OpaqueContinuationStateVersion);
        Assert.Equal(dialect.SemanticDigest, restored.SemanticDigest);
        Assert.Equal(
            dialect.ToJson().GetRawText(),
            restored.ToJson().GetRawText());
    }

    [Fact]
    public void TypedDialectEvidenceRejectsStructuralAndSemanticTampering()
    {
        var dialect = Dialect("typed-state.v1");
        var source = JsonNode.Parse(dialect.ToJson().GetRawText())!
            .AsObject();
        var cases = new List<JsonElement>();

        var missing = source.DeepClone().AsObject();
        missing.Remove("requestSchemaVersion");
        cases.Add(Json(missing.ToJsonString()));

        var extra = source.DeepClone().AsObject();
        extra["unknown"] = "value";
        cases.Add(Json(extra.ToJsonString()));

        var wrongType = source.DeepClone().AsObject();
        wrongType["requestFamily"] = "custom";
        cases.Add(Json(wrongType.ToJsonString()));

        var invalidEnum = source.DeepClone().AsObject();
        invalidEnum["streamFraming"] = int.MaxValue;
        cases.Add(Json(invalidEnum.ToJsonString()));

        var changedSemantics = source.DeepClone().AsObject();
        changedSemantics["requestContentType"] = "application/cbor";
        cases.Add(Json(changedSemantics.ToJsonString()));

        var changedDigest = source.DeepClone().AsObject();
        changedDigest["semanticDigest"] = new string('b', 64);
        cases.Add(Json(changedDigest.ToJsonString()));

        Assert.All(
            cases,
            evidence => Assert.Throws<InvalidDataException>(
                () => ProviderDialectContract.Restore(evidence)));
    }

    [Fact]
    public void WireEvidenceJournalIntegrityBindsCanonicalEvidenceAndDialect()
    {
        var route = Identity("provider", Dialect("typed-state.v1"));
        var evidence = ProviderWireRequestEvidence.CreateAvailable(
            System.Text.Encoding.UTF8.GetBytes("""{"prompt":"value"}"""),
            route.DialectContract.RequestContentType,
            route);
        var json = evidence.ToJson();
        var integrityDigest = CanonicalJsonDigest.ComputeSha256(json);

        ProviderWireRequestEvidence.ValidateJournalEvidence(
            json,
            route.ProviderId,
            route.RouteDigest,
            route.TransportDialect,
            route.DialectSemanticDigest,
            route.DialectContract.RequestContentType,
            integrityDigest);

        var changed = JsonNode.Parse(json.GetRawText())!.AsObject();
        changed["payloadByteLength"] =
            evidence.PayloadByteLength!.Value + 1;
        var changedJson = Json(changed.ToJsonString());

        var error = Assert.Throws<ProviderException>(
            () => ProviderWireRequestEvidence.ValidateJournalEvidence(
                changedJson,
                route.ProviderId,
                route.RouteDigest,
                route.TransportDialect,
                route.DialectSemanticDigest,
                route.DialectContract.RequestContentType,
                integrityDigest));
        Assert.Equal("provider_wire_evidence_invalid", error.Code);

        var dialectError = Assert.Throws<ProviderException>(
            () => ProviderWireRequestEvidence.ValidateJournalEvidence(
                json,
                route.ProviderId,
                route.RouteDigest,
                route.TransportDialect,
                new string('c', 64),
                route.DialectContract.RequestContentType,
                integrityDigest));
        Assert.Equal(
            "provider_wire_evidence_invalid",
            dialectError.Code);
    }

    [Fact]
    public void WireEvidenceRoundTripsWithoutContentAndTamperingFailsClosed()
    {
        var route = Identity("provider", Dialect("typed-state.v1"));
        var body = System.Text.Encoding.UTF8.GetBytes(
            """{"prompt":"private-content"}""");
        var evidence = ProviderWireRequestEvidence.CreateAvailable(
            body,
            "application/json",
            route);
        var envelope = evidence.ToJson();

        Assert.DoesNotContain(
            "private-content",
            envelope.GetRawText(),
            StringComparison.Ordinal);
        var restored = ProviderWireRequestEvidence.Restore(
            envelope,
            route);
        Assert.Equal(evidence.PayloadSha256, restored.PayloadSha256);
        Assert.Equal(evidence.PayloadByteLength, restored.PayloadByteLength);

        var tampered = Json(
            envelope.GetRawText().Replace(
                ProviderWireRequestEvidence.EvidenceVersion,
                "provider-wire-request-evidence.v2",
                StringComparison.Ordinal));
        var error = Assert.Throws<ProviderException>(
            () => ProviderWireRequestEvidence.Restore(
                tampered,
                route));
        Assert.Equal("provider_wire_evidence_invalid", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void OpaqueStateIsEphemeralByDefault()
    {
        var route = Identity("provider", Dialect("typed-state.v1"));
        var state = ProviderOpaqueContinuationState.Bind(
            route,
            new ProviderOpaqueContinuationUpdate(
                "typed-state.v1",
                Json("""{"cursor":"next"}""")));

        Assert.False(state.IsDurableNonSecret);
        Assert.False(state.TryCreateDurableEnvelope(out _));
        Assert.True(state.Matches(route));
    }

    [Fact]
    public void DurableOpaqueStateRoundTripsAndTamperingFailsClosed()
    {
        var route = Identity("provider", Dialect("typed-state.v1"));
        var state = ProviderOpaqueContinuationState.Bind(
            route,
            new ProviderOpaqueContinuationUpdate(
                "typed-state.v1",
                Json("""{"cursor":"next","page":7}"""),
                ProviderOpaqueStatePersistence.DurableNonSecret));
        Assert.True(state.TryCreateDurableEnvelope(out var envelope));

        var restored = ProviderOpaqueContinuationState.RestoreDurable(
            envelope,
            route.ProviderId,
            route.RouteDigest,
            "typed-state.v1");
        Assert.True(restored.Matches(route));
        Assert.Equal(state.PayloadDigest, restored.PayloadDigest);

        var tampered = Json(
            $$"""
            {
              "envelopeVersion":"{{ProviderOpaqueContinuationState.EnvelopeVersion}}",
              "providerId":"{{route.ProviderId}}",
              "providerRouteDigest":"{{route.RouteDigest}}",
              "stateVersion":"typed-state.v1",
              "persistence":"durable_non_secret",
              "payloadDigest":"{{state.PayloadDigest}}",
              "payload":{"cursor":"tampered","page":7}
            }
            """);
        var digestError =
            Assert.Throws<ProviderOpaqueContinuationStateException>(
                () => ProviderOpaqueContinuationState.RestoreDurable(
                    tampered,
                    route.ProviderId,
                    route.RouteDigest,
                    "typed-state.v1"));
        Assert.Equal(
            "provider_opaque_state_digest_mismatch",
            digestError.Code);

        Assert.Throws<ProviderOpaqueContinuationStateException>(
            () => ProviderOpaqueContinuationState.RestoreDurable(
                envelope,
                "different-provider",
                route.RouteDigest,
                "typed-state.v1"));
        Assert.Throws<ProviderOpaqueContinuationStateException>(
            () => ProviderOpaqueContinuationState.RestoreDurable(
                envelope,
                route.ProviderId,
                new string('a', 64),
                "typed-state.v1"));
        Assert.Throws<ProviderOpaqueContinuationStateException>(
            () => ProviderOpaqueContinuationState.RestoreDurable(
                envelope,
                route.ProviderId,
                route.RouteDigest,
                "typed-state.v2"));
    }

    [Fact]
    public void OpaqueStateCapacityAndUnsupportedVersionFailClosed()
    {
        var route = Identity("provider", Dialect("typed-state.v1"));
        var capacity = Assert.Throws<ProviderOpaqueContinuationStateException>(
            () => new ProviderOpaqueContinuationUpdate(
                "typed-state.v1",
                Json(
                    "\""
                    + new string(
                        'x',
                        ProviderOpaqueContinuationState
                            .MaximumPayloadUtf8Bytes)
                    + "\"")));
        Assert.Equal(
            "provider_opaque_state_capacity_exceeded",
            capacity.Code);

        var version = Assert.Throws<
            ProviderOpaqueContinuationStateException>(
            () => ProviderOpaqueContinuationState.Bind(
                route,
                new ProviderOpaqueContinuationUpdate(
                    "typed-state.v2",
                    Json("""{"cursor":"next"}"""))));
        Assert.Equal(
            "provider_opaque_state_version_unsupported",
            version.Code);
    }

    [Fact]
    public async Task FallbackStripsRouteBoundOpaqueState()
    {
        StreamingModelRequest? firstRequest = null;
        StreamingModelRequest? secondRequest = null;
        var first = new TypedStateProvider(
            "first",
            request =>
            {
                firstRequest = request;
                return FailKnownZero();
            });
        var second = new TypedStateProvider(
            "second",
            request =>
            {
                secondRequest = request;
                return Success(request);
            });
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { first, second },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new StableIds());
        var plan = runner.CaptureRoutePlan(cancellationToken: TestContext.Current.CancellationToken);
        var state = ProviderOpaqueContinuationState.Bind(
            plan.RouteIdentities[0],
            new ProviderOpaqueContinuationUpdate(
                "typed-state.v1",
                Json("""{"cursor":"route-A"}""")));

        var result = await runner.RunAsync(
            "run-1",
            "attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            opaqueContinuationState: state,
            routePlan: plan);

        Assert.NotNull(firstRequest!.OpaqueContinuationState);
        Assert.Null(secondRequest!.OpaqueContinuationState);
        Assert.Equal("second", result.ProviderId);
    }

    [Fact]
    public async Task CompletionUpdateIsBoundToExactDispatchRoute()
    {
        var provider = new TypedStateProvider(
            "stateful",
            request => Success(
                request,
                new ProviderOpaqueContinuationUpdate(
                    "typed-state.v1",
                    Json("""{"cursor":"after"}"""),
                    ProviderOpaqueStatePersistence.DurableNonSecret)));
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new StableIds());
        var plan = runner.CaptureRoutePlan(cancellationToken: TestContext.Current.CancellationToken);

        var result = await runner.RunAsync(
            "run-1",
            "attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            routePlan: plan);

        Assert.NotNull(result.OpaqueContinuationState);
        Assert.True(
            result.OpaqueContinuationState!.Matches(
                result.RouteIdentity!));
        Assert.Equal(
            result.RouteIdentity!.RouteDigest,
            result.OpaqueContinuationState.ProviderRouteDigest);
    }

    [Fact]
    public async Task CapturedRoutePlanPreventsCapabilityDrift()
    {
        var provider = new TypedStateProvider(
            "stable",
            request => Success(request));
        provider.Capabilities.MaxContextTokens = 8_192;
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new StableIds());
        var plan = runner.CaptureRoutePlan(cancellationToken: TestContext.Current.CancellationToken);
        var captured = plan.PrimaryRouteIdentity;
        provider.Capabilities.MaxContextTokens = 1;
        ProviderDispatchNotice? dispatch = null;

        _ = await runner.RunAsync(
            "run-1",
            "attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            estimatedPromptTokens: 10,
            onDispatch: notice =>
            {
                dispatch = notice;
                return default;
            },
            routePlan: plan);

        Assert.Same(captured, dispatch!.RouteIdentity);
        Assert.Equal(
            captured.CapabilityDigest,
            dispatch.RouteIdentity.CapabilityDigest);
    }

    [Fact]
    public async Task TimedOutWirePreparationIsQuarantinedAndDisposed()
    {
        var provider = new HangingPreparedProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                RequestPreparationTimeout =
                    TimeSpan.FromMilliseconds(40)
            },
            new ImmediateDelay(),
            new StableIds());
        Task? detachedCleanup = null;

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onDetachedCleanup: cleanup =>
                        detachedCleanup = cleanup)
                .AsTask());
        Assert.Equal("provider_wire_preparation_timeout", error.Code);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        var quarantined = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-2",
                    "attempt-2",
                    "turn-2",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("provider_cleanup_pending", quarantined.Code);

        provider.Release();
        Assert.NotNull(detachedCleanup);
        await detachedCleanup!.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(provider.Prepared);
        Assert.Equal(1, provider.Prepared!.DisposeCount);
        Assert.All(
            provider.Prepared.Body,
            item => Assert.Equal((byte)0, item));
    }

    [Fact]
    public async Task WirePreparationFailureDoesNotRetainSecretException()
    {
        var secret = string.Concat(
            "prepared-",
            "exception-",
            "canary");
        var provider = new SecretPreparedFailureProvider(secret);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new StableIds());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_request_preparation_failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(
            secret,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PredispatchCleanupTimeoutQuarantinesPreparedBody()
    {
        var provider = new HangingDisposePreparedProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                CleanupTimeout = TimeSpan.FromMilliseconds(40)
            },
            new ImmediateDelay(),
            new StableIds());
        Task? detachedCleanup = null;

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onDetachedCleanup: cleanup =>
                        detachedCleanup = cleanup,
                    onDispatch: _ => throw new InvalidOperationException(
                        "journal callback failed"))
                .AsTask());

        Assert.Equal(
            "provider_prepared_stream_cleanup_timeout",
            error.Code);
        await provider.Prepared.DisposeStarted.WaitAsync(
            TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var quarantined = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-2",
                    "attempt-2",
                    "turn-2",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("provider_cleanup_pending", quarantined.Code);

        provider.Prepared.ReleaseDispose();
        Assert.NotNull(detachedCleanup);
        await detachedCleanup!.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(
            provider.Prepared.Body,
            item => Assert.Equal((byte)0, item));
    }

    private static ProviderDialectContract Dialect(string stateVersion)
    {
        return new ProviderDialectContract(
            "typed.sse.v1",
            ProviderRequestFamily.Custom,
            "typed.request.v1",
            ProviderStreamFraming.ServerSentEvents,
            "typed.sse.v1",
            "typed.tools.v1",
            "typed.usage.v1",
            "typed.reasoning.v1",
            "application/json",
            stateVersion);
    }

    private static ProviderRouteIdentity Identity(
        string providerId,
        ProviderDialectContract dialect)
    {
        return new ProviderRouteIdentity(
            providerId,
            new ProviderRouteMetadata(
                "model",
                dialect,
                "test-route-policy.v1",
                new string('a', 64)),
            new ProviderCapabilities());
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<ModelStreamEvent> FailKnownZero()
    {
        yield return await Task.FromException<ModelStreamEvent>(
            new ProviderException(
                "provider_retry",
                "network",
                "The test route failed before dispatch.",
                true,
                usageKnownToBeZero: true));
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Success(
        StreamingModelRequest request,
        ProviderOpaqueContinuationUpdate? update = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = "ok"
        };
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 1,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage
            {
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = "0"
            }
        };
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 2,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = "stop",
            OpaqueContinuationUpdate = update
        };
    }

    private sealed class TypedStateProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private readonly Func<
            StreamingModelRequest,
            IAsyncEnumerable<ModelStreamEvent>> _script;

        public TypedStateProvider(
            string providerId,
            Func<
                StreamingModelRequest,
                IAsyncEnumerable<ModelStreamEvent>> script)
        {
            ProviderId = providerId;
            RouteMetadata = new ProviderRouteMetadata(
                "model",
                Dialect("typed-state.v1"),
                "test-route-policy.v1",
                new string('a', 64));
            _script = script;
        }

        public string ProviderId { get; }

        public ProviderRouteMetadata RouteMetadata { get; }

        public ProviderCapabilities Capabilities { get; } = new();

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return _script(request);
        }
    }

    private sealed class HangingPreparedProvider :
        IStreamingModelProvider,
        IPreparedStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "hanging-prepared";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new(
                "model",
                Dialect("typed-state.v1"),
                "test-route-policy.v1",
                new string('a', 64));

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Started => _started.Task;

        public TrackingPreparedStream? Prepared { get; private set; }

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public async ValueTask<PreparedProviderStream> PrepareStreamAsync(
            ProviderStreamPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _started.TrySetResult(true);
            await _release.Task;
            var body = new byte[] { 1, 2, 3, 4 };
            Prepared = new TrackingPreparedStream(
                body,
                ProviderWireRequestEvidence.CreateAvailable(
                    body,
                    "application/json",
                    context.RouteIdentity));
            return Prepared;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException(
                "The direct stream must not be used.");
        }
    }

    private sealed class TrackingPreparedStream : PreparedProviderStream
    {
        private int _disposeCount;

        public TrackingPreparedStream(
            byte[] body,
            ProviderWireRequestEvidence evidence)
            : base(evidence)
        {
            Body = body;
        }

        public byte[] Body { get; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public override IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            CancellationToken cancellationToken)
        {
            return Empty(cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                Array.Clear(Body, 0, Body.Length);
            }

            return default;
        }

        private static async IAsyncEnumerable<ModelStreamEvent> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SecretPreparedFailureProvider :
        IStreamingModelProvider,
        IPreparedStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private readonly string _secret;

        public SecretPreparedFailureProvider(string secret)
        {
            _secret = secret;
        }

        public string ProviderId => "secret-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new(
                "model",
                Dialect("typed-state.v1"),
                "test-route-policy.v1",
                new string('a', 64));

        public ProviderCapabilities Capabilities { get; } = new();

        public ValueTask<PreparedProviderStream> PrepareStreamAsync(
            ProviderStreamPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            var failure = new InvalidOperationException(
                "adapter leaked " + _secret);
            failure.Data["secret"] = _secret;
            return ValueTask.FromException<PreparedProviderStream>(
                failure);
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException(
                "The direct stream must not be used.");
        }
    }

    private sealed class HangingDisposePreparedProvider :
        IStreamingModelProvider,
        IPreparedStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        public string ProviderId => "hanging-dispose";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new(
                "model",
                Dialect("typed-state.v1"),
                "test-route-policy.v1",
                new string('a', 64));

        public ProviderCapabilities Capabilities { get; } = new();

        public HangingDisposePreparedStream Prepared { get; private set; } =
            null!;

        public ValueTask<PreparedProviderStream> PrepareStreamAsync(
            ProviderStreamPreparationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = new byte[] { 9, 8, 7 };
            Prepared = new HangingDisposePreparedStream(
                body,
                ProviderWireRequestEvidence.CreateAvailable(
                    body,
                    "application/json",
                    context.RouteIdentity));
            return new ValueTask<PreparedProviderStream>(Prepared);
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException(
                "The direct stream must not be used.");
        }
    }

    private sealed class HangingDisposePreparedStream :
        PreparedProviderStream
    {
        private readonly TaskCompletionSource<bool> _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HangingDisposePreparedStream(
            byte[] body,
            ProviderWireRequestEvidence evidence)
            : base(evidence)
        {
            Body = body;
        }

        public byte[] Body { get; }

        public Task DisposeStarted => _disposeStarted.Task;

        public void ReleaseDispose()
        {
            _releaseDispose.TrySetResult(true);
        }

        public override IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            CancellationToken cancellationToken)
        {
            return Success(
                new StreamingModelRequest
                {
                    StreamAttemptId = "unused"
                },
                cancellationToken: cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            _disposeStarted.TrySetResult(true);
            await _releaseDispose.Task;
            Array.Clear(Body, 0, Body.Length);
        }
    }

    private sealed class ImmediateDelay : IRuntimeDelay
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    private sealed class StableIds : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }
}
