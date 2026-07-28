using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class ProviderAttemptRunnerTests
{
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
        var provider = new TestStreamingProvider(
            "primary",
            request =>
            {
                calls++;
                if (calls == 1)
                {
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
    }

    [Fact]
    public async Task FallbackEmitsStructuredLifecycleNotice()
    {
        var first = new TestStreamingProvider(
            "first",
            request => FailingEvents(request));
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
                        Usage(request, 0, 3, 2, "0.001"),
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
                    Usage(request, 2, 5, 4, "0.002"));
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
        Assert.Equal(2, usageNotices.Count);
        var retry = Assert.Single(lifecycle);
        Assert.Equal(ProviderAttemptNoticeKinds.Retry, retry.Kind);
        Assert.Equal("provider_empty_response", retry.ErrorCode);
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
        string costUsd)
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
                CostUsd = costUsd
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

    private static async IAsyncEnumerable<ModelStreamEvent> Events(
        params ModelStreamEvent[] events)
    {
        foreach (var item in events)
        {
            await Task.Yield();
            yield return item;
        }
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

    private sealed class TestStreamingProvider : IStreamingModelProvider
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
            _script = script;
        }

        public string ProviderId { get; }

        public ProviderCapabilities Capabilities { get; } = new();

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            CancellationToken cancellationToken)
        {
            return _script(request, cancellationToken);
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
}
