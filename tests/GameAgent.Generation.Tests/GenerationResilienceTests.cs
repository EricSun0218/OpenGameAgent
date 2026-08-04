using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace GameAgent.Generation.Tests;

public sealed class GenerationResilienceTests
{
    [Fact]
    public async Task Cancellation_while_waiting_for_submission_slot_never_dispatches_provider()
    {
        var provider = new BlockingProvider();
        var jobs = new InMemoryGenerationJobStore();
        var runtime = new GenerationRuntime(
            new[] { provider },
            jobs,
            new PassArtifactStore(),
            options: new GenerationRuntimeOptions { MaxConcurrentSubmissions = 1 });
        var first = runtime.SubmitAsync(Request("first"), cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var second = runtime.SubmitAsync(Request("second"), cancelled.Token).AsTask();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var durable = await jobs.TryGetAsync("second", TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(GenerationJobStatuses.Cancelled, durable!.Status);
        Assert.Equal(GenerationAcceptance.NotAccepted, durable.Acceptance);
        provider.Release.TrySetResult(true);
        await first;
    }

    [Fact]
    public async Task Slow_event_sink_cannot_delay_or_change_generation_outcome()
    {
        var sink = new NeverCompletingEventSink();
        var runtime = new GenerationRuntime(
            new IGenerationProvider[] { new ImmediateProvider() },
            new InMemoryGenerationJobStore(),
            new PassArtifactStore(),
            events: sink,
            options: new GenerationRuntimeOptions
            {
                EventPublishTimeout = TimeSpan.FromMilliseconds(20)
            });
        var started = DateTime.UtcNow;

        var result = await runtime.SubmitAsync(Request("event-timeout"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatuses.Succeeded, result.Status);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.True(sink.Calls >= 1);
    }

    [Fact]
    public async Task Streaming_speech_falls_back_only_before_audio_is_visible()
    {
        var runtime = new StreamingSpeechRuntime(new IStreamingSpeechProvider[]
        {
            new StartedThenRejectingSpeechProvider(),
            new CompletingSpeechProvider()
        });

        var events = await CollectAsync(runtime.StreamAsync(Request(
            "speech-fallback",
            GenerationModalities.Speech), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(events, item => item.Kind == SpeechStreamEventKinds.Audio);
        Assert.Equal(SpeechStreamEventKinds.Completed, events[^1].Kind);
        Assert.DoesNotContain(events, item => item.Kind == SpeechStreamEventKinds.Started);
    }

    [Fact]
    public async Task Streaming_speech_never_mixes_providers_after_audio_started()
    {
        var fallback = new CompletingSpeechProvider();
        var runtime = new StreamingSpeechRuntime(new IStreamingSpeechProvider[]
        {
            new AudioThenFailSpeechProvider(),
            fallback
        });

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await CollectAsync(runtime.StreamAsync(Request(
                "speech-no-mix",
                GenerationModalities.Speech), cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal("speech_stream_interrupted_after_output", exception.ReasonCode);
        Assert.True(exception.OutcomeUncertain);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_speech_requires_monotonic_lifecycle_and_completion()
    {
        var runtime = new StreamingSpeechRuntime(new IStreamingSpeechProvider[]
        {
            new AudioWithoutCompletionSpeechProvider()
        });

        var exception = await Assert.ThrowsAsync<GenerationOperationException>(
            async () => await CollectAsync(runtime.StreamAsync(Request(
                "speech-incomplete",
                GenerationModalities.Speech), cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal("speech_stream_interrupted_after_output", exception.ReasonCode);
        Assert.True(exception.OutcomeUncertain);
    }

    [Fact]
    public async Task Artifact_verification_detects_post_import_tampering()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "game-agent-artifact-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new FileGenerationArtifactStore(
                new FileGenerationArtifactStoreOptions { RootDirectory = root });
            var artifact = await store.ImportAsync(
                "image",
                0,
                new GenerationArtifactSource
                {
                    InlineData = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 },
                    MediaType = "image/png",
                    SizeBytes = 9
                },
                TestContext.Current.CancellationToken);
            await store.VerifyAsync(artifact, cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(new Uri(artifact.Uri).LocalPath,
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 2 }, cancellationToken: TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<GenerationOperationException>(
                async () => await store.VerifyAsync(artifact, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("generation_artifact_integrity_mismatch", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GenerationRequest Request(
        string id,
        string modality = GenerationModalities.Image) => new()
        {
            OperationId = id,
            IdempotencyKey = id,
            Modality = modality,
            Input = Json("{\"prompt\":\"hello\"}")
        };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<List<SpeechStreamEvent>> CollectAsync(
        IAsyncEnumerable<SpeechStreamEvent> source)
    {
        var result = new List<SpeechStreamEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class PassArtifactStore : IGenerationArtifactStore
    {
        public ValueTask<GenerationArtifact> ImportAsync(
            string operationId,
            int ordinal,
            GenerationArtifactSource source,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingProvider : IGenerationProvider
    {
        public string Name => "blocking";

        public GenerationProviderCapabilities Capabilities { get; } = new()
        {
            Modalities = new[] { GenerationModalities.Image }
        };

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SubmitCalls { get; private set; }

        public async ValueTask<GenerationSubmission> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return Success();
        }

        public ValueTask<GenerationProviderResult> GetAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GenerationCancelResult> CancelAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ImmediateProvider : IGenerationProvider
    {
        public string Name => "immediate";

        public GenerationProviderCapabilities Capabilities { get; } = new()
        {
            Modalities = new[] { GenerationModalities.Image }
        };

        public ValueTask<GenerationSubmission> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken) => new(Success());

        public ValueTask<GenerationProviderResult> GetAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GenerationCancelResult> CancelAsync(
            string providerJobId,
            string modality,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static GenerationSubmission Success() => new()
    {
        Acceptance = GenerationAcceptance.Accepted,
        Result = new GenerationProviderResult
        {
            Status = GenerationJobStatuses.Succeeded,
            Output = Json("{\"ok\":true}")
        }
    };

    private sealed class NeverCompletingEventSink : IGenerationEventSink
    {
        public int Calls { get; private set; }

        public async ValueTask PublishAsync(
            GenerationEvent generationEvent,
            CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StartedThenRejectingSpeechProvider : IStreamingSpeechProvider
    {
        public string Name => "reject";

        public async IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
            GenerationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new SpeechStreamEvent
            {
                Kind = SpeechStreamEventKinds.Started,
                MediaType = "audio/pcm",
                Sequence = 0
            };
            throw new GenerationProviderException(
                "not_available",
                "not accepted",
                GenerationAcceptance.NotAccepted);
        }
    }

    private sealed class CompletingSpeechProvider : IStreamingSpeechProvider
    {
        public string Name => "complete";

        public int Calls { get; private set; }

        public async IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
            GenerationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            yield return new SpeechStreamEvent
            {
                Kind = SpeechStreamEventKinds.Audio,
                MediaType = "audio/pcm",
                Audio = new byte[] { 1, 2 },
                Sequence = 0
            };
            yield return new SpeechStreamEvent
            {
                Kind = SpeechStreamEventKinds.Completed,
                MediaType = "audio/pcm",
                Sequence = 1
            };
        }
    }

    private sealed class AudioThenFailSpeechProvider : IStreamingSpeechProvider
    {
        public string Name => "partial";

        public async IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
            GenerationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new SpeechStreamEvent
            {
                Kind = SpeechStreamEventKinds.Audio,
                MediaType = "audio/pcm",
                Audio = new byte[] { 1 },
                Sequence = 0
            };
            throw new IOException("stream interrupted");
        }
    }

    private sealed class AudioWithoutCompletionSpeechProvider : IStreamingSpeechProvider
    {
        public string Name => "incomplete";

        public async IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
            GenerationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new SpeechStreamEvent
            {
                Kind = SpeechStreamEventKinds.Audio,
                MediaType = "audio/pcm",
                Audio = new byte[] { 1 },
                Sequence = 0
            };
        }
    }
}
