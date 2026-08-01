using System.Runtime.CompilerServices;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class SimpleCompletionRuntimeTests
{
    [Fact]
    public async Task CompletionIsStatelessAndNeverExposesTools()
    {
        var provider = new RecordingProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        await using var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator());

        var outcome = await runtime.CompleteAsync(
            new SimpleCompletionRequest
            {
                OperationId = "classify-event",
                Messages = new[] { UserMessage("message-1") },
                MaxOutputTokens = 32,
                Inference = new ModelInferenceOptions
                {
                    ReasoningEnabled = false,
                    Temperature = 0.2
                }
            });

        Assert.Equal("classify-event", outcome.OperationId);
        Assert.Equal("recording", outcome.ProviderId);
        Assert.Equal("classified", outcome.Text);
        Assert.Equal(1, outcome.Usage.InputTokens);
        Assert.Empty(provider.ToolNames);
        Assert.Equal(32, provider.MaxOutputTokens);
        Assert.Equal("classify-event", provider.RunId);
        Assert.False(provider.Inference!.ReasoningEnabled);
        Assert.Equal(0.2, provider.Inference.Temperature);
    }

    [Fact]
    public async Task EmptyCompletionFailsBeforeProviderDispatch()
    {
        var provider = new RecordingProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        await using var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator());

        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.CompleteAsync(
                    new SimpleCompletionRequest())
                .AsTask());

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task StopCancelsAndDrainsAnActiveCompletion()
    {
        var provider = new CancellationProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        await using var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator());
        var completion = runtime.CompleteAsync(
                new SimpleCompletionRequest
                {
                    Messages = new[] { UserMessage("message-1") }
                })
            .AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => runtime.CompleteAsync(
                    new SimpleCompletionRequest
                    {
                        Messages = new[] { UserMessage("message-2") }
                    })
                .AsTask());
    }

    [Fact]
    public async Task CompletionSnapshotsMessagesBeforeProviderAdmission()
    {
        var provider = new GatedMessageProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        await using var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator());
        var message = UserMessage("message-1");
        var completion = runtime.CompleteAsync(
                new SimpleCompletionRequest
                {
                    Messages = new[] { message }
                })
            .AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        message.Parts[0] = NormalizedContentPart.FromText("mutated");
        provider.Release.TrySetResult();

        await completion;
        Assert.Equal("Classify this game event.", provider.ObservedText);
    }

    [Fact]
    public async Task CompletionSnapshotsTokenLimitsBeforeProviderAdmission()
    {
        var provider = new AdmissionSnapshotProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        await using var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator(),
            new SimpleCompletionRuntimeOptions
            {
                MaxConcurrentProviderCalls = 1
            });
        var first = runtime.CompleteAsync(
                new SimpleCompletionRequest
                {
                    OperationId = "first",
                    Messages = new[] { UserMessage("first-message") }
                })
            .AsTask();
        await provider.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = new SimpleCompletionRequest
        {
            OperationId = "queued",
            Messages = new[] { UserMessage("queued-message") },
            EstimatedPromptTokens = 7,
            MaxOutputTokens = 33
        };
        var second = runtime.CompleteAsync(queued).AsTask();

        queued.EstimatedPromptTokens = 700;
        queued.MaxOutputTokens = 3;
        provider.ReleaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(33, provider.Observed["queued"]);
    }

    [Fact]
    public async Task StopIsBoundedWhenProviderCancellationCallbackBlocks()
    {
        using var release = new ManualResetEventSlim(false);
        var provider = new BlockingCancellationCallbackProvider(release);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator(),
            new SimpleCompletionRuntimeOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });
        var completion = runtime.CompleteAsync(
                new SimpleCompletionRequest
                {
                    Messages = new[] { UserMessage("blocking-callback") }
                })
            .AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.StopAsync().AsTask().WaitAsync(
            TimeSpan.FromMilliseconds(500));

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWaitsForDetachedProviderCleanupAfterBoundedStop()
    {
        var provider = new SlowCancellationCleanupProvider();
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy(),
            new SystemRuntimeDelay(),
            new GuidRuntimeIdGenerator());
        var runtime = new SimpleCompletionRuntime(
            runner,
            new GuidRuntimeIdGenerator(),
            new SimpleCompletionRuntimeOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        var completion = runtime.CompleteAsync(
                new SimpleCompletionRequest
                {
                    Messages = new[] { UserMessage("slow-cleanup") }
                })
            .AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await runtime.StopWithDrainResultAsync());
        var disposal = runtime.DisposeAsync().AsTask();
        await Task.Delay(80);
        Assert.False(disposal.IsCompleted);

        provider.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static NormalizedMessage UserMessage(string id)
    {
        return new NormalizedMessage
        {
            MessageId = id,
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UtcNow,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("Classify this game event.")
            }
        };
    }

    private sealed class RecordingProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "recording";

        public int CallCount => Volatile.Read(ref _callCount);

        public string? RunId { get; private set; }

        public int? MaxOutputTokens { get; private set; }

        public IReadOnlyList<string> ToolNames { get; private set; } =
            Array.Empty<string>();

        public ModelInferenceOptions? Inference { get; private set; }

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            RunId = request.RunId;
            MaxOutputTokens = request.MaxOutputTokens;
            Inference = request.Inference?.CloneValidated();
            ToolNames = request.Tools.Select(item => item.Name).ToArray();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "classified"
            };
            await Task.Yield();
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
                FinishReason = "stop"
            };
        }
    }

    private sealed class CancellationProvider : IStreamingModelProvider
    {
        public string ProviderId => "cancellation";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = false,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SlowCancellationCleanupProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "slow-cancellation-cleanup";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = false,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                await Release.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class GatedMessageProvider : IStreamingModelProvider
    {
        public string ProviderId => "gated-message";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? ObservedText { get; private set; }

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = false,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            ObservedText = request.Messages[0].Parts[0].Text;
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "done"
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
                FinishReason = "stop"
            };
        }
    }

    private sealed class AdmissionSnapshotProvider : IStreamingModelProvider
    {
        public string ProviderId => "admission-snapshot";

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, int?> Observed { get; } =
            new(StringComparer.Ordinal);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = false,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.RunId == "first")
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            Observed[request.RunId] = request.MaxOutputTokens;
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "done"
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
                FinishReason = "stop"
            };
        }
    }

    private sealed class BlockingCancellationCallbackProvider :
        IStreamingModelProvider
    {
        private readonly ManualResetEventSlim _release;

        public BlockingCancellationCallbackProvider(
            ManualResetEventSlim release)
        {
            _release = release;
        }

        public string ProviderId => "blocking-cancellation-callback";

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = false,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            using var registration = cancellationToken.Register(
                () => _release.Wait());
            Entered.TrySetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            yield break;
        }
    }
}
