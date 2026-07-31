using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class ProviderAttemptRunnerTests
{
    [Fact]
    public async Task DispatchNoticeIncludesExactProviderRouteIdentity()
    {
        var provider = new TestStreamingProvider(
            "routed-provider",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                Usage(request, 1, 0, 1, "0"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        provider.Capabilities.MaxContextTokens = 32_768;
        var runner = CreateRunner(provider);
        ProviderDispatchNotice? dispatch = null;

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            onDispatch: notice =>
            {
                dispatch = notice;
                return default;
            });

        Assert.NotNull(dispatch);
        Assert.Equal(provider.ProviderId, dispatch!.RouteIdentity.ProviderId);
        Assert.Equal(
            provider.RouteMetadata.ModelId,
            dispatch.RouteIdentity.ModelId);
        Assert.Equal(
            provider.RouteMetadata.TransportDialect,
            dispatch.RouteIdentity.TransportDialect);
        Assert.Equal(
            provider.RouteMetadata.RoutePolicyVersion,
            dispatch.RouteIdentity.RoutePolicyVersion);
        Assert.Equal(
            provider.RouteMetadata.RoutePolicyDigest,
            dispatch.RouteIdentity.RoutePolicyDigest);
        Assert.Equal(64, dispatch.RouteIdentity.CapabilityDigest.Length);
        Assert.Equal(64, dispatch.RouteIdentity.RouteDigest.Length);
        Assert.False(dispatch.RouteIdentity.HasBoundDialectSemantics);
        Assert.False(dispatch.WireRequestEvidence.IsAvailable);
        Assert.Equal(
            "provider_wire_evidence_unavailable",
            dispatch.WireRequestEvidence.UnavailableReason);
        Assert.Null(dispatch.WireRequestEvidence.PayloadSha256);
        Assert.Null(dispatch.WireRequestEvidence.PayloadByteLength);
        Assert.Same(dispatch.RouteIdentity, result.RouteIdentity);
    }

    [Fact]
    public async Task CommonGateRejectsToolSchemasForProviderWithoutAdapter()
    {
        var dispatches = 0;
        var provider = new TestStreamingProvider(
            "bounded-schema",
            request =>
            {
                Interlocked.Increment(ref dispatches);
                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "unexpected"
                    },
                    Usage(request, 1, 1, 1, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        provider.Capabilities.MaxToolSchemaUtf8Bytes = 1;

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CreateRunner(provider).RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    new[] { CreateTool() },
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_tool_schema_bytes_exceeded", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public async Task PreparationFailureFallsBackBeforeProviderDispatch()
    {
        var failing = new FailingPreparationProvider();
        var healthy = new TestStreamingProvider(
            "healthy",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "recovered"
                },
                Usage(request, 1, 1, 1, "0"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var notices = new List<ProviderAttemptNotice>();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { failing, healthy },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(1),
                TotalTimeout = TimeSpan.FromSeconds(2)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);

        Assert.Equal("healthy", result.ProviderId);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(0, failing.StreamCalls);
        var fallback = Assert.Single(notices);
        Assert.Equal(
            "provider_request_preparation_failed",
            fallback.ErrorCode);
    }

    [Fact]
    public async Task StalePreparationEvidenceIsRejectedBeforeDispatch()
    {
        var provider = new StalePreparationProvider();
        var runner = CreateRunner(provider);
        var messages = new[]
        {
            new NormalizedMessage
            {
                MessageId = "user",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("original")
                }
            }
        };

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    messages,
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            "provider_request_adapter_evidence_invalid",
            error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Equal(0, provider.StreamCalls);
    }

    [Fact]
    public async Task NonCooperativeRequestPreparationIsBoundedAndQuarantined()
    {
        var provider = new BlockingPreparationProvider();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(1),
                TotalTimeout = TimeSpan.FromSeconds(2),
                RequestPreparationTimeout = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onDetachedCleanup: detached.Add)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("provider_request_preparation_timeout", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Equal(0, provider.StreamCalls);
        var cleanup = Assert.Single(detached);
        Assert.False(cleanup.IsCompleted);

        var quarantined = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-2",
                    "run-attempt-2",
                    "turn-2",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("provider_cleanup_pending", quarantined.Code);

        provider.Release();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LatePreparedStreamCleanupFailureKeepsProviderQuarantined()
    {
        var provider = new LateFaultingPreparedStreamProvider();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                RequestPreparationTimeout = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();

        try
        {
            var timeout = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-prepared-timeout",
                        "run-attempt-prepared-timeout",
                        "turn-prepared-timeout",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None,
                        onDetachedCleanup: detached.Add)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("provider_wire_preparation_timeout", timeout.Code);
            await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var cleanup = Assert.Single(detached);
            Assert.False(cleanup.IsCompleted);

            provider.Release();
            var cleanupFailure =
                await Assert.ThrowsAsync<ProviderException>(
                    () => cleanup.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                "provider_prepared_stream_cleanup_failed",
                cleanupFailure.Code);
            Assert.Equal(1, provider.DisposeCount);

            var quarantined = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-after-prepared-cleanup-failure",
                        "run-attempt-after-prepared-cleanup-failure",
                        "turn-after-prepared-cleanup-failure",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal("provider_cleanup_pending", quarantined.Code);
            Assert.Equal(1, provider.PrepareCalls);
        }
        finally
        {
            provider.Release();
            foreach (var cleanup in detached)
            {
                try
                {
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The asserted cleanup failure is expected.
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronousStreamStartupCannotWedgeRunner(
        bool blockStreamFactory)
    {
        var provider = new BlockingStreamStartProvider(blockStreamFactory);
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(1),
                TotalTimeout = TimeSpan.FromSeconds(2),
                StreamStartTimeout = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();

        try
        {
            var error = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-start",
                        "run-attempt-start",
                        "turn-start",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None,
                        onDetachedCleanup: detached.Add)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("provider_stream_start_timeout", error.Code);
            await provider.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(Assert.Single(detached).IsCompleted);
        }
        finally
        {
            provider.Release();
        }

        await Assert.Single(detached).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LateStreamStartCleanupFailureKeepsProviderQuarantined()
    {
        var provider = new LateFaultingStreamStartProvider();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                StreamStartTimeout = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();

        try
        {
            var timeout = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-start-timeout",
                        "run-attempt-start-timeout",
                        "turn-start-timeout",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None,
                        onDetachedCleanup: detached.Add)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("provider_stream_start_timeout", timeout.Code);
            await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var cleanup = Assert.Single(detached);
            Assert.False(cleanup.IsCompleted);

            provider.Release();
            var cleanupFailure =
                await Assert.ThrowsAsync<ProviderException>(
                    () => cleanup.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("provider_cleanup_failed", cleanupFailure.Code);
            Assert.Equal(1, provider.DisposeCount);

            var quarantined = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-after-start-cleanup-failure",
                        "run-attempt-after-start-cleanup-failure",
                        "turn-after-start-cleanup-failure",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal("provider_cleanup_pending", quarantined.Code);
            Assert.Equal(1, provider.StreamCalls);
        }
        finally
        {
            provider.Release();
            foreach (var cleanup in detached)
            {
                try
                {
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The asserted cleanup failure is expected.
                }
            }
        }
    }

    [Fact]
    public async Task CallerCancellationCleanupFailureKeepsProviderQuarantined()
    {
        var provider = new CooperativeFaultingCleanupProvider();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(5),
                TotalTimeout = TimeSpan.FromSeconds(10),
                CleanupTimeout = TimeSpan.FromSeconds(1)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync(
                "run-caller-cancelled",
                "run-attempt-caller-cancelled",
                "turn-caller-cancelled",
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                cancellation.Token)
            .AsTask();

        await provider.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var cleanupFailure = await Assert.ThrowsAsync<ProviderException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("provider_cleanup_failed", cleanupFailure.Code);
        Assert.Equal(1, provider.DisposeCount);

        var quarantined = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-after-cancel-cleanup-failure",
                    "run-attempt-after-cancel-cleanup-failure",
                    "turn-after-cancel-cleanup-failure",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_cleanup_pending", quarantined.Code);
        Assert.Equal(1, provider.StreamCalls);
    }

    [Fact]
    public async Task ProviderWithoutAdapterCannotReceiveReasoning()
    {
        var provider = new CapturingReasoningDisabledProvider();
        var runner = CreateRunner(provider);
        var message = new NormalizedMessage
        {
            MessageId = "assistant-private",
            Role = NormalizedRoles.Assistant,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromReasoning("private-chain"),
                NormalizedContentPart.FromText("visible")
            }
        };

        _ = await runner.RunAsync(
            "run-reasoning",
            "run-attempt-reasoning",
            "turn-reasoning",
            new[] { message },
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.DoesNotContain(
            Assert.Single(provider.Requests).Messages
                .SelectMany(item => item.Parts),
            part => part.Type == NormalizedPartTypes.Reasoning);
    }

    [Fact]
    public async Task OversizedAdapterOutputFailsBeforeDeepSnapshot()
    {
        var provider = new OversizedPreparationProvider();
        var runner = CreateRunner(provider);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-oversized-adapter",
                    "run-attempt-oversized-adapter",
                    "turn-oversized-adapter",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_request_adapter_output_limit", error.Code);
        Assert.Equal(0, provider.StreamCalls);
    }

    [Fact]
    public async Task AdapterOutputCountIsReadOnceBeforeDispatch()
    {
        var provider = new ChangingCountPreparationProvider();
        var runner = CreateRunner(provider);
        var messages = new[]
        {
            new NormalizedMessage
            {
                MessageId = "changing-count-input",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("hello")
                }
            }
        };

        var result = await runner.RunAsync(
            "run-changing-count",
            "run-attempt-changing-count",
            "turn-changing-count",
            messages,
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(1, provider.StreamCalls);
        Assert.Equal(1, provider.CountReads);
    }

    [Fact]
    public async Task AdapterOutputEnumeratorIsNeverTrusted()
    {
        var provider = new DeceptiveToolEnumerationProvider();
        var runner = CreateRunner(provider);

        var result = await runner.RunAsync(
            "run-deceptive-enumerator",
            "run-attempt-deceptive-enumerator",
            "turn-deceptive-enumerator",
            Array.Empty<NormalizedMessage>(),
            new[] { CreateTool() },
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(1, provider.StreamCalls);
        Assert.Equal(0, provider.EnumeratorReads);
    }

    [Fact]
    public async Task AdapterValidationSharesThePreparationDeadline()
    {
        var provider = new BlockingValidationProvider();
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(1),
                TotalTimeout = TimeSpan.FromSeconds(2),
                RequestPreparationTimeout = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-blocked-validation",
                    "run-attempt-blocked-validation",
                    "turn-blocked-validation",
                    new[]
                    {
                        new NormalizedMessage
                        {
                            MessageId = "blocked-validation-input",
                            Role = NormalizedRoles.User,
                            CreatedAt = DateTimeOffset.UnixEpoch,
                            Parts = new List<NormalizedContentPart>
                            {
                                NormalizedContentPart.FromText("hello")
                            }
                        }
                    },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onDetachedCleanup: detached.Add)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("provider_request_preparation_timeout", error.Code);
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, provider.StreamCalls);
        var cleanup = Assert.Single(detached);
        Assert.False(cleanup.IsCompleted);

        provider.Release();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AdapterFailureBeforeDispatchIsAlwaysKnownZero()
    {
        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CreateRunner(new UnknownUsagePreparationProvider())
                .RunAsync(
                    "run-adapter-known-zero",
                    "run-attempt-adapter-known-zero",
                    "turn-adapter-known-zero",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("adapter_declined", error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task PublicPreparationReportSupportsCustomTransforms()
    {
        var provider = new TransformingPreparationProvider();

        _ = await CreateRunner(provider).RunAsync(
            "run-custom-adapter",
            "run-attempt-custom-adapter",
            "turn-custom-adapter",
            new[]
            {
                new NormalizedMessage
                {
                    MessageId = "custom-adapter-input",
                    Role = NormalizedRoles.User,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("original")
                    }
                }
            },
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(
            "adapted",
            Assert.Single(Assert.Single(request.Messages).Parts).Text);
    }

    [Fact]
    public async Task OversizedInitialRequestFailsBeforeSnapshotOrDispatch()
    {
        var streamCalls = 0;
        var provider = new TestStreamingProvider(
            "bounded-input",
            request =>
            {
                Interlocked.Increment(ref streamCalls);
                return Events(Usage(request, 0, 0, 0, "0"));
            });
        var message = new NormalizedMessage
        {
            MessageId = "oversized-input",
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText(
                    new string('\u0001', 2 * 1_048_576))
            }
        };

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CreateRunner(provider).RunAsync(
                    "run-oversized-input",
                    "run-attempt-oversized-input",
                    "turn-oversized-input",
                    new[] { message },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Equal(0, streamCalls);
    }

    [Fact]
    public async Task InitialRequestCountIsBoundedBeforeSnapshot()
    {
        var streamCalls = 0;
        var provider = new TestStreamingProvider(
            "bounded-count",
            request =>
            {
                Interlocked.Increment(ref streamCalls);
                return Events(Usage(request, 0, 0, 0, "0"));
            });
        var messages = Enumerable.Range(0, 4_097)
            .Select(
                index => new NormalizedMessage
                {
                    MessageId = "message-" + index,
                    Role = NormalizedRoles.User,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("x")
                    }
                })
            .ToArray();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CreateRunner(provider).RunAsync(
                    "run-oversized-count",
                    "run-attempt-oversized-count",
                    "turn-oversized-count",
                    messages,
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.Equal(0, streamCalls);
    }

    [Fact]
    public async Task CancellationWinsBeforeProviderInputEnumeration()
    {
        var provider = new TestStreamingProvider(
            "cancelled-input",
            request => Events(Usage(request, 0, 0, 0, "0")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateRunner(provider).RunAsync(
                    "run-cancelled-input",
                    "run-attempt-cancelled-input",
                    "turn-cancelled-input",
                    new ThrowingReadOnlyList<NormalizedMessage>(),
                    new ThrowingReadOnlyList<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    cancellation.Token)
                .AsTask());
    }

    [Fact]
    public void ConstructorRejectsUnsafeRetryPoliciesAndDuplicateProviders()
    {
        var provider = new TestStreamingProvider(
            "provider-a",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = ZeroUsage()
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProviderAttemptRunner(
                new[] { provider },
                new ProviderRetryPolicy { MaxAttemptsPerProvider = 0 },
                new ImmediateDelay(),
                new SequentialIdGenerator()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProviderAttemptRunner(
                new[] { provider },
                new ProviderRetryPolicy
                {
                    IdleTimeout = TimeSpan.FromSeconds(2),
                    TotalTimeout = TimeSpan.FromSeconds(1)
                },
                new ImmediateDelay(),
                new SequentialIdGenerator()));
        Assert.Throws<ArgumentException>(
            () => new ProviderAttemptRunner(
                new[] { provider, provider },
                new ProviderRetryPolicy(),
                new ImmediateDelay(),
                new SequentialIdGenerator()));
    }

    [Fact]
    public void ConstructorSnapshotsProviderListsByIndexExactlyOnce()
    {
        var provider = new TestStreamingProvider(
            "stable-provider",
            SuccessfulEvents);
        var indexed = new DeceptiveEnumeratorReadOnlyList<
            IStreamingModelProvider>(new[] { provider });
        var runner = new ProviderAttemptRunner(
            indexed,
            new ProviderRetryPolicy(),
            new ImmediateDelay(),
            new SequentialIdGenerator());

        Assert.Equal("stable-provider", runner.PrimaryProviderId);
        Assert.Equal(0, indexed.EnumeratorReads);

        var changing = new ChangingCountReadOnlyList<
            IStreamingModelProvider>(new[] { provider });
        var changingRunner = new ProviderAttemptRunner(
            changing,
            new ProviderRetryPolicy(),
            new ImmediateDelay(),
            new SequentialIdGenerator());

        Assert.Equal(
            "stable-provider",
            changingRunner.PrimaryProviderId);
        Assert.Equal(1, changing.CountReads);
    }

    [Fact]
    public async Task RequestPreflightAndCloneUseOneIndexedInputSnapshot()
    {
        string? observedText = null;
        var provider = new TestStreamingProvider(
            "snapshot-provider",
            request =>
            {
                observedText = request.Messages[0].Parts[0].Text;
                return SuccessfulEvents(request);
            });
        var runner = CreateRunner(provider);
        var safe = new NormalizedMessage
        {
            MessageId = "safe-message",
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("safe")
            }
        };
        var oversized = new NormalizedMessage
        {
            MessageId = "oversized-message",
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText(
                    new string('x', 2 * 1_048_576))
            }
        };
        var messages =
            new ChangingItemReadOnlyList<NormalizedMessage>(
                safe,
                oversized);
        var tools = new ChangingCountReadOnlyList<ToolDescriptor>(
            Array.Empty<ToolDescriptor>());

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            messages,
            tools,
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.Equal("safe", observedText);
        Assert.Equal("ok", result.Text);
        Assert.Equal(1, messages.IndexReads);
        Assert.Equal(1, messages.CountReads);
        Assert.Equal(1, tools.CountReads);
    }

    [Fact]
    public async Task AssemblesFragmentedToolCallAndUsage()
    {
        var provider = new TestStreamingProvider(
            "primary",
            request => Events(
                Event(request, 0, ModelStreamEventKinds.ToolCallDelta, "call-1", "gather_", """{"resource":"""),
                Event(request, 1, ModelStreamEventKinds.ToolCallDelta, "call-1", "food", "\"berries\"}"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 12,
                        OutputTokens = 7,
                        CostUsd = "0.002"
                    }
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 3,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "tool_calls"
                }));
        var runner = CreateRunner(provider);
        var observed = new List<ModelStreamEvent>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            new[] { CreateTool() },
            new AttemptFence(),
            item =>
            {
                observed.Add(item);
                return default;
            },
            CancellationToken.None);

        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("gather_food", toolCall.Name);
        Assert.Equal("berries", toolCall.Arguments.GetProperty("resource").GetString());
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(4, observed.Count);
    }

    [Fact]
    public async Task RetryUsesANewFenceAndDiscardsMismatchedStream()
    {
        var calls = 0;
        string? abandonedProviderAttemptId = null;
        string? abandonedStreamAttemptId = null;
        var provider = new TestStreamingProvider(
            "primary",
            request =>
            {
                calls++;
                if (calls == 1)
                {
                    abandonedProviderAttemptId =
                        request.ProviderAttemptId;
                    abandonedStreamAttemptId =
                        request.StreamAttemptId;
                    return FailingEvents(request);
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = "stale-stream",
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "stale"
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "fresh"
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Usage,
                        Usage = ZeroUsage()
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 3,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var runner = CreateRunner(provider);
        var notices = new List<ProviderAttemptNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);

        Assert.Equal(2, calls);
        Assert.Equal("fresh", result.Text);
        var retry = Assert.Single(notices);
        Assert.Equal(ProviderAttemptNoticeKinds.Retry, retry.Kind);
        Assert.Equal("transient", retry.ErrorCode);
        Assert.Equal(
            abandonedProviderAttemptId,
            retry.ProviderAttemptId);
        Assert.Equal(
            abandonedStreamAttemptId,
            retry.StreamAttemptId);
    }

    [Fact]
    public async Task FallbackEmitsStructuredLifecycleNotice()
    {
        StreamingModelRequest? abandonedRequest = null;
        var first = new TestStreamingProvider(
            "first",
            request =>
            {
                abandonedRequest = request;
                return FailingEvents(request);
            });
        var second = new TestStreamingProvider(
            "second",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = ZeroUsage()
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = new ProviderAttemptRunner(
            new[] { first, second },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var notices = new List<ProviderAttemptNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);

        Assert.Equal("second", result.ProviderId);
        var fallback = Assert.Single(notices);
        Assert.Equal(ProviderAttemptNoticeKinds.Fallback, fallback.Kind);
        Assert.Equal("first", fallback.ProviderId);
        Assert.Equal("second", fallback.NextProviderId);
        Assert.NotNull(abandonedRequest);
        Assert.Equal(
            abandonedRequest.ProviderAttemptId,
            fallback.ProviderAttemptId);
        Assert.Equal(
            abandonedRequest.StreamAttemptId,
            fallback.StreamAttemptId);
    }

    [Fact]
    public async Task RetryPresentationNeverConcatenatesAbandonedPartialText()
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "primary",
            request =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return PartialFailingEvents(request, "attempt-A");
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "answer-B"
                    },
                    Usage(request, 1, 0, 0, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var coordinator =
            new AttemptSafeStreamingPresentationCoordinator(
                new AttemptSafeStreamingPresentationOptions
                {
                    Stream = new StreamingPresentationOptions
                    {
                        TargetChunkUtf8Bytes = 1,
                        MaximumBufferedUtf8Bytes = 32
                    }
                });
        var identities =
            new Dictionary<
                string,
                StreamingPresentationAttemptIdentity>(
                StringComparer.Ordinal);
        var presentation =
            new List<AttemptStreamingPresentationChunk>();
        var view = new System.Text.StringBuilder();
        var runner = CreateRunner(provider);

        var result = await runner.RunAsync(
            "run-presentation",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            item =>
            {
                if (string.Equals(
                        item.Kind,
                        ModelStreamEventKinds.TextDelta,
                        StringComparison.Ordinal)
                    && identities.TryGetValue(
                        item.StreamAttemptId,
                        out var identity))
                {
                    ApplyPresentation(
                        view,
                        presentation,
                        coordinator.Push(
                            identity,
                            item.TextDelta ?? string.Empty,
                            DateTimeOffset.UnixEpoch));
                }

                return default;
            },
            CancellationToken.None,
            notice => ApplyPresentation(
                view,
                presentation,
                coordinator.ApplyLifecycle(
                    "run-presentation",
                    "turn-1",
                    notice)),
            onDispatch: notice =>
            {
                var identity =
                    new StreamingPresentationAttemptIdentity(
                        "run-presentation",
                        "turn-1",
                        notice.ProviderId,
                        notice.ProviderAttemptId,
                        notice.StreamAttemptId);
                identities.Add(notice.StreamAttemptId, identity);
                ApplyPresentation(
                    view,
                    presentation,
                    coordinator.BeginAttempt(identity));
                return default;
            });
        var finalIdentity = identities[result.StreamAttemptId];
        ApplyPresentation(
            view,
            presentation,
            coordinator.Complete(
                finalIdentity,
                result.Text ?? string.Empty));

        Assert.Equal("answer-B", view.ToString());
        Assert.Contains(
            presentation,
            chunk => chunk.Identity.StreamAttemptId
                     != result.StreamAttemptId
                     && chunk.Kind
                     == AttemptStreamingPresentationChunkKinds.Delta);
        Assert.Contains(
            presentation,
            chunk => chunk.Kind
                     == AttemptStreamingPresentationChunkKinds.Superseded);
        Assert.DoesNotContain("attempt-A", view.ToString());
    }

    [Fact]
    public async Task ContextLimitSkipsProviderAndCapsFallbackOutput()
    {
        var first = new TestStreamingProvider(
            "too-small",
            _ => throw new InvalidOperationException(
                "A provider with insufficient context must not be called."));
        first.Capabilities.MaxContextTokens = 10;
        StreamingModelRequest? observed = null;
        var second = new TestStreamingProvider(
            "fits",
            request =>
            {
                observed = request;
                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "ok"
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.Usage,
                        Usage = ZeroUsage()
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        second.Capabilities.MaxContextTokens = 40;
        var runner = new ProviderAttemptRunner(
            new[] { first, second },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var notices = new List<ProviderAttemptNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add,
            estimatedPromptTokens: 20,
            maxOutputTokens: 50);

        Assert.Equal("fits", result.ProviderId);
        Assert.NotNull(observed);
        Assert.Equal(20, observed!.MaxOutputTokens);
        var fallback = Assert.Single(notices);
        Assert.Equal(
            "provider_context_limit_exceeded",
            fallback.ErrorCode);
    }

    [Fact]
    public async Task ProviderOwnedCjkEstimateControlsEachRouteContextGate()
    {
        var messages = new[]
        {
            new NormalizedMessage
            {
                MessageId = "cjk-user",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText(
                        "\u6c5f\u6e56\u4eba\u7269\u6b63\u5728\u5171\u540c\u884c\u52a8")
                }
            }
        };
        var cjkEstimator = new CalibratingProviderTokenEstimator();
        var cjkTokens = cjkEstimator.EstimatePromptTokens(
            messages,
            Array.Empty<ToolDescriptor>());
        Assert.True(cjkTokens > 1);
        var primary = new EstimatingStreamingProvider(
            "cjk-primary",
            cjkEstimator,
            _ => throw new InvalidOperationException(
                "The route-specific CJK estimate must reject this route."));
        primary.Capabilities.MaxContextTokens = cjkTokens;
        StreamingModelRequest? fallbackRequest = null;
        var fallback = new EstimatingStreamingProvider(
            "compact-fallback",
            new FixedProviderPromptEstimator("compact", "1", 2),
            request =>
            {
                fallbackRequest = request;
                return SuccessfulEvents(request);
            });
        fallback.Capabilities.MaxContextTokens = 8;
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { primary, fallback },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());

        var result = await runner.RunAsync(
            "cjk-context",
            "cjk-context-attempt",
            "cjk-context-turn",
            messages,
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            estimatedPromptTokens: 1,
            maxOutputTokens: 20);

        Assert.Equal("compact-fallback", result.ProviderId);
        Assert.Equal(0, primary.StreamCalls);
        Assert.NotNull(fallbackRequest);
        Assert.Equal(6, fallbackRequest!.MaxOutputTokens);
    }

    [Fact]
    public async Task CompletedUsageCalibratesLaterRouteContextGate()
    {
        var runtimeEstimator = new FixedRuntimeTokenEstimator(5);
        var calibrating =
            new CalibratingProviderTokenEstimator(runtimeEstimator);
        var primaryCalls = 0;
        var primary = new EstimatingStreamingProvider(
            "calibrating-primary",
            calibrating,
            request =>
            {
                Interlocked.Increment(ref primaryCalls);
                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "ok"
                    },
                    Usage(request, 1, 20, 1, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        primary.Capabilities.MaxContextTokens = 100;
        var fallbackCalls = 0;
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        fallback.Capabilities.MaxContextTokens = 100;
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { primary, fallback },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());
        var messages = new[]
        {
            new NormalizedMessage
            {
                MessageId = "user",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("calibrate")
                }
            }
        };

        Assert.Equal(
            "calibrating-primary",
            (await runner.RunAsync(
                "calibration-first",
                "calibration-first-attempt",
                "calibration-first-turn",
                messages,
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None,
                estimatedPromptTokens: 1,
                maxOutputTokens: 10)).ProviderId);
        Assert.Equal(4.0, calibrating.CurrentMultiplier);

        primary.Capabilities.MaxContextTokens = 15;
        Assert.Equal(
            "fallback",
            (await runner.RunAsync(
                "calibration-second",
                "calibration-second-attempt",
                "calibration-second-turn",
                messages,
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None,
                estimatedPromptTokens: 1,
                maxOutputTokens: 10)).ProviderId);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public async Task InvalidProviderTokenEstimateSkipsRouteBeforeDispatch()
    {
        var primary = new EstimatingStreamingProvider(
            "invalid-estimator",
            new FixedProviderPromptEstimator("invalid", "1", 0),
            _ => throw new InvalidOperationException(
                "An invalid estimate must fail before dispatch."));
        var fallback = new TestStreamingProvider(
            "fallback",
            SuccessfulEvents);
        var runner = new ProviderAttemptRunner(
            new IStreamingModelProvider[] { primary, fallback },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());
        var notices = new List<ProviderAttemptNotice>();

        var result = await runner.RunAsync(
            "invalid-estimator-run",
            "invalid-estimator-attempt",
            "invalid-estimator-turn",
            new[]
            {
                new NormalizedMessage
                {
                    MessageId = "user",
                    Role = NormalizedRoles.User,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("hello")
                    }
                }
            },
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);

        Assert.Equal("fallback", result.ProviderId);
        Assert.Equal(0, primary.StreamCalls);
        Assert.Equal(
            "provider_token_estimate_invalid",
            Assert.Single(notices).ErrorCode);
    }

    [Fact]
    public async Task CompletedAttemptWithoutUsageIsRejected()
    {
        var provider = new TestStreamingProvider(
            "missing-usage",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = CreateRunner(provider);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => Run(runner));

        Assert.Equal("provider_usage_missing", error.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalMarkersMayArriveInEitherOrder(bool usageFirst)
    {
        var provider = new TestStreamingProvider(
            "terminal-order",
            request =>
            {
                var text = new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                };
                var usage = Usage(request, usageFirst ? 1 : 2, 3, 2, "0.001");
                var completed = new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = usageFirst ? 2 : 1,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                };
                return usageFirst
                    ? Events(text, usage, completed)
                    : Events(text, completed, usage);
            });
        var runner = CreateRunner(provider);

        var result = await Run(runner);

        Assert.Equal("ok", result.Text);
        Assert.Equal(3, result.Usage.InputTokens);
        Assert.Equal(2, result.Usage.OutputTokens);
        Assert.Equal("0.001", result.Usage.CostUsd);
    }

    [Theory]
    [InlineData(true, ModelStreamEventKinds.TextDelta)]
    [InlineData(true, ModelStreamEventKinds.ReasoningDelta)]
    [InlineData(true, ModelStreamEventKinds.ToolCallDelta)]
    [InlineData(false, ModelStreamEventKinds.TextDelta)]
    [InlineData(false, ModelStreamEventKinds.ReasoningDelta)]
    [InlineData(false, ModelStreamEventKinds.ToolCallDelta)]
    public async Task ContentAfterEitherTerminalMarkerIsRejected(
        bool usageFirst,
        string contentKind)
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "post-terminal-content",
            request =>
            {
                Interlocked.Increment(ref calls);
                var terminal = usageFirst
                    ? Usage(request, 0, 1, 1, "0.001")
                    : new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    };
                return Events(
                    terminal,
                    ContentEvent(request, 1, contentKind));
            });
        var runner = CreateRunner(provider);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => Run(runner));

        Assert.Equal("provider_content_after_terminal_marker", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DuplicateUsageIsRejected()
    {
        var provider = new TestStreamingProvider(
            "duplicate-usage",
            request => Events(
                Usage(request, 0, 1, 2, "0.001"),
                Usage(request, 1, 3, 4, "0.002"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = CreateRunner(provider);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => Run(runner));

        Assert.Equal("provider_usage_duplicate", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task InvalidUsageIsRejected()
    {
        var provider = new TestStreamingProvider(
            "invalid-usage",
            request => Events(
                Usage(request, 0, -1, 0, "not-a-cost"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = CreateRunner(provider);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => Run(runner));

        Assert.Equal("provider_usage_invalid", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task FallbackAggregatesUsageFromEveryAttempt()
    {
        var first = new TestStreamingProvider(
            "first",
            request => FailingEvents(
                request,
                inputTokens: 3,
                outputTokens: 2,
                costUsd: "0.001"));
        var second = new TestStreamingProvider(
            "second",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                Usage(request, 1, 5, 4, "0.002"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = new ProviderAttemptRunner(
            new[] { first, second },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var usageNotices = new List<ProviderUsageNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            onUsage: notice =>
            {
                usageNotices.Add(notice);
                return default;
            });

        Assert.Equal("second", result.ProviderId);
        Assert.Equal(8, result.Usage.InputTokens);
        Assert.Equal(6, result.Usage.OutputTokens);
        Assert.Equal("0.003", result.Usage.CostUsd);
        Assert.Equal(2, usageNotices.Count);
        Assert.Equal(
            new[] { "first", "second" },
            usageNotices.Select(notice => notice.ProviderId));
    }

    [Fact]
    public async Task EmptyResponseRetriesAndAggregatesUsage()
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "empty-then-recovered",
            request =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return Events(
                        Usage(
                            request,
                            0,
                            3,
                            2,
                            "0.001",
                            cacheReadTokens: 1,
                            cacheWriteTokens: 0,
                            cacheMissTokens: 2,
                            reasoningTokens: 1,
                            providerTotalTokens: 5),
                        new ModelStreamEvent
                        {
                            StreamAttemptId = request.StreamAttemptId,
                            Ordinal = 1,
                            Kind = ModelStreamEventKinds.Completed,
                            FinishReason = "stop"
                        });
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "recovered"
                    },
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    },
                    Usage(
                        request,
                        2,
                        5,
                        4,
                        "0.002",
                        cacheReadTokens: 2,
                        cacheWriteTokens: 0,
                        cacheMissTokens: 3,
                        reasoningTokens: 2,
                        providerTotalTokens: 9));
            });
        var runner = CreateRunner(provider);
        var lifecycle = new List<ProviderAttemptNotice>();
        var usageNotices = new List<ProviderUsageNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            lifecycle.Add,
            onUsage: notice =>
            {
                usageNotices.Add(notice);
                return default;
            });

        Assert.Equal(2, calls);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(8, result.Usage.InputTokens);
        Assert.Equal(6, result.Usage.OutputTokens);
        Assert.Equal("0.003", result.Usage.CostUsd);
        Assert.Equal(2, result.Usage.Samples);
        Assert.Equal(3, result.Usage.CacheReadTokens);
        Assert.Equal(0, result.Usage.CacheWriteTokens);
        Assert.Equal(5, result.Usage.CacheMissTokens);
        Assert.Equal(3, result.Usage.ReasoningTokens);
        Assert.Equal(14, result.Usage.ProviderTotalTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            result.Usage.Availability);
        Assert.Equal(2, usageNotices.Count);
        var retry = Assert.Single(lifecycle);
        Assert.Equal(ProviderAttemptNoticeKinds.Retry, retry.Kind);
        Assert.Equal("provider_empty_response", retry.ErrorCode);
    }

    [Fact]
    public async Task AggregationDoesNotConvertUnavailableCacheCostIntoMissCost()
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "mixed-availability",
            request =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return Events(
                        Usage(
                            request,
                            0,
                            3,
                            1,
                            "0.001",
                            cacheReadTokens: 1,
                            cacheWriteTokens: 0,
                            cacheMissTokens: 2),
                        new ModelStreamEvent
                        {
                            StreamAttemptId = request.StreamAttemptId,
                            Ordinal = 1,
                            Kind = ModelStreamEventKinds.Completed,
                            FinishReason = "stop"
                        });
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "recovered"
                    },
                    Usage(
                        request,
                        1,
                        5,
                        2,
                        "0",
                        availability:
                            UsageAvailabilityStates.CostUnavailable),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var result = await CreateRunner(provider).RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.Equal(8, result.Usage.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal(2, result.Usage.Samples);
        Assert.Null(result.Usage.CacheReadTokens);
        Assert.Null(result.Usage.CacheMissTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            result.Usage.Availability);
        Assert.Equal("0.001", result.Usage.CostUsd);
    }

    [Fact]
    public async Task RetryOutputCapSubtractsUsageFromEarlierAttempts()
    {
        var calls = 0;
        var outputCaps = new List<int?>();
        var provider = new TestStreamingProvider(
            "bounded-retry",
            request =>
            {
                outputCaps.Add(request.MaxOutputTokens);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return FailingEvents(
                        request,
                        inputTokens: 300,
                        outputTokens: 200,
                        costUsd: "0.001");
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "ok"
                    },
                    Usage(request, 1, 1, 1, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var runner = CreateRunner(provider);

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            maxOutputTokens: 900);

        Assert.Equal("ok", result.Text);
        Assert.Equal(new int?[] { 900, 400 }, outputCaps);
        Assert.Equal(301, result.Usage.InputTokens);
        Assert.Equal(201, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task LengthFinishIsNotRetriedAfterUsageWasCharged()
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "length-limited",
            request =>
            {
                Interlocked.Increment(ref calls);
                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "partial"
                    },
                    Usage(request, 1, 10, 2, "0.003"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "length"
                    });
            });
        var runner = CreateRunner(provider);
        var usage = new List<ProviderUsageNotice>();
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsage: notice =>
                    {
                        usage.Add(notice);
                        return default;
                    },
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_output_incomplete", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(1, calls);
        var charged = Assert.Single(usage).Usage;
        Assert.Equal(10, charged.InputTokens);
        Assert.Equal(2, charged.OutputTokens);
        Assert.Equal("0.003", charged.CostUsd);
        Assert.Empty(uncertain);
    }

    [Fact]
    public async Task CancellationRejectsLateProviderOutput()
    {
        var provider = new TestStreamingProvider(
            "slow",
            request => SlowIgnoringCancellation(request));
        var runner = CreateRunner(provider);
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(
                "run-1",
                "run-attempt-1",
                "turn-1",
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                cancellation.Token)
            .AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task CancellationDuringDispatchNoticeDoesNotCallProvider()
    {
        var providerCalls = 0;
        var provider = new TestStreamingProvider(
            "cancelled-before-provider",
            request =>
            {
                Interlocked.Increment(ref providerCalls);
                return Events(
                    Usage(request, 0, 0, 0, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var runner = CreateRunner(provider);
        using var cancellation = new CancellationTokenSource();
        var uncertain = new List<ProviderUsageUncertainNotice>();
        var knownZero = new List<ProviderDispatchKnownZeroNotice>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    cancellation.Token,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    },
                    onDispatch: _ =>
                    {
                        cancellation.Cancel();
                        return default;
                    },
                    onDispatchKnownZero: notice =>
                    {
                        knownZero.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal(0, providerCalls);
        var notice = Assert.Single(uncertain);
        Assert.Equal("provider_cancelled_before_usage", notice.ReasonCode);
        Assert.Empty(knownZero);
    }

    [Fact]
    public async Task HighFrequencyStreamCancelsEveryEventWaitDelay()
    {
        const int deltaCount = 2_048;
        var waitDelay = new TrackingWaitDelay();
        var provider = new TestStreamingProvider(
            "high-frequency",
            request => HighFrequencyEvents(request, deltaCount));
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                IdleTimeout = TimeSpan.FromSeconds(10),
                TotalTimeout = TimeSpan.FromSeconds(20)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator(),
            new ProviderStreamLimits(maxEventsPerAttempt: deltaCount + 2),
            waitDelay);

        var result = await Run(runner);

        Assert.Equal(deltaCount, result.Text!.Length);
        Assert.Equal(deltaCount + 3, waitDelay.Started);
        Assert.Equal(waitDelay.Started, waitDelay.Cancelled);
        Assert.Equal(0, waitDelay.Active);
        Assert.Equal(1, waitDelay.PeakActive);
    }

    [Fact]
    public async Task BlockingEventWaitCleanupFailsAtBoundedCapacity()
    {
        const int capacity = 4;
        var waitDelay = new BlockingWaitDelay();
        var dispatcher =
            new BoundedCancellationDispatcher(capacity);
        var provider = new TestStreamingProvider(
            "bounded-wait-cleanup",
            request => HighFrequencyEvents(request, deltaCount: 16));
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                IdleTimeout = TimeSpan.FromSeconds(10),
                TotalTimeout = TimeSpan.FromSeconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(100)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator(),
            new ProviderStreamLimits(maxEventsPerAttempt: 32),
            waitDelay,
            dispatcher);
        var uncertain = new List<ProviderUsageUncertainNotice>();
        var knownZero = new List<ProviderDispatchKnownZeroNotice>();

        try
        {
            var error = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-1",
                        "run-attempt-1",
                        "turn-1",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None,
                        onUsageUncertain: notice =>
                        {
                            uncertain.Add(notice);
                            return default;
                        },
                        onDispatchKnownZero: notice =>
                        {
                            knownZero.Add(notice);
                            return default;
                        })
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(
                "provider_cancellation_capacity_exceeded",
                error.Code);
            Assert.False(error.UsageKnownToBeZero);
            var notice = Assert.Single(uncertain);
            Assert.Equal(error.Code, notice.ReasonCode);
            Assert.Empty(knownZero);
            Assert.InRange(
                dispatcher.ActiveReservations,
                1,
                capacity);
            Assert.Equal(capacity - 1, waitDelay.Started);
            Assert.Equal(waitDelay.Started, waitDelay.Callbacks);
        }
        finally
        {
            waitDelay.Release.TrySetResult();
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.ActiveReservations == 0,
                TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task IdleWinnerCannotBeReclassifiedByLateMoveNext()
    {
        var moveNext = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = Task.CompletedTask;
        var winner = await Task.WhenAny(moveNext.Task, idle);

        moveNext.TrySetResult(false);

        Assert.False(
            ProviderAttemptRunner.IsMoveNextWithinDeadline(
                winner,
                moveNext.Task,
                completedAt: TimeSpan.FromSeconds(1),
                waitStartedAt: TimeSpan.Zero,
                idleTimeout: TimeSpan.FromSeconds(2),
                totalTimeout: TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task WaitDelayCancellationCallbackCannotChangeSuccessfulResult()
    {
        var waitDelay = new TrackingWaitDelay(throwOnCancellation: true);
        var provider = new TestStreamingProvider(
            "throwing-wait-callback",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                Usage(request, 1, 1, 1, "0"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                }));
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                IdleTimeout = TimeSpan.FromSeconds(10),
                TotalTimeout = TimeSpan.FromSeconds(20)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator(),
            streamLimits: null,
            eventWaitDelay: waitDelay);

        var result = await Run(runner);

        Assert.Equal("ok", result.Text);
        Assert.Equal(1, result.Usage.InputTokens);
        Assert.Equal(4, waitDelay.Started);
        Assert.Equal(waitDelay.Started, waitDelay.Cancelled);
        Assert.Equal(0, waitDelay.Active);
    }

    [Fact]
    public async Task ProviderCancellationBeforeUsageIsNotRunCancellation()
    {
        var provider = new TestStreamingProvider(
            "self-cancelling",
            _ => CancelBeforeUsage());
        var runner = CreateRunner(provider);
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_usage_unknown", error.Code);
        Assert.False(error.Retryable);
        var notice = Assert.Single(uncertain);
        Assert.Equal("self-cancelling", notice.ProviderId);
        Assert.Equal("provider_usage_unknown", notice.ReasonCode);
    }

    [Fact]
    public async Task ProviderCancellationAfterUsageCanRetry()
    {
        var calls = 0;
        var provider = new TestStreamingProvider(
            "self-cancelling",
            request =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return UsageThenCancel(request);
                }

                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "recovered"
                    },
                    Usage(request, 1, 1, 1, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var runner = CreateRunner(provider);
        var lifecycle = new List<ProviderAttemptNotice>();
        var uncertain = new List<ProviderUsageUncertainNotice>();
        var discarded = new List<ProviderResultDiscardedNotice>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            lifecycle.Add,
            onUsageUncertain: notice =>
            {
                uncertain.Add(notice);
                return default;
            },
            onResultDiscarded: notice =>
            {
                discarded.Add(notice);
                return default;
            });

        Assert.Equal(2, calls);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(3, result.Usage.InputTokens);
        Assert.Equal(2, result.Usage.OutputTokens);
        Assert.Empty(uncertain);
        var discardedAttempt = Assert.Single(discarded);
        Assert.Equal(
            "provider_stream_cancelled",
            discardedAttempt.ReasonCode);
        var retry = Assert.Single(lifecycle);
        Assert.Equal(ProviderAttemptNoticeKinds.Retry, retry.Kind);
        Assert.Equal("provider_stream_cancelled", retry.ErrorCode);
    }

    [Fact]
    public async Task AggregateOutputLimitStopsManySmallDeltas()
    {
        var provider = new TestStreamingProvider(
            "bounded",
            request => Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "1234"
                },
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "5678"
                }));
        var runner = CreateRunner(
            provider,
            new ProviderStreamLimits(maxTextUtf8Bytes: 7));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_text_limit", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task PartialTimeoutWithoutUsageFailsClosedWithoutRetry()
    {
        var calls = 0;
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestStreamingProvider(
            "stalled",
            (request, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                return PartialOutputThenWaitForCancellation(
                    request,
                    cancellationToken,
                    cancelled);
            });
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromMilliseconds(20),
                TotalTimeout = TimeSpan.FromSeconds(1)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "run-attempt-1",
                    "turn-1",
                    Array.Empty<NormalizedMessage>(),
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("provider_usage_unknown", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(1, calls);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CooperativeTimeoutCleanupAllowsSafeRetry()
    {
        var calls = 0;
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestStreamingProvider(
            "cooperative-timeout",
            (request, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                return call == 1
                    ? UsageThenWaitForCancellation(
                        request,
                        cancellationToken,
                        cancelled)
                    : Events(
                        new ModelStreamEvent
                        {
                            StreamAttemptId = request.StreamAttemptId,
                            Ordinal = 0,
                            Kind = ModelStreamEventKinds.TextDelta,
                            TextDelta = "recovered"
                        },
                        Usage(request, 1, 3, 2, "0.002"),
                        new ModelStreamEvent
                        {
                            StreamAttemptId = request.StreamAttemptId,
                            Ordinal = 2,
                            Kind = ModelStreamEventKinds.Completed,
                            FinishReason = "stop"
                        });
            });
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromMilliseconds(25),
                TotalTimeout = TimeSpan.FromSeconds(1),
                CleanupTimeout = TimeSpan.FromMilliseconds(500)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var notices = new List<ProviderAttemptNotice>();
        var detachedCleanups = new List<Task>();

        var result = await runner.RunAsync(
            "run-1",
            "run-attempt-1",
            "turn-1",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add,
            onDetachedCleanup: detachedCleanups.Add);

        Assert.Equal(2, calls);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(5, result.Usage.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal("0.003", result.Usage.CostUsd);
        Assert.Empty(detachedCleanups);
        var retry = Assert.Single(notices);
        Assert.Equal(ProviderAttemptNoticeKinds.Retry, retry.Kind);
        Assert.Equal("provider_idle_timeout", retry.ErrorCode);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotDefeatProviderTimeout()
    {
        var callbackInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestStreamingProvider(
            "blocking-cancellation",
            (request, cancellationToken) =>
                WaitWithBlockingCancellation(
                    request,
                    cancellationToken,
                    callbackInvoked,
                    release));
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                IdleTimeout = TimeSpan.FromMilliseconds(20),
                TotalTimeout = TimeSpan.FromSeconds(1),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());

        try
        {
            var error = await Assert.ThrowsAsync<ProviderException>(
                () => runner.RunAsync(
                        "run-1",
                        "run-attempt-1",
                        "turn-1",
                        Array.Empty<NormalizedMessage>(),
                        Array.Empty<ToolDescriptor>(),
                        new AttemptFence(),
                        null,
                        CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("provider_idle_timeout", error.Code);
            Assert.False(error.Retryable);
            await callbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task QuarantinedProviderDoesNotBlockHealthyFallback()
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var primaryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrimary = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new TestStreamingProvider(
            "quarantined",
            (request, _) =>
            {
                Interlocked.Increment(ref primaryCalls);
                return IgnoreCancellationUntilReleased(
                    request,
                    primaryStarted,
                    releasePrimary);
            });
        var fallback = new TestStreamingProvider(
            "healthy",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return Events(
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.TextDelta,
                        TextDelta = "healthy"
                    },
                    Usage(request, 1, 1, 1, "0"),
                    new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "stop"
                    });
            });
        var runner = new ProviderAttemptRunner(
            new[] { primary, fallback },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(1),
                TotalTimeout = TimeSpan.FromSeconds(2),
                CleanupTimeout = TimeSpan.FromMilliseconds(25)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator());
        var detached = new List<Task>();
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = runner.RunAsync(
                "cancelled-run",
                "cancelled-attempt",
                "cancelled-turn",
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                cancellation.Token,
                onDetachedCleanup: detached.Add)
            .AsTask();
        await primaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledRun);
        var cleanup = Assert.Single(detached);
        Assert.False(cleanup.IsCompleted);
        var notices = new List<ProviderAttemptNotice>();

        var result = await runner.RunAsync(
            "healthy-run",
            "healthy-attempt",
            "healthy-turn",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);

        Assert.Equal("healthy", result.ProviderId);
        Assert.Equal("healthy", result.Text);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(1, fallbackCalls);
        var fallbackNotice = Assert.Single(notices);
        Assert.Equal(ProviderAttemptNoticeKinds.Fallback, fallbackNotice.Kind);
        Assert.Equal("provider_cleanup_pending", fallbackNotice.ErrorCode);

        releasePrimary.TrySetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("provider_auth_failed")]
    [InlineData("provider_balance_exhausted")]
    [InlineData("provider_route_unavailable")]
    public async Task RouteScopedFailureFailsOverWithoutRetryingSameRoute(
        string errorCode)
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var primary = new TestStreamingProvider(
            "primary",
            request =>
            {
                Interlocked.Increment(ref primaryCalls);
                return RouteFailureEvents(request, errorCode);
            });
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        var runner = new ProviderAttemptRunner(
            new[] { primary, fallback },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 3,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());

        var result = await RunNamed(runner, "route-failover");

        Assert.Equal("fallback", result.ProviderId);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public async Task RequestFatalFailureAbortsWithoutFallback()
    {
        var fallbackCalls = 0;
        var primary = new TestStreamingProvider(
            "primary",
            request => FatalRequestEvents(request));
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        var runner = new ProviderAttemptRunner(
            new[] { primary, fallback },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 3,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => RunNamed(runner, "fatal-request"));

        Assert.Equal("provider_invalid_request", error.Code);
        Assert.Equal(ProviderFailureDisposition.AbortRun, error.Disposition);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public async Task UnknownUsageStillFailsClosedBeforeFallback()
    {
        var fallbackCalls = 0;
        var uncertain = new List<ProviderUsageUncertainNotice>();
        var primary = new TestStreamingProvider(
            "primary",
            request => UnknownUsageRouteFailureEvents(request));
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        var runner = new ProviderAttemptRunner(
            new[] { primary, fallback },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 1,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => RunNamed(
                runner,
                "unknown-usage",
                onUsageUncertain: notice =>
                {
                    uncertain.Add(notice);
                    return default;
                }));

        Assert.Equal("provider_usage_unknown", error.Code);
        Assert.Equal(ProviderFailureDisposition.AbortRun, error.Disposition);
        Assert.Equal(0, fallbackCalls);
        Assert.Equal(
            "provider_usage_unknown",
            Assert.Single(uncertain).ReasonCode);
    }

    [Fact]
    public async Task CooldownSkipsFailedRouteAcrossNewRunAndCapturedPlan()
    {
        var clock = new ManualRuntimeClock(DateTimeOffset.UnixEpoch);
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var primary = new TestStreamingProvider(
            "primary",
            request =>
            {
                Interlocked.Increment(ref primaryCalls);
                return RouteFailureEvents(
                    request,
                    "provider_auth_failed");
            });
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        var runner = CreateResilientRunner(
            new[] { primary, fallback },
            clock,
            initialCooldown: TimeSpan.FromMinutes(1));
        var capturedRecoveryPlan = runner.CaptureRoutePlan();

        var first = await RunNamed(runner, "first-run");
        var recovered = await RunNamed(
            runner,
            "recovered-run",
            routePlan: capturedRecoveryPlan);

        Assert.Equal("fallback", first.ProviderId);
        Assert.Equal("fallback", recovered.ProviderId);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(2, fallbackCalls);
    }

    [Fact]
    public async Task CooldownExpiryAdmitsOnlyOneConcurrentHalfOpenProbe()
    {
        var clock = new ManualRuntimeClock(DateTimeOffset.UnixEpoch);
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var probeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new TestStreamingProvider(
            "primary",
            (request, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref primaryCalls);
                return call == 1
                    ? RouteFailureEvents(request, "provider_auth_failed")
                    : call == 2
                        ? BlockingSuccessfulEvents(
                            request,
                            probeEntered,
                            releaseProbe,
                            cancellationToken)
                        : SuccessfulEvents(request);
            });
        var fallback = new TestStreamingProvider(
            "fallback",
            request =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return SuccessfulEvents(request);
            });
        var runner = CreateResilientRunner(
            new[] { primary, fallback },
            clock,
            initialCooldown: TimeSpan.FromSeconds(10));

        var initial = await RunNamed(runner, "initial");
        Assert.Equal("fallback", initial.ProviderId);
        clock.Advance(TimeSpan.FromSeconds(10));

        var probe = RunNamed(runner, "probe");
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var followers = Enumerable.Range(0, 12)
            .Select(index => RunNamed(runner, "follower-" + index))
            .ToArray();
        var followerResults = await Task.WhenAll(followers);

        Assert.All(
            followerResults,
            result => Assert.Equal("fallback", result.ProviderId));
        Assert.Equal(2, primaryCalls);
        Assert.Equal(13, fallbackCalls);

        releaseProbe.TrySetResult();
        Assert.Equal("primary", (await probe).ProviderId);
        Assert.Equal(
            "primary",
            (await RunNamed(runner, "after-recovery")).ProviderId);
        Assert.Equal(3, primaryCalls);
    }

    [Fact]
    public async Task FailedHalfOpenProbeReopensIncreasingCooldown()
    {
        var clock = new ManualRuntimeClock(DateTimeOffset.UnixEpoch);
        var primaryCalls = 0;
        var primary = new TestStreamingProvider(
            "primary",
            request =>
            {
                var call = Interlocked.Increment(ref primaryCalls);
                return call <= 2
                    ? RouteFailureEvents(
                        request,
                        "provider_route_unavailable")
                    : SuccessfulEvents(request);
            });
        var fallback = new TestStreamingProvider(
            "fallback",
            SuccessfulEvents);
        var runner = CreateResilientRunner(
            new[] { primary, fallback },
            clock,
            initialCooldown: TimeSpan.FromSeconds(5),
            maxCooldown: TimeSpan.FromSeconds(20));

        Assert.Equal(
            "fallback",
            (await RunNamed(runner, "initial")).ProviderId);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "fallback",
            (await RunNamed(runner, "failed-probe")).ProviderId);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "fallback",
            (await RunNamed(runner, "still-cooling")).ProviderId);
        Assert.Equal(2, primaryCalls);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "primary",
            (await RunNamed(runner, "recovered-probe")).ProviderId);
        Assert.Equal(3, primaryCalls);
    }

    [Fact]
    public async Task HalfOpenProbeDispatchesOnlyOneAttempt()
    {
        var clock = new ManualRuntimeClock(DateTimeOffset.UnixEpoch);
        var primaryCalls = 0;
        var primary = new TestStreamingProvider(
            "primary",
            request =>
            {
                var call = Interlocked.Increment(ref primaryCalls);
                return call == 1
                    ? RouteFailureEvents(
                        request,
                        "provider_route_unavailable")
                    : FailingEvents(request);
            });
        var fallback = new TestStreamingProvider(
            "fallback",
            SuccessfulEvents);
        var runner = CreateResilientRunner(
            new[] { primary, fallback },
            clock,
            initialCooldown: TimeSpan.FromSeconds(5),
            maxAttemptsPerProvider: 3);

        Assert.Equal(
            "fallback",
            (await RunNamed(runner, "initial")).ProviderId);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "fallback",
            (await RunNamed(runner, "half-open")).ProviderId);

        Assert.Equal(2, primaryCalls);
    }

    [Fact]
    public async Task AllRouteFailuresReturnTheLastFailure()
    {
        var firstCalls = 0;
        var secondCalls = 0;
        var first = new TestStreamingProvider(
            "first",
            request =>
            {
                Interlocked.Increment(ref firstCalls);
                return RouteFailureEvents(request, "first_route_failed");
            });
        var second = new TestStreamingProvider(
            "second",
            request =>
            {
                Interlocked.Increment(ref secondCalls);
                return RouteFailureEvents(request, "second_route_failed");
            });
        var runner = new ProviderAttemptRunner(
            new[] { first, second },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 4,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => RunNamed(runner, "all-routes-failed"));

        Assert.Equal("second_route_failed", error.Code);
        Assert.Equal(ProviderFailureDisposition.Failover, error.Disposition);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    private static ProviderAttemptRunner CreateRunner(
        IStreamingModelProvider provider,
        ProviderStreamLimits? streamLimits = null)
    {
        return new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(2),
                TotalTimeout = TimeSpan.FromSeconds(5)
            },
            new ImmediateDelay(),
            new SequentialIdGenerator(),
            streamLimits);
    }

    private static ProviderAttemptRunner CreateResilientRunner(
        IReadOnlyList<IStreamingModelProvider> providers,
        IRuntimeClock clock,
        TimeSpan initialCooldown,
        TimeSpan? maxCooldown = null,
        int maxAttemptsPerProvider = 1)
    {
        return new ProviderAttemptRunner(
            providers,
            new ProviderRetryPolicy
            {
                MaxAttemptsPerProvider = maxAttemptsPerProvider,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(2),
                TotalTimeout = TimeSpan.FromSeconds(5)
            },
            new ImmediateDelay(),
            new GuidRuntimeIdGenerator(),
            routeResilienceOptions: new ProviderRouteResilienceOptions
            {
                InitialCooldown = initialCooldown,
                MaxCooldown = maxCooldown ?? initialCooldown,
                MaxTrackedRoutes = 16
            },
            clock: clock);
    }

    private static ToolDescriptor CreateTool()
    {
        return new ToolDescriptor
        {
            Name = "gather_food",
            Version = "1",
            Description = "Gather food.",
            ParametersSchema = ProtocolJson.ParseElement("""{"type":"object"}"""),
            Effect = ToolEffects.WorldCommand,
            ThreadAffinity = ThreadAffinities.EngineMainThread,
            TimeoutMs = 1000,
            RetryPolicy = "idempotent",
            IdempotencyPolicy = "required"
        };
    }

    private static ModelStreamEvent Event(
        StreamingModelRequest request,
        long ordinal,
        string kind,
        string toolCallId,
        string name,
        string arguments)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = kind,
            ToolCallId = toolCallId,
            ToolNameDelta = name,
            ArgumentsJsonDelta = arguments
        };
    }

    private static ModelStreamEvent Usage(
        StreamingModelRequest request,
        long ordinal,
        int inputTokens,
        int outputTokens,
        string costUsd,
        int? cacheReadTokens = null,
        int? cacheWriteTokens = null,
        int? cacheMissTokens = null,
        int? reasoningTokens = null,
        int? providerTotalTokens = null,
        string availability =
            UsageAvailabilityStates.CostAvailable)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = costUsd,
                CacheReadTokens = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens,
                CacheMissTokens = cacheMissTokens,
                ReasoningTokens = reasoningTokens,
                ProviderTotalTokens = providerTotalTokens,
                Availability = availability
            }
        };
    }

    private static ModelStreamEvent ContentEvent(
        StreamingModelRequest request,
        long ordinal,
        string kind)
    {
        return kind switch
        {
            ModelStreamEventKinds.TextDelta => new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal,
                Kind = kind,
                TextDelta = "late"
            },
            ModelStreamEventKinds.ReasoningDelta => new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal,
                Kind = kind,
                ReasoningDelta = "late"
            },
            ModelStreamEventKinds.ToolCallDelta => Event(
                request,
                ordinal,
                kind,
                "late-call",
                "late_tool",
                "{}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static ProviderUsage ZeroUsage()
    {
        return new ProviderUsage
        {
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = "0"
        };
    }

    private static Task<ProviderAttemptResult> Run(
        ProviderAttemptRunner runner)
    {
        return runner.RunAsync(
                "run-1",
                "run-attempt-1",
                "turn-1",
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None)
            .AsTask();
    }

    private static Task<ProviderAttemptResult> RunNamed(
        ProviderAttemptRunner runner,
        string runId,
        ProviderRoutePlan? routePlan = null,
        Func<ProviderUsageUncertainNotice, ValueTask>?
            onUsageUncertain = null)
    {
        return runner.RunAsync(
                runId,
                runId + "-attempt",
                runId + "-turn",
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None,
                onUsageUncertain: onUsageUncertain,
                routePlan: routePlan)
            .AsTask();
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Events(
        params ModelStreamEvent[] events)
    {
        foreach (var item in events)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        RouteFailureEvents(
            StreamingModelRequest request,
            string errorCode)
    {
        _ = request;
        await Task.Yield();
        throw new ProviderException(
            errorCode,
            "routing",
            "The provider route is unavailable.",
            ProviderFailureDisposition.Failover,
            usageKnownToBeZero: true);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        FatalRequestEvents(StreamingModelRequest request)
    {
        _ = request;
        await Task.Yield();
        throw new ProviderException(
            "provider_invalid_request",
            "validation",
            "The request is invalid for every route.",
            ProviderFailureDisposition.AbortRun,
            usageKnownToBeZero: true);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        UnknownUsageRouteFailureEvents(StreamingModelRequest request)
    {
        _ = request;
        await Task.Yield();
        throw new ProviderException(
            "provider_route_unavailable",
            "routing",
            "The provider route is unavailable.",
            ProviderFailureDisposition.Failover);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        BlockingSuccessfulEvents(
            StreamingModelRequest request,
            TaskCompletionSource entered,
            TaskCompletionSource release,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = "ok"
        };
        yield return Usage(request, 1, 1, 1, "0");
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 2,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = "stop"
        };
    }

    private static async IAsyncEnumerable<ModelStreamEvent> HighFrequencyEvents(
        StreamingModelRequest request,
        int deltaCount)
    {
        for (var ordinal = 0; ordinal < deltaCount; ordinal++)
        {
            await Task.Yield();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "x"
            };
        }

        await Task.Yield();
        yield return Usage(request, deltaCount, 1, 1, "0");
        await Task.Yield();
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = deltaCount + 1,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = "stop"
        };
    }

    private static async IAsyncEnumerable<ModelStreamEvent> FailingEvents(
        StreamingModelRequest request,
        int inputTokens = 0,
        int outputTokens = 0,
        string costUsd = "0",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return Usage(
            request,
            0,
            inputTokens,
            outputTokens,
            costUsd);
        throw new ProviderException(
            "transient",
            "network",
            "Transient provider failure.",
            true);
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        PartialFailingEvents(
            StreamingModelRequest request,
            string partialText)
    {
        await Task.Yield();
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = partialText
        };
        yield return Usage(request, 1, 0, 0, "0");
        throw new ProviderException(
            "transient",
            "network",
            "Transient provider failure.",
            true);
    }

    private static void ApplyPresentation(
        System.Text.StringBuilder view,
        ICollection<AttemptStreamingPresentationChunk> retained,
        IReadOnlyList<AttemptStreamingPresentationChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            retained.Add(chunk);
            if (chunk.ReplacesPriorText)
            {
                view.Clear();
            }

            view.Append(chunk.Text);
        }
    }

    private static async IAsyncEnumerable<ModelStreamEvent> SlowIgnoringCancellation(
        StreamingModelRequest request)
    {
        await Task.Delay(50);
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = "late"
        };
    }

    private static async IAsyncEnumerable<ModelStreamEvent> CancelBeforeUsage()
    {
        await Task.Yield();
        throw new OperationCanceledException(
            "The provider cancelled its own operation.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ModelStreamEvent> UsageThenCancel(
        StreamingModelRequest request)
    {
        yield return Usage(request, 0, 2, 1, "0");
        await Task.Yield();
        throw new TaskCanceledException(
            "The provider cancelled its own stream.");
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        PartialOutputThenWaitForCancellation(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            TaskCompletionSource cancelled)
    {
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = "partial"
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            cancelled.TrySetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        UsageThenWaitForCancellation(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            TaskCompletionSource cancelled)
    {
        yield return Usage(request, 0, 2, 1, "0.001");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        IgnoreCancellationUntilReleased(
            StreamingModelRequest request,
            TaskCompletionSource started,
            TaskCompletionSource release)
    {
        started.TrySetResult();
        await release.Task;
        yield return Usage(request, 0, 0, 0, "0");
    }

    private static async IAsyncEnumerable<ModelStreamEvent>
        WaitWithBlockingCancellation(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            TaskCompletionSource callbackInvoked,
            TaskCompletionSource release)
    {
        _ = request;
        using var registration = cancellationToken.Register(
            () =>
            {
                callbackInvoked.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            });
        await release.Task;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private sealed class TestStreamingProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private readonly Func<
            StreamingModelRequest,
            CancellationToken,
            IAsyncEnumerable<ModelStreamEvent>> _script;

        public TestStreamingProvider(
            string providerId,
            Func<StreamingModelRequest, IAsyncEnumerable<ModelStreamEvent>> script)
            : this(providerId, (request, _) => script(request))
        {
        }

        public TestStreamingProvider(
            string providerId,
            Func<
                StreamingModelRequest,
                CancellationToken,
                IAsyncEnumerable<ModelStreamEvent>> script)
        {
            ProviderId = providerId;
            RouteMetadata = new ProviderRouteMetadata(
                providerId + "-model",
                "test.streaming.v1");
            _script = script;
        }

        public string ProviderId { get; }

        public ProviderRouteMetadata RouteMetadata { get; }

        public ProviderCapabilities Capabilities { get; } = new();

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            return _script(request, cancellationToken);
        }
    }

    private sealed class EstimatingStreamingProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource,
        ICalibratingProviderPromptTokenEstimator
    {
        private readonly IProviderPromptTokenEstimator _estimator;
        private readonly Func<
            StreamingModelRequest,
            IAsyncEnumerable<ModelStreamEvent>> _script;
        private int _streamCalls;

        public EstimatingStreamingProvider(
            string providerId,
            IProviderPromptTokenEstimator estimator,
            Func<
                StreamingModelRequest,
                IAsyncEnumerable<ModelStreamEvent>> script)
        {
            ProviderId = providerId;
            RouteMetadata = new ProviderRouteMetadata(
                providerId + "-model",
                "test.streaming.v1");
            _estimator = estimator;
            _script = script;
        }

        public string ProviderId { get; }

        public ProviderRouteMetadata RouteMetadata { get; }

        public ProviderCapabilities Capabilities { get; } = new();

        public string EstimatorId => _estimator.EstimatorId;

        public string Version => _estimator.Version;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public int EstimatePromptTokens(
            IReadOnlyList<NormalizedMessage> messages,
            IReadOnlyList<ToolDescriptor> tools)
        {
            return _estimator.EstimatePromptTokens(messages, tools);
        }

        public void ObserveActualInputTokens(
            int estimatedTokens,
            int actualInputTokens)
        {
            if (_estimator
                is ICalibratingProviderPromptTokenEstimator calibrating)
            {
                calibrating.ObserveActualInputTokens(
                    estimatedTokens,
                    actualInputTokens);
            }
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _streamCalls);
            return _script(request);
        }
    }

    private sealed class FixedProviderPromptEstimator :
        IProviderPromptTokenEstimator
    {
        private readonly int _tokens;

        public FixedProviderPromptEstimator(
            string estimatorId,
            string version,
            int tokens)
        {
            EstimatorId = estimatorId;
            Version = version;
            _tokens = tokens;
        }

        public string EstimatorId { get; }

        public string Version { get; }

        public int EstimatePromptTokens(
            IReadOnlyList<NormalizedMessage> messages,
            IReadOnlyList<ToolDescriptor> tools)
        {
            _ = messages ?? throw new ArgumentNullException(nameof(messages));
            _ = tools ?? throw new ArgumentNullException(nameof(tools));
            return _tokens;
        }
    }

    private sealed class FixedRuntimeTokenEstimator : IRuntimeTokenEstimator
    {
        private readonly int _tokens;

        public FixedRuntimeTokenEstimator(int tokens)
        {
            _tokens = tokens;
        }

        public string EstimatorId => "fixed-runtime";

        public string Version => "1";

        public int EstimateTokens(string content)
        {
            _ = content ?? throw new ArgumentNullException(nameof(content));
            return content.Length == 0 ? 0 : _tokens;
        }

        public int EstimateOpaqueUtf8Bytes(int utf8Bytes)
        {
            if (utf8Bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(utf8Bytes));
            }

            return utf8Bytes == 0 ? 0 : _tokens;
        }
    }

    private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count =>
            throw new InvalidOperationException(
                "The cancelled input must not be enumerated.");

        public T this[int index] =>
            throw new InvalidOperationException(
                "The cancelled input must not be enumerated.");

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException(
                "The cancelled input must not be enumerated.");

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class FailingPreparationProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource,
        IProviderRequestAdapter
    {
        private int _streamCalls;

        public string ProviderId => "failing-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("failing-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("expected");
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return Events();
        }
    }

    private sealed class StalePreparationProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource,
        IProviderRequestAdapter
    {
        private int _streamCalls;

        public string ProviderId => "stale-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("stale-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new()
        {
            ReasoningInput = true
        };

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public async ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            var prepared = await new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, cancellationToken);
            prepared.Request.Messages[0].Parts[0].Text = "mutated";
            return prepared;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return Events();
        }
    }

    private sealed class BlockingPreparationProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource,
        IProviderRequestAdapter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCalls;

        public string ProviderId => "blocking-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("blocking-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _release.Task.GetAwaiter().GetResult();
            return new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, CancellationToken.None);
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return Events();
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class BlockingStreamStartProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource
    {
        private readonly bool _blockStreamFactory;
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingStreamStartProvider(bool blockStreamFactory)
        {
            _blockStreamFactory = blockStreamFactory;
        }

        public string ProviderId => "blocking-stream-start";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("blocking-start-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Entered => _entered.Task;

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (_blockStreamFactory)
            {
                _entered.TrySetResult();
                _release.Wait();
                return Events(Usage(request, 0, 0, 0, "0"));
            }

            return new BlockingEnumeratorEnumerable(
                request,
                _entered,
                _release);
        }

        public void Release()
        {
            _release.Set();
        }
    }

    private sealed class LateFaultingPreparedStreamProvider
        : IStreamingModelProvider,
          IPreparedStreamingModelProvider,
          IProviderRouteMetadataSource
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FaultingPreparedStream? _prepared;
        private int _prepareCalls;

        public string ProviderId => "late-faulting-prepared-stream";

        public ProviderRouteMetadata RouteMetadata { get; } = new(
            "late-faulting-prepared-model",
            new ProviderDialectContract(
                "test.prepared.sse.v1",
                ProviderRequestFamily.Custom,
                "test.prepared.request.v1",
                ProviderStreamFraming.ServerSentEvents,
                "test.prepared.sse.v1",
                "test.prepared.tools.v1",
                "test.prepared.usage.v1",
                "test.prepared.reasoning.v1",
                "application/json",
                "test.prepared.state.v1"),
            "test-route-policy.v1",
            new string('a', 64));

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Started => _started.Task;

        public int PrepareCalls => Volatile.Read(ref _prepareCalls);

        public int DisposeCount => _prepared?.DisposeCount ?? 0;

        public async ValueTask<PreparedProviderStream> PrepareStreamAsync(
            ProviderStreamPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _prepareCalls);
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            var body = new byte[] { 1, 2, 3 };
            _prepared = new FaultingPreparedStream(
                ProviderWireRequestEvidence.CreateAvailable(
                    body,
                    "application/json",
                    context.RouteIdentity));
            return _prepared;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException(
                "The prepared provider must not use direct dispatch.");
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        private sealed class FaultingPreparedStream : PreparedProviderStream
        {
            private int _disposeCount;

            public FaultingPreparedStream(
                ProviderWireRequestEvidence evidence)
                : base(evidence)
            {
            }

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public override IAsyncEnumerable<ModelStreamEvent> StreamAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Events();
            }

            public override ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCount);
                return ValueTask.FromException(
                    new InvalidOperationException(
                        "Prepared stream cleanup failed."));
            }
        }
    }

    private sealed class LateFaultingStreamStartProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _streamCalls;
        private int _disposeCount;

        public string ProviderId => "late-faulting-stream-start";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("late-faulting-stream-start-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Started => _started.Task;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            _started.TrySetResult();
            _release.Wait();
            return new FaultingStreamStartEnumerable(this);
        }

        public void Release()
        {
            _release.Set();
        }

        private sealed class FaultingStreamStartEnumerable
            : IAsyncEnumerable<ModelStreamEvent>,
              IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly LateFaultingStreamStartProvider _owner;

            public FaultingStreamStartEnumerable(
                LateFaultingStreamStartProvider owner)
            {
                _owner = owner;
            }

            public ModelStreamEvent Current =>
                throw new InvalidOperationException(
                    "The controlled stream never yields an event.");

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                _ = cancellationToken;
                return this;
            }

            public ValueTask<bool> MoveNextAsync() => new(false);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _owner._disposeCount);
                return ValueTask.FromException(
                    new InvalidOperationException(
                        "Stream-start cleanup failed."));
            }
        }
    }

    private sealed class CooperativeFaultingCleanupProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCalls;
        private int _disposeCount;

        public string ProviderId => "cooperative-faulting-cleanup";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("cooperative-faulting-cleanup-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Started => _started.Task;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            return Interlocked.Increment(ref _streamCalls) == 1
                ? new CooperativeFaultingEnumerable(
                    this,
                    cancellationToken)
                : SuccessfulEvents(request);
        }

        private sealed class CooperativeFaultingEnumerable
            : IAsyncEnumerable<ModelStreamEvent>,
              IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly CooperativeFaultingCleanupProvider _owner;
            private readonly CancellationToken _cancellationToken;

            public CooperativeFaultingEnumerable(
                CooperativeFaultingCleanupProvider owner,
                CancellationToken cancellationToken)
            {
                _owner = owner;
                _cancellationToken = cancellationToken;
            }

            public ModelStreamEvent Current =>
                throw new InvalidOperationException(
                    "The controlled stream never yields an event.");

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                _ = cancellationToken;
                return this;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                _owner._started.TrySetResult();
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        _cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _owner._disposeCount);
                return ValueTask.FromException(
                    new InvalidOperationException(
                        "Cooperative cleanup failed."));
            }
        }
    }

    private sealed class OversizedPreparationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        private int _streamCalls;

        public string ProviderId => "oversized-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("oversized-preparation-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public async ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            var prepared = await new ProviderRequestSanitizer()
                .PrepareRequestAsync(context, cancellationToken);
            prepared.Request.Messages = Enumerable.Range(0, 4_097)
                .Select(
                    index => new NormalizedMessage
                    {
                        MessageId = "oversized-" + index,
                        Role = NormalizedRoles.User,
                        Parts = new List<NormalizedContentPart>
                        {
                            NormalizedContentPart.FromText("x")
                        }
                    })
                .ToArray();
            return prepared;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return Events();
        }
    }

    private sealed class ChangingCountPreparationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        private ChangingCountReadOnlyList<NormalizedMessage>? _messages;
        private int _streamCalls;

        public string ProviderId => "changing-count-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("changing-count-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public int CountReads => _messages?.CountReads ?? 0;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report =
                ProviderRequestSanitizer.Unchanged(
                    context.Request,
                    cancellationToken).Report;
            _messages = new ChangingCountReadOnlyList<NormalizedMessage>(
                context.Request.Messages);
            return new ValueTask<ProviderPreparedRequest>(
                new ProviderPreparedRequest(
                    CopyRequest(context.Request, _messages),
                    report));
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return SuccessfulEvents(request);
        }
    }

    private sealed class DeceptiveToolEnumerationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        private DeceptiveEnumeratorReadOnlyList<ToolDescriptor>? _tools;
        private int _streamCalls;

        public string ProviderId => "deceptive-tool-enumeration";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("deceptive-tool-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public int EnumeratorReads => _tools?.EnumeratorReads ?? 0;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report =
                ProviderRequestSanitizer.Unchanged(
                    context.Request,
                    cancellationToken).Report;
            _tools = new DeceptiveEnumeratorReadOnlyList<ToolDescriptor>(
                context.Request.Tools);
            return new ValueTask<ProviderPreparedRequest>(
                new ProviderPreparedRequest(
                    CopyRequest(
                        context.Request,
                        context.Request.Messages,
                        _tools),
                    report));
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return SuccessfulEvents(request);
        }
    }

    private sealed class BlockingValidationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCalls;

        public string ProviderId => "blocking-validation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("blocking-validation-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public Task Entered => _entered.Task;

        public int StreamCalls => Volatile.Read(ref _streamCalls);

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report =
                ProviderRequestSanitizer.Unchanged(context.Request).Report;
            var request = CopyRequest(
                context.Request,
                new BlockingCountReadOnlyList<NormalizedMessage>(
                    context.Request.Messages,
                    _entered,
                    _release));
            return new ValueTask<ProviderPreparedRequest>(
                new ProviderPreparedRequest(request, report));
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _streamCalls);
            return Events();
        }

        public void Release()
        {
            _release.Set();
        }
    }

    private sealed class UnknownUsagePreparationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        public string ProviderId => "unknown-usage-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("unknown-usage-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException(
                "adapter_declined",
                "provider",
                "The adapter declined the request.",
                false);
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Events();
        }
    }

    private sealed class TransformingPreparationProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource,
          IProviderRequestAdapter
    {
        public string ProviderId => "transforming-preparation";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("transforming-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new();

        public List<StreamingModelRequest> Requests { get; } = new();

        public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
            ProviderRequestPreparationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Assert.Single(context.Request.Messages);
            var output = new[]
            {
                new NormalizedMessage
                {
                    MessageId = source.MessageId,
                    Role = source.Role,
                    CreatedAt = source.CreatedAt,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("adapted")
                    }
                }
            };
            return new ValueTask<ProviderPreparedRequest>(
                context.CreatePreparedRequest(
                    CopyRequest(context.Request, output)));
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(request);
            return Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                Usage(request, 1, 1, 1, "0"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                });
        }
    }

    private sealed class BlockingCountReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner;
        private readonly TaskCompletionSource _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingCountReadOnlyList(
            IReadOnlyList<T> inner,
            TaskCompletionSource entered,
            ManualResetEventSlim release)
        {
            _inner = inner;
            _entered = entered;
            _release = release;
        }

        public int Count
        {
            get
            {
                _entered.TrySetResult();
                _release.Wait();
                return _inner.Count;
            }
        }

        public T this[int index] => _inner[index];

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ChangingCountReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner;
        private int _countReads;

        public ChangingCountReadOnlyList(IReadOnlyList<T> inner)
        {
            _inner = inner;
        }

        public int Count
        {
            get
            {
                var read = Interlocked.Increment(ref _countReads);
                return read == 1
                    ? _inner.Count
                    : checked(_inner.Count + 1);
            }
        }

        public int CountReads => Volatile.Read(ref _countReads);

        public T this[int index] => _inner[index];

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class DeceptiveEnumeratorReadOnlyList<T>
        : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner;
        private int _enumeratorReads;

        public DeceptiveEnumeratorReadOnlyList(IReadOnlyList<T> inner)
        {
            _inner = inner;
        }

        public int Count => _inner.Count;

        public int EnumeratorReads => Volatile.Read(ref _enumeratorReads);

        public T this[int index] => _inner[index];

        public IEnumerator<T> GetEnumerator()
        {
            Interlocked.Increment(ref _enumeratorReads);
            throw new InvalidOperationException(
                "Adapter-owned enumerators must not be trusted.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ChangingItemReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T _first;
        private readonly T _later;
        private int _countReads;
        private int _indexReads;

        public ChangingItemReadOnlyList(T first, T later)
        {
            _first = first;
            _later = later;
        }

        public int Count
        {
            get
            {
                Interlocked.Increment(ref _countReads);
                return 1;
            }
        }

        public int CountReads => Volatile.Read(ref _countReads);

        public int IndexReads => Volatile.Read(ref _indexReads);

        public T this[int index]
        {
            get
            {
                if (index != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return Interlocked.Increment(ref _indexReads) == 1
                    ? _first
                    : _later;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException(
                "Enumeration is not supported.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static StreamingModelRequest CopyRequest(
        StreamingModelRequest source,
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<ToolDescriptor>? tools = null)
    {
        return new StreamingModelRequest
        {
            RunId = source.RunId,
            RunAttemptId = source.RunAttemptId,
            TurnId = source.TurnId,
            ProviderAttemptId = source.ProviderAttemptId,
            StreamAttemptId = source.StreamAttemptId,
            Messages = messages,
            Tools = tools ?? source.Tools,
            MaxOutputTokens = source.MaxOutputTokens
        };
    }

    private static IAsyncEnumerable<ModelStreamEvent> SuccessfulEvents(
        StreamingModelRequest request)
    {
        return Events(
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "ok"
            },
            Usage(request, 1, 1, 1, "0"),
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            });
    }

    private sealed class BlockingEnumeratorEnumerable
        : IAsyncEnumerable<ModelStreamEvent>
    {
        private readonly StreamingModelRequest _request;
        private readonly TaskCompletionSource _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingEnumeratorEnumerable(
            StreamingModelRequest request,
            TaskCompletionSource entered,
            ManualResetEventSlim release)
        {
            _request = request;
            _entered = entered;
            _release = release;
        }

        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            _entered.TrySetResult();
            _release.Wait();
            return Events(Usage(_request, 0, 0, 0, "0"))
                .GetAsyncEnumerator();
        }
    }

    private sealed class CapturingReasoningDisabledProvider
        : IStreamingModelProvider,
          IProviderRouteMetadataSource
    {
        public string ProviderId => "reasoning-disabled";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("reasoning-disabled-model", "test.streaming.v1");

        public ProviderCapabilities Capabilities { get; } = new()
        {
            ReasoningInput = false
        };

        public List<StreamingModelRequest> Requests { get; } = new();

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(request);
            return Events(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = "ok"
                },
                Usage(request, 1, 1, 1, "0"),
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                });
        }
    }

    private sealed class ManualRuntimeClock : IRuntimeClock
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow;

        public ManualRuntimeClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow;
                }
            }
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            lock (_sync)
            {
                _utcNow = _utcNow.Add(duration);
            }
        }
    }

    private sealed class ImmediateDelay : IRuntimeDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    private sealed class TrackingWaitDelay : IRuntimeDelay
    {
        private readonly bool _throwOnCancellation;
        private int _started;
        private int _cancelled;
        private int _active;
        private int _peakActive;

        public TrackingWaitDelay(bool throwOnCancellation = false)
        {
            _throwOnCancellation = throwOnCancellation;
        }

        public int Started => Volatile.Read(ref _started);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int Active => Volatile.Read(ref _active);

        public int PeakActive => Volatile.Read(ref _peakActive);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            Interlocked.Increment(ref _started);
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(
                () =>
                {
                    Interlocked.Increment(ref _cancelled);
                    Interlocked.Decrement(ref _active);
                    completion.TrySetCanceled(cancellationToken);
                    if (_throwOnCancellation)
                    {
                        throw new InvalidOperationException(
                            "The wait cancellation callback failed.");
                    }
                });
            return new ValueTask(completion.Task);
        }

        private void UpdatePeak(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _peakActive);
                if (value <= current
                    || Interlocked.CompareExchange(
                        ref _peakActive,
                        value,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class BlockingWaitDelay : IRuntimeDelay
    {
        private int _started;
        private int _callbacks;

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started => Volatile.Read(ref _started);

        public int Callbacks => Volatile.Read(ref _callbacks);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            Interlocked.Increment(ref _started);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(
                () =>
                {
                    Interlocked.Increment(ref _callbacks);
                    Release.Task.GetAwaiter().GetResult();
                    completion.TrySetCanceled(cancellationToken);
                });
            return new ValueTask(completion.Task);
        }
    }

}
